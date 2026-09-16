using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services.Mcp;

/// <summary>Streaming, bounded summaries of native DataFlash records; no aircraft writes.</summary>
internal static class McpFlightAnalysis {
  internal sealed record VibrationRecord(double Time, string Instance, IReadOnlyDictionary<string, double> Fields);
  private static readonly string[] VibrationFields = ["VibeX", "VibeY", "VibeZ", "Clip", "Clip0", "Clip1", "Clip2"];
  internal static object Overview(DFLogBuffer log, CancellationToken ct) {
    var messages = new Dictionary<string, MessageSummary>();
    int scanned = 0;
    foreach (var row in log.GetEnumeratorType(log.SeenMessageTypes.ToArray())) {
      if ((scanned++ & 255) == 0) { ct.ThrowIfCancellationRequested(); }
      if (!messages.TryGetValue(row.msgtype, out var summary)) { messages[row.msgtype] = summary = new(); }
      summary.Count++;
      double time = Time(row);
      if (double.IsFinite(time)) {
        summary.Start = Math.Min(summary.Start, time); summary.End = Math.Max(summary.End, time);
      }
      if (summary.Instances.Count < 64) { summary.Instances.Add(McpLogCatalog.Instance(log, row)); }
      else if (!summary.Instances.Contains(McpLogCatalog.Instance(log, row))) { summary.InstancesTruncated = true; }
    }
    return new { lines = log.Count, messages = messages.OrderBy(p => p.Key).Select(p => new {
      type = p.Key, count = p.Value.Count,
      startSeconds = double.IsFinite(p.Value.Start) ? (double?)p.Value.Start : null,
      endSeconds = double.IsFinite(p.Value.End) ? (double?)p.Value.End : null,
      instances = p.Value.Instances.Order().ToArray(), instancesTruncated = p.Value.InstancesTruncated,
    }).ToArray(),
      suggestedEventTypes = new[] { "MSG", "MODE", "EV", "ERR", "ARM", "MAV" }.Where(messages.ContainsKey).ToArray(),
      note = "Times use each record's TimeUS/TimeMS when present; untimed metadata has null bounds. "
          + "Select flight segments with read_log_records (MODE/ARM/EV/ERR). Message counts are not proof of continuous sampling.",
    };
  }

  internal static object Parameters(DFLogBuffer log, double atSeconds, string filter, int offset, int count, CancellationToken ct) {
    McpLogCatalog.ValidateWindow(0, atSeconds);
    if (offset < 0 || count is < 1 or > 200 || filter.Length > 64) { throw new ArgumentException("offset >= 0, count 1..200, filter <= 64 characters."); }
    if (!log.SeenMessageTypes.Contains("PARM")) { throw new ArgumentException("This log has no PARM history."); }
    var values = new Dictionary<string, ParameterHistory>(StringComparer.Ordinal);
    int scanned = 0;
    foreach (var row in log.GetEnumeratorType("PARM")) {
      if ((scanned++ & 255) == 0) { ct.ThrowIfCancellationRequested(); }
      string name = row["Name"]?.Trim() ?? "";
      double time = Time(row), value = McpLogCatalog.Number(row["Value"]);
      if (!double.IsFinite(time) || time < 0 || time > atSeconds || !double.IsFinite(value)
          || name.Length == 0 || McpVehicleAccess.Sensitive(name) || !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) { continue; }
      if (!values.TryGetValue(name, out var previous)) {
        if (values.Count >= 16384) { throw new ArgumentException("Too many parameters; narrow the filter."); }
        values[name] = new(value, time, row.lineno, 1, 0);
      } else {
        if (time < previous.TimeSeconds) { throw new ArgumentException("PARM timestamps go backwards; this log may contain multiple boots. Analyze separate flight logs."); }
        values[name] = new(value, time, row.lineno, previous.Records + 1, previous.Changes + (value != previous.Value ? 1 : 0));
      }
    }
    return new { atSeconds, total = values.Count, parameters = values.OrderBy(p => p.Key, StringComparer.Ordinal)
      .Skip(offset).Take(count).Select(p => new { name = p.Key, value = p.Value.Value,
        timeSeconds = p.Value.TimeSeconds, line = p.Value.Line, records = p.Value.Records, changes = p.Value.Changes }).ToArray(),
      note = "Last recorded PARM at or before the requested boot time; never backfilled from later records or current vehicle parameters. "
          + "An absent parameter is unknown. Security-sensitive names omitted. Use read_log_records(PARM) for individual changes.",
    };
  }

  internal static object Vibration(DFLogBuffer log, double start, double end, CancellationToken ct) {
    McpLogCatalog.ValidateWindow(start, end);
    if (!log.SeenMessageTypes.Contains("VIBE")) { throw new ArgumentException("This log has no VIBE records. Inspect log_schema for IMU/ACC or ISBH/ISBD."); }
    return SummarizeVibration(log.GetEnumeratorType("VIBE").Select(row => new VibrationRecord(Time(row), McpLogCatalog.Instance(log, row),
        VibrationFields.ToDictionary(f => f, f => McpLogCatalog.Number(row[f]) * (f.StartsWith("Vibe", StringComparison.Ordinal)
            ? McpLogCatalog.Scale(log, "VIBE", f) : 1)))), start, end, ct, "DataFlash seconds since boot; instances identify IMUs.");
  }

