using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services;

public enum LogTestStatus { Good, Warn, Fail, Unknown, NA }

public enum LogAnalyzerVehicleType { Unknown, Copter, Plane, Rover }

public readonly record struct LogTestResult(string Name, LogTestStatus Status, string Message);

public sealed record LogAnalyzerSample(
    int Line,
    double TimeSeconds,
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyDictionary<string, string>? TextValues = null);

public sealed class LogAnalyzerData {
  public LogAnalyzerData(
      int lineCount,
      LogAnalyzerVehicleType vehicleType,
      IReadOnlyDictionary<string, IReadOnlyList<LogAnalyzerSample>> records,
      IReadOnlyDictionary<string, double> parameters) {
    ArgumentOutOfRangeException.ThrowIfNegative(lineCount);
    LineCount = lineCount;
    VehicleType = vehicleType;
    Records = records ?? throw new ArgumentNullException(nameof(records));
    Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
  }

  public int LineCount { get; }
  public LogAnalyzerVehicleType VehicleType { get; }
  public IReadOnlyDictionary<string, IReadOnlyList<LogAnalyzerSample>> Records { get; }
  public IReadOnlyDictionary<string, double> Parameters { get; }
}

/// <summary>
/// In-process, cross-platform replacement for Mission Planner's downloaded Python 2/py2exe log
/// analyzer. The enabled upstream checks are kept granular so a missing channel cannot hide the
/// results of unrelated tests.
/// </summary>
public static class LogAnalyzer {
  private const double Epsilon = 1e-9;

