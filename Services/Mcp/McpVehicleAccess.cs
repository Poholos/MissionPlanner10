using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpTarget(string Id, MavLinkConnection Connection, MAVState State, long Generation);
internal sealed record ParameterChange(string Name, double Expected, double Proposed, string Reason);
internal sealed record ParameterProposal(string Id, string TargetId, string Rationale,
    ParameterChange[] Changes, DateTime CreatedUtc) {
  public string Status { get; internal set; } = "Pending review in Mission Planner";
  public override string ToString() => $"{Id[..8]} — {Changes.Length} parameter(s) — {Status}";
}

internal sealed class McpVehicleAccess {
  private readonly Func<IReadOnlyList<MavLinkConnection>> _connections;
  private readonly Dictionary<string, McpTarget> _targets = new();
  private readonly Dictionary<string, ParameterProposal> _proposals = new();
  private readonly object _sync = new();
  private readonly SemaphoreSlim _transfer = new(1, 1);

  internal McpVehicleAccess(Func<IReadOnlyList<MavLinkConnection>> connections) => _connections = connections;
  internal static bool Sensitive(string name) => new[] { "KEY", "PASS", "SECRET", "TOKEN" }
      .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

  internal object ListVehicles() {
    lock (_sync) {
      var live = new HashSet<string>();
      var result = new List<object>();
      foreach (var connection in _connections().Where(c => c.IsOpen)) {
        foreach (var state in connection.Link.MAVlist.Where(s => s.sysid != 0)) {
          var target = _targets.Values.FirstOrDefault(t => t.Connection == connection
              && t.State == state && t.Generation == connection.Generation);
          if (target == null) {
            target = new McpTarget(Guid.NewGuid().ToString("N"), connection, state, connection.Generation);
            _targets.Add(target.Id, target);
          }
          live.Add(target.Id);
          result.Add(new { targetId = target.Id, endpoint = connection.Endpoint,
            systemId = state.sysid, componentId = state.compid, firmware = state.VersionString,
            vehicleType = state.aptype.ToString(), autopilot = state.apname.ToString(),
            armed = state.cs.armed, lastPacketUtc = PacketTime(state), parameterCount = state.param.TotalReceived,
            reportedParameterCount = state.param.TotalReported });
        }
      }
      foreach (string stale in _targets.Keys.Where(id => !live.Contains(id)).ToArray()) { _targets.Remove(stale); }
      return result;
    }
  }

  private static DateTime? PacketTime(MAVState state) => state.lastvalidpacket == DateTime.MinValue
      ? null : state.lastvalidpacket.ToUniversalTime();

  internal McpTarget Resolve(string id, bool requireFresh = false) {
    McpTarget target;
    lock (_sync) {
      if (!_targets.TryGetValue(id, out target!)) { throw new ArgumentException("Unknown target; call list_vehicles."); }
    }
    if (!_connections().Contains(target.Connection) || !target.Connection.IsOpen
        || target.Connection.Generation != target.Generation
        || !target.Connection.Link.MAVlist.Any(s => ReferenceEquals(s, target.State))) {
      throw new InvalidOperationException("Vehicle connection changed. Call list_vehicles and start a new analysis.");
    }
    if (requireFresh && (PacketTime(target.State) is not DateTime last
        || DateTime.UtcNow - last > TimeSpan.FromSeconds(5))) {
      throw new InvalidOperationException("Fresh vehicle telemetry (within 5 seconds) is required.");
    }
    return target;
  }

