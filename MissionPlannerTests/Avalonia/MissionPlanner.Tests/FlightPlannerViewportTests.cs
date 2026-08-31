using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;
using MissionPlanner.ArduPilot;
using MissionPlanner.Controls;

namespace MissionPlanner.Tests;

public class FlightPlannerViewportTests {
  [AvaloniaFact]
  public void Waypoint_redraw_preserves_an_initialized_viewport() {
    var map = new FlightPlannerMap();
    var window = new Window { Width = 1200, Height = 800, Content = map };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();

      map.CenterOnAndZoom(50.344, 30.88, 14);
      map.SetHome(37.619373, -122.376637, 5.28);
      map.SetWaypoints(KyivWaypoints(2), 50, 80, Firmwares.ArduPlane);
      Dispatcher.UIThread.RunJobs();
      Mapsui.Viewport expected = map.Map.Navigator.Viewport;

      WritableLayer route = RouteLayer(map);
      route.DataChanged += (_, _) => {
        if (route.GetFeatures().OfType<GeometryFeature>().Count() == 2) {
          Dispatcher.UIThread.Post(() => map.Map.Navigator.ZoomToBox(route.Extent));
        }
      };

      map.SetWaypoints(KyivWaypoints(3), 50, 80, Firmwares.ArduPlane);
      Dispatcher.UIThread.RunJobs();
      Mapsui.Viewport actual = map.Map.Navigator.Viewport;

      Assert.Equal(expected.CenterX, actual.CenterX, 3);
      Assert.Equal(expected.CenterY, actual.CenterY, 3);
      Assert.Equal(expected.Resolution, actual.Resolution, 6);
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void Distant_home_closing_route_remains_solid() {
    var map = new FlightPlannerMap();
    map.SetHome(37.619373, -122.376637, 5.28);
    map.SetWaypoints(KyivWaypoints(3), 50, 80, Firmwares.ArduPlane);

    GeometryFeature homeRoute = HomeRoute(map);
    Assert.True(Assert.IsType<Mapsui.MRect>(homeRoute.Extent).Width > 10_000_000);
    Assert.Equal(PenStyle.Solid, LineStyle(homeRoute));
  }

  [AvaloniaFact]
  public void Nearby_home_closing_route_remains_dashed() {
    var map = new FlightPlannerMap();
    map.SetHome(50.3440, 30.8940, 100);
    map.SetWaypoints(KyivWaypoints(3), 50, 80, Firmwares.ArduPlane);

    Assert.Equal(PenStyle.Dash, LineStyle(HomeRoute(map)));
  }

  private static GeometryFeature HomeRoute(FlightPlannerMap map) {
    GeometryFeature[] features = RouteLayer(map).GetFeatures().OfType<GeometryFeature>().ToArray();
    Assert.Equal(2, features.Length);
    return Assert.Single(features,
        feature => Assert.Single(feature.Styles.OfType<VectorStyle>()).Line?.Width == 2);
  }

  private static PenStyle LineStyle(GeometryFeature feature) =>
      Assert.IsType<Pen>(Assert.Single(feature.Styles.OfType<VectorStyle>()).Line).PenStyle;

  private static WritableLayer RouteLayer(FlightPlannerMap map) =>
      Assert.IsAssignableFrom<WritableLayer>(
          Assert.Single(map.Map.Layers, candidate => candidate.Name == "Route"));

  private static IReadOnlyList<(
      int Seq, double Lat, double Lng, ushort Cmd, double P1, double P2, double P3, double P4)>
      KyivWaypoints(int count) => new[] {
        Waypoint(0, 50.3443012, 30.8947693),
        Waypoint(1, 50.3475125, 30.8815415),
        Waypoint(2, 50.3468588, 30.8318016),
      }.Take(count).ToArray();

  private static (
      int Seq, double Lat, double Lng, ushort Cmd, double P1, double P2, double P3, double P4)
      Waypoint(int seq, double lat, double lng) =>
      (seq, lat, lng, (ushort)MAVLink.MAV_CMD.WAYPOINT, 0, 0, 0, 0);
}
