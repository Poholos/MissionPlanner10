using Avalonia.Headless.XUnit;
using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public sealed class AiRobotEyesTests {
  [AvaloniaFact]
  public void Robot_eyes_cycle_through_three_distinct_shapes() {
    Assert.Equal(3, AiRobotEyes.Shapes.Distinct().Count());
    int shape = 0; var seen = new HashSet<int>();
    for (int i = 0; i < 3; i++) { shape = AiRobotEyes.Next(shape); seen.Add(shape); Assert.NotNull(AiRobotEyes.Geometry(shape)); }
    Assert.Equal(3, seen.Count); Assert.Equal(0, shape);
  }
}
