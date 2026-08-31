using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class DialogLayoutTests {
  private const double Tolerance = 0.5;

  [AvaloniaFact]
  public void Update_choice_buttons_stay_inside_the_dialog_at_default_and_minimum_width() {
    Window? window = null;
    try {
      window = Dialogs.CreateChoiceWindow(
          "Update available",
          "Version 1.3.83.2 (22ec12fe) is available "
          + "(you have 1.3.83.2 (bcc3e2c0-dirty)).",
          ["Install", "What's new", "Skip this version", "Later"]);
      window.Show();
      Dispatcher.UIThread.RunJobs();

      AssertButtonsFit(window, expectSingleRow: true);

      window.SizeToContent = SizeToContent.Height;
      window.Width = window.MinWidth;
      Dispatcher.UIThread.RunJobs();

      Assert.InRange(window.Bounds.Width,
          window.MinWidth - Tolerance, window.MinWidth + Tolerance);
      AssertButtonsFit(window, expectSingleRow: false);
    } finally {
      window?.Close();
    }
  }

  private static void AssertButtonsFit(Window window, bool expectSingleRow) {
    Button[] buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
    Assert.Equal(4, buttons.Length);
    Assert.Equal(
        new[] { "Install", "What's new", "Skip this version", "Later" },
        buttons.Select(button => button.Content as string));

    foreach (Button button in buttons) {
      Point topLeft = button.TranslatePoint(new Point(0, 0), window) ?? default;
      Assert.True(topLeft.X >= -Tolerance,
          $"{button.Content} starts outside the dialog at {topLeft.X:0.#}px.");
      Assert.True(topLeft.X + button.Bounds.Width <= window.ClientSize.Width + Tolerance,
          $"{button.Content} ends at {topLeft.X + button.Bounds.Width:0.#}px, past the "
          + $"{window.ClientSize.Width:0.#}px dialog edge.");
      Assert.True(topLeft.Y >= -Tolerance,
          $"{button.Content} starts outside the dialog at {topLeft.Y:0.#}px.");
      Assert.True(topLeft.Y + button.Bounds.Height <= window.ClientSize.Height + Tolerance,
          $"{button.Content} ends at {topLeft.Y + button.Bounds.Height:0.#}px, past the "
          + $"{window.ClientSize.Height:0.#}px dialog edge.");
    }

    if (expectSingleRow) {
      double[] tops = buttons
          .Select(button => button.TranslatePoint(new Point(0, 0), window)?.Y ?? 0)
          .ToArray();
      Assert.True(tops.Max() - tops.Min() <= Tolerance,
          "The automatically sized choice dialog should keep all actions on one row.");
    }
  }
}
