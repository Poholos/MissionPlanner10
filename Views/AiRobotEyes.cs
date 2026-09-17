using Avalonia.Media;

namespace MissionPlanner.Views;

/// <summary>Eye shapes of the AI button's robot: circles, vertical bars and horizontal bars, cycled per MCP exchange.</summary>
internal static class AiRobotEyes {
  internal static readonly string[] Shapes = [
    "M 5.5,9.5 a 2,2 0 1,0 4,0 a 2,2 0 1,0 -4,0 Z M 12.5,9.5 a 2,2 0 1,0 4,0 a 2,2 0 1,0 -4,0 Z",
    "M 6.75,7.5 h 1.5 v 4 h -1.5 Z M 13.75,7.5 h 1.5 v 4 h -1.5 Z",
    "M 5.5,8.75 h 4 v 1.5 h -4 Z M 12.5,8.75 h 4 v 1.5 h -4 Z",
  ];
  private static readonly Geometry[] Parsed = new Geometry[Shapes.Length];
  internal static int Next(int shape) => (shape + 1) % Shapes.Length;
  internal static Geometry Geometry(int shape) => Parsed[shape] ??= Avalonia.Media.Geometry.Parse(Shapes[shape]);
}
