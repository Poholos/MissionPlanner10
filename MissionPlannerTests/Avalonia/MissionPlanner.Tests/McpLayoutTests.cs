using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner.Views;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpLayoutTests {
  internal static AgentToolsWindow Window(Func<CancellationToken, Task<McpAgent[]>> discover) =>
      new(new McpAgentHub(null!, () => null, discover));
  internal static async Task CloseWindowAsync(AgentToolsWindow window) {
    // Closing the window keeps the hub alive by design; tests dispose the hub explicitly so
    // listener/catalogue teardown finishes before the headless dispatcher is reset.
    window.Close();
    await window.Hub.DisposeAsync();
    Dispatcher.UIThread.RunJobs();
  }
  [AvaloniaFact]
  public async Task Closing_the_agent_window_keeps_the_hub_and_its_listeners_alive() {
    var window = Window(_ => Task.FromResult<McpAgent[]>([]));
    try {
      window.Show(); Dispatcher.UIThread.RunJobs();
      var server = await window.Hub.OpenSessionPortAsync();
      window.Close(); Dispatcher.UIThread.RunJobs();
      Assert.False(server.Stopping.IsCancellationRequested);
      Assert.Same(server, window.Hub.SessionServer);
      Assert.NotNull(server.Endpoint);
      await window.Hub.StopAllAsync();
      Assert.Null(window.Hub.SessionServer);
      Assert.True(server.Stopping.IsCancellationRequested);
    } finally { await CloseWindowAsync(window); }
  }
  [AvaloniaFact]
  public async Task Agent_window_buttons_fit_at_minimum_size_in_all_tabs() {
    var window = Window(_ => Task.FromResult<McpAgent[]>([
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
    var window = Window(_ => Task.FromResult<McpAgent[]>([
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
    var window = Window(_ => Task.FromResult(found));
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
