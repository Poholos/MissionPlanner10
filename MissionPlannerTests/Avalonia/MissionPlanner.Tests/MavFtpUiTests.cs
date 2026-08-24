using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public sealed class MavFtpUiTests {
  [Fact]
  public void System_directory_is_not_added_twice_when_root_already_advertises_it() {
    var root = new FtpDirNode("/", "/", null);
    root.Children.Clear();
    root.Children.Add(new FtpDirNode("@SYS", "/@SYS", null));

    Assert.False(MavFTPUIViewModel.ShouldAddSystemRoot(root.Children));
  }

  [Fact]
  public void Legacy_firmware_without_system_directory_keeps_the_virtual_root_fallback() {
    var root = new FtpDirNode("/", "/", null);
    root.Children.Clear();
    root.Children.Add(new FtpDirNode("APM", "/APM", null));

    Assert.True(MavFTPUIViewModel.ShouldAddSystemRoot(root.Children));
  }
}
