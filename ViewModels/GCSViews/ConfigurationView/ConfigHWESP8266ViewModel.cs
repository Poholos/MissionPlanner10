using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Services;

namespace MissionPlanner.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigHWESP8266ViewModel : ViewModelBase,
    IActivationAware, IDeactivationAware, IDisposable {
  private const byte _udpBridge = (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_UDP_BRIDGE;
  private static readonly TimeSpan _parameterTimeout = TimeSpan.FromSeconds(3);
  private static readonly TimeSpan _parameterPollInterval = TimeSpan.FromMilliseconds(100);

  private readonly MAVLinkInterface _comPort;
  private readonly LatestOperationController _activation = new();

  [ObservableProperty]
  private string _ssid = "";

  [ObservableProperty]
  private string _password = "";

  [ObservableProperty]
  private string _baud = "115200";

  [ObservableProperty]
  private string _channel = "11";

  [ObservableProperty]
  private bool _staMode;

  [ObservableProperty]
  private string _ipSta = "192.168.4.1";

  [ObservableProperty]
  private string _gatewaySta = "192.168.4.1";

  [ObservableProperty]
  private string _subnetSta = "255.255.255.0";

  [ObservableProperty]
  private string _details = "";

  [ObservableProperty]
  private string _status = "";

  [ObservableProperty]
  private bool _isLoaded;

  public string[] ChannelOptions { get; } = {
    "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13",
  };

  public string[] BaudOptions { get; } = {
    "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600",
  };

  public bool IsConnected => _comPort.BaseStream?.IsOpen == true;

  public ConfigHWESP8266ViewModel() : this(AppState.comPort) {
  }

  internal ConfigHWESP8266ViewModel(MAVLinkInterface comPort) {
    _comPort = comPort ?? throw new ArgumentNullException(nameof(comPort));
    if (!IsConnected) {
      Status = "Not connected.";
    }
  }

  public void Activate() {
    if (!IsConnected) {
      Status = "Not connected.";
      return;
    }

    LatestOperationController.Lease operation = _activation.Begin(default);
    _ = LoadSettingsAsync(operation);
  }

  public void Deactivate() => _activation.CancelCurrent();

  public void Dispose() => _activation.Dispose();

  private async Task LoadSettingsAsync(LatestOperationController.Lease operation) {
    CancellationToken cancellationToken = operation.Token;
    byte sysid = _comPort.MAV.sysid;
    try {
      await ApplyIfCurrentAsync(operation, () => {
        IsLoaded = false;
        Status = "Requesting ESP8266 parameters…";
      });
      await Task.Run(() => _comPort.sendPacket(new MAVLink.mavlink_param_request_list_t {
        target_system = sysid,
        target_component = _udpBridge,
      }, sysid, _udpBridge), cancellationToken).ConfigureAwait(false);

      MAVState mav = _comPort.MAVlist[sysid, _udpBridge];
      DateTime deadline = DateTime.UtcNow + _parameterTimeout;
      Esp8266SettingsSnapshot? snapshot = null;
      IReadOnlyList<string> missing = [];
      while (!TryReadSettings(mav.param, out snapshot, out missing)
          && DateTime.UtcNow < deadline) {
        await Task.Delay(_parameterPollInterval, cancellationToken).ConfigureAwait(false);
      }
      cancellationToken.ThrowIfCancellationRequested();
      bool targetChanged = _comPort.BaseStream?.IsOpen != true
          || _comPort.sysidcurrent != sysid;
      if (targetChanged || snapshot is null) {
        string status = targetChanged
            ? "The selected device changed before ESP8266 parameters were loaded."
            : missing.Count == 0
                ? "No ESP8266 / UDP-bridge component responded."
                : "Incomplete ESP8266 response; missing: " + string.Join(", ", missing) + ".";
        await ApplyIfCurrentAsync(operation, () => {
          IsLoaded = false;
          Status = status;
        });
        return;
      }

      await ApplyIfCurrentAsync(operation, () => Apply(snapshot));
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      // Deactivation or a newer refresh owns the visible state now.
    } catch (Exception ex) {
      await ApplyIfCurrentAsync(operation, () => {
        IsLoaded = false;
        Status = "ESP8266 parameter load failed: " + ex.Message;
      });
    } finally {
      operation.Dispose();
    }
  }

  private Task ApplyIfCurrentAsync(
      LatestOperationController.Lease operation, Action update) =>
      Dispatcher.UIThread.InvokeAsync(() => {
        if (operation.IsCurrent) {
          update();
        }
      }).GetTask();

  private void Apply(Esp8266SettingsSnapshot snapshot) {
    Ssid = snapshot.Ssid;
    Password = snapshot.Password;
    Baud = snapshot.Baud;
    Channel = snapshot.Channel;
    IpSta = snapshot.IpSta;
    GatewaySta = snapshot.GatewaySta;
    SubnetSta = snapshot.SubnetSta;
    StaMode = snapshot.WifiMode != "0";
    Details = snapshot.Details;
    IsLoaded = true;
    Status = "";
  }

  internal static bool TryReadSettings(
      MAVLink.MAVLinkParamList parameters,
      out Esp8266SettingsSnapshot? snapshot,
      out IReadOnlyList<string> missing) {
    ArgumentNullException.ThrowIfNull(parameters);
    var absent = new List<string>();
    string ssid = ReadPackedString(parameters, "WIFI_SSID", absent);
    string password = ReadPackedString(parameters, "WIFI_PASSWORD", absent);
    string baud = ReadNumber(parameters, "UART_BAUDRATE", absent);
    string channel = ReadNumber(parameters, "WIFI_CHANNEL", absent);
    string debugEnabled = ReadNumber(parameters, "DEBUG_ENABLED", absent);
    string wifiMode = ReadNumber(parameters, "WIFI_MODE", absent);
    string wifiIpAddress = ReadIp(parameters, "WIFI_IPADDRESS", absent);
    string wifiUdpHport = ReadNumber(parameters, "WIFI_UDP_HPORT", absent);
    string wifiUdpCport = ReadNumber(parameters, "WIFI_UDP_CPORT", absent);
    string ipSta = ReadIp(parameters, "WIFI_IPSTA", absent);
    string gatewaySta = ReadIp(parameters, "WIFI_GATEWAYSTA", absent);
    string subnetSta = ReadIp(parameters, "WIFI_SUBNET_STA", absent);
    missing = absent;
    if (absent.Count != 0) {
      snapshot = null;
      return false;
    }

    string details = string.Format(CultureInfo.InvariantCulture,
        "DEBUG_ENABLED {0},\n" +
        "WIFI_MODE {1},\n" +
        "WIFI_IPADDRESS {2},\n" +
        "WIFI_UDP_HPORT {3},\n" +
        "WIFI_UDP_CPORT {4},\n" +
        "WIFI_IPSTA {5},\n" +
        "WIFI_GATEWAYSTA {6},\n" +
        "WIFI_SUBNET_STA {7}\n",
        debugEnabled, wifiMode, wifiIpAddress, wifiUdpHport, wifiUdpCport,
        ipSta, gatewaySta, subnetSta);
    snapshot = new Esp8266SettingsSnapshot(
        ssid, password, baud, channel, wifiMode, ipSta, gatewaySta, subnetSta, details);
    return true;
  }

  private static string ReadPackedString(
      MAVLink.MAVLinkParamList parameters, string prefix, ICollection<string> missing) {
    var value = new StringBuilder(16);
    for (int index = 1; index <= 4; index++) {
      string name = prefix + index.ToString(CultureInfo.InvariantCulture);
      MAVLink.MAVLinkParam? parameter = parameters[name];
      if (parameter is null) {
        missing.Add(name);
      } else {
        value.Append(Encoding.ASCII.GetString(parameter.data));
      }
    }
    return value.ToString().TrimEnd('\0');
  }

  private static string ReadNumber(
      MAVLink.MAVLinkParamList parameters, string name, ICollection<string> missing) {
    MAVLink.MAVLinkParam? parameter = parameters[name];
    if (parameter is null) {
      missing.Add(name);
      return "";
    }
    return parameter.Value.ToString("0.######", CultureInfo.InvariantCulture);
  }

  private static string ReadIp(
      MAVLink.MAVLinkParamList parameters, string name, ICollection<string> missing) {
    MAVLink.MAVLinkParam? parameter = parameters[name];
    if (parameter is null) {
      missing.Add(name);
      return "";
    }
    return new IPAddress(BitConverter.GetBytes(unchecked((int)parameter.Value))).ToString();
  }

  private static byte[] StringToByteArray(string input, int start, int length) {
    var ans = Encoding.ASCII.GetBytes(input ?? "");
    Array.Resize(ref ans, start + length);
    byte[] dst = new byte[length];
    Array.ConstrainedCopy(ans, start, dst, 0, length);
    return dst;
  }

  private bool SetU32(byte sysid, string name, string source, int start) {
    return _comPort.setParam(sysid, _udpBridge, name,
        BitConverter.ToUInt32(StringToByteArray(source, start, 4), 0));
  }

  [RelayCommand]
  private async Task Save() {
    if (!IsConnected) {
      Status = "Not connected.";
      return;
    }

    Status = "Saving…";
    byte sysid = _comPort.MAV.sysid;
    string ssid = Ssid;
    string password = Password;
    if (!int.TryParse(Channel, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int channel)
        || !int.TryParse(Baud, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int baud)
        || !IPAddress.TryParse(IpSta, out IPAddress? ipSta)
        || !IPAddress.TryParse(GatewaySta, out IPAddress? gatewaySta)
        || !IPAddress.TryParse(SubnetSta, out IPAddress? subnetSta)
        || ipSta.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
        || gatewaySta.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
        || subnetSta.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) {
      Status = "Invalid channel, baud rate, or IPv4 station settings.";
      return;
    }
    bool staMode = StaMode;
    bool pass = await Task.Run(() => {
      try {
        bool ok = _comPort.setParam(sysid, _udpBridge, "WIFI_CHANNEL", channel);
        ok &= _comPort.setParam(sysid, _udpBridge, "UART_BAUDRATE", baud);

        ok &= SetU32(sysid, "WIFI_SSID1", ssid, 0);
        ok &= SetU32(sysid, "WIFI_SSID2", ssid, 4);
        ok &= SetU32(sysid, "WIFI_SSID3", ssid, 8);
        ok &= SetU32(sysid, "WIFI_SSID4", ssid, 12);

        ok &= SetU32(sysid, "WIFI_PASSWORD1", password, 0);
        ok &= SetU32(sysid, "WIFI_PASSWORD2", password, 4);
        ok &= SetU32(sysid, "WIFI_PASSWORD3", password, 8);
        ok &= SetU32(sysid, "WIFI_PASSWORD4", password, 12);

        ok &= SetU32(sysid, "WIFI_SSIDSTA1", ssid, 0);
        ok &= SetU32(sysid, "WIFI_SSIDSTA2", ssid, 4);
        ok &= SetU32(sysid, "WIFI_SSIDSTA3", ssid, 8);
        ok &= SetU32(sysid, "WIFI_SSIDSTA4", ssid, 12);

        ok &= SetU32(sysid, "WIFI_PWDSTA1", password, 0);
        ok &= SetU32(sysid, "WIFI_PWDSTA2", password, 4);
        ok &= SetU32(sysid, "WIFI_PWDSTA3", password, 8);
        ok &= SetU32(sysid, "WIFI_PWDSTA4", password, 12);

        ok &= _comPort.setParam(sysid, _udpBridge, "WIFI_IPSTA",
            BitConverter.ToUInt32(ipSta.GetAddressBytes(), 0));
        ok &= _comPort.setParam(sysid, _udpBridge, "WIFI_GATEWAYSTA",
            BitConverter.ToUInt32(gatewaySta.GetAddressBytes(), 0));
        ok &= _comPort.setParam(sysid, _udpBridge, "WIFI_SUBNET_STA",
            BitConverter.ToUInt32(subnetSta.GetAddressBytes(), 0));

        ok &= _comPort.setParam(sysid, _udpBridge, "WIFI_MODE", staMode ? 1 : 0);
        if (!ok) {
          return false;
        }

        ok = _comPort.doCommand(sysid, _udpBridge,
          MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 1, 0, 0, 0, 0, 0, 0);
        return ok && _comPort.doCommand(sysid, _udpBridge,
          MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 0, 1, 0, 0, 0, 0, 0);
      } catch {
        return false;
      }
    });

    Status = pass ? "Programmed OK." : "Error setting parameter.";
  }

  [RelayCommand]
  private async Task ResetDefaults() {
    if (!IsConnected) {
      Status = "Not connected.";
      return;
    }

    Status = "Resetting to defaults…";
    byte sysid = _comPort.MAV.sysid;
    bool pass = await Task.Run(() => {
      try {
        if (!_comPort.doCommand(sysid, _udpBridge,
          MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 2, 0, 0, 0, 0, 0, 0)) {
          return false;
        }
        return _comPort.doCommand(sysid, _udpBridge,
          MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 1, 0, 0, 0, 0, 0, 0);
      } catch {
        return false;
      }
    });

    if (pass) {
      Status = "Programmed OK. Refreshing parameters…";
      Activate();
    } else {
      Status = "Error setting parameter.";
    }
  }
}

internal sealed record Esp8266SettingsSnapshot(
    string Ssid,
    string Password,
    string Baud,
    string Channel,
    string WifiMode,
    string IpSta,
    string GatewaySta,
    string SubnetSta,
    string Details);
