using System.Text.Json;
using MissionPlanner.Services.Mcp;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class McpFlightAnalysisTests {
  private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

  [Fact]
  public void Parameter_snapshot_does_not_leak_future_values_and_records_changes() {
    WithLog([
      "PARM, 1000000, ATC_RAT_RLL_P, 0.5, 0.2",
      "PARM, 2000000, ATC_RAT_RLL_P, 0.8, 0.2",
      "PARM, 3000000, ATC_RAT_RLL_P, 0.8, 0.2",
      "PARM, 1000000, AUTH_KEY, 1234, 0",
      "PARM, 4000000, INS_GYRO_FILTER, 20, 10",
    ], log => {
      var early = Json(McpFlightAnalysis.Parameters(log, 1.5, "", 0, 100, default));
      var item = Assert.Single(early.GetProperty("parameters").EnumerateArray());
      Assert.Equal(0.5, item.GetProperty("value").GetDouble());
      Assert.Equal(1, item.GetProperty("timeSeconds").GetDouble());
      Assert.Equal(0, item.GetProperty("changes").GetInt64());
      var later = Json(McpFlightAnalysis.Parameters(log, 3, "", 0, 100, default));
      item = Assert.Single(later.GetProperty("parameters").EnumerateArray());
      Assert.Equal(0.8, item.GetProperty("value").GetDouble());
      Assert.Equal(3, item.GetProperty("records").GetInt64());
      Assert.Equal(1, item.GetProperty("changes").GetInt64());
      var before = Json(McpFlightAnalysis.Parameters(log, 0.5, "", 0, 100, default));
      Assert.Empty(before.GetProperty("parameters").EnumerateArray());
      var page = Json(McpFlightAnalysis.Parameters(log, 4, "", 1, 1, default));
      Assert.Equal(2, page.GetProperty("total").GetInt32());
      Assert.Equal("INS_GYRO_FILTER", Assert.Single(page.GetProperty("parameters").EnumerateArray()).GetProperty("name").GetString());
    });
  }

  [Fact]
  public void Vibration_keeps_instances_separate_and_counts_clipping_resets_without_negative_deltas() {
    WithLog([
      "VIBE, 1000000, 0, 10, 20, 30, 100, 0, 0",
      "VIBE, 2000000, 0, 40, 60, 70, 105, 0, 0",
      "VIBE, 3000000, 0, 20, 10, 40, 2, 0, 0",
      "VIBE, 4000000, 0, 10, 10, 20, 5, 0, 0",
      "VIBE, 2000000, 1, 5, 5, 5, 105, 0, 0",
      "VIBE, 5000000, 0, 200, 200, 200, 1000, 0, 0",
    ], log => {
      var report = Json(McpFlightAnalysis.Vibration(log, 1, 4, default));
      var sensors = report.GetProperty("sensors");
      Assert.Equal(2, sensors.GetArrayLength());
      var x = sensors[0].GetProperty("axes").GetProperty("VibeX");
      Assert.Equal(20, x.GetProperty("mean").GetDouble());
      Assert.Equal(40, x.GetProperty("max").GetDouble());
      Assert.Equal(1, x.GetProperty("above30Samples").GetInt64());
      Assert.Equal(0, x.GetProperty("above60Samples").GetInt64());
      Assert.Equal(1, sensors[0].GetProperty("axes").GetProperty("VibeZ").GetProperty("above60Samples").GetInt64());
      var clipping = sensors[0].GetProperty("clipping").GetProperty("Clip0");
      Assert.Equal(8, clipping.GetProperty("observedIncrease").GetDouble());
      Assert.Equal(1, clipping.GetProperty("resetsOrWraps").GetInt64());
      Assert.Equal(0, sensors[1].GetProperty("clipping").GetProperty("Clip0").GetProperty("observedIncrease").GetDouble());
      Assert.Empty(Json(McpFlightAnalysis.Vibration(log, 10, 11, default)).GetProperty("sensors").EnumerateArray());
    });
  }

  [Fact]
  public void Modern_per_imu_clip_counter_and_missing_axes_are_reported() {
    WithLog([
      "FMT, 131, 28, VIBE, QBfffI, TimeUS,IMU,VibeX,VibeY,VibeZ,Clip",
      "VIBE, 1000000, 0, 10, NaN, 30, 15",
      "VIBE, 2000000, 0, 20, NaN, 40, 19",
      "VIBE, 1000000, 1, 10, 20, 30, 100",
      "VIBE, 2000000, 1, 10, 20, 30, 101",
    ], log => {
      var sensors = Json(McpFlightAnalysis.Vibration(log, 0, 3, default)).GetProperty("sensors");
      Assert.Equal(4, sensors[0].GetProperty("clipping").GetProperty("Clip").GetProperty("observedIncrease").GetDouble());
      Assert.Equal(1, sensors[1].GetProperty("clipping").GetProperty("Clip").GetProperty("observedIncrease").GetDouble());
      Assert.False(sensors[0].GetProperty("clipping").TryGetProperty("Clip0", out _));
      Assert.False(sensors[0].GetProperty("axes").TryGetProperty("VibeY", out _));
    });
  }

  [Fact]
  public void Overview_reports_counts_bounds_and_instances_without_treating_metadata_as_boot_time_zero() {
    WithLog(["VIBE, 2000000, 1, 1, 2, 3, 0, 0, 0", "VIBE, 3000000, 0, 1, 2, 3, 0, 0, 0"], log => {
      var messages = Json(McpFlightAnalysis.Overview(log, default)).GetProperty("messages").EnumerateArray().ToArray();
      var vibe = messages.Single(m => m.GetProperty("type").GetString() == "VIBE");
      Assert.Equal(2, vibe.GetProperty("count").GetInt64());
      Assert.Equal(2, vibe.GetProperty("startSeconds").GetDouble());
      Assert.Equal(3, vibe.GetProperty("endSeconds").GetDouble());
      Assert.Equal(new[] { "0", "1" }, vibe.GetProperty("instances").EnumerateArray().Select(i => i.GetString()));
      var fmt = messages.Single(m => m.GetProperty("type").GetString() == "FMT");
      Assert.Equal(JsonValueKind.Null, fmt.GetProperty("startSeconds").ValueKind);
    });
  }

  [Fact]
  public void Analysis_rejects_invalid_windows_cancels_scans_and_detects_clock_reversal() {
    WithLog([
      "PARM, 2000000, TEST, 1, 0", "PARM, 1000000, TEST, 2, 0",
      "VIBE, 2000000, 0, 1, 2, 3, 0, 0, 0", "VIBE, 1000000, 0, 1, 2, 3, 0, 0, 0",
    ], log => {
      Assert.Throws<ArgumentException>(() => McpFlightAnalysis.Parameters(log, double.NaN, "", 0, 100, default));
      Assert.Throws<ArgumentException>(() => McpFlightAnalysis.Parameters(log, 10, "", 0, 100, default));
      Assert.Throws<ArgumentException>(() => McpFlightAnalysis.Vibration(log, 0, 10, default));
      Assert.Throws<ArgumentException>(() => McpFlightAnalysis.Vibration(log, 10, 0, default));
      using var stop = new CancellationTokenSource(); stop.Cancel();
      Assert.Throws<OperationCanceledException>(() => McpFlightAnalysis.Overview(log, stop.Token));
      Assert.Throws<OperationCanceledException>(() => McpFlightAnalysis.Parameters(log, 10, "", 0, 100, stop.Token));
      Assert.Throws<OperationCanceledException>(() => McpFlightAnalysis.Vibration(log, 0, 10, stop.Token));
    });
  }

  private static void WithLog(string[] records, Action<DFLogBuffer> action) {
    string path = Path.Combine(Path.GetTempPath(), $"mp-mcp-report-{Guid.NewGuid():N}.log");
    try {
      File.WriteAllLines(path, [
        "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
        "FMT, 130, 40, PARM, QNff, TimeUS,Name,Value,Default",
        "FMT, 131, 36, VIBE, QBfffIII, TimeUS,IMU,VibeX,VibeY,VibeZ,Clip0,Clip1,Clip2",
        .. records,
      ]);
      using var log = new DFLogBuffer(path); action(log);
    } finally { File.Delete(path); }
  }
}
