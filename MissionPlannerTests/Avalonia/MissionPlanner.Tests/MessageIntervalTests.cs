using MissionPlanner.ViewModels;
using Xunit;

namespace MissionPlanner.Tests;

public class MessageIntervalTests {
  [Theory]
  [InlineData(1, 1_000_000)]
  [InlineData(2, 500_000)]
  [InlineData(10, 100_000)]
  [InlineData(50, 20_000)]
  public void ConvertsRateToMicroseconds(double rateHz, float expected) {
    Assert.Equal(expected, FlightDataViewModel.MessageIntervalMicroseconds(rateHz));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  public void RejectsInvalidRates(double rateHz) {
    Assert.Throws<ArgumentOutOfRangeException>(
        () => FlightDataViewModel.MessageIntervalMicroseconds(rateHz));
  }

  [Fact]
  public void ClampsExcessiveRatesToProtocolSafeMaximum() {
    Assert.Equal(1_000, FlightDataViewModel.MessageIntervalMicroseconds(10_000));
  }

  [Fact]
  public void Message_picker_excludes_the_legacy_offspec_video_duplicate() {
    IReadOnlyList<MavlinkMessageOption> options =
        FlightDataViewModel.BuildMessageIntervalOptions();

    Assert.Contains(options, option =>
        option.Name == nameof(MAVLink.MAVLINK_MSG_ID.VIDEO_STREAM_INFORMATION));
    Assert.DoesNotContain(options, option => option.Name == "zVIDEO_STREAM_INFORMATION");
    Assert.Equal(options.Count, options.Select(option => option.Id).Distinct().Count());
  }
}
