using System.Text;
using System.Text.Json;
using MissionPlanner.Services.Mcp;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class McpLogAnalysisTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void Raw_batch_uses_sample_rate_and_rejects_a_missing_packet(bool missing) {
    string path = WriteBatch(missing);
    try {
      using var log = new DFLogBuffer(path);
      var header = log.GetEnumeratorType("ISBH").Single();
      if (missing) {
        Assert.Throws<ArgumentException>(() => McpBatchImu.Spectrum(log, header.lineno, "x", 256, default));
      } else {
        var data = JsonSerializer.SerializeToElement(McpBatchImu.Spectrum(log, header.lineno, "x", 256, default));
        var spectrum = data.GetProperty("spectrum");
        var power = spectrum.GetProperty("PowerDensity").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        int peak = Array.IndexOf(power, power.Max());
        Assert.Equal(64, spectrum.GetProperty("FrequencyHz")[peak].GetDouble(), 5);
        Assert.InRange(power.Sum() * 4, 1.98, 2.02);
        Assert.Equal("rad/s", data.GetProperty("units").GetString());
      }
    } finally { File.Delete(path); }
  }

  [Fact]
  public void Pid_integral_field_is_not_misidentified_as_a_sensor_instance() {
    string path = Path.Combine(Path.GetTempPath(), $"mp-mcp-pid-{Guid.NewGuid():N}.log");
    try {
      File.WriteAllLines(path, [
        "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
        "FMT, 130, 40, PIDR, Qfff, TimeUS,Tar,Act,I",
        .. Enumerable.Range(0, 64).Select(i => $"PIDR, {1000000 + i * 10000}, 1, 1, {i}"),
      ]);
      using var log = new DFLogBuffer(path);
      var series = McpLogCatalog.Series(log, "PIDR", ["Tar", "Act"], 0, 10, null, default);
      Assert.Equal(64, series[0].Count);
      Assert.All(log.GetEnumeratorType("PIDR"), item => Assert.Equal("", McpLogCatalog.Instance(log, item)));
    } finally { File.Delete(path); }
  }

  private static string WriteBatch(bool missing) {
    string path = Path.Combine(Path.GetTempPath(), $"mp-mcp-batch-{Guid.NewGuid():N}.bin");
    using var writer = new BinaryWriter(File.Create(path));
    void Prefix(byte type) { writer.Write((byte)0xa3); writer.Write((byte)0x95); writer.Write(type); }
    void Fixed(string value, int count) { var bytes = new byte[count]; Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0); writer.Write(bytes); }
    void Fmt(byte type, byte length, string name, string format, string columns) {
      Prefix(128); writer.Write(type); writer.Write(length); Fixed(name, 4); Fixed(format, 16); Fixed(columns, 64);
    }
    Fmt(128, 89, "FMT", "BBnNZ", "Type,Length,Name,Format,Columns");
    Fmt(130, 31, "ISBH", "QHBBHHQf", "TimeUS,N,type,instance,mul,smp_cnt,SampleUS,smp_rate");
    Fmt(131, 207, "ISBD", "QHHaaa", "TimeUS,N,seqno,x,y,z");
    Prefix(130); writer.Write((ulong)1000000); writer.Write((ushort)7); writer.Write((byte)1); writer.Write((byte)0);
    writer.Write((ushort)1024); writer.Write((ushort)256); writer.Write((ulong)1000000); writer.Write(1024f);
    for (int block = 0; block < 8; block++) {
      if (missing && block == 2) { continue; }
      Prefix(131); writer.Write((ulong)(1000000 + block * 31250)); writer.Write((ushort)7); writer.Write((ushort)block);
      for (int axis = 0; axis < 3; axis++) {
        for (int j = 0; j < 32; j++) { writer.Write((short)Math.Round(2048 * Math.Sin(2 * Math.PI * 64 * (block * 32 + j) / 1024))); }
      }
    }
    return path;
  }
}