  internal static void RequireDisarmed(McpTarget target) {
    // CurrentState is refreshed by the UI and may lag the packet stream. Read the
    // last heartbeat directly; other fresh telemetry cannot establish armed state.
    var packet = target.State.getPacketLast((uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT);
    if (packet == null || DateTime.UtcNow - packet.rxtime.ToUniversalTime() > TimeSpan.FromSeconds(5)) {
      throw new InvalidOperationException("A fresh vehicle heartbeat (within 5 seconds) is required.");
    }
    var heartbeat = packet.ToStructure<MAVLink.mavlink_heartbeat_t>();
    if ((heartbeat.base_mode & (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0 || target.State.cs.armed) {
      throw new InvalidOperationException("Vehicle must be disarmed for this operation.");
    }
  }

  internal static PropertyInfo[] TelemetryProperties => typeof(CurrentState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive
          || p.PropertyType.IsEnum || p.PropertyType == typeof(string) || p.PropertyType == typeof(double)
          || p.PropertyType == typeof(float))).OrderBy(p => p.Name).ToArray();

  internal object TelemetrySchema() => TelemetryProperties.Select(p => new { name = p.Name, type = p.PropertyType.Name,
    description = p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description }).ToArray();

  internal object Health(string targetId) {
    var target = Resolve(targetId);
    DateTime now = DateTime.UtcNow;
    object Packet<T>(MAVLink.MAVLINK_MSG_ID id, Func<T, object> project) where T : struct {
      var packet = target.State.getPacketLast((uint)id);
      if (packet == null) { return new { available = false }; }
      DateTime received = packet.rxtime.ToUniversalTime();
      double age = (now - received).TotalSeconds;
      return new { available = true, receivedUtc = received, ageSeconds = age,
        fresh = age is >= 0 and <= 5, values = project(packet.ToStructure<T>()) };
    }
    static double? Finite(float value) => float.IsFinite(value) ? value : null;
    var result = new { targetId, capturedUtc = now,
      heartbeat = Packet<MAVLink.mavlink_heartbeat_t>(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, p => new {
        armed = (p.base_mode & (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0,
        systemStatus = ((MAVLink.MAV_STATE)p.system_status).ToString(), customMode = p.custom_mode,
      }),
      system = Packet<MAVLink.mavlink_sys_status_t>(MAVLink.MAVLINK_MSG_ID.SYS_STATUS, p => new {
        sensorsPresent = p.onboard_control_sensors_present, sensorsEnabled = p.onboard_control_sensors_enabled,
        sensorsHealthy = p.onboard_control_sensors_health,
        unhealthyEnabledSensors = p.onboard_control_sensors_present & p.onboard_control_sensors_enabled & ~p.onboard_control_sensors_health,
        loadPercent = p.load / 10.0,
        batteryVolts = p.voltage_battery == ushort.MaxValue ? (double?)null : p.voltage_battery / 1000.0,
        batteryAmps = p.current_battery == -1 ? (double?)null : p.current_battery / 100.0,
        batteryRemainingPercent = p.battery_remaining < 0 ? (int?)null : p.battery_remaining,
        communicationDropPercent = p.drop_rate_comm / 100.0, communicationErrors = p.errors_comm,
      }),
      gps = Packet<MAVLink.mavlink_gps_raw_int_t>(MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT, p => new {
        fixType = p.fix_type, satellites = p.satellites_visible == byte.MaxValue ? (int?)null : p.satellites_visible,
        hdop = p.eph == ushort.MaxValue ? (double?)null : p.eph / 100.0,
        vdop = p.epv == ushort.MaxValue ? (double?)null : p.epv / 100.0,
      }),
      vibration = Packet<MAVLink.mavlink_vibration_t>(MAVLink.MAVLINK_MSG_ID.VIBRATION, p => new {
        timeUsec = p.time_usec, x = Finite(p.vibration_x), y = Finite(p.vibration_y), z = Finite(p.vibration_z),
        units = "m/s^2", clipping0 = p.clipping_0, clipping1 = p.clipping_1, clipping2 = p.clipping_2,
      }),
      ekf = Packet<MAVLink.mavlink_ekf_status_report_t>(MAVLink.MAVLINK_MSG_ID.EKF_STATUS_REPORT, p => new {
        flags = p.flags, velocityVariance = Finite(p.velocity_variance), horizontalPositionVariance = Finite(p.pos_horiz_variance),
        verticalPositionVariance = Finite(p.pos_vert_variance), compassVariance = Finite(p.compass_variance),
        terrainAltitudeVariance = Finite(p.terrain_alt_variance),
      }),
      note = "Latest raw MAVLink packets for this exact system/component, with separate receipt ages. Missing is unknown, not healthy. "
          + "Packets are not an atomic snapshot. Clipping counters are cumulative; compare over time. No stream rates or aircraft state were changed.",
    };
    Resolve(targetId);
    return result;
  }

  internal object Telemetry(string targetId, string fields) {
    var target = Resolve(targetId);
    var names = fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (names.Count is < 1 or > 80 || names.Any(n => !TelemetryProperties.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))) {
      throw new ArgumentException("Choose 1..80 field names from telemetry_schema.");
    }
    var values = new Dictionary<string, object?>();
    foreach (var property in TelemetryProperties.Where(p => names.Contains(p.Name))) {
      try {
        object? value = property.GetValue(target.State.cs);
        values[property.Name] = value switch {
          double d when !double.IsFinite(d) => null,
          float f when !float.IsFinite(f) => null,
          Enum e => e.ToString(),
          string s => s.Length > 4096 ? s[..4096] : s,
          _ => value,
        };
      } catch (TargetInvocationException) { values[property.Name] = null; }
    }
    Resolve(targetId);
    return new { targetId, capturedUtc = DateTime.UtcNow, lastPacketUtc = PacketTime(target.State), values,
      distanceUnit = CurrentState.DistanceUnit, altitudeUnit = CurrentState.AltUnit, speedUnit = CurrentState.SpeedUnit,
      note = "CurrentState display values use Mission Planner's configured units. Snapshot is best-effort, not atomic. "
          + "Last packet age is not a per-field freshness guarantee; use flight logs for quantitative timing analysis." };
  }

  internal object Parameters(string targetId, string filter, int offset, int count, bool metadata) {
    if (offset < 0 || count is < 1 or > 200 || filter.Length > 64) { throw new ArgumentException("offset >= 0, count 1..200, filter <= 64 characters."); }
    var target = Resolve(targetId);
    string firmware = target.State.cs.firmware.ToString();
    var parameters = target.State.param.Snapshot().Where(p => !Sensitive(p.Name)
        && p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name).ToArray();
    return new { targetId, total = parameters.Length, received = target.State.param.TotalReceived,
      reported = target.State.param.TotalReported,
      complete = target.State.param.TotalReported > 0 && target.State.param.TotalReceived == target.State.param.TotalReported,
      source = "Current connection parameter cache; use refresh_parameters to request fresh values. Security material omitted.",
      parameters = parameters.Skip(offset).Take(count).Select(p => new {
        name = p.Name, value = double.IsFinite(p.Value) ? (double?)p.Value : null,
        type = p.TypeAP.ToString(), metadata = metadata ? Metadata(p.Name, firmware) : null,
      }).ToArray() };
  }

  internal static Dictionary<string, string> Metadata(string name, string firmware) =>
      new[] { "DisplayName", "Description", "Units", "Range", "Values", "Bitmask", "Increment", "ReadOnly", "RebootRequired" }
          .ToDictionary(k => k, k => ParameterMetaDataRepository.GetParameterMetaData(name, k, firmware));

  internal async Task<object> Refresh(string targetId, CancellationToken ct) {
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    lifetime.CancelAfter(TimeSpan.FromMinutes(3));
    using var watcher = WatchTarget(targetId, lifetime, true);
    ct = lifetime.Token;
    var target = Resolve(targetId, true);
    RequireDisarmed(target);
    if (target.Connection.Link.giveComport) { throw new InvalidOperationException("Vehicle transport is busy."); }
    // Existing coordinator handles parsing, missing parameters and cancellation, with explicit target IDs.
    await _transfer.WaitAsync(ct).ConfigureAwait(false);
    try {
      Resolve(targetId, true);
      if (target.Connection.Link.giveComport) { throw new InvalidOperationException("Vehicle transport is busy."); }
      var coordinator = new VehicleParameterLoadCoordinator(target.Connection.Link);
      bool complete = await coordinator.LoadLatestAsync(target.State.sysid, target.State.compid, ct).ConfigureAwait(false);
      Resolve(targetId);
      return new { complete, received = target.State.param.TotalReceived, reported = target.State.param.TotalReported };
    } finally { _transfer.Release(); }
  }

  internal async Task<object> OnboardLogs(string targetId, CancellationToken ct) {
    var target = Resolve(targetId, true);
    var entries = new Dictionary<ushort, MAVLink.mavlink_log_entry_t>();
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    object sync = new();
    void Receive(object? sender, MAVLink.MAVLinkMessage message) {
      if (message.msgid != (uint)MAVLink.MAVLINK_MSG_ID.LOG_ENTRY || message.sysid != target.State.sysid
          || message.compid != target.State.compid) { return; }
      var item = message.ToStructure<MAVLink.mavlink_log_entry_t>();
      lock (sync) {
        entries[item.id] = item;
        if (item.num_logs == 0 || entries.Count >= item.num_logs) { ready.TrySetResult(); }
      }
    }
    await _transfer.WaitAsync(ct).ConfigureAwait(false);
    var link = target.Connection.Link;
    link.OnPacketReceived += Receive;
    try {
      bool complete = false;
      for (int attempt = 0; attempt < 3 && !complete; attempt++) {
        Resolve(targetId, true);
        link.generatePacket((byte)MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_LIST, new MAVLink.mavlink_log_request_list_t {
          target_system = target.State.sysid, target_component = target.State.compid, start = 0, end = ushort.MaxValue,
        }, target.State.sysid, target.State.compid);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); complete = true; }
        catch (TimeoutException) { }
      }
      Resolve(targetId);
      lock (sync) {
        return new { complete, logs = entries.Values.Where(e => e.num_logs != 0).OrderBy(e => e.id)
            .Select(e => new { id = e.id, bytes = e.size, utcUnixSeconds = e.time_utc }).ToArray() };
      }
    } finally { link.OnPacketReceived -= Receive; _transfer.Release(); }
  }

  internal async Task<string> Download(string targetId, ushort logId, CancellationToken ct) {
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    using var watcher = WatchTarget(targetId, lifetime, true);
    ct = lifetime.Token;
    var target = Resolve(targetId, true);
    RequireDisarmed(target);
    await _transfer.WaitAsync(ct).ConfigureAwait(false);
    try {
      Resolve(targetId, true);
      if (target.Connection.Link.giveComport) { throw new InvalidOperationException("Vehicle transport is busy."); }
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
      timeout.CancelAfter(TimeSpan.FromMinutes(10));
      string path = await target.Connection.Link.GetLog(target.State.sysid, target.State.compid, logId, timeout.Token).ConfigureAwait(false);
      try { Resolve(targetId); return path; } catch { System.IO.File.Delete(path); throw; }
    } finally { _transfer.Release(); }
  }

  private Timer WatchTarget(string id, CancellationTokenSource lifetime, bool requireDisarmed) => new(_ => {
    try {
      var target = Resolve(id);
      if (requireDisarmed) { RequireDisarmed(target); }
    } catch (Exception e) when (e is ArgumentException or InvalidOperationException or ObjectDisposedException) {
      try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }
  }, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

  internal ParameterProposal Propose(string targetId, ParameterChange[] changes, string rationale) {
    if (changes.Length is < 1 or > 100 || rationale.Length is < 10 or > 8000
        || changes.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != changes.Length) {
      throw new ArgumentException("Provide 1..100 unique changes and a rationale of 10..8000 characters.");
    }
    var target = Resolve(targetId, true);
    foreach (var change in changes) { ValidateChange(target, change); }
    lock (_sync) {
      if (_proposals.Count >= 100) { throw new InvalidOperationException("Proposal limit reached; restart the server."); }
      var proposal = new ParameterProposal(Guid.NewGuid().ToString("N"), targetId, rationale, changes.ToArray(), DateTime.UtcNow);
      _proposals.Add(proposal.Id, proposal);
      return proposal;
    }
  }

  internal static void ValidateChange(McpTarget target, ParameterChange change) {
    if (Sensitive(change.Name) || change.Name.Length is < 1 or > 16
        || !double.IsFinite(change.Expected) || !double.IsFinite(change.Proposed)
        || change.Reason.Length is < 5 or > 2000) { throw new ArgumentException("Invalid parameter change or security-sensitive parameter."); }
    var parameter = target.State.param[change.Name] ?? throw new ArgumentException("Unknown parameter: " + change.Name);
    if (parameter.Value != change.Expected) { throw new InvalidOperationException("Parameter changed since analysis: " + change.Name); }
    string fw = target.State.cs.firmware.ToString();
    var meta = Metadata(change.Name, fw);
    if (meta["ReadOnly"].Equals("True", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Read-only parameter: " + change.Name); }
    double min = 0, max = 0;
    if (ParameterMetaDataRepository.GetParameterRange(change.Name, ref min, ref max, fw)
        && (change.Proposed < min || change.Proposed > max)) { throw new ArgumentException("Outside metadata range: " + change.Name); }
    (double low, double high) = parameter.TypeAP switch {
      MAVLink.MAV_PARAM_TYPE.UINT8 => (byte.MinValue, byte.MaxValue),
      MAVLink.MAV_PARAM_TYPE.INT8 => (sbyte.MinValue, sbyte.MaxValue),
      MAVLink.MAV_PARAM_TYPE.UINT16 => (ushort.MinValue, ushort.MaxValue),
      MAVLink.MAV_PARAM_TYPE.INT16 => (short.MinValue, short.MaxValue),
      MAVLink.MAV_PARAM_TYPE.UINT32 => (uint.MinValue, uint.MaxValue),
      MAVLink.MAV_PARAM_TYPE.INT32 => (int.MinValue, int.MaxValue),
      MAVLink.MAV_PARAM_TYPE.REAL32 => (-float.MaxValue, float.MaxValue),
      _ => throw new ArgumentException("Unsupported MAVLink wire type: " + parameter.TypeAP),
    };
    if (parameter.TypeAP != MAVLink.MAV_PARAM_TYPE.REAL32
        && !MAVLinkInterface.UsesBytewiseParameterEncoding(target.State.cs.capabilities, target.State.apname)
        && (double)(float)change.Proposed != change.Proposed) {
      throw new ArgumentException("Integer value cannot be represented exactly by this vehicle's C-cast parameter encoding: " + change.Name);
    }
    if (change.Proposed < low || change.Proposed > high) { throw new ArgumentException("Outside wire-type range: " + change.Name); }
    if (parameter.TypeAP != MAVLink.MAV_PARAM_TYPE.REAL32 && parameter.TypeAP != MAVLink.MAV_PARAM_TYPE.REAL64
        && change.Proposed != Math.Truncate(change.Proposed)) { throw new ArgumentException("Integer parameter requires an integer: " + change.Name); }
  }

  internal ParameterProposal[] Proposals() { lock (_sync) { return _proposals.Values.ToArray(); } }
  internal ParameterProposal Proposal(string id) {
    lock (_sync) { return _proposals.TryGetValue(id, out var p) ? p : throw new ArgumentException("Unknown proposal."); }
  }
}
