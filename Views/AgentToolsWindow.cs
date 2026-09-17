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

namespace MissionPlanner.Views;

/// <summary>
/// View over the application-wide <see cref="McpAgentHub"/>. Closing this window hides it only;
/// listeners, launched agents and their grants continue until Stop all connections or application exit.
/// </summary>
internal sealed class AgentToolsWindow : Window {
  internal McpAgentHub Hub { get; }
  private readonly ComboBox _detectedAgents = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
  private readonly ComboBox _agentKind = new() { ItemsSource = new[] { "Codex CLI", "Claude Code" }, SelectedIndex = 0 };
  private readonly ListBox _sessions = new() { MinHeight = 100 };
  private readonly TextBlock _registration = new() { TextWrapping = TextWrapping.Wrap };
  private sealed record SessionRow(MissionPlannerMcpServer Server, McpConnectionInfo Info) {
    public override string ToString() => Info.ToString();
  }
  private readonly NumericUpDown _desktopPort = new() { Minimum = 1024, Maximum = 65535, Increment = 1,
    Value = McpDesktopRegistration.DefaultPort, FormatString = "0", Width = 150 };
  private readonly TextBlock _desktopState = new() { TextWrapping = TextWrapping.Wrap, Text = "Persistent port closed." };
  private readonly TextBox _desktopEndpoint = new() { IsReadOnly = true };
  private readonly TabItem _desktopTab = new() { Header = "Connections" };
  private readonly TabControl _tabs = new();
  private readonly TextBox _workingDirectory = new() { Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
  private readonly TextBox _endpoint = new() { IsReadOnly = true, Watermark = "Session port closed" };
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
  private readonly TextBlock _status = new() { Text = "Choose an installed agent and Launch. Closing this window keeps agents connected.", TextWrapping = TextWrapping.Wrap };
  private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
  private readonly System.Text.StringBuilder _pendingOutput = new();
  private readonly object _outputSync = new();
  private readonly Action<string> _onOutput;
  private bool _closed;
  private int _activeOperations;

  internal AgentToolsWindow(McpAgentHub hub) {
    Hub = hub;
    Title = "AI agents / MCP"; Width = 960; Height = 780; MinWidth = 680; MinHeight = 560;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var start = Button("Open session port", StartAsync);
    var stop = Button("Stop all connections", StopAsync, true);
    var attach = Button("Attach flight log…", AttachAsync);
    var copy = Button("Copy connection settings", CopyAsync);
    var launch = Button("Launch selected agent", LaunchAsync);
    var browse = Button("Executable…", ChooseExecutableAsync);
    var review = Button("Review / apply selected proposal", ApplyAsync);
    var export = Button("Export proposal…", ExportAsync);
    var top = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "AI agent connection", FontSize = 20 },
      new TextBlock { Text = "Launch grants the agent full access to Mission Planner: navigation, controls, missions, parameters, logs and vehicle commands. "
          + "Close this window at any time; the agent keeps working until Stop all connections. Model-provider login belongs to the agent.", TextWrapping = TextWrapping.Wrap },
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
      new TextBlock { Text = "Terminal agents receive a free port and a one-use token. Desktop applications are registered if needed, the fixed port opens without a token and the application is activated. Either way the session is allowed immediately.", TextWrapping = TextWrapping.Wrap },
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
    _output.Text = hub.RecentOutput;
    _onOutput = Output; hub.Output += _onOutput;
    _timer.Tick += (_, _) => Refresh(); _timer.Start();
    Opened += async (_, _) => {
      Refresh();
      try { await FindAgentsAsync(); } catch (OperationCanceledException) { }
      catch (Exception e) { _status.Text = "Agent discovery: " + e.Message; }
    };
    Closed += (_, _) => { _closed = true; _timer.Stop(); hub.Output -= _onOutput; };
  }

  internal async Task FindAgentsAsync() {
    var agents = await Hub.DiscoverAsync();
    if (_closed) { return; }
    var selected = _detectedAgents.SelectedItem as McpAgent;
    _detectedAgents.ItemsSource = agents;
    _detectedAgents.SelectedItem = agents.FirstOrDefault(a => a.Kind == selected?.Kind && a.Executable == selected.Executable) ?? agents.FirstOrDefault();
    RefreshRegistration();
    _status.Text = $"Found {agents.Length} installed agent(s). No application was launched.";
  }

