using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpUiTarget(string Id, string ViewId, string Name, Control Control, Rect Bounds, string Value, string Revision);
internal sealed record McpUiSnapshot(string Id, long AccessEpoch, long UiEpoch, McpUiTarget[] Targets);

internal sealed partial class McpUiHost {
  private sealed record Surface(string Id, Control Root, bool Capture);
  private IEnumerable<Surface> Surfaces() {
    if (owner() is { } window) {
      foreach (var control in window.GetVisualDescendants().OfType<Control>()) {
        string? id = control.Name switch { "FdMap" => "flight-map", "Map" when control is MissionPlanner.Controls.FlightPlannerMap => "planner-map", "TuningPlot" => "live-plot", _ => null };
        if (id != null) { yield return new(id, control, true); }
      }
    }
    foreach (var view in _logViews.Values) {
      yield return new(view.Id + "/plot", view.Plot, true);
      if (view.View.FindControl<Control>("TrackMap") is { } map) { yield return new(view.Id + "/map", map, true); }
    }
  }
  private Surface SurfaceById(string id) {
    var surface = Surfaces().FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown surface; read ui_get_state.");
    var log = _logViews.Values.FirstOrDefault(v => id.StartsWith(v.Id + "/", StringComparison.Ordinal));
    if (log != null) { RequireLog(log.Id); }
    return surface;
  }
  private static void RequireVisible(Control control) {
    if (!control.IsEffectivelyVisible || !control.IsEffectivelyEnabled || control.Bounds.Width <= 0 || control.Bounds.Height <= 0
        || TopLevel.GetTopLevel(control) is not Window { IsVisible: true, IsEnabled: true }) {
      throw new InvalidOperationException("unavailable_control: target is hidden, disabled, detached or blocked by a dialog.");
    }
  }
  private static Rect BoundsOf(Control control) {
    var window = TopLevel.GetTopLevel(control)!;
    return new Rect(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);
  }
  private static string ValueOf(Control control) => control switch {
    NumericUpDown number => number.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
    ToggleButton toggle => toggle.IsChecked == true ? "true" : "false",
    _ => "",
  };
  internal Task<object> Inspect(McpConnectionSession session, CancellationToken ct) => OnUi<object>(() => {
    RequireMain();
    long epoch = session.AccessEpoch;
    var targets = new List<McpUiTarget>();
    foreach (var view in _logViews.Values.Where(v => !v.Vm.Busy && v.Vm.CurrentPath == v.Path)) {
      foreach (string name in new[] { "ClearBtn", "ScaleBox", "OffsetBox", "MapToggle" }) {
        if (view.View.FindControl<Control>(name) is not { } control || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled) { continue; }
        RequireVisible(control);
        targets.Add(new("control-" + Guid.NewGuid().ToString("N"), view.Id, name, control, BoundsOf(control), ValueOf(control), view.Revision));
      }
    }
    var snapshot = new McpUiSnapshot(Guid.NewGuid().ToString("N"), epoch, Interlocked.Read(ref _uiEpoch), targets.ToArray());
    session.UiSnapshot = snapshot;
    return new { snapshotId = snapshot.Id, complete = true, scope = "Registered diagnostic controls in MCP-opened log views only. Use typed tools for navigation, maps, plots and mission drafts.",
      controls = targets.Select(t => new { controlId = t.Id, viewId = t.ViewId, name = t.Name, type = t.Control.GetType().Name,
        action = t.Control is NumericUpDown or ToggleButton ? "set_value" : "invoke", value = t.Value,
        bounds = new { t.Bounds.X, t.Bounds.Y, t.Bounds.Width, t.Bounds.Height } }).ToArray() };
  }, ct);
  private McpUiTarget Resolve(McpConnectionSession session, string snapshotId, string controlId) {
    RequireMain();
    var snapshot = session.UiSnapshot;
    if (snapshot == null || snapshot.Id != snapshotId || snapshot.AccessEpoch != session.AccessEpoch || snapshot.UiEpoch != Interlocked.Read(ref _uiEpoch)) { throw new InvalidOperationException("stale_snapshot: call ui_inspect again."); }
    var target = snapshot.Targets.FirstOrDefault(t => t.Id == controlId) ?? throw new ArgumentException("Unknown controlId in this snapshot.");
    RequireLog(target.ViewId, target.Revision); RequireVisible(target.Control);
    if (BoundsOf(target.Control) != target.Bounds || ValueOf(target.Control) != target.Value) { throw new InvalidOperationException("stale_control: geometry or value changed; inspect again."); }
    session.UiSnapshot = null;
    return target;
  }
  internal Task<object> Invoke(McpConnectionSession session, string snapshotId, string controlId, CancellationToken ct) => OnUi<object>(() => {
    var target = Resolve(session, snapshotId, controlId);
    if (target.Name != "ClearBtn") { throw new ArgumentException("This control needs ui_set_value."); }
    RequireLog(target.ViewId).View.ClearGraph();
    return new { completed = true, action = "clear_graph", revision = RequireLog(target.ViewId).Revision };
  }, ct);
  internal Task<object> SetValue(McpConnectionSession session, string snapshotId, string controlId, JsonElement value, CancellationToken ct) => OnUi<object>(() => {
    var target = Resolve(session, snapshotId, controlId);
    if (target.Control is NumericUpDown number) {
      if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal parsed) || parsed is < -1000000 or > 1000000) { throw new ArgumentException("Graph scale/offset requires a number between -1000000 and 1000000."); }
      number.SetCurrentValue(NumericUpDown.ValueProperty, parsed);
    } else if (target.Control is ToggleButton toggle) {
      if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw new ArgumentException("Toggle requires a JSON boolean."); }
      toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, value.GetBoolean());
    } else { throw new ArgumentException("This control needs ui_invoke."); }
    return new { completed = true, value = ValueOf(target.Control), note = "Scale/offset affect the next native Graph action. ui_plot_log always displays original physical values." };
  }, ct);
}
