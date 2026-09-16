using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using MissionPlanner.Services;
using MissionPlanner.Services.Mcp;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

public sealed class McpTelemetryLogTests {
  [Fact]
  public void Signed_and_unsigned_packets_preserve_source_receipt_time_and_lossless_pagination() {
    using var file = new TlogFixture();
    file.Add(0, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 1 }, 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 100 }, 2, signed: true);
    file.Add(2, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 3 }, 1);
    var rows = McpTelemetryLog.Read(file.Path).ToArray();
    Assert.Equal(3, rows.Length);
    Assert.Equal(2, rows[2].TimeSeconds);
    Assert.Equal("ATTITUDE[2:1]", rows[1].Key);
    Assert.Equal(DateTimeKind.Utc, rows[1].Packet.rxtime.Kind);
    var schema = JsonSerializer.SerializeToElement(McpTelemetryLog.Schema(file.Path, default));
    Assert.Equal(2, schema.GetProperty("messages").GetArrayLength());
    Assert.Equal("[rad]", schema.GetProperty("messages")[0].GetProperty("fields").EnumerateArray()
        .Single(f => f.GetProperty("name").GetString() == "roll").GetProperty("unit").GetString());
    var page = JsonSerializer.SerializeToElement(McpTelemetryLog.Rows(file.Path, "ATTITUDE", 0, 1, 0, 10, "1:1", default));
    Assert.False(page.GetProperty("complete").GetBoolean());
    Assert.Equal(2, page.GetProperty("nextLine").GetInt32());
    var last = JsonSerializer.SerializeToElement(McpTelemetryLog.Rows(file.Path, "ATTITUDE", 2, 1, 0, 10, "1:1", default));
    Assert.Equal(3, last.GetProperty("rows")[0].GetProperty("fields").GetProperty("roll").GetDouble());
    Assert.True(last.GetProperty("complete").GetBoolean());
    Assert.Throws<ArgumentException>(() => McpTelemetryLog.Series(file.Path, "ATTITUDE", "roll"));
    Assert.Equal(new[] { 1.0, 3.0 }, McpTelemetryLog.Series(file.Path, "ATTITUDE[1:1]", "roll").Select(p => p.value));
  }

  [Fact]
  public void Historical_parameters_use_only_preceding_encoding_evidence_and_keep_sources_separate() {
    using var file = new TlogFixture();
    var param = new MAVLink.mavlink_param_value_t { param_id = Name("TEST_INT"), param_type = (byte)MAVLink.MAV_PARAM_TYPE.INT32, param_value = 17 };
    file.Add(0, MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, param, 1); // Unknown until heartbeat.
    file.Add(1, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t { autopilot = (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA }, 1);
    file.Add(2, MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, param, 1);
    file.Add(3, MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION, new MAVLink.mavlink_autopilot_version_t {
      capabilities = (ulong)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_BYTEWISE,
    }, 2);
    param.param_value = BitConverter.Int32BitsToSingle(60180513);
    file.Add(4, MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, param, 2);
    var history = McpTelemetryLog.ParameterHistory(file.Path).ToArray();
    Assert.Null(history[0].Value);
    Assert.Equal(17, history[1].Value);
    Assert.Equal(60180513, history[2].Value);
    Assert.Equal("2:1", history[2].Instance);
    var early = JsonSerializer.SerializeToElement(McpTelemetryLog.Parameters(file.Path, 0.5, "", 0, 100, default));
    Assert.Equal(JsonValueKind.Null, early.GetProperty("parameters")[0].GetProperty("Value").ValueKind);
    Assert.Equal(2, JsonSerializer.SerializeToElement(McpTelemetryLog.Parameters(file.Path, 4, "", 0, 100, default)).GetProperty("total").GetInt32());
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void Corruption_and_truncation_are_reported_instead_of_silent_data_loss(bool truncate) {
    using var file = new TlogFixture();
    file.Add(0, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 1 }, 1, signed: true);
    byte[] bytes = File.ReadAllBytes(file.Path);
    if (truncate) { Array.Resize(ref bytes, bytes.Length - 3); }
    else { bytes[18] ^= 0x55; }
    File.WriteAllBytes(file.Path, bytes);
    Assert.Throws<InvalidDataException>(() => McpTelemetryLog.Read(file.Path).ToArray());
  }

  [Fact]
  public void Reversed_receipt_clock_is_reported_instead_of_silently_filtering_negative_time() {
    using var file = new TlogFixture();
    file.Add(2, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(), 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(), 1);
    Assert.Contains("clock moves backwards", Assert.Throws<InvalidDataException>(() => McpTelemetryLog.Read(file.Path).ToArray()).Message);
  }

  [Fact]
  public async Task Catalog_accepts_tlog_and_telemetry_tools_analyze_it_without_using_DataFlash_parser() {
    using var file = new TlogFixture();
    file.Add(0, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 1 }, 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 3 }, 1);
    using var catalog = new McpLogCatalog();
    var info = catalog.Attach(file.Path); Assert.Equal("tlog", info.Format);
    string? opened = null;
    var tools = new MissionPlannerMcpTools(new McpVehicleAccess(() => []), catalog, _ => Task.FromResult<object>(new { }),
        (path, _) => { opened = path; return Task.CompletedTask; });
    using var stats = JsonDocument.Parse(await tools.Statistics(info.Id, "ATTITUDE", "roll", default));
    Assert.Equal(2, stats.RootElement.GetProperty("statistics").GetProperty("ATTITUDE[1:1].roll").GetProperty("mean").GetDouble());
    Assert.Contains("tlog", await tools.LogSchema(info.Id, default));
    Assert.Contains("tlog", await tools.Overview(info.Id, default));
    await tools.OpenAnalyzer(info.Id, default); Assert.Equal(file.Path, opened);
    await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.OpenAnalyzer("/etc/passwd", default));
    await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.Spectrum(info.Id, "ATTITUDE", "roll", 0, 1, default));
    using var stop = new CancellationTokenSource(); stop.Cancel();
    Assert.Throws<OperationCanceledException>(() => McpTelemetryLog.Read(file.Path, stop.Token).ToArray());
  }

  [AvaloniaFact]
  public async Task Browser_loads_telemetry_graphs_text_records_and_source_specific_parameters() {
    using var file = new TlogFixture();
    file.Add(0, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t { autopilot = (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA }, 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t { roll = 0.5f }, 1);
    file.Add(2, MAVLink.MAVLINK_MSG_ID.STATUSTEXT, new MAVLink.mavlink_statustext_t { text = Text("Flight, message") }, 1);
    file.Add(3, MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t { param_id = Name("TEST"), param_value = 42, param_type = (byte)MAVLink.MAV_PARAM_TYPE.REAL32 }, 1);
    var vm = new LogBrowseViewModel();
    await vm.LoadFileAsync(file.Path);
    Assert.Equal(file.Path, vm.CurrentPath);
    Assert.Contains("ATTITUDE[1:1]", vm.MessageTypes);
    Assert.Equal(0.5, vm.ReadCurve("ATTITUDE[1:1]", "roll")!.Value.ys.Single());
    Assert.Contains("Flight, message", vm.ReadRows("STATUSTEXT[1:1]").rows.Single());
    Assert.Contains("Flight, message", Assert.Single(vm.ReadMessages()).Message);
    vm.SelectedType = "PARAM_VALUE[1:1]";
    Assert.Equal("42", Assert.Single(vm.ReadParameters()).Value);
    File.WriteAllText(file.Path, "not a tlog");
    await vm.LoadFileAsync(file.Path);
    Assert.Null(vm.CurrentPath); Assert.Empty(vm.MessageTypes);
  }

  [Fact]
  public async Task Downloaded_bin_is_retained_catalogued_and_ready_for_analysis_and_cancel_cleans_temporary() {
    string root = Path.Combine(Path.GetTempPath(), "mp-download-test-" + Guid.NewGuid().ToString("N"));
    string temporary = McpServerTests.TemporaryLog();
    using var catalog = new McpLogCatalog();
    try {
      var info = await McpFlightLogWorkflow.DownloadAsync(_ => Task.FromResult(temporary), catalog, root, 3, default);
      Assert.False(File.Exists(temporary)); Assert.EndsWith(".bin", info.Name);
      var tools = new MissionPlannerMcpTools(new McpVehicleAccess(() => []), catalog, _ => Task.FromResult<object>(new { }));
      Assert.Contains("ATC_RAT_RLL_P", await tools.Records(info.Id, "PARM", default));
      temporary = McpServerTests.TemporaryLog();
      using var stop = new CancellationTokenSource(); stop.Cancel();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpFlightLogWorkflow.DownloadAsync(_ => Task.FromResult(temporary), catalog, root, 4, stop.Token));
      Assert.False(File.Exists(temporary)); Assert.Single(catalog.List());
    } finally { catalog.Dispose(); if (Directory.Exists(root)) { Directory.Delete(root, true); } File.Delete(temporary); }
  }

  internal static byte[] Name(string value) { var b = new byte[16]; Encoding.UTF8.GetBytes(value).CopyTo(b, 0); return b; }
  internal static byte[] Text(string value) { var b = new byte[50]; Encoding.UTF8.GetBytes(value).CopyTo(b, 0); return b; }

  internal sealed class TlogFixture : IDisposable {
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mp-telemetry-" + Guid.NewGuid().ToString("N") + ".tlog");
    private readonly MAVLink.MavlinkParse _parser = new();
    internal TlogFixture() => File.WriteAllBytes(Path, []);
    internal void Add(double seconds, MAVLink.MAVLINK_MSG_ID type, object data, byte system, byte component = 1, bool signed = false) {
      using var stream = File.Open(Path, FileMode.Append);
      byte[] time = new byte[8];
      BinaryPrimitives.WriteUInt64BigEndian(time, 1_700_000_000_000_000UL + (ulong)(seconds * 1e6));
      stream.Write(time);
      stream.Write(_parser.GenerateMAVLinkPacket20(type, data, signed, system, component));
    }
    public void Dispose() => File.Delete(Path);
  }
}
