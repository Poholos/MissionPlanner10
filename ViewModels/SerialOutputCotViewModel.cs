using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlanner.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SerialPort = MissionPlanner.Comms.SerialPort;

namespace MissionPlanner.ViewModels;

public partial class SerialOutputCotViewModel : ViewModelBase, IDisposable {
  public const string TakMulticast = "TAK Multicast";
  public const string UdpClient = "UDP Client";
  public const string UdpHost = "UDP Host";
  public const string TcpClient = "TCP Client";
  public const string TcpHost = "TCP Host";

  private readonly object _clientsSync = new();
  private readonly List<System.Net.Sockets.TcpClient> _clients = new();
  private readonly HashSet<CotIdentityRow> _subscribedIdentityRows = [];
  private CancellationTokenSource? _runCts;
  private Task? _outputTask;
  private Task? _endpointTask;
  private System.Net.Sockets.UdpClient? _udp;
  private IPEndPoint? _udpDestination;
  private TcpListener? _listener;
  private System.Net.Sockets.TcpClient? _tcpClient;
  private SerialPort? _serial;
  private CotIdentitySnapshot[] _identitySnapshot = [];
  private bool _identitiesDirty;
  private int _disposed;

  public SerialOutputCotViewModel() {
    Host = "239.2.3.1";
    Port = 6969;
    Baud = 57600;
    UpdateSeconds = 10;
    EventType = "a-f-A-M-F-Q";
    UidPrefix = "MissionPlanner";

    RefreshEndpoints();
    var settings = Settings.Instance;
    var savedEndpoint = settings["CoT_AvaloniaEndpoint"];
    SelectedEndpoint = savedEndpoint != null && Endpoints.Contains(savedEndpoint)
        ? savedEndpoint
        : TakMulticast;
    Host = settings["CoT_AvaloniaHost", Host];
    Port = settings.GetInt32("CoT_AvaloniaPort", Port);
    Baud = settings.GetInt32("CoT_AvaloniaBaud", Baud);
    UpdateSeconds = settings.GetDouble("CoT_AvaloniaUpdateSeconds", UpdateSeconds);
    EventType = settings["CoT_AvaloniaEventType", EventType];
    UidPrefix = settings["CoT_AvaloniaUidPrefix", UidPrefix];
    Callsign = settings["CoT_AvaloniaCallsign", Callsign];
    IndentXml = settings.GetBoolean("CoT_AvaloniaIndentXml", IndentXml);
    AdvancedMode = settings.GetBoolean("CoT_CB_advancedMode", true);
    foreach (var row in ParseIdentityRows(settings["CoTUID"])) {
      Identities.Add(row);
    }
    Identities.CollectionChanged += OnIdentitiesChanged;
    foreach (var row in Identities) {
      SubscribeIdentity(row);
    }
    RebuildIdentitySnapshot();
  }

  public ObservableCollection<string> Endpoints { get; } = new();
  public ObservableCollection<int> Bauds { get; } = new() { 4800, 9600, 19200, 38400, 57600, 115200 };
  public ObservableCollection<CotIdentityRow> Identities { get; } = [];

  [ObservableProperty] private string? _selectedEndpoint;
  [ObservableProperty] private string _host = "";
  [ObservableProperty] private int _port;
  [ObservableProperty] private int _baud;
  [ObservableProperty] private double _updateSeconds;
  [ObservableProperty] private string _eventType = "";
  [ObservableProperty] private string _uidPrefix = "";
  [ObservableProperty] private string _callsign = "";
  [ObservableProperty] private bool _indentXml;
  [ObservableProperty] private bool _advancedMode = true;
  [ObservableProperty] private CotIdentityRow? _selectedIdentity;
  [ObservableProperty] private string _status = "Stopped.";
  [ObservableProperty] private string _connectButtonText = "Connect";
  [ObservableProperty] private string _lastEvent = "";

  public bool IsRunning => Volatile.Read(ref _runCts) != null;
  public bool IsNetworkEndpoint => SelectedEndpoint is TakMulticast or UdpClient or UdpHost or TcpClient or TcpHost;
  public bool IsSerialEndpoint => !string.IsNullOrWhiteSpace(SelectedEndpoint) && !IsNetworkEndpoint;

  partial void OnSelectedEndpointChanged(string? value) {
    switch (value) {
      case TakMulticast:
        Host = "239.2.3.1";
        Port = 6969;
        break;
      case UdpHost:
      case TcpHost:
        Host = "0.0.0.0";
        Port = 14551;
        break;
      case UdpClient:
      case TcpClient:
        if (Host is "" or "0.0.0.0" or "239.2.3.1") {
          Host = "127.0.0.1";
        }
        Port = 14551;
        break;
    }
    OnPropertyChanged(nameof(IsNetworkEndpoint));
    OnPropertyChanged(nameof(IsSerialEndpoint));
  }

