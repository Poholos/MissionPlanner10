using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Utilities;

namespace MissionPlanner.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigHWIDViewModel : ViewModelBase {
  private static readonly IReadOnlyDictionary<uint, string> MavPortDeviceNames =
      new Dictionary<uint, string> {
        [0] = "Unknown",
        [6] = "USB0",
        [14] = "SERIAL1",
        [22] = "SERIAL2",
        [30] = "SERIAL3",
        [38] = "SERIAL4",
        [46] = "SERIAL5",
        [54] = "SERIAL6",
        [62] = "SERIAL7",
        [70] = "SERIAL8",
        [78] = "SERIAL9",
        [174] = "NET_P1",
        [182] = "NET_P2",
        [190] = "NET_P3",
        [198] = "NET_P4",
        [334] = "CAN_D1_UC_S1",
        [414] = "CAN_D2_UC_S1",
        [494] = "SCR_SDEV1",
        [502] = "SCR_SDEV2",
      };

  private readonly MAVLinkInterface _comPort = AppState.comPort;

  public ObservableCollection<HwIdRow> Devices { get; } = new();

  public bool IsConnected => _comPort.BaseStream?.IsOpen == true;

  public ConfigHWIDViewModel() {
    Load();
  }

  [RelayCommand]
  private void Load() {
    Devices.Clear();

    var param = _comPort.MAV?.param;
    if (param == null) {
      return;
    }

    var rows = param
        .Where(a => (a.Name.Contains("_ID") || a.Name.Contains("_DEVID"))
            && !a.Name.Contains("_IDX") && !a.Name.Contains("FRSKY"))
        .OrderBy(a => a.Name)
        .Select(a => Decode(a.Name, (uint)a.Value))
        .ToList();

    foreach (var row in rows) {
      Devices.Add(row);
    }
  }

  private static HwIdRow Decode(string paramName, uint id) {
    if (IsMavPortDeviceId(paramName)) {
      return new HwIdRow {
        ParamName = paramName,
        DevID = unchecked((int)id),
        BusType = "MAVLink",
        DevType = DecodeMavPortDeviceId(id),
      };
    }

    var devid = new Device.DeviceStructure(paramName, id);

    string busType = devid.bus_type.ToString().Replace("BUS_TYPE_", "");
    string devType;
    if (devid.bus_type == Device.BusType.BUS_TYPE_UAVCAN) {
      devType = "SENSOR_ID#" + devid.devtype;
    } else if (paramName.Contains("COMP")) {
      devType = devid.devtypecompass.ToString().Replace("DEVTYPE_", "");
    } else if (paramName.Contains("BARO")) {
      devType = devid.devtypebaro.ToString().Replace("DEVTYPE_", "");
    } else if (paramName.Contains("ASP")) {
      devType = devid.devtypeairspd.ToString().Replace("DEVTYPE_", "");
    } else {
      devType = devid.devtypeimu.ToString().Replace("DEVTYPE_", "");
    }

    return new HwIdRow {
      ParamName = paramName,
      DevID = (int)devid.devid,
      BusType = busType,
      Bus = devid.bus,
      Address = devid.address,
      DevType = devType,
    };
  }

  internal static bool IsMavPortDeviceId(string paramName) {
    const string prefix = "MAV";
    const string suffix = "_DEVID";
    if (!paramName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        || !paramName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        || paramName.Length <= prefix.Length + suffix.Length) {
      return false;
    }

    int digitsEnd = paramName.Length - suffix.Length;
    for (int index = prefix.Length; index < digitsEnd; index++) {
      if (!char.IsAsciiDigit(paramName[index])) {
        return false;
      }
    }
    return true;
  }

  internal static string DecodeMavPortDeviceId(uint id) =>
      MavPortDeviceNames.TryGetValue(id, out string? name)
          ? name
          : $"Unknown ({id})";
}

public class HwIdRow {
  public string ParamName { get; init; } = "";
  public int DevID { get; init; }
  public string BusType { get; init; } = "";
  public int Bus { get; init; }
  public int Address { get; init; }
  public string DevType { get; init; } = "";
}
