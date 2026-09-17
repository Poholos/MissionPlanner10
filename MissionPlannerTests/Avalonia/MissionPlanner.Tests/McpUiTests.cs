using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner.Controls;
using MissionPlanner.Services.Mcp;
using MissionPlanner.ViewModels;
using MissionPlanner.Views;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MissionPlanner.Tests;

public sealed class McpUiTests {
  private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, MissionPlannerMcpTools.JsonOptions);
  private static JsonElement Data(CallToolResult value) => JsonDocument.Parse(Assert.IsType<TextContentBlock>(value.Content[0]).Text).RootElement.Clone();

  [Fact]
  public async Task Operation_journal_reserves_before_dispatch_and_recovers_without_duplicate_mutation() {
    var journal = new McpOperationJournal();
    var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    int calls = 0;
    Task<object> Change() { calls++; return release.Task; }
    var first = journal.RunAsync("op-1", "change", new { n = 1 }, Change, default);
    Assert.Equal("running", journal.Get("op-1").Status);
    Assert.Equal("running", (await journal.RunAsync("op-1", "change", new { n = 1 }, Change, default)).Status);
    await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RunAsync("op-1", "change", new { n = 2 }, Change, default));
    release.SetResult(new { changed = true });
    var receipt = await first;
    Assert.Same(receipt, await journal.RunAsync("op-1", "change", new { n = 1 }, Change, default));
    Assert.Equal(1, calls); Assert.Equal("completed", receipt.Status);
    Assert.Equal("unknown_operation", journal.Get("other").Status);
  }
  [Fact]
  public async Task Operation_journal_keeps_cancelled_receipts_and_rejects_capacity_without_eviction() {
    var journal = new McpOperationJournal(); int calls = 0;
    var cancelled = await journal.RunAsync("cancel", "change", new { }, () => { calls++; throw new OperationCanceledException(); }, default);
    Assert.Equal("cancelled", cancelled.Status);
    Assert.Same(cancelled, await journal.RunAsync("cancel", "change", new { }, () => { calls++; return Task.FromResult<object>(new { }); }, default));
    Assert.Equal(1, calls);
    for (int i = 1; i < McpOperationJournal.Capacity; i++) { await journal.RunAsync("op" + i, "change", new { }, () => Task.FromResult<object>(new { }), default); }
    await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RunAsync("overflow", "change", new { }, () => Task.FromResult<object>(new { }), default));
    Assert.Same(cancelled, journal.Get("cancel"));
    using var stop = new CancellationTokenSource(); stop.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new McpOperationJournal().RunAsync("stop", "change", new { }, () => throw new Exception("must not dispatch"), stop.Token));
  }
  [Theory]
  [InlineData(0, 0, 16, 3, true)]
  [InlineData(91, 0, 16, 3, false)]
  [InlineData(0, -181, 16, 3, false)]
  [InlineData(0, 0, 65535, 3, false)]
  [InlineData(0, 0, 16, 1, false)]
  public void Draft_validation_uses_known_commands_and_explicit_global_frames(double lat, double lng, int command, int frame, bool valid) {
    var result = McpMissionDraft.Validate(new(0, 0, 5), [new((ushort)command, (byte)frame, lat, lng, 40)]);
    Assert.Equal(valid, result.Valid); Assert.Contains(result.Warnings, text => text.Contains("Structural checks only"));
  }
  [Fact]
  public void Draft_validation_rejects_nonfinite_and_bad_jump_targets() {
    Assert.False(McpMissionDraft.Validate(new(0, 0, 0), [new(16, 3, 0, 0, double.NaN)]).Valid);
    Assert.False(McpMissionDraft.Validate(new(0, 0, 0), [new(177, 3, 0, 0, 0, P1: 2, P2: 1)]).Valid);
    Assert.False(McpMissionDraft.Validate(new(0, 0, 0), [null!]).Valid);
    Assert.Throws<ArgumentNullException>(() => McpMissionDraft.Validate(null!, []));
  }
  [AvaloniaFact]
  public void Draft_replace_has_compare_and_swap_and_one_native_undo_including_home() {
    var vm = new FlightPlannerViewModel { VerifyHeight = false };
    var priorPlannedHome = AppState.comPort.MAV.cs.PlannedHomeLocation;
    try {
      vm.AddWaypointAt(34, 33); vm.MissionType = "Fence"; vm.AddWaypointAt(35, 32); vm.MissionType = "Mission";
      vm.HomeLat = 34; vm.HomeLng = 33; vm.HomeAlt = 5;
      string original = McpMissionDraft.Revision(vm);
      var result = Data(McpMissionDraft.Replace(vm, original, new(40, 41, 6), [new(16, 3, 40.1, 41.1, 50), new(16, 3, 40.2, 41.2, 60)]));
      Assert.False(result.GetProperty("uploaded").GetBoolean()); Assert.Equal(2, vm.Waypoints.Count);
      Assert.Throws<InvalidOperationException>(() => McpMissionDraft.Replace(vm, original, new(0, 0, 0), []));
      vm.UndoCommand.Execute(null);
      Assert.Equal(original, McpMissionDraft.Revision(vm)); Assert.Single(vm.Waypoints);
      vm.MissionType = "Fence"; Assert.Single(vm.Waypoints);
      Assert.Throws<InvalidOperationException>(() => McpMissionDraft.Replace(vm, McpMissionDraft.Revision(vm), new(0, 0, 0), []));
      var page = Data(McpMissionDraft.Read(vm, int.MaxValue, 200));
      Assert.True(page.GetProperty("complete").GetBoolean()); Assert.Empty(page.GetProperty("items").EnumerateArray());
    } finally { AppState.comPort.MAV.cs.PlannedHomeLocation = priorPlannedHome; }
  }
  [AvaloniaFact]
  public async Task Ui_serialization_has_no_late_queue_and_cancellation_prevents_dispatch() {
    using var logs = new McpLogCatalog(); var host = new McpUiHost(null!, logs, () => null);
    var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = host.Mutate(() => release.Task, default);
    await Assert.ThrowsAsync<InvalidOperationException>(() => host.Mutate(() => throw new Exception("must not dispatch"), default));
    release.SetResult(new { }); await first;
    using var stop = new CancellationTokenSource(); stop.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Navigate("PLAN", stop.Token));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.OpenLog("invalid", stop.Token));
  }
  [AvaloniaFact]
  public async Task Log_ui_uses_native_plot_and_rejects_stale_hidden_revoked_and_cross_session_controls() {
    string path = McpServerTests.TemporaryLog();
    using var logs = new McpLogCatalog(); var host = new McpUiHost(null!, logs, () => null);
    var session = new McpConnectionSession(null!, "", "test", true, true);
    string? viewId = null;
    try {
      string logId = logs.Attach(path).Id;
      var opened = Data(await host.Mutate(() => host.OpenLog(logId, default), default));
      viewId = opened.GetProperty("viewId").GetString()!;
      string revision = opened.GetProperty("revision").GetString()!;
      Assert.DoesNotContain(path, revision);
      var plotted = Data(await host.Mutate(() => host.PlotLog(viewId, revision, [new("PARM", "Value")], 0, 3, default), default));
      Assert.Equal(2, plotted.GetProperty("samples")[0].GetInt32());
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.PlotLog(viewId, revision, [new("PARM", "Value")], 0, 3, default));
      Dispatcher.UIThread.RunJobs();
      var inspected = Data(await host.Inspect(session, default));
      var targets = session.UiSnapshot!.Targets;
      Assert.All(targets, t => Assert.Contains(t.Name, new[] { "ClearBtn", "ScaleBox", "OffsetBox", "MapToggle" }));
      var scale = Assert.Single(targets, t => t.Name == "ScaleBox");
      string snapshot = session.UiSnapshot.Id;
      var otherSession = new McpConnectionSession(null!, "", "test", true, true);
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetValue(otherSession, snapshot, scale.Id, Data(2), default));
      await host.Mutate(() => host.SetValue(session, snapshot, scale.Id, Data(2), default), default);
      Assert.Equal(2, ((NumericUpDown)scale.Control).Value);
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetValue(session, snapshot, scale.Id, Data(3), default));
      await host.Inspect(session, default); snapshot = session.UiSnapshot!.Id;
      var clear = session.UiSnapshot.Targets.Single(t => t.Name == "ClearBtn");
      clear.Control.IsEnabled = false;
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke(session, snapshot, clear.Id, default));
      clear.Control.IsEnabled = true;
      await host.Inspect(session, default); snapshot = session.UiSnapshot!.Id;
      clear = session.UiSnapshot.Targets.Single(t => t.Name == "ClearBtn");
      session.Revoke();
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke(session, snapshot, clear.Id, default));
      var fresh = new McpConnectionSession(null!, "", "test", true, true);
      await host.Inspect(fresh, default); snapshot = fresh.UiSnapshot!.Id;
      clear = fresh.UiSnapshot.Targets.Single(t => t.Name == "ClearBtn");
      await host.Mutate(() => host.Invoke(fresh, snapshot, clear.Id, default), default);
      var window = (LogBrowseWindow)TopLevel.GetTopLevel(clear.Control)!;
      var plot = ((LogBrowseView)window.Content!).FindControl<LivePlot>("Plot")!;
      Assert.Empty(plot.SeriesLabels);
      await Assert.ThrowsAsync<ArgumentException>(() => host.Capture(viewId + "/plot", 2000, 800, default));
      await Assert.ThrowsAsync<ArgumentException>(() => host.Capture("consent", 800, 600, default));
      await host.Inspect(fresh, default); snapshot = fresh.UiSnapshot!.Id;
      clear = fresh.UiSnapshot.Targets.Single(t => t.Name == "ClearBtn");
      await ((LogBrowseViewModel)window.DataContext!).LoadFileAsync(path);
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke(fresh, snapshot, clear.Id, default));
      string other = McpServerTests.TemporaryLog();
      try {
        await ((LogBrowseViewModel)window.DataContext!).LoadFileAsync(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Capture(viewId + "/plot", 800, 600, default));
      } finally { window.Close(); viewId = null; File.Delete(other); }
    } finally { if (viewId != null) { await host.CloseLog(viewId, default); } File.Delete(path); }
  }

  [AvaloniaFact]
  public async Task Real_http_ui_mutation_requires_allow_replays_once_and_survives_revoke_allow() {
    var main = new MainWindowViewModel();
    var previousHome = AppState.comPort.MAV.cs.PlannedHomeLocation;
    using var logs = new McpLogCatalog(); var host = new McpUiHost(main, logs, () => null);
    await using var server = new MissionPlannerMcpServer(new(() => []), logs) { UiHost = host };
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp }, http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    try {
      var denied = await client.CallToolAsync("ui_get_state", cancellationToken: timeout.Token);
      Assert.True(denied.IsError); Assert.Contains("permission_required", Assert.IsType<TextContentBlock>(denied.Content[0]).Text);
      string id = Assert.Single(server.Sessions).Id; server.AllowSession(id);
      var draft = Data(await client.CallToolAsync("mission_draft_get", cancellationToken: timeout.Token));
      var args = new Dictionary<string, object?> { ["operationId"] = "replace-1", ["expectedRevision"] = draft.GetProperty("revision").GetString(),
        ["home"] = new { latitude = 34, longitude = 33, altitudeMetres = 10 }, ["items"] = new[] { new { command = 16, frame = 3, latitude = 34.1, longitude = 33.1, altitudeMetres = 50 } } };
      var changed = await client.CallToolAsync("mission_draft_replace", args, cancellationToken: timeout.Token);
      Assert.NotEqual(true, changed.IsError); Assert.Equal("completed", Data(changed).GetProperty("status").GetString());
      string changedRevision = McpMissionDraft.Revision(main.FlightPlanner);
      var replay = await client.CallToolAsync("mission_draft_replace", args, cancellationToken: timeout.Token);
      Assert.Equal(Data(changed).ToString(), Data(replay).ToString());
      Assert.Single(main.FlightPlanner.Waypoints);
      server.RevokeSession(id);
      Assert.True((await client.CallToolAsync("mission_draft_replace", args, cancellationToken: timeout.Token)).IsError);
      server.AllowSession(id);
      var status = Data(await client.CallToolAsync("ui_operation_status", new Dictionary<string, object?> { ["operationId"] = "replace-1" }, cancellationToken: timeout.Token));
      Assert.Equal("completed", status.GetProperty("status").GetString());
      var undo = await client.CallToolAsync("mission_draft_undo", new Dictionary<string, object?> { ["operationId"] = "undo-1", ["expectedRevision"] = changedRevision }, cancellationToken: timeout.Token);
      Assert.NotEqual(true, undo.IsError); Assert.Equal(draft.GetProperty("revision").GetString(), McpMissionDraft.Revision(main.FlightPlanner));
      var state = Data(await client.CallToolAsync("ui_get_state", cancellationToken: timeout.Token));
      Assert.Equal("DATA", state.GetProperty("activeScreen").GetString());
    } finally { AppState.comPort.MAV.cs.PlannedHomeLocation = previousHome; }
  }

  [Fact]
  public async Task Official_http_client_reads_embedded_docs_and_discovers_ui_schemas_without_server_internals() {
    await using var server = new MissionPlannerMcpServer(new(() => []));
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.IssueLaunchToken());
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp }, http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    Assert.Equal(46, tools.Count);
    var mutation = tools.Single(t => t.Name == "mission_draft_replace");
    Assert.DoesNotContain("server", mutation.JsonSchema.ToString());
    Assert.Contains("expectedRevision", mutation.JsonSchema.ToString());
    var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);
    Assert.Equal(3, resources.Count);
    var document = await client.ReadResourceAsync(McpDocumentation.StartUri, cancellationToken: timeout.Token);
    Assert.Contains("ui_operation_status", Assert.IsType<TextResourceContents>(Assert.Single(document.Contents)).Text);
    foreach (string uri in new[] { "missionplanner://documentation/../../etc/passwd", "missionplanner://documentation/AI_START.md?file=secret", "file:///etc/passwd" }) {
      await Assert.ThrowsAnyAsync<McpException>(async () => { await client.ReadResourceAsync(uri, cancellationToken: timeout.Token); });
    }
    var validation = await client.CallToolAsync("mission_draft_validate", new Dictionary<string, object?> { ["home"] = new { latitude = 0, longitude = 0, altitudeMetres = 0 }, ["items"] = Array.Empty<object>() }, cancellationToken: timeout.Token);
    Assert.True(Data(validation).GetProperty("valid").GetBoolean());
    var unavailable = await client.CallToolAsync("ui_get_state", cancellationToken: timeout.Token);
    Assert.True(unavailable.IsError); Assert.Contains("UI unavailable", Assert.IsType<TextContentBlock>(unavailable.Content[0]).Text);
    foreach (string name in new[] { "ui_get_state", "ui_capture", "ui_inspect", "ui_operation_status", "mission_draft_replace", "mission_draft_undo", "future_unknown_tool" }) { Assert.False(MissionPlannerMcpServer.IsPassiveTool(name)); }
    foreach (string name in new[] { "mission_draft_get", "mission_command_schema", "mission_draft_validate" }) { Assert.True(MissionPlannerMcpServer.IsPassiveTool(name)); }
  }
}
