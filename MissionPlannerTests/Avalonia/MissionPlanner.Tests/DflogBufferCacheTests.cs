using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

/// <summary>
/// The DFLogBuffer index cache for large logs. Upstream serialized it with
/// BinaryFormatter, which throws unconditionally on modern .NET - saving
/// crashed the open of any path-backed log over the threshold, and loading
/// silently never worked. These tests pin the replacement format: round-trip
/// fidelity, stale- and corrupt-cache rejection, and that an over-threshold
/// open never throws (the first open here fails outright on the old code).
/// </summary>
public class DflogBufferCacheTests {
  private static byte[] BuildLog(int rows, byte flip = 0) {
    // FMT: type 0xA0 "TST", format "Hf", labels "N,V", len 3+2+4
    var data = new List<byte> { 0xA3, 0x95, 0x80 };
    var fmt = new byte[86];
    fmt[0] = 0xA0;
    fmt[1] = 3 + 2 + 4;
    System.Text.Encoding.ASCII.GetBytes("TST").CopyTo(fmt, 2);
    System.Text.Encoding.ASCII.GetBytes("Hf").CopyTo(fmt, 6);
    System.Text.Encoding.ASCII.GetBytes("N,V").CopyTo(fmt, 22);
    data.AddRange(fmt);
    for (int i = 0; i < rows; i++) {
      data.AddRange(new byte[] { 0xA3, 0x95, 0xA0 });
      data.AddRange(BitConverter.GetBytes((ushort)(i ^ flip)));
      data.AddRange(BitConverter.GetBytes(1.5f * i));
    }
    return data.ToArray();
  }

  private static List<string> Lines(DFLogBuffer buffer) {
    var lines = new List<string>();
    for (int i = 0; i < buffer.Count; i++) {
      lines.Add(buffer[i]);
    }
    return lines;
  }

  /// <summary>
  /// Lowers the cache threshold so kilobyte fixtures exercise the cache, and
  /// sweeps the cache files (which live in the system temp directory, keyed
  /// by the log's mangled path) on the way out.
  /// </summary>
  private sealed class CacheScope : IDisposable {
    private readonly long _oldThreshold;

    public DirectoryInfo Dir { get; }
    public string LogPath { get; }

    public CacheScope() {
      _oldThreshold = DFLogBuffer.CacheThresholdBytes;
      DFLogBuffer.CacheThresholdBytes = 1;
      Dir = Directory.CreateTempSubdirectory("DflogBufferCacheTests");
      LogPath = Path.Combine(Dir.FullName, "test.bin");
    }

    public string[] CacheFiles() {
      return Directory.GetFiles(Path.GetTempPath(), "*" + Dir.Name + "*");
    }

    public void Dispose() {
      DFLogBuffer.CacheThresholdBytes = _oldThreshold;
      try {
        Dir.Delete(true);
      } catch (IOException) {
      }
      foreach (string stale in CacheFiles()) {
        try {
          File.Delete(stale);
        } catch (IOException) {
        }
      }
    }
  }

  [Fact]
  public void Cache_round_trip_restores_the_identical_index() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    List<string> scanned;
    long count;
    // the first open scans and saves the cache; on the BinaryFormatter code
    // this line throws for any over-threshold log
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.False(DFLogBuffer.LastLoadFromCache);
      scanned = Lines(buffer);
      count = buffer.Count;
      Assert.Equal(51, count);
    }

    Assert.NotEmpty(scope.CacheFiles());

    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.True(DFLogBuffer.LastLoadFromCache,
          "second open scanned instead of loading the cache");
      Assert.Equal(count, buffer.Count);
      Assert.Equal(scanned, Lines(buffer));
      Assert.Equal(50, buffer.GetEnumeratorType("TST").Count());
    }
  }

  [Fact]
  public void Same_length_edit_rejects_the_stale_cache() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));
    using (new DFLogBuffer(scope.LogPath)) {
    }
    Assert.NotEmpty(scope.CacheFiles());

    // same byte count, different content - the cache path encodes only the
    // length, so this must be caught by the recorded write time
    File.WriteAllBytes(scope.LogPath, BuildLog(50, flip: 1));
    File.SetLastWriteTimeUtc(scope.LogPath, DateTime.UtcNow.AddSeconds(3));

    using var buffer = new DFLogBuffer(scope.LogPath);
    Assert.False(DFLogBuffer.LastLoadFromCache,
        "a cache for an older copy of the log was loaded");
    Assert.Equal(51, buffer.Count);
  }

  /// <summary>
  /// The cache lives in the shared temp directory, so a crafted file with a
  /// valid header but an absurd list length must be rejected by the
  /// plausibility bound instead of turning into a giant pre-allocation.
  /// </summary>
  [Fact]
  public void Implausible_list_length_in_the_cache_is_rejected() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    List<string> scanned;
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scanned = Lines(buffer);
    }

    string cache = Assert.Single(scope.CacheFiles());
    var source = new FileInfo(scope.LogPath);
    using (var file = File.Create(cache))
    using (var gzip = new System.IO.Compression.GZipStream(
        file, System.IO.Compression.CompressionMode.Compress))
    using (var writer = new BinaryWriter(gzip)) {
      writer.Write(0x4C444D31u);                       // valid magic
      writer.Write(1);                                 // valid version
      writer.Write(source.Length);                     // matching identity
      writer.Write(source.LastWriteTimeUtc.Ticks);
      writer.Write(51L);                               // line count
      writer.Write(int.MaxValue);                      // absurd list length
    }

    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.False(DFLogBuffer.LastLoadFromCache);
      Assert.Equal(scanned, Lines(buffer));
    }
  }

  [Theory]
  [InlineData("garbage")]
  [InlineData("truncated")]
  public void Corrupt_cache_falls_back_to_a_clean_rescan(string mode) {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    List<string> scanned;
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scanned = Lines(buffer);
    }

    string cache = Assert.Single(scope.CacheFiles());
    if (mode == "garbage") {
      File.WriteAllBytes(cache, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    } else {
      // a torn write: valid prefix, missing tail - the loader must reject it
      // without leaving a half-committed index behind for the rescan
      byte[] full = File.ReadAllBytes(cache);
      File.WriteAllBytes(cache, full.Take(full.Length / 2).ToArray());
    }

    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.False(DFLogBuffer.LastLoadFromCache);
      Assert.Equal(scanned, Lines(buffer));
      Assert.Equal(50, buffer.GetEnumeratorType("TST").Count());
    }
  }
}
