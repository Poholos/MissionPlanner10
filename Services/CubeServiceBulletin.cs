using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace MissionPlanner.Services;

internal sealed record CubeServiceBulletinSnapshot(
    string BoardType,
    string SerialNumber,
    string Accelerometer1,
    string Accelerometer2,
    string Accelerometer3,
    string Gyroscope1,
    string Gyroscope2,
    string Gyroscope3,
    string Barometer1,
    string Barometer2);

/// <summary>
/// Preserves Mission Planner's CubeBlack service-bulletin checks without retaining the WinForms
/// dialog. No report is submitted by the application: after explicit consent the user's browser
/// opens the vendor page with the same diagnostic fields as the native implementation.
/// </summary>
internal static class CubeServiceBulletin {
  private const string ReportEndpoint = "https://discuss.cubepilot.org:444/CubeSB";

  internal static bool IsAffectedCubeBlack(string serialNumber) =>
      serialNumber.Contains("CubeBlack", StringComparison.OrdinalIgnoreCase)
      && !serialNumber.Contains("CubeBlack+", StringComparison.OrdinalIgnoreCase);

  internal static bool RequiresParameterScan(
      string serialNumber, Func<string, double?> parameter) =>
      IsAffectedCubeBlack(serialNumber)
      && parameter("INS_ACC3_ID") is 0
      && parameter("INS_GYR3_ID") is 0
      && parameter("INS_ENABLE_MASK") is >= 7;

  internal static CubeServiceBulletinSnapshot Capture(MAVState mav) => new(
      ParameterText(mav, "BRD_TYPE"),
      mav.SerialString,
      ParameterText(mav, "INS_ACC_ID"),
      ParameterText(mav, "INS_ACC2_ID"),
      ParameterText(mav, "INS_ACC3_ID"),
      ParameterText(mav, "INS_GYR_ID"),
      ParameterText(mav, "INS_GYR2_ID"),
      ParameterText(mav, "INS_GYR3_ID"),
      mav.cs.press_abs.ToString("R", CultureInfo.InvariantCulture),
      mav.cs.press_abs2.ToString("R", CultureInfo.InvariantCulture));

  internal static string BuildReportUrl(
      CubeServiceBulletinSnapshot report, string name, string email) {
    (string Name, string Value)[] fields = [
      ("BRD_TYPE", report.BoardType),
      ("SerialNo", report.SerialNumber),
      ("INS_ACC_ID", report.Accelerometer1),
      ("INS_ACC2_ID", report.Accelerometer2),
      ("INS_ACC3_ID", report.Accelerometer3),
      ("INS_GYR_ID", report.Gyroscope1),
      ("INS_GYR2_ID", report.Gyroscope2),
      ("INS_GYR3_ID", report.Gyroscope3),
      ("Baro1", report.Barometer1),
      ("Baro2", report.Barometer2),
      ("Name", name),
      ("Email", email),
    ];
    return ReportEndpoint + "?" + string.Join("&", fields.Select(field =>
        Uri.EscapeDataString(field.Name) + "=" + Uri.EscapeDataString(field.Value ?? "")));
  }

  internal static async Task ShowAsync(
      CubeServiceBulletinSnapshot report, string detectedVia) {
    bool proceed = await Dialogs.ConfirmDangerous(
        "Critical Cube service bulletin",
        $"This CubeBlack may be affected by a critical service bulletin (detected via "
        + $"{detectedVia}). Mission Planner can open CubePilot's report page in your browser. "
        + "The URL contains the board serial number, board/sensor identifiers and barometer "
        + "readings shown by this vehicle. Name and email are optional. Nothing is opened or "
        + "sent unless you continue.",
        "CONTINUE");
    if (!proceed) {
      return;
    }

    string? name = await Dialogs.InputBox(
        "Critical Cube service bulletin", "Name (optional; Cancel stops the report)");
    if (name == null) {
      return;
    }
    string? email = await Dialogs.InputBox(
        "Critical Cube service bulletin", "Email (optional; Cancel stops the report)");
    if (email == null) {
      return;
    }

    try {
      Dialogs.OpenUrl(BuildReportUrl(report, name, email));
    } catch (Exception ex) {
      await Dialogs.Alert("Critical Cube service bulletin",
          "Could not open the CubePilot report page: " + ex.Message);
    }
  }

  internal static bool ProbeSpi(MAVLinkInterface link, MAVState mav) {
    if (!IsAffectedCubeBlack(mav.SerialString) || link.BaseStream?.IsOpen != true) {
      return false;
    }
    try {
      link.device_op(mav.sysid, mav.compid, out byte[] gyro,
          MAVLink.DEVICE_OP_BUSTYPE.SPI, "lsm9ds0_ext_g", 0, 0, 0x8f, 1);
      if (link.BaseStream?.IsOpen != true) {
        return false;
      }
      link.device_op(mav.sysid, mav.compid, out byte[] accelerometer,
          MAVLink.DEVICE_OP_BUSTYPE.SPI, "lsm9ds0_ext_am", 0, 0, 0x8f, 1);
      return HasUnexpectedSpiIdentity(gyro, accelerometer);
    } catch {
      return false;
    }
  }

  internal static bool HasUnexpectedSpiIdentity(
      ReadOnlySpan<byte> gyroscope, ReadOnlySpan<byte> accelerometer) =>
      gyroscope.Length != 0 && gyroscope[0] is not (0xd4 or 0xd7)
      || accelerometer.Length != 0 && accelerometer[0] != 0x49;

  private static string ParameterText(MAVState mav, string name) =>
      mav.param.ContainsKey(name)
          ? mav.param[name].Value.ToString("R", CultureInfo.InvariantCulture)
          : "";
}
