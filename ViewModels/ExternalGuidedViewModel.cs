using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Services;
using MissionPlanner.Utilities;

namespace MissionPlanner.ViewModels;

/// <summary>
/// Native replacement for the official ExtGuided plugin. The original plugin reread a selected
/// lat,lon,alt file once per second and sent it through the global MAVLink connection. This version
/// preserves that wire behavior while binding the session to one exact link/system/component.
/// </summary>
public partial class ExternalGuidedViewModel : ViewModelBase, IDisposable {
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly Func<NmeaVehicleTarget?> _activeTarget;
  private readonly Action<NmeaVehicleTarget, Locationwp, bool> _sendGuided;
  private readonly Func<NmeaVehicleTarget, string, Task<bool>> _confirmStart;
  private readonly Func<string, CancellationToken, Task<string>> _readFile;
  private readonly bool _subscribedToAppState;
  private CancellationTokenSource? _cts;
  private Task? _senderTask;
  private NmeaVehicleTarget? _boundTarget;
  private volatile bool _targetInvalidated;
  private int _stopScheduled;
  private bool _guidedSet;
  private bool _disposed;

  public ExternalGuidedViewModel()
      : this(
          () => NmeaVehicleSession.CaptureActive(requireOpen: true),
          static (target, waypoint, setGuided) => target.Link.setGuidedModeWP(
              target.SystemId, target.ComponentId, waypoint, setGuided),
          static (target, path) => Dialogs.ConfirmDangerous(
              "Start External Guided",
              $"External Guided will reread '{path}' once per second and repeatedly command "
              + $"{NmeaVehicleSession.Describe(target)} to its lat,lon,alt target. The altitude "
              + "is a relative GUIDED altitude in metres. Verify the file writer, selected modem, "
              + "flight mode and surrounding airspace before continuing.",
              "Start External Guided"),
          ReadWaypointFileAsync,
          subscribeToAppState: true) {
  }

