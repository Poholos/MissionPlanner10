using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public class DroneCanInspectorTests {
  private sealed class Nested {
    public short Scalar;
  }

  private sealed class Sample {
    public float[] Values = [1.25f, -2.5f];
    public Nested Detail = new() { Scalar = -17 };
    public Nested[] Items = [new() { Scalar = 42 }];
    public string Text = "not numeric";
  }

  [Fact]
  public void Extracts_numeric_arrays_and_nested_DroneCAN_fields() {
    var sample = new Sample();

    Assert.True(DroneCanGraphSampleExtractor.TryRead(
        sample, ["Values"], out IReadOnlyList<double> values));
    Assert.Equal([1.25, -2.5], values);
    Assert.True(DroneCanGraphSampleExtractor.TryRead(
        sample, ["Detail", "Scalar"], out IReadOnlyList<double> scalar));
    Assert.Equal([-17d], scalar);
    Assert.True(DroneCanGraphSampleExtractor.TryRead(
        sample, ["Items[0]", "Scalar"], out IReadOnlyList<double> arrayMember));
    Assert.Equal([42d], arrayMember);
  }

  [Fact]
  public void Rejects_missing_non_numeric_and_out_of_range_DroneCAN_fields() {
    var sample = new Sample();

    Assert.False(DroneCanGraphSampleExtractor.TryRead(
        sample, ["Missing"], out _));
    Assert.False(DroneCanGraphSampleExtractor.TryRead(
        sample, ["Text"], out _));
    Assert.False(DroneCanGraphSampleExtractor.TryRead(
        sample, ["Items[2]", "Scalar"], out _));
  }

  [Fact]
  public void Subscriber_log_retains_only_the_requested_number_of_lines() {
    var lines = new List<string> { "old-1", "old-2" };

    DroneCanSubscriberWindow.AppendBoundedLines(lines, "new-1\r\nnew-2\nnew-3", 3);

    Assert.Equal(["new-1", "new-2", "new-3"], lines);
  }
}
