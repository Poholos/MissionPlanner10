using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class LogAnalyzerTests {
  [Fact]
  public void Classify_higher_is_worse() {
    Assert.Equal(LogTestStatus.Good, LogAnalyzer.Classify(10, 30, 60, higherWorse: true));
    Assert.Equal(LogTestStatus.Warn, LogAnalyzer.Classify(40, 30, 60, higherWorse: true));
    Assert.Equal(LogTestStatus.Fail, LogAnalyzer.Classify(70, 30, 60, higherWorse: true));
  }

  [Fact]
  public void Classify_lower_is_worse() {
    Assert.Equal(LogTestStatus.Good, LogAnalyzer.Classify(12, 7, 5, higherWorse: false));
    Assert.Equal(LogTestStatus.Warn, LogAnalyzer.Classify(6, 7, 5, higherWorse: false));
    Assert.Equal(LogTestStatus.Fail, LogAnalyzer.Classify(4, 7, 5, higherWorse: false));
  }

  [Fact]
  public void Classify_nan_is_na() {
    Assert.Equal(LogTestStatus.NA, LogAnalyzer.Classify(double.NaN, 30, 60, higherWorse: true));
  }

  [Fact]
  public void Analyze_includes_every_enabled_official_check() {
    List<LogTestResult> results = LogAnalyzer.Analyze(Data());

    Assert.Equal(17, results.Count);
    Assert.Equal(new[] {
      "Empty", "Vibration", "GPS", "VCC", "Compass", "Motor balance", "NaN",
      "Event/Failsafe", "Brownout", "Duplicate data", "Parameters", "PM", "Pitch/Roll",
      "Thrust", "IMU mismatch", "Autotune", "Optical flow",
    }, results.Select(result => result.Name));
  }

  [Fact]
  public void Gps_and_event_checks_report_logged_glitch() {
    LogAnalyzerData data = Data(Records(
        ("GPS", [Sample(10, ("NSats", 4), ("HDop", 2))]),
        ("ERR", [Sample(11, ("Subsys", 11), ("ECode", 2))])));

    Assert.Equal(LogTestStatus.Fail, Find(data, "GPS").Status);
    Assert.Equal(LogTestStatus.Fail, Find(data, "Event/Failsafe").Status);
  }

  [Fact]
  public void Brownout_detects_log_ending_armed_and_airborne() {
    LogAnalyzerData data = Data(Records(
        ("EV", [Sample(1, ("Id", 10))]),
        ("CTUN", [Sample(100, ("BarAlt", 7.5), ("ThrOut", 500))])));

    LogTestResult result = Find(data, "Brownout");

    Assert.Equal(LogTestStatus.Fail, result.Status);
    Assert.Contains("ends armed", result.Message);
  }

  [Fact]
  public void Duplicate_data_detects_repeated_nonconstant_pitch_window() {
    var samples = new List<LogAnalyzerSample>();
    for (int index = 0; index < 100; index++) {
      double value = index is >= 40 and < 60 ? index - 40 : index;
      samples.Add(Sample(index, ("Pitch", value)));
    }
    LogAnalyzerData data = Data(Records(("ATT", samples.ToArray())));

    Assert.Equal(LogTestStatus.Fail, Find(data, "Duplicate data").Status);
  }

  [Fact]
  public void Imu_mismatch_uses_time_aligned_low_pass_difference() {
    LogAnalyzerSample[] primary = Enumerable.Range(1, 400)
        .Select(line => Sample(line, ("AccX", 4), ("AccY", 0), ("AccZ", 9.8)))
        .ToArray();
    LogAnalyzerSample[] secondary = Enumerable.Range(1, 400)
        .Select(line => Sample(line, ("AccX", 0), ("AccY", 0), ("AccZ", 9.8)))
        .ToArray();
    LogAnalyzerData data = Data(Records(("IMU", primary), ("IMU2", secondary)));

    Assert.Equal(LogTestStatus.Fail, Find(data, "IMU mismatch").Status);
  }

  [Fact]
  public void Optical_flow_returns_reviewable_scalers_without_writing_files() {
    LogAnalyzerSample[] flow = Enumerable.Range(1, 140)
        .Select(line => {
          double body = (line % 20 - 10) / 10.0;
          if (body == 0) {
            body = 0.1;
          }
          return Sample(line,
              ("bodyX", body), ("flowX", body),
              ("bodyY", body), ("flowY", body), ("Qual", 200));
        })
        .ToArray();
    LogAnalyzerData data = Data(
        Records(
          ("OF", flow),
          ("ATT", [Sample(1, ("Roll", 20), ("Pitch", 20))])),
        parameters: new Dictionary<string, double> {
          ["FLOW_FXSCALER"] = 0,
          ["FLOW_FYSCALER"] = 0,
        });

    LogTestResult result = Find(data, "Optical flow");

    Assert.Equal(LogTestStatus.Good, result.Status);
    Assert.Contains("FLOW_FXSCALER=0", result.Message);
  }

  [Fact]
  public void Copter_safety_checks_detect_excessive_lean_and_insufficient_thrust() {
    LogAnalyzerSample[] attitude = Enumerable.Range(1, 120)
        .Select(line => Sample(line,
            ("Roll", line == 1 ? 70 : 0), ("Pitch", 0)))
        .ToArray();
    LogAnalyzerSample[] tuning = Enumerable.Range(1, 120)
        .Select(line => Sample(line,
            ("BarAlt", 5), ("ThrOut", 800), ("CRate", 20)))
        .ToArray();
    LogAnalyzerData data = Data(
        Records(("ATT", attitude), ("CTUN", tuning)),
        LogAnalyzerVehicleType.Copter,
        new Dictionary<string, double> { ["ANGLE_MAX"] = 4500 });

    Assert.Equal(LogTestStatus.Fail, Find(data, "Pitch/Roll").Status);
    Assert.Equal(LogTestStatus.Fail, Find(data, "Thrust").Status);
  }

  [Theory]
  [InlineData("ACRO", 1)]
  [InlineData("SPORT", 13)]
  [InlineData("FLIP", 14)]
  [InlineData("AUTOTUNE", 15)]
  public void Pitch_roll_ignores_expected_high_lean_flight_modes(string modeName, int modeNumber) {
    var mode = new LogAnalyzerSample(
        1,
        0.1,
        new Dictionary<string, double> { ["ModeNum"] = modeNumber },
        new Dictionary<string, string> { ["Mode"] = modeName });
    LogAnalyzerData data = Data(
        Records(
          ("MODE", [mode]),
          ("ATT", [Sample(10, ("Roll", 80), ("Pitch", 0))]),
          ("CTUN", [Sample(10, ("BarAlt", 5))])),
        LogAnalyzerVehicleType.Copter,
        new Dictionary<string, double> { ["ANGLE_MAX"] = 4500 });

    Assert.Equal(LogTestStatus.Good, Find(data, "Pitch/Roll").Status);
  }

  [Theory]
  [InlineData(33, LogTestStatus.Good)]
  [InlineData(34, LogTestStatus.Fail)]
  [InlineData(35, LogTestStatus.Fail)]
  public void Autotune_reports_final_session_outcome(int outcome, LogTestStatus expected) {
    LogAnalyzerData data = Data(Records(
        ("EV", [Sample(1, ("Id", 30)), Sample(2, ("Id", outcome))]),
        ("ATUN", [Sample(2, ("Axis", 0))])),
        LogAnalyzerVehicleType.Copter);

    Assert.Equal(expected, Find(data, "Autotune").Status);
  }

  private static LogTestResult Find(LogAnalyzerData data, string name) =>
      Assert.Single(LogAnalyzer.Analyze(data), result => result.Name == name);

  private static LogAnalyzerData Data(
      IReadOnlyDictionary<string, IReadOnlyList<LogAnalyzerSample>>? records = null,
      LogAnalyzerVehicleType vehicleType = LogAnalyzerVehicleType.Unknown,
      IReadOnlyDictionary<string, double>? parameters = null,
      int lineCount = 1000) =>
      new(lineCount, vehicleType,
          records ?? new Dictionary<string, IReadOnlyList<LogAnalyzerSample>>(
              StringComparer.OrdinalIgnoreCase),
          parameters ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

  private static IReadOnlyDictionary<string, IReadOnlyList<LogAnalyzerSample>> Records(
      params (string type, LogAnalyzerSample[] samples)[] groups) =>
      groups.ToDictionary(
          group => group.type,
          group => (IReadOnlyList<LogAnalyzerSample>)group.samples,
          StringComparer.OrdinalIgnoreCase);

  private static LogAnalyzerSample Sample(
      int line, params (string field, double value)[] values) =>
      new(line, line / 10.0,
          values.ToDictionary(
              value => value.field,
              value => value.value,
              StringComparer.OrdinalIgnoreCase));
}
