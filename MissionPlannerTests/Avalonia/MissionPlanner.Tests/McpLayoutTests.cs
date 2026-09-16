using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public sealed class McpLayoutTests {
  [AvaloniaFact]
  public void Agent_window_buttons_fit_at_minimum_size_in_both_tabs() {
    var window = new AgentToolsWindow(null!);
    try {
      window.Show(); window.Width = window.MinWidth; window.Height = window.MinHeight;
      Dispatcher.UIThread.RunJobs();
      var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
      for (int tab = 0; tab < 2; tab++) {
        tabs.SelectedIndex = tab;
        Dispatcher.UIThread.RunJobs();
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)) {
          var start = button.TranslatePoint(new Point(), window)!.Value;
          Assert.True(start.X >= -1 && start.Y >= -1, $"{button.Content} starts outside window.");
          Assert.True(start.X + button.Bounds.Width <= window.ClientSize.Width + 1, $"{button.Content} overflows width.");
          Assert.True(start.Y + button.Bounds.Height <= window.ClientSize.Height + 1, $"{button.Content} overflows height.");
        }
      }
    } finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
  }
}