  [RelayCommand]
  private void RefreshEndpoints() {
    var selected = SelectedEndpoint;
    Endpoints.Clear();
    Endpoints.Add(TakMulticast);
    Endpoints.Add(UdpClient);
    Endpoints.Add(UdpHost);
    Endpoints.Add(TcpClient);
    Endpoints.Add(TcpHost);
    foreach (var port in SerialPort.GetPortNames().Distinct()) {
      Endpoints.Add(port);
    }
    SelectedEndpoint = selected != null && Endpoints.Contains(selected)
        ? selected
        : Endpoints.FirstOrDefault();
  }

  [RelayCommand]
  private void RefreshIdentitySystems() {
    try {
      int added = 0;
      foreach (byte systemId in AppState.comPort.MAVlist.Select(mav => mav.sysid).Distinct().Order()) {
        string systemIdText = systemId.ToString(CultureInfo.InvariantCulture);
        if (Volatile.Read(ref _identitySnapshot).Any(row => row.SystemId == systemIdText)) {
          continue;
        }
        Identities.Add(new CotIdentityRow {
          SystemId = systemIdText,
          Uid = $"{UidPrefix}-{systemId}",
        });
        added++;
      }
      Status = added == 0
          ? "No new MAVLink systems found. Identity rows are preserved for offline systems."
          : $"Added {added} MAVLink system identity row(s).";
    } catch (Exception ex) {
      Status = "Unable to refresh CoT systems: " + ex.Message;
    }
  }

  [RelayCommand]
  private void AddIdentity() {
    int systemId = Enumerable.Range(1, 255).FirstOrDefault(candidate =>
        !Volatile.Read(ref _identitySnapshot).Any(row =>
            row.SystemId == candidate.ToString(CultureInfo.InvariantCulture)));
    if (systemId == 0) {
      Status = "All MAVLink system IDs already have identity rows.";
      return;
    }
    var row = new CotIdentityRow {
      SystemId = systemId.ToString(CultureInfo.InvariantCulture),
      Uid = $"{UidPrefix}-{systemId}",
    };
    Identities.Add(row);
    SelectedIdentity = row;
  }

  [RelayCommand]
  private void RemoveIdentity() {
    if (SelectedIdentity == null) {
      return;
    }
    Identities.Remove(SelectedIdentity);
    SelectedIdentity = null;
  }

  [RelayCommand]
  private async Task ToggleConnect() {
    if (IsRunning) {
      await StopAsync();
      return;
    }
    if (Volatile.Read(ref _disposed) != 0) {
      return;
    }
    if (string.IsNullOrWhiteSpace(SelectedEndpoint)) {
      Status = "Select an output endpoint.";
      return;
    }
    if (UpdateSeconds is < 0.1 or > 3600) {
      Status = "Update interval must be between 0.1 and 3600 seconds.";
      return;
    }

    var cts = new CancellationTokenSource();
    if (Interlocked.CompareExchange(ref _runCts, cts, null) != null) {
      cts.Dispose();
      return;
    }
    OnPropertyChanged(nameof(IsRunning));
    try {
      await OpenEndpointAsync(SelectedEndpoint, cts.Token);
      if (!IsCurrentRun(cts)) {
        return;
      }
      _outputTask = OutputLoopAsync(cts);
      ConnectButtonText = "Stop";
      var runningStatus = SelectedEndpoint == UdpHost
          ? $"Listening for a UDP peer on port {Port}."
          : $"Emitting Cursor-on-Target events through {SelectedEndpoint}.";
      try {
        SaveSettings();
        Status = runningStatus;
      } catch (Exception ex) {
        Status = runningStatus + " Settings could not be saved: " + ex.Message;
      }
    } catch (Exception ex) {
      bool ownsRun = ReferenceEquals(
          Interlocked.CompareExchange(ref _runCts, null, cts), cts);
      if (ownsRun) {
        try {
          cts.Cancel();
        } catch (ObjectDisposedException) {
        }
        CloseEndpoint();
        await ObserveTasksAsync(_outputTask, _endpointTask);
        _outputTask = null;
        _endpointTask = null;
        cts.Dispose();
        OnPropertyChanged(nameof(IsRunning));
      }
      if (Volatile.Read(ref _disposed) == 0 && ex is not OperationCanceledException) {
        Status = "Unable to start CoT output: " + ex.Message;
      }
    }
  }

