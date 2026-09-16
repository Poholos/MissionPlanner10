using System.Text.Json;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Services;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpVehicleTests {
  [Fact]
  public void Health_uses_packet_ages_and_wire_units_without_current_state_or_other_targets() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var state = link.MAVlist[42, 1];
    link.MAVlist[42, 1] = state;
    var parser = new MAVLink.MavlinkParse();
    void Add(MAVLink.MAVLINK_MSG_ID type, object value, DateTime time) => state.addPacket(new MAVLink.MAVLinkMessage(
        parser.GenerateMAVLinkPacket20(type, value, false, 42, 1), time));
    Add(MAVLink.MAVLINK_MSG_ID.VIBRATION, new MAVLink.mavlink_vibration_t { vibration_x = 35, clipping_0 = 12 }, DateTime.UtcNow.AddSeconds(-20));
    Add(MAVLink.MAVLINK_MSG_ID.SYS_STATUS, new MAVLink.mavlink_sys_status_t {
      onboard_control_sensors_present = 7, onboard_control_sensors_enabled = 3, onboard_control_sensors_health = 1,
      voltage_battery = 12345, current_battery = -1, battery_remaining = -1, load = 123,
    }, DateTime.UtcNow);
    Add(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t {
      base_mode = (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED,
    }, DateTime.UtcNow);
    var connection = new MavLinkConnection(link, "test", true, null);
    var access = new McpVehicleAccess(() => [connection]);
    string id = JsonSerializer.SerializeToElement(access.ListVehicles()).EnumerateArray()
        .Single(v => v.GetProperty("systemId").GetByte() == 42).GetProperty("targetId").GetString()!;
    link.sysidcurrent = 43;
    var data = JsonSerializer.SerializeToElement(access.Health(id));
    Assert.True(data.GetProperty("heartbeat").GetProperty("values").GetProperty("armed").GetBoolean());
    Assert.False(state.cs.armed);
    Assert.False(data.GetProperty("vibration").GetProperty("fresh").GetBoolean());
    Assert.Equal(35, data.GetProperty("vibration").GetProperty("values").GetProperty("x").GetDouble());
    var system = data.GetProperty("system").GetProperty("values");
    Assert.Equal(2u, system.GetProperty("unhealthyEnabledSensors").GetUInt32());
    Assert.Equal(12.345, system.GetProperty("batteryVolts").GetDouble());
    Assert.Equal(JsonValueKind.Null, system.GetProperty("batteryAmps").ValueKind);
    Assert.Equal(JsonValueKind.Null, system.GetProperty("batteryRemainingPercent").ValueKind);
    Assert.False(data.GetProperty("gps").GetProperty("available").GetBoolean());
    connection.MarkClosed(); connection.MarkOpened();
    Assert.Throws<InvalidOperationException>(() => access.Health(id));
  }

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
  [Fact]
  public void Integer_proposal_rejects_precision_loss_with_c_cast_but_accepts_bytewise_encoding() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var state = link.MAVlist[42, 1];
    state.apname = MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA;
    state.param["DEVICE_CODE"] = new MAVLink.MAVLinkParam("DEVICE_CODE", 12, MAVLink.MAV_PARAM_TYPE.UINT32);
    var connection = new MavLinkConnection(link, "test", true, null);
    var target = new McpTarget("test", connection, state, connection.Generation);
    var change = new ParameterChange("DEVICE_CODE", 12, 60180513, "Test exact integer value");
    Assert.Throws<ArgumentException>(() => McpVehicleAccess.ValidateChange(target, change));
    state.cs.capabilities = (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_BYTEWISE;
    McpVehicleAccess.ValidateChange(target, change);
  }
}
