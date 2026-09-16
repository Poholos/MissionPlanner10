using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace MissionPlanner.Services.Mcp;

internal sealed partial class MissionPlannerMcpTools {
  [McpServerTool(Name = "read_vehicle_messages", ReadOnly = true), Description("Read retained STATUSTEXT packets from one exact target: pre-arm reasons, warnings and firmware messages. Severity 0 is most severe, 7 includes debug. Page with nextSequence; reports dropped history. MAVLink 2 chunks retain IDs, not falsely joined messages.")]
  public string VehicleMessages(string targetId, long afterSequence = 0, int count = 100, int maximumSeverity = 7) =>
      Guard(() => _vehicles.Messages(targetId, afterSequence, count, maximumSeverity));

  [McpServerTool(Name = "telemetry_packet_inventory", ReadOnly = true), Description("Page received message types and last receipt ages for one exact target. Diagnose absent/stale telemetry before interpreting CurrentState values. Does not request streams or expose packet payloads.")]
  public string PacketInventory(string targetId, int offset = 0, int count = 100) =>
      Guard(() => _vehicles.PacketInventory(targetId, offset, count));

  [McpServerTool(Name = "log_events", ReadOnly = true), Description("Flight event evidence: DataFlash MODE/ARM/EV/ERR/MSG/FAIL; TLOG state changes, status text and command acknowledgments. Page by nextLine, filter by time/source. First observed state is not a proven transition. Inspect this before selecting analysis windows.")]
  public Task<string> Events(string logId, CancellationToken cancellationToken, int startLine = 0, int count = 100,
      double startSeconds = 0, double endSeconds = 1e12, string? instance = null) => GuardAsync(async () => {
    bool telemetry = _logs.IsTelemetry(logId);
    object result = telemetry
        ? await _logs.ReadFile(logId, (path, ct) => McpLogContext.Events(McpLogContext.TelemetryEvents(path, ct),
            startLine, count, startSeconds, endSeconds, instance, ct), cancellationToken).ConfigureAwait(false)
        : await _logs.Read(logId, (log, ct) => McpLogContext.Events(McpLogContext.DataFlashEvents(log, ct),
            startLine, count, startSeconds, endSeconds, instance, ct), cancellationToken).ConfigureAwait(false);
    return new { logId, timeBasis = telemetry ? "elapsed receipt seconds" : "boot seconds", result };
  });

  [McpServerTool(Name = "log_time_series", ReadOnly = true), Description("Compact full-window numeric trend for one log message/field. Equal-duration bins preserve all-sample means, counts, extrema with times and gaps; each sensor/source stays separate. Useful for battery, EKF, RC/motor output and tracking trends. Not suitable for FFT; use log_spectrum for raw data.")]
  public Task<string> TimeSeries(string logId, string type, string field, double startSeconds, double endSeconds,
      CancellationToken cancellationToken, int bins = 100, string? instance = null) => GuardAsync(async () => {
    bool telemetry = _logs.IsTelemetry(logId);
    object result = telemetry
        ? await _logs.ReadFile(logId, (path, ct) => McpLogContext.Trend(McpLogContext.TelemetrySamples(path, type, field, ct),
            startSeconds, endSeconds, bins, instance, ct), cancellationToken).ConfigureAwait(false)
        : await _logs.Read(logId, (log, ct) => McpLogContext.Trend(McpLogContext.DataFlashSamples(log, type, field, ct),
            startSeconds, endSeconds, bins, instance, ct), cancellationToken).ConfigureAwait(false);
    return new { logId, type, field, timeBasis = telemetry ? "elapsed receipt seconds" : "boot seconds", result };
  });

  private Task<Dictionary<string, McpParameterPoint>> ParameterSnapshot(string id, double time, string filter,
      string? source, CancellationToken ct) {
    McpParameterComparison.Validate(time, filter);
    if (_logs.IsTelemetry(id)) {
      string[] parts = (source ?? "").Split(':');
      if (parts.Length != 2 || !byte.TryParse(parts[0], out _) || !byte.TryParse(parts[1], out _)) {
        throw new ArgumentException("TLOG comparison requires systemId:componentId from log_schema.");
      }
      return _logs.ReadFile(id, (path, token) => McpParameterComparison.Telemetry(path, time, filter, source, token), ct);
    }
    if (!string.IsNullOrEmpty(source)) { throw new ArgumentException("DataFlash contains one aircraft; omit its source argument."); }
    return _logs.Read(id, (log, token) => McpParameterComparison.DataFlash(log, time, filter, token), ct);
  }

  [McpServerTool(Name = "compare_log_parameters", ReadOnly = true), Description("Compare recorded parameters at two explicit times in the same or different BIN/LOG/TLOG files. TLOG requires beforeSource/afterSource = systemId:componentId. Returns changed, missing and unknown values with original line/time evidence, never live substitutions or writes.")]
  public Task<string> CompareLogParameters(string beforeLogId, double beforeSeconds, string afterLogId,
      double afterSeconds, CancellationToken cancellationToken, string? beforeSource = null, string? afterSource = null,
      string filter = "", int offset = 0, int count = 100, bool includeUnchanged = false) => GuardAsync(async () => {
    var before = await ParameterSnapshot(beforeLogId, beforeSeconds, filter, beforeSource, cancellationToken).ConfigureAwait(false);
    var after = await ParameterSnapshot(afterLogId, afterSeconds, filter, afterSource, cancellationToken).ConfigureAwait(false);
    _ = _logs.PathFor(beforeLogId); _ = _logs.PathFor(afterLogId);
    return new { before = new { logId = beforeLogId, seconds = beforeSeconds, source = beforeSource },
      after = new { logId = afterLogId, seconds = afterSeconds, source = afterSource },
      comparison = McpParameterComparison.Compare(before, after, offset, count, includeUnchanged),
      timeBasis = "Each log has its own clock: DataFlash boot seconds, TLOG elapsed receipt seconds. Times are not aligned automatically." };
  });

  [McpServerTool(Name = "compare_vehicle_parameters_to_log", ReadOnly = true), Description("Compare a recorded flight-time parameter snapshot with one exact vehicle's current cache. Requires explicit TLOG source. Reports cache completeness and capture time. Does not assume the log belongs to this aircraft, refresh parameters, stage changes or write anything.")]
  public Task<string> CompareVehicleParameters(string targetId, string logId, double atSeconds,
      CancellationToken cancellationToken, string? source = null, string filter = "", int offset = 0, int count = 100,
      bool includeUnchanged = false) => GuardAsync(async () => {
    _ = _vehicles.Resolve(targetId);
    var before = await ParameterSnapshot(logId, atSeconds, filter, source, cancellationToken).ConfigureAwait(false);
    var target = _vehicles.Resolve(targetId);
    var captured = DateTime.UtcNow;
    var snapshot = target.State.param.Snapshot();
    var after = snapshot.Where(p => McpParameterComparison.Include(p.Name, filter)).ToDictionary(p => p.Name,
        p => new McpParameterPoint(p.Name, double.IsFinite(p.Value) ? p.Value : null, p.TypeAP.ToString(), null, null), StringComparer.Ordinal);
    int reported = target.State.param.TotalReported;
    _ = _vehicles.Resolve(targetId); _ = _logs.PathFor(logId);
    return new { targetId, logId, atSeconds, source, capturedUtc = captured, received = snapshot.Length, reported,
      complete = reported > 0 && snapshot.Length == reported,
      currentSource = "Current connection parameter cache; capture time is not per-parameter freshness. Verify log/aircraft identity explicitly.",
      comparison = McpParameterComparison.Compare(before, after, offset, count, includeUnchanged) };
  });
}