  private void SaveSettings() {
    var settings = Settings.Instance;
    settings["CoT_AvaloniaEndpoint"] = SelectedEndpoint ?? "";
    settings["CoT_AvaloniaHost"] = Host;
    settings["CoT_AvaloniaPort"] = Port.ToString();
    settings["CoT_AvaloniaBaud"] = Baud.ToString();
    settings["CoT_AvaloniaUpdateSeconds"] = UpdateSeconds.ToString();
    settings["CoT_AvaloniaEventType"] = EventType;
    settings["CoT_AvaloniaUidPrefix"] = UidPrefix;
    settings["CoT_AvaloniaCallsign"] = Callsign;
    settings["CoT_AvaloniaIndentXml"] = IndentXml.ToString();
    settings["CoT_CB_advancedMode"] = AdvancedMode.ToString();
    if (_identitiesDirty) {
      settings["CoTUID"] = SerializeIdentityRows(Identities);
    }
    settings.Save();
    _identitiesDirty = false;
  }

  private async Task OpenEndpointAsync(string endpoint, CancellationToken token) {
    switch (endpoint) {
      case TakMulticast:
      case UdpClient: {
          var address = await ResolveAddressAsync(Host, token);
          token.ThrowIfCancellationRequested();
          _udpDestination = new IPEndPoint(address, Port);
          _udp = new System.Net.Sockets.UdpClient(address.AddressFamily);
          break;
        }
      case UdpHost:
        _udp = UdpSerial.CreateSharedListener(Port);
        _endpointTask = LearnUdpPeerAsync(_udp, _runCts!, token);
        break;
      case TcpClient:
        _tcpClient = new System.Net.Sockets.TcpClient();
        await _tcpClient.ConnectAsync(Host, Port, token).ConfigureAwait(false);
        break;
      case TcpHost:
        _listener = new TcpListener(ParseListenAddress(Host), Port);
        _listener.Start();
        _endpointTask = AcceptClientsAsync(_listener, _runCts!, token);
        break;
      default:
        _serial = new SerialPort { PortName = endpoint, BaudRate = Baud };
        _serial.Open();
        break;
    }
  }

