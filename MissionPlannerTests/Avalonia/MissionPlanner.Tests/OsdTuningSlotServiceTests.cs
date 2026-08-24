using System;
using System.Linq;
using MissionPlanner.Services;
using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public sealed class OsdTuningSlotServiceTests {
  [Fact]
  public void ParameterIdRoundTripsFullSixteenByteName() {
    const string name = "OSD_PARAM_123456";

    byte[] encoded = OsdTuningSlotService.EncodeParameterId(name);

    Assert.Equal(OsdTuningSlotService.ParameterIdLength, encoded.Length);
    Assert.DoesNotContain((byte)0, encoded);
    Assert.Equal(name, OsdTuningSlotService.DecodeParameterId(encoded));
  }

  [Fact]
  public void ParameterIdPadsShortNameWithNulls() {
    byte[] encoded = OsdTuningSlotService.EncodeParameterId("RC6_OPTION");

    Assert.Equal("RC6_OPTION", OsdTuningSlotService.DecodeParameterId(encoded));
    Assert.All(encoded.Skip("RC6_OPTION".Length), value => Assert.Equal((byte)0, value));
  }

  [Theory]
  [InlineData("")]
  [InlineData("PARAMETER_NAME_TOO_LONG")]
  [InlineData("OSD_ПАРАМЕТР")]
  public void ParameterIdRejectsInvalidNames(string name) {
    Assert.Throws<ArgumentException>(() => OsdTuningSlotService.EncodeParameterId(name));
  }

  [Theory]
  [InlineData(4, 1)]
  [InlineData(7, 1)]
  [InlineData(5, 0)]
  [InlineData(5, 10)]
  public void SlotValidationRejectsOutOfRange(byte screen, byte index) {
    Assert.Throws<ArgumentOutOfRangeException>(
        () => OsdTuningSlotService.ValidateSlot(screen, index));
  }

  [Fact]
  public void EditorRowBuildsManualSlotUsingInvariantNumbers() {
    var original = new OsdTuningSlot(
        5, 1, "RC6_OPTION", MAVLink.OSD_PARAM_CONFIG_TYPE.OSD_PARAM_NONE, 0, 10, 1);
    var row = new OsdTuningSlotRow(original) {
      ParameterName = "SERIAL1_PROTOCOL",
      MinimumText = "-2.5",
      MaximumText = "25.5",
      IncrementText = "0.5",
    };

    Assert.True(row.TryBuild(out OsdTuningSlot? slot, out string error), error);
    Assert.NotNull(slot);
    Assert.Equal("SERIAL1_PROTOCOL", slot!.ParameterName);
    Assert.Equal(-2.5f, slot.Minimum);
    Assert.Equal(25.5f, slot.Maximum);
    Assert.Equal(0.5f, slot.Increment);
  }

  [Theory]
  [InlineData("8", "0", "1", "1")]
  [InlineData("0", "2", "1", "1")]
  [InlineData("0", "0", "1", "-1")]
  [InlineData("0", "NaN", "1", "1")]
  public void EditorRowRejectsInvalidValues(
      string type, string minimum, string maximum, string increment) {
    var original = new OsdTuningSlot(
        6, 9, "RC7_OPTION", MAVLink.OSD_PARAM_CONFIG_TYPE.OSD_PARAM_NONE, 0, 10, 1);
    var row = new OsdTuningSlotRow(original) {
      TypeText = type,
      MinimumText = minimum,
      MaximumText = maximum,
      IncrementText = increment,
    };

    Assert.False(row.TryBuild(out _, out string error));
    Assert.NotEmpty(error);
  }
}
