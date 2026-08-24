using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public sealed class ConfigHwidTests {
  [Theory]
  [InlineData(6u, "USB0")]
  [InlineData(14u, "SERIAL1")]
  [InlineData(198u, "NET_P4")]
  [InlineData(334u, "CAN_D1_UC_S1")]
  [InlineData(502u, "SCR_SDEV2")]
  [InlineData(999u, "Unknown (999)")]
  public void Mav_port_device_ids_use_the_firmware_port_mapping(uint value, string expected) {
    Assert.Equal(expected, ConfigHWIDViewModel.DecodeMavPortDeviceId(value));
  }

  [Theory]
  [InlineData("MAV1_DEVID", true)]
  [InlineData("mav12_devid", true)]
  [InlineData("MAV_DEVID", false)]
  [InlineData("MAVX_DEVID", false)]
  [InlineData("COMPASS_DEV_ID", false)]
  public void Only_numbered_mav_devid_parameters_use_the_port_mapping(
      string name, bool expected) {
    Assert.Equal(expected, ConfigHWIDViewModel.IsMavPortDeviceId(name));
  }
}
