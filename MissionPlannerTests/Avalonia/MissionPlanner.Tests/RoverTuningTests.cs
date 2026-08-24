using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public sealed class RoverTuningTests {
  [Fact]
  public void Basic_tuning_uses_current_rover_navigation_controllers() {
    var viewModel = new ConfigArduroverViewModel();
    string[] names = viewModel.Groups.SelectMany(group => group.Rows)
        .Select(row => row.Name)
        .ToArray();

    Assert.DoesNotContain("WP_OVERSHOOT", names);
    Assert.DoesNotContain("NAVL1_PERIOD", names);
    Assert.DoesNotContain("NAVL1_DAMPING", names);
    Assert.Contains("ATC_DECEL_MAX", names);
    Assert.Contains("ATC_STR_ANG_P", names);
    Assert.Contains("PSC_POS_P", names);
    Assert.Contains("PSC_VEL_P", names);
  }
}
