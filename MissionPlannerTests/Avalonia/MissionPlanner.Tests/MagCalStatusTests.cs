using System.Reflection;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public sealed class MagCalStatusTests {
  [Theory]
  [InlineData(MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED_ORIENTATION, 6, "orientation")]
  [InlineData(MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED_RADIUS, 7, "radius")]
  [InlineData(MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED_OFFSETS, 8, "offset")]
  [InlineData(MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED_DIAG_SCALING, 9, "scaling")]
  [InlineData(MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED_RESIDUALS_HIGH, 10, "fitness")]
  public void Failure_wire_values_and_descriptions_match_mavlink(
      MAVLink.MAG_CAL_STATUS status, byte wireValue, string diagnostic) {
    Assert.Equal(wireValue, (byte)status);
    MAVLink.Description? description = typeof(MAVLink.MAG_CAL_STATUS)
        .GetField(status.ToString())?.GetCustomAttribute<MAVLink.Description>();
    Assert.NotNull(description);
    Assert.Contains(diagnostic, description.Text, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(diagnostic, MagCalStatusFormatter.Describe(wireValue),
        StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData(0, false)]
  [InlineData(1, false)]
  [InlineData(2, false)]
  [InlineData(3, false)]
  [InlineData(4, false)]
  [InlineData(5, true)]
  [InlineData(6, true)]
  [InlineData(7, true)]
  [InlineData(8, true)]
  [InlineData(9, true)]
  [InlineData(10, true)]
  public void Failure_partition_and_report_progress_are_stable(byte status, bool failed) {
    Assert.Equal(failed, MagCalStatusFormatter.IsFailure(status));
    Assert.Equal(failed ? 0 : 100, MagCalStatusFormatter.ProgressForReport(status));
  }

  [Fact]
  public void Unknown_future_status_has_an_unambiguous_fallback() {
    Assert.Equal("MAG_CAL_STATUS(42)", MagCalStatusFormatter.Describe(42));
  }
}
