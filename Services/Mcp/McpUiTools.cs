using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MissionPlanner.Services.Mcp;

[McpServerToolType]
internal sealed class McpUiTools(McpUiHost? host, Func<McpServer, McpConnectionSession> session) {
  private McpUiHost Ui => host ?? throw new McpException("UI unavailable in this server instance.");
  private static string Json(object value) => JsonSerializer.Serialize(value, MissionPlannerMcpTools.JsonOptions);
  private static async Task<string> Read(Func<Task<object>> action) {
    try { return Json(await action().ConfigureAwait(false)); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.IOException) { throw new McpException(e.Message); }
  }
  private async Task<CallToolResult> Change(McpServer server, string operationId, string method, object args, Func<Task<object>> action, CancellationToken ct) {
    try {
      var result = await session(server).Operations.RunAsync(operationId, method, args, () => Ui.Mutate(action, ct), ct).ConfigureAwait(false);
      return new() { IsError = result.Status is not ("completed" or "running"), Content = [new TextContentBlock { Text = Json(result) }] };
    } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new McpException(e.Message); }
  }
  [McpServerTool(Name = "ui_get_state", ReadOnly = true), Description("Inspect active screen, capturable surfaces, MCP log windows/revisions, live tuning fields and mission-draft revision. Requires session Allow.")]
  public Task<string> State(CancellationToken ct) => Read(() => Ui.State(ct));
  [McpServerTool(Name = "ui_navigate", ReadOnly = false, Destructive = false), Description("Navigate to DATA, PLAN or HELP using native UI. Requires operationId; no aircraft or configuration actions.")]
  public Task<CallToolResult> Navigate(McpServer server, string operationId, string route, CancellationToken ct) => Change(server, operationId, "navigate", new { route }, () => Ui.Navigate(route, ct), ct);
  [McpServerTool(Name = "ui_inspect", ReadOnly = true), Description("Inspect registered diagnostic controls in MCP-opened log views. Returns connection-local snapshotId/controlId, supported actions, current values and geometry. Invalid after changes or revoke. Does not inspect passwords, consent, aircraft controls or arbitrary windows.")]
  public Task<string> Inspect(McpServer server, CancellationToken ct) => Read(() => Ui.Inspect(session(server), ct));
  [McpServerTool(Name = "ui_invoke", ReadOnly = false, Destructive = false), Description("Invoke an allowed diagnostic action from the latest ui_inspect snapshot. Currently Clear graph. Checks identity, window, revision, geometry and enabled/visible state; invalidates snapshot.")]
  public Task<CallToolResult> Invoke(McpServer server, string operationId, string snapshotId, string controlId, CancellationToken ct) => Change(server, operationId, "invoke", new { snapshotId, controlId }, () => Ui.Invoke(session(server), snapshotId, controlId, ct), ct);
  [McpServerTool(Name = "ui_set_value", ReadOnly = false, Destructive = false), Description("Set an inspected diagnostic control: numeric native graph scale/offset or boolean map visibility. JSON value must match control type. Typed semantic action; no desktop-global input.")]
  public Task<CallToolResult> SetValue(McpServer server, string operationId, string snapshotId, string controlId, JsonElement value, CancellationToken ct) => Change(server, operationId, "set_value", new { snapshotId, controlId, value }, () => Ui.SetValue(session(server), snapshotId, controlId, value, ct), ct);
  [McpServerTool(Name = "ui_capture", ReadOnly = true), Description("Return a PNG MCP image of a visible map or graph surface from ui_get_state. Maximum 1600×1200 and 2 MiB. Captures only that surface, never the desktop, consent or configuration controls.")]
  public async Task<CallToolResult> Capture(string surfaceId, CancellationToken ct, int maxWidth = 1200, int maxHeight = 800) {
    try {
      var image = await Ui.Capture(surfaceId, maxWidth, maxHeight, ct);
      return new() { Content = [new TextContentBlock { Text = Json(new { surfaceId, image.Width, image.Height, mimeType = "image/png" }) }, ImageContentBlock.FromBytes(image.Png, "image/png")] };
    } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new McpException(e.Message); }
  }
  [McpServerTool(Name = "ui_set_map_view", ReadOnly = false, Destructive = false), Description("Center a visible map by WGS84 latitude/longitude and zoom 3..20; disables auto-pan. Changes display only, never vehicle position or mission points.")]
  public Task<CallToolResult> Map(McpServer server, string operationId, string surfaceId, double latitude, double longitude, int zoom, CancellationToken ct) => Change(server, operationId, "map", new { surfaceId, latitude, longitude, zoom }, () => Ui.SetMap(surfaceId, latitude, longitude, zoom, ct), ct);
  [McpServerTool(Name = "ui_set_tuning_fields", ReadOnly = false, Destructive = false), Description("Set 1..12 distinct scalar telemetry field names for the live tuning plot and its visibility. Uses current UI vehicle/display units; no stream requests or target switching.")]
  public Task<CallToolResult> Tuning(McpServer server, string operationId, string[] fields, bool enabled, CancellationToken ct) => Change(server, operationId, "tuning", new { fields, enabled }, () => Ui.SetTuning(fields, enabled, ct), ct);
  [McpServerTool(Name = "ui_open_log", ReadOnly = false, Destructive = false), Description("Open a catalogue logId in the native Log Browser; return viewId and revision for plotting. Reuses an already open view. At most eight MCP log views. No arbitrary filesystem paths.")]
  public Task<CallToolResult> OpenLog(McpServer server, string operationId, string logId, CancellationToken ct) => Change(server, operationId, "open_log", new { logId }, () => Ui.OpenLog(logId, ct), ct);
  [McpServerTool(Name = "ui_plot_log", ReadOnly = false, Destructive = false), Description("Replace an MCP log view graph with 1..8 original scalar fields from log_schema, explicit time window and expectedRevision. Up to 32768 points/field; instance must identify one sensor or TLOG system:component. Units are native/physical. Read state again if operator changed graph.")]
  public Task<CallToolResult> PlotLog(McpServer server, string operationId, string viewId, string expectedRevision, McpPlotField[] fields, double startSeconds, double endSeconds, CancellationToken ct) => Change(server, operationId, "plot_log", new { viewId, expectedRevision, fields, startSeconds, endSeconds }, () => Ui.PlotLog(viewId, expectedRevision, fields, startSeconds, endSeconds, ct), ct);
  [McpServerTool(Name = "ui_close_log", ReadOnly = false, Destructive = false), Description("Close one MCP-opened log window by viewId. Does not delete logs or close other application windows.")]
  public Task<CallToolResult> CloseLog(McpServer server, string operationId, string viewId, CancellationToken ct) => Change(server, operationId, "close_log", new { viewId }, () => Ui.CloseLog(viewId, ct), ct);
  [McpServerTool(Name = "ui_operation_status", ReadOnly = true), Description("Read a connection-local mutation receipt by operationId. Recover a lost response before retrying. Receipts survive revoke/allow but not disconnect. unknown_operation is not evidence that a different connection did nothing.")]
  public string Operation(McpServer server, string operationId) {
    try { return Json(session(server).Operations.Get(operationId)); } catch (ArgumentException e) { throw new McpException(e.Message); }
  }
  [McpServerTool(Name = "mission_draft_get", ReadOnly = true), Description("Page the UI Mission/Fence/Rally draft with content revision, home, explicit units and Undo availability. offset >= 0, count 1..200. This is not the onboard mission.")]
  public Task<string> Draft(CancellationToken ct, int offset = 0, int count = 200) => Read(() => Ui.Draft(offset, count, ct));
  [McpServerTool(Name = "mission_command_schema", ReadOnly = true), Description("Discover native mission command IDs/names and available parameter labels. A known ID is not aircraft support or permission to execute it. No command dispatch.")]
  public string Commands() => Json(new { commands = MissionCommandCatalog.Names().Select(name => { MissionCommandCatalog.TryGetId(name, out ushort id); return new { id, name, parameterLabels = MissionCommandCatalog.GetLabels(id) }; }), frames = new[] { new { id = 0, name = "GLOBAL", altitude = "metres AMSL" }, new { id = 3, name = "GLOBAL_RELATIVE_ALT", altitude = "metres above home" }, new { id = 10, name = "GLOBAL_TERRAIN_ALT", altitude = "metres above terrain" } } });
  [McpServerTool(Name = "mission_draft_validate", ReadOnly = true), Description("Structurally validate up to 1000 proposed Mission items and home. Finite coordinates/parameters, known commands, supported frames and DO_JUMP targets. Does not establish flight feasibility, terrain clearance or aircraft compatibility; never modifies or uploads.")]
  public string Validate(McpDraftHome home, McpDraftItem[] items) {
    try { return Json(McpMissionDraft.Validate(home, items)); } catch (ArgumentException e) { throw new McpException(e.Message); }
  }
  [McpServerTool(Name = "mission_draft_replace", ReadOnly = false, Destructive = false), Description("Replace the local Mission draft/home as one native Undo group, only if expectedRevision still matches. Requires Allow and operationId. Fence/Rally editing excluded. Does not upload; operator reviews and uploads separately.")]
  public Task<CallToolResult> Replace(McpServer server, string operationId, string expectedRevision, McpDraftHome home, McpDraftItem[] items, CancellationToken ct) => Change(server, operationId, "draft_replace", new { expectedRevision, home, items }, () => Ui.ReplaceDraft(expectedRevision, home, items, ct), ct);
  [McpServerTool(Name = "mission_draft_undo", ReadOnly = false, Destructive = false), Description("Undo one native planner history entry when Mission is selected and expectedRevision matches. May undo the latest operator edit; inspect the draft first. Does not upload.")]
  public Task<CallToolResult> Undo(McpServer server, string operationId, string expectedRevision, CancellationToken ct) => Change(server, operationId, "draft_undo", new { expectedRevision }, () => Ui.UndoDraft(expectedRevision, ct), ct);
}
