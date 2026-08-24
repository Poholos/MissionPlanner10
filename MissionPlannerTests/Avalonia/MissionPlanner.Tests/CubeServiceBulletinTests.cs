using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class CubeServiceBulletinTests {
  [Theory]
  [InlineData("CubeBlack 0001", true)]
  [InlineData("cubeblack 0001", true)]
  [InlineData("CubeBlack+ 0001", false)]
  [InlineData("CubeOrange 0001", false)]
  public void Identifies_only_legacy_CubeBlack_serials(string serial, bool expected) =>
      Assert.Equal(expected, CubeServiceBulletin.IsAffectedCubeBlack(serial));

  [Fact]
  public void Parameter_scan_requires_both_missing_third_imus_and_enabled_mask() {
    var parameters = new Dictionary<string, double> {
      ["INS_ACC3_ID"] = 0,
      ["INS_GYR3_ID"] = 0,
      ["INS_ENABLE_MASK"] = 7,
    };

    Assert.True(CubeServiceBulletin.RequiresParameterScan(
        "CubeBlack 123", name => parameters.GetValueOrDefault(name)));
    parameters["INS_GYR3_ID"] = 123;
    Assert.False(CubeServiceBulletin.RequiresParameterScan(
        "CubeBlack 123", name => parameters.GetValueOrDefault(name)));
    Assert.False(CubeServiceBulletin.RequiresParameterScan(
        "CubeBlack+ 123", name => parameters.GetValueOrDefault(name)));
  }

  [Theory]
  [InlineData(new byte[] { 0xd4 }, new byte[] { 0x49 }, false)]
  [InlineData(new byte[] { 0xd7 }, new byte[] { 0x49 }, false)]
  [InlineData(new byte[] { 0x00 }, new byte[] { 0x49 }, true)]
  [InlineData(new byte[] { 0xd4 }, new byte[] { 0x00 }, true)]
  [InlineData(new byte[] { }, new byte[] { }, false)]
  public void Spi_scan_preserves_official_identity_checks(
      byte[] gyro, byte[] accelerometer, bool expected) =>
      Assert.Equal(expected,
          CubeServiceBulletin.HasUnexpectedSpiIdentity(gyro, accelerometer));

  [Fact]
  public void Report_url_encodes_user_and_vehicle_values() {
    var report = new CubeServiceBulletinSnapshot(
        "3", "Cube Black/01", "1", "2", "0", "4", "5", "0", "1001.5", "998.25");

    string url = CubeServiceBulletin.BuildReportUrl(
        report, "Alex & Team", "pilot+cube@example.test");

    Assert.StartsWith("https://discuss.cubepilot.org:444/CubeSB?", url,
        StringComparison.Ordinal);
    Assert.Contains("SerialNo=Cube%20Black%2F01", url, StringComparison.Ordinal);
    Assert.Contains("Name=Alex%20%26%20Team", url, StringComparison.Ordinal);
    Assert.Contains("Email=pilot%2Bcube%40example.test", url, StringComparison.Ordinal);
  }
}
