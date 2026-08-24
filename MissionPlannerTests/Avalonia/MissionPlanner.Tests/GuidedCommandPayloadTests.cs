using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class GuidedCommandPayloadTests {
  [Theory]
  [InlineData(true, (float)MAVLink.MAV_DO_REPOSITION_FLAGS.CHANGE_MODE)]
  [InlineData(false, 0f)]
  public void Reposition_payload_preserves_target_frame_coordinates_and_yaw(
      bool changeMode, float expectedFlags) {
    var target = new Locationwp {
      lat = 34.1234567,
      lng = 32.7654321,
      alt = 123.5f,
      frame = (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT,
    };

    MAVLink.mavlink_command_int_t command =
        MAVLinkInterface.BuildGuidedRepositionCommand(42, 190, target, changeMode);

    Assert.Equal((ushort)MAVLink.MAV_CMD.DO_REPOSITION, command.command);
    Assert.Equal((byte)42, command.target_system);
    Assert.Equal((byte)190, command.target_component);
    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT, command.frame);
    Assert.Equal(-1, command.param1);
    Assert.Equal(expectedFlags, command.param2);
    Assert.Equal(0, command.param3);
    Assert.True(float.IsNaN(command.param4));
    Assert.Equal(341234567, command.x);
    Assert.Equal(327654321, command.y);
    Assert.Equal(123.5f, command.z);
  }

  [Fact]
  public void Altitude_payload_uses_relative_home_frame_and_requested_metres() {
    MAVLink.mavlink_command_long_t command =
        MAVLinkInterface.BuildAltitudeChangeCommand(17, 3, 87.25f);

    Assert.Equal((ushort)MAVLink.MAV_CMD.DO_CHANGE_ALTITUDE, command.command);
    Assert.Equal((byte)17, command.target_system);
    Assert.Equal((byte)3, command.target_component);
    Assert.Equal(87.25f, command.param1);
    Assert.Equal((float)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, command.param2);
    Assert.Equal(0, command.param3);
    Assert.Equal(0, command.param4);
    Assert.Equal(0, command.param5);
    Assert.Equal(0, command.param6);
    Assert.Equal(0, command.param7);
  }
}
