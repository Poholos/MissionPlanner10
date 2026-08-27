using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner.Views;
using AvaloniaGrid = Avalonia.Controls.Grid;

namespace MissionPlanner.Tests;

/// <summary>
/// Avalonia's Grid does not clip its children and Layoutable.ArrangeCore honours a child's MinWidth
/// over the cell it was given, so a control that does not fit paints across its neighbours instead
/// of being constrained or throwing. None of that is visible at a window's default size - the
/// joystick window's RC bars only overflowed once it was dragged towards the 640px minimum it
/// allows - so each case renders at both extremes.
/// </summary>
public class LayoutOverflowTests {
  /// Rounding slack. Arranged bounds land on subpixel values that differ harmlessly between runs.
  private const double Tolerance = 0.5;

  /// The same boundary as InputSizingTests.SpinnerFloor of 98, so a view cannot pass one check and
  /// fail the other. Measured rather than derived: a declared width of 98 arranges a 30px text box,
  /// because the box keeps the 12px InputPad and 2px border that the spinner allowance does not
  /// cover. It separates the two states decisively - the joystick Expo field measured 2px before it
  /// was widened and 52px after.
  private const double MinimumEditableWidth = 30;

  /// Enough failures to show the shape of the problem without burying it.
  private const int MaxReported = 12;

  [AvaloniaTheory]
  [InlineData(640)]
  [InlineData(900)]
  public void Joystick_setup_window_arranges_without_collisions(double width) {
    JoystickSetupWindow? window = null;

    try {
      // Constructing the view model subscribes to process-wide joystick state and starts a timer,
      // so it must not happen outside the block that closes the window again.
      window = new JoystickSetupWindow { Width = width, Height = 700 };
      window.Show();
      Dispatcher.UIThread.RunJobs();

      List<string> problems = new();
      CollectGridProblems(window, problems);
      CollectCollapsedInputs(window, problems);

      Assert.True(problems.Count == 0, Report(problems, width));
    } finally {
      window?.Close();
    }
  }

  private static string Report(List<string> problems, double width) {
    string body = string.Join(Environment.NewLine, problems.Take(MaxReported));
    string more = problems.Count > MaxReported
        ? $"{Environment.NewLine}... and {problems.Count - MaxReported} more"
        : string.Empty;

    return $"Layout problems in JoystickSetupWindow at {width}px:{Environment.NewLine}{body}{more}";
  }

  private static void CollectGridProblems(Visual root, List<string> problems) {
    foreach (AvaloniaGrid grid in root.GetVisualDescendants().OfType<AvaloniaGrid>()) {
      // Grids inside a control template are skipped. Avalonia's own templates routinely park a
      // popup or overlay part in a nominal cell it deliberately exceeds - Fluent's ComboBox puts
      // PART_Popup in column 0 at the full control width while the chevron sits in column 1 - so
      // asserting against them reports the theme rather than this application's markup. Grids from
      // a DataTemplate have no TemplatedParent, so item rows stay covered.
      if (grid.TemplatedParent != null) {
        continue;
      }

      Control[] children = grid.Children
          .OfType<Control>()
          .Where(child => child.IsVisible && child.Bounds.Width > 0)
          .ToArray();
      Rect cell = new(0, 0, grid.Bounds.Width, grid.Bounds.Height);

      foreach (Control child in children) {
        if (!Escapes(child.Bounds, cell)) {
          continue;
        }

        problems.Add(
            $"{Describe(child)} is arranged outside its Grid: {child.Bounds} escapes {cell}");
      }

      for (int i = 0; i < children.Length; i++) {
        for (int j = i + 1; j < children.Length; j++) {
          Control first = children[i];
          Control second = children[j];

          if (!ColumnsAreDisjoint(grid, first, second)
              || !OverlapsBeyondRounding(first.Bounds, second.Bounds)) {
            continue;
          }

          problems.Add(
              $"{Describe(first)} and {Describe(second)} sit in different columns of a Grid but "
              + $"overlap: {first.Bounds} against {second.Bounds}");
        }
      }
    }
  }

  /// <summary>
  /// A NumericUpDown whose spinner buttons have eaten the whole control still draws its chevrons, so
  /// the defect only shows as a text field arranged down to nothing.
  /// </summary>
  private static void CollectCollapsedInputs(Visual root, List<string> problems) {
    foreach (NumericUpDown input in root.GetVisualDescendants().OfType<NumericUpDown>()) {
      if (!input.IsVisible) {
        continue;
      }

      TextBox? field = input.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
      if (field == null || field.Bounds.Width >= MinimumEditableWidth) {
        continue;
      }

      problems.Add(
          $"NumericUpDown at {input.Bounds} has its text field arranged to "
          + $"{field.Bounds.Width:0.#}px, leaving no room to read or type a value");
    }
  }

  private static bool Escapes(Rect child, Rect cell) =>
      child.Left < -Tolerance
      || child.Top < -Tolerance
      || child.Right > cell.Width + Tolerance
      || child.Bottom > cell.Height + Tolerance;

  private static bool OverlapsBeyondRounding(Rect first, Rect second) {
    double horizontal = Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left);
    double vertical = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
    return horizontal > Tolerance && vertical > Tolerance;
  }

  private static bool ColumnsAreDisjoint(AvaloniaGrid grid, Control first, Control second) {
    (int firstStart, int firstEnd) = ColumnRange(grid, first);
    (int secondStart, int secondEnd) = ColumnRange(grid, second);

    return firstEnd <= secondStart || secondEnd <= firstStart;
  }

  /// <summary>
  /// Avalonia clamps an out-of-range Grid.Column to the last column at layout time, so two children
  /// marked for columns past the end share a cell despite reading as disjoint.
  /// </summary>
  private static (int Start, int End) ColumnRange(AvaloniaGrid grid, Control child) {
    int last = Math.Max(0, grid.ColumnDefinitions.Count - 1);
    int start = Math.Clamp(AvaloniaGrid.GetColumn(child), 0, last);
    int end = Math.Clamp(start + Math.Max(1, AvaloniaGrid.GetColumnSpan(child)), start + 1, last + 1);

    return (start, end);
  }

  private static string Describe(Control control) {
    string name = control.Name ?? (control as ContentControl)?.Content?.ToString() ?? string.Empty;
    return name.Length == 0
        ? control.GetType().Name
        : $"{control.GetType().Name} '{name.Trim()}'";
  }
}
