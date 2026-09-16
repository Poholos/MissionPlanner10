using MissionPlanner;
using MissionPlanner.Comms;

namespace MissionPlanner.Tests;

public sealed class MavlinkOperationOwnershipTests {
  [Fact]
  public async Task Compare_before_write_reads_fresh_value_and_refuses_stale_proposal() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var stream = (CommsInjection)link.BaseStream;
    var state = link.MAVlist[42, 1];
    state.apname = MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA;
    state.param["TEST_P"] = new MAVLink.MAVLinkParam("TEST_P", 1, MAVLink.MAV_PARAM_TYPE.REAL32);
    int writes = 0;
    var parser = new MAVLink.MavlinkParse();
    stream.WriteCallback += (_, bytes) => {
      var packet = new MAVLink.MAVLinkMessage(bytes.ToArray());
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_SET) { writes++; }
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_READ) {
        stream.AppendBuffer(parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
            new MAVLink.mavlink_param_value_t { param_id = System.Text.Encoding.ASCII.GetBytes("TEST_P".PadRight(16, '\0')),
              param_value = 2, param_count = 1, param_index = 0, param_type = (byte)MAVLink.MAV_PARAM_TYPE.REAL32 }, false, 42, 1));
      }
    };
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await Assert.ThrowsAsync<InvalidOperationException>(() => link.SetParamIfUnchangedAsync(42, 1, "TEST_P", 1, 3, () => { }, cancel.Token));
    Assert.Equal(0, writes);
    Assert.Equal(2, state.param["TEST_P"].Value);
  }

  [Fact]
  public async Task Guarded_write_targets_the_pinned_vehicle_and_preserves_uint32_acknowledgement() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var stream = (CommsInjection)link.BaseStream;
    var state = link.MAVlist[42, 1];
    state.apname = MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA;
    state.cs.capabilities = (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_BYTEWISE;
    state.param["TEST_INT"] = new MAVLink.MAVLinkParam("TEST_INT", 12, MAVLink.MAV_PARAM_TYPE.UINT32);
    state.param_types["TEST_INT"] = MAVLink.MAV_PARAM_TYPE.UINT32;
    link.sysidcurrent = 55; link.compidcurrent = 2;
    var parser = new MAVLink.MavlinkParse();
    int writes = 0;
    stream.WriteCallback += (_, bytes) => {
      var packet = new MAVLink.MAVLinkMessage(bytes.ToArray());
      float wire;
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_READ) {
        var request = packet.ToStructure<MAVLink.mavlink_param_request_read_t>();
        Assert.Equal((byte)42, request.target_system);
        wire = new MAVLink.MAVLinkParam("TEST_INT", 12, MAVLink.MAV_PARAM_TYPE.UINT32).float_value;
      } else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_SET) {
        var request = packet.ToStructure<MAVLink.mavlink_param_set_t>();
        Assert.Equal((byte)42, request.target_system); Assert.Equal((byte)1, request.target_component);
        wire = request.param_value; writes++;
      } else { return; }
      stream.AppendBuffer(parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
          new MAVLink.mavlink_param_value_t { param_id = System.Text.Encoding.ASCII.GetBytes("TEST_INT".PadRight(16, '\0')),
            param_value = wire, param_count = 1, param_index = 0, param_type = (byte)MAVLink.MAV_PARAM_TYPE.UINT32 }, false, 42, 1));
    };
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(await link.SetParamIfUnchangedAsync(42, 1, "TEST_INT", 12, 60180513, () => { }, cancel.Token));
    Assert.Equal(1, writes);
    Assert.Equal(60180513, state.param["TEST_INT"].Value);
  }

  [Fact]
  public async Task Concurrent_log_download_is_rejected_and_cancellation_does_not_release_someone_elses_transport() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    using var cancel = new CancellationTokenSource();
    Task<string> first = link.GetLog(42, 1, 3, cancel.Token);
    await Assert.ThrowsAsync<InvalidOperationException>(() => link.GetLog(42, 1, 4));
    link.giveComport = true;
    cancel.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    Assert.True(link.giveComport);
    link.giveComport = false;
  }

  [Fact]
  public async Task Single_parameter_refresh_honors_bytewise_capability_for_ardupilot() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var stream = (CommsInjection)link.BaseStream;
    var state = link.MAVlist[42, 1];
    state.apname = MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA;
    state.cs.capabilities = (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_BYTEWISE;
    var parser = new MAVLink.MavlinkParse();
    stream.WriteCallback += (_, bytes) => {
      var packet = new MAVLink.MAVLinkMessage(bytes.ToArray());
      if (packet.msgid != (uint)MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_READ) { return; }
      var encoded = new MAVLink.MAVLinkParam("TEST_INT", 60180513, MAVLink.MAV_PARAM_TYPE.UINT32);
      stream.AppendBuffer(parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
          new MAVLink.mavlink_param_value_t { param_id = System.Text.Encoding.ASCII.GetBytes("TEST_INT".PadRight(16, '\0')),
            param_value = encoded.float_value, param_count = 1, param_index = 0, param_type = (byte)MAVLink.MAV_PARAM_TYPE.UINT32 }, false, 42, 1));
    };
    await link.GetParamAsync(42, 1, "TEST_INT").WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(60180513, state.param["TEST_INT"].Value);
  }
  [Fact]
  public async Task Guarded_write_rechecks_vehicle_state_before_retrying_unacknowledged_write() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var stream = (CommsInjection)link.BaseStream;
    var state = link.MAVlist[42, 1];
    state.apname = MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA;
    state.param["TEST_P"] = new MAVLink.MAVLinkParam("TEST_P", 1, MAVLink.MAV_PARAM_TYPE.REAL32);
    var parser = new MAVLink.MavlinkParse();
    int writes = 0;
    stream.WriteCallback += (_, bytes) => {
      var packet = new MAVLink.MAVLinkMessage(bytes.ToArray());
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_SET) { writes++; state.cs.armed = true; }
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_READ) {
        stream.AppendBuffer(parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
            new MAVLink.mavlink_param_value_t { param_id = System.Text.Encoding.ASCII.GetBytes("TEST_P".PadRight(16, '\0')),
              param_value = 1, param_count = 1, param_index = 0, param_type = (byte)MAVLink.MAV_PARAM_TYPE.REAL32 }, false, 42, 1));
      }
    };
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await Assert.ThrowsAsync<InvalidOperationException>(() => link.SetParamIfUnchangedAsync(42, 1, "TEST_P", 1, 3,
        () => { if (state.cs.armed) { throw new InvalidOperationException("Vehicle armed"); } }, cancel.Token));
    Assert.Equal(1, writes);
    Assert.False(link.giveComport);
  }
}
