using System.Text.Json;
using MissionPlanner.Comms;
using MissionPlanner.Services;
using MissionPlanner.Services.Mcp;
using MissionPlanner.Utilities;
using ModelContextProtocol;

namespace MissionPlanner.Tests;

public sealed class McpFlightContextTests {
  private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, MissionPlannerMcpTools.JsonOptions);
  private static JsonElement Parse(string value) => JsonDocument.Parse(value).RootElement;

  [Fact]
  public void Status_history_is_bounded_source_specific_non_consuming_and_cursor_paged() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var state = link.MAVlist[42, 1]; link.MAVlist[42, 1] = state;
    var parser = new MAVLink.MavlinkParse();
    void Add(int i) => state.addPacket(new MAVLink.MAVLinkMessage(parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.STATUSTEXT,
        new MAVLink.mavlink_statustext_t { text = McpTelemetryLogTests.Text("PreArm " + i), severity = (byte)(i % 8), id = 9, chunk_seq = 1 },
        false, 42, 1), DateTime.UtcNow));
    for (int i = 0; i < 300; i++) { Add(i); }
    var connection = new MavLinkConnection(link, "test", true, null);
    var access = new McpVehicleAccess(() => [connection]);
    string id = access.ListTargets().Single(t => t.State.sysid == 42).Id;
    link.sysidcurrent = 43;
    var first = Json(access.Messages(id, 0, 3, 7));
    Assert.True(first.GetProperty("historyTruncated").GetBoolean());
    Assert.False(first.GetProperty("complete").GetBoolean());
    Assert.Equal(45, first.GetProperty("oldestSequence").GetInt64());
    var messages = first.GetProperty("messages");
    Assert.Equal("PreArm 44", messages[0].GetProperty("text").GetString());
    Assert.Equal(9, messages[0].GetProperty("messageId").GetInt32());
    Assert.Equal(1, messages[0].GetProperty("chunkSequence").GetInt32());
    long cursor = first.GetProperty("nextSequence").GetInt64();
    Assert.Equal(47, cursor);
    var next = Json(access.Messages(id, cursor, 200, 2));
    Assert.All(next.GetProperty("messages").EnumerateArray(), p => Assert.InRange(p.GetProperty("severity").GetInt32(), 0, 2));
    Assert.Equal(300, next.GetProperty("nextSequence").GetInt64());
    Assert.True(Json(access.Messages(id, 1, 10, 7)).GetProperty("missedSinceCursor").GetBoolean());
    Assert.True(Json(access.Messages(id, long.MaxValue, 10, 7)).GetProperty("cursorAhead").GetBoolean());
    Assert.NotNull(state.getPacket((uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT));
    var inventory = Json(access.PacketInventory(id, 0, 200));
    Assert.Equal("STATUSTEXT", Assert.Single(inventory.GetProperty("packets").EnumerateArray()).GetProperty("name").GetString());
    Assert.DoesNotContain("PreArm", inventory.ToString());
    connection.MarkClosed(); connection.MarkOpened();
    Assert.Throws<InvalidOperationException>(() => access.Messages(id, 0, 10, 7));
  }

  [Fact]
  public void Packet_snapshots_are_safe_during_concurrent_receipt() {
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var state = link.MAVlist[1, 1];
    var packet = new MAVLink.MAVLinkMessage(new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
        MAVLink.MAVLINK_MSG_ID.STATUSTEXT, new MAVLink.mavlink_statustext_t { text = McpTelemetryLogTests.Text("x") }, false, 1, 1));
    Parallel.For(0, 2000, _ => { state.addPacket(packet); _ = state.GetPacketSnapshot().Length; _ = state.GetStatusTextSnapshot().Length; });
    var rows = state.GetStatusTextSnapshot();
    Assert.Equal(256, rows.Length); Assert.Equal(2000, rows[^1].Sequence);
    Assert.Equal(Enumerable.Range(1745, 256).Select(i => (long)i), rows.Select(p => p.Sequence));
  }

  [Fact]
  public void Telemetry_events_keep_baselines_sources_chunks_and_lossless_pages() {
    using var file = new McpTelemetryLogTests.TlogFixture();
    var heartbeat = new MAVLink.mavlink_heartbeat_t { custom_mode = 3 };
    file.Add(0, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, heartbeat, 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, heartbeat, 1);
    file.Add(2, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, heartbeat, 2);
    heartbeat.base_mode = (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED;
    file.Add(3, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, heartbeat, 1);
    file.Add(4, MAVLink.MAVLINK_MSG_ID.STATUSTEXT, new MAVLink.mavlink_statustext_t {
      text = McpTelemetryLogTests.Text("Warning\0ignored"), severity = 4, id = 2, chunk_seq = 1 }, 1);
    file.Add(5, MAVLink.MAVLINK_MSG_ID.COMMAND_ACK, new MAVLink.mavlink_command_ack_t {
      command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM, result = (byte)MAVLink.MAV_RESULT.DENIED }, 1);
    var events = McpLogContext.TelemetryEvents(file.Path, default).ToArray();
    Assert.Equal(new[] { 0, 2, 3, 4, 5 }, events.Select(e => e.Line));
    Assert.Equal("2:1", events[1].Instance);
    Assert.Equal("Warning", Json(events[3].Evidence).GetProperty("text").GetString());
    var first = Json(McpLogContext.Events(events, 0, 1, 1, 5, "1:1", default));
    Assert.Equal(3, first.GetProperty("events")[0].GetProperty("line").GetInt32());
    Assert.Equal(4, first.GetProperty("nextLine").GetInt32());
    var second = Json(McpLogContext.Events(McpLogContext.TelemetryEvents(file.Path, default), 4, 10, 1, 5, "1:1", default));
    Assert.Equal(2, second.GetProperty("events").GetArrayLength());
    Assert.Equal("DENIED", second.GetProperty("events")[1].GetProperty("evidence").GetProperty("resultName").GetString());
    Assert.True(second.GetProperty("complete").GetBoolean());
  }

  [Fact]
  public void Dataflash_events_preserve_codes_and_leave_untimed_text_unknown() {
    using var file = new LogFixture([
      "FMT, 131, 67, MSG, Z, Message", "FMT, 132, 16, MODE, QBB, TimeUS,Mode,ModeNum",
      "MSG, Firmware information", "MODE, 1000000, 3, 3", "MODE, 2000000, 5, 5",
    ]);
    using var log = new DFLogBuffer(file.Path);
    var events = McpLogContext.DataFlashEvents(log, default).ToArray();
    Assert.Null(events[0].TimeSeconds);
    Assert.Equal("3", Json(events[1].Evidence).GetProperty("ModeNum").GetString());
    var window = Json(McpLogContext.Events(events, 0, 200, 1.5, 2, null, default));
    Assert.Single(window.GetProperty("events").EnumerateArray());
  }

  [Fact]
  public void Trend_preserves_spikes_in_large_series_and_never_fills_gaps_or_mixes_sources() {
    IEnumerable<McpTrendSample> Samples() {
      for (int i = 0; i < 50000; i++) { yield return new(i, i / 1000.0, "0", i == 7 ? 999 : 1, "V", 1); }
      yield return new(50000, 50, "0", double.NaN, "V", 1);
      yield return new(50001, 100, "0", 2, "V", 1);
      yield return new(50002, 1, "1", 5, "V", 1);
    }
    var result = Json(McpLogContext.Trend(Samples(), 0, 100, 10, null, default));
    var series = result.GetProperty("series"); Assert.Equal(2, series.GetArrayLength());
    var buckets = series[0].GetProperty("buckets");
    Assert.Equal(7, buckets.GetArrayLength());
    Assert.Equal(999, buckets[0].GetProperty("max").GetDouble());
    Assert.Equal(0.007, buckets[0].GetProperty("maxSeconds").GetDouble(), 8);
    Assert.Equal(10000, buckets[0].GetProperty("samples").GetInt64());
    Assert.Equal(1.0998, buckets[0].GetProperty("mean").GetDouble(), 8);
    Assert.Equal(JsonValueKind.Null, buckets[5].GetProperty("mean").ValueKind);
    Assert.Equal(1, buckets[5].GetProperty("invalidSamples").GetInt64());
    Assert.Equal(9, buckets[6].GetProperty("index").GetInt32());
    Assert.Equal(50, series[0].GetProperty("maximumObservedGapSeconds").GetDouble());
    Assert.Single(series[1].GetProperty("buckets").EnumerateArray());
  }

  [Fact]
  public void Trend_rejects_clock_reversal_invalid_bounds_excess_sources_and_obeys_cancellation() {
    var samples = new[] { new McpTrendSample(0, 2, "0", 1, "", 1), new McpTrendSample(1, 1, "0", 2, "", 1) };
    Assert.Throws<ArgumentException>(() => McpLogContext.Trend(samples, 0, 3, 10, null, default));
    Assert.Throws<ArgumentException>(() => McpLogContext.Trend([], 0, 0, 10, null, default));
    Assert.Throws<ArgumentException>(() => McpLogContext.Trend([], 0, 1, 513, null, default));
    Assert.Throws<ArgumentException>(() => McpLogContext.Trend(Enumerable.Range(0, 17).Select(i => new McpTrendSample(i, 1, i.ToString(), 1, "", 1)), 0, 2, 1, null, default));
    using var stop = new CancellationTokenSource(); stop.Cancel();
    Assert.Throws<OperationCanceledException>(() => McpLogContext.Trend(samples, 0, 3, 10, null, stop.Token));
  }

  [Fact]
  public async Task Tlog_trends_keep_wire_units_and_sources_and_reject_unknown_fields() {
    using var file = new McpTelemetryLogTests.TlogFixture();
    file.Add(0, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 1 }, 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 9 }, 2);
    file.Add(2, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 3 }, 1);
    using var catalog = new McpLogCatalog(); var id = catalog.Attach(file.Path).Id;
    var tools = new MissionPlannerMcpTools(new McpVehicleAccess(() => []), catalog, _ => Task.FromResult<object>(new { }));
    var result = Parse(await tools.TimeSeries(id, "ATTITUDE", "roll", 0, 2, default, bins: 1));
    var series = result.GetProperty("result").GetProperty("series");
    Assert.Equal(2, series.GetArrayLength());
    Assert.Equal("[rad]", series[0].GetProperty("unit").GetString());
    Assert.Equal(2, series[0].GetProperty("buckets")[0].GetProperty("mean").GetDouble());
    Assert.Equal(9, series[1].GetProperty("buckets")[0].GetProperty("mean").GetDouble());
    Assert.Equal(JsonValueKind.Null, series[1].GetProperty("maximumObservedGapSeconds").ValueKind);
    await Assert.ThrowsAsync<McpException>(() => tools.TimeSeries(id, "ATTITUDE", "missing", 0, 2, default));
  }

  [Fact]
  public void Dataflash_trend_uses_native_instance_and_applies_native_multiplier_once() {
    using var file = new LogFixture([
      "FMT, 131, 16, BAT, QBf, TimeUS,Instance,Volt", "FMT, 132, 44, FMTU, QBNN, TimeUS,FmtType,UnitIds,MultIds",
      "FMTU, 0, 131, s#v, ---", "BAT, 1000000, 0, 1200", "BAT, 2000000, 1, 2400",
    ]);
    using var log = new DFLogBuffer(file.Path);
    // The MCP adapter consumes the parser's resolved metadata, not raw FMTU codes.
    log.UnitMultiList.RemoveAll(p => p.Item1 == "BAT" && p.Item2 == "Volt");
    log.UnitMultiList.Add(new("BAT", "Volt", "V", 0.01));
    var result = Json(McpLogContext.Trend(McpLogContext.DataFlashSamples(log, "BAT", "Volt", default), 0, 3, 1, null, default));
    var series = result.GetProperty("series");
    Assert.Equal(new[] { "0", "1" }, series.EnumerateArray().Select(s => s.GetProperty("instance").GetString()));
    Assert.Equal(12, series[0].GetProperty("buckets")[0].GetProperty("mean").GetDouble());
    Assert.Equal(24, series[1].GetProperty("buckets")[0].GetProperty("mean").GetDouble());
  }

  [Fact]
  public async Task Tools_compare_recorded_snapshots_and_live_cache_without_modifying_either() {
    using var file = new LogFixture(["PARM, 1000000, GAIN, 1, 0", "PARM, 2000000, GAIN, 2, 0",
      "PARM, 3000000, AUTH_KEY, 12345, 0", "PARM, 4000000, NEW, 7, 0"]);
    using var catalog = new McpLogCatalog(); string logId = catalog.Attach(file.Path).Id;
    using var link = new MAVLinkInterface { BaseStream = new CommsInjection() };
    var state = link.MAVlist[1, 1]; link.MAVlist[1, 1] = state;
    state.param["GAIN"] = new MAVLink.MAVLinkParam("GAIN", 9, MAVLink.MAV_PARAM_TYPE.REAL32);
    var connection = new MavLinkConnection(link, "test", true, null);
    var access = new McpVehicleAccess(() => [connection]);
    var tools = new MissionPlannerMcpTools(access, catalog, _ => Task.FromResult<object>(new { }));
    var compared = Parse(await tools.CompareLogParameters(logId, 1, logId, 4, default));
    var diff = compared.GetProperty("comparison").GetProperty("differences");
    Assert.Equal(2, diff.GetArrayLength());
    Assert.Equal(1, diff[0].GetProperty("delta").GetDouble());
    Assert.Equal("onlyAfter", diff[1].GetProperty("status").GetString());
    Assert.DoesNotContain("AUTH_KEY", compared.ToString());
    var current = Parse(await tools.CompareVehicleParameters(access.ListTargets().Single(t => t.State.sysid == 1).Id, logId, 1, default));
    Assert.Equal(8, current.GetProperty("comparison").GetProperty("differences")[0].GetProperty("delta").GetDouble());
    Assert.False(current.GetProperty("complete").GetBoolean());
    Assert.Equal(9, state.param["GAIN"].Value);
    Assert.Empty(access.Proposals());
  }

  [Fact]
  public async Task Tlog_comparison_requires_source_and_keeps_unknown_encoding_unknown() {
    using var file = new McpTelemetryLogTests.TlogFixture();
    file.Add(0, MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t {
      param_id = McpTelemetryLogTests.Name("GAIN"), param_value = 5, param_type = (byte)MAVLink.MAV_PARAM_TYPE.INT32 }, 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t {
      param_id = McpTelemetryLogTests.Name("GAIN"), param_value = 99, param_type = (byte)MAVLink.MAV_PARAM_TYPE.REAL32 }, 2);
    file.Add(2, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t { autopilot = (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA }, 1);
    file.Add(3, MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t {
      param_id = McpTelemetryLogTests.Name("GAIN"), param_value = 6, param_type = (byte)MAVLink.MAV_PARAM_TYPE.INT32 }, 1);
    using var catalog = new McpLogCatalog(); var id = catalog.Attach(file.Path).Id;
    var tools = new MissionPlannerMcpTools(new McpVehicleAccess(() => []), catalog, _ => Task.FromResult<object>(new { }));
    await Assert.ThrowsAsync<McpException>(() => tools.CompareLogParameters(id, 0, id, 3, default));
    var result = Parse(await tools.CompareLogParameters(id, 0, id, 3, default, "1:1", "1:1"));
    var diff = Assert.Single(result.GetProperty("comparison").GetProperty("differences").EnumerateArray());
    Assert.Equal("unknown", diff.GetProperty("status").GetString());
    Assert.Equal(JsonValueKind.Null, diff.GetProperty("before").GetProperty("value").ValueKind);
    Assert.Equal(6, diff.GetProperty("after").GetProperty("value").GetDouble());
  }

  [Fact]
  public void Comparison_distinguishes_types_unknowns_missing_values_and_paginates() {
    var a = new Dictionary<string, McpParameterPoint> { ["A"] = new("A", 1, "INT32", 0, 1), ["B"] = new("B", null, null, 0, 2), ["C"] = new("C", 3, null, 0, 3) };
    var b = new Dictionary<string, McpParameterPoint> { ["A"] = new("A", 1, "REAL32", 1, 5), ["B"] = new("B", 2, null, 1, 6), ["D"] = new("D", 4, null, 1, 7) };
    var result = Json(McpParameterComparison.Compare(a, b, 1, 2, false));
    Assert.Equal(4, result.GetProperty("total").GetInt32());
    Assert.Equal(new[] { "unknown", "onlyBefore" }, result.GetProperty("differences").EnumerateArray().Select(d => d.GetProperty("status").GetString()));
    Assert.Equal(1, result.GetProperty("summary").GetProperty("typeChanged").GetInt32());
    Assert.False(result.GetProperty("complete").GetBoolean());
  }

  [Fact]
  public void Snapshot_rejects_multiple_boots_even_when_each_parameter_occurs_once() {
    using var file = new LogFixture(["PARM, 2000000, A, 1, 0", "PARM, 1000000, B, 2, 0"]);
    using var log = new DFLogBuffer(file.Path);
    Assert.Throws<ArgumentException>(() => McpParameterComparison.DataFlash(log, 3, "", default));
  }

  internal sealed class LogFixture : IDisposable {
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mp-context-{Guid.NewGuid():N}.log");
    internal LogFixture(string[] rows) => File.WriteAllLines(Path, [
      "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
      "FMT, 130, 40, PARM, QNff, TimeUS,Name,Value,Default", .. rows]);
    public void Dispose() => File.Delete(Path);
  }
}