  private static readonly IReadOnlyDictionary<string, string[]> MessageFields =
      new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        ["ATT"] = ["Roll", "Pitch"],
        ["ATDE"] = [],
        ["ATUN"] = ["Axis", "TuneStep", "RateMin", "RateMax", "RPGain", "RDGain", "SPGain"],
        ["CTUN"] = ["BarAlt", "Alt", "ThO", "ThrOut", "CRate", "CRt"],
        ["CURR"] = ["Vcc"],
        ["ERR"] = ["Subsys", "ECode"],
        ["EV"] = ["Id"],
        ["GPS"] = ["NSats", "NSat", "numSV", "HDop", "HDp", "EPH", "Status"],
        ["GPS2"] = ["NSats", "NSat", "numSV", "HDop", "HDp", "EPH", "Status"],
        ["IMU"] = ["AccX", "AccY", "AccZ"],
        ["IMU2"] = ["AccX", "AccY", "AccZ"],
        ["MAG"] = ["OfsX", "OfsY", "OfsZ", "MagX", "MagY", "MagZ"],
        ["MODE"] = ["Mode", "ModeNum"],
        ["OF"] = ["flowX", "flowY", "bodyX", "bodyY", "Qual"],
        ["PM"] = ["NLon", "NLoop", "MaxT"],
        ["POWR"] = ["Vcc"],
        ["RCOU"] = Enumerable.Range(1, 16)
            .SelectMany(index => new[] { $"C{index}", $"Ch{index}", $"Chan{index}" })
            .ToArray(),
        ["VIBE"] = ["VibeX", "VibeY", "VibeZ"],
      };

  public static LogTestStatus Classify(double value, double warn, double fail, bool higherWorse) {
    if (double.IsNaN(value)) {
      return LogTestStatus.NA;
    }
    if (higherWorse) {
      return value >= fail ? LogTestStatus.Fail : value >= warn ? LogTestStatus.Warn : LogTestStatus.Good;
    }
    return value <= fail ? LogTestStatus.Fail : value <= warn ? LogTestStatus.Warn : LogTestStatus.Good;
  }

  public static List<LogTestResult> Analyze(string binPath) => Analyze(Load(binPath));

  public static List<LogTestResult> Analyze(LogAnalyzerData data) {
    ArgumentNullException.ThrowIfNull(data);
    return [
      TestEmpty(data),
      TestVibration(data),
      TestGps(data),
      TestVcc(data),
      TestCompass(data),
      TestMotorBalance(data),
      TestNaN(data),
      TestEvents(data),
      TestBrownout(data),
      TestDuplicateData(data),
      TestParameters(data),
      TestPerformance(data),
      TestPitchRoll(data),
      TestThrust(data),
      TestImuMismatch(data),
      TestAutotune(data),
      TestOpticalFlow(data),
    ];
  }

  public static string Format(IEnumerable<LogTestResult> results) =>
      string.Join("\n", results.Select(result =>
          $"[{result.Status.ToString().ToUpperInvariant()}] {result.Name}: {result.Message}"));

  private static LogAnalyzerData Load(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    using var log = new DFLogBuffer(path);
    var mutableRecords = MessageFields.Keys.ToDictionary(
        key => key, _ => new List<LogAnalyzerSample>(), StringComparer.OrdinalIgnoreCase);
    var parameters = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    LogAnalyzerVehicleType vehicleType = LogAnalyzerVehicleType.Unknown;

    string[] requestedTypes = MessageFields.Keys.Concat(["MSG", "PARM"]).ToArray();
    foreach (DFLog.DFItem item in log.GetEnumeratorType(requestedTypes)) {
      if (item.msgtype.Equals("PARM", StringComparison.OrdinalIgnoreCase)) {
        string name = item["Name"]?.Trim() ?? "";
        if (name.Length > 0 && TryNumber(item["Value"], out double value)) {
          parameters[name] = value;
        }
        continue;
      }

      if (item.msgtype.Equals("MSG", StringComparison.OrdinalIgnoreCase)) {
        string message = item["Message"] ?? item["Msg"] ?? item["Text"] ?? "";
        vehicleType = DetectVehicleType(vehicleType, message);
        continue;
      }

      if (!MessageFields.TryGetValue(item.msgtype, out string[]? fields)) {
        continue;
      }

      var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
      var textValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (string field in fields) {
        string? raw = item[field];
        if (!string.IsNullOrWhiteSpace(raw)) {
          textValues[field] = raw.Trim();
        }
        if (TryNumber(raw, out double value)) {
          values[field] = value;
        }
      }

      double timeSeconds;
      try {
        timeSeconds = item.timems / 1000.0;
      } catch (Exception) {
        timeSeconds = 0;
      }
      mutableRecords[item.msgtype].Add(
          new LogAnalyzerSample(item.lineno, timeSeconds, values, textValues));
    }

    if (vehicleType == LogAnalyzerVehicleType.Unknown && parameters.ContainsKey("FRAME_CLASS")) {
      vehicleType = LogAnalyzerVehicleType.Copter;
    }

    var records = mutableRecords.ToDictionary(
        pair => pair.Key,
        pair => (IReadOnlyList<LogAnalyzerSample>)pair.Value,
        StringComparer.OrdinalIgnoreCase);
    return new LogAnalyzerData(log.Count, vehicleType, records, parameters);
  }

  private static LogAnalyzerVehicleType DetectVehicleType(
      LogAnalyzerVehicleType current, string message) {
    if (message.Contains("ArduCopter", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Copter", StringComparison.OrdinalIgnoreCase)) {
      return LogAnalyzerVehicleType.Copter;
    }
    if (message.Contains("ArduPlane", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Plane", StringComparison.OrdinalIgnoreCase)) {
      return LogAnalyzerVehicleType.Plane;
    }
    if (message.Contains("ArduRover", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Rover", StringComparison.OrdinalIgnoreCase)) {
      return LogAnalyzerVehicleType.Rover;
    }
    return current;
  }

  private static LogTestResult TestEmpty(LogAnalyzerData data) {
    if (data.LineCount == 0) {
      return Result("Empty", LogTestStatus.Fail, "log contains no records");
    }

    IReadOnlyList<LogAnalyzerSample> ctun = Rows(data, "CTUN");
    if (ctun.Count == 0) {
      return Result("Empty", LogTestStatus.Unknown, "no CTUN throttle data");
    }

    List<double> throttle = Values(ctun, "ThrOut", "ThO").ToList();
    if (throttle.Count == 0) {
      return Result("Empty", LogTestStatus.Unknown, "no throttle output column");
    }
    double threshold = throttle.Max() <= 1.5 ? 0.2 :
        data.VehicleType == LogAnalyzerVehicleType.Copter ? 200 : 20;
    return throttle.Max() < threshold
        ? Result("Empty", LogTestStatus.Fail, "throttle never exceeded 20%")
        : Result("Empty", LogTestStatus.Good, "log contains powered flight data");
  }

  private static LogTestResult TestVibration(LogAnalyzerData data) {
    List<double> vibration = new[] { "VibeX", "VibeY", "VibeZ" }
        .SelectMany(field => Values(Rows(data, "VIBE"), field))
        .Select(Math.Abs)
        .ToList();
    if (vibration.Count == 0) {
      return Result("Vibration", LogTestStatus.Unknown, "no VIBE data");
    }
    double maximum = vibration.Max();
    return Result("Vibration", Classify(maximum, 30, 60, true),
        $"maximum {maximum:0.0} m/s/s");
  }

  private static LogTestResult TestGps(LogAnalyzerData data) {
    IReadOnlyList<LogAnalyzerSample> gps = Rows(data, "GPS");
    if (gps.Count == 0) {
      gps = Rows(data, "GPS2");
    }
    if (gps.Count == 0) {
      return Result("GPS", LogTestStatus.Unknown, "no GPS/GPS2 data");
    }

    List<double> satellites = Values(gps, "NSats", "NSat", "numSV").ToList();
    List<double> hdop = Values(gps, "HDop", "HDp", "EPH").ToList();
    int glitches = ErrorPairs(data).Count(pair => pair.subsystem == 11 && pair.code == 2);
    if (satellites.Count == 0 && hdop.Count == 0) {
      return Result("GPS", glitches > 0 ? LogTestStatus.Fail : LogTestStatus.Unknown,
          glitches > 0 ? $"GPS glitch errors: {glitches}" : "satellite/HDop columns are missing");
    }

    double minSatellites = satellites.Count == 0 ? double.PositiveInfinity : satellites.Min();
    double maxHdop = hdop.Count == 0 ? 0 : hdop.Max();
    LogTestStatus status = glitches > 0 || minSatellites < 5 || maxHdop > 10
        ? LogTestStatus.Fail
        : minSatellites < 6 || maxHdop > 3 ? LogTestStatus.Warn : LogTestStatus.Good;
    string message = $"min satellites {Display(minSatellites)}, max HDop {maxHdop:0.00}";
    if (glitches > 0) {
      message = $"{glitches} GPS glitch error(s); {message}";
    }
    return Result("GPS", status, message);
  }

  private static LogTestResult TestVcc(LogAnalyzerData data) {
    List<double> values = Values(Rows(data, "CURR"), "Vcc").ToList();
    if (values.Count == 0) {
      values = Values(Rows(data, "POWR"), "Vcc").ToList();
    }
    if (values.Count == 0) {
      return Result("VCC", LogTestStatus.Unknown, "no CURR/POWR Vcc data");
    }
    if (values.OrderBy(value => value).ElementAt(values.Count / 2) > 100) {
      values = values.Select(value => value / 1000.0).ToList();
    }

    double minimum = values.Min();
    double spread = values.Max() - minimum;
    LogTestStatus status = minimum < 4.3 || spread > 0.5 ? LogTestStatus.Fail
        : minimum < 4.6 || spread > 0.3 ? LogTestStatus.Warn : LogTestStatus.Good;
    return Result("VCC", status, $"minimum {minimum:0.00} V, spread {spread:0.00} V");
  }

  private static LogTestResult TestCompass(LogAnalyzerData data) {
    IReadOnlyList<LogAnalyzerSample> mag = Rows(data, "MAG");
    var messages = new List<string>();
    LogTestStatus status = LogTestStatus.Good;

    var parameterOffset = Vector(
        Parameter(data, "COMPASS_OFS_X"),
        Parameter(data, "COMPASS_OFS_Y"),
        Parameter(data, "COMPASS_OFS_Z"));
    if (parameterOffset.HasValue) {
      (status, string message) = AssessCompassOffset(parameterOffset.Value, "parameter");
      if (message.Length > 0) {
        messages.Add(message);
      }
    }

    List<double> loggedOffsets = mag.Select(sample => Vector(
            Value(sample, "OfsX"), Value(sample, "OfsY"), Value(sample, "OfsZ")))
        .Where(vector => vector.HasValue)
        .Select(vector => vector!.Value)
        .ToList();
    if (loggedOffsets.Count > 0) {
      (LogTestStatus offsetStatus, string message) =
          AssessCompassOffset(loggedOffsets.Max(), "logged");
      status = Worse(status, offsetStatus);
      if (message.Length > 0) {
        messages.Add(message);
      }
    }

    var fields = new List<double>();
    bool zeroVector = false;
    foreach (LogAnalyzerSample sample in mag) {
      double? x = Value(sample, "MagX");
      double? y = Value(sample, "MagY");
      double? z = Value(sample, "MagZ");
      double? vector = Vector(x, y, z);
      if (!vector.HasValue) {
        continue;
      }
      if (vector.Value <= Epsilon) {
        zeroVector = true;
      } else {
        fields.Add(vector.Value);
      }
    }

    if (fields.Count > 0) {
      double minimum = fields.Min();
      double maximum = fields.Max();
      double change = (maximum - minimum) / minimum;
      LogTestStatus fieldStatus = change > 0.35 ? LogTestStatus.Fail
          : change > 0.25 || zeroVector ? LogTestStatus.Warn : LogTestStatus.Good;
      status = Worse(status, fieldStatus);
      messages.Add($"magnetic field change {change:P1}, range {minimum:0}-{maximum:0}");
    } else if (loggedOffsets.Count == 0 && !parameterOffset.HasValue) {
      return Result("Compass", LogTestStatus.Unknown, "no MAG or compass-offset data");
    }

    return Result("Compass", status,
        messages.Count == 0 ? "offsets and magnetic field are within limits" : string.Join("; ", messages));
  }

  private static LogTestResult TestMotorBalance(LogAnalyzerData data) {
    if (data.VehicleType != LogAnalyzerVehicleType.Copter) {
      return Result("Motor balance", LogTestStatus.NA, "copter-only check");
    }
    IReadOnlyList<LogAnalyzerSample> rows = Rows(data, "RCOU");
    if (rows.Count == 0) {
      return Result("Motor balance", LogTestStatus.Unknown, "no RCOU data");
    }

    int motorCount = CopterMotorCount(data) ?? 8;
    var averages = new List<double>();
    for (int channel = 1; channel <= motorCount; channel++) {
      List<double> values = Values(rows, $"C{channel}", $"Ch{channel}", $"Chan{channel}")
          .Where(value => value is > 0 and < 3000)
          .ToList();
      if (values.Count > 0) {
        averages.Add(values.Average());
      }
    }
    if (averages.Count < 2) {
      return Result("Motor balance", LogTestStatus.Unknown, "fewer than two motor output channels");
    }

    double spread = averages.Max() - averages.Min();
    LogTestStatus status = spread > 150 ? LogTestStatus.Fail
        : spread > 75 ? LogTestStatus.Warn : LogTestStatus.Good;
    return Result("Motor balance", status,
        $"{averages.Count} channel averages, output spread {spread:0} us");
  }

  private static LogTestResult TestNaN(LogAnalyzerData data) {
    foreach ((string type, IReadOnlyList<LogAnalyzerSample> samples) in data.Records) {
      foreach (LogAnalyzerSample sample in samples) {
        foreach ((string field, double value) in sample.Values) {
          if (double.IsNaN(value) || double.IsInfinity(value)) {
            return Result("NaN", LogTestStatus.Fail,
                $"invalid number in {type}.{field} at line {sample.Line}");
          }
        }
      }
    }
    string? badParameter = data.Parameters.FirstOrDefault(pair =>
        double.IsNaN(pair.Value) || double.IsInfinity(pair.Value)).Key;
    return badParameter == null
        ? Result("NaN", LogTestStatus.Good, "no invalid numeric values")
        : Result("NaN", LogTestStatus.Fail, $"invalid parameter value: {badParameter}");
  }

  private static LogTestResult TestEvents(LogAnalyzerData data) {
    var errors = new HashSet<string>(StringComparer.Ordinal);
    foreach ((int subsystem, int code) in ErrorPairs(data)) {
      string? error = subsystem switch {
        2 when code == 1 => "PPM",
        3 when code is 1 or 2 => "COMPASS",
        5 when code == 1 => "FS_THR",
        6 when code == 1 => "FS_BATT",
        7 when code == 1 => "GPS",
        8 when code == 1 => "GCS",
        9 when code is 1 or 2 => "FENCE",
        10 => "FLT_MODE",
        11 when code == 2 => "GPS_GLITCH",
        12 when code == 1 => "CRASH",
        _ => null,
      };
      if (error != null) {
        errors.Add(error);
      }
    }
    if (errors.Count == 0) {
      return Result("Event/Failsafe", LogTestStatus.Good, "no recognized ERR/failsafe events");
    }
    LogTestStatus status = errors.SetEquals(["FENCE"]) ? LogTestStatus.Warn : LogTestStatus.Fail;
    return Result("Event/Failsafe", status, string.Join(", ", errors.Order(StringComparer.Ordinal)));
  }

  private static LogTestResult TestBrownout(LogAnalyzerData data) {
    bool armed = false;
    foreach (double eventId in Values(Rows(data, "EV"), "Id")) {
      if ((int)eventId == 10) {
        armed = true;
      } else if ((int)eventId == 11) {
        armed = false;
      }
    }

    List<double> altitude = Values(Rows(data, "CTUN"), "BarAlt", "Alt").ToList();
    if (altitude.Count == 0) {
      return Result("Brownout", LogTestStatus.Unknown, "no CTUN altitude data");
    }
    double finalAltitude = altitude[^1];
    return armed && finalAltitude > 3
        ? Result("Brownout", LogTestStatus.Fail,
            $"log ends armed at {finalAltitude:0.00} m; possible truncation/brownout")
        : Result("Brownout", LogTestStatus.Good, "log ending is consistent with a normal shutdown");
  }

  private static LogTestResult TestDuplicateData(LogAnalyzerData data) {
    List<(int line, double value)> pitch = Rows(data, "ATT")
        .Select(sample => (sample.Line, Value(sample, "Pitch")))
        .Where(item => item.Item2.HasValue)
        .Select(item => (item.Line, item.Item2!.Value))
        .ToList();
    const int window = 20;
    if (pitch.Count < window * 2) {
      return Result("Duplicate data", LogTestStatus.Unknown, "insufficient ATT.Pitch samples");
    }

    var windows = new Dictionary<string, int>(StringComparer.Ordinal);
    for (int index = 0; index <= pitch.Count - window; index++) {
      bool constant = true;
      for (int offset = 1; offset < window; offset++) {
        if (pitch[index + offset].value != pitch[index].value) {
          constant = false;
          break;
        }
      }
      if (constant) {
        continue;
      }

      string key = string.Join("|", pitch.Skip(index).Take(window)
          .Select(item => item.value.ToString("R", CultureInfo.InvariantCulture)));
      if (windows.TryGetValue(key, out int previous) && index - previous >= window) {
        return Result("Duplicate data", LogTestStatus.Fail,
            $"duplicate 20-sample ATT.Pitch chunks at lines {pitch[previous].line} and {pitch[index].line}");
      }
      windows.TryAdd(key, index);
    }
    return Result("Duplicate data", LogTestStatus.Good, "no repeated ATT.Pitch chunks");
  }

  private static LogTestResult TestParameters(LogAnalyzerData data) {
    var errors = data.Parameters
        .Where(pair => double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
        .Select(pair => $"{pair.Key} is not finite")
        .ToList();
    if (data.VehicleType == LogAnalyzerVehicleType.Copter) {
      CheckParameter(data, errors, "MAG_ENABLE", value => value == 1, "must equal 1");
      CheckParameter(data, errors, "THR_MIN", value => value < 200, "must be below 200");
      CheckParameter(data, errors, "THR_MID", value => value is > 299 and < 701,
          "must be between 300 and 700");
    }
    return errors.Count == 0
        ? Result("Parameters", LogTestStatus.Good, "no known unsafe parameter values")
        : Result("Parameters", LogTestStatus.Fail, string.Join("; ", errors));
  }

  private static LogTestResult TestPerformance(LogAnalyzerData data) {
    if (data.VehicleType != LogAnalyzerVehicleType.Copter) {
      return Result("PM", LogTestStatus.NA, "copter-only check");
    }
    IReadOnlyList<LogAnalyzerSample> pm = Rows(data, "PM");
    if (pm.Count == 0) {
      return Result("PM", LogTestStatus.Unknown, "no PM performance data");
    }
    var slow = new List<(int line, double percent)>();
    foreach (LogAnalyzerSample sample in pm) {
      double? longLoops = Value(sample, "NLon");
      double? totalLoops = Value(sample, "NLoop");
      if (!longLoops.HasValue || !totalLoops.HasValue || totalLoops.Value <= 0) {
        continue;
      }
      double percent = longLoops.Value / totalLoops.Value * 100;
      if (percent > 6) {
        slow.Add((sample.Line, percent));
      }
    }
    if (slow.Count == 0) {
      return Result("PM", LogTestStatus.Good, "no slow-loop interval above 6%");
    }
    (int line, double maximum) = slow.MaxBy(item => item.percent);
    LogTestStatus status = maximum > 10 || slow.Count > 6 ? LogTestStatus.Fail : LogTestStatus.Warn;
    return Result("PM", status,
        $"{slow.Count} slow-loop interval(s), maximum {maximum:0.00}% at line {line}");
  }

  private static LogTestResult TestPitchRoll(LogAnalyzerData data) {
    if (data.VehicleType != LogAnalyzerVehicleType.Copter) {
      return Result("Pitch/Roll", LogTestStatus.NA, "copter-only check");
    }
    IReadOnlyList<LogAnalyzerSample> attitude = Rows(data, "ATT");
    IReadOnlyList<LogAnalyzerSample> ctun = Rows(data, "CTUN");
    if (attitude.Count == 0 || ctun.Count == 0) {
      return Result("Pitch/Roll", LogTestStatus.Unknown, "ATT or CTUN altitude data is missing");
    }
    double maximumLean = Parameter(data, "ANGLE_MAX") is { } angle ? angle / 100.0 : 45;
    double limit = maximumLean + 10;
    IReadOnlyList<LogAnalyzerSample> modes = Rows(data, "MODE");
    foreach (LogAnalyzerSample sample in attitude) {
      double? altitude = PreviousValue(ctun, sample.Line, "BarAlt", "Alt");
      if (!altitude.HasValue || altitude.Value <= 2 ||
          IsIgnoredCopterModeAtLine(modes, sample.Line)) {
        continue;
      }
      double roll = Math.Abs(Value(sample, "Roll") ?? 0);
      double pitch = Math.Abs(Value(sample, "Pitch") ?? 0);
      if (Math.Max(roll, pitch) > limit) {
        string axis = roll >= pitch ? "roll" : "pitch";
        double value = Math.Max(roll, pitch);
        return Result("Pitch/Roll", LogTestStatus.Fail,
            $"{axis} {value:0.00}° exceeds buffered lean limit {limit:0.00}° at line {sample.Line}");
      }
    }
    return Result("Pitch/Roll", LogTestStatus.Good, $"airborne lean stayed within {limit:0.0}°");
  }

  private static LogTestResult TestThrust(LogAnalyzerData data) {
    if (data.VehicleType != LogAnalyzerVehicleType.Copter) {
      return Result("Thrust", LogTestStatus.NA, "copter-only check");
    }
    IReadOnlyList<LogAnalyzerSample> ctun = Rows(data, "CTUN");
    IReadOnlyList<LogAnalyzerSample> attitude = Rows(data, "ATT");
    if (ctun.Count == 0 || attitude.Count == 0) {
      return Result("Thrust", LogTestStatus.Unknown, "CTUN or ATT data is missing");
    }

    List<double> availableThrottle = Values(ctun, "ThrOut", "ThO").ToList();
    if (availableThrottle.Count == 0) {
      return Result("Thrust", LogTestStatus.Unknown, "throttle output column is missing");
    }
    double highThreshold = availableThrottle.Max() <= 1.5 ? 0.7 : 700;
    var segment = new List<(double throttle, double climb)>();
    LogTestResult? worst = null;
    foreach (LogAnalyzerSample sample in ctun) {
      double? throttle = Value(sample, "ThrOut", "ThO");
      double? climb = Value(sample, "CRate", "CRt");
      double roll = Math.Abs(NearestValue(attitude, sample.Line, "Roll") ?? 0);
      double pitch = Math.Abs(NearestValue(attitude, sample.Line, "Pitch") ?? 0);
      if (throttle > highThreshold && climb.HasValue && roll <= 20 && pitch <= 20) {
        segment.Add((throttle.Value, climb.Value));
        continue;
      }
      worst = AssessThrustSegment(segment, worst);
      segment.Clear();
    }
    worst = AssessThrustSegment(segment, worst);
    return worst ?? Result("Thrust", LogTestStatus.Good,
        "no sustained level high-throttle/low-climb interval");
  }

  private static LogTestResult TestImuMismatch(LogAnalyzerData data) {
    IReadOnlyList<LogAnalyzerSample> imu1 = Rows(data, "IMU");
    IReadOnlyList<LogAnalyzerSample> imu2 = Rows(data, "IMU2");
    if (imu1.Count > 0 && imu2.Count == 0) {
      return Result("IMU mismatch", LogTestStatus.NA, "no secondary IMU");
    }
    if (imu1.Count == 0 || imu2.Count == 0) {
      return Result("IMU mismatch", LogTestStatus.Unknown, "IMU/IMU2 accelerometer data is missing");
    }

    int secondIndex = 0;
    double filteredX = 0;
    double filteredY = 0;
    double filteredZ = 0;
    double maximum = 0;
    double? previousTime = null;
    foreach (LogAnalyzerSample first in imu1) {
      if (!TryVector(first, out (double x, double y, double z) firstVector)) {
        continue;
      }
      double time = EffectiveTime(first);
      while (secondIndex + 1 < imu2.Count &&
             EffectiveTime(imu2[secondIndex + 1]) <= time) {
        secondIndex++;
      }
      int nearest = secondIndex;
      if (secondIndex + 1 < imu2.Count &&
          Math.Abs(EffectiveTime(imu2[secondIndex + 1]) - time) <
          Math.Abs(EffectiveTime(imu2[secondIndex]) - time)) {
        nearest = secondIndex + 1;
      }
      if (!TryVector(imu2[nearest], out (double x, double y, double z) secondVector)) {
        continue;
      }

      double delta = previousTime.HasValue ? Math.Clamp(time - previousTime.Value, 0, 0.1) : 0;
      filteredX += (firstVector.x - secondVector.x - filteredX) * delta / 5.0;
      filteredY += (firstVector.y - secondVector.y - filteredY) * delta / 5.0;
      filteredZ += (firstVector.z - secondVector.z - filteredZ) * delta / 5.0;
      maximum = Math.Max(maximum,
          Math.Sqrt(filteredX * filteredX + filteredY * filteredY + filteredZ * filteredZ));
      previousTime = time;
    }

    LogTestStatus status = maximum > 1.5 ? LogTestStatus.Fail
        : maximum > 0.75 ? LogTestStatus.Warn : LogTestStatus.Good;
    return Result("IMU mismatch", status,
        $"filtered accelerometer mismatch {maximum:0.00} m/s/s (warn 0.75, fail 1.50)");
  }

  private static LogTestResult TestAutotune(LogAnalyzerData data) {
    if (data.VehicleType != LogAnalyzerVehicleType.Copter) {
      return Result("Autotune", LogTestStatus.NA, "copter-only check");
    }
    int[] autotuneEvents = Values(Rows(data, "EV"), "Id")
        .Select(value => (int)value)
        .Where(value => value is >= 30 and <= 37)
        .ToArray();
    if (autotuneEvents.Length == 0) {
      return Result("Autotune", LogTestStatus.NA, "no autotune session");
    }
    if (Rows(data, "ATUN").Count == 0 && Rows(data, "ATDE").Count == 0) {
      return Result("Autotune", LogTestStatus.Unknown, "autotune events exist but ATUN/ATDE data is missing");
    }

    int sessions = 0;
    int lastOutcome = 0;
    foreach (int eventId in autotuneEvents) {
      if (eventId == 30) {
        sessions++;
        lastOutcome = 0;
      } else if (sessions > 0 && eventId is 33 or 34 or 35) {
        lastOutcome = eventId;
      }
    }
    LogTestStatus status = lastOutcome == 33 ? LogTestStatus.Good
        : lastOutcome is 34 or 35 ? LogTestStatus.Fail : LogTestStatus.Unknown;
    string outcome = lastOutcome switch {
      33 => "success",
      34 => "failed",
      35 => "reached limit",
      _ => "has no final result",
    };
    return Result("Autotune", status, $"{sessions} session(s); last session {outcome}");
  }

  private static LogTestResult TestOpticalFlow(LogAnalyzerData data) {
    IReadOnlyList<LogAnalyzerSample> flow = Rows(data, "OF");
    if (flow.Count == 0) {
      return Result("Optical flow", LogTestStatus.NA, "no optical-flow calibration data");
    }
    IReadOnlyList<LogAnalyzerSample> attitude = Rows(data, "ATT");
    if (attitude.Count == 0) {
      return Result("Optical flow", LogTestStatus.Unknown, "OF data exists but ATT data is missing");
    }
    bool hasRollSweep = Values(attitude, "Roll").Any(value => Math.Abs(value) > 15);
    bool hasPitchSweep = Values(attitude, "Pitch").Any(value => Math.Abs(value) > 15);
    if (!hasRollSweep || !hasPitchSweep) {
      return Result("Optical flow", LogTestStatus.Fail,
          "calibration requires both roll and pitch sweeps beyond 15°");
    }

    List<(double body, double measured)> x = FlowPairs(flow, "bodyX", "flowX");
    List<(double body, double measured)> y = FlowPairs(flow, "bodyY", "flowY");
    if (x.Count < 100 || y.Count < 100) {
      return Result("Optical flow", LogTestStatus.Fail,
          $"insufficient high-quality samples (X={x.Count}, Y={y.Count}, need 100)");
    }
    if (!TryLinearFit(x, out double slopeX, out double stdX) ||
        !TryLinearFit(y, out double slopeY, out double stdY)) {
      return Result("Optical flow", LogTestStatus.Fail, "optical-flow scale fit is degenerate");
    }

    double existingX = Parameter(data, "FLOW_FXSCALER") ?? 0;
    double existingY = Parameter(data, "FLOW_FYSCALER") ?? 0;
    int newX = (int)Math.Round(1000 * ((1 + 0.001 * existingX) / slopeX - 1));
    int newY = (int)Math.Round(1000 * ((1 + 0.001 * existingY) / slopeY - 1));
    LogTestStatus status = Math.Abs(newX) > 200 || Math.Abs(newY) > 200 ||
        1000 * stdX > 5 || 1000 * stdY > 5
        ? LogTestStatus.Fail
        : LogTestStatus.Good;
    return Result("Optical flow", status,
        $"recommended FLOW_FXSCALER={newX}, FLOW_FYSCALER={newY}; slope σ={1000 * stdX:0.0}/{1000 * stdY:0.0}");
  }

  private static IEnumerable<(int subsystem, int code)> ErrorPairs(LogAnalyzerData data) {
    foreach (LogAnalyzerSample sample in Rows(data, "ERR")) {
      double? subsystem = Value(sample, "Subsys");
      double? code = Value(sample, "ECode");
      if (subsystem.HasValue && code.HasValue) {
        yield return ((int)subsystem.Value, (int)code.Value);
      }
    }
  }

  private static (LogTestStatus status, string message) AssessCompassOffset(
      double magnitude, string source) {
    LogTestStatus status = magnitude > 600 ? LogTestStatus.Fail
        : magnitude > 350 ? LogTestStatus.Warn : LogTestStatus.Good;
    return status == LogTestStatus.Good
        ? (status, "")
        : (status, $"{source} compass offset magnitude {magnitude:0}");
  }

  private static LogTestResult? AssessThrustSegment(
      IReadOnlyList<(double throttle, double climb)> segment, LogTestResult? current) {
    if (segment.Count <= 50) {
      return current;
    }
    double averageClimb = segment.Average(item => item.climb);
    double averageThrottle = segment.Average(item => item.throttle);
    LogTestStatus status = averageClimb < 50 ? LogTestStatus.Fail
        : averageClimb < 100 ? LogTestStatus.Warn : LogTestStatus.Good;
    var candidate = Result("Thrust", status,
        $"average climb {averageClimb:0.0} cm/s at throttle {averageThrottle:0}");
    return current == null || Worse(current.Value.Status, status) == status ? candidate : current;
  }

  private static List<(double body, double measured)> FlowPairs(
      IReadOnlyList<LogAnalyzerSample> rows, string bodyField, string measuredField) {
    var result = new List<(double body, double measured)>();
    foreach (LogAnalyzerSample sample in rows) {
      double? body = Value(sample, bodyField);
      double? measured = Value(sample, measuredField);
      double? quality = Value(sample, "Qual");
      if (body.HasValue && measured.HasValue && quality > 124 &&
          Math.Abs(body.Value) is > 0 and < 2) {
        result.Add((body.Value, measured.Value));
      }
    }
    return result;
  }

  private static bool TryLinearFit(
      IReadOnlyList<(double body, double measured)> points,
      out double slope,
      out double slopeStandardError) {
    double meanX = points.Average(point => point.body);
    double meanY = points.Average(point => point.measured);
    double sxx = points.Sum(point => Math.Pow(point.body - meanX, 2));
    if (sxx <= Epsilon) {
      slope = 0;
      slopeStandardError = double.PositiveInfinity;
      return false;
    }
    slope = points.Sum(point => (point.body - meanX) * (point.measured - meanY)) / sxx;
    if (Math.Abs(slope) <= Epsilon) {
      slopeStandardError = double.PositiveInfinity;
      return false;
    }
    double fittedSlope = slope;
    double intercept = meanY - fittedSlope * meanX;
    double residual = points.Sum(point =>
        Math.Pow(point.measured - (intercept + fittedSlope * point.body), 2));
    slopeStandardError = Math.Sqrt(residual / Math.Max(1, points.Count - 2) / sxx);
    return true;
  }

  private static bool TryVector(
      LogAnalyzerSample sample, out (double x, double y, double z) vector) {
    double? x = Value(sample, "AccX");
    double? y = Value(sample, "AccY");
    double? z = Value(sample, "AccZ");
    if (x.HasValue && y.HasValue && z.HasValue) {
      vector = (x.Value, y.Value, z.Value);
      return true;
    }
    vector = default;
    return false;
  }

  private static double EffectiveTime(LogAnalyzerSample sample) =>
      sample.TimeSeconds > 0 ? sample.TimeSeconds : sample.Line / 100.0;

  private static void CheckParameter(
      LogAnalyzerData data,
      ICollection<string> errors,
      string name,
      Func<double, bool> valid,
      string requirement) {
    if (data.Parameters.TryGetValue(name, out double value) && !valid(value)) {
      errors.Add($"{name}={value.ToString(CultureInfo.InvariantCulture)} {requirement}");
    }
  }

  private static double? Parameter(LogAnalyzerData data, string name) =>
      data.Parameters.TryGetValue(name, out double value) ? value : null;

  private static IReadOnlyList<LogAnalyzerSample> Rows(LogAnalyzerData data, string type) =>
      data.Records.TryGetValue(type, out IReadOnlyList<LogAnalyzerSample>? rows)
          ? rows
          : Array.Empty<LogAnalyzerSample>();

  private static IEnumerable<double> Values(
      IReadOnlyList<LogAnalyzerSample> rows, params string[] aliases) {
    foreach (LogAnalyzerSample sample in rows) {
      double? value = Value(sample, aliases);
      if (value.HasValue) {
        yield return value.Value;
      }
    }
  }

  private static double? Value(LogAnalyzerSample sample, params string[] aliases) {
    foreach (string alias in aliases) {
      if (sample.Values.TryGetValue(alias, out double value)) {
        return value;
      }
    }
    return null;
  }

  private static double? NearestValue(
      IReadOnlyList<LogAnalyzerSample> rows, int line, params string[] aliases) {
    LogAnalyzerSample? nearest = null;
    int distance = int.MaxValue;
    foreach (LogAnalyzerSample sample in rows) {
      int candidate = Math.Abs(sample.Line - line);
      if (candidate >= distance) {
        continue;
      }
      if (!Value(sample, aliases).HasValue) {
        continue;
      }
      nearest = sample;
      distance = candidate;
    }
    return nearest == null ? null : Value(nearest, aliases);
  }

  private static double? PreviousValue(
      IReadOnlyList<LogAnalyzerSample> rows, int line, params string[] aliases) {
    LogAnalyzerSample? previous = null;
    foreach (LogAnalyzerSample sample in rows) {
      if (sample.Line <= line && (previous == null || sample.Line > previous.Line) &&
          Value(sample, aliases).HasValue) {
        previous = sample;
      }
    }
    return previous == null ? null : Value(previous, aliases);
  }

  private static bool IsIgnoredCopterModeAtLine(
      IReadOnlyList<LogAnalyzerSample> modes, int line) {
    LogAnalyzerSample? active = null;
    foreach (LogAnalyzerSample sample in modes) {
      if (sample.Line <= line && (active == null || sample.Line > active.Line)) {
        active = sample;
      }
    }
    if (active == null) {
      return false;
    }

    if (active.TextValues != null &&
        active.TextValues.TryGetValue("Mode", out string? modeName) &&
        modeName.Trim().ToUpperInvariant() is "ACRO" or "SPORT" or "FLIP" or "AUTOTUNE") {
      return true;
    }

    double? modeNumber = Value(active, "ModeNum", "Mode");
    return modeNumber.HasValue && (int)Math.Round(modeNumber.Value) is 1 or 13 or 14 or 15;
  }

  private static int? CopterMotorCount(LogAnalyzerData data) {
    int? frameClass = Parameter(data, "FRAME_CLASS") is { } value
        ? (int)Math.Round(value)
        : null;
    return frameClass switch {
      0 => 4,
      1 => 6,
      2 or 3 => 8,
      4 => 6,
      6 => 3,
      8 or 9 => 2,
      10 => 12,
      11 => 4,
      12 => 10,
      _ => null,
    };
  }

  private static double? Vector(double? x, double? y, double? z) =>
      x.HasValue && y.HasValue && z.HasValue
          ? Math.Sqrt(x.Value * x.Value + y.Value * y.Value + z.Value * z.Value)
          : null;

  private static bool TryNumber(string? raw, out double value) =>
      double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands,
          CultureInfo.InvariantCulture, out value);

  private static string Display(double value) => double.IsPositiveInfinity(value)
      ? "n/a"
      : value.ToString("0", CultureInfo.InvariantCulture);

  private static LogTestStatus Worse(LogTestStatus first, LogTestStatus second) {
    static int Severity(LogTestStatus status) => status switch {
      LogTestStatus.Fail => 4,
      LogTestStatus.Warn => 3,
      LogTestStatus.Unknown => 2,
      LogTestStatus.NA => 1,
      _ => 0,
    };
    return Severity(first) >= Severity(second) ? first : second;
  }

  private static LogTestResult Result(string name, LogTestStatus status, string message) =>
      new(name, status, message);
}