  private Control BuildDesktopPanel(Button start, Button copy) {
    _desktopPort.Value = Hub.DesktopPort;
    _desktopPort.ValueChanged += (_, _) => {
      try { Hub.DesktopPort = (int)(_desktopPort.Value ?? McpDesktopRegistration.DefaultPort); }
      catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException) { _status.Text = e.Message; _desktopPort.Value = Hub.DesktopPort; }
      RefreshRegistration();
    };
    return new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "Persistent local MCP port", FontWeight = FontWeight.Bold },
      _desktopPort,
      new WrapPanel { Children = { Button("Open port", OpenDesktopAsync), Button("Close port", StopDesktopAsync, true) } },
      _desktopEndpoint, _desktopState,
      new TextBlock { Text = "Opening this port lets local clients read diagnostics. Launch or Allow grants full control: UI actions, mission upload, parameter writes and vehicle commands.", TextWrapping = TextWrapping.Wrap },
      new TextBlock { Text = "Temporary session port (bearer token)", FontWeight = FontWeight.Bold },
      new WrapPanel { Children = { start, copy } }, _endpoint,
      new TextBlock { Text = "Connected sessions — names are reported by clients", FontWeight = FontWeight.Bold },
      _sessions,
      new WrapPanel { Children = { Button("Allow selected", () => SessionAction(0)), Button("Revoke selected", () => SessionAction(1), true),
        Button("Disconnect selected", () => SessionAction(2), true), Button("Allow all", AllowAllAsync), Button("Revoke all", RevokeAllAsync, true) } },
    } } };
  }

  private McpAgent SelectedDesktop() => _detectedAgents.SelectedItem is McpAgent { IsDesktop: true } agent ? agent
      : throw new InvalidOperationException("Select a desktop application in the agent list.");
  private void RefreshRegistration() {
    _registration.Text = _detectedAgents.SelectedItem is McpAgent { IsDesktop: true } agent
        ? McpDesktopRegistration.RegistrationState(agent.Kind, Hub.DesktopPort) : "Terminal launch uses this session without permanent registration.";
  }
  private Task SessionAction(int action) {
    var row = _sessions.SelectedItem as SessionRow ?? throw new InvalidOperationException("Select a connected session.");
    if (action == 0) { Hub.AllowSession(row.Info.Id); }
    else if (action == 1) { Hub.RevokeSession(row.Info.Id); }
    else { Hub.DisconnectSession(row.Info.Id); }
    Refresh(); return Task.CompletedTask;
  }
  private Task AllowAllAsync() { Hub.AllowAll(); Refresh(); return Task.CompletedTask; }
  private Task RevokeAllAsync() { Hub.RevokeAll(); Refresh(); return Task.CompletedTask; }
  private async Task RegisterDesktopAsync() {
    await Hub.RegisterDesktopAsync(SelectedDesktop());
    RefreshRegistration();
    _status.Text = "Registered. Restart or reconnect the desktop client to load MCP settings.";
  }
  private async Task UnregisterDesktopAsync() {
    await Hub.UnregisterDesktopAsync(SelectedDesktop());
    RefreshRegistration();
    _status.Text = "Registration removed. Restart the client to reload its settings.";
  }
  private async Task OpenDesktopAsync() {
    await Hub.OpenDesktopPortAsync();
    _status.Text = "Persistent port open. Self-connected sessions wait for Allow.";
  }
  private async Task StopDesktopAsync() { await Hub.CloseDesktopPortAsync(); Refresh(); }

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
    _logTarget.ItemsSource = Hub.Vehicles.ListTargets();
    _logTarget.SelectedIndex = 0;
    return Task.CompletedTask;
  }

  private async Task ListOnboardLogsAsync() {
    var cancellation = Hub.LocalOperationToken;
    var target = _logTarget.SelectedItem as McpTarget ?? throw new InvalidOperationException("Refresh vehicles and select a target.");
    _onboardLogs.ItemsSource = null; _onboardTargetId = null;
    _status.Text = "Requesting onboard log directory…";
    var result = (McpOnboardLogs)await Hub.Vehicles.OnboardLogs(target.Id, cancellation);
    if (cancellation.IsCancellationRequested || !ReferenceEquals(_logTarget.SelectedItem, target)) { return; }
    _onboardTargetId = target.Id; _onboardLogs.ItemsSource = result.Logs;
    _onboardLogs.SelectedIndex = 0;
    _status.Text = $"{result.Logs.Length} onboard logs. " + (result.Complete ? "Directory complete." : "Directory incomplete; retry if needed.");
  }

  private async Task DownloadSelectedLogAsync() {
    var cancellation = Hub.LocalOperationToken;
    var target = _logTarget.SelectedItem as McpTarget ?? throw new InvalidOperationException("Select a vehicle.");
    var log = _onboardLogs.SelectedItem as McpOnboardLog ?? throw new InvalidOperationException("List and select an onboard log.");
    if (target.Id != _onboardTargetId) { throw new InvalidOperationException("Target changed; list onboard logs again."); }
    _status.Text = $"Downloading log {log.Id} from {target.State.sysid}:{target.State.compid}… Stop all connections cancels.";
    var info = await McpFlightLogWorkflow.DownloadAsync(ct => Hub.Vehicles.Download(target.Id, log.Id, ct), Hub.Logs,
        Path.Combine(Settings.Instance.LogDir, "agent-downloads"), log.Id, cancellation);
    if (cancellation.IsCancellationRequested) { return; }
    Refresh(); _localLogs.SelectedItem = info;
    _status.Text = $"Downloaded {info.Name}. Available to MCP; open it in the analyzer below.";
  }

  private async Task DiscoverLogsAsync() {
    await Task.Run(() => Hub.Logs.Discover(Settings.Instance.LogDir), Hub.Stopping);
    if (!_closed) { Refresh(); _status.Text = "Local BIN/LOG/TLOG catalogue refreshed."; }
  }

  private async Task OpenSelectedLogAsync() {
    var log = _localLogs.SelectedItem as McpLogInfo ?? throw new InvalidOperationException("Select a local or downloaded log.");
    await LogBrowseWindow.OpenWith(Hub.Logs.PathFor(log.Id));
  }

  private Button Button(string label, Func<Task> action, bool interrupt = false) {
    var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) };
    button.Click += async (_, _) => {
      if (_closed || (_activeOperations != 0 && !interrupt)) { return; }
      _activeOperations++;
      try { await action(); } catch (OperationCanceledException) { _status.Text = "Cancelled."; }
      catch (Exception e) { _status.Text = e.Message; }
      finally { _activeOperations--; }
    };
    return button;
  }

  private void Refresh() {
    var rows = Hub.SessionRows().Select(pair => new SessionRow(pair.Server, pair.Info)).ToArray();
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
    _endpoint.Text = Hub.SessionServer?.Endpoint?.AbsoluteUri ?? "";
    _desktopEndpoint.Text = Hub.DesktopServer?.Endpoint?.AbsoluteUri ?? "";
    _desktopState.Text = Hub.DesktopState;
    _desktopPort.IsEnabled = Hub.DesktopServer == null;
    var logs = Hub.Logs.List();
    if (_localLogs.ItemsSource is not McpLogInfo[] displayed || !displayed.SequenceEqual(logs)) {
      object? selected = _localLogs.SelectedItem;
      _localLogs.ItemsSource = logs; _localLogs.SelectedItem = selected;
    }
    var proposals = Hub.Vehicles.Proposals();
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
    var server = await Hub.OpenSessionPortAsync();
    _endpoint.Text = server.Endpoint!.AbsoluteUri;
    _status.Text = "Session MCP listening on loopback with a temporary token.";
  }

  private async Task StopAsync() {
    await Hub.StopAllAsync();
    _status.Text = "All MCP connections closed; session credentials revoked.";
    Refresh();
  }

  private async Task AttachAsync() {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Attach flight logs", AllowMultiple = true,
      FileTypeFilter = [new FilePickerFileType("Flight logs") { Patterns = ["*.bin", "*.log", "*.tlog"] }],
    });
    if (_closed) { return; }
    foreach (var file in files) {
      string? path = file.TryGetLocalPath();
      if (path != null) {
        var info = Hub.Logs.Attach(path);
        Refresh(); _localLogs.SelectedItem = info;
        Output($"Attached {info.Name}; log ID {info.Id}\n");
      }
    }
  }

  private async Task CopyAsync() {
    var server = Hub.SessionServer ?? Hub.DesktopServer ?? throw new InvalidOperationException("Open a port first in Connections.");
    if (Clipboard != null) {
      string configuration = "[mcp_servers.missionplanner]\nurl = " + JsonSerializer.Serialize(server.Endpoint!.AbsoluteUri)
          + (server.RequiresToken ? "\nhttp_headers = { Authorization = \"Bearer " + server.Token + "\" }" : "") + "\ntool_timeout_sec = 720\n";
      await Clipboard.SetTextAsync(configuration);
      _status.Text = "Copied connection settings. Stop all connections revokes current access.";
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
    McpAgent agent;
    if (_detectedAgents.SelectedItem is McpAgent { IsDesktop: true } desktop) { agent = desktop; }
    else {
      string executable = _executable.Text?.Trim() ?? "";
      if (!File.Exists(executable)) { throw new InvalidOperationException("Find agents or select an installed executable first."); }
      agent = new McpAgent(_agentKind.SelectedIndex == 1 ? McpAgentKind.ClaudeCode : McpAgentKind.CodexCli,
          (_detectedAgents.SelectedItem as McpAgent)?.Name ?? "Terminal agent", executable, []);
    }
    _status.Text = $"Launching {agent.Name}…";
    await Hub.LaunchAsync(agent, _workingDirectory.Text ?? "", _task.Text ?? "");
    Refresh(); RefreshRegistration();
    _status.Text = agent.IsDesktop
        ? $"{agent.Name} launched; its sessions on port {Hub.DesktopPort} are allowed. You can close this window."
        : $"{agent.Name} launched in a terminal with full access. You can close this window.";
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
    var cancellation = Hub.LocalOperationToken;
    var target = Hub.Vehicles.Resolve(proposal.TargetId, true);
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
      try { _status.Text = await McpProposalWriter.ApplyAsync(Hub.Vehicles, proposal, cancellation); }
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
