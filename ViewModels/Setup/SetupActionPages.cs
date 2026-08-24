using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Comms;
using MissionPlanner.Services;
using RawSerialPort = MissionPlanner.Comms.SerialPort;

namespace MissionPlanner.ViewModels.Setup;

public partial class SikRadioViewModel : ViewModelBase {

  private static readonly (int Num, string Name, string Label)[] _regMap = {
    (0, "FORMAT", "Format"),
    (1, "SERIAL_SPEED", "Baud"),
    (2, "AIR_SPEED", "Air Speed"),
    (3, "NETID", "Net ID"),
    (4, "TXPOWER", "Tx Power"),
    (5, "ECC", "ECC"),
    (6, "MAVLINK", "Mavlink"),
    (7, "OPPRESEND", "Op Resend"),
    (8, "MIN_FREQ", "Min Freq"),
    (9, "MAX_FREQ", "Max Freq"),
    (10, "NUM_CHANNELS", "# Channels"),
    (11, "DUTY_CYCLE", "Duty Cycle"),
    (12, "LBT_RSSI", "LBT RSSI"),
    (13, "MANCHESTER", "Manchester"),
    (14, "RTSCTS", "RTS CTS"),
    (15, "MAX_WINDOW", "Max Window"),
  };

  private static readonly int[] _candidateBauds = { 57600, 115200, 38400, 19200, 9600 };
  private static readonly Regex _regLine =
      new(@"S(\d+):\s*([A-Za-z0-9_/]+)\s*=\s*(\S+)", RegexOptions.Compiled);
  private static readonly Regex _sikBanner = new(@"SiK|RFD", RegexOptions.Compiled);

  public partial class SikRegister : ObservableObject {
    public SikRegister(int num, string name, string label) {
      Num = num;
      Name = name;
      Label = label;
    }

    public int Num { get; set; }
    public string Name { get; }
    public string Label { get; }
    public string OrigLocal { get; set; } = "";
    public string OrigRemote { get; set; } = "";
    public bool HasRemote { get; set; }
    public int? Minimum { get; set; }
    public int? Maximum { get; set; }

    public ObservableCollection<string> Options { get; } = new();
    public bool HasOptions => Options.Count > 0;

    public void SetOptions(IEnumerable<string> values) {
      Options.Clear();
      foreach (var v in values) {
        Options.Add(v);
      }
      OnPropertyChanged(nameof(HasOptions));
    }

    public void EnsureOption(string value) {
      if (Options.Count > 0 && !string.IsNullOrEmpty(value) && !Options.Contains(value)) {
        Options.Insert(0, value);
      }
    }

    [ObservableProperty]
    private string _localValue = "";

    [ObservableProperty]
    private string _remoteValue = "";
  }

  public string Title { get; } = "SiK Radio";

  public string Instructions { get; } =
      "Configure a SiK/RFD telemetry radio over a raw serial port or through the selected "
      + "disarmed autopilot's MAVLink SERIAL_CONTROL TELEM1 tunnel, then Load Settings.";

  public ObservableCollection<SikRegister> Registers { get; } = new();
  public ObservableCollection<string> Ports { get; } = new();

  public int[] Bauds { get; } =
      { 57600, 115200, 38400, 19200, 9600, 230400, 250000 };

  [ObservableProperty]
  private string? _selectedPort;

  [ObservableProperty]
  private int _selectedBaud = 57600;

  [ObservableProperty]
  private bool _useMavlinkSerialControl;

  [ObservableProperty]
  private string _localVersion = "";

  [ObservableProperty]
  private string _remoteVersion = "";

  [ObservableProperty]
  private string _status = "Idle";

  [ObservableProperty]
  private string _log = "";

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private string _boardType = "";

  [ObservableProperty]
  private string _remoteBoardType = "";

  [ObservableProperty]
  private string _freqBand = "";

  [ObservableProperty]
  private string _countryCode = "";

  [ObservableProperty]
  private string _remoteCountryCode = "";

  [ObservableProperty]
  private string _rssiInfo = "";

  [ObservableProperty]
  private string _aesKey = "";

  [ObservableProperty]
  private bool _aesEnabled;

  [ObservableProperty]
  private string _remoteAesKey = "";

  [ObservableProperty]
  private bool _remoteAesEnabled;

  [ObservableProperty]
  private string _commandText = "ATI";

  [ObservableProperty]
  private bool _terminalOpen;

  [ObservableProperty]
  private bool _rssiRunning;

  public string TerminalButtonLabel => TerminalOpen ? "Close Terminal" : "Open Terminal";
  public string RssiButtonLabel => RssiRunning ? "Stop RSSI" : "Start RSSI";

  public IBrush StatusLed =>
      RssiRunning ? Brushes.LimeGreen
      : (IsBusy || _session != null || TerminalOpen) ? Brushes.Goldenrod
      : Brushes.DimGray;

  public string StatusLedLabel =>
      RssiRunning ? "Link live" : (IsBusy || _session != null || TerminalOpen) ? "Active" : "Idle";

  public event Action<double, double, double, double, double>? RssiSample;
  public event Action? RssiReset;

  private static readonly Regex _rssiLine =
      new(@"RSSI:\s*([0-9]+)/([0-9]+)\s+L/R noise:\s*([0-9]+)/([0-9]+)", RegexOptions.Compiled);

  public SikRadioViewModel() {
    foreach (var (num, name, label) in _regMap) {
      var reg = new SikRegister(num, name, label);
      var opts = DefaultOptions(name, rfd: false);
      if (opts != null) {
        reg.SetOptions(opts);
      }
      Registers.Add(reg);
    }

    RefreshPorts();

    var baud = AppState.comPort.BaseStream?.BaudRate ?? 0;
    if (baud > 0 && Bauds.Contains(baud)) {
      SelectedBaud = baud;
    }
    UseMavlinkSerialControl = LinkOpen;
  }

