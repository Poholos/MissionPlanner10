using System.Xml.Linq;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public class ParameterMetadataOverlayTests {
  private static readonly string[] FlightModeParameters =
    Enumerable.Range(1, 6).Select(index => $"FLTMODE{index}").ToArray();

  private static readonly string[] DeadReckoningParameters = [
    "DR_NEXT_MODE",
    "DR_MODEL_TIME",
    "DR_DIST_MARGIN",
    "DR_OBS_ENABLE",
    "DR_OBS_VMAX",
    "DR_OBS_AMAX",
    "DR_OBS_UNC",
    "DR_OBS_CLIP",
    "DR_OBS_VIBE",
    "DR_EKF_BRIDGE",
    "DR_EKF_POS_MAX",
    "DR_EKF_VEL_MAX",
    "DR_EKF_TIME_MAX",
  ];

  private static readonly string[] NewDeadReckoningParameters =
    DeadReckoningParameters.Where(name => name != "DR_NEXT_MODE").ToArray();

  private static readonly string[] ModelCalibrationParameters = [
    "MCAL_ENABLE",
    "MCAL_ANG1",
    "MCAL_ANG2",
    "MCAL_ANG3",
    "MCAL_SETTLE",
    "MCAL_SAMPLE",
    "MCAL_RADIUS",
    "MCAL_APPLY",
    "MCAL_BCOEF_X",
    "MCAL_MCOEF",
    "MCAL_QUALITY",
    "MCAL_VMAX",
    "MCAL_WIND_N",
    "MCAL_WIND_E",
    "MCAL_THR",
    "MCAL_VOLT",
    "MCAL_RPM",
    "MCAL_ARSP_ERR",
    "MCAL_CURR",
    "MCAL_PWR",
  ];

  private static readonly string[] EkfParameters = [
    "EK3_OPTIONS",
    "EK3_ARSP_MODE",
    "EK3_GPS_Q_POS",
    "EK3_GPS_Q_VEL",
    "EK3_GPS_Q_TIME",
    "EK3_GPS_Q_OFS",
  ];

  private static readonly string[] NewEkfParameters =
    EkfParameters.Where(name => name != "EK3_OPTIONS").ToArray();

  [Fact]
  public void Overlay_is_packaged_and_pins_the_local_ardupilot_source() {
    XDocument overlay = LoadOverlay();
    XElement root = Assert.IsType<XElement>(overlay.Element("paramfile"));

    Assert.Equal("3b2f9ac14e9da48aff58aa206ae60d80527f6edd",
      root.Attribute("sourceCommit")?.Value);
    Assert.Equal("582a71193ad2ed2d47e6723a53380447b0038a02",
      root.Attribute("sourceBase")?.Value);

    string[] expected = FlightModeParameters
      .Concat(DeadReckoningParameters)
      .Append("ARSPD_USE")
      .Concat(ModelCalibrationParameters)
      .Select(name => $"ArduCopter:{name}")
      .Concat(EkfParameters)
      .Order(StringComparer.Ordinal)
      .ToArray();
    string[] actual = root.Descendants("param")
      .Select(param => Assert.IsType<XAttribute>(param.Attribute("name")).Value)
      .Order(StringComparer.Ordinal)
      .ToArray();

    Assert.Equal(expected, actual);
  }

  [Fact]
  public void New_fork_parameters_have_operator_facing_names_and_descriptions() {
    XDocument overlay = LoadOverlay();

    foreach (string name in NewDeadReckoningParameters.Concat(ModelCalibrationParameters)) {
      Assert.False(string.IsNullOrWhiteSpace(Read(
        overlay, name, "humanName", "ArduCopter")));
      Assert.False(string.IsNullOrWhiteSpace(Read(
        overlay, name, "documentation", "ArduCopter")));
    }
    foreach (string name in NewEkfParameters) {
      Assert.False(string.IsNullOrWhiteSpace(Read(
        overlay, name, "humanName", "ArduPlane")));
      Assert.False(string.IsNullOrWhiteSpace(Read(
        overlay, name, "documentation", "ArduPlane")));
    }
  }

  [Fact]
  public void Overlay_wins_over_downloaded_metadata_but_keeps_vehicle_scope() {
    XDocument overlay = LoadOverlay();
    XDocument downloaded = XDocument.Parse("""
      <paramfile>
        <libraries>
          <parameters name="upstream">
            <param name="EK3_OPTIONS" documentation="upstream EKF description">
              <field name="Bitmask">0:JammingExpected</field>
            </param>
            <param name="ARSPD_USE" documentation="upstream airspeed description" />
          </parameters>
        </libraries>
      </paramfile>
      """);

    string ekfDescription = ParameterMetaDataPdefReader.ResolveParameterMetaData(
      overlay, downloaded, "EK3_OPTIONS", "documentation", "ArduPlane");
    Assert.Contains("GPSQuarantine", ekfDescription, StringComparison.Ordinal);
    Assert.Contains("4:GPS quarantine", ParameterMetaDataPdefReader.ResolveParameterMetaData(
      overlay, downloaded, "EK3_OPTIONS", ParameterMetaDataConstants.Bitmask, "ArduPlane"),
      StringComparison.Ordinal);

    Assert.Contains("forward-facing pitot", ParameterMetaDataPdefReader.ResolveParameterMetaData(
      overlay, downloaded, "ARSPD_USE", "documentation", "ArduCopter"),
      StringComparison.Ordinal);
    Assert.Equal("upstream airspeed description",
      ParameterMetaDataPdefReader.ResolveParameterMetaData(
        overlay, downloaded, "ARSPD_USE", "documentation", "ArduPlane"));

    Assert.Empty(Read(overlay, "MCAL_ENABLE", "documentation", "ArduPlane"));
    Assert.NotEmpty(Read(overlay, "EK3_ARSP_MODE", "documentation", "ArduPlane"));
  }

  [Fact]
  public void Overlay_exposes_ranges_units_read_only_flags_and_custom_mode_values() {
    XDocument overlay = LoadOverlay();

    foreach (string name in FlightModeParameters) {
      string values = Read(overlay, name, ParameterMetaDataConstants.Values, "ArduCopter");
      Assert.Contains("31:ModelCal", values, StringComparison.Ordinal);
      Assert.Contains("28:Turtle", values, StringComparison.Ordinal);
    }

    Assert.Equal("2 50", Read(
      overlay, "DR_OBS_VMAX", ParameterMetaDataConstants.Range, "ArduCopter"));
    Assert.Equal("m/s", Read(
      overlay, "DR_OBS_VMAX", ParameterMetaDataConstants.Units, "ArduCopter"));
    Assert.Equal("True", Read(
      overlay, "MCAL_BCOEF_X", ParameterMetaDataConstants.ReadOnly, "ArduCopter"));
    Assert.Equal("0:Default,1:BodyXWind,2:BodyXNav", Read(
      overlay, "EK3_ARSP_MODE", ParameterMetaDataConstants.Values, "ArduSub"));
    Assert.Equal("True", Read(
      overlay, "EK3_ARSP_MODE", ParameterMetaDataConstants.RebootRequired, "Rover"));
  }

  private static XDocument LoadOverlay() {
    string path = Path.Combine(
      AppContext.BaseDirectory,
      ParameterMetaDataRepositoryAPMpdef.LocalParameterMetaDataFileName);
    Assert.True(File.Exists(path), $"Packaged parameter overlay is missing: {path}");
    return XDocument.Load(path);
  }

  private static string Read(
      XDocument overlay, string nodeKey, string metaKey, string vehicleType) =>
    ParameterMetaDataPdefReader.ResolveParameterMetaData(
      overlay, new XDocument(), nodeKey, metaKey, vehicleType);
}