  internal ExternalGuidedViewModel(
      Func<NmeaVehicleTarget?> activeTarget,
      Action<NmeaVehicleTarget, Locationwp, bool> sendGuided,
      Func<NmeaVehicleTarget, string, Task<bool>> confirmStart,
      Func<string, CancellationToken, Task<string>> readFile,
      bool subscribeToAppState = false) {
    _activeTarget = activeTarget;
    _sendGuided = sendGuided;
    _confirmStart = confirmStart;
    _readFile = readFile;
    _subscribedToAppState = subscribeToAppState;
    RefreshTargetDescription();
    if (_subscribedToAppState) {
      AppState.ConnectionChanged += OnConnectionChanged;
    }
  }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditSettings))]
  private bool _busy;

  [ObservableProperty]
  private string _filePath = "";

  [ObservableProperty]
  private string _status = "Stopped.";

  [ObservableProperty]
  private string _locationLabel = "No file target read.";

  [ObservableProperty]
  private string _targetDescription = "No connected vehicle selected.";

  [ObservableProperty]
  private string _connectButtonText = "Start";

  public bool IsRunning => _cts != null;
  public bool CanEditSettings => !Busy && !IsRunning;

  [RelayCommand]
  private async Task BrowseAsync() {
    var owner = Dialogs.Owner;
    if (owner?.StorageProvider == null) {
      Status = "No application window is available for selecting a target file.";
      return;
    }
    var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select External Guided target file",
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType("GUIDED target") {
        Patterns = ["*.txt", "*.csv", "*"],
      }],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (!string.IsNullOrWhiteSpace(path)) {
      FilePath = path;
      Status = "Target file selected. Press Start after verifying the active vehicle.";
    }
  }

  [RelayCommand]
  private async Task ToggleAsync() {
    await _lifecycleGate.WaitAsync();
    Busy = true;
    try {
      if (_cts != null) {
        await StopCoreAsync("Stopped.");
      } else {
        await StartCoreAsync();
      }
    } finally {
      Busy = false;
      _lifecycleGate.Release();
    }
  }

  private async Task StartCoreAsync() {
    if (_disposed) {
      return;
    }
    string path;
    try {
      path = Path.GetFullPath(FilePath.Trim());
    } catch (Exception ex) {
      Status = "Select a valid External Guided target file: " + ex.Message;
      return;
    }
    if (!File.Exists(path)) {
      Status = "Select an existing External Guided target file first.";
      return;
    }

    ExternalGuidedWaypoint initial;
    try {
      string text = await _readFile(path, CancellationToken.None);
      if (!TryParseWaypoint(text, out initial, out string parseError)) {
        Status = "Target file is invalid: " + parseError;
        return;
      }
    } catch (Exception ex) {
      Status = "Target file cannot be read: " + ex.Message;
      return;
    }

    NmeaVehicleTarget? target = _activeTarget();
    if (target == null) {
      Status = "Connect and select a vehicle before starting External Guided.";
      RefreshTargetDescription();
      return;
    }
    if (!await _confirmStart(target, path)) {
      Status = "External Guided start cancelled.";
      return;
    }
    if (!IsTargetCurrent(target)) {
      Status = TargetChangedMessage;
      RefreshTargetDescription();
      return;
    }

    FilePath = path;
    _boundTarget = target;
    _targetInvalidated = false;
    _guidedSet = false;
    var cts = new CancellationTokenSource();
    _cts = cts;
    _senderTask = Task.Run(() => SendLoop(target, path, initial, cts.Token), cts.Token);
    TargetDescription = "Bound to " + NmeaVehicleSession.Describe(target) + ".";
    ConnectButtonText = "Stop";
    Status = "External Guided is watching the target file.";
    NotifyRunningState();
  }

  private async Task SendLoop(
      NmeaVehicleTarget target,
      string path,
      ExternalGuidedWaypoint initial,
      CancellationToken cancellationToken) {
    ExternalGuidedWaypoint? pending = initial;
    try {
      while (true) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTargetCurrent(target)) {
          InvalidateTarget();
          return;
        }

        ExternalGuidedWaypoint waypoint;
        if (pending is { } first) {
          waypoint = first;
          pending = null;
        } else {
          try {
            string text = await _readFile(path, cancellationToken).ConfigureAwait(false);
            if (!TryParseWaypoint(text, out waypoint, out string error)) {
              PostStatus("Target file is invalid; GUIDED update withheld: " + error);
              await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false);
              continue;
            }
          } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return;
          } catch (Exception ex) {
            PostStatus("Target file cannot be read; GUIDED update withheld: " + ex.Message);
            await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false);
            continue;
          }
        }

        if (!IsTargetCurrent(target)) {
          InvalidateTarget();
          return;
        }
        if (target.Link.giveComport) {
          PostStatus("MAVLink link is busy; GUIDED update withheld.");
        } else {
          var location = new Locationwp {
            id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
            frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
            lat = waypoint.Latitude,
            lng = waypoint.Longitude,
            alt = (float)waypoint.RelativeAltitudeM,
          };
          _sendGuided(target, location, !_guidedSet);
          _guidedSet = true;
          PostLocation(waypoint);
          PostStatus("External Guided target sent to " + NmeaVehicleSession.Describe(target) + ".");
        }

        await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      RequestStop("External Guided command stream stopped: " + ex.Message);
    }
  }

  internal static bool TryParseWaypoint(
      string? text, out ExternalGuidedWaypoint waypoint, out string error) {
    waypoint = default;
    string[] fields = (text ?? "").Trim().Split(',', StringSplitOptions.TrimEntries);
    if (fields.Length != 3
        || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture,
            out double latitude)
        || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture,
            out double longitude)
        || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture,
            out double altitude)) {
      error = "expected exactly three invariant numbers: latitude,longitude,relative-altitude-m.";
      return false;
    }
    if (!double.IsFinite(latitude) || latitude is < -90 or > 90) {
      error = "latitude must be between -90 and 90 degrees.";
      return false;
    }
    if (!double.IsFinite(longitude) || longitude is < -180 or > 180) {
      error = "longitude must be between -180 and 180 degrees.";
      return false;
    }
    if (!double.IsFinite(altitude) || altitude <= 0 || altitude > 10000) {
      error = "relative altitude must be greater than zero and no more than 10000 metres.";
      return false;
    }
    waypoint = new ExternalGuidedWaypoint(latitude, longitude, altitude);
    error = "";
    return true;
  }

  internal static async Task<string> ReadWaypointFileAsync(
      string path, CancellationToken cancellationToken) {
    using var stream = new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    if (stream.Length > MaximumFileBytes) {
      throw new InvalidDataException($"target file exceeds {MaximumFileBytes} bytes.");
    }
    using var reader = new StreamReader(stream);
    string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    if (text.Length > MaximumFileBytes) {
      throw new InvalidDataException($"target file exceeds {MaximumFileBytes} characters.");
    }
    return text;
  }

  private void OnConnectionChanged() {
    if (_disposed) {
      return;
    }
    NmeaVehicleTarget? bound = _boundTarget;
    if (bound == null) {
      Dispatcher.UIThread.Post(RefreshTargetDescription);
    } else if (!IsTargetCurrent(bound)) {
      InvalidateTarget();
    }
  }

  private bool IsTargetCurrent(NmeaVehicleTarget target) =>
      NmeaVehicleSession.ShouldContinue(
          _targetInvalidated, target, _activeTarget(), requireOpen: true);

  internal void SynchronizeActiveTarget() => OnConnectionChanged();

  private void InvalidateTarget() {
    _targetInvalidated = true;
    _cts?.Cancel();
    RequestStop(TargetChangedMessage);
  }

  private void RequestStop(string reason) {
    if (_disposed || Interlocked.Exchange(ref _stopScheduled, 1) != 0) {
      return;
    }
    Dispatcher.UIThread.Post(() => _ = StopForReasonAsync(reason));
  }

  private async Task StopForReasonAsync(string reason) {
    await _lifecycleGate.WaitAsync();
    try {
      if (_cts != null || _boundTarget != null) {
        await StopCoreAsync(reason);
      } else if (!_disposed) {
        Status = reason;
      }
    } finally {
      _lifecycleGate.Release();
      Interlocked.Exchange(ref _stopScheduled, 0);
    }
  }

  private async Task StopCoreAsync(string reason) {
    CancellationTokenSource? cts = _cts;
    Task? sender = _senderTask;
    _cts = null;
    _senderTask = null;
    cts?.Cancel();
    if (sender != null) {
      try {
        await sender.WaitAsync(TimeSpan.FromSeconds(2));
      } catch (OperationCanceledException) {
      } catch (TimeoutException) {
      } catch {
      }
    }
    cts?.Dispose();
    _boundTarget = null;
    _targetInvalidated = false;
    _guidedSet = false;
    ConnectButtonText = "Start";
    Status = reason;
    NotifyRunningState();
    RefreshTargetDescription();
  }

  public async Task StopAsync() {
    await _lifecycleGate.WaitAsync();
    try {
      if (_cts != null || _boundTarget != null) {
        await StopCoreAsync("Stopped.");
      }
    } finally {
      _lifecycleGate.Release();
    }
  }

  private void RefreshTargetDescription() {
    if (_disposed || _boundTarget != null) {
      return;
    }
    NmeaVehicleTarget? target = _activeTarget();
    TargetDescription = target == null
        ? "No connected vehicle selected."
        : "Ready for " + NmeaVehicleSession.Describe(target) + ".";
  }

  private void PostStatus(string status) => Dispatcher.UIThread.Post(() => {
    if (!_disposed && _cts != null) {
      Status = status;
    }
  });

  private void PostLocation(ExternalGuidedWaypoint waypoint) => Dispatcher.UIThread.Post(() => {
    if (!_disposed && _cts != null) {
      LocationLabel = $"{waypoint.Latitude:0.0000000}, {waypoint.Longitude:0.0000000}, "
          + $"{waypoint.RelativeAltitudeM:0.##} m relative";
    }
  });

  private void NotifyRunningState() {
    OnPropertyChanged(nameof(IsRunning));
    OnPropertyChanged(nameof(CanEditSettings));
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    if (_subscribedToAppState) {
      AppState.ConnectionChanged -= OnConnectionChanged;
    }
    _targetInvalidated = true;
    CancellationTokenSource? cts = _cts;
    _cts = null;
    _senderTask = null;
    _boundTarget = null;
    cts?.Cancel();
    cts?.Dispose();
  }

  internal static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);
  internal const int MaximumFileBytes = 4096;
  private const string TargetChangedMessage =
      "The active modem or vehicle changed or disconnected. External Guided was stopped; start "
      + "it again only after verifying the selected target.";
}

internal readonly record struct ExternalGuidedWaypoint(
    double Latitude, double Longitude, double RelativeAltitudeM);
