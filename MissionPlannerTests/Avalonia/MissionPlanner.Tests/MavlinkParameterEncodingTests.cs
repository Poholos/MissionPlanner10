namespace MissionPlanner.Tests;

public sealed class MavlinkParameterEncodingTests {
  [Fact]
  public void Explicit_bytewise_capability_overrides_firmware_identity() {
    uint bytewise = (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_BYTEWISE;

    Assert.True(MAVLinkInterface.UsesBytewiseParameterEncoding(
        bytewise, MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA));
    Assert.False(MAVLinkInterface.UsesBytewiseParameterEncoding(
        (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_C_CAST,
        MAVLink.MAV_AUTOPILOT.INVALID));
  }

  [Fact]
  public void Bytewise_uint32_round_trip_preserves_all_bits() {
    const uint expected = 60180513;
    var encoded = new MAVLink.MAVLinkParam(
        "DEVICE_CODE", expected, MAVLink.MAV_PARAM_TYPE.UINT32);
    var decoded = new MAVLink.MAVLinkParam(
        "DEVICE_CODE", BitConverter.GetBytes(encoded.float_value),
        MAVLink.MAV_PARAM_TYPE.UINT32, MAVLink.MAV_PARAM_TYPE.UINT32);

    Assert.Equal(expected, decoded.Value);
    Assert.NotEqual(expected, (double)(float)expected);
  }
}
