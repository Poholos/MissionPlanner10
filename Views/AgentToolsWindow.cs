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
  private MissionPlannerMcpServer? _server;
  private CancellationTokenSource? _agentStop;
  private Task? _agentTask;
  private readonly TextBox _endpoint = new() { IsReadOnly = true, Watermark = "Server stopped" };
  private readonly TextBox _executable = new() { Text = "codex", Watermark = "Codex executable or absolute path" };
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

  internal AgentToolsWindow(MainWindowViewModel main) {
    _main = main;
    Title = "AI diagnostics / MCP"; Width = 960; Height = 780; MinWidth = 680; MinHeight = 560;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var start = Button("Start server", StartAsync);
    var stop = Button("Stop / revoke access", StopAsync, true);
    var attach = Button("Attach flight log…", AttachAsync);
    var copy = Button("Copy connection settings", CopyAsync);
    var launch = Button("Run Codex", LaunchAsync);
    var browse = Button("Executable…", ChooseExecutableAsync);
    var review = Button("Review / apply selected proposal", ApplyAsync);
    var export = Button("Export proposal…", ExportAsync);
    var top = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "Flight diagnostics and tuning", FontSize = 20 },
      new TextBlock { Text = "Agent data: vehicle telemetry, parameters and flight logs. Parameter changes require review here. "
          + "Model-provider authentication is handled by the installed agent.", TextWrapping = TextWrapping.Wrap },
      new WrapPanel { Orientation = Orientation.Horizontal, Children = { start, stop, attach, copy } }, _endpoint,
    } };
    var agentPanel = new Avalonia.Controls.Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), RowSpacing = 8 };
    var launchRow = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8,
      Children = { _executable, browse, launch } };
    Avalonia.Controls.Grid.SetColumn(browse, 1); Avalonia.Controls.Grid.SetColumn(launch, 2);
    agentPanel.Children.Add(launchRow);
    agentPanel.Children.Add(_task); Avalonia.Controls.Grid.SetRow(_task, 1);
    var hint = new TextBlock { Text = "Output is limited to the latest 64 KiB. Stop also revokes external MCP access.", TextWrapping = TextWrapping.Wrap };
    agentPanel.Children.Add(hint); Avalonia.Controls.Grid.SetRow(hint, 2);
    agentPanel.Children.Add(_output); Avalonia.Controls.Grid.SetRow(_output, 3);
    var proposalPanel = new Avalonia.Controls.Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8,
      Children = { _proposals, _details, new WrapPanel { Children = { review, export } } } };
    Avalonia.Controls.Grid.SetRow(_details, 1); Avalonia.Controls.Grid.SetRow(proposalPanel.Children[2], 2);
    _proposals.SelectionChanged += (_, _) => {
      _details.Text = _proposals.SelectedItem is ParameterProposal p
          ? JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }) : "";
    };
    var tabs = new TabControl { Items = {
      new TabItem { Header = "Agent", Content = agentPanel },
      new TabItem { Header = "Parameter proposals", Content = proposalPanel },
      new TabItem { Header = "Flight logs", Content = BuildLogPanel() },
    } };
    Content = new Avalonia.Controls.Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10,
      Children = { top, tabs, _status } };
    Avalonia.Controls.Grid.SetRow(tabs, 1); Avalonia.Controls.Grid.SetRow(_status, 2);
    _timer.Tick += (_, _) => Refresh(); _timer.Start();
    Closing += (_, e) => {
      if (!_closing) { e.Cancel = true; _ = CloseAsync(); }
    };
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
    await StartAsync();
    var server = _server!;
    await Task.Run(() => server.Logs.Discover(Settings.Instance.LogDir));
    if (_server == server) { Refresh(); _status.Text = "Local BIN/LOG/TLOG catalogue refreshed."; }
  }

  private async Task OpenSelectedLogAsync() {
    var server = _server ?? throw new InvalidOperationException("Start the server first.");
    var log = _localLogs.SelectedItem as McpLogInfo ?? throw new InvalidOperationException("Select a local or downloaded log.");
    await LogBrowseWindow.OpenWith(server.Logs.PathFor(log.Id));
  }

  private Button Button(string label, Func<Task> action, bool interrupt = false) {
    var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) };
    button.Click += async (_, _) => {
      if (_activeOperations != 0 && !interrupt) { return; }
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
    var logs = _server?.Logs.List() ?? [];
    if (_localLogs.ItemsSource is not McpLogInfo[] displayed || !displayed.SequenceEqual(logs)) {
      object? selected = _localLogs.SelectedItem;
      _localLogs.ItemsSource = logs; _localLogs.SelectedItem = selected;
    }
    var proposals = _server?.Vehicles.Proposals() ?? [];
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

  private async Task StartAsync() {
    if (_server != null) { return; }
    var server = new MissionPlannerMcpServer(new McpVehicleAccess(() => AppState.Connections.Snapshot())) {
      OpenLogAnalyzer = async (path, ct) => {
        ct.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => {
          ct.ThrowIfCancellationRequested();
          return LogBrowseWindow.OpenWith(path);
        });
      },
    };
    server.Activity += text => Output(text + Environment.NewLine);
    _server = server;
    try {
      await server.StartAsync(async ct => await Dispatcher.UIThread.InvokeAsync(() => {
        ct.ThrowIfCancellationRequested();
        return (object)new { source = "Mission Planner UI draft", activeSystemId = AppState.comPort.MAV.sysid,
          activeComponentId = AppState.comPort.MAV.compid, homeLatitude = _main.FlightPlanner.HomeLat,
          homeLongitude = _main.FlightPlanner.HomeLng, homeAltitude = _main.FlightPlanner.HomeAlt,
          waypoints = _main.FlightPlanner.Waypoints.Take(10000).Select(w => new {
            sequence = w.Seq, command = w.Command, frame = w.Frame, latitude = w.Lat, longitude = w.Lng,
            altitude = w.Alt, p1 = w.P1, p2 = w.P2, p3 = w.P3, p4 = w.P4,
          }).ToArray() };
      }));
      if (_server != server) { return; }
      _endpoint.Text = server.Endpoint!.AbsoluteUri;
      _status.Text = "MCP is listening on loopback. Attach logs or launch Codex.";
    } catch { if (_server == server) { _server = null; } await server.DisposeAsync(); throw; }
  }

  private async Task StopAsync() {
    _agentStop?.Cancel();
    var server = _server; _server = null;
    _endpoint.Text = "";
    if (server != null) { await server.DisposeAsync(); }
    if (_agentTask != null) {
      try { await _agentTask; } catch (OperationCanceledException) { }
    }
    _agentStop?.Dispose(); _agentStop = null;
    _status.Text = "Stopped. Endpoint and token revoked.";
  }

  private async Task CloseAsync() {
    try { await StopAsync(); }
    catch (Exception e) { Output(e.Message); }
    finally { _timer.Stop(); _closing = true; Close(); }
  }

  internal void BeginShutdown() { _ = CloseAsync(); }

  private async Task AttachAsync() {
    await StartAsync();
    var server = _server!;
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Attach flight logs", AllowMultiple = true,
      FileTypeFilter = [new FilePickerFileType("Flight logs") { Patterns = ["*.bin", "*.log", "*.tlog"] }],
    });
    if (_server != server) { return; }
    foreach (var file in files) {
      string? path = file.TryGetLocalPath();
      if (path != null) {
        var info = server.Logs.Attach(path);
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
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select Codex executable" });
    if (files.Count > 0 && files[0].TryGetLocalPath() is string path) { _executable.Text = path; }
  }

  private async Task LaunchAsync() {
    if (_agentTask is { IsCompleted: false }) { throw new InvalidOperationException("An agent is already running. Stop it first."); }
    await StartAsync();
    _agentStop?.Dispose();
    _agentStop = CancellationTokenSource.CreateLinkedTokenSource(_server!.Stopping);
    _status.Text = "Agent running. Results appear below; proposals appear in the second tab.";
    _agentTask = RunAgentAsync(_server, _agentStop.Token);
  }

  private async Task RunAgentAsync(MissionPlannerMcpServer server, CancellationToken ct) {
    try {
      int exit = await McpAgentProcess.RunAsync(_executable.Text ?? "codex", server.Endpoint!, server.Token,
          _task.Text ?? "", Output, ct);
      _status.Text = $"Agent exited with code {exit}. Review output and parameter proposals.";
    } catch (OperationCanceledException) { _status.Text = "Agent stopped."; }
    catch (Exception e) { _status.Text = "Agent failed: " + e.Message; }
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
