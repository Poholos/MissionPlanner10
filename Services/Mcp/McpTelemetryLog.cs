using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace MissionPlanner.Services.Mcp;

internal sealed record TelemetryLogPacket(int Line, double TimeSeconds, MAVLink.MAVLinkMessage Packet) {
  internal string Instance => $"{Packet.sysid}:{Packet.compid}";
  internal string Type => Packet.msgtypename;
  internal string Key => $"{Type}[{Instance}]";
}
internal sealed record TelemetryParameter(string Name, string Instance, double? Value, string Type,
    string Encoding, int Line, double TimeSeconds);

/// <summary>Offline MAVLink telemetry records. Never feeds playback into a live connection.</summary>
internal static class McpTelemetryLog {
  internal static bool IsTlog(string path) => Path.GetExtension(path).Equals(".tlog", StringComparison.OrdinalIgnoreCase);
  internal const string TimeNote = "Tlog time is seconds from the first recorded receipt timestamp, not flight boot time. "
      + "Fields are decoded MAVLink wire values, without display scaling. Telemetry rates/gaps are not raw IMU sample rates. "
      + "Protocol sentinel values are retained; inspect field descriptions before interpretation. "
      + "CRC is validated; MAVLink signing authenticity is not verified offline. Truncated/corrupt/unknown-dialect records are rejected, never silently skipped.";

