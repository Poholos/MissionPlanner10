using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MissionPlanner.Views;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpUiTarget(string Id, string WindowId, string Kind, string Name, string Label, string Path, Control Control, string Value, string[] Actions);
internal sealed record McpUiSnapshot(string Id, long AccessEpoch, McpUiTarget[] Targets) {
  internal McpUiTarget? Find(string controlId) => Targets.FirstOrDefault(t => t.Id == controlId);
}

/// <summary>
/// Generic native inspection of every visible Mission Planner window. Actions go through the same
/// routed events and properties the operator's mouse and keyboard reach, never through simulated input.
/// The AI window itself (consent controls) and password boxes are never targets.
/// </summary>
internal sealed partial class McpUiHost {
  private sealed record Surface(string Id, Control Root, bool Capture);
  private readonly ConditionalWeakTable<Window, string> _windowIds = new();
  private int _windowCounter;

  internal IEnumerable<Window> Windows() {
    var seen = new HashSet<Window>();
    if (owner() is { } main) { seen.Add(main); yield return main; }
    var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
    foreach (var window in lifetime?.Windows.ToArray() ?? []) {
      if (window.IsVisible && window is not AgentToolsWindow && seen.Add(window)) { yield return window; }
    }
    foreach (var view in _logViews.Values) { if (view.Window.IsVisible && seen.Add(view.Window)) { yield return view.Window; } }
  }
  internal string WindowId(Window window) {
    if (ReferenceEquals(window, owner())) { return "main"; }
    var log = _logViews.Values.FirstOrDefault(v => ReferenceEquals(v.Window, window));
    if (log != null) { return log.Id; }
    return _windowIds.GetValue(window, _ => "window-" + Interlocked.Increment(ref _windowCounter).ToString(CultureInfo.InvariantCulture));
  }
  internal Window WindowById(string id) => Windows().FirstOrDefault(w => WindowId(w) == id) ?? throw new ArgumentException("Unknown windowId; read ui_get_state.");
  internal object WindowSummary(Window window) => new { windowId = WindowId(window), title = window.Title, type = window.GetType().Name,
    enabled = window.IsEnabled, active = window.IsActive, isMain = ReferenceEquals(window, owner()),
    width = window.Bounds.Width, height = window.Bounds.Height };

