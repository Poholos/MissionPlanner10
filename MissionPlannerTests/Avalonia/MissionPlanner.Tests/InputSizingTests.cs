using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MissionPlanner.Tests;

/// <summary>
/// A NumericUpDown spends a fixed part of its declared width on chrome before the first glyph, so a
/// control that looks generously sized in markup can render as a bare pair of chevrons with no
/// readable value. The joystick Expo field, the connection bar baud field and the SITL swarm
/// instance count all shipped that way. This check reads the markup rather than rendering it, so it
/// costs nothing and needs no display; <see cref="LayoutOverflowTests"/> covers what only shows up
/// once a view is actually arranged, including widths imposed by a parent rather than declared.
/// </summary>
public class InputSizingTests {
  /// 34px for each of the two ButtonSpinner RepeatButtons in Avalonia's Fluent theme, plus the
  /// border and padding below. Measured on a headless NumericUpDown: DesiredWidth is exactly 82.
  private const double SpinnerChrome = 82;

  /// ShowButtonSpinner="False" drops the buttons out of the template, leaving 1px of border a side
  /// and the 12px of InputPad from Theme/MpTheme.axaml. Measured at exactly 14.
  private const double FlatChrome = 14;

  /// One character at the default 14px face. Derived from a 105px baud field that rendered exactly
  /// three of the six digits in 115200. This is a screen, not a measurement of every glyph: letters
  /// in a FormatString suffix run wider, which is why LayoutOverflowTests renders for real.
  private const double DigitWidth = 7.8;

  private const double SignWidth = 4.5;

  /// Applied when Maximum is absent or bound and the largest value cannot be read from the markup.
  /// The field still must not collapse below roughly two characters.
  private const double SpinnerFloor = 98;
  private const double FlatFloor = 30;

  /// Maximum is often a sentinel meaning "no practical limit" - the baud field allows 10000000 - and
  /// demanding room for every digit of that would be stricter than any real value needs.
  private const int MaxDigitsEnforced = 6;

  private static readonly Regex FractionPlaceholders = new("^[0#]*", RegexOptions.Compiled);

  [Fact]
  public void NumericUpDown_widths_leave_room_for_their_largest_value() {
    string[] offenders = FindUndersized()
        .Select(found =>
            $"{found.Path}:{found.Line} Width={found.Width:0.#} needs {found.Required:0.#} "
            + $"(Maximum={Describe(found.Maximum)}, FormatString={Describe(found.Format)}, "
            + $"spinner={found.HasSpinner})")
        .ToArray();

    Assert.True(offenders.Length == 0,
        "NumericUpDown controls too narrow to show their own value. With the spinner shown, "
        + $"{SpinnerChrome}px of any width you set goes to chrome and padding before the first "
        + "glyph, so these truncate or render as bare chevrons. Prefer omitting Width so the "
        + "control sizes to its content."
        + Environment.NewLine
        + string.Join(Environment.NewLine, offenders));
  }

  private static IEnumerable<Undersized> FindUndersized() {
    string root = FindRepoRoot();

    foreach (string file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)) {
      // GetRelativePath does not prefix a separator, so a repo-root bin/ needs the leading slash
      // added before testing.
      string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
      string rooted = "/" + relative;
      if (rooted.Contains("/bin/", StringComparison.Ordinal)
          || rooted.Contains("/obj/", StringComparison.Ordinal)) {
        continue;
      }

      XDocument document;
      try {
        document = XDocument.Load(file, LoadOptions.SetLineInfo);
      } catch (XmlException) {
        continue;
      }

      foreach (XElement element in document.Descendants()) {
        if (element.Name.LocalName != "NumericUpDown") {
          continue;
        }

        // Width="NaN" is the XAML spelling of "size to content" and parses as a double, so it has
        // to be excluded explicitly or it compares false against every requirement.
        if (!TryParse((string?)element.Attribute("Width"), out double width)
            || !double.IsFinite(width)) {
          continue;
        }

        bool hasSpinner = !string.Equals(
            (string?)element.Attribute("ShowButtonSpinner"), "False",
            StringComparison.OrdinalIgnoreCase);
        string maximum = (string?)element.Attribute("Maximum") ?? string.Empty;
        string format = (string?)element.Attribute("FormatString") ?? string.Empty;
        string increment = (string?)element.Attribute("Increment") ?? string.Empty;

        double required = RequiredWidth(
            maximum, (string?)element.Attribute("Minimum"), format, increment, hasSpinner);
        if (width >= required) {
          continue;
        }

        yield return new Undersized(
            relative,
            element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0,
            width,
            maximum,
            format,
            hasSpinner,
            required);
      }
    }
  }

  private static double RequiredWidth(
      string maximum, string? minimum, string format, string increment, bool hasSpinner) {
    double chrome = hasSpinner ? SpinnerChrome : FlatChrome;
    double floor = hasSpinner ? SpinnerFloor : FlatFloor;
    int overhead = ValueOverhead(format, increment);

    // A bound or absent Maximum hides the digit count, but the fractional part and any suffix are
    // still known, so they must be charged for here rather than dropped along with the digits.
    if (!TryParse(maximum, out double maximumValue)) {
      return Math.Max(floor, chrome + ((1 + overhead) * DigitWidth));
    }

    int digits = Math.Min(
        Math.Abs(maximumValue).ToString("0", CultureInfo.InvariantCulture).Length,
        MaxDigitsEnforced);
    double signAllowance = TryParse(minimum, out double minimumValue) && minimumValue < 0
        ? SignWidth
        : 0;

    return Math.Max(floor, chrome + ((digits + overhead) * DigitWidth) + signAllowance);
  }

  /// <summary>
  /// Characters the rendered value carries beyond the digits Maximum implies. A FormatString adds a
  /// separator, its fractional placeholders and any literal suffix: "0.##" and "0 m" both appear in
  /// the tree. With no FormatString a fractional Increment still produces decimals, which is how a
  /// field whose Maximum is 60 ends up rendering "12.5".
  /// </summary>
  private static int ValueOverhead(string format, string increment) {
    if (format.Length == 0) {
      bool fractionalSteps = TryParse(increment, out double step)
          && step != Math.Floor(step);
      return fractionalSteps ? 2 : 0;
    }

    int decimals = 0;
    int separator = format.IndexOf('.');
    if (separator >= 0) {
      decimals = 1 + FractionPlaceholders.Match(format[(separator + 1)..]).Length;
    }

    int literals = format.Count(character => !"0#.,".Contains(character, StringComparison.Ordinal));
    return decimals + literals;
  }

  private static string Describe(string value) => value.Length == 0 ? "unset" : value;

  private static bool TryParse(string? text, out double value) =>
      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

  private static string FindRepoRoot() {
    string? path = AppContext.BaseDirectory;
    while (path != null && !File.Exists(Path.Combine(path, "MissionPlanner.slnx"))) {
      path = Directory.GetParent(path)?.FullName;
    }
    return path ?? throw new DirectoryNotFoundException("Repository root not found.");
  }

  private readonly record struct Undersized(
      string Path,
      int Line,
      double Width,
      string Maximum,
      string Format,
      bool HasSpinner,
      double Required);
}
