using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MissionPlanner.Services;
using MissionPlanner.Services.Mcp;
using MissionPlanner.Utilities;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Views;

internal sealed class AgentToolsWindow : Window {
  private readonly MainWindowViewModel _main;
  private readonly McpUiHost _ui;
  private readonly Func<CancellationToken, Task<McpAgent[]>> _discover;
  private MissionPlannerMcpServer? _server;
  private MissionPlannerMcpServer? _desktopServer;
  private readonly McpVehicleAccess _vehicles = new(() => AppState.Connections.Snapshot());
  private readonly McpLogCatalog _logs = new();
  private readonly CancellationTokenSource _windowStop = new();
  private readonly ComboBox _detectedAgents = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
  private readonly ComboBox _agentKind = new() { ItemsSource = new[] { "Codex CLI", "Claude Code" }, SelectedIndex = 0 };
  private readonly ListBox _sessions = new() { MinHeight = 100 };
  private readonly TextBlock _registration = new() { TextWrapping = TextWrapping.Wrap };
  private readonly List<McpTerminalLaunch> _terminalLaunches = new();
  private CancellationTokenSource _localOperationsStop = new();
  private int _accessGeneration;
  private sealed record SessionRow(MissionPlannerMcpServer Server, McpConnectionInfo Info) {
    public override string ToString() => Info.ToString();
  }
  private readonly NumericUpDown _desktopPort = new() { Minimum = 1024, Maximum = 65535, Increment = 1,
    Value = McpDesktopRegistration.DefaultPort, FormatString = "0", Width = 150 };
  private readonly TextBlock _desktopState = new() { TextWrapping = TextWrapping.Wrap, Text = "Desktop MCP stopped." };
  private readonly TextBox _desktopEndpoint = new() { IsReadOnly = true };
  private readonly TabItem _desktopTab = new() { Header = "Connections" };
  private bool _closingStarted;
  private readonly TabControl _tabs = new();
  private readonly TextBox _workingDirectory = new() { Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
  private readonly TextBox _endpoint = new() { IsReadOnly = true, Watermark = "Server stopped" };
  private readonly TextBox _executable = new() { Watermark = "Agent executable; detect or select a file" };
  private readonly TextBox _task = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90,
    Text = "Analyze the connected drone and its flight logs. Identify firmware, frame and tuning parameters. "
        + "Inspect vibration/clipping, gyro noise, rate tracking, PID terms, actuator saturation and EKF health. "
        + "Compare flight-time parameters with current values. Explain missing evidence and propose justified changes with validation steps." };
  private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
  private readonly ListBox _proposals = new() { MinHeight = 70 };
  private readonly ComboBox _logTarget = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
  private readonly ListBox _onboardLogs = new() { MinHeight = 60 };
  private readonly ListBox _localLogs = new() { MinHeight = 70 };
  private string? _onboardTargetId;
  private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
  private readonly TextBlock _status = new() { Text = "Choose an installed agent and Launch, or open the persistent MCP port in Connections.", TextWrapping = TextWrapping.Wrap };
  private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
  private readonly System.Text.StringBuilder _pendingOutput = new();
  private readonly object _outputSync = new();
  private bool _closing;
  private int _activeOperations;

