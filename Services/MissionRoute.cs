using System;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.Services;

internal static class MissionRoute {
  private const ushort LegacyNavigationRoi = 80;

  internal static bool IsNavigation(ushort command) {
    var value = (MAVLink.MAV_CMD)command;
    return (value >= MAVLink.MAV_CMD.WAYPOINT && value < MAVLink.MAV_CMD.LAST
            && command != LegacyNavigationRoi)
           || value == MAVLink.MAV_CMD.DO_LAND_START;
  }

  // DO_LAND_START carries a location used by the autopilot to choose a landing sequence, but the
  // vehicle never flies to that location. Keep treating it as a positioned mission item so its
  // marker remains visible, while excluding it from routes, distances and corridor prefetches.
  internal static bool IsFlightPath(ushort command) =>
      IsNavigation(command) && command != (ushort)MAVLink.MAV_CMD.DO_LAND_START;

  internal static double LoiterTurnsRadius(
      double commandRadius, double configuredRadius, Firmwares firmware) {
    if (!double.IsFinite(commandRadius) || !double.IsFinite(configuredRadius)) {
      return 0;
    }
    if (commandRadius != 0) {
      return Math.Abs(commandRadius);
    }
    return firmware == Firmwares.ArduCopter2 ? 0 : Math.Abs(configuredRadius);
  }

  internal static double AdditionalLoiterDistance(
      ushort command, double turns, double commandRadius, double configuredRadius,
      Firmwares firmware) {
    if (command != (ushort)MAVLink.MAV_CMD.LOITER_TURNS
        || !double.IsFinite(turns) || turns <= 0) {
      return 0;
    }
    return 2 * Math.PI * LoiterTurnsRadius(commandRadius, configuredRadius, firmware) * turns;
  }
}
