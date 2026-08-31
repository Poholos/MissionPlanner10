using Avalonia.Headless.XUnit;
using MissionPlanner.ArduPilot;
using MissionPlanner.Controls;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

public class FlightMapOverlayTests {
  [Theory]
  [InlineData(MAVLink.MAV_FRAME.GLOBAL)]
  [InlineData((MAVLink.MAV_FRAME)5)] // MAV_FRAME_GLOBAL_INT
  [InlineData(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT)]
  [InlineData((MAVLink.MAV_FRAME)6)] // MAV_FRAME_GLOBAL_RELATIVE_ALT_INT
  [InlineData(MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT)]
  [InlineData((MAVLink.MAV_FRAME)11)] // MAV_FRAME_GLOBAL_TERRAIN_ALT_INT
  public void Global_mission_frames_are_renderable(MAVLink.MAV_FRAME frame) {
    var item = Item(frame, 34.1234567, 33.7654321);

    Assert.True(MapView.TryGlobalPosition(item, out double lat, out double lng));
    Assert.Equal(34.1234567, lat, 7);
    Assert.Equal(33.7654321, lng, 7);
  }

  [Fact]
  public void Local_mission_frames_are_not_misread_as_latitude_longitude() {
    var item = Item(MAVLink.MAV_FRAME.LOCAL_NED, 34.1, 33.2);

    Assert.False(MapView.TryGlobalPosition(item, out _, out _));
  }

  [Theory]
  [InlineData(0, 0)]
  [InlineData(91, 33)]
  [InlineData(34, 181)]
  public void Invalid_global_coordinates_are_not_rendered(double lat, double lng) {
    var item = Item(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, lat, lng);

    Assert.False(MapView.TryGlobalPosition(item, out _, out _));
  }

  [AvaloniaFact]
  public void Flight_map_contains_operational_overlay_layers() {
    var map = new MapView();
    var names = map.Map.Layers.Select(layer => layer.Name).ToArray();

    Assert.Contains("Mission route", names);
    Assert.Contains("Mission waypoints", names);
    Assert.Contains("GeoFence", names);
    Assert.Contains("Rally points", names);
    Assert.Contains("Guided target", names);
    Assert.Contains("POI", names);
    Assert.Contains("ADS-B / AIS traffic", names);
    Assert.Contains("Camera overlap count", names);
    Assert.Contains("Camera target", names);
    Assert.Contains("Other vehicles", names);
  }

  [AvaloniaFact]
  public void Flight_map_scopes_vehicle_symbol_to_the_point_feature() {
    var map = new MapView();

    map.ShowSampleMarker(34, 33);

    Mapsui.Layers.WritableLayer layer = Assert.IsType<Mapsui.Layers.WritableLayer>(
        Assert.Single(map.Map.Layers, candidate => candidate.Name == "Vehicle"));
    Assert.Null(layer.Style);
    Mapsui.Layers.PointFeature marker = Assert.Single(
        layer.GetFeatures().OfType<Mapsui.Layers.PointFeature>());
    Mapsui.Styles.SymbolStyle style = Assert.Single(
        marker.Styles.OfType<Mapsui.Styles.SymbolStyle>());
    Assert.Equal(Mapsui.Styles.SymbolType.Triangle, style.SymbolType);
  }

