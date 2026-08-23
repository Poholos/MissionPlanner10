using System.Xml.Serialization;
using KMLib;
using MissionPlanner.Comms;
using DrawingMatrix = System.Drawing.Drawing2D.Matrix;

namespace MissionPlanner.Tests;

public sealed class NativeDependencyWarningTests {
  [Fact]
  public void Equal_drawing_matrices_have_equal_hash_codes() {
    using var first = new DrawingMatrix(1, 2, 3, 4, 5, 6);
    using var second = new DrawingMatrix(1, 2, 3, 4, 5, 6);

    Assert.Equal(first, second);
    Assert.Equal(first.GetHashCode(), second.GetHashCode());
  }

  [Fact]
  public void Kml_optional_color_mode_is_serialized_only_after_assignment() {
    var serializer = new XmlSerializer(typeof(ColorStyle));
    var style = new ColorStyle();

    using var initial = new StringWriter();
    serializer.Serialize(initial, style);
    Assert.DoesNotContain("colorMode", initial.ToString(), StringComparison.Ordinal);

    style.colorMode = ColorStyle.ColorMode.random;
    using var assigned = new StringWriter();
    serializer.Serialize(assigned, style);
    Assert.Contains("<colorMode>random</colorMode>", assigned.ToString(),
        StringComparison.Ordinal);
  }

  [Fact]
  public void Unopened_ble_stream_can_be_disposed_repeatedly_without_native_calls() {
    var stream = new CommsBLE();

    stream.Dispose();
    stream.Dispose();

    Assert.False(stream.IsOpen);
  }
}
