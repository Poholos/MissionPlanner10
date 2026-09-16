using System.Text.Json;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Services;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpVehicleTests {
  [Fact]
  public void Target_survives_ui_selection_but_not_reconnect() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    link.MAVlist[42, 1].lastvalidpacket = DateTime.UtcNow;
    link.MAVlist[42, 1] = link.MAVlist[42, 1];
    var connection = new MavLinkConnection(link, "test", true, null);
    var access = new McpVehicleAccess(() => [connection]);
    var list = JsonSerializer.SerializeToElement(access.ListVehicles());
    string id = list.EnumerateArray().Single(v => v.GetProperty("systemId").GetByte() == 42).GetProperty("targetId").GetString()!;
    link.sysidcurrent = 43;
    Assert.Equal((byte)42, access.Resolve(id).State.sysid);
    connection.MarkClosed(); connection.MarkOpened();
    Assert.Throws<InvalidOperationException>(() => access.Resolve(id));
  }

  [Fact]
  public void Proposal_rejects_stale_values_sensitive_names_fractional_integers_and_wire_overflow() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var state = link.MAVlist[42, 1];
    state.lastvalidpacket = DateTime.UtcNow;
    state.param["TEST_INT"] = new MAVLink.MAVLinkParam("TEST_INT", 12, MAVLink.MAV_PARAM_TYPE.INT8);
    var connection = new MavLinkConnection(link, "test", true, null);
    var target = new McpTarget("test", connection, state, connection.Generation);
    Assert.Throws<InvalidOperationException>(() => McpVehicleAccess.ValidateChange(target, new("TEST_INT", 11, 14, "Test evidence")));
    Assert.Throws<ArgumentException>(() => McpVehicleAccess.ValidateChange(target, new("TEST_INT", 12, 12.5, "Test evidence")));
    Assert.Throws<ArgumentException>(() => McpVehicleAccess.ValidateChange(target, new("TEST_INT", 12, 500, "Test evidence")));
    Assert.Throws<ArgumentException>(() => McpVehicleAccess.ValidateChange(target, new("AUTH_KEY", 0, 1, "Test evidence")));
    McpVehicleAccess.ValidateChange(target, new("TEST_INT", 12, 14, "Test evidence"));
    Assert.Equal(12, state.param["TEST_INT"].Value);
  }

  [Fact]
  public void Disarmed_guard_requires_fresh_heartbeat_and_reads_armed_bit_even_before_ui_updates() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var state = link.MAVlist[42, 1];
    state.lastvalidpacket = DateTime.UtcNow;
    var connection = new MavLinkConnection(link, "test", true, null);
    var target = new McpTarget("test", connection, state, connection.Generation);
    Assert.Throws<InvalidOperationException>(() => McpVehicleAccess.RequireDisarmed(target));
    var parser = new MAVLink.MavlinkParse();
    void Heartbeat(byte mode, DateTime time) => state.addPacket(new MAVLink.MAVLinkMessage(
        parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
            new MAVLink.mavlink_heartbeat_t { base_mode = mode }, false, 42, 1), time));
    Heartbeat(0, DateTime.UtcNow.AddSeconds(-10));
    Assert.Throws<InvalidOperationException>(() => McpVehicleAccess.RequireDisarmed(target));
    Heartbeat((byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED, DateTime.UtcNow);
    Assert.False(state.cs.armed);
    Assert.Throws<InvalidOperationException>(() => McpVehicleAccess.RequireDisarmed(target));
    Heartbeat(0, DateTime.UtcNow);
    McpVehicleAccess.RequireDisarmed(target);
  }
}
