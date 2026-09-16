using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services.Mcp;

internal static class McpBatchImu {
  internal static object Spectrum(DFLogBuffer log, int headerLine, string axis, int window, CancellationToken ct) {
    if (headerLine < 0 || headerLine >= log.Count || axis is not ("x" or "y" or "z")) {
      throw new ArgumentException("Select an ISBH headerLine from read_log_records and axis x/y/z.");
    }
    var header = log[(long)headerLine];
    if (header.msgtype != "ISBH") { throw new ArgumentException("headerLine must point to an ISBH record."); }
    int batch = Int(header, "N"), count = Int(header, "smp_cnt");
    double rate = McpLogCatalog.Number(header["smp_rate"]);
    double multiplier = McpLogCatalog.Number(header["mul"]);
    double start = McpLogCatalog.Number(header["SampleUS"]) / 1e6;
    if (count is < 32 or > 32768 || !double.IsFinite(rate) || rate <= 0 || !double.IsFinite(multiplier)
        || multiplier <= 0 || !double.IsFinite(start)) { throw new ArgumentException("Invalid batch sample count, time, rate or multiplier."); }
    var samples = new List<DiagnosticSample>();
    int expectedSequence = 0;
    foreach (var item in log.GetEnumeratorType(new[] { "ISBH", "ISBD" })) {
      ct.ThrowIfCancellationRequested();
      if (item.lineno <= headerLine) { continue; }
      // Never join different batches or silently bridge missing packets.
      if (item.msgtype == "ISBH" && Int(item, "N") == batch) { break; }
      if (item.msgtype != "ISBD" || Int(item, "N") != batch) { continue; }
      if (Int(item, "seqno") != expectedSequence++) { throw new ArgumentException("Incomplete/out-of-order IMU batch; cannot compute a reliable spectrum."); }
      short[] values = DecodeArray(item.GetRaw(axis), item[axis]);
      if (values.Length != 32) { throw new ArgumentException("ISBD packet must have exactly 32 samples per axis."); }
      foreach (short value in values) {
        if (samples.Count >= count) { break; }
        samples.Add(new DiagnosticSample(start + samples.Count / rate, value / multiplier));
      }
      if (samples.Count == count) { break; }
    }
    if (samples.Count != count) { throw new ArgumentException($"Incomplete batch: {samples.Count}/{count} samples."); }
    return new { batch, headerLine, sensorType = Int(header, "type"), instance = Int(header, "instance"), axis,
      startSeconds = start, units = Int(header, "type") == 0 ? "m/s^2" : "rad/s",
      spectrum = McpDiagnostics.Spectrum(samples, window),
      note = "One complete ISBH/ISBD batch; no concatenation across discontinuities. Compare multiple batches and flight conditions." };
  }

  private static int Int(DFLog.DFItem row, string field) => int.Parse(row[field], CultureInfo.InvariantCulture);
  private static short[] DecodeArray(object raw, string text) {
    if (raw is BinaryLog.UnionArray array) { return array.Shorts.ToArray(); }
    if (raw is short[] values) { return values; }
    // Text DataFlash arrays use bracket/comma/space separators depending on exporter.
    string[] parts = text.Trim('[', ']', ' ').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
    return Array.ConvertAll(parts, p => short.Parse(p, CultureInfo.InvariantCulture));
  }
}