  internal static object SummarizeVibration(IEnumerable<VibrationRecord> records, double start, double end, CancellationToken ct, string timeBasis) {
    McpLogCatalog.ValidateWindow(start, end);
    var sensors = new Dictionary<string, VibrationSummary>();
    int scanned = 0;
    foreach (var row in records) {
      if ((scanned++ & 255) == 0) { ct.ThrowIfCancellationRequested(); }
      double time = row.Time;
      if (!double.IsFinite(time) || time < start || time > end) { continue; }
      string instance = row.Instance;
      if (!sensors.TryGetValue(instance, out var sensor)) {
        if (sensors.Count >= 64) { throw new ArgumentException("Too many VIBE instances."); }
        sensors[instance] = sensor = new();
      }
      if (sensor.Count > 0 && time < sensor.End) { throw new ArgumentException("VIBE timestamps go backwards; select a single-boot log."); }
      if (sensor.Count++ == 0) { sensor.Start = time; }
      sensor.End = time;
      foreach (string field in new[] { "VibeX", "VibeY", "VibeZ" }) {
        double value = row.Fields.GetValueOrDefault(field, double.NaN);
        if (!double.IsFinite(value) || value < 0) { continue; }
        if (!sensor.Axes.TryGetValue(field, out var axis)) { sensor.Axes[field] = axis = new(); }
        axis.Count++; axis.Mean += (value - axis.Mean) / axis.Count;
        axis.Maximum = Math.Max(axis.Maximum, value);
        if (value > 30) { axis.Above30++; }
        if (value > 60) { axis.Above60++; }
      }
      // Modern logs use per-instance Clip; legacy Clip0/1/2 identify accelerometers.
      foreach (string field in new[] { "Clip", "Clip0", "Clip1", "Clip2" }) {
        double value = row.Fields.GetValueOrDefault(field, double.NaN);
        if (!double.IsFinite(value) || value < 0 || value != Math.Truncate(value)) { continue; }
        if (!sensor.Clipping.TryGetValue(field, out var counter)) { sensor.Clipping[field] = counter = new(); }
        if (counter.Samples++ == 0) { counter.First = value; }
        else if (value >= counter.Last) { counter.Increase += value - counter.Last; }
        else { counter.Resets++; }
        counter.Last = value;
      }
    }
    return new { startSeconds = start, endSeconds = end, units = "m/s^2", timeBasis, sensors = sensors.OrderBy(p => p.Key).Select(p => new {
      instance = p.Key, records = p.Value.Count, startSeconds = p.Value.Start, endSeconds = p.Value.End,
      axes = p.Value.Axes.ToDictionary(a => a.Key, a => new { samples = a.Value.Count, mean = a.Value.Mean,
        max = a.Value.Maximum, above30Samples = a.Value.Above30, above60Samples = a.Value.Above60 }),
      clipping = p.Value.Clipping.ToDictionary(c => c.Key, c => new { samples = c.Value.Samples, first = c.Value.First,
        last = c.Value.Last, observedIncrease = c.Value.Increase, resetsOrWraps = c.Value.Resets }),
    }).ToArray(),
      guidance = "ArduPilot guidance: VIBE below 30 m/s^2 is normally acceptable; above 60 can cause problems. "
          + "Counts above thresholds are samples, not elapsed time. Missing axes/counters are unknown. "
          + "Clipping increase excludes the first sample and intervals crossing a reset/wrap, so it is a lower bound. "
          + "Clip belongs to the record's IMU instance; legacy Clip0/1/2 identify accelerometers. Do not sum repeated counters across instances. "
          + "Use raw IMU spectra to investigate resonance; VIBE is an envelope, not raw acceleration. This is not a flight-safety verdict.",
      reference = "https://ardupilot.org/copter/docs/common-measuring-vibration.html",
    };
  }

  private static double Time(DFLog.DFItem row) => row["TimeUS"] is string us ? McpLogCatalog.Number(us) / 1e6
      : row["TimeMS"] is string ms ? McpLogCatalog.Number(ms) / 1000 : double.NaN;
  private sealed class MessageSummary {
    internal long Count;
    internal double Start = double.PositiveInfinity, End = double.NegativeInfinity;
    internal readonly HashSet<string> Instances = new();
    internal bool InstancesTruncated;
  }
  private sealed record ParameterHistory(double Value, double TimeSeconds, int Line, long Records, long Changes);
  private sealed class VibrationSummary {
    internal long Count;
    internal double Start, End;
    internal readonly Dictionary<string, AxisSummary> Axes = new();
    internal readonly Dictionary<string, CounterSummary> Clipping = new();
  }
  private sealed class AxisSummary {
    internal long Count, Above30, Above60;
    internal double Mean, Maximum;
  }
  private sealed class CounterSummary {
    internal long Samples, Resets;
    internal double First, Last, Increase;
  }
}
