using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class DataFlashMetadataTests {
  [Fact]
  public void Parameter_export_is_sorted_and_uses_mission_planner_format() {
    string path = Path.Combine(Path.GetTempPath(), $"mp_params_{Guid.NewGuid():N}.param");
    try {
      DataFlashLog.ExportParameters([
        new DataFlashParameter("WPNAV_SPEED", "500", "1000"),
        new DataFlashParameter("ARMING_CHECK", "1", "1"),
      ], path);

      string[] lines = File.ReadAllLines(path);
      Assert.StartsWith("#", lines[0]);
      Assert.Equal("ARMING_CHECK,1", lines[1]);
      Assert.Equal("WPNAV_SPEED,500", lines[2]);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void Parameter_history_keeps_every_change_and_computes_final_values() {
    string path = Path.Combine(Path.GetTempPath(), $"mp_param_history_{Guid.NewGuid():N}.log");
    try {
      File.WriteAllLines(path, [
        "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
        "FMT, 130, 40, PARM, QNff, TimeUS,Name,Value,Default",
        "PARM, 1000000, ATC_STR_RAT_FF, 0.5, 0.2",
        "PARM, 2000000, ARMING_CHECK, 1, 1",
        "PARM, 3000000, ATC_STR_RAT_FF, 0.8, 0.2",
      ]);

      DataFlashParameterHistory history = DataFlashLog.ReadParameterHistory(path);

      Assert.Equal(3, history.Changes.Count);
      Assert.Equal(new[] { 1d, 2d, 3d },
          history.Changes.Select(change => change.TimeSeconds));
      Assert.Equal("0.8", history.FinalValues.Single(
          parameter => parameter.Name == "ATC_STR_RAT_FF").Value);
      Assert.Equal(2, history.FinalValues.Count);
    } finally {
      File.Delete(path);
    }
  }
}
