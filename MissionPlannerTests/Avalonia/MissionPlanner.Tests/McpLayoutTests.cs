using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner.Views;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpLayoutTests {
  internal static async Task CloseWindowAsync(AgentToolsWindow window) {
    // Closing initiates asynchronous listener/catalogue teardown. Finish it before the
    // per-test headless application resets its process-wide dispatcher.
    var close = typeof(AgentToolsWindow).GetMethod("CloseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    await (Task)close.Invoke(window, null)!;
    Dispatcher.UIThread.RunJobs();
  }
  [AvaloniaFact]
  public async Task Agent_window_buttons_fit_at_minimum_size_in_all_tabs() {
    var window = new AgentToolsWindow(null!, _ => Task.FromResult<McpAgent[]>([
      new(McpAgentKind.CodexCli, "Codex CLI", "/fake/codex", []),
      new(McpAgentKind.ClaudeCode, "Claude Code", "/fake/claude", []),
      new(McpAgentKind.OpenAiDesktop, "Codex Desktop", "/fake/desktop", []),
    ]));
    try {
      window.Show(); window.Width = window.MinWidth; window.Height = window.MinHeight;
      await window.FindAgentsAsync();
      Dispatcher.UIThread.RunJobs();
      var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
      Assert.True(tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == "Connections").IsVisible);
      for (int tab = 0; tab < tabs.ItemCount; tab++) {
        tabs.SelectedIndex = tab;
        Dispatcher.UIThread.RunJobs();
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)) {
          var start = button.TranslatePoint(new Point(), window)!.Value;
          Assert.True(start.X >= -1 && start.Y >= -1, $"{button.Content} starts outside window.");
          Assert.True(start.X + button.Bounds.Width <= window.ClientSize.Width + 1, $"{button.Content} overflows width.");
          // Agent, flight-log and desktop workflows scroll vertically at minimum height.
          if (tab == 1) { Assert.True(start.Y + button.Bounds.Height <= window.ClientSize.Height + 1, $"{button.Content} overflows height."); }
        }
      }
    } finally { await CloseWindowAsync(window); }
  }
  [AvaloniaFact]
  public async Task Connections_are_available_when_only_cli_agents_are_found() {
    var window = new AgentToolsWindow(null!, _ => Task.FromResult<McpAgent[]>([
      new(McpAgentKind.ClaudeCode, "Claude Code", "/fake/claude", []),
    ]));
    try {
      window.Show(); await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
      Assert.True(tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == "Connections").IsVisible);
    } finally { await CloseWindowAsync(window); }
  }

  [AvaloniaFact]
  public async Task Connections_remain_available_if_all_agents_disappear() {
    McpAgent[] found = [new(McpAgentKind.OpenAiDesktop, "Codex Desktop", "/fake/app", [])];
    var window = new AgentToolsWindow(null!, _ => Task.FromResult(found));
    try {
      window.Show(); await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
      tabs.SelectedIndex = 3;
      found = []; await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      Assert.Equal(3, tabs.SelectedIndex);
      Assert.True(tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == "Connections").IsVisible);
    } finally { await CloseWindowAsync(window); }
  }

}
