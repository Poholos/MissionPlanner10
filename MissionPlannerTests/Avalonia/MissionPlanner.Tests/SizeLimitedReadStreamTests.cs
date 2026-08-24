using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class SizeLimitedReadStreamTests {
  [Fact]
  public void Exact_limit_can_be_read_to_end() {
    using var source = new MemoryStream([1, 2, 3, 4]);
    using var limited = new SizeLimitedReadStream(source, 4, leaveOpen: true);
    var buffer = new byte[16];

    Assert.Equal(4, limited.Read(buffer));
    Assert.Equal(0, limited.Read(buffer));
    Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer[..4]);
  }

  [Fact]
  public void First_byte_past_limit_is_rejected_instead_of_returned() {
    using var source = new MemoryStream([1, 2, 3, 4, 5]);
    using var limited = new SizeLimitedReadStream(source, 4, leaveOpen: true);
    var buffer = new byte[16];

    Assert.Throws<InvalidDataException>(() => limited.Read(buffer));
    Assert.Equal(5, source.Position);
  }

  [Fact]
  public void Leave_open_preserves_the_wrapped_stream() {
    var source = new MemoryStream([1]);
    var limited = new SizeLimitedReadStream(source, 1, leaveOpen: true);

    limited.Dispose();

    Assert.True(source.CanRead);
    source.Dispose();
  }
}
