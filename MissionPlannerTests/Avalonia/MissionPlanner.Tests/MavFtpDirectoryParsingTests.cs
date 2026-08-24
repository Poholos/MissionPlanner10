using System.Text;
using MissionPlanner.ArduPilot.Mavlink;

namespace MissionPlanner.Tests;

public sealed class MavFtpDirectoryParsingTests {
  [Fact]
  public void Directory_packet_decodes_utf8_names_and_invariant_file_sizes() {
    byte[] payload = BuildPayload(
        (byte)'F', "полёт-飞行.bin\t18446744073709551615",
        (byte)'D', "данные-資料",
        (byte)'S', "ignored");

    bool parsed = MAVFtp.TryParseDirectoryEntries(
        payload, payload.Length, "/APM", out List<MAVFtp.FtpFileInfo> entries, out string error);

    Assert.True(parsed, error);
    Assert.Collection(entries,
        file => {
          Assert.Equal("полёт-飞行.bin", file.Name);
          Assert.False(file.isDirectory);
          Assert.Equal(ulong.MaxValue, file.Size);
          Assert.Equal("/APM/полёт-飞行.bin", file.FullName);
        },
        directory => {
          Assert.Equal("данные-資料", directory.Name);
          Assert.True(directory.isDirectory);
        },
        skipped => {
          Assert.Equal("", skipped.Name);
          Assert.True(skipped.isDirectory);
        });
  }

  [Theory]
  [MemberData(nameof(MalformedPackets))]
  public void Malformed_directory_packet_is_rejected_without_partial_results(
      byte[] payload, int count, string expectedError) {
    bool parsed = MAVFtp.TryParseDirectoryEntries(
        payload, count, "/", out List<MAVFtp.FtpFileInfo> entries, out string error);

    Assert.False(parsed);
    Assert.Empty(entries);
    Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
  }

  public static IEnumerable<object[]> MalformedPackets() {
    byte[] unterminated = Encoding.UTF8.GetBytes("Fname\t12");
    byte[] missingSize = BuildPayload((byte)'F', "name-without-size");
    byte[] validDirectory = BuildPayload((byte)'D', "ok");
    yield return [unterminated, unterminated.Length, "null terminated"];
    yield return [missingSize, missingSize.Length, "size"];
    yield return [new byte[] { (byte)'D', 0xc3, 0x28, 0 }, 4, "UTF-8"];
    yield return [validDirectory, validDirectory.Length + 1, "exceeds"];
  }

  [Fact]
  public void String_decoder_respects_packet_limit_not_backing_array_length() {
    byte[] data = [.. Encoding.UTF8.GetBytes("name"), 0, (byte)'x', 0];

    Assert.False(MAVFtp.TryExtractNullTerminatedUtf8(
        data, 0, 4, out _, out int nextOffset, out string error));
    Assert.Equal(0, nextOffset);
    Assert.Contains("null terminated", error, StringComparison.OrdinalIgnoreCase);
  }

  private static byte[] BuildPayload(params object[] entries) {
    var bytes = new List<byte>();
    for (int index = 0; index < entries.Length; index += 2) {
      bytes.Add((byte)entries[index]);
      bytes.AddRange(Encoding.UTF8.GetBytes((string)entries[index + 1]));
      bytes.Add(0);
    }
    return bytes.ToArray();
  }
}