  private string _origAesKey = "";
  private string _origRemoteAesKey = "";

  private ICommsSerial? _session;
  private System.Threading.CancellationTokenSource? _rssiCts;

  private bool LinkOpen => AppState.comPort.BaseStream?.IsOpen == true;
  private bool NotBusy => !IsBusy && _session == null;

  partial void OnIsBusyChanged(bool value) {
    OnPropertyChanged(nameof(StatusLed));
    OnPropertyChanged(nameof(StatusLedLabel));
    LoadCommand.NotifyCanExecuteChanged();
    SaveCommand.NotifyCanExecuteChanged();
    ResetDefaultsCommand.NotifyCanExecuteChanged();
    RefreshPortsCommand.NotifyCanExecuteChanged();
    UploadFirmwareCommand.NotifyCanExecuteChanged();
    OpenTerminalCommand.NotifyCanExecuteChanged();
    StartRssiCommand.NotifyCanExecuteChanged();
    SendCommandCommand.NotifyCanExecuteChanged();
    SetLocalPpmFailsafeCommand.NotifyCanExecuteChanged();
    SetRemotePpmFailsafeCommand.NotifyCanExecuteChanged();
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private void RefreshPorts() {
    var current = SelectedPort;
    Ports.Clear();
    foreach (var p in RawSerialPort.GetPortNames().OrderBy(p => p)) {
      Ports.Add(p);
    }

    var prefer = AppState.comPort.BaseStream?.PortName;
    if (current != null && Ports.Contains(current)) {
      SelectedPort = current;
    } else if (!string.IsNullOrEmpty(prefer) && Ports.Contains(prefer)) {
      SelectedPort = prefer;
    } else {
      SelectedPort = Ports.FirstOrDefault();
    }
  }

  private bool GuardLink() {
    if (UseMavlinkSerialControl) {
      if (!LinkOpen) {
        AppendLog("MAVLink SERIAL_CONTROL was selected, but there is no live autopilot link.");
        Status = "Connect a disarmed autopilot first";
        return false;
      }
      return true;
    }
    if (LinkOpen) {
      AppendLog("MAVLink link is open. Enable 'MAVLink TELEM1' to reach a radio attached to the "
          + "selected autopilot, or disconnect before opening a raw physical serial port.");
      Status = "Choose MAVLink TELEM1 or disconnect";
      return false;
    }
    if (string.IsNullOrEmpty(SelectedPort)) {
      AppendLog("Select a serial port first.");
      return false;
    }
    return true;
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task Load() {
    if (!GuardLink()) {
      return;
    }

    var port = SelectedPort!;
    var baud = SelectedBaud;
    IsBusy = true;
    Status = "Loading…";
    AppendLog("=== Load Settings ===");

    await Task.Run(() => {
      ICommsSerial? sp = null;
      try {
        sp = Connect(port, baud, out var used);
        if (sp == null) {
          SetStatus("Failed to enter AT mode");
          AppendLog("Could not enter AT command mode on any candidate baud.");
          return;
        }

        AppendLog("In AT command mode @ " + used + " baud.");
        DoCommand(sp, "AT&T", false);

        var ati = DoCommand(sp, "ATI").Trim();
        Ui(() => LocalVersion = ati);

        var isRfd = ati.IndexOf("RFD", StringComparison.OrdinalIgnoreCase) >= 0
            || ati.IndexOf("MP on", StringComparison.OrdinalIgnoreCase) >= 0;

        var ati2 = DoCommand(sp, "ATI2").Trim();
        Ui(() => BoardType = ati2);
        if (SikRadioFirmwareService.TryParseBoard(ati2, out var loadedBoard)
            && SikRadioFirmwareService.CountryRegister(loadedBoard) is int countryRegister) {
          var country = StripMultipointPrefix(DoCommand(sp, "AT+C" + countryRegister + "?").Trim());
          Ui(() => CountryCode = country);
        } else {
          Ui(() => CountryCode = "");
        }
        var ati3 = DoCommand(sp, "ATI3").Trim();
        Ui(() => FreqBand = ati3);
        var ati7 = DoCommand(sp, "ATI7").Trim();
        Ui(() => RssiInfo = ati7);

        var aes = DoCommand(sp, "AT&E?").Trim();
        if (aes.Length == 0 || aes.Contains("ERROR")) {
          _origAesKey = "";
          Ui(() => { AesKey = ""; AesEnabled = false; });
        } else {
          _origAesKey = aes;
          Ui(() => { AesKey = aes; AesEnabled = true; });
        }

        ResetOrig(remote: false);
        ParseInto(DoCommand(sp, "ATI5", true), remote: false);
        ApplyFirmwareOptions(isRfd);
        ApplySettingMetadata(QuerySettingMetadata(sp, remote: false), remote: false);

        var rti = DoCommand(sp, "RTI").Trim();
        if (_sikBanner.IsMatch(rti)) {
          Ui(() => RemoteVersion = rti);
          string remoteBoard = StripMultipointPrefix(DoCommand(sp, "RTI2").Trim());
          Ui(() => RemoteBoardType = remoteBoard);
          if (SikRadioFirmwareService.TryParseBoard(ati2, out loadedBoard)
              && SikRadioFirmwareService.CountryRegister(loadedBoard) is int remoteCountryRegister) {
            string remoteCountry = StripMultipointPrefix(
                DoCommand(sp, "RT+C" + remoteCountryRegister + "?").Trim());
            Ui(() => RemoteCountryCode = remoteCountry);
          }
          ResetOrig(remote: true);
          ParseInto(DoCommand(sp, "RTI5", true), remote: true);
          ApplySettingMetadata(QuerySettingMetadata(sp, remote: true), remote: true);

          var remoteAes = StripMultipointPrefix(DoCommand(sp, "RT&E?").Trim());
          if (remoteAes.Length == 0 || remoteAes.Contains("ERROR")) {
            _origRemoteAesKey = "";
            Ui(() => { RemoteAesKey = ""; RemoteAesEnabled = false; });
          } else {
            _origRemoteAesKey = remoteAes;
            Ui(() => { RemoteAesKey = remoteAes; RemoteAesEnabled = true; });
          }
        } else {
          _origRemoteAesKey = "";
          Ui(() => {
            RemoteVersion = "(no remote)";
            RemoteBoardType = "";
            RemoteCountryCode = "";
            RemoteAesKey = "";
            RemoteAesEnabled = false;
          });
          AppendLog("No remote radio responded to RTI.");
        }

        DoCommand(sp, "ATO", false);
        SetStatus("Loaded");
      } catch (Exception ex) {
        AppendLog("Load error: " + ex.Message);
        SetStatus("Error");
      } finally {
        ClosePort(sp);
      }
    });

    IsBusy = false;
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task Save() {
    if (!GuardLink()) {
      return;
    }

    string? validationError = ValidatePendingSettings();
    if (validationError != null) {
      Status = "Invalid settings — nothing sent";
      AppendLog(validationError);
      return;
    }

    var port = SelectedPort!;
    var baud = SelectedBaud;
    var snapshot = Registers.Select(r => (r.Num, r.Name, r.LocalValue, r.OrigLocal,
        r.RemoteValue, r.OrigRemote, r.HasRemote)).ToArray();
    var aesEnabled = AesEnabled;
    var aesKey = AesKey?.Trim() ?? "";
    var origAes = _origAesKey;
    var remoteAesEnabled = RemoteAesEnabled;
    var remoteAesKey = RemoteAesKey?.Trim() ?? "";
    var origRemoteAes = _origRemoteAesKey;
    IsBusy = true;
    Status = "Saving…";
    AppendLog("=== Save Settings ===");

    await Task.Run(() => {
      ICommsSerial? sp = null;
      try {
        sp = Connect(port, baud, out var used);
        if (sp == null) {
          SetStatus("Failed to enter AT mode");
          AppendLog("Could not enter AT command mode.");
          return;
        }

        AppendLog("In AT command mode @ " + used + " baud.");
        DoCommand(sp, "AT&T", false);

        var remoteChanged = snapshot.Any(s =>
            s.Num != 0 && s.HasRemote && s.RemoteValue != s.OrigRemote);
        if (remoteChanged) {
          DoCommand(sp, "RTI5", true);
          foreach (var s in snapshot) {
            if (s.Num != 0 && s.HasRemote && s.RemoteValue != s.OrigRemote) {
              if (!SikRadioSettingsService.IsValidInteger(s.RemoteValue)) {
                AppendLog("RTS" + s.Num + " (" + s.Name + ")='" + s.RemoteValue
                    + "' SKIPPED (not a valid integer)");
                continue;
              }
              var ans = DoCommand(sp, "RTS" + s.Num + "=" + s.RemoteValue);
              AppendLog("RTS" + s.Num + " (" + s.Name + ")=" + s.RemoteValue
                  + (ans.Contains("OK") ? " OK" : " FAILED"));
            }
          }
          if (remoteAesEnabled && remoteAesKey != origRemoteAes) {
            if (SikRadioSettingsService.IsValidHexKey(remoteAesKey)) {
              var ans = DoCommand(sp, "RT&E=" + remoteAesKey, true);
              AppendLog("RT&E (remote AES key)" + (ans.Contains("ERROR") ? " FAILED" : " OK"));
            } else {
              AppendLog("Remote AES key SKIPPED (must be 1..64 hex characters)");
            }
          }
          DoCommand(sp, "RT&W");
          DoCommand(sp, "RTZ");
        } else if (remoteAesEnabled && remoteAesKey != origRemoteAes) {
          if (SikRadioSettingsService.IsValidHexKey(remoteAesKey)) {
            var ans = DoCommand(sp, "RT&E=" + remoteAesKey, true);
            AppendLog("RT&E (remote AES key)" + (ans.Contains("ERROR") ? " FAILED" : " OK"));
            DoCommand(sp, "RT&W");
            DoCommand(sp, "RTZ");
          } else {
            AppendLog("Remote AES key SKIPPED (must be 1..64 hex characters)");
          }
        }

        DoCommand(sp, "ATI5", true);
        foreach (var s in snapshot) {
          if (s.Num != 0 && s.LocalValue != s.OrigLocal) {

            if (!int.TryParse(s.LocalValue, out _)) {
              AppendLog("ATS" + s.Num + " (" + s.Name + ")='" + s.LocalValue
                  + "' SKIPPED (not a valid integer)");
              continue;
            }
            var ans = DoCommand(sp, "ATS" + s.Num + "=" + s.LocalValue);
            AppendLog("ATS" + s.Num + " (" + s.Name + ")=" + s.LocalValue
                + (ans.Contains("OK") ? " OK" : " FAILED"));
          }
        }

        if (aesEnabled && aesKey != origAes) {
          if (SikRadioSettingsService.IsValidHexKey(aesKey)) {
            var ans = DoCommand(sp, "AT&E=" + aesKey, true);
            AppendLog("AT&E (AES key)" + (ans.Contains("ERROR") ? " FAILED" : " OK"));
          } else {
            AppendLog("AES key SKIPPED (must be 1..64 hex characters)");
          }
        }

        var w = DoCommand(sp, "AT&W");
        AppendLog("AT&W (write eeprom)" + (w.Contains("OK") ? " OK" : " FAILED"));
        DoCommand(sp, "ATZ");
        SetStatus("Saved & rebooted");
      } catch (Exception ex) {
        AppendLog("Save error: " + ex.Message);
        SetStatus("Error");
      } finally {
        ClosePort(sp);
      }
    });

    IsBusy = false;
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task ResetDefaults() {
    if (!GuardLink()) {
      return;
    }

    var port = SelectedPort!;
    var baud = SelectedBaud;
    var hasRemote = Registers.Any(r => r.HasRemote);
    IsBusy = true;
    Status = "Resetting…";
    AppendLog("=== Reset to Defaults ===");

    await Task.Run(() => {
      ICommsSerial? sp = null;
      try {
        sp = Connect(port, baud, out var used);
        if (sp == null) {
          SetStatus("Failed to enter AT mode");
          AppendLog("Could not enter AT command mode.");
          return;
        }

        AppendLog("In AT command mode @ " + used + " baud.");
        DoCommand(sp, "AT&T", false);

        if (hasRemote) {
          DoCommand(sp, "RT&F");
          DoCommand(sp, "RT&W");
          DoCommand(sp, "RTZ");
          AppendLog("Remote reset to factory defaults.");
        }

        DoCommand(sp, "AT&F");
        DoCommand(sp, "AT&W");
        DoCommand(sp, "ATZ");
        AppendLog("Local reset to factory defaults.");
        SetStatus("Reset & rebooted");
      } catch (Exception ex) {
        AppendLog("Reset error: " + ex.Message);
        SetStatus("Error");
      } finally {
        ClosePort(sp);
      }
    });

    IsBusy = false;
  }

  [ObservableProperty]
  private bool _betaFirmware;

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task UploadFirmware() => await UploadFirmwareFile(null);

  public async Task UploadFirmwareFromFile(string path) => await UploadFirmwareFile(path);

  private async Task UploadFirmwareFile(string? selectedFile) {
    if (!GuardLink()) {
      return;
    }

    var port = SelectedPort!;
    var baud = SelectedBaud;
    IsBusy = true;
    Status = "Programming firmware…";
    AppendLog("=== Upload Firmware (" + (selectedFile == null
        ? (BetaFirmware ? "beta" : "stable") : "selected file") + ") ===");

    await Task.Run(() => {
      ICommsSerial? atsp = null;
      ICommsSerial? boot = null;
      string? temporaryFirmware = null;
      try {
        atsp = Connect(port, baud, out var used);
        if (atsp == null) {
          SetStatus("Failed to enter AT mode");
          AppendLog("Could not enter AT command mode — cannot reflash.");
          return;
        }
        AppendLog("In AT mode @ " + used + " baud. Identifying modem before bootloader entry…");
        string firmwareBanner = DoCommand(atsp, "ATI").Trim();
        bool dinioRequired = firmwareBanner.Contains("DINIO", StringComparison.OrdinalIgnoreCase);
        string boardReply = DoCommand(atsp, "ATI2").Trim();
        if (!SikRadioFirmwareService.TryParseBoard(boardReply, out var board)) {
          SetStatus("Unable to identify radio board");
          AppendLog("ATI2 returned an unsupported board code: " + boardReply);
          return;
        }
        Ui(() => BoardType = board.ToString());

        bool countryLocked = false;
        if (SikRadioFirmwareService.CountryRegister(board) is int countryRegister) {
          string country = StripMultipointPrefix(
              DoCommand(atsp, "AT+C" + countryRegister + "?").Trim());
          countryLocked = SikRadioFirmwareService.IsCountryLocked(country);
          Ui(() => CountryCode = country);
          AppendLog("Country lock: " + (countryLocked ? country : "not locked"));
        }

        string firmwarePath;
        if (selectedFile == null) {
          string? url = SikRadioFirmwareService.StableFirmwareUrl(board, BetaFirmware);
          if (url == null) {
            SetStatus(BetaFirmware && SikRadioFirmwareService.UsesXModem(board)
                ? "No vendor beta channel — uncheck Beta or choose a file"
                : "Board not supported by SiK/RFD uploader");
            AppendLog($"No verified {(BetaFirmware ? "beta" : "stable")} image is configured for {board}.");
            return;
          }
          temporaryFirmware = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
              Guid.NewGuid().ToString("N") + "-" + System.IO.Path.GetFileName(new Uri(url).LocalPath));
          firmwarePath = temporaryFirmware;
          AppendLog("Downloading verified upstream image " + url);
          if (!MissionPlanner.Utilities.Download.getFilefromNet(url, firmwarePath)) {
            SetStatus("Firmware download failed");
            AppendLog("Could not download firmware — aborted before bootloader entry.");
            return;
          }
        } else {
          firmwarePath = selectedFile;
        }

        if (!SikRadioFirmwareService.ValidateImage(
                board, firmwarePath, countryLocked, out string validationError, dinioRequired)) {
          SetStatus("Firmware image rejected");
          AppendLog(validationError + " Aborted before bootloader entry.");
          return;
        }

        AppendLog("Image matches " + board + ". Rebooting into bootloader (AT&UPDATE)…");
        atsp.DiscardInBuffer();
        atsp.Write("\r\n");
        Thread.Sleep(100);
        atsp.Write("AT&UPDATE\r\n");
        Thread.Sleep(700);
        ClosePort(atsp);
        atsp = null;

        bool xModem = SikRadioFirmwareService.UsesXModem(board);
        int bootBaud = xModem ? 57600 : 115200;
        boot = OpenTransport(port, bootBaud);
        boot.ReadTimeout = 3000;
        Thread.Sleep(300);

        if (xModem) {
          AppendLog("Using RFDesign X-series XModem bootloader.");
          bool uploaded = SikRadioFirmwareService.UploadXModem(
              firmwarePath, boot, SikRadioFirmwareService.UsesHighSpeedXModem(board),
              (message, progress) => {
                if (!string.IsNullOrEmpty(message)) {
                  AppendLog(message);
                }
                if (!double.IsNaN(progress)) {
                  SetStatus($"Programming… {progress * 100:0}%");
                }
              });
          if (!uploaded) {
            SetStatus("XModem upload failed — power-cycle and retry");
            return;
          }
          SetStatus("Firmware programmed");
          AppendLog("Firmware uploaded; modem reboot requested.");
          return;
        }

        var up = new MissionPlanner.Radio.Uploader();
        up.LogEvent += (m, _) => AppendLog(m.TrimEnd());
        up.ProgressEvent += pct => SetStatus($"Programming… {pct * 100:0}%");
        up.port = boot;
        up.connect_and_sync();

        var bootBoard = MissionPlanner.Radio.Uploader.Board.FAILED;
        var freq = MissionPlanner.Radio.Uploader.Frequency.FREQ_NONE;
        up.getDevice(ref bootBoard, ref freq);
        AppendLog($"Bootloader: board={bootBoard} freq={freq}");
        if (bootBoard != board) {
          SetStatus("Bootloader board mismatch");
          AppendLog($"Firmware mode reported {bootBoard}, but AT mode reported {board}. "
              + "Aborted before erase.");
          return;
        }

        var ihex = new MissionPlanner.Radio.IHex();
        ihex.load(firmwarePath);
        AppendLog($"Loaded {ihex.Count} hex blocks. Erasing + programming…");
        up.upload(boot, ihex);

        SetStatus("Firmware programmed");
        AppendLog("Firmware programmed + verified. Radio rebooted.");
      } catch (Exception ex) {
        AppendLog("Upload error: " + ex.Message);
        SetStatus("Upload failed — power-cycle the radio and retry");
      } finally {
        ClosePort(atsp);
        try {
          if (boot?.IsOpen == true) {
            boot.Close();
          }
        } catch {

        }
        if (temporaryFirmware != null) {
          try {
            System.IO.File.Delete(temporaryFirmware);
          } catch {
          }
        }
      }
    });

    IsBusy = false;
  }

  public string UploadFirmwareTooltip { get; } =
      "Reflash SiK/RFD firmware (HM_TRP, RFD900/a/p/u/x/ux/X2). Disconnect MAVLink first.";

  [RelayCommand]
  private void RandomAesKey() {
    Span<byte> bytes = stackalloc byte[16];
    RandomNumberGenerator.Fill(bytes);
    AesKey = Convert.ToHexString(bytes);
    AppendLog("Generated random AES key.");
  }

  [RelayCommand]
  private void CopyRequiredToRemote() {
    foreach (var s in Registers) {
      if (s.Num == 0) {
        continue;
      }
      s.RemoteValue = s.LocalValue;
      s.EnsureOption(s.LocalValue);
      s.HasRemote = true;
    }
    if (AesEnabled && RemoteAesEnabled) {
      RemoteAesKey = AesKey;
    }
    Status = "Copy then Save to apply";
    AppendLog("Copied Local register values to Remote. Save to apply to the remote radio.");
  }

  public string ExportProfile(bool remote) {
    var values = Registers
        .Where(setting => setting.Num != 0)
        .Select(setting => new KeyValuePair<string, string>(setting.Name,
            remote ? setting.RemoteValue : setting.LocalValue))
        .ToList();
    string key = remote ? RemoteAesKey : AesKey;
    if (!string.IsNullOrWhiteSpace(key)) {
      values.Add(new KeyValuePair<string, string>("AESKEY", key));
    }
    return SikRadioSettingsService.SerializeProfile(values);
  }

  public (int Applied, int Unknown, int Invalid, int Ignored) ImportProfile(
      string text, bool remote) {
    SikRadioProfile profile = SikRadioSettingsService.ParseProfile(text);
    int applied = 0;
    int unknown = 0;
    int invalid = 0;
    foreach (var pair in profile.Values) {
      if (pair.Key.Equals("AESKEY", StringComparison.OrdinalIgnoreCase)) {
        bool enabled = remote ? RemoteAesEnabled : AesEnabled;
        if (!enabled || !SikRadioSettingsService.IsValidHexKey(pair.Value)) {
          invalid++;
        } else {
          if (remote) {
            RemoteAesKey = pair.Value;
          } else {
            AesKey = pair.Value;
          }
          applied++;
        }
        continue;
      }

      SikRegister? setting = Registers.FirstOrDefault(item =>
          item.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
      if (setting == null) {
        unknown++;
      } else if (!SikRadioSettingsService.IsValidInteger(pair.Value)) {
        invalid++;
      } else {
        setting.EnsureOption(pair.Value);
        if (remote) {
          if (!setting.HasRemote) {
            unknown++;
            continue;
          }
          setting.RemoteValue = pair.Value;
        } else {
          setting.LocalValue = pair.Value;
        }
        applied++;
      }
    }
    Status = "Profile staged — Save to apply";
    AppendLog($"Profile staged: applied={applied}, unknown={unknown}, invalid={invalid}, "
        + $"ignored lines={profile.IgnoredLines}. Nothing sent until Save Settings.");
    return (applied, unknown, invalid, profile.IgnoredLines);
  }

  public string SuggestedProfileFileName(bool remote) {
    string side = remote ? "remote" : "local";
    string board = Regex.Replace(BoardType, @"[^A-Za-z0-9_-]+", "-").Trim('-');
    return $"sik-{side}-{(board.Length == 0 ? "radio" : board)}.ini";
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task SetLocalPpmFailsafe() => await SetPpmFailsafe(remote: false);

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task SetRemotePpmFailsafe() => await SetPpmFailsafe(remote: true);

  private async Task SetPpmFailsafe(bool remote) {
    if (!GuardLink() || !await Dialogs.ConfirmDangerous(
            "Capture PPM failsafe",
            $"The {(remote ? "remote" : "local")} radio will capture its current PPM input as "
                + "the failsafe state and write it to EEPROM. Verify receiver outputs first.",
            "CAPTURE FAILSAFE")) {
      return;
    }

    string port = SelectedPort!;
    int baud = SelectedBaud;
    IsBusy = true;
    Status = "Capturing PPM failsafe…";
    await Task.Run(() => {
      ICommsSerial? sp = null;
      try {
        sp = Connect(port, baud, out _);
        if (sp == null) {
          SetStatus("Failed to enter AT mode");
          return;
        }
        string set = remote ? "RT&R" : "AT&R";
        string save = remote ? "RT&W" : "AT&W";
        string answer = DoCommand(sp, set);
        if (!answer.Contains("OK")) {
          SetStatus("Failsafe capture failed");
          AppendLog(set + " FAILED");
          return;
        }
        DoCommand(sp, save);
        if (remote) {
          DoCommand(sp, "RTZ");
        } else {
          DoCommand(sp, "ATZ");
        }
        SetStatus("PPM failsafe captured");
        AppendLog(set + " OK; saved to EEPROM.");
      } finally {
        ClosePort(sp);
      }
    });
    IsBusy = false;
  }

  private bool CanOpenTerminal => !IsBusy && !RssiRunning;
  private bool CanSendCommand => _session != null && TerminalOpen;

  partial void OnTerminalOpenChanged(bool value) {
    OnPropertyChanged(nameof(TerminalButtonLabel));
    SendCommandCommand.NotifyCanExecuteChanged();
    OnIsBusyChanged(false);
  }

  partial void OnRssiRunningChanged(bool value) {
    OnPropertyChanged(nameof(RssiButtonLabel));
    OnPropertyChanged(nameof(StatusLed));
    OnPropertyChanged(nameof(StatusLedLabel));
    OpenTerminalCommand.NotifyCanExecuteChanged();
    OnIsBusyChanged(false);
  }

  [RelayCommand(CanExecute = nameof(CanOpenTerminal))]
  private async Task OpenTerminal() {
    if (TerminalOpen) {
      await CloseSession();
      return;
    }

    if (!GuardLink()) {
      return;
    }

    var port = SelectedPort!;
    var baud = SelectedBaud;
    AppendLog("=== Open AT terminal ===");
    await Task.Run(() => {
      var sp = Connect(port, baud, out var used);
      if (sp == null) {
        SetStatus("Failed to enter AT mode");
        return;
      }
      _session = sp;
      AppendLog("Terminal in AT command mode @ " + used + " baud. Type AT commands below.");
      Ui(() => { TerminalOpen = true; Status = "Terminal open"; });
    });
  }

  [RelayCommand(CanExecute = nameof(CanSendCommand))]
  private async Task SendCommand() {
    var sp = _session;
    var cmd = CommandText?.Trim() ?? "";
    if (sp == null || cmd.Length == 0) {
      return;
    }

    await Task.Run(() => {
      try {
        DoCommand(sp, cmd, true);
      } catch (Exception ex) {
        AppendLog("terminal error: " + ex.Message);
      }
    });
  }

  private bool CanStartRssi => !IsBusy && !TerminalOpen;

  [RelayCommand(CanExecute = nameof(CanStartRssi))]
  private async Task StartRssi() {
    if (RssiRunning) {
      await StopRssi();
      return;
    }

    if (!GuardLink()) {
      return;
    }

    var port = SelectedPort!;
    var baud = SelectedBaud;
    AppendLog("=== Start RSSI stream ===");
    RssiReset?.Invoke();

    var ok = await Task.Run(() => {
      var sp = Connect(port, baud, out var used);
      if (sp == null) {
        SetStatus("Failed to enter AT mode");
        return false;
      }
      AppendLog("AT mode @ " + used + " baud. Enabling RSSI debug report.");
      DoCommand(sp, "AT&T=RSSI", true);
      DoCommand(sp, "ATO", false);
      _session = sp;
      return true;
    });

    if (!ok) {
      return;
    }

    _rssiCts = new System.Threading.CancellationTokenSource();
    var token = _rssiCts.Token;
    RssiRunning = true;
    Status = "RSSI streaming";

    _ = Task.Run(() => RssiLoop(token));
  }

  private void RssiLoop(System.Threading.CancellationToken token) {
    var sp = _session;
    if (sp == null) {
      return;
    }
    var tickStart = Environment.TickCount;
    try {
      while (!token.IsCancellationRequested && sp.IsOpen) {
        try {
          sp.WriteLine("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
          if (sp.BytesToRead < 50) {
            System.Threading.Thread.Sleep(150);
            continue;
          }
          var line = ReadLine(sp);
          var match = _rssiLine.Match(line);
          if (match.Success) {
            var t = (Environment.TickCount - tickStart) / 1000.0;
            RssiSample?.Invoke(t,
                double.Parse(match.Groups[1].Value), double.Parse(match.Groups[2].Value),
                double.Parse(match.Groups[3].Value), double.Parse(match.Groups[4].Value));
          }
        } catch {
          System.Threading.Thread.Sleep(150);
        }
      }
    } catch (Exception ex) {
      AppendLog("RSSI loop error: " + ex.Message);
    }
  }

  [RelayCommand]
  private async Task StopRssi() {
    _rssiCts?.Cancel();
    Ui(() => { RssiRunning = false; Status = "Idle"; });
    await Task.Run(() => {
      System.Threading.Thread.Sleep(250);
      var sp = _session;
      if (sp == null) {
        return;
      }
      try {

        EnterCommandMode(sp);
        DoCommand(sp, "AT&T", false);
        DoCommand(sp, "ATO", false);
      } catch {
      }
      ClosePort(sp);
      _session = null;
    });
    OnIsBusyChanged(false);
    AppendLog("RSSI stream stopped.");
  }

  private async Task CloseSession() {
    _rssiCts?.Cancel();
    await Task.Run(() => {
      var sp = _session;
      if (sp != null) {
        try {
          DoCommand(sp, "ATO", false);
        } catch {
        }
        ClosePort(sp);
        _session = null;
      }
    });
    Ui(() => { TerminalOpen = false; RssiRunning = false; Status = "Idle"; });
    AppendLog("Session closed.");
  }

  private ICommsSerial? Connect(string port, int preferredBaud, out int usedBaud) {
    foreach (var baud in new[] { preferredBaud }.Concat(_candidateBauds).Distinct()) {
      ICommsSerial? sp = null;
      try {
        sp = OpenTransport(port, baud);
        AppendLog("Probing " + (UseMavlinkSerialControl ? "MAVLink TELEM1" : port)
            + " @ " + baud + "…");
        if (EnterCommandMode(sp)) {
          usedBaud = baud;
          return sp;
        }
      } catch (Exception ex) {
        AppendLog("Open " + baud + " failed: " + ex.Message);
      }
      ClosePort(sp);
    }
    usedBaud = 0;
    return null;
  }

  private ICommsSerial OpenTransport(string port, int baud) {
    ICommsSerial sp;
    if (UseMavlinkSerialControl) {
      if (!MavlinkSerialControlPort.TryCreate(
              MAVLink.SERIAL_CONTROL_DEV.TELEM1, baud, out var mavlinkPort, out string error)
          || mavlinkPort == null) {
        throw new InvalidOperationException(error);
      }
      sp = mavlinkPort;
    } else {
      sp = new RawSerialPort(port, baud);
    }
    sp.ReadTimeout = 1500;
    sp.WriteTimeout = 1500;
    sp.DtrEnable = false;
    sp.RtsEnable = false;
    sp.Open();
    return sp;
  }

  private static void ClosePort(ICommsSerial? sp) {
    try {
      if (sp != null) {
        if (sp.IsOpen) {
          sp.Close();
        }
        sp.Dispose();
      }
    } catch {

    }
  }

  private bool EnterCommandMode(ICommsSerial sp) {
    if (ProbeAt(sp)) {
      return true;
    }

    for (var t = 0; t < 3; t++) {
      try {
        sp.DiscardInBuffer();

        Thread.Sleep(1200);
        sp.Write("+++");
        Thread.Sleep(1200);
        var resp = sp.ReadExisting();
        if (resp.Contains("OK") || ProbeAt(sp)) {
          return true;
        }
      } catch {

      }
    }
    return false;
  }

  private bool ProbeAt(ICommsSerial sp) {
    var v = DoCommand(sp, "ATI").Trim();
    return _sikBanner.IsMatch(v) || v.Contains(" on ");
  }

  private string DoCommand(ICommsSerial sp, string cmd, bool multiLine = false) {
    if (!sp.IsOpen) {
      return "";
    }

    try {
      sp.DiscardInBuffer();
      AppendLog(">> " + cmd);
      sp.Write("\r\n");
      ReadLine(sp);
      Thread.Sleep(50);
      sp.Write(cmd + "\r\n");

      var echo = ReadLine(sp);
      if (!echo.Contains(cmd)) {
        sp.DiscardInBuffer();
        sp.Write(cmd + "\r\n");
        echo = ReadLine(sp);
        if (!echo.Contains(cmd)) {
          return "";
        }
      }

      string value;
      if (multiLine) {
        var sb = new StringBuilder();
        var deadline = DateTime.Now.AddMilliseconds(1000);
        while (sp.BytesToRead > 0 || DateTime.Now < deadline) {
          var line = ReadLine(sp);
          if (line.Length > 0) {
            sb.Append(line).Append('\n');
          }
        }
        value = sb.ToString();
      } else {
        value = ReadLine(sp);
      }

      var trimmed = value.Trim();
      if (trimmed.Length > 0) {
        AppendLog("<< " + trimmed.Replace("\n", "\n   "));
      }
      return value;
    } catch (Exception ex) {
      AppendLog("cmd '" + cmd + "' error: " + ex.Message);
      return "";
    }
  }

  private string QuerySettingMetadata(ICommsSerial sp, bool remote) {
    var result = new StringBuilder();
    string prefix = remote ? "RTI10:" : "ATI10:";
    for (int index = 0; index < 256; index++) {
      string line = DoCommand(sp, prefix + index).Trim();
      if (line.Length == 0 || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) {
        result.Clear();
        break;
      }
      if (line.Contains("EOF", StringComparison.OrdinalIgnoreCase) || !line.Contains('=')) {
        break;
      }
      result.AppendLine(line);
    }

    if (result.Length == 0) {
      result.Append(DoCommand(sp, remote ? "RTI5?" : "ATI5?", true));
    }
    return result.ToString();
  }

  private void ApplySettingMetadata(string block, bool remote) {
    IReadOnlyDictionary<string, SikRadioSettingMetadata> parsed =
        SikRadioSettingsService.ParseMetadata(block);
    Ui(() => {
      foreach (SikRadioSettingMetadata metadata in parsed.Values) {
        SikRegister? setting = Registers.FirstOrDefault(item =>
            item.Name.Equals(metadata.Name, StringComparison.OrdinalIgnoreCase));
        if (setting == null) {
          int.TryParse(Regex.Match(metadata.Designator, @"\d+").Value, out int number);
          setting = new SikRegister(number, metadata.Name, metadata.Name);
          Registers.Add(setting);
        }
        if (metadata.AllowedValues.Count > 0) {
          setting.SetOptions(metadata.AllowedValues);
        }
        setting.Minimum = metadata.Minimum;
        setting.Maximum = metadata.Maximum;
        setting.EnsureOption(metadata.Value);
        if (remote) {
          setting.RemoteValue = metadata.Value;
          setting.OrigRemote = metadata.Value;
          setting.HasRemote = true;
        } else {
          setting.LocalValue = metadata.Value;
          setting.OrigLocal = metadata.Value;
        }
      }
    });
  }

  private static string StripMultipointPrefix(string value) {
    value = value.Trim();
    if (value.StartsWith('[') && value.IndexOf(']') is int end && end >= 0) {
      return value[(end + 1)..].Trim();
    }
    return value;
  }

  private string? ValidatePendingSettings() {
    foreach (SikRegister setting in Registers.Where(item => item.Num != 0)) {
      foreach ((string side, string value, string original, bool present) in new[] {
          ("Local", setting.LocalValue, setting.OrigLocal, true),
          ("Remote", setting.RemoteValue, setting.OrigRemote, setting.HasRemote),
      }) {
        if (!present || value == original) {
          continue;
        }
        if (!int.TryParse(value, out int numeric)) {
          return $"{side} {setting.Name} must be an integer; '{value}' is invalid.";
        }
        if (setting.Minimum is int minimum && numeric < minimum) {
          return $"{side} {setting.Name}={numeric} is below the firmware minimum {minimum}.";
        }
        if (setting.Maximum is int maximum && numeric > maximum) {
          return $"{side} {setting.Name}={numeric} is above the firmware maximum {maximum}.";
        }
      }
    }

    foreach ((string side, bool remote) in new[] { ("Local", false), ("Remote", true) }) {
      SikRegister? minSetting = Registers.FirstOrDefault(item => item.Name == "MIN_FREQ");
      SikRegister? maxSetting = Registers.FirstOrDefault(item => item.Name == "MAX_FREQ");
      if (minSetting == null || maxSetting == null || (remote && !minSetting.HasRemote)) {
        continue;
      }
      string minText = remote ? minSetting.RemoteValue : minSetting.LocalValue;
      string maxText = remote ? maxSetting.RemoteValue : maxSetting.LocalValue;
      if (int.TryParse(minText, out int minimum) && int.TryParse(maxText, out int maximum)
          && minimum > maximum) {
        return $"{side} MIN_FREQ ({minimum}) cannot be greater than MAX_FREQ ({maximum}).";
      }
    }
    return null;
  }

  private static string ReadLine(ICommsSerial sp) {
    try {
      return sp.ReadLine();
    } catch {
      return "";
    }
  }

  private static string[]? DefaultOptions(string name, bool rfd) {
    static string[] Range(int from, int step, int to) {
      var list = new List<string>();
      for (var v = from; v <= to; v += step) {
        list.Add(v.ToString());
      }
      return list.ToArray();
    }

    switch (name) {
      case "SERIAL_SPEED":
        return rfd
            ? new[] { "1", "2", "4", "9", "19", "38", "57", "115", "230", "460" }
            : new[] { "1", "2", "4", "9", "19", "38", "57", "115", "230" };
      case "AIR_SPEED":
        return rfd
            ? new[] { "4", "64", "125", "250", "500" }
            : new[] { "2", "4", "8", "16", "19", "24", "32", "48", "64", "96", "125", "128", "192", "250" };
      case "TXPOWER":
        return rfd ? Range(0, 1, 30) : new[] { "1", "2", "5", "8", "11", "14", "17", "20" };
      case "ECC":
      case "OPPRESEND":
      case "MANCHESTER":
      case "RTSCTS":
        return new[] { "0", "1" };
      case "MAVLINK":
        return new[] { "0", "1", "2" };
      case "DUTY_CYCLE":
        return Range(10, 10, 100);
      case "LBT_RSSI":
        return rfd ? Range(0, 25, 220) : new[] { "0" };
      default:
        return null;
    }
  }

  private void ApplyFirmwareOptions(bool rfd) {
    Ui(() => {
      foreach (var r in Registers) {
        var opts = DefaultOptions(r.Name, rfd);
        if (opts != null) {
          r.SetOptions(opts);
          r.EnsureOption(r.LocalValue);
        }
      }
    });
  }

  private void ParseInto(string block, bool remote) {
    foreach (var raw in block.Split('\n')) {
      var m = _regLine.Match(raw);
      if (!m.Success) {
        continue;
      }

      var num = int.TryParse(m.Groups[1].Value, out var parsedNum) ? parsedNum : -1;
      var name = m.Groups[2].Value.Trim();
      var val = m.Groups[3].Value.Trim();
      var reg = Registers.FirstOrDefault(r => r.Name == name);

      if (reg == null) {
        if (remote) {
          continue;
        }
        var added = new SikRegister(num, name, name);
        var opts = DefaultOptions(name, rfd: false);
        if (opts != null) {
          added.SetOptions(opts);
        }
        Ui(() => Registers.Add(added));
        reg = added;
      }

      Ui(() => {
        if (remote) {
          reg.RemoteValue = val;
          reg.OrigRemote = val;
          reg.HasRemote = true;
          reg.EnsureOption(val);
        } else {
          reg.Num = num >= 0 ? num : reg.Num;
          reg.LocalValue = val;
          reg.OrigLocal = val;
          reg.EnsureOption(val);
        }
      });
    }
  }

  private void ResetOrig(bool remote) {
    Ui(() => {
      foreach (var r in Registers) {
        if (remote) {
          r.RemoteValue = "";
          r.OrigRemote = "";
          r.HasRemote = false;
        } else {
          r.LocalValue = "";
          r.OrigLocal = "";
        }
      }
    });
  }

  private void SetStatus(string s) => Ui(() => Status = s);

  private void Ui(Action a) {
    if (Dispatcher.UIThread.CheckAccess()) {
      a();
    } else {
      Dispatcher.UIThread.Post(a);
    }
  }

  private void AppendLog(string line) {
    void Do() => Log += (Log.Length > 0 ? "\n" : "") + line;
    if (Dispatcher.UIThread.CheckAccess()) {
      Do();
    } else {
      Dispatcher.UIThread.Post(Do);
    }
  }
}