  internal static IEnumerable<TelemetryLogPacket> Read(string path, CancellationToken ct = default) {
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var parser = new MAVLink.MavlinkParse();
    byte[] timestamp = new byte[8];
    DateTime? start = null, previous = null;
    int line = 0;
    while (stream.Position < stream.Length) {
      ct.ThrowIfCancellationRequested();
      long offset = stream.Position;
      if (stream.Length - offset < 10) { throw new InvalidDataException($"Truncated tlog record at byte {offset}."); }
      stream.ReadExactly(timestamp);
      ulong micros = BinaryPrimitives.ReadUInt64BigEndian(timestamp);
      if (micros > (ulong)((DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / 10)) {
        throw new InvalidDataException($"Invalid tlog timestamp at byte {offset}.");
      }
      DateTime utc = DateTime.UnixEpoch.AddTicks((long)micros * 10);
      if (previous.HasValue && utc < previous.Value) {
        throw new InvalidDataException($"Tlog receipt clock moves backwards at byte {offset}; split the recording before time-window analysis.");
      }
      previous = utc;
      int magic = stream.ReadByte(), payload = stream.ReadByte();
      int header = magic switch { 0xfd => 10, 0xfe => 6, _ => throw new InvalidDataException($"Invalid MAVLink framing at byte {offset}.") };
      byte[] frame = new byte[header + payload + 2 + 13];
      frame[0] = (byte)magic; frame[1] = (byte)payload;
      if (stream.Length - stream.Position < header - 2) { throw new InvalidDataException($"Truncated tlog header at byte {offset}."); }
      stream.ReadExactly(frame.AsSpan(2, header - 2));
      if (magic == 0xfd && (frame[2] & ~1) != 0) { throw new InvalidDataException("Unsupported MAVLink incompatibility flags."); }
      int signature = magic == 0xfd && (frame[2] & 1) != 0 ? 13 : 0;
      int length = header + payload + 2 + signature;
      if (stream.Length - stream.Position < length - header) { throw new InvalidDataException($"Truncated tlog packet at byte {offset}."); }
      stream.ReadExactly(frame.AsSpan(header, length - header));
      using var packetStream = new MemoryStream(frame, 0, length, false);
      var packet = parser.ReadPacket(packetStream);
      if (packet == null || packet.data == null) { throw new InvalidDataException($"Invalid CRC or unsupported MAVLink dialect at byte {offset}."); }
      packet.rxtime = utc;
      start ??= utc;
      yield return new(line++, (utc - start.Value).TotalSeconds, packet);
    }
  }

  internal static FieldInfo[] Fields(MAVLink.MAVLinkMessage packet) => packet.data.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
  internal static object? Value(object? value) => value switch {
    float f when !float.IsFinite(f) => null,
    double d when !double.IsFinite(d) => null,
    Array array => array.Cast<object>().Select(Value).ToArray(),
    _ => value,
  };
  internal static Dictionary<string, object?> Values(MAVLink.MAVLinkMessage packet) => Fields(packet)
      .ToDictionary(f => f.Name, f => Value(f.GetValue(packet.data)));
  internal static string Text(object? value) => value switch {
    byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
    Array array => string.Join(";", array.Cast<object>().Select(Text)),
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
    _ => value?.ToString() ?? "",
  };

  internal static object Schema(string path, CancellationToken ct) {
    var messages = new Dictionary<string, (TelemetryLogPacket First, long Count, double Last)>();
    int packets = 0; DateTime? start = null, end = null;
    foreach (var row in Read(path, ct)) {
      packets++; start ??= row.Packet.rxtime; end = row.Packet.rxtime;
      if (messages.TryGetValue(row.Key, out var prior)) { messages[row.Key] = (prior.First, prior.Count + 1, row.TimeSeconds); }
      else {
        if (messages.Count >= 4096) { throw new InvalidDataException("Tlog has more than 4096 message/source combinations."); }
        messages[row.Key] = (row, 1, row.TimeSeconds);
      }
    }
    return new { format = "tlog", lines = packets, startUtc = start, endUtc = end,
      messages = messages.OrderBy(p => p.Key).Select(p => new {
        type = p.Value.First.Type, instance = p.Value.First.Instance, key = p.Key,
        systemId = p.Value.First.Packet.sysid, componentId = p.Value.First.Packet.compid,
        count = p.Value.Count, startSeconds = p.Value.First.TimeSeconds, endSeconds = p.Value.Last,
        fields = Fields(p.Value.First.Packet).Select(f => new { name = f.Name, type = f.FieldType.Name,
          unit = f.GetCustomAttribute<MAVLink.Units>()?.Unit,
          description = f.GetCustomAttribute<MAVLink.Description>()?.Text }).ToArray(),
      }).ToArray(), note = TimeNote };
  }

  internal static IEnumerable<TelemetryLogPacket> Select(string path, string types, double start, double end, string? instance, CancellationToken ct) {
    McpLogCatalog.ValidateWindow(start, end);
    string[] names = types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
    if (names.Length is < 1 or > 32 || names.Any(n => n.Length > 100)) { throw new ArgumentException("Select 1..32 MAVLink message names or keys from log_schema."); }
    if (instance != null && (!instance.Contains(':') || instance.Length > 7)) { throw new ArgumentException("Tlog instance is systemId:componentId from log_schema."); }
    foreach (var row in Read(path, ct)) {
      if (row.TimeSeconds < start || row.TimeSeconds > end || (instance != null && row.Instance != instance)) { continue; }
      if (names.Contains(row.Type, StringComparer.Ordinal) || names.Contains(row.Key, StringComparer.Ordinal)) { yield return row; }
    }
  }

  internal static object Rows(string path, string types, int startLine, int count, double start, double end, string? instance, CancellationToken ct) {
    if (startLine < 0 || count is < 1 or > 500) { throw new ArgumentException("startLine >= 0; count 1..500."); }
    var rows = new List<object>(); int next = startLine; bool complete = true;
    foreach (var row in Select(path, types, start, end, instance, ct)) {
      if (row.Line < startLine) { continue; }
      if (rows.Count == count) { next = row.Line; complete = false; break; }
      rows.Add(new { line = row.Line, timeSeconds = row.TimeSeconds, receivedUtc = row.Packet.rxtime,
        type = row.Type, instance = row.Instance, systemId = row.Packet.sysid, componentId = row.Packet.compid,
        fields = Values(row.Packet) });
      next = row.Line + 1;
    }
    return new { rows, nextLine = next, complete, note = TimeNote };
  }

  internal static List<(double time, double value)> Series(string path, string type, string field) {
    var result = new List<(double, double)>(); var sources = new HashSet<string>();
    foreach (var row in Select(path, type, 0, 1e12, null, default)) {
      var info = Fields(row.Packet).FirstOrDefault(f => f.Name == field);
      object? value = info?.GetValue(row.Packet.data);
      if (value is null or Array || !double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) { continue; }
      sources.Add(row.Instance);
      if (sources.Count > 1) { throw new ArgumentException("Select the message key including [systemId:componentId] to keep vehicles separate."); }
      if (result.Count >= 2_000_000) { throw new ArgumentException("Curve exceeds two million points; use MCP time windows for this log."); }
      result.Add((row.TimeSeconds, number));
    }
    return result;
  }

  internal static IEnumerable<TelemetryParameter> ParameterHistory(string path, CancellationToken ct = default) {
    var autopilots = new Dictionary<string, MAVLink.MAV_AUTOPILOT>();
    var capabilities = new Dictionary<string, uint>();
    foreach (var row in Read(path, ct)) {
      if (row.Packet.data is MAVLink.mavlink_heartbeat_t heartbeat) {
        autopilots[row.Instance] = (MAVLink.MAV_AUTOPILOT)heartbeat.autopilot;
      } else if (row.Packet.data is MAVLink.mavlink_autopilot_version_t version) {
        capabilities[row.Instance] = (uint)version.capabilities;
      } else if (row.Packet.data is MAVLink.mavlink_param_value_t parameter) {
        string name = Text(parameter.param_id);
        if (name.Length == 0 || McpVehicleAccess.Sensitive(name)) { continue; }
        var type = (MAVLink.MAV_PARAM_TYPE)parameter.param_type;
        uint caps = capabilities.GetValueOrDefault(row.Instance);
        bool known = type == MAVLink.MAV_PARAM_TYPE.REAL32 || autopilots.ContainsKey(row.Instance)
            || (caps & ((uint)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_BYTEWISE | (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.PARAM_ENCODE_C_CAST)) != 0;
        double? value = null; string encoding = "unknown";
        if (known && type is >= MAVLink.MAV_PARAM_TYPE.UINT8 and <= MAVLink.MAV_PARAM_TYPE.REAL32
            && type is not (MAVLink.MAV_PARAM_TYPE.UINT64 or MAVLink.MAV_PARAM_TYPE.INT64)) {
          bool bytewise = type != MAVLink.MAV_PARAM_TYPE.REAL32
              && MAVLinkInterface.UsesBytewiseParameterEncoding(caps, autopilots.GetValueOrDefault(row.Instance));
          double decoded = new MAVLink.MAVLinkParam(name, BitConverter.GetBytes(parameter.param_value),
              bytewise ? type : MAVLink.MAV_PARAM_TYPE.REAL32, type).Value;
          value = double.IsFinite(decoded) ? decoded : null;
          encoding = bytewise ? "bytewise" : "c-cast/float";
        }
        yield return new(name, row.Instance, value, type.ToString(), encoding, row.Line, row.TimeSeconds);
      }
    }
  }

  internal static object Parameters(string path, double atSeconds, string filter, int offset, int count, CancellationToken ct) {
    McpLogCatalog.ValidateWindow(0, atSeconds);
    if (offset < 0 || count is < 1 or > 200 || filter.Length > 64) { throw new ArgumentException("offset >= 0, count 1..200, filter <= 64 characters."); }
    var values = new Dictionary<string, TelemetryParameter>();
    foreach (var parameter in ParameterHistory(path, ct)) {
      if (parameter.TimeSeconds < 0 || parameter.TimeSeconds > atSeconds || !parameter.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) { continue; }
      string key = parameter.Instance + ":" + parameter.Name;
      if (values.TryGetValue(key, out var old) && parameter.TimeSeconds < old.TimeSeconds) { throw new ArgumentException("Tlog clock moves backwards; split the recording before historical analysis."); }
      if (!values.ContainsKey(key) && values.Count >= 16384) { throw new ArgumentException("Too many parameters; narrow filter."); }
      values[key] = parameter;
    }
    return new { atSeconds, total = values.Count, parameters = values.OrderBy(p => p.Key, StringComparer.Ordinal).Skip(offset).Take(count).Select(p => p.Value).ToArray(),
      note = TimeNote + " PARAM_VALUE decoding uses only preceding heartbeat/capability evidence per source. Unknown encoding yields null; future or live values are never substituted." };
  }

  internal static object Vibration(string path, double start, double end, CancellationToken ct) =>
      McpFlightAnalysis.SummarizeVibration(Select(path, "VIBRATION", start, end, null, ct).Select(row => {
        var p = row.Packet.ToStructure<MAVLink.mavlink_vibration_t>();
        return new McpFlightAnalysis.VibrationRecord(row.TimeSeconds, row.Instance, new Dictionary<string, double> {
          ["VibeX"] = p.vibration_x, ["VibeY"] = p.vibration_y, ["VibeZ"] = p.vibration_z,
          ["Clip0"] = p.clipping_0, ["Clip1"] = p.clipping_1, ["Clip2"] = p.clipping_2,
        });
      }), start, end, ct, "TLOG elapsed receipt seconds. Instances identify MAVLink system:component, not IMU; VIBRATION reports the transmitted primary envelope. " + TimeNote);
}
