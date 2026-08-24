using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Comms;
using MissionPlanner.Services;
using px4uploader;

namespace MissionPlanner.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigFirmwareLegacyViewModel : ViewModelBase {
  private const string AllOptions = "All";
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly List<APFirmware.FirmwareInfo> _allFirmwares = new();
  private int _refreshGeneration;
  private bool _updatingFilters;

  public ObservableCollection<string> Vehicles { get; } = new();
  public ObservableCollection<string> ReleaseTypes { get; } = new();
  public ObservableCollection<string> Platforms { get; } = new();
  public ObservableCollection<string> Formats { get; } = new();
  public ObservableCollection<FirmwareItem> Firmwares { get; } = new();

  [ObservableProperty]
  private string? _selectedVehicle;

  [ObservableProperty]
  private string? _selectedReleaseType = "OFFICIAL";

  [ObservableProperty]
  private string? _selectedPlatform = AllOptions;

  [ObservableProperty]
  private string? _selectedFormat = AllOptions;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
  private FirmwareItem? _selectedFirmware;

  [ObservableProperty]
  private string _status = "Select a vehicle type and firmware, then click Upload Firmware.";

  [ObservableProperty]
  private string _log = "";

  [ObservableProperty]
  private double _progress;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
  [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
  private bool _busy;

  public ConfigFirmwareLegacyViewModel() {
    foreach (var rt in Enum.GetNames(typeof(APFirmware.RELEASE_TYPES))) {
      ReleaseTypes.Add(rt);
    }
    foreach (var mt in Enum.GetNames(typeof(APFirmware.MAV_TYPE))) {
      Vehicles.Add(mt);
    }
    SelectedVehicle = Vehicles.FirstOrDefault();
    _ = RefreshList();
  }

  partial void OnSelectedVehicleChanged(string? value) => _ = RefreshList();

  partial void OnSelectedReleaseTypeChanged(string? value) => _ = RefreshList();

  partial void OnSelectedPlatformChanged(string? value) => ApplyFilters();

  partial void OnSelectedFormatChanged(string? value) => ApplyFilters();

  partial void OnSelectedFirmwareChanged(FirmwareItem? value) {
    if (value != null &&
        LegacyFirmwareUploader.InferManifestTarget(value.Info.Format, value.Info.Platform) == null) {
      Status = $"{value.Info.Format} firmware for {value.Info.Platform} has no safe automatic " +
          "uploader. Download it manually and use Load custom firmware with an explicit target.";
    }
  }

  [RelayCommand(CanExecute = nameof(CanRefresh))]
  private async Task Refresh() => await RefreshList(true);

  private bool CanRefresh() => !Busy;

  private async Task RefreshList(bool force = false) {
    if (SelectedVehicle == null || SelectedReleaseType == null) {
      return;
    }

    var generation = System.Threading.Interlocked.Increment(ref _refreshGeneration);
    Busy = true;
    Status = "Fetching firmware manifest…";
    try {
      var vehicle = SelectedVehicle;
      var reltype = SelectedReleaseType;
      var list = await Task.Run(() => {
        APFirmware.GetList(force: force);
        if (APFirmware.Manifest?.Firmware == null) {
          return new List<APFirmware.FirmwareInfo>();
        }

        return FilterFirmwareOptions(
            APFirmware.Manifest.Firmware, vehicle, reltype, null, null);
      });
      if (generation != System.Threading.Volatile.Read(ref _refreshGeneration)) {
        return;
      }

      _allFirmwares.Clear();
      _allFirmwares.AddRange(list);
      SelectedPlatform = ReplaceFilterValues(
          Platforms, list.Select(item => item.Platform), SelectedPlatform);
      SelectedFormat = ReplaceFilterValues(
          Formats, list.Select(item => item.Format), SelectedFormat);
      ApplyFilters();
    } catch (Exception ex) {
      if (generation == System.Threading.Volatile.Read(ref _refreshGeneration)) {
        Status = "Failed to fetch manifest: " + ex.Message;
      }
    } finally {
      if (generation == System.Threading.Volatile.Read(ref _refreshGeneration)) {
        Busy = false;
      }
    }
  }

  [RelayCommand(CanExecute = nameof(CanUpload))]
  private async Task Upload() {
    var item = SelectedFirmware;
    if (item?.Info.Url == null) {
      return;
    }

    var target = LegacyFirmwareUploader.InferManifestTarget(item.Info.Format, item.Info.Platform);
    if (target == null) {
      Status = $"No safe automatic uploader is available for {item.Info.Platform} " +
          $"({item.Info.Format}).";
      return;
    }

    string? portName = null;
    if (LegacyFirmwareUploader.RequiresSerialPort(target.Value)) {
      var ports = SerialPort.GetPortNames()
          .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
          .ToArray();
      var selected = await Dialogs.Select(
          "Select bootloader port",
          $"Select the physical serial port for {LegacyFirmwareUploader.DescribeTarget(target.Value)}.",
          ports);
      if (selected == null) {
        Status = ports.Length == 0 ? "No physical serial ports were found." : "Firmware upload canceled.";
        return;
      }
      portName = ports[selected.Value];
    }

    Busy = true;
    Progress = 0;
    Log = "";
    string? tempFile = null;
    try {
      tempFile = await DownloadFirmware(item.Info);
      AppendLog($"Saved firmware to {tempFile}");
      AppendLog($"Selected target: {LegacyFirmwareUploader.DescribeTarget(target.Value)}");
      await Task.Run(() => UploadFirmware(tempFile, target.Value, portName));
    } catch (Exception ex) {
      Status = "Upload failed: " + ex.Message;
      AppendLog(ex.ToString());
    } finally {
      if (tempFile != null && File.Exists(tempFile)) {
        try {
          File.Delete(tempFile);
        } catch {
        }
      }
      Busy = false;
    }
  }

  private bool CanUpload() => !Busy && SelectedFirmware != null &&
      LegacyFirmwareUploader.InferManifestTarget(
          SelectedFirmware.Info.Format, SelectedFirmware.Info.Platform) != null;

  private async Task<string> DownloadFirmware(APFirmware.FirmwareInfo info) {
    SetStatus("Downloading firmware…");
    var extension = Path.GetExtension(info.Url.AbsolutePath);
    if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12) {
      extension = "." + info.Format.TrimStart('.').ToLowerInvariant();
    }
    var dest = Path.Combine(
        Path.GetTempPath(), "ap_legacy_" + Guid.NewGuid().ToString("N") + extension);
    using var client = new HttpClient();
    var bytes = await client.GetByteArrayAsync(info.Url);
    await File.WriteAllBytesAsync(dest, bytes);
    return dest;
  }

