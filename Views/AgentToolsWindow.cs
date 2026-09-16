using System;
using System.Globalization;
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
  private readonly Func<CancellationToken, Task<McpAgent[]>> _discover;
  private MissionPlannerMcpServer? _server;
  private MissionPlannerMcpServer? _desktopServer;
  private readonly McpVehicleAccess _vehicles = new(() => AppState.Connections.Snapshot());
  private readonly McpLogCatalog _logs = new();
  private readonly CancellationTokenSource _windowStop = new();
  private readonly ComboBox _detectedAgents = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
  private readonly ComboBox _agentKind = new() { ItemsSource = new[] { "Codex CLI", "Claude Code" }, SelectedIndex = 0 };
  private readonly ComboBox _desktopAgents = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
  private readonly NumericUpDown _desktopPort = new() { Minimum = 1024, Maximum = 65535, Increment = 1,
    Value = McpDesktopRegistration.DefaultPort, FormatString = "0", Width = 150 };
  private readonly TextBlock _desktopState = new() { TextWrapping = TextWrapping.Wrap, Text = "Desktop MCP stopped." };
  private readonly TextBox _desktopEndpoint = new() { IsReadOnly = true };
  private readonly TabItem _desktopTab = new() { Header = "Desktop", IsVisible = false };
  private bool _closingStarted;
  private readonly TabControl _tabs = new();
  private CancellationTokenSource? _agentStop;
  private Task? _agentTask;
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
  private readonly TextBlock _status = new() { Text = "Start the server, attach a log or connect a vehicle, then launch an agent.", TextWrapping = TextWrapping.Wrap };
  private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
  private readonly System.Text.StringBuilder _pendingOutput = new();
  private readonly object _outputSync = new();
  private bool _closing;
  private int _activeOperations;

  internal AgentToolsWindow(MainWindowViewModel main, Func<CancellationToken, Task<McpAgent[]>>? discover = null) {
    _main = main; _discover = discover ?? McpAgentDiscovery.DiscoverAsync;
    Title = "AI diagnostics / MCP"; Width = 960; Height = 780; MinWidth = 680; MinHeight = 560;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var start = Button("Start server", StartAsync);
    var stop = Button("Stop / revoke access", StopAsync, true);
    var attach = Button("Attach flight log…", AttachAsync);
    var copy = Button("Copy connection settings", CopyAsync);
    var launch = Button("Run agent", LaunchAsync);
    var browse = Button("Executable…", ChooseExecutableAsync);
    var review = Button("Review / apply selected proposal", ApplyAsync);
    var export = Button("Export proposal…", ExportAsync);
    var top = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "Flight diagnostics and tuning", FontSize = 20 },
      new TextBlock { Text = "Agent data: vehicle telemetry, parameters and flight logs. Parameter changes require review here. "
          + "Model-provider authentication is handled by the installed agent.", TextWrapping = TextWrapping.Wrap },
      new WrapPanel { Orientation = Orientation.Horizontal, Children = { start, stop, attach, copy } }, _endpoint,
    } };
    _detectedAgents.SelectionChanged += (_, _) => {
      if (_detectedAgents.SelectedItem is McpAgent agent) {
        _executable.Text = agent.Executable;
        _agentKind.SelectedIndex = agent.Kind == McpAgentKind.ClaudeCode ? 1 : 0;
      }
    };
    var agentPanel = new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = {
      new WrapPanel { Children = { Button("Find agents", FindAgentsAsync), browse, launch } },
      _detectedAgents, _agentKind, _executable, _task,
      new TextBlock { Text = "Session-only MCP settings. Uses the agent's existing login. Closing the window stops CLI agents and both MCP listeners.", TextWrapping = TextWrapping.Wrap },
      _output,
    } } };
    _output.MinHeight = 140;
    _desktopTab.Content = BuildDesktopPanel();
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
    var cli = agents.Where(a => a.Kind != McpAgentKind.OpenAiDesktop).ToArray();
    var desktop = agents.Where(a => a.Kind == McpAgentKind.OpenAiDesktop).ToArray();
    _detectedAgents.ItemsSource = cli; _detectedAgents.SelectedIndex = cli.Length > 0 ? 0 : -1;
    _desktopAgents.ItemsSource = desktop; _desktopAgents.SelectedIndex = desktop.Length > 0 ? 0 : -1;
    if (desktop.Length == 0 && ReferenceEquals(_tabs.SelectedItem, _desktopTab)) { _tabs.SelectedIndex = 0; }
    _desktopTab.IsVisible = desktop.Length > 0;
    _status.Text = $"Found {cli.Length} CLI agent(s), {desktop.Length} OpenAI desktop app(s). Claude Desktop is not enabled.";
  }

  private Control BuildDesktopPanel() {
    if (int.TryParse(Settings.Instance["mcpDesktopPort"], out int saved) && saved is >= 1024 and <= 65535) { _desktopPort.Value = saved; }
    return new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "Register once, then launch the desktop app when you need access. Uses the OpenAI client's Codex MCP configuration.", TextWrapping = TextWrapping.Wrap },
      _desktopAgents,
      new TextBlock { Text = "Local MCP port" }, _desktopPort,
      new TextBox { IsReadOnly = true, Text = McpDesktopRegistration.ConfigPath, TextWrapping = TextWrapping.Wrap },
      new WrapPanel { Children = {
        Button("Register MCP", RegisterDesktopAsync), Button("Remove registration", UnregisterDesktopAsync),
        Button("Launch desktop agent", LaunchDesktopAsync), Button("Stop desktop access", StopDesktopAsync, true),
      } },
      _desktopEndpoint, _desktopState,
      new TextBlock { Text = "No token: any process on this computer can access exposed MCP tools while this listener runs. "
          + "Parameter changes still require review here. Closing this window stops access. Restart the desktop client after changing its registration.", TextWrapping = TextWrapping.Wrap },
    } } };
  }

  private int DesktopPort => (int)(_desktopPort.Value ?? McpDesktopRegistration.DefaultPort);
  private McpAgent SelectedDesktop() => _desktopAgents.SelectedItem as McpAgent
      ?? throw new InvalidOperationException("Find and select an installed OpenAI desktop app first.");

  private async Task RegisterDesktopAsync() {
    _ = SelectedDesktop();
    if (_desktopServer != null) { throw new InvalidOperationException("Stop desktop access before changing registration."); }
    int port = DesktopPort;
    string? backup = await Task.Run(() => McpDesktopRegistration.Update(McpDesktopRegistration.ConfigPath, port));
    Settings.Instance["mcpDesktopPort"] = port.ToString(CultureInfo.InvariantCulture);
    _desktopState.Text = "Registered " + McpDesktopRegistration.Endpoint(port) + ". Restart the desktop client to load it.";
    if (backup != null) { Output("Client configuration backup: " + backup + "\n"); }
  }

  private async Task UnregisterDesktopAsync() {
    _ = SelectedDesktop(); await StopDesktopAsync();
    string? backup = await Task.Run(() => McpDesktopRegistration.Update(McpDesktopRegistration.ConfigPath, null));
    _desktopState.Text = "Mission Planner registration removed. Restart the desktop client to reload its settings.";
    if (backup != null) { Output("Client configuration backup: " + backup + "\n"); }
  }

  private async Task LaunchDesktopAsync() {
    var agent = SelectedDesktop(); int port = DesktopPort;
    if (!McpDesktopRegistration.IsRegistered(McpDesktopRegistration.ConfigPath, port)) {
      throw new InvalidOperationException("Register MCP for this port before launching the desktop agent.");
    }
    bool created = _desktopServer == null;
    var server = _desktopServer ?? CreateServer(port, false);
    _desktopServer = server; _desktopPort.IsEnabled = false;
    try {
      await server.StartAsync(ReadMissionAsync, _windowStop.Token);
      if (_desktopServer != server) { return; }
      await McpAgentDiscovery.LaunchDesktopAsync(agent, server.Stopping);
      if (_desktopServer != server) { return; }
      _desktopEndpoint.Text = server.Endpoint!.AbsoluteUri;
      _desktopState.Text = "Desktop launch requested; local MCP listening without a token. Waiting for requests.";
    } catch {
      if (created) {
        if (_desktopServer == server) { _desktopServer = null; _desktopPort.IsEnabled = true; }
        await server.DisposeAsync();
      }
      throw;
    }
  }

  private async Task StopDesktopAsync() {
    var server = _desktopServer; _desktopServer = null;
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

  private async Task RefreshLogTargetsAsync() {
    await StartAsync();
    _logTarget.ItemsSource = _server!.Vehicles.ListTargets();
    _logTarget.SelectedIndex = 0;
  }

  private async Task ListOnboardLogsAsync() {
    await StartAsync();
    var server = _server!;
    var target = _logTarget.SelectedItem as McpTarget ?? throw new InvalidOperationException("Refresh vehicles and select a target.");
    _onboardLogs.ItemsSource = null; _onboardTargetId = null;
    _status.Text = "Requesting onboard log directory…";
    var result = (McpOnboardLogs)await server.Vehicles.OnboardLogs(target.Id, server.Stopping);
    if (_server != server || !ReferenceEquals(_logTarget.SelectedItem, target)) { return; }
    _onboardTargetId = target.Id; _onboardLogs.ItemsSource = result.Logs;
    _onboardLogs.SelectedIndex = 0;
    _status.Text = $"{result.Logs.Length} onboard logs. " + (result.Complete ? "Directory complete." : "Directory incomplete; retry if needed.");
  }

  private async Task DownloadSelectedLogAsync() {
    await StartAsync();
    var server = _server ?? throw new InvalidOperationException("Start the server first.");
    var target = _logTarget.SelectedItem as McpTarget ?? throw new InvalidOperationException("Select a vehicle.");
    var log = _onboardLogs.SelectedItem as McpOnboardLog ?? throw new InvalidOperationException("List and select an onboard log.");
    if (target.Id != _onboardTargetId) { throw new InvalidOperationException("Target changed; list onboard logs again."); }
    _status.Text = $"Downloading log {log.Id} from {target.State.sysid}:{target.State.compid}… Stop / revoke access cancels.";
    var info = await McpFlightLogWorkflow.DownloadAsync(ct => server.Vehicles.Download(target.Id, log.Id, ct), server.Logs,
        Path.Combine(Settings.Instance.LogDir, "agent-downloads"), log.Id, server.Stopping);
    if (_server != server) { return; }
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
      OpenLogAnalyzer = async (path, ct) => {
        ct.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => {
          ct.ThrowIfCancellationRequested();
          return LogBrowseWindow.OpenWith(path);
        });
      },
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

  private async Task StopAsync() {
    _agentStop?.Cancel();
    var server = _server; _server = null;
    _endpoint.Text = "";
    if (server != null) { await server.DisposeAsync(); }
    await StopDesktopAsync();
    if (_agentTask != null) {
      try { await _agentTask; } catch (OperationCanceledException) { }
    }
    _agentStop?.Dispose(); _agentStop = null;
    _status.Text = "Stopped. Both MCP listeners closed; session token revoked.";
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
    await StartAsync();
    if (Clipboard != null) {
      string configuration = "[mcp_servers.missionplanner]\nurl = " + JsonSerializer.Serialize(_server!.Endpoint!.AbsoluteUri)
          + "\nhttp_headers = { Authorization = \"Bearer " + _server.Token + "\" }\ntool_timeout_sec = 720\n";
      await Clipboard.SetTextAsync(configuration);
      _status.Text = "Copied MCP configuration including this session's access token. Stop revokes it.";
    }
  }

  private async Task ChooseExecutableAsync() {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select native codex or claude executable" });
    if (files.Count > 0 && files[0].TryGetLocalPath() is string path) { _executable.Text = path; }
  }

  private async Task LaunchAsync() {
    if (_agentTask is { IsCompleted: false }) { throw new InvalidOperationException("An agent is already running. Stop it first."); }
    string executable = _executable.Text?.Trim() ?? "";
    if (!File.Exists(executable)) { throw new InvalidOperationException("Find agents or select an installed executable first."); }
    string task = _task.Text ?? "";
    if (task.Length is < 5 or > 32000) { throw new InvalidOperationException("Enter a task of 5..32000 characters."); }
    await StartAsync();
    if (_server?.Endpoint == null) { return; }
    _agentStop?.Dispose();
    _agentStop = CancellationTokenSource.CreateLinkedTokenSource(_server!.Stopping);
    _status.Text = "Agent running. Results appear below; proposals appear in the second tab.";
    _agentTask = RunAgentAsync(_server, executable, task, _agentKind.SelectedIndex == 1 ? McpAgentKind.ClaudeCode : McpAgentKind.CodexCli, _agentStop.Token);
  }

  private async Task RunAgentAsync(MissionPlannerMcpServer server, string executable, string task, McpAgentKind kind, CancellationToken ct) {
    try {
      int exit = await McpAgentProcess.RunAsync(executable, server.Endpoint!, server.Token,
          task, Output, ct, kind);
      _status.Text = $"Agent exited with code {exit}. Review output and parameter proposals.";
    } catch (OperationCanceledException) { _status.Text = "Agent stopped."; }
    catch (Exception e) { _status.Text = "Agent failed: " + e.Message; }
    finally {
      if (_server == server) { _server = null; _endpoint.Text = ""; }
      await server.DisposeAsync();
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
    // Completed CLI sessions retain proposals; review starts a fresh cancellable access session.
    await StartAsync();
    var server = _server ?? throw new InvalidOperationException("Server stopped.");
    var target = server.Vehicles.Resolve(proposal.TargetId, true);
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
      try { _status.Text = await McpProposalWriter.ApplyAsync(server.Vehicles, proposal, server.Stopping); }
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