  private static async Task<IPAddress> ResolveAddressAsync(
      string host, CancellationToken cancellationToken) {
    if (IPAddress.TryParse(host, out var address)) {
      return address;
    }
    var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken)
        .ConfigureAwait(false);
    return addresses.First(address => address.AddressFamily is AddressFamily.InterNetwork
        or AddressFamily.InterNetworkV6);
  }

  private static IPAddress ParseListenAddress(string host) =>
      string.IsNullOrWhiteSpace(host) || host == "0.0.0.0"
          ? IPAddress.Any
          : IPAddress.Parse(host);

  private async Task LearnUdpPeerAsync(
      System.Net.Sockets.UdpClient udp,
      CancellationTokenSource run,
      CancellationToken token) {
    try {
      while (!token.IsCancellationRequested) {
        var received = await udp.ReceiveAsync(token).ConfigureAwait(false);
        if (!IsCurrentRun(run)) {
          return;
        }
        _udpDestination = received.RemoteEndPoint;
        PostForRun(run, () => Status =
            $"Emitting Cursor-on-Target events to UDP peer {received.RemoteEndPoint}.");
      }
    } catch (OperationCanceledException) {
    } catch (ObjectDisposedException) {
    } catch (Exception ex) {
      PostForRun(run, () => Status = "UDP host error: " + ex.Message);
    }
  }

  private async Task AcceptClientsAsync(
      TcpListener listener,
      CancellationTokenSource run,
      CancellationToken token) {
    try {
      while (!token.IsCancellationRequested) {
        var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
        if (!IsCurrentRun(run)) {
          client.Dispose();
          return;
        }
        lock (_clientsSync) {
          if (!IsCurrentRun(run)) {
            client.Dispose();
            return;
          }
          _clients.Add(client);
        }
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "client";
        PostForRun(run, () => Status = $"CoT TCP client connected: {remote}.");
      }
    } catch (OperationCanceledException) {
    } catch (ObjectDisposedException) {
    } catch (Exception ex) {
      PostForRun(run, () => Status = "TCP host error: " + ex.Message);
    }
  }

  private async Task OutputLoopAsync(CancellationTokenSource run) {
    CancellationToken token = run.Token;
    while (!token.IsCancellationRequested) {
      try {
        var identities = Volatile.Read(ref _identitySnapshot);
        var events = AppState.comPort.MAVlist.Select(mav => {
          string systemId = mav.sysid.ToString(CultureInfo.InvariantCulture);
          var identity = identities.FirstOrDefault(row => row.SystemId == systemId);
          string eventUid = ResolveEventUid(
              UidPrefix, mav.sysid, mav.compid, identity != null, identity?.Uid);
          return CotEventSerializer.Serialize(
              eventUid, EventType,
              mav.cs.lat, mav.cs.lng, mav.cs.altasl, mav.cs.groundcourse, mav.cs.groundspeed,
              string.IsNullOrWhiteSpace(Callsign) ? null : $"{Callsign}-{mav.sysid}",
              IndentXml,
              identity: AdvancedMode && eventUid.Length > 0 ? identity?.ToDetail() : null);
        }).ToArray();
        foreach (var xml in events) {
          await SendAsync(xml + "\n", token).ConfigureAwait(false);
        }
        string preview = string.Join(Environment.NewLine, events);
        PostForRun(run, () => LastEvent = preview);
        await Task.Delay(TimeSpan.FromSeconds(UpdateSeconds), token).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        PostForRun(run, () => Status = "CoT output warning: " + ex.Message);
        try {
          await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          break;
        }
      }
    }
  }

  private async Task SendAsync(string text, CancellationToken token) {
    var bytes = Encoding.UTF8.GetBytes(text);
    if (_udp != null && _udpDestination != null) {
      await _udp.SendAsync(bytes, _udpDestination, token).ConfigureAwait(false);
    }
    if (_tcpClient?.Connected == true) {
      await _tcpClient.GetStream().WriteAsync(bytes, token).ConfigureAwait(false);
    }
    List<System.Net.Sockets.TcpClient> clients;
    lock (_clientsSync) {
      clients = _clients.ToList();
    }
    foreach (var client in clients) {
      try {
        await client.GetStream().WriteAsync(bytes, token).ConfigureAwait(false);
      } catch {
        lock (_clientsSync) {
          _clients.Remove(client);
        }
        client.Dispose();
      }
    }
    if (_serial?.IsOpen == true) {
      _serial.Write(text);
    }
  }

  public async Task StopAsync() {
    await StopCoreAsync();
    if (Volatile.Read(ref _disposed) != 0) {
      return;
    }
    ConnectButtonText = "Connect";
    Status = "Stopped.";
    OnPropertyChanged(nameof(IsRunning));
  }

  private async Task StopCoreAsync() {
    var cts = Interlocked.Exchange(ref _runCts, null);
    Task? outputTask = _outputTask;
    Task? endpointTask = _endpointTask;
    _outputTask = null;
    _endpointTask = null;
    if (cts != null) {
      try {
        cts.Cancel();
      } catch (ObjectDisposedException) {
      }
    }
    CloseEndpoint();
    await ObserveTasksAsync(outputTask, endpointTask).ConfigureAwait(false);
    cts?.Dispose();
  }

  private static async Task ObserveTasksAsync(params Task?[] tasks) {
    Task[] pending = tasks.Where(task => task != null).Cast<Task>().ToArray();
    if (pending.Length == 0) {
      return;
    }
    try {
      await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    } catch (OperationCanceledException) {
    } catch (TimeoutException ex) {
      Trace.WriteLine($"CoT output cleanup timed out: {ex}");
    } catch (Exception ex) {
      Trace.WriteLine($"CoT output cleanup failed: {ex}");
    }
  }

  private bool IsCurrentRun(CancellationTokenSource run) =>
      Volatile.Read(ref _disposed) == 0
      && !run.IsCancellationRequested
      && ReferenceEquals(Volatile.Read(ref _runCts), run);

  private void PostForRun(CancellationTokenSource run, Action action) =>
      Dispatcher.UIThread.Post(() => {
        if (IsCurrentRun(run)) {
          action();
        }
      });

  private void CloseEndpoint() {
    try { _listener?.Stop(); } catch { }
    _listener = null;
    try { _udp?.Dispose(); } catch { }
    _udp = null;
    _udpDestination = null;
    try { _tcpClient?.Dispose(); } catch { }
    _tcpClient = null;
    try { _serial?.Close(); } catch { }
    _serial = null;
    lock (_clientsSync) {
      foreach (var client in _clients) {
        client.Dispose();
      }
      _clients.Clear();
    }
  }

  private void OnIdentitiesChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    if (e.Action == NotifyCollectionChangedAction.Reset) {
      foreach (var row in _subscribedIdentityRows.ToArray()) {
        UnsubscribeIdentity(row);
      }
      foreach (var row in Identities) {
        SubscribeIdentity(row);
      }
    }
    if (e.OldItems != null) {
      foreach (CotIdentityRow row in e.OldItems) {
        UnsubscribeIdentity(row);
      }
    }
    if (e.NewItems != null) {
      foreach (CotIdentityRow row in e.NewItems) {
        SubscribeIdentity(row);
      }
    }
    _identitiesDirty = true;
    RebuildIdentitySnapshot();
  }

  private void SubscribeIdentity(CotIdentityRow row) {
    if (_subscribedIdentityRows.Add(row)) {
      row.PropertyChanged += OnIdentityChanged;
    }
  }

  private void UnsubscribeIdentity(CotIdentityRow row) {
    if (_subscribedIdentityRows.Remove(row)) {
      row.PropertyChanged -= OnIdentityChanged;
    }
  }

  private void OnIdentityChanged(object? sender, PropertyChangedEventArgs e) {
    _identitiesDirty = true;
    RebuildIdentitySnapshot();
  }

  private void RebuildIdentitySnapshot() => Volatile.Write(ref _identitySnapshot,
      [.. Identities.Select(row => new CotIdentitySnapshot(
          row.SystemId, row.Uid, row.IncludeTakv, row.ContactCallsign,
          row.ContactEndpoint, row.Vmf))]);

  internal static string SerializeIdentityRows(IEnumerable<CotIdentityRow> rows) =>
      new JArray(rows.Select(row => new JArray(
          SystemIdToken(row.SystemId), TextToken(row.Uid), row.IncludeTakv,
          TextToken(row.ContactCallsign), TextToken(row.ContactEndpoint),
          TextToken(row.Vmf)))).ToString(Formatting.None);

  internal static string ResolveEventUid(
      string uidPrefix, byte systemId, byte componentId,
      bool hasIdentityRow, string? identityUid) =>
      hasIdentityRow
          ? identityUid ?? ""
          : $"{uidPrefix}-{systemId}-{componentId}";

  internal static IReadOnlyList<CotIdentityRow> ParseIdentityRows(string? json) {
    if (string.IsNullOrWhiteSpace(json)) {
      return [];
    }
    try {
      var result = new List<CotIdentityRow>();
      foreach (var token in JArray.Parse(json).OfType<JArray>()) {
        if (token.Count < 2) {
          continue;
        }
        result.Add(new CotIdentityRow {
          SystemId = TokenText(token[0]),
          Uid = TokenText(token[1]),
          IncludeTakv = ParseBoolean(token.Count > 2 ? token[2] : null),
          ContactCallsign = token.Count > 3 ? TokenText(token[3]) : null,
          ContactEndpoint = token.Count > 4 ? TokenText(token[4]) : null,
          Vmf = token.Count > 5 ? TokenText(token[5]) : null,
        });
      }
      return result;
    } catch (JsonException) {
      return [];
    }
  }

  private static bool ParseBoolean(JToken? token) =>
      token?.Type == JTokenType.Boolean
          ? token.Value<bool>()
          : bool.TryParse(token?.ToString(), out bool value) && value;

  private static string? TokenText(JToken? token) =>
      token == null || token.Type == JTokenType.Null ? null : token.ToString();

  private static JToken SystemIdToken(string? systemId) =>
      int.TryParse(systemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric)
          ? new JValue(numeric)
          : systemId == null ? JValue.CreateNull() : new JValue(systemId);

  private static JToken TextToken(string? value) =>
      value == null ? JValue.CreateNull() : new JValue(value);

  public void Dispose() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    try {
      SaveSettings();
    } catch {
      // Closing the tool must still release sockets/ports if settings storage is unavailable.
    }
    Identities.CollectionChanged -= OnIdentitiesChanged;
    foreach (var row in _subscribedIdentityRows.ToArray()) {
      UnsubscribeIdentity(row);
    }
    StopCoreAsync().GetAwaiter().GetResult();
  }

  private sealed record CotIdentitySnapshot(
      string? SystemId,
      string? Uid,
      bool IncludeTakv,
      string? ContactCallsign,
      string? ContactEndpoint,
      string? Vmf) {
    public CotIdentityDetail ToDetail() => new(
        IncludeTakv, ContactCallsign, ContactEndpoint, Vmf);
  }
}

public partial class CotIdentityRow : ObservableObject {
  [ObservableProperty] private string? _systemId;
  [ObservableProperty] private string? _uid;
  [ObservableProperty] private bool _includeTakv;
  [ObservableProperty] private string? _contactCallsign;
  [ObservableProperty] private string? _contactEndpoint;
  [ObservableProperty] private string? _vmf;
}
