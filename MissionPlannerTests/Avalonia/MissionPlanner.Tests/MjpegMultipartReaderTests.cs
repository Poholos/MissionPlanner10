using System.Text;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class MjpegMultipartReaderTests {
  [Fact]
  public void Reads_length_delimited_frames_with_case_insensitive_headers() {
    byte[] body = Encoding.ASCII.GetBytes(
        "--frame\r\ncontent-type: image/jpeg\r\ncontent-length: 4\r\n\r\nABCD\r\n" +
        "--frame\r\nContent-Length: 3\r\n\r\nXYZ\r\n--frame--\r\n");

    using var binary = Reader(body);
    var reader = new MjpegMultipartReader(binary,
        "multipart/x-mixed-replace; charset=utf-8; BOUNDARY=\"frame\"");

    Assert.True(reader.TryReadFrame(out byte[] first));
    Assert.Equal("ABCD", Encoding.ASCII.GetString(first));
    Assert.True(reader.TryReadFrame(out byte[] second));
    Assert.Equal("XYZ", Encoding.ASCII.GetString(second));
    Assert.False(reader.TryReadFrame(out _));
  }

  [Fact]
  public void Sniffs_boundary_and_does_not_skip_lengthless_frames() {
    byte[] body = Encoding.ASCII.GetBytes(
        "camera preamble\n--cam\nContent-Type: image/jpeg\n\nONE\n" +
        "--cam\nContent-Type: image/jpeg\n\nTWO\n--cam--\n");

    using var binary = Reader(body);
    var reader = new MjpegMultipartReader(binary, null);

    Assert.True(reader.TryReadFrame(out byte[] first));
    Assert.Equal("ONE", Encoding.ASCII.GetString(first));
    Assert.True(reader.TryReadFrame(out byte[] second));
    Assert.Equal("TWO", Encoding.ASCII.GetString(second));
    Assert.False(reader.TryReadFrame(out _));
  }

  [Fact]
  public void Rejects_invalid_or_oversized_content_length() {
    byte[] body = Encoding.ASCII.GetBytes(
        "--safe\r\nContent-Length: 12\r\n\r\nsmall");
    using var binary = Reader(body);
    var reader = new MjpegMultipartReader(binary,
        "multipart/x-mixed-replace; boundary=safe", maxFrameBytes: 8);

    InvalidDataException error = Assert.Throws<InvalidDataException>(
        () => reader.TryReadFrame(out _));
    Assert.Contains("size limit", error.Message);
  }

  [Fact]
  public void Rejects_truncated_length_delimited_frames() {
    byte[] body = Encoding.ASCII.GetBytes(
        "--frame\r\nContent-Length: 8\r\n\r\nshort");
    using var binary = Reader(body);
    var reader = new MjpegMultipartReader(binary,
        "multipart/x-mixed-replace; boundary=frame");

    Assert.Throws<EndOfStreamException>(() => reader.TryReadFrame(out _));
  }

  private static BinaryReader Reader(byte[] bytes) =>
      new(new MemoryStream(bytes, writable: false), Encoding.ASCII, leaveOpen: false);
}
