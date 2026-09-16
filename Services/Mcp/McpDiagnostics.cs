using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace MissionPlanner.Services.Mcp;

internal sealed record DiagnosticSample(double TimeSeconds, double Value);
internal sealed record SpectrumResult(double SampleRateHz, int WindowSamples, int Windows,
    double[] FrequencyHz, double[] PowerDensity, string Method);
internal sealed record ResponseResult(int Samples, double SampleRateHz, double RmsError,
    double MeanError, double? EstimatedLagSeconds, double? Correlation, string Interpretation);

/// <summary>Bounded numerical diagnostics. No automatic tuning or inferred airframe constants.</summary>
internal static class McpDiagnostics {
  internal static double ValidateSampling(IReadOnlyList<DiagnosticSample> samples) {
    if (samples.Count < 32) {
      throw new ArgumentException("At least 32 samples from one sensor instance are required.");
    }
    double[] steps = samples.Zip(samples.Skip(1), (a, b) => b.TimeSeconds - a.TimeSeconds).ToArray();
    double dt = steps.Order().ElementAt(steps.Length / 2);
    if (!double.IsFinite(dt) || dt <= 0 || samples.Any(s => !double.IsFinite(s.Value))
        || steps.Any(s => !double.IsFinite(s) || s <= 0 || Math.Abs(s - dt) > dt * 0.1)) {
      throw new ArgumentException("Samples must be monotonic and regularly spaced (10% tolerance). "
          + "Select one instance and a shorter continuous window; do not FFT gaps or mixed sensors.");
    }
    return 1 / dt;
  }

  internal static SpectrumResult Spectrum(IReadOnlyList<DiagnosticSample> samples, int window) {
    if (window is < 32 or > 4096 || (window & (window - 1)) != 0 || samples.Count < window) {
      throw new ArgumentException("FFT window must be a power of two from 32 to 4096 and fit the samples.");
    }
    double rate = ValidateSampling(samples);
    var power = new double[window / 2 + 1];
    double[] taper = Enumerable.Range(0, window)
        .Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (window - 1))).ToArray();
    double scale = rate * taper.Sum(x => x * x);
    int windows = 0;
    for (int start = 0; start + window <= samples.Count; start += window / 2) {
      double mean = 0;
      for (int i = 0; i < window; i++) { mean += samples[start + i].Value / window; }
      var data = new Complex[window];
      for (int i = 0; i < window; i++) { data[i] = (samples[start + i].Value - mean) * taper[i]; }
      Transform(data);
      for (int k = 0; k < power.Length; k++) {
        power[k] += data[k].Magnitude * data[k].Magnitude / scale * (k == 0 || k == window / 2 ? 1 : 2);
      }
      windows++;
    }
    return new SpectrumResult(rate, window, windows,
        Enumerable.Range(0, power.Length).Select(i => i * rate / window).ToArray(),
        power.Select(p => p / windows).ToArray(),
        "Welch one-sided PSD, mean removed per window, Hann window, 50% overlap; units are field-unit squared/Hz. "
        + "Peak frequency alone does not establish a notch setting or a mechanical cause.");
  }

  private static void Transform(Complex[] data) {
    int n = data.Length;
    for (int i = 1, j = 0; i < n; i++) {
      int bit = n >> 1;
      for (; (j & bit) != 0; bit >>= 1) { j ^= bit; }
      j ^= bit;
      if (i < j) { (data[i], data[j]) = (data[j], data[i]); }
    }
    for (int size = 2; size <= n; size <<= 1) {
      Complex step = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
      for (int offset = 0; offset < n; offset += size) {
        Complex factor = Complex.One;
        for (int j = 0; j < size / 2; j++) {
          Complex even = data[offset + j];
          Complex odd = data[offset + j + size / 2] * factor;
          data[offset + j] = even + odd;
          data[offset + j + size / 2] = even - odd;
          factor *= step;
        }
      }
    }
  }

  internal static ResponseResult Response(IReadOnlyList<DiagnosticSample> target,
      IReadOnlyList<DiagnosticSample> actual, double maxLagSeconds) {
    double rate = ValidateSampling(target);
    _ = ValidateSampling(actual);
    if (target.Count != actual.Count || target.Where((t, i) => t.TimeSeconds != actual[i].TimeSeconds).Any()
        || !double.IsFinite(maxLagSeconds) || maxLagSeconds is <= 0 or > 2) {
      throw new ArgumentException("Response analysis requires paired timestamps and a maximum lag in (0, 2] seconds.");
    }
    int maxLag = Math.Min((int)Math.Ceiling(rate * maxLagSeconds), Math.Min(1000, target.Count / 4));
    double best = double.NegativeInfinity;
    int bestLag = 0;
    for (int lag = 0; lag <= maxLag; lag++) {
      int count = target.Count - maxLag;
      double xmean = 0, ymean = 0;
      for (int i = 0; i < count; i++) { xmean += target[i].Value / count; ymean += actual[i + lag].Value / count; }
      double xx = 0, yy = 0, xy = 0;
      for (int i = 0; i < count; i++) {
        double x = target[i].Value - xmean, y = actual[i + lag].Value - ymean;
        xx += x * x; yy += y * y; xy += x * y;
      }
      double corr = xx > 1e-12 && yy > 1e-12 ? xy / Math.Sqrt(xx * yy) : double.NaN;
      if (corr > best) { best = corr; bestLag = lag; }
    }
    double[] errors = target.Select((t, i) => actual[i].Value - t.Value).ToArray();
    bool useful = double.IsFinite(best) && best >= 0.5 && bestLag < maxLag;
    return new ResponseResult(target.Count, rate, Math.Sqrt(errors.Average(e => e * e)), errors.Average(),
        useful ? bestLag / rate : null, double.IsFinite(best) ? best : null,
        "Positive lag means actual follows target. Correlation is observational, not causal or a settling-time measurement. "
        + "Constant input, weak correlation or a peak at the search boundary gives no lag estimate. "
        + "Use identical units; angular wrap, saturation, flight mode and excitation must be inspected before tuning.");
  }
}
