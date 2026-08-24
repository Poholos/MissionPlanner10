using System.Net;
using System.Text;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public class Esp8266SettingsTests {
  [Fact]
  public void Complete_parameter_response_is_decoded_without_locale_dependent_values() {
    var parameters = new MAVLink.MAVLinkParamList();
    AddPacked(parameters, "WIFI_SSID", "FieldNetwork");
    AddPacked(parameters, "WIFI_PASSWORD", "secret-password");
    AddNumber(parameters, "UART_BAUDRATE", 115200);
    AddNumber(parameters, "WIFI_CHANNEL", 11);
    AddNumber(parameters, "DEBUG_ENABLED", 0);
    AddNumber(parameters, "WIFI_MODE", 1);
    AddIp(parameters, "WIFI_IPADDRESS", "192.168.4.1");
    AddNumber(parameters, "WIFI_UDP_HPORT", 14550);
    AddNumber(parameters, "WIFI_UDP_CPORT", 14555);
    AddIp(parameters, "WIFI_IPSTA", "10.42.0.20");
    AddIp(parameters, "WIFI_GATEWAYSTA", "10.42.0.1");
    AddIp(parameters, "WIFI_SUBNET_STA", "255.255.255.0");

    bool complete = ConfigHWESP8266ViewModel.TryReadSettings(
        parameters, out Esp8266SettingsSnapshot? snapshot, out var missing);

    Assert.True(complete);
    Assert.Empty(missing);
    Assert.NotNull(snapshot);
    Assert.Equal("FieldNetwork", snapshot!.Ssid);
    Assert.Equal("secret-password", snapshot.Password);
    Assert.Equal("115200", snapshot.Baud);
    Assert.Equal("11", snapshot.Channel);
    Assert.Equal("10.42.0.20", snapshot.IpSta);
    Assert.Equal("10.42.0.1", snapshot.GatewaySta);
    Assert.Equal("255.255.255.0", snapshot.SubnetSta);
    Assert.Contains("WIFI_UDP_CPORT 14555", snapshot.Details, StringComparison.Ordinal);
  }

  [Fact]
  public void Partial_parameter_response_reports_every_missing_value_instead_of_throwing() {
    var parameters = new MAVLink.MAVLinkParamList();
    parameters.Add(new MAVLink.MAVLinkParam(
        "WIFI_SSID1", Encoding.ASCII.GetBytes("test"),
        MAVLink.MAV_PARAM_TYPE.UINT32, MAVLink.MAV_PARAM_TYPE.UINT32));

    bool complete = ConfigHWESP8266ViewModel.TryReadSettings(
        parameters, out Esp8266SettingsSnapshot? snapshot, out var missing);

    Assert.False(complete);
    Assert.Null(snapshot);
    Assert.Contains("WIFI_SSID2", missing);
    Assert.Contains("WIFI_PASSWORD1", missing);
    Assert.Contains("UART_BAUDRATE", missing);
    Assert.Contains("WIFI_SUBNET_STA", missing);
  }

  private static void AddPacked(
      MAVLink.MAVLinkParamList parameters, string prefix, string value) {
    byte[] bytes = Encoding.ASCII.GetBytes(value);
    Array.Resize(ref bytes, 16);
    for (int index = 0; index < 4; index++) {
      parameters.Add(new MAVLink.MAVLinkParam(
          prefix + (index + 1), bytes[(index * 4)..((index + 1) * 4)],
          MAVLink.MAV_PARAM_TYPE.UINT32, MAVLink.MAV_PARAM_TYPE.UINT32));
    }
  }

  private static void AddNumber(
      MAVLink.MAVLinkParamList parameters, string name, uint value) =>
      parameters.Add(new MAVLink.MAVLinkParam(name, value, MAVLink.MAV_PARAM_TYPE.UINT32));

  private static void AddIp(
      MAVLink.MAVLinkParamList parameters, string name, string value) =>
      AddNumber(parameters, name,
          BitConverter.ToUInt32(IPAddress.Parse(value).GetAddressBytes(), 0));
}
