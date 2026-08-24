using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlanner.Services;

public sealed record SikRadioSettingMetadata(
    string Designator,
    string Name,
    string Value,
    int? Minimum,
    int? Maximum,
    IReadOnlyList<string> AllowedValues);

public sealed record SikRadioProfile(
    IReadOnlyDictionary<string, string> Values,
    int IgnoredLines);

/// <summary>
/// Pure parsing and profile persistence for the SiK/RFD settings page. RFD firmware exposes
/// setting metadata through ATI10 (or the older ATI5?) while classic SiK exposes only ATI5.
/// Keeping this code independent from the serial port makes malformed modem replies and profile
/// files testable without hardware.
/// </summary>
public static class SikRadioSettingsService {
  private static readonly Regex MetadataLine = new(
      @"^[^A-Za-z]*(?<designator>[A-Za-z][A-Za-z0-9]*):(?<name>[^=(]+)" +
      @"(?:\([^)]*\)\[(?<min>-?\d+)\.\.(?<max>-?\d+)\])?=" +
      @"(?<value>-?\d+)(?:\{(?<options>[^}]*)\})?",
      RegexOptions.Compiled);

  private static readonly Regex ProfileName = new(
      @"\A[A-Za-z][A-Za-z0-9_/]*\z", RegexOptions.Compiled);

  public static IReadOnlyDictionary<string, SikRadioSettingMetadata> ParseMetadata(string text) {
    var result = new Dictionary<string, SikRadioSettingMetadata>(StringComparer.OrdinalIgnoreCase);
    foreach (string raw in (text ?? "").Split('\n', '\r')) {
      Match match = MetadataLine.Match(raw.Trim());
      if (!match.Success) {
        continue;
      }

      string name = match.Groups["name"].Value.Trim();
      if (name.Length == 0 || name.Equals("RESERVED", StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      string value = match.Groups["value"].Value;
      int? minimum = int.TryParse(match.Groups["min"].Value, NumberStyles.Integer,
          CultureInfo.InvariantCulture, out int parsedMinimum) ? parsedMinimum : null;
      int? maximum = int.TryParse(match.Groups["max"].Value, NumberStyles.Integer,
          CultureInfo.InvariantCulture, out int parsedMaximum) ? parsedMaximum : null;
      var allowed = BuildAllowedValues(name, value, match.Groups["min"].Value,
          match.Groups["max"].Value, match.Groups["options"].Value);
      result[name] = new SikRadioSettingMetadata(
          match.Groups["designator"].Value, name, value, minimum, maximum, allowed);
    }
    return result;
  }

  public static SikRadioProfile ParseProfile(string text) {
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    int ignored = 0;
    foreach (string raw in (text ?? "").Split('\n', '\r')) {
      string line = raw.Trim();
      if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) {
        continue;
      }

      int comment = line.IndexOfAny([';', '#']);
      if (comment >= 0) {
        line = line[..comment].Trim();
      }
      int equals = line.IndexOf('=');
      if (equals <= 0 || equals != line.LastIndexOf('=')) {
        ignored++;
        continue;
      }

      string name = line[..equals].Trim();
      string value = line[(equals + 1)..].Trim();
      if (!ProfileName.IsMatch(name) || value.Length == 0) {
        ignored++;
        continue;
      }
      values[name] = value;
    }
    return new SikRadioProfile(values, ignored);
  }

  public static string SerializeProfile(IEnumerable<KeyValuePair<string, string>> values) {
    var output = new StringBuilder();
    foreach (var pair in values
        .Where(pair => ProfileName.IsMatch(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
      output.Append(pair.Key).Append(" = ").Append(pair.Value.Trim()).AppendLine();
    }
    return output.ToString();
  }

  public static bool IsValidInteger(string value) =>
      int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

  public static bool IsValidHexKey(string value, int maxHexDigits = 64) =>
      value.Length is > 0 && value.Length <= maxHexDigits && value.All(Uri.IsHexDigit);

  public static IReadOnlyList<string> BuildRange(int min, int max, int increment,
      int maximumItems = 512) {
    if (increment <= 0 || min > max || ((long)max - min) / increment + 1 > maximumItems) {
      return Array.Empty<string>();
    }
    var result = new List<string>();
    for (long current = min; current <= max; current += increment) {
      result.Add(current.ToString(CultureInfo.InvariantCulture));
    }
    if (result.Count == 0 || result[^1] != max.ToString(CultureInfo.InvariantCulture)) {
      result.Add(max.ToString(CultureInfo.InvariantCulture));
    }
    return result;
  }

  private static IReadOnlyList<string> BuildAllowedValues(
      string name, string current, string minText, string maxText, string optionsText) {
    if (!int.TryParse(minText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int min)
        || !int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int max)) {
      return Array.Empty<string>();
    }

    string[] options = optionsText.Split(',', StringSplitOptions.RemoveEmptyEntries
        | StringSplitOptions.TrimEntries);
    if (options.Length > 0 && options.All(IsValidInteger)) {
      var numeric = options.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
      double scale = name.Equals("SERIAL_SPEED", StringComparison.OrdinalIgnoreCase)
          && numeric.All(value => value >= 1000) ? 0.001 : 1;
      var scaled = numeric.Select(value => ((int)(value * scale)).ToString(CultureInfo.InvariantCulture))
          .Distinct().ToList();
      if (!scaled.Contains(current)) {
        scaled.Insert(0, current);
      }
      return scaled;
    }

    int increment = name is "MIN_FREQ" or "MAX_FREQ" ? 500
        : name == "MAX_WINDOW" ? 20 : 1;
    var range = BuildRange(min, max, increment).ToList();
    if (range.Count > 0 && !range.Contains(current)) {
      range.Insert(0, current);
    }
    return range;
  }
}