  private IEnumerable<Surface> Surfaces() {
    foreach (var window in Windows()) {
      string id = WindowId(window);
      yield return new("window:" + id, window, true);
      if (ReferenceEquals(window, owner())) {
        foreach (var control in window.GetVisualDescendants().OfType<Control>()) {
          string? surface = control.Name switch { "FdMap" => "flight-map", "Map" when control is MissionPlanner.Controls.FlightPlannerMap => "planner-map", "TuningPlot" => "live-plot", _ => null };
          if (surface != null) { yield return new(surface, control, true); }
        }
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
      throw new InvalidOperationException("unavailable_control: target is hidden, disabled, detached or blocked by a dialog; inspect again.");
    }
  }
  private static Rect BoundsOf(Control control) {
    var window = TopLevel.GetTopLevel(control)!;
    return new Rect(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);
  }

  private static string Trim(string? text, int max = 200) {
    text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
    return text.Length <= max ? text : text[..max] + "…";
  }
  private static string TextOf(object? content) => content switch {
    null => "",
    string s => s,
    TextBlock block => block.Text ?? "",
    Control control => string.Join(" ", control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t))
        .Concat(control is TextBlock inner ? new[] { inner.Text } : [])),
    _ => content.ToString() ?? "",
  };
  private static string Kind(Control control) => control switch {
    CheckBox => "checkbox", RadioButton => "radio", ToggleButton => "toggle", Button => "button",
    TextBox => "text", NumericUpDown => "number", ComboBox => "combo", Slider => "slider",
    TabControl => "tabs", TabItem => "tab", ListBox => "list", ListBoxItem => "listitem", MenuItem => "menu", Expander => "expander", DatePicker => "date",
    TextBlock => "static", _ => "",
  };
  private static string[] ActionsFor(Control control) => control switch {
    ToggleButton or Expander => ["invoke", "set_value"],
    Button or MenuItem or TabItem or ListBoxItem => ["invoke"],
    TextBox or NumericUpDown or ComboBox or Slider or TabControl or ListBox or DatePicker => ["set_value"],
    _ => [],
  };
  private static string LabelOf(Control control) {
    string label = control switch {
      TextBox box => Trim(box.Watermark),
      MenuItem item => Trim(TextOf(item.Header)),
      HeaderedContentControl headed => Trim(TextOf(headed.Header)),
      ContentControl content when content is not Window => Trim(TextOf(content.Content)),
      TextBlock block => Trim(block.Text),
      _ => "",
    };
    if (label.Length == 0) { label = Trim(AutomationProperties.GetName(control)); }
    if (label.Length == 0 && ToolTip.GetTip(control) is string tip) { label = Trim(tip); }
    if (label.Length == 0 && control.Parent is Panel panel) {
      // Common form layout: a TextBlock immediately before the input names it.
      int index = panel.Children.IndexOf(control);
      for (int i = index - 1; i >= 0 && i >= index - 2; i--) {
        if (panel.Children[i] is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) { label = Trim(text.Text); break; }
      }
    }
    return label;
  }
  internal static string ValueOf(Control control) => control switch {
    TextBox box => Trim(box.Text, 500),
    NumericUpDown number => number.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
    ToggleButton toggle => toggle.IsChecked == null ? "null" : toggle.IsChecked == true ? "true" : "false",
    Expander expander => expander.IsExpanded ? "true" : "false",
    ComboBox combo => combo.SelectedIndex < 0 ? "" : $"{combo.SelectedIndex}: {Trim(TextOf(combo.SelectedItem))}",
    Slider slider => slider.Value.ToString(CultureInfo.InvariantCulture),
    TabControl tabs => tabs.SelectedIndex < 0 ? "" : $"{tabs.SelectedIndex}: {Trim(TextOf((tabs.SelectedItem as TabItem)?.Header ?? tabs.SelectedItem))}",
    TabItem tab => tab.IsSelected ? "selected" : "",
    ListBox list => list.SelectedIndex < 0 ? "" : $"{list.SelectedIndex}: {Trim(TextOf(list.SelectedItem))}",
    ListBoxItem item => item.IsSelected ? "selected" : "",
    DatePicker date => date.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
    TextBlock block => Trim(block.Text, 500),
    _ => "",
  };
  private static string[] OptionsOf(Control control) => control switch {
    ComboBox combo => combo.Items.Cast<object?>().Take(200).Select(i => Trim(TextOf(i), 80)).ToArray(),
    TabControl tabs => tabs.Items.Cast<object?>().Take(200).Select(i => Trim(TextOf((i as TabItem)?.Header ?? i), 80)).ToArray(),
    ListBox list => list.Items.Cast<object?>().Take(200).Select(i => Trim(TextOf(i), 80)).ToArray(),
    _ => [],
  };
  private static string PathOf(Control control, Window window) {
    var parts = new List<string>();
    for (Visual? node = control; node != null && !ReferenceEquals(node, window); node = node.GetVisualParent()) {
      var parent = node.GetVisualParent();
      int index = parent == null ? 0 : parent.GetVisualChildren().TakeWhile(c => !ReferenceEquals(c, node)).Count();
      parts.Add(node.GetType().Name + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
    }
    parts.Reverse();
    return string.Join("/", parts);
  }
  private static bool Excluded(Control control) =>
      control is TextBox { PasswordChar: not '\0' } || control is Window
      || control.Name?.StartsWith("PART_", StringComparison.Ordinal) == true // template parts (spinner arrows, etc.)
      || control.GetVisualAncestors().Any(a => a is AgentToolsWindow)
      || control.Classes.Contains("mcp-private");

  /// <summary>Native click through the control's automation peer: runs OnClick, so Command and Click handlers both fire.</summary>
  private static void Click(Button button) {
    var peer = ControlAutomationPeer.CreatePeerForElement(button);
    if (button is ToggleButton toggle) {
      // ToggleButton peers expose Toggle rather than Invoke; a real click toggles first, then raises Click.
      if (peer is IToggleProvider provider) { provider.Toggle(); } else { toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, toggle.IsChecked != true); }
      toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    } else if (peer is IInvokeProvider invoke) { invoke.Invoke(); }
    else { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
  }
  private McpUiTarget Target(Control control, Window window, string windowId, string kind, string? path = null) {
    path ??= PathOf(control, window);
    string identity = windowId + "|" + kind + "|" + (control.Name ?? "") + "|" + path;
    string id = "c-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
    return new(id, windowId, kind, control.Name ?? "", LabelOf(control), path, control, ValueOf(control), ActionsFor(control));
  }

  /// <summary>Menus are logical, not visual, until opened: list their items so an agent can invoke them directly.</summary>
  private IEnumerable<McpUiTarget> MenuTargets(Window window, string windowId) {
    foreach (var menu in window.GetVisualDescendants().OfType<Menu>().Where(m => m.IsEffectivelyVisible)) {
      string menuPath = PathOf(menu, window);
      var stack = new Stack<(MenuItem Item, string Path)>();
      foreach (var top in menu.Items.OfType<MenuItem>().Reverse()) { stack.Push((top, menuPath + "/" + Trim(TextOf(top.Header), 60))); }
      while (stack.Count > 0) {
        var (item, path) = stack.Pop();
        if (!item.IsVisible || item.Header is Separator) { continue; }
        if (item.Items.Count > 0) {
          foreach (var child in item.Items.OfType<MenuItem>().Reverse()) { stack.Push((child, path + "/" + Trim(TextOf(child.Header), 60))); }
          continue;
        }
        var target = Target(item, window, windowId, "menu", path);
        yield return target with { Label = path[(menuPath.Length + 1)..].Replace("/", " > ", StringComparison.Ordinal) };
      }
    }
  }

  internal Task<object> Inspect(McpConnectionSession session, string? windowId, string? filter, bool includeStatic, CancellationToken ct) => OnUi<object>(() => {
    long epoch = session.AccessEpoch;
    var targets = new List<McpUiTarget>();
    var windows = Windows().ToArray();
    if (windowId != null && windows.All(w => WindowId(w) != windowId)) { throw new ArgumentException("Unknown windowId; read ui_get_state."); }
    bool truncated = false;
    foreach (var window in windows) {
      string id = WindowId(window);
      if (windowId != null && id != windowId) { continue; }
      foreach (var control in window.GetVisualDescendants().OfType<Control>()) {
        string kind = Kind(control);
        // Menu items are listed logically below, including the ones hidden in closed popups.
        if (kind.Length == 0 || kind == "menu" || Excluded(control) || !control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) { continue; }
        if (kind == "static") {
          if (!includeStatic || string.IsNullOrWhiteSpace(((TextBlock)control).Text) || control.GetVisualAncestors().Any(a => a is Button or MenuItem or TabItem or ListBoxItem)) { continue; }
        }
        if (targets.Count >= 2000) { truncated = true; break; }
        targets.Add(Target(control, window, id, kind));
      }
      targets.AddRange(MenuTargets(window, id));
    }
    if (!string.IsNullOrWhiteSpace(filter)) {
      targets = targets.Where(t => t.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
          || t.Kind.Equals(filter, StringComparison.OrdinalIgnoreCase) || t.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    var snapshot = new McpUiSnapshot(Guid.NewGuid().ToString("N"), epoch, targets.ToArray());
    session.UiSnapshot = snapshot;
    return new { snapshotId = snapshot.Id, complete = !truncated, windows = windows.Select(WindowSummary).ToArray(),
      scope = "Visible controls of every open Mission Planner window plus logical menu items; the AI window and password boxes are excluded. "
          + "Controls keep the same controlId while their window and layout path are unchanged. A disabled window (behind a modal dialog) rejects actions until the dialog closes.",
      controls = targets.Select(t => new { controlId = t.Id, windowId = t.WindowId, kind = t.Kind, name = t.Name, label = t.Label, value = t.Value,
        options = OptionsOf(t.Control), actions = t.Actions, enabled = t.Control.IsEffectivelyEnabled, visible = t.Control.IsEffectivelyVisible,
        bounds = t.Control.IsEffectivelyVisible ? (object?)BoundsOf(t.Control) : null, path = t.Path }).ToArray() };
  }, ct);

  private McpUiTarget Resolve(McpConnectionSession session, string snapshotId, string controlId) {
    var snapshot = session.UiSnapshot;
    if (snapshot == null || snapshot.Id != snapshotId || snapshot.AccessEpoch != session.AccessEpoch) { throw new InvalidOperationException("stale_snapshot: call ui_inspect again."); }
    var target = snapshot.Find(controlId) ?? throw new ArgumentException("Unknown controlId in this snapshot.");
    var window = Windows().FirstOrDefault(w => WindowId(w) == target.WindowId);
    if (window == null || !window.IsVisible) { throw new InvalidOperationException("stale_control: its window closed; inspect again."); }
    if (target.Control is MenuItem item) {
      // Items of closed menus are logical children only; check the owning window and the item's own state.
      if (!item.IsEnabled || !window.IsEnabled || (item.GetLogicalAncestors().OfType<Control>().Any(a => !a.IsEnabled))) { throw new InvalidOperationException("unavailable_control: menu item disabled or window blocked."); }
      return target;
    }
    if (!ReferenceEquals(TopLevel.GetTopLevel(target.Control), window)) { throw new InvalidOperationException("stale_control: the control left its window; inspect again."); }
    RequireVisible(target.Control);
    return target;
  }

  internal Task<object> Invoke(McpConnectionSession session, string snapshotId, string controlId, CancellationToken ct) => OnUi<object>(() => {
    var target = Resolve(session, snapshotId, controlId);
    string action;
    switch (target.Control) {
      case ToggleButton toggle: Click(toggle); action = "toggle"; break;
      case Button button: Click(button); action = "click"; break;
      case MenuItem item: item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); action = "menu"; break;
      case TabItem tab: if (tab.Parent is TabControl tabs) { tabs.SelectedItem = tab; } else { tab.IsSelected = true; } action = "select"; break;
      case ListBoxItem listItem: listItem.IsSelected = true; action = "select"; break;
      case Expander expander: expander.IsExpanded = !expander.IsExpanded; action = "expand"; break;
      default: throw new ArgumentException("This control needs ui_set_value.");
    }
    return new { completed = true, action, controlId, value = ValueOf(target.Control), note = "Dispatched natively; inspect again to observe resulting windows or state." };
  }, ct);

  internal Task<object> SetValue(McpConnectionSession session, string snapshotId, string controlId, JsonElement value, CancellationToken ct) => OnUi<object>(() => {
    var target = Resolve(session, snapshotId, controlId);
    static double Number(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && double.IsFinite(d) ? d
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d
        : throw new ArgumentException(what + " requires a finite number.");
    static int Index(JsonElement value, IEnumerable<object?> items, Func<object?, string> text, string what) {
      var list = items.ToList();
      if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index)) {
        if (index < 0 || index >= list.Count) { throw new ArgumentException($"{what} index must be 0..{list.Count - 1}."); }
        return index;
      }
      if (value.ValueKind == JsonValueKind.String) {
        string wanted = value.GetString() ?? "";
        int found = list.FindIndex(i => string.Equals(text(i), wanted, StringComparison.Ordinal));
        if (found < 0) { found = list.FindIndex(i => text(i).Contains(wanted, StringComparison.OrdinalIgnoreCase)); }
        if (found < 0) { throw new ArgumentException($"{what}: no option matches '{wanted}'; see options in ui_inspect."); }
        return found;
      }
      throw new ArgumentException(what + " requires an option index or text.");
    }
    switch (target.Control) {
      case TextBox box:
        if (value.ValueKind != JsonValueKind.String) { throw new ArgumentException("Text requires a JSON string."); }
        string text = value.GetString() ?? "";
        if (text.Length > 65536) { throw new ArgumentException("Text longer than 65536 characters."); }
        box.SetCurrentValue(TextBox.TextProperty, text); box.Focus(); break;
      case NumericUpDown number:
        double n = Number(value, "Number");
        number.SetCurrentValue(NumericUpDown.ValueProperty, (decimal)Math.Clamp(n, (double)number.Minimum, (double)number.Maximum)); break;
      case ToggleButton toggle:
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw new ArgumentException("Toggle requires a JSON boolean."); }
        if (toggle.IsChecked != value.GetBoolean()) { Click(toggle); }
        if (toggle.IsChecked != value.GetBoolean()) { toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, value.GetBoolean()); }
        break;
      case Expander expander:
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw new ArgumentException("Expander requires a JSON boolean."); }
        expander.IsExpanded = value.GetBoolean(); break;
      case ComboBox combo: combo.SelectedIndex = Index(value, combo.Items.Cast<object?>(), i => Trim(TextOf(i), 80), "Combo"); break;
      case TabControl tabs: tabs.SelectedIndex = Index(value, tabs.Items.Cast<object?>(), i => Trim(TextOf((i as TabItem)?.Header ?? i), 80), "Tab"); break;
      case ListBox list: list.SelectedIndex = Index(value, list.Items.Cast<object?>(), i => Trim(TextOf(i), 80), "List"); break;
      case Slider slider: slider.Value = Math.Clamp(Number(value, "Slider"), slider.Minimum, slider.Maximum); break;
      case DatePicker date:
        if (value.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)) { throw new ArgumentException("Date requires an ISO-8601 string."); }
        date.SelectedDate = when; break;
      default: throw new ArgumentException("This control needs ui_invoke.");
    }
    return new { completed = true, controlId, value = ValueOf(target.Control), note = "Value set through the native property; bound view-model logic ran as for keyboard entry. Buttons that commit the value still need ui_invoke." };
  }, ct);
}