  private void UploadFirmware(
      string path, LegacyFirmwareTarget target, string? portName) {
    switch (target) {
      case LegacyFirmwareTarget.Px4Bootloader:
        UploadToBoard(path);
        break;
      case LegacyFirmwareTarget.Stm32Dfu:
      case LegacyFirmwareTarget.Stm32DfuBinary:
        LegacyFirmwareUploader.UploadDfu(path, target, OnLegacyProgress);
        break;
      case LegacyFirmwareTarget.Apm1280:
      case LegacyFirmwareTarget.Apm2560:
      case LegacyFirmwareTarget.Apm2560V2:
        LegacyFirmwareUploader.UploadAvr(
            path, portName ?? throw new InvalidOperationException("No serial port was selected."),
            target, OnLegacyProgress);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(target), target, null);
    }
  }

  private void UploadToBoard(string filename) {
    SetStatus("Reading firmware file…");
    px4uploader.Firmware fw;
    try {
      fw = px4uploader.Firmware.ProcessFirmware(filename);
    } catch (Exception ex) {
      SetStatus("Invalid firmware file: " + ex.Message);
      return;
    }

    AppendLog($"Loaded firmware board_id={fw.board_id} rev={fw.board_revision}");

    AttemptRebootToBootloader();

    var deadline = DateTime.Now.AddSeconds(30);
    SetStatus("Scanning comports for bootloader…");

    while (DateTime.Now < deadline) {
      var ports = SerialPort.GetPortNames();
      Uploader? found = null;

      foreach (var port in ports) {
        if (ProbeStillRunning(port)) {
          AppendLog($"{port}: reboot probe still holds this port - skipping for now");
          continue;
        }
        Uploader up;
        try {
          up = new Uploader(port, 115200);
        } catch (Exception ex) {
          AppendLog($"{port}: {ex.Message}");
          continue;
        }

        try {
          up.identify();
          AppendLog($"{port}: board_type={up.board_type} bl_rev={up.bl_rev} fw_maxsize={up.fw_maxsize}");

          if (up.board_type != fw.board_id && !(up.board_type == 33 && fw.board_id == 9)) {
            AppendLog($"{port}: board mismatch (detected {up.board_type}, fw {fw.board_id}) - skipping");
            up.close();
            continue;
          }

          found = up;
          break;
        } catch (Exception ex) {
          AppendLog($"{port}: not a bootloader ({ex.Message})");
          try {
            up.close();
          } catch {
          }
        }
      }

      if (found == null) {
        System.Threading.Thread.Sleep(250);
        continue;
      }

      SetStatus("Connecting…");
      System.Threading.Thread.Sleep(500);

      found.ProgressEvent += OnUploaderProgress;
      found.LogEvent += OnUploaderLog;
      found.ConfirmEvent += _ => true;

      try {
        found.currentChecksum(fw);
        AppendLog("Firmware already on the board. No upload required.");
        SetStatus("No upload required — firmware already present.");
        try {
          found.__reboot();
        } catch {
        }
        return;
      } catch (IOException) {
        SetStatus("Lost communication with the board.");
        found.close();
        return;
      } catch (TimeoutException) {
        SetStatus("Communication timeout with the board.");
        found.close();
        return;
      } catch {

      }

      try {
        SetStatus("Uploading firmware…");
        SetProgress(0);
        found.upload(fw);
        SetProgress(100);
        SetStatus("Upload complete.");
      } catch (Exception ex) {
        SetStatus("ERROR: " + ex.Message);
        AppendLog(ex.ToString());
      } finally {
        found.close();
      }

      return;
    }

    SetStatus("ERROR: No response from board.");
  }

  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>>
      _probeTasks = new();

  private bool ProbeStillRunning(string port) =>
      _probeTasks.TryGetValue(port, out var t) && !t.IsCompleted;

  private void AttemptRebootToBootloader() {
    var ports = SerialPort.GetPortNames();
    var tasks = new List<Task<bool>>();

    foreach (var port in ports) {
      var task = Task.Run(() => {
        try {
          using var up = new Uploader(port, 115200);
          up.identify();
          return true;
        } catch {
          return false;
        }
      });
      _probeTasks[port] = task;
      tasks.Add(task);
    }

    // Wait for every probe to finish (bounded). Any probe that outlives the wait stays
    // registered in _probeTasks, and the bootloader scan skips its port until it completes.
    try {
      if (!Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(15))) {
        AppendLog("Some serial probes are still busy; their ports are skipped until released.");
      }
    } catch {
    }
    if (tasks.Any(t => t.IsCompletedSuccessfully && t.Result)) {
      return;
    }

    if (_comPort.BaseStream is SerialPort mavSerial) {
      try {
        // Do not reopen the MAVLink port while its probe still holds it.
        if (_probeTasks.TryGetValue(mavSerial.PortName, out var probe) && !probe.IsCompleted) {
          try {
            if (!probe.Wait(TimeSpan.FromSeconds(10))) {
              AppendLog($"{mavSerial.PortName}: reboot probe is still using the MAVLink port; " +
                        "heartbeat reopen deferred.");
              SetStatus("Waiting for the serial port to become available…");
              return;
            }
          } catch {
            return;
          }
        }
        SetStatus("Looking for heartbeat…");
        var heartbeatTask = Task.Run(() => {
          try {
            _comPort.BaseStream.Open();
            _comPort.giveComport = true;
            if (_comPort.getHeartBeat().Length == 0) {
              throw new Exception("No heartbeat found");
            }
            _comPort.doReboot(true, false);
            _comPort.Close();
            return true;
          } catch (Exception ex) {
            AppendLog(ex.Message);
            try {
              _comPort.BaseStream.Close();
            } catch {
            }
            return false;
          } finally {
            _comPort.giveComport = false;
          }
        });
        // A timed-out heartbeat attempt remains registered, so the following bootloader scan
        // cannot open the same port until the task has actually released it.
        _probeTasks[mavSerial.PortName] = heartbeatTask;
        if (heartbeatTask.Wait(TimeSpan.FromSeconds(5)) &&
            heartbeatTask.GetAwaiter().GetResult()) {
          SetStatus("Rebooting to bootloader…");
        } else {
          SetStatus("Please unplug the board and plug it back in.");
        }
      } catch (Exception ex) {
        AppendLog(ex.Message);
        SetStatus("Please unplug the board and plug it back in.");
      }
    }
  }

  private void OnUploaderProgress(double completed) => SetProgress(completed);

  private void OnUploaderLog(string message, int level) => AppendLog(message);

  private void OnLegacyProgress(int percent, string status) {
    if (percent >= 0) {
      SetProgress(percent);
    }
    SetStatus(status);
    AppendLog(status);
  }

  private void ApplyFilters() {
    if (_updatingFilters || SelectedVehicle == null || SelectedReleaseType == null) {
      return;
    }

    var list = FilterFirmwareOptions(
        _allFirmwares,
        SelectedVehicle,
        SelectedReleaseType,
        SelectedPlatform == AllOptions ? null : SelectedPlatform,
        SelectedFormat == AllOptions ? null : SelectedFormat);
    Firmwares.Clear();
    foreach (var firmware in list) {
      Firmwares.Add(new FirmwareItem(firmware));
    }
    SelectedFirmware = Firmwares.FirstOrDefault();
    Status = Firmwares.Count > 0
      ? $"{Firmwares.Count} firmware images match the selected vehicle, release, platform and format."
      : "No firmware matches the selected filters.";
  }

  private string ReplaceFilterValues(
      ObservableCollection<string> destination,
      IEnumerable<string?> values,
      string? selected) {
    _updatingFilters = true;
    try {
      destination.Clear();
      destination.Add(AllOptions);
      foreach (var value in values
                   .Where(value => !string.IsNullOrWhiteSpace(value))
                   .Select(value => value!)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) {
        destination.Add(value);
      }
      return selected != null && destination.Contains(selected) ? selected : AllOptions;
    } finally {
      _updatingFilters = false;
    }
  }

  internal static List<APFirmware.FirmwareInfo> FilterFirmwareOptions(
      IEnumerable<APFirmware.FirmwareInfo> source,
      string? vehicle,
      string? releaseType,
      string? platform,
      string? format) {
    ArgumentNullException.ThrowIfNull(source);
    return source
        .Where(item => vehicle == null ||
            string.Equals(item.MavType, vehicle, StringComparison.OrdinalIgnoreCase))
        .Where(item => releaseType == null ||
            string.Equals(item.MavFirmwareVersionType, releaseType, StringComparison.OrdinalIgnoreCase))
        .Where(item => platform == null ||
            string.Equals(item.Platform, platform, StringComparison.OrdinalIgnoreCase))
        .Where(item => format == null ||
            string.Equals(item.Format, format, StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.Platform, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Format, StringComparer.OrdinalIgnoreCase)
        .ThenByDescending(item => item.MavFirmwareVersion)
        .ToList();
  }

  private void SetStatus(string status) => Dispatcher.UIThread.Post(() => Status = status);

  private void SetProgress(double value) => Dispatcher.UIThread.Post(() => Progress = value);

  private void AppendLog(string line) =>
    Dispatcher.UIThread.Post(() => Log = (Log + line + Environment.NewLine));
}

public class FirmwareItem {
  public FirmwareItem(APFirmware.FirmwareInfo info) {
    Info = info;
  }

  public APFirmware.FirmwareInfo Info { get; }

  public string Display {
    get {
      var ver = string.IsNullOrEmpty(Info.MavFirmwareVersionStr)
        ? Info.MavFirmwareVersion?.ToString()
        : Info.MavFirmwareVersionStr;
      return $"{Info.Platform}  {ver}  {Info.Format}  board {Info.BoardId}  " +
          $"({Info.MavFirmwareVersionType})  {Info.Url}";
    }
  }
}