  [AvaloniaFact]
  public void Live_vehicle_bearing_and_radius_overlays_do_not_duplicate_the_symbol() {
    string[] settingKeys = {
      "GMapMarkerBase_DisplayHeading",
      "GMapMarkerBase_DisplayNavBearing",
      "GMapMarkerBase_DisplayCOG",
      "GMapMarkerBase_DisplayTarget",
      "GMapMarkerBase_DisplayRadius",
    };
    var saved = settingKeys.ToDictionary(key => key, key => Utilities.Settings.Instance[key]);
    try {
      foreach (string key in settingKeys) {
        Utilities.Settings.Instance[key] = bool.TrueString;
      }

      using var link = new MAVLinkInterface();
      MAVState mav = link.MAV;
      mav.aptype = MAVLink.MAV_TYPE.FIXED_WING;
      mav.cs.yaw = 10;
      mav.cs.nav_bearing = 20;
      mav.cs.groundcourse = 30;
      mav.cs.target_bearing = 40;
      mav.cs.groundspeed = 20;
      mav.cs.roll = 20;
      var map = new MapView();

      map.PopulateVehicleLayer(mav, new Mapsui.MPoint(1000, 2000), resolution: 2);

      Mapsui.Layers.WritableLayer layer = Assert.IsType<Mapsui.Layers.WritableLayer>(
          Assert.Single(map.Map.Layers, candidate => candidate.Name == "Vehicle"));
      Assert.Null(layer.Style);
      Mapsui.IFeature[] features = layer.GetFeatures().ToArray();
      Mapsui.Layers.PointFeature marker = Assert.Single(
          features.OfType<Mapsui.Layers.PointFeature>());
      Assert.Single(marker.Styles.OfType<Mapsui.Styles.SymbolStyle>());
      Mapsui.Nts.GeometryFeature[] overlays =
          features.OfType<Mapsui.Nts.GeometryFeature>().ToArray();
      Assert.Equal(5, overlays.Length);
      Assert.All(overlays, overlay => {
        Assert.Contains(overlay.Styles, style => style is Mapsui.Styles.VectorStyle);
        Assert.DoesNotContain(overlay.Styles, style => style is Mapsui.Styles.SymbolStyle);
      });
    } finally {
      foreach ((string key, string? value) in saved) {
        Utilities.Settings.Instance[key] = value;
      }
    }
  }

  [AvaloniaFact]
  public void Bearing_overlays_keep_configured_map_length_when_viewport_zooms() {
    string[] settingKeys = {
      "GMapMarkerBase_Length",
      "GMapMarkerBase_DisplayHeading",
      "GMapMarkerBase_DisplayNavBearing",
      "GMapMarkerBase_DisplayCOG",
      "GMapMarkerBase_DisplayTarget",
      "GMapMarkerBase_DisplayRadius",
    };
    var saved = settingKeys.ToDictionary(key => key, key => Utilities.Settings.Instance[key]);
    try {
      Utilities.Settings.Instance["GMapMarkerBase_Length"] = "500";
      Utilities.Settings.Instance["GMapMarkerBase_DisplayHeading"] = bool.TrueString;
      foreach (string key in settingKeys.Skip(2)) {
        Utilities.Settings.Instance[key] = bool.FalseString;
      }

      using var link = new MAVLinkInterface();
      MAVState mav = link.MAV;
      mav.cs.yaw = 90;
      var point = new Mapsui.MPoint(1000, 2000);
      var map = new MapView();
      map.Map.Navigator.SetSize(1000, 800);
      map.Map.Navigator.CenterOnAndZoomTo(point, 2);
      map.PopulateVehicleLayer(mav, point, map.Map.Navigator.Viewport.Resolution);

      Assert.Equal(250, BearingLineScreenLength(map), 6);

      map.Map.Navigator.CenterOnAndZoomTo(point, 8);

      Assert.Equal(8, map.Map.Navigator.Viewport.Resolution, 6);
      Assert.Equal(62.5, BearingLineScreenLength(map), 6);

      // A periodic telemetry redraw must not expand the vector back to 500 screen pixels.
      map.PopulateVehicleLayer(mav, point, map.Map.Navigator.Viewport.Resolution);

      Assert.Equal(62.5, BearingLineScreenLength(map), 6);
    } finally {
      foreach ((string key, string? value) in saved) {
        Utilities.Settings.Instance[key] = value;
      }
    }
  }

