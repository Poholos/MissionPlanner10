using MissionPlanner.Utilities;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public sealed class DroneCanParameterFileTests {
  [Fact]
  public void Text_parameter_files_round_trip_numeric_string_comma_space_and_empty_values() {
    string path = Path.Combine(Path.GetTempPath(), $"dronecan-{Guid.NewGuid():N}.param");
    try {
      ParamFile.SaveTextParamFile(path, new Dictionary<string, string> {
        ["NUMBER"] = "12.5",
        ["PATH"] = "/camera/main stream,low latency",
        ["EMPTY"] = "",
      });

      Dictionary<string, string> loaded = ParamFile.LoadTextParamFile(path);

      Assert.Equal("12.5", loaded["NUMBER"]);
      Assert.Equal("/camera/main stream,low latency", loaded["PATH"]);
      Assert.Equal("", loaded["EMPTY"]);
      Assert.Equal(new[] { "EMPTY", "NUMBER", "PATH" },
          File.ReadLines(path).Select(line => line.Split(',')[0]));
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [Fact]
  public void Text_parameter_export_rejects_record_injection() {
    string path = Path.Combine(Path.GetTempPath(), $"dronecan-{Guid.NewGuid():N}.param");
    try {
      Assert.Throws<InvalidDataException>(() => ParamFile.SaveTextParamFile(path,
          new Dictionary<string, string> { ["SAFE"] = "one\nINJECTED,2" }));
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [Fact]
  public void DroneCan_value_conversion_preserves_string_type_and_rejects_bad_numbers() {
    var textParameter = new DroneCanParam { Name = "PATH", IsString = true };
    var numericParameter = new DroneCanParam { Name = "RATE", IsString = false };

    Assert.True(ConfigDroneCanViewModel.TryConvertParameterValue(
        textParameter, "123", out object text));
    Assert.IsType<string>(text);
    Assert.Equal("123", text);

    Assert.True(ConfigDroneCanViewModel.TryConvertParameterValue(
        numericParameter, "12.5", out object number));
    Assert.Equal(12.5, Assert.IsType<double>(number));
    Assert.False(ConfigDroneCanViewModel.TryConvertParameterValue(
        numericParameter, "not-a-number", out _));
  }

  [Theory]
  [InlineData("-1", false)]
  [InlineData("0", true)]
  [InlineData("5.5", true)]
  [InlineData("10", true)]
  [InlineData("11", false)]
  public void DroneCan_numeric_values_respect_reported_node_limits(
      string text, bool expected) {
    var parameter = new DroneCanParam {
      Name = "RATE",
      IsString = false,
      Min = "0",
      Max = "10",
    };

    Assert.Equal(expected,
        ConfigDroneCanViewModel.TryConvertParameterValue(parameter, text, out _));
  }
}
