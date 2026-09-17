using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mapsui.Projections;
using Mapsui.UI.Avalonia;
using MissionPlanner.Controls;
using MissionPlanner.ViewModels;
using MissionPlanner.Views;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpPlotField(string Type, string Field, string? Instance = null, bool RightAxis = false);
internal sealed record McpUiImage(byte[] Png, int Width, int Height);

/// <summary>Native UI adapters shared by both listeners. No desktop-global input or aircraft-command dispatch.</summary>
internal sealed partial class McpUiHost(MainWindowViewModel main, McpLogCatalog logs, Func<Window?> owner) {
  private long _uiEpoch;
  private readonly SemaphoreSlim _mutations = new(1, 1);
  private readonly Dictionary<string, LogView> _logViews = new();
  private sealed record LogView(string Id, string LogId, string Path, LogBrowseWindow Window) {
    internal LogBrowseView View => (LogBrowseView)Window.Content!;
    internal LogBrowseViewModel Vm => (LogBrowseViewModel)Window.DataContext!;
    internal LivePlot Plot => View.FindControl<LivePlot>("Plot")!;
    internal string Revision => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id + "|" + Vm.CurrentPath + "|" + Vm.LoadRevision + "|" + Plot.ContentRevision)));
  }
  internal static async Task<T> OnUi<T>(Func<T> action, CancellationToken ct) => await Dispatcher.UIThread.InvokeAsync(() => {
    ct.ThrowIfCancellationRequested(); return action();
  });
  internal async Task<object> Mutate(Func<Task<object>> action, CancellationToken ct) {
    if (!await _mutations.WaitAsync(0, ct).ConfigureAwait(false)) { throw new InvalidOperationException("ui_busy: another UI operation is still running."); }
    try { ct.ThrowIfCancellationRequested(); return await action().ConfigureAwait(false); }
    finally { Interlocked.Increment(ref _uiEpoch); _mutations.Release(); }
  }
  private void RequireMain() {
    if (owner() is { IsVisible: true, IsEnabled: false }) { throw new InvalidOperationException("modal_window: finish the operator dialog first."); }
  }
  internal Task<object> State(CancellationToken ct) => OnUi<object>(() => new {
    activeScreen = main.ActiveTab,
    routes = new[] { "DATA", "PLAN", "HELP" },
    scope = "Navigation, maps, live tuning graph, Mission draft and MCP-opened log views. Aircraft commands, configuration writes, consent controls and native dialogs are operator-only.",
    surfaces = Surfaces().Select(s => new { s.Id, visible = s.Root.IsEffectivelyVisible, enabled = s.Root.IsEffectivelyEnabled,
      capture = s.Capture, type = s.Root.GetType().Name }).ToArray(),
    logViews = _logViews.Values.Select(v => new { viewId = v.Id, logId = v.LogId, name = System.IO.Path.GetFileName(v.Path),
      busy = v.Vm.Busy, stale = v.Vm.CurrentPath != v.Path, revision = v.Revision, status = v.Vm.Status,
      series = v.Plot.SeriesLabels.ToArray() }).ToArray(),
    mission = new { revision = McpMissionDraft.Revision(main.FlightPlanner), type = main.FlightPlanner.MissionType, count = main.FlightPlanner.Waypoints.Count },
    tuning = new { enabled = main.FlightData.Tuning, fields = main.FlightData.TuningFieldList().Where(f => main.FlightData.IsTuningField(f.name)).Select(f => f.name).ToArray() },
  }, ct);

  internal Task<object> Navigate(string route, CancellationToken ct) => OnUi<object>(() => {
    RequireMain();
    if (route is not ("DATA" or "PLAN" or "HELP")) { throw new ArgumentException("Supported routes: DATA, PLAN, HELP."); }
    if (route == "HELP" && !main.ShowHelp) { throw new InvalidOperationException("Help is hidden by the display profile."); }
    main.NavigateCommand.Execute(route);
    return new { activeScreen = main.ActiveTab, completed = main.ActiveTab == route };
  }, ct);

  internal Task<object> Draft(int offset, int count, CancellationToken ct) => OnUi(() => McpMissionDraft.Read(main.FlightPlanner, offset, count), ct);
  internal Task<object> ReplaceDraft(string expected, McpDraftHome home, McpDraftItem[] items, CancellationToken ct) => OnUi(() => {
    RequireMain(); return McpMissionDraft.Replace(main.FlightPlanner, expected, home, items);
  }, ct);
  internal Task<object> UndoDraft(string expected, CancellationToken ct) => OnUi<object>(() => {
    RequireMain(); McpMissionDraft.RequireRevision(main.FlightPlanner, expected);
    if (!main.FlightPlanner.CanUndo) { throw new InvalidOperationException("No draft Undo entry is available."); }
    main.FlightPlanner.UndoCommand.Execute(null);
    return new { revision = McpMissionDraft.Revision(main.FlightPlanner), uploaded = false };
  }, ct);

  internal Task<object> SetMap(string surfaceId, double latitude, double longitude, int zoom, CancellationToken ct) => OnUi<object>(() => {
    RequireMain();
    if (!double.IsFinite(latitude) || latitude is < -85 or > 85 || !double.IsFinite(longitude) || longitude is < -180 or > 180 || zoom is < 3 or > 20) {
      throw new ArgumentException("Map needs latitude -85..85, longitude -180..180, zoom 3..20.");
    }
    var surface = SurfaceById(surfaceId); RequireVisible(surface.Root);
    if (surface.Root is not MapControl map) { throw new ArgumentException("Select a map surface from ui_get_state."); }
    if (map is MapView view) { view.AutoPan = false; if (surfaceId == "flight-map") { main.FlightData.AutoPan = false; } view.CenterOn(latitude, longitude); view.SetZoomLevel(zoom); }
    else if (map is FlightPlannerMap planner) { planner.CenterOnAndZoom(latitude, longitude, zoom); }
    var current = map.Map.Navigator.Viewport;
    var (lng, lat) = SphericalMercator.ToLonLat(current.CenterX, current.CenterY);
    return new { latitude = lat, longitude = lng, zoom = MapView.ZoomLevelForResolution(current.Resolution), autoPan = false };
  }, ct);

  internal Task<object> SetTuning(string[] fields, bool enabled, CancellationToken ct) => OnUi<object>(() => {
    RequireMain();
    if (fields == null || fields.Length is < 1 or > 12 || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length
        || fields.Any(f => !main.FlightData.TuningFieldList().Any(available => available.name == f))) {
      throw new ArgumentException("Choose 1..12 distinct scalar names from telemetry_schema.");
    }
    main.FlightData.SetTuningFields(fields); main.FlightData.Tuning = enabled;
    return new { enabled, fields, timeWindowSeconds = FlightDataViewModel.TuningWindowSeconds, note = "Shows the currently selected UI vehicle; does not request streams or switch aircraft." };
  }, ct);

  internal async Task<object> OpenLog(string logId, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();
    string path = logs.PathFor(logId);
    var pending = await OnUi(async () => {
      RequireMain();
      var existing = _logViews.Values.FirstOrDefault(v => v.LogId == logId && v.Vm.CurrentPath == path);
      if (existing != null) { existing.Window.Activate(); return (object)new { viewId = existing.Id, logId, revision = existing.Revision }; }
      if (_logViews.Count >= 8) { throw new InvalidOperationException("At most eight MCP log views; close one with ui_close_log."); }
      var window = new LogBrowseWindow();
      var view = new LogView("log-" + Guid.NewGuid().ToString("N"), logId, path, window);
      try {
        await view.Vm.LoadFileAsync(path, ct);
        ct.ThrowIfCancellationRequested(); RequireMain();
        if (view.Vm.CurrentPath == null) { throw new InvalidDataException(view.Vm.Info); }
        _logViews.Add(view.Id, view);
        window.Closed += (_, _) => _logViews.Remove(view.Id);
        if (owner() is { } parent) { window.Show(parent); } else { window.Show(); }
        return (object)new { viewId = view.Id, logId, revision = view.Revision };
      } catch { window.Close(); throw; }
    }, ct);
    return await pending;
  }
  internal async Task OpenPath(string path, CancellationToken ct) { await Mutate(() => OpenLog(logs.Attach(path).Id, ct), ct); }
  private LogView RequireLog(string id, string? expected = null) {
    if (!_logViews.TryGetValue(id, out var view) || !view.Window.IsVisible || view.Vm.CurrentPath != view.Path) { throw new InvalidOperationException("stale_view: open the log again using its catalogue ID."); }
    RequireVisible(view.View);
    if (view.Vm.Busy) { throw new InvalidOperationException("ui_busy: the log view is loading or graphing."); }
    if (expected != null && expected != view.Revision) { throw new InvalidOperationException("stale_revision: read ui_get_state before changing this graph."); }
    return view;
  }
  internal Task<object> CloseLog(string id, CancellationToken ct) => OnUi<object>(() => {
    var view = RequireLog(id); view.Window.Close(); return new { viewId = id, closed = true };
  }, ct);

  internal async Task<object> PlotLog(string id, string expected, McpPlotField[] fields, double start, double end, CancellationToken ct) {
    McpLogCatalog.ValidateWindow(start, end);
    if (fields == null || fields.Length is < 1 or > 8 || fields.Any(f => f == null || string.IsNullOrWhiteSpace(f.Type) || string.IsNullOrWhiteSpace(f.Field)
        || f.Type.Length > 100 || f.Field.Length > 100 || f.Instance?.Length > 100)) { throw new ArgumentException("Choose 1..8 log fields from log_schema."); }
    var view = await OnUi(() => RequireLog(id, expected), ct);
    var curves = new List<List<DiagnosticSample>>();
    foreach (var field in fields) {
      List<DiagnosticSample> values;
      if (logs.IsTelemetry(view.LogId)) {
        values = (List<DiagnosticSample>)await logs.ReadFile(view.LogId, (path, token) => {
          var data = new List<DiagnosticSample>(); var instances = new HashSet<string>();
          foreach (var row in McpTelemetryLog.Select(path, field.Type, start, end, field.Instance, token)) {
            instances.Add(row.Instance);
            if (instances.Count > 1) { throw new ArgumentException("Specify TLOG systemId:componentId to keep sources separate."); }
            var info = McpTelemetryLog.Fields(row.Packet).FirstOrDefault(f => f.Name == field.Field)
                ?? throw new ArgumentException("Unknown log field; read log_schema.");
            object? raw = info.GetValue(row.Packet.data);
            if (raw is null or Array) { throw new ArgumentException("Choose a scalar numeric field."); }
            double value = McpLogCatalog.Number(McpTelemetryLog.Text(raw));
            if (!double.IsFinite(value)) { continue; }
            if (data.Count >= 32768) { throw new ArgumentException("More than 32768 points; narrow the time window."); }
            data.Add(new(row.TimeSeconds, value));
          }
          return data;
        }, ct).ConfigureAwait(false);
      } else {
        values = (List<DiagnosticSample>)await logs.Read(view.LogId, (log, token) =>
            McpLogCatalog.Series(log, field.Type, [field.Field], start, end, field.Instance, token)[0], ct).ConfigureAwait(false);
      }
      if (values.Count == 0) { throw new ArgumentException("No numeric samples in the selected field/window."); }
      curves.Add(values);
    }
    return await OnUi<object>(() => {
      RequireLog(id, expected); // Native user edits during parsing win; never overwrite a changed graph.
      view.Plot.ClearAll();
      for (int i = 0; i < fields.Length; i++) {
        var f = fields[i]; string label = $"{i + 1}: {f.Type}[{f.Instance ?? "single source"}].{f.Field}";
        view.Plot.SetSeries(label, curves[i].Select(p => p.TimeSeconds).ToArray(), curves[i].Select(p => p.Value).ToArray(), rightAxis: f.RightAxis);
      }
      view.Plot.SetAxisLabels("Time (s)", "Value", "MCP log analysis");
      view.Plot.Plot.Axes.AutoScale(); view.Plot.Refresh();
      view.Vm.Status = $"MCP plotted {fields.Length} field(s), {start}–{end} s.";
      return new { viewId = id, revision = view.Revision, series = view.Plot.SeriesLabels, samples = curves.Select(c => c.Count).ToArray(),
        units = logs.IsTelemetry(view.LogId) ? "Native MAVLink field units; TLOG elapsed receipt seconds." : "DataFlash physical field multipliers; boot/sample seconds." };
    }, ct);
  }

  internal Task<McpUiImage> Capture(string surfaceId, int maxWidth, int maxHeight, CancellationToken ct) => OnUi(() => {
    if (maxWidth is < 64 or > 1600 || maxHeight is < 64 or > 1200) { throw new ArgumentException("Capture bounds: width 64..1600, height 64..1200."); }
    var surface = SurfaceById(surfaceId);
    if (!surface.Capture) { throw new ArgumentException("Capture supports map, live tuning and log plot surfaces only."); }
    RequireVisible(surface.Root);
    double scale = Math.Min(1, Math.Min(maxWidth / surface.Root.Bounds.Width, maxHeight / surface.Root.Bounds.Height));
    int width = Math.Max(1, (int)Math.Ceiling(surface.Root.Bounds.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(surface.Root.Bounds.Height * scale));
    using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale));
    bitmap.Render(surface.Root);
    using var stream = new MemoryStream(); bitmap.Save(stream);
    if (stream.Length > 2 * 1024 * 1024) { throw new InvalidOperationException("Capture exceeded 2 MiB; choose smaller dimensions."); }
    return new McpUiImage(stream.ToArray(), width, height);
  }, ct);
}