  [Theory]
  [InlineData(Firmwares.ArduCopter2, 1, 2, 150, true)]
  [InlineData(Firmwares.ArduCopter2, 1, 3, 150, true)]
  [InlineData(Firmwares.ArduCopter2, 0, 2, 150, false)]
  [InlineData(Firmwares.ArduCopter2, 1, 1, 150, false)]
  [InlineData(Firmwares.ArduCopter2, 1, 2, 0, false)]
  [InlineData(Firmwares.ArduPlane, 1, 2, 150, false)]
  public void Legacy_copter_fence_requires_enabled_horizontal_circle_bit(
      Firmwares firmware, double enabled, double type, double radius, bool expected) {
    Assert.Equal(expected,
        MapView.ShouldShowLegacyCircularFence(firmware, enabled, type, radius));
  }

  [Theory]
  [InlineData(MAVLink.MAV_TYPE.FIXED_WING, true)]
  [InlineData(MAVLink.MAV_TYPE.VTOL_DUOROTOR, true)]
  [InlineData(MAVLink.MAV_TYPE.VTOL_RESERVED5, true)]
  [InlineData(MAVLink.MAV_TYPE.QUADROTOR, false)]
  [InlineData(MAVLink.MAV_TYPE.HELICOPTER, false)]
  [InlineData(MAVLink.MAV_TYPE.GROUND_ROVER, false)]
  public void Dynamic_turn_radius_matches_the_upstream_fixed_wing_marker_contract(
      MAVLink.MAV_TYPE vehicleType, bool expected) {
    Assert.Equal(expected, MapView.SupportsTurnRadiusOverlay(vehicleType));
  }

  [Fact]
  public void Oa_db_radius_builds_a_closed_red_map_polygon() {
    var feature = MapView.BuildTrafficRadius(33, 34, 25);

    var polygon = Assert.IsType<NetTopologySuite.Geometries.Polygon>(feature.Geometry);
    Assert.Equal(49, polygon.ExteriorRing.Coordinates.Length);
    Assert.Equal(polygon.ExteriorRing.Coordinates[0], polygon.ExteriorRing.Coordinates[^1]);
    Assert.Contains(feature.Styles, style => style is Mapsui.Styles.VectorStyle);
  }

  [Fact]
  public void Ais_vessels_use_a_distinct_boat_marker_style() {
    var style = MavMarker.Vessel(123);

    Assert.Equal(Mapsui.Styles.SymbolType.Rectangle, style.SymbolType);
    Assert.Equal(123, style.SymbolRotation);
  }

  [Fact]
  public void Mission_progress_uses_completed_legs_minus_current_wp_distance() {
    var items = new[] {
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          0, Item(MAVLink.MAV_FRAME.GLOBAL, 34, 33)),
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          1, Item(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, 34, 33.001)),
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          2, Item(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, 34, 33.002)),
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          3, Item(MAVLink.MAV_FRAME.LOCAL_NED, 34, 33.003)),
    };

    var info = FlightDataViewModel.CalculateMissionProgress(34, 33, items, 1, 40);

    Assert.Equal(2, info.ItemCount);
    Assert.InRange(info.TotalDistance, 180, 190);
    Assert.InRange(info.TravelledDistance, 50, 55);
  }

  private static MAVLink.mavlink_mission_item_int_t Item(
      MAVLink.MAV_FRAME frame, double lat, double lng) => new() {
        frame = (byte)frame,
        x = (int)Math.Round(lat * 1e7),
        y = (int)Math.Round(lng * 1e7),
      };

  private static double BearingLineScreenLength(MapView map) {
    Mapsui.Layers.WritableLayer layer = Assert.IsType<Mapsui.Layers.WritableLayer>(
        Assert.Single(map.Map.Layers, candidate => candidate.Name == "Vehicle"));
    Mapsui.Nts.GeometryFeature feature = Assert.Single(
        layer.GetFeatures().OfType<Mapsui.Nts.GeometryFeature>());
    var line = Assert.IsType<NetTopologySuite.Geometries.LineString>(feature.Geometry);
    return line.Coordinates[0].Distance(line.Coordinates[1])
        / map.Map.Navigator.Viewport.Resolution;
  }
}
