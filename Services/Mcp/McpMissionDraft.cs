using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpDraftHome(double Latitude, double Longitude, double AltitudeMetres);
internal sealed record McpDraftItem(ushort Command, byte Frame, double Latitude, double Longitude, double AltitudeMetres,
    double P1 = 0, double P2 = 0, double P3 = 0, double P4 = 0);
internal sealed record McpDraftValidation(bool Valid, string[] Errors, string[] Warnings);

internal static class McpMissionDraft {
  internal static McpDraftHome Home(FlightPlannerViewModel vm) => new(vm.HomeLat, vm.HomeLng, vm.HomeAlt);
  internal static McpDraftItem[] Items(FlightPlannerViewModel vm) => vm.Waypoints.Select(row => new McpDraftItem(
      row.Command, row.Frame, row.Lat, row.Lng, row.Alt, row.P1, row.P2, row.P3, row.P4)).ToArray();
  internal static string Revision(FlightPlannerViewModel vm) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
      JsonSerializer.Serialize(new { vm.MissionType, home = Home(vm), items = Items(vm) }, MissionPlannerMcpTools.JsonOptions))));
  internal static object Read(FlightPlannerViewModel vm, int offset, int count) {
    if (offset < 0 || count is < 1 or > 200) { throw new ArgumentException("offset >= 0; count 1..200."); }
    var items = Items(vm);
    return new { source = "Mission Planner UI draft; not onboard mission", revision = Revision(vm), missionType = vm.MissionType,
      home = Home(vm), total = items.Length, offset, nextOffset = Math.Min(items.Length, (long)offset + count),
      complete = (long)offset + count >= items.Length, items = items.Skip(offset).Take(count).ToArray(), canUndo = vm.CanUndo,
      units = "Coordinates WGS84 degrees; home altitude metres AMSL; item altitude metres in its MAV_FRAME; list index zero is displayed/onboard item 1 (home is separate)." };
  }
  internal static void RequireRevision(FlightPlannerViewModel vm, string expected) {
    if (vm.MissionType != "Mission") { throw new InvalidOperationException("Select Mission in the planner; Fence/Rally drafts are outside this API."); }
    if (!string.Equals(expected, Revision(vm), StringComparison.Ordinal)) { throw new InvalidOperationException("stale_revision: reread the draft and reconsider the change."); }
  }
  internal static McpDraftValidation Validate(McpDraftHome home, McpDraftItem[] items) {
    ArgumentNullException.ThrowIfNull(home); ArgumentNullException.ThrowIfNull(items);
    var errors = new List<string>(); var warnings = new List<string>();
    if (items.Length > 1000) { return new(false, ["At most 1000 draft items per replacement."], []); }
    if (!double.IsFinite(home.Latitude) || home.Latitude is < -90 or > 90 || !double.IsFinite(home.Longitude)
        || home.Longitude is < -180 or > 180 || !double.IsFinite(home.AltitudeMetres)) { errors.Add("Home requires finite WGS84 coordinates and metres AMSL."); }
    for (int i = 0; i < items.Length; i++) {
      var item = items[i];
      if (item == null) { errors.Add($"Item {i + 1} is null."); continue; }
      if (MissionCommandCatalog.GetName(item.Command) == null) { errors.Add($"Item {i + 1}: unknown command {item.Command}."); }
      if (item.Frame is not (0 or 3 or 10)) { errors.Add($"Item {i + 1}: frame must be GLOBAL (0), GLOBAL_RELATIVE_ALT (3) or GLOBAL_TERRAIN_ALT (10)."); }
      if (new[] { item.Latitude, item.Longitude, item.AltitudeMetres, item.P1, item.P2, item.P3, item.P4 }.Any(v => !double.IsFinite(v))
          || item.Latitude is < -90 or > 90 || item.Longitude is < -180 or > 180) { errors.Add($"Item {i + 1}: invalid coordinates or non-finite parameter."); }
      if (item.Command == (ushort)MAVLink.MAV_CMD.DO_JUMP && (item.P1 != Math.Truncate(item.P1) || item.P1 < 1 || item.P1 > items.Length
          || item.P2 != Math.Truncate(item.P2) || item.P2 < -1)) { errors.Add($"Item {i + 1}: DO_JUMP needs an existing 1-based target and integer repeats >= -1."); }
      if (item.Frame == 10 && !warnings.Contains("Terrain altitudes require terrain data and vehicle support.")) { warnings.Add("Terrain altitudes require terrain data and vehicle support."); }
    }
    if (items.Length == 0) { warnings.Add("This replaces the Mission draft with an empty list."); }
    warnings.Add("Structural checks only: no terrain clearance, geofence, energy, flight feasibility or aircraft compatibility approval. Nothing is uploaded.");
    return new(errors.Count == 0, errors.Take(50).ToArray(), warnings.ToArray());
  }
  internal static object Replace(FlightPlannerViewModel vm, string expected, McpDraftHome home, McpDraftItem[] items) {
    RequireRevision(vm, expected);
    var validation = Validate(home, items);
    if (!validation.Valid) { throw new ArgumentException(string.Join(" ", validation.Errors)); }
    vm.ReplaceAgentDraft(items.Select(item => new WpRow { Command = item.Command, Frame = item.Frame,
      Lat = item.Latitude, Lng = item.Longitude, Alt = item.AltitudeMetres, P1 = item.P1, P2 = item.P2, P3 = item.P3, P4 = item.P4 }).ToArray(),
      home.Latitude, home.Longitude, home.AltitudeMetres);
    return new { revision = Revision(vm), total = items.Length, uploaded = false, canUndo = vm.CanUndo, validation.Warnings };
  }
}