  internal AgentToolsWindow(MainWindowViewModel main, Func<CancellationToken, Task<McpAgent[]>>? discover = null) {
    _main = main; _ui = new(main, _logs, () => Owner as Window); _discover = discover ?? McpAgentDiscovery.DiscoverAsync;
    Title = "AI diagnostics / MCP"; Width = 960; Height = 780; MinWidth = 680; MinHeight = 560;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var start = Button("Open session port", StartAsync);
    var stop = Button("Close all connections", StopAsync, true);
    var attach = Button("Attach flight log…", AttachAsync);
    var copy = Button("Copy connection settings", CopyAsync);
    var launch = Button("Launch selected agent", LaunchAsync);
    var browse = Button("Executable…", ChooseExecutableAsync);
    var review = Button("Review / apply selected proposal", ApplyAsync);
    var export = Button("Export proposal…", ExportAsync);
    var top = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "Flight diagnostics and tuning", FontSize = 20 },
      new TextBlock { Text = "Agent tools: diagnostics, maps, graphs and local mission drafts. Aircraft parameter changes require review here. "
          + "Model-provider authentication is handled by the installed agent.", TextWrapping = TextWrapping.Wrap },
      new WrapPanel { Orientation = Orientation.Horizontal, Children = { stop, attach } },
      _detectedAgents, _registration,
    } };
    _detectedAgents.SelectionChanged += (_, _) => {
      if (_detectedAgents.SelectedItem is McpAgent agent) {
        _executable.Text = agent.Executable;
        _agentKind.SelectedIndex = agent.Kind == McpAgentKind.ClaudeCode ? 1 : 0;
        _agentKind.IsVisible = _executable.IsVisible = !agent.IsDesktop;
        RefreshRegistration();
      }
    };
    var agentPanel = new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = {
      new WrapPanel { Children = { Button("Find agents", FindAgentsAsync), browse, launch } },
      _agentKind, _executable,
      new TextBlock { Text = "Terminal working directory" }, _workingDirectory,
      new TextBlock { Text = "Initial task (optional for terminal agents)" }, _task,
      new WrapPanel { Children = { Button("Register selected desktop", RegisterDesktopAsync), Button("Remove registration", UnregisterDesktopAsync) } },
      new TextBlock { Text = "Launch opens the external agent with its existing login and settings. Desktop Launch registers MCP automatically. Closing access leaves the external app open.", TextWrapping = TextWrapping.Wrap },
      _output,
    } } };
    _output.MinHeight = 140;
    _desktopTab.Content = BuildDesktopPanel(start, copy);
    var proposalPanel = new Avalonia.Controls.Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8,
      Children = { _proposals, _details, new WrapPanel { Children = { review, export } } } };
    Avalonia.Controls.Grid.SetRow(_details, 1); Avalonia.Controls.Grid.SetRow(proposalPanel.Children[2], 2);
    _proposals.SelectionChanged += (_, _) => {
      _details.Text = _proposals.SelectedItem is ParameterProposal p
          ? JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }) : "";
    };
    foreach (var tab in new[] {
      new TabItem { Header = "Agent", Content = agentPanel },
      new TabItem { Header = "Parameter proposals", Content = proposalPanel },
      new TabItem { Header = "Flight logs", Content = BuildLogPanel() },
      _desktopTab,
    }) { _tabs.Items.Add(tab); }
    Content = new Avalonia.Controls.Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10,
      Children = { top, _tabs, _status } };
    Avalonia.Controls.Grid.SetRow(_tabs, 1); Avalonia.Controls.Grid.SetRow(_status, 2);
    _timer.Tick += (_, _) => Refresh(); _timer.Start();
    Opened += async (_, _) => {
      try { await FindAgentsAsync(); } catch (OperationCanceledException) { }
      catch (Exception e) { _status.Text = "Agent discovery: " + e.Message; }
    };
    Closing += (_, e) => {
      if (!_closing) { e.Cancel = true; _ = CloseAsync(); }
    };
  }

  internal async Task FindAgentsAsync() {
    var agents = await Task.Run(() => _discover(_windowStop.Token), _windowStop.Token);
    if (_closingStarted) { return; }
    var selected = _detectedAgents.SelectedItem as McpAgent;
    _detectedAgents.ItemsSource = agents;
    _detectedAgents.SelectedItem = agents.FirstOrDefault(a => a.Kind == selected?.Kind && a.Executable == selected.Executable) ?? agents.FirstOrDefault();
    RefreshRegistration();
    _status.Text = $"Found {agents.Length} installed agent(s). No application was launched.";
  }

  private Control BuildDesktopPanel(Button start, Button copy) {
    if (int.TryParse(Settings.Instance["mcpDesktopPort"], out int saved) && saved is >= 1024 and <= 65535) { _desktopPort.Value = saved; }
    _desktopPort.ValueChanged += (_, _) => RefreshRegistration();
    return new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "Persistent local MCP port", FontWeight = FontWeight.Bold },
      _desktopPort,
      new WrapPanel { Children = { Button("Open port", OpenDesktopAsync), Button("Close port", StopDesktopAsync, true) } },
      _desktopEndpoint, _desktopState,
      new TextBlock { Text = "Opening this port permits local clients to read diagnostics. Launch or Allow also permits UI inspection/capture, navigation, graphs, local mission draft edits, proposals and vehicle read requests. Mission upload and aircraft parameter writes require operator action.", TextWrapping = TextWrapping.Wrap },
      new TextBlock { Text = "Temporary session port (bearer token)", FontWeight = FontWeight.Bold },
      new WrapPanel { Children = { start, copy } }, _endpoint,
      new TextBlock { Text = "Connected sessions — names are reported by clients", FontWeight = FontWeight.Bold },
      _sessions,
      new WrapPanel { Children = { Button("Allow selected", () => SessionAction(0)), Button("Revoke selected", () => SessionAction(1), true),
        Button("Disconnect selected", () => SessionAction(2), true), Button("Allow all", AllowAllAsync), Button("Revoke all", RevokeAllAsync, true) } },
    } } };
  }

  private IEnumerable<MissionPlannerMcpServer> Servers => new[] { _server, _desktopServer }.OfType<MissionPlannerMcpServer>();
  private int DesktopPort => (int)(_desktopPort.Value ?? McpDesktopRegistration.DefaultPort);
  private McpAgent SelectedDesktop() => _detectedAgents.SelectedItem is McpAgent { IsDesktop: true } agent ? agent
      : throw new InvalidOperationException("Select a desktop application in the agent list.");
  private void RefreshRegistration() {
    _registration.Text = _detectedAgents.SelectedItem is McpAgent { IsDesktop: true } agent
        ? McpDesktopRegistration.RegistrationState(agent.Kind, DesktopPort) : "Terminal launch uses this session without permanent registration.";
  }
  private Task SessionAction(int action) {
    var row = _sessions.SelectedItem as SessionRow ?? throw new InvalidOperationException("Select a connected session.");
    if (action == 0) { row.Server.AllowSession(row.Info.Id); }
    else if (action == 1) { row.Server.RevokeSession(row.Info.Id); }
    else { row.Server.DisconnectSession(row.Info.Id); }
    Refresh(); return Task.CompletedTask;
  }
  private Task AllowAllAsync() {
    foreach (var server in Servers) { foreach (var session in server.Sessions) { server.AllowSession(session.Id); } }
    Refresh(); return Task.CompletedTask;
  }
  private Task RevokeAllAsync() {
    foreach (var server in Servers) { server.RevokeSessions(); }
    Refresh(); return Task.CompletedTask;
  }
  private async Task RegisterDesktopAsync() {
    var agent = SelectedDesktop(); int port = DesktopPort;
    string? backup = await Task.Run(() => McpDesktopRegistration.Update(agent.Kind, port));
    Settings.Instance["mcpDesktopPort"] = port.ToString(CultureInfo.InvariantCulture);
    RefreshRegistration();
    _status.Text = "Registered. Restart or reconnect the desktop client to load MCP settings.";
    if (backup != null) { Output("Client configuration backup: " + backup + "\n"); }
  }
  private async Task UnregisterDesktopAsync() {
    var agent = SelectedDesktop(); await StopDesktopAsync();
    string? backup = await Task.Run(() => McpDesktopRegistration.Update(agent.Kind, null));
    RefreshRegistration();
    _status.Text = "Registration removed. Restart the client to reload its settings.";
    if (backup != null) { Output("Client configuration backup: " + backup + "\n"); }
  }
  private async Task OpenDesktopAsync() {
    if (_desktopServer != null) { return; }
    int generation = _accessGeneration, port = DesktopPort;
    var server = CreateServer(port, false);
    _desktopServer = server; _desktopPort.IsEnabled = false;
    try {
      await server.StartAsync(ReadMissionAsync, _windowStop.Token);
      if (_desktopServer != server || generation != _accessGeneration) { return; }
      Settings.Instance["mcpDesktopPort"] = port.ToString(CultureInfo.InvariantCulture);
      _desktopEndpoint.Text = server.Endpoint!.AbsoluteUri;
      _desktopState.Text = "Port open. Waiting for clients; self-connected sessions start read only.";
    } catch {
      if (_desktopServer == server) { _desktopServer = null; _desktopPort.IsEnabled = true; }
      await server.DisposeAsync(); throw;
    }
  }
  private async Task LaunchDesktopAsync() {
    var agent = SelectedDesktop(); int generation = _accessGeneration;
    await RegisterDesktopAsync();
    if (_closingStarted || generation != _accessGeneration) { return; }
    await OpenDesktopAsync();
    var server = _desktopServer;
    if (server == null || generation != _accessGeneration) { return; }
    server.GrantDesktopLaunch();
    try {
      await McpAgentDiscovery.LaunchDesktopAsync(agent, server.Stopping);
      if (_desktopServer == server) { _desktopState.Text = "Launch requested. Its sessions are allowed until Revoke or Close. Waiting for MCP requests."; }
    } catch { server.RevokeSessions(); throw; }
  }

  private async Task StopDesktopAsync() {
    _accessGeneration++;
    var server = _desktopServer; _desktopServer = null;
    server?.RevokeAccess();
    _desktopEndpoint.Text = ""; _desktopPort.IsEnabled = true;
    if (server != null) { await server.DisposeAsync(); }
    _desktopState.Text = "Desktop MCP stopped. Registration retained; desktop app remains open.";
  }

  private Control BuildLogPanel() {
    _logTarget.SelectionChanged += (_, _) => { _onboardLogs.ItemsSource = null; _onboardTargetId = null; };
    return new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "Download DataFlash BIN from a disarmed aircraft, or attach BIN/LOG/TLOG files. "
          + "TLOG is recorded by the ground station during telemetry connection.", TextWrapping = TextWrapping.Wrap },
      new WrapPanel { Children = { Button("Refresh vehicles", RefreshLogTargetsAsync), Button("List onboard logs", ListOnboardLogsAsync) } },
      _logTarget, _onboardLogs,
      new WrapPanel { Children = { Button("Download selected BIN", DownloadSelectedLogAsync) } },
      new TextBlock { Text = "Logs available to the agent and analyzer", FontWeight = FontWeight.Bold },
      new WrapPanel { Children = { Button("Discover local logs", DiscoverLogsAsync), Button("Attach files…", AttachAsync),
        Button("Open selected in analyzer", OpenSelectedLogAsync) } },
      _localLogs,
    } } };
  }

  private Task RefreshLogTargetsAsync() {
    _logTarget.ItemsSource = _vehicles.ListTargets();
    _logTarget.SelectedIndex = 0;
    return Task.CompletedTask;
  }

  private async Task ListOnboardLogsAsync() {
    var cancellation = LocalOperationToken;
    var target = _logTarget.SelectedItem as McpTarget ?? throw new InvalidOperationException("Refresh vehicles and select a target.");
    _onboardLogs.ItemsSource = null; _onboardTargetId = null;
    _status.Text = "Requesting onboard log directory…";
    var result = (McpOnboardLogs)await _vehicles.OnboardLogs(target.Id, cancellation);
    if (cancellation.IsCancellationRequested || !ReferenceEquals(_logTarget.SelectedItem, target)) { return; }
    _onboardTargetId = target.Id; _onboardLogs.ItemsSource = result.Logs;
    _onboardLogs.SelectedIndex = 0;
    _status.Text = $"{result.Logs.Length} onboard logs. " + (result.Complete ? "Directory complete." : "Directory incomplete; retry if needed.");
  }

  private async Task DownloadSelectedLogAsync() {
    var cancellation = LocalOperationToken;
    var target = _logTarget.SelectedItem as McpTarget ?? throw new InvalidOperationException("Select a vehicle.");
    var log = _onboardLogs.SelectedItem as McpOnboardLog ?? throw new InvalidOperationException("List and select an onboard log.");
    if (target.Id != _onboardTargetId) { throw new InvalidOperationException("Target changed; list onboard logs again."); }
    _status.Text = $"Downloading log {log.Id} from {target.State.sysid}:{target.State.compid}… Close all connections cancels.";
    var info = await McpFlightLogWorkflow.DownloadAsync(ct => _vehicles.Download(target.Id, log.Id, ct), _logs,
        Path.Combine(Settings.Instance.LogDir, "agent-downloads"), log.Id, cancellation);
    if (cancellation.IsCancellationRequested) { return; }
    Refresh(); _localLogs.SelectedItem = info;
    _status.Text = $"Downloaded {info.Name}. Available to MCP; open it in the analyzer below.";
  }

  private async Task DiscoverLogsAsync() {
    await Task.Run(() => _logs.Discover(Settings.Instance.LogDir), _windowStop.Token);
    if (!_closingStarted) { Refresh(); _status.Text = "Local BIN/LOG/TLOG catalogue refreshed."; }
  }

  private async Task OpenSelectedLogAsync() {
    var log = _localLogs.SelectedItem as McpLogInfo ?? throw new InvalidOperationException("Select a local or downloaded log.");
    await LogBrowseWindow.OpenWith(_logs.PathFor(log.Id));
  }

  private Button Button(string label, Func<Task> action, bool interrupt = false) {
    var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) };
    button.Click += async (_, _) => {
      if (_closingStarted || (_activeOperations != 0 && !interrupt)) { return; }
      _activeOperations++;
      try { await action(); } catch (Exception e) { _status.Text = e.Message; }
      finally { _activeOperations--; }
    };
    return button;
  }

  private void Refresh() {
    var rows = Servers.SelectMany(server => server.Sessions.Select(info => new SessionRow(server, info))).ToArray();
    if (_sessions.ItemsSource is not SessionRow[] currentRows || !currentRows.SequenceEqual(rows)) {
      string? selected = (_sessions.SelectedItem as SessionRow)?.Info.Id;
      _sessions.ItemsSource = rows;
      _sessions.SelectedItem = rows.FirstOrDefault(r => r.Info.Id == selected);
    }
    lock (_outputSync) {
      if (_pendingOutput.Length != 0) {
        string value = (_output.Text ?? "") + _pendingOutput;
        _output.Text = value.Length <= 65536 ? value : value[^65536..];
        _pendingOutput.Clear();
      }
    }
    var logs = _logs.List();
    if (_localLogs.ItemsSource is not McpLogInfo[] displayed || !displayed.SequenceEqual(logs)) {
      object? selected = _localLogs.SelectedItem;
      _localLogs.ItemsSource = logs; _localLogs.SelectedItem = selected;
    }
    var proposals = _vehicles.Proposals();
    var current = _proposals.ItemsSource as ParameterProposal[];
    if (current == null || !current.SequenceEqual(proposals)) {
      object? selected = _proposals.SelectedItem;
      _proposals.ItemsSource = proposals;
      _proposals.SelectedItem = selected;
    }
  }

  private void Output(string message) {
    lock (_outputSync) {
      _pendingOutput.Append(message);
      if (_pendingOutput.Length > 65536) { _pendingOutput.Remove(0, _pendingOutput.Length - 65536); }
    }
  }

  private MissionPlannerMcpServer CreateServer(int port = 0, bool requiresToken = true) {
    var server = new MissionPlannerMcpServer(_vehicles, _logs, port, requiresToken) {
      UiHost = _ui, OpenLogAnalyzer = _ui.OpenPath,
    };
    server.Activity += text => {
      Output((requiresToken ? "CLI: " : "Desktop: ") + text + Environment.NewLine);
      if (!requiresToken) { Dispatcher.UIThread.Post(() => {
        if (_desktopServer == server) { _desktopState.Text = "Desktop MCP received a request: " + text; }
      }); }
    };
    return server;
  }

  private async Task<object> ReadMissionAsync(CancellationToken ct) => await Dispatcher.UIThread.InvokeAsync(() => {
    ct.ThrowIfCancellationRequested();
    return (object)new { source = "Mission Planner UI draft", activeSystemId = AppState.comPort.MAV.sysid,
      activeComponentId = AppState.comPort.MAV.compid, homeLatitude = _main.FlightPlanner.HomeLat,
      homeLongitude = _main.FlightPlanner.HomeLng, homeAltitude = _main.FlightPlanner.HomeAlt,
      waypoints = _main.FlightPlanner.Waypoints.Take(10000).Select(w => new {
        sequence = w.Seq, command = w.Command, frame = w.Frame, latitude = w.Lat, longitude = w.Lng,
        altitude = w.Alt, p1 = w.P1, p2 = w.P2, p3 = w.P3, p4 = w.P4,
      }).ToArray() };
  });

  private async Task StartAsync() {
    if (_server != null) { return; }
    var server = CreateServer(); _server = server;
    try {
      await server.StartAsync(ReadMissionAsync, _windowStop.Token);
      if (_server != server) { return; }
      _endpoint.Text = server.Endpoint!.AbsoluteUri;
      _status.Text = "Session MCP listening on loopback with a temporary token.";
    } catch { if (_server == server) { _server = null; } await server.DisposeAsync(); throw; }
  }

  private CancellationToken LocalOperationToken {
    get {
      if (_localOperationsStop.IsCancellationRequested) { _localOperationsStop = CancellationTokenSource.CreateLinkedTokenSource(_windowStop.Token); }
      return _localOperationsStop.Token;
    }
  }
  private async Task StopAsync() {
    _accessGeneration++;
    var servers = Servers.ToArray();
    // Revoke every listener synchronously BEFORE the first await, including blocked requests/startup.
    foreach (var server in servers) { server.RevokeAccess(); }
    _localOperationsStop.Cancel();
    _server = _desktopServer = null;
    _endpoint.Text = _desktopEndpoint.Text = ""; _desktopPort.IsEnabled = true;
    _desktopState.Text = "MCP access closed. Registration retained; external agents remain open.";
    foreach (var launch in _terminalLaunches) { launch.Dispose(); }
    _terminalLaunches.Clear();
    await Task.WhenAll(servers.Select(server => server.DisposeAsync().AsTask()));
    _status.Text = "All MCP connections closed; session credentials revoked.";
    Refresh();
  }

  private async Task CloseAsync() {
    if (_closingStarted) { return; }
    _closingStarted = true; _windowStop.Cancel();
    try { await StopAsync(); await Task.Run(_logs.Dispose); }
    catch (Exception e) { Output(e.Message); }
    finally { _timer.Stop(); _closing = true; Close(); }
  }

  internal void BeginShutdown() { _ = CloseAsync(); }

  private async Task AttachAsync() {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Attach flight logs", AllowMultiple = true,
      FileTypeFilter = [new FilePickerFileType("Flight logs") { Patterns = ["*.bin", "*.log", "*.tlog"] }],
    });
    if (_closingStarted) { return; }
    foreach (var file in files) {
      string? path = file.TryGetLocalPath();
      if (path != null) {
        var info = _logs.Attach(path);
        Refresh(); _localLogs.SelectedItem = info;
        Output($"Attached {info.Name}; log ID {info.Id}\n");
      }
    }
  }

  private async Task CopyAsync() {
    var server = _server ?? _desktopServer ?? throw new InvalidOperationException("Open a port first in Connections.");
    if (Clipboard != null) {
      string configuration = "[mcp_servers.missionplanner]\nurl = " + JsonSerializer.Serialize(server.Endpoint!.AbsoluteUri)
          + (server.RequiresToken ? "\nhttp_headers = { Authorization = \"Bearer " + server.Token + "\" }" : "") + "\ntool_timeout_sec = 720\n";
      await Clipboard.SetTextAsync(configuration);
      _status.Text = "Copied connection settings. Close all connections revokes current access.";
    }
  }

  private async Task ChooseExecutableAsync() {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select native codex or claude executable" });
    if (files.Count > 0 && files[0].TryGetLocalPath() is string path) {
      _detectedAgents.SelectedItem = null; _agentKind.IsVisible = _executable.IsVisible = true;
      _executable.Text = path; RefreshRegistration();
    }
  }

  private async Task LaunchAsync() {
    if (_detectedAgents.SelectedItem is McpAgent { IsDesktop: true }) { await LaunchDesktopAsync(); return; }
    string executable = _executable.Text?.Trim() ?? "";
    if (!File.Exists(executable)) { throw new InvalidOperationException("Find agents or select an installed executable first."); }
    int generation = _accessGeneration;
    await StartAsync();
    var server = _server;
    if (server?.Endpoint == null || generation != _accessGeneration) { return; }
    string token = server.IssueLaunchToken();
    McpTerminalLaunch? launch = null;
    try {
      if (_terminalLaunches.Count >= 64) { throw new InvalidOperationException("Close connections before launching more agents."); }
      var agent = new McpAgent(_agentKind.SelectedIndex == 1 ? McpAgentKind.ClaudeCode : McpAgentKind.CodexCli, "Terminal agent", executable, []);
      launch = new(agent, server.Endpoint, token, _workingDirectory.Text ?? "", _task.Text ?? "");
      _terminalLaunches.Add(launch);
      await launch.LaunchAsync(server.Stopping);
      if (generation == _accessGeneration) { _status.Text = "Terminal launch requested. Connected clients appear in Connections."; }
    } catch {
      server.CancelLaunch(token);
      if (launch != null) { _terminalLaunches.Remove(launch); launch.Dispose(); }
      throw;
    }
  }

  private ParameterProposal Selected() => _proposals.SelectedItem as ParameterProposal
      ?? throw new InvalidOperationException("Select a proposal first.");

  private async Task ExportAsync() {
    var proposal = Selected();
    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Export proposed parameters", SuggestedFileName = "agent-proposal.param",
    });
    if (file == null) { return; }
    await using var stream = await file.OpenWriteAsync();
    stream.SetLength(0);
    await using var writer = new StreamWriter(stream);
    await writer.WriteLineAsync("# Proposed values only; review aircraft identity and evidence before applying.");
    foreach (var change in proposal.Changes) {
      await writer.WriteLineAsync(change.Name + "," + change.Proposed.ToString("R", CultureInfo.InvariantCulture));
    }
  }

  private async Task ApplyAsync() {
    var proposal = Selected();
    var cancellation = LocalOperationToken;
    var target = _vehicles.Resolve(proposal.TargetId, true);
    McpVehicleAccess.RequireDisarmed(target);
    if (target.Connection.Link.ReadOnly) {
      throw new InvalidOperationException("Applying proposals requires a disarmed vehicle and a writable connection.");
    }
    string summary = $"Vehicle {target.State.sysid}/{target.State.compid} on {target.Connection.Endpoint}\n\n"
        + proposal.Rationale + "\n\n" + string.Join("\n", proposal.Changes.Select(c =>
            $"{c.Name}: {c.Expected.ToString("R", CultureInfo.InvariantCulture)} → {c.Proposed.ToString("R", CultureInfo.InvariantCulture)}\n{c.Reason}"));
    var review = new Window { Title = "Review parameter changes", Width = 780, Height = 640,
      MinWidth = 560, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
    var apply = new Button { Content = "Apply to disarmed vehicle" };
    var progress = new TextBlock { TextWrapping = TextWrapping.Wrap };
    var body = new TextBox { Text = summary, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    var actions = new WrapPanel { Children = { cancel, apply } };
    review.Content = new Avalonia.Controls.Grid { Margin = new Thickness(12), RowSpacing = 8,
      RowDefinitions = new RowDefinitions("*,Auto,Auto"), Children = { body, progress, actions } };
    Avalonia.Controls.Grid.SetRow(progress, 1); Avalonia.Controls.Grid.SetRow(actions, 2);
    bool applying = false;
    cancel.Click += (_, _) => { if (!applying) { review.Close(); } };
    review.Closing += (_, e) => e.Cancel = applying;
    apply.Click += async (_, _) => {
      applying = true; apply.IsEnabled = false; cancel.IsEnabled = false;
      progress.Text = "Applying and verifying. Wait for completion before disconnecting or arming.";
      try { _status.Text = await McpProposalWriter.ApplyAsync(_vehicles, proposal, cancellation); }
      catch (Exception e) { _status.Text = e.Message; }
      finally { applying = false; review.Close(); }
    };
    // Keep ordinary UI writes unavailable throughout review/application. The core also serializes
    // parameter writers and compares a fresh value immediately before every proposal write.
    Window? mainOwner = Owner as Window;
    bool ownerEnabled = mainOwner?.IsEnabled ?? true;
    try {
      if (mainOwner != null) { mainOwner.IsEnabled = false; }
      await review.ShowDialog(this);
    } finally { if (mainOwner != null) { mainOwner.IsEnabled = ownerEnabled; } }
    _details.Text = JsonSerializer.Serialize(proposal, new JsonSerializerOptions { WriteIndented = true });
  }
}
