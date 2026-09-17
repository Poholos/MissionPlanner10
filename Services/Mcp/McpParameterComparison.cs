using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpParameterPoint(string Name, double? Value, string? Type, double? TimeSeconds, int? Line);

internal static class McpParameterComparison {
  internal static Dictionary<string, McpParameterPoint> DataFlash(DFLogBuffer log, double at, string filter, CancellationToken ct) {
    Validate(at, filter);
    var result = new Dictionary<string, McpParameterPoint>(StringComparer.Ordinal);
    if (!log.SeenMessageTypes.Contains("PARM")) { return result; }
    double previous = -1;
    foreach (var row in log.GetEnumeratorType("PARM")) {
      ct.ThrowIfCancellationRequested();
      double time = McpLogContext.Time(row);
      if (!double.IsFinite(time)) { continue; }
      if (time < previous) { throw new ArgumentException("PARM clock moves backwards; select a single-boot log."); }
      previous = time;
      string name = row["Name"]?.Trim() ?? "";
      if (time < 0 || time > at || !Include(name, filter)) { continue; }
      double value = McpLogCatalog.Number(row["Value"]);
      Put(result, new(name, double.IsFinite(value) ? value : null, null, time, row.lineno));
    }
    return result;
  }

  internal static Dictionary<string, McpParameterPoint> Telemetry(string path, double at, string filter, string? source, CancellationToken ct) {
    Validate(at, filter);
    if (string.IsNullOrWhiteSpace(source)) { throw new ArgumentException("TLOG parameter comparison requires an explicit systemId:componentId source from log_schema."); }
    var result = new Dictionary<string, McpParameterPoint>(StringComparer.Ordinal);
    foreach (var p in McpTelemetryLog.ParameterHistory(path, ct)) {
      if (p.Instance != source || p.TimeSeconds > at || !Include(p.Name, filter)) { continue; }
      Put(result, new(p.Name, p.Value, p.Type, p.TimeSeconds, p.Line));
    }
    return result;
  }

  internal static void Validate(double at, string filter) {
    McpLogCatalog.ValidateWindow(0, at);
    if (filter.Length > 64) { throw new ArgumentException("filter <= 64 characters."); }
  }
  internal static bool Include(string name, string filter) => name.Length > 0 && !McpVehicleAccess.Sensitive(name)
      && name.Contains(filter, StringComparison.OrdinalIgnoreCase);
  private static void Put(Dictionary<string, McpParameterPoint> values, McpParameterPoint p) {
    if (!values.ContainsKey(p.Name) && values.Count >= 16384) { throw new ArgumentException("Too many parameters; narrow filter."); }
    values[p.Name] = p;
  }

  internal static object Compare(IReadOnlyDictionary<string, McpParameterPoint> before,
      IReadOnlyDictionary<string, McpParameterPoint> after, int offset, int count, bool includeUnchanged) {
    if (offset < 0 || count is < 1 or > 200) { throw new ArgumentException("offset >= 0, count 1..200."); }
    var differences = before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(name => {
      before.TryGetValue(name, out var a); after.TryGetValue(name, out var b);
      string status = a == null ? "onlyAfter" : b == null ? "onlyBefore"
          : a.Value == null || b.Value == null ? "unknown"
          : a.Type != null && b.Type != null && a.Type != b.Type ? "typeChanged"
          : a.Value == b.Value ? "unchanged" : "changed";
      double? delta = a?.Value is double av && b?.Value is double bv && double.IsFinite(bv - av) ? bv - av : null;
      return new { name, status, before = a, after = b, delta };
    }).ToArray();
    var selected = differences.Where(p => includeUnchanged || p.status != "unchanged").ToArray();
    return new { beforeCount = before.Count, afterCount = after.Count,
      summary = differences.GroupBy(p => p.status).ToDictionary(g => g.Key, g => g.Count()),
      total = selected.Length, differences = selected.Skip(offset).Take(count).ToArray(),
      nextOffset = Math.Min((long)offset + count, selected.Length), complete = (long)offset + count >= selected.Length,
      note = "Exact numeric comparison, after minus before; no tolerance or parameter writes. Missing means unrecorded/unknown, not deleted. "
          + "Unknown encodings remain null. DataFlash does not establish MAVLink wire type. Verify aircraft/firmware identity before attributing differences." };
  }
}
