using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpLogInfo(string Id, string Name, long Bytes, DateTime ModifiedUtc, string Format = "dataflash") {
  public override string ToString() => $"{Name} — {Bytes / 1048576.0:0.0} MB ({Format})";
}
internal sealed record McpLogRow(int Line, double TimeSeconds, string Type, string Instance,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>Only user-attached or log-directory files receive opaque handles. Never arbitrary filesystem RPC.</summary>
internal sealed class McpLogCatalog : IDisposable {
  private readonly Dictionary<string, (string Path, McpLogInfo Info)> _files = new();
  private readonly object _sync = new();
  private readonly SemaphoreSlim _readGate = new(1, 1);
  private DFLogBuffer? _reader;
  private string? _readerId;
  private bool _disposed;

  internal McpLogInfo Attach(string path) {
    path = Path.GetFullPath(path);
    if (!new[] { ".bin", ".log", ".tlog" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) {
      throw new ArgumentException("Select a DataFlash .bin/.log or telemetry .tlog file.");
    }
    var file = new FileInfo(path);
    if (!file.Exists) { throw new FileNotFoundException("Log does not exist."); }
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      var existing = _files.Values.FirstOrDefault(f => f.Path == path
          && f.Info.Bytes == file.Length && f.Info.ModifiedUtc == file.LastWriteTimeUtc);
      if (existing.Info != null) { return existing.Info; }
      if (_files.Count >= 1000) { throw new InvalidOperationException("Log catalog limit reached; restart the MCP session."); }
      var info = new McpLogInfo(Guid.NewGuid().ToString("N"), file.Name, file.Length, file.LastWriteTimeUtc,
          McpTelemetryLog.IsTlog(path) ? "tlog" : "dataflash");
      _files.Add(info.Id, (path, info));
      return info;
    }
  }

  internal McpLogInfo[] List() {
    lock (_sync) { return _files.Values.Select(f => f.Info).OrderByDescending(f => f.ModifiedUtc).ToArray(); }
  }

  internal void Discover(string directory) {
    if (!Directory.Exists(directory)) { return; }
    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
      AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 5 };
    foreach (string path in Directory.EnumerateFiles(directory, "*", options)
        .Where(p => Path.GetExtension(p).Equals(".bin", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(p).Equals(".log", StringComparison.OrdinalIgnoreCase)
            || McpTelemetryLog.IsTlog(p)).Take(500)) {
      try { Attach(path); } catch (IOException) { }
    }
  }

  internal string PathFor(string id) {
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (!_files.TryGetValue(id, out var entry)) { throw new ArgumentException("Unknown log ID; discover or attach the log first."); }
      var file = new FileInfo(entry.Path);
      if (!file.Exists || file.Length != entry.Info.Bytes || file.LastWriteTimeUtc != entry.Info.ModifiedUtc) {
        throw new InvalidOperationException("Log changed since discovery; attach/discover it again to get a new ID.");
      }
      return entry.Path;
    }
  }

  internal bool IsTelemetry(string id) => McpTelemetryLog.IsTlog(PathFor(id));

  internal async Task<T> ReadFile<T>(string id, Func<string, CancellationToken, T> action, CancellationToken ct) {
    await _readGate.WaitAsync(ct).ConfigureAwait(false);
    try {
      string path = PathFor(id);
      T result = await Task.Run(() => action(path, ct), ct).ConfigureAwait(false);
      _ = PathFor(id);
      return result;
    } finally { _readGate.Release(); }
  }

  internal async Task<T> Read<T>(string id, Func<DFLogBuffer, CancellationToken, T> action, CancellationToken ct) {
    if (IsTelemetry(id)) { throw new ArgumentException("This operation requires a DataFlash .bin/.log. For tlogs use log_schema, read_log_records or log_field_statistics; receipt timestamps cannot establish raw IMU sampling."); }
    await _readGate.WaitAsync(ct).ConfigureAwait(false);
    try {
      (string Path, McpLogInfo Info) entry;
      lock (_sync) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_files.TryGetValue(id, out entry)) { throw new ArgumentException("Unknown log ID. Use list_local_logs or attach a log in Mission Planner."); }
      }
      void ValidateFile() {
        var file = new FileInfo(entry.Path);
        if (!file.Exists || file.Length != entry.Info.Bytes || file.LastWriteTimeUtc != entry.Info.ModifiedUtc) {
          throw new InvalidOperationException("Log changed since discovery; attach/discover it again to get a new ID.");
        }
      }
      ValidateFile();
      return await Task.Run(() => {
        ct.ThrowIfCancellationRequested();
        if (_readerId != id) {
          _reader?.Dispose(); _reader = null; _readerId = null;
          // Index construction is synchronous upstream. Keep it off the UI and serialize ownership.
          _reader = new DFLogBuffer(entry.Path); _readerId = id;
        }
        ct.ThrowIfCancellationRequested();
        T result = action(_reader!, ct);
        ValidateFile();
        return result;
      }, ct).ConfigureAwait(false);
    } finally { _readGate.Release(); }
  }

  internal static object Schema(DFLogBuffer log) => new {
    lines = log.Count,
    messages = log.SeenMessageTypes.Order().Select(type => new {
      type,
      fields = log.dflog.logformat[type].FieldNames.Select(field => new {
        name = field, unit = log.GetUnit(type, field).Item1,
        multiplier = Scale(log, type, field),
        multiplierKnown = log.GetUnit(type, field).Item2 > 0,
      }).ToArray(),
    }).ToArray(),
    valueEncoding = "Decoded DataFlash field values as shown by Mission Planner, before optional FMTU display multipliers. "
        + "Use the supplied multiplier exactly once for physical units. Array fields remain comma-separated values.",
  };

  internal static object Rows(DFLogBuffer log, string types, int startLine, int count,
      double startSeconds, double endSeconds, string? instance, CancellationToken ct) {
    ValidateWindow(startSeconds, endSeconds);
    if (startLine < 0 || count is < 1 or > 500) { throw new ArgumentException("startLine >= 0; count must be 1..500."); }
    string[] selected = Types(log, types);
    var rows = new List<McpLogRow>();
    int scanned = 0, next = log.Count;
    foreach (var item in log.GetEnumeratorType(selected)) {
      if ((scanned++ & 255) == 0) { ct.ThrowIfCancellationRequested(); }
      if (item.lineno < startLine) { continue; }
      double time = item.timems / 1000;
      if (time < startSeconds || time > endSeconds || (instance != null && Instance(log, item) != instance)) { continue; }
      if (rows.Count == count) { next = item.lineno; break; }
      rows.Add(new McpLogRow(item.lineno, time, item.msgtype, Instance(log, item),
          log.dflog.logformat[item.msgtype].FieldNames.ToDictionary(f => f, f => item[f] ?? "")));
    }
    return new { rows, nextLine = next, complete = next >= log.Count };
  }

  internal static string[] Types(DFLogBuffer log, string types) {
    string[] names = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
    if (names.Length is < 1 or > 32 || names.Any(n => !log.dflog.logformat.ContainsKey(n))) {
      throw new ArgumentException("Request 1..32 message types from log_schema, separated by commas.");
    }
    return names;
  }

  internal static void ValidateWindow(double start, double end) {
    if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start) {
      throw new ArgumentException("Time window must be finite with 0 <= start <= end (DataFlash boot seconds or TLOG elapsed receipt seconds).");
    }
  }

  internal static List<DiagnosticSample>[] Series(DFLogBuffer log, string type, string[] fields,
      double start, double end, string? instance, CancellationToken ct) {
    ValidateWindow(start, end);
    _ = Types(log, type);
    if (!log.dflog.logformat.TryGetValue(type, out var format) || fields.Any(f => !format.FieldNames.Contains(f))) {
      throw new ArgumentException("Unknown message field. Inspect log_schema first.");
    }
    var result = fields.Select(_ => new List<DiagnosticSample>()).ToArray();
    var instances = new HashSet<string>();
    int scanned = 0;
    foreach (var row in log.GetEnumeratorType(type)) {
      if ((scanned++ & 255) == 0) { ct.ThrowIfCancellationRequested(); }
      double time = row["SampleUS"] != null ? Number(row["SampleUS"]) / 1e6 : row.timems / 1000;
      if (time < start || time > end || (instance != null && Instance(log, row) != instance)) { continue; }
      instances.Add(Instance(log, row));
      if (instances.Count > 1) { throw new ArgumentException("Multiple sensor instances. Specify instance explicitly."); }
      double[] values = fields.Select(f => Number(row[f])).ToArray();
      if (values.Any(v => !double.IsFinite(v))) { continue; }
      if (result[0].Count >= 32768) { throw new ArgumentException("Window exceeds 32768 samples. Select a shorter time window; samples are never silently decimated for FFT."); }
      for (int i = 0; i < fields.Length; i++) {
        result[i].Add(new DiagnosticSample(time, values[i] * Scale(log, type, fields[i])));
      }
    }
    return result;
  }

  internal static string Instance(DFLogBuffer log, DFLog.DFItem row) {
    int index = log.getInstanceIndex(row.msgtype);
    if (index > 0 && index < row.items.Length) { return row.items[index].Trim(); }
    if (log.FMTU.ContainsKey(log.dflog.logformat[row.msgtype].Id)) { return ""; }
    // Never interpret PID*.I (the integral term) as an instance. Only known sensor
    // schemas have a fallback when old firmware omitted FMTU instance annotations.
    if (row.msgtype is "IMU" or "ACC" or "GYR" or "MAG" or "BARO" or "BAT" or "GPS" or "GPA" or "VIBE") {
      foreach (string field in new[] { "I", "IMU", "instance" }) {
        string? value = row[field];
        if (value != null) { return value.Trim(); }
      }
    }
    return "";
  }

  internal static double Scale(DFLogBuffer log, string type, string field) {
    double multiplier = log.GetUnit(type, field).Item2;
    // Upstream returns zero for unknown '-' multipliers. That means unspecified,
    // not that all measurements should become zero.
    return double.IsFinite(multiplier) && multiplier > 0 ? multiplier : 1;
  }

  internal static double Number(string? value) => double.TryParse(value,
      NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : double.NaN;

  public void Dispose() {
    lock (_sync) { _disposed = true; }
    // Read owns both the reader and this gate. Shutdown can wait asynchronously on a worker.
    _readGate.Wait();
    try { _reader?.Dispose(); _reader = null; } finally { _readGate.Release(); }
  }
}
