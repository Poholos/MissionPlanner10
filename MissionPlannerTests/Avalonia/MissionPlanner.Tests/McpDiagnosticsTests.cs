using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpDiagnosticsTests {
  [Fact]
  public void Welch_finds_frequency_and_preserves_integrated_signal_power() {
    var samples = Enumerable.Range(0, 4096)
        .Select(i => new DiagnosticSample(i / 1024d, 2 * Math.Sin(2 * Math.PI * 64 * i / 1024))).ToArray();
    var spectrum = McpDiagnostics.Spectrum(samples, 1024);
    int peak = Array.IndexOf(spectrum.PowerDensity, spectrum.PowerDensity.Max());
    Assert.Equal(64, spectrum.FrequencyHz[peak], 6);
    Assert.InRange(spectrum.PowerDensity.Sum() * 1024 / 1024, 1.99, 2.01);
    Assert.Equal(7, spectrum.Windows);
  }

  [Fact]
  public void Response_measures_known_delay_without_claiming_causality() {
    const double rate = 200;
    double Signal(int i) => Math.Sin(i * 0.07) + Math.Sin(i * 0.151) + 0.3 * Math.Cos(i * 0.031);
    var target = Enumerable.Range(0, 2048).Select(i => new DiagnosticSample(i / rate, Signal(i))).ToArray();
    var actual = Enumerable.Range(0, 2048).Select(i => new DiagnosticSample(i / rate, Signal(i - 12))).ToArray();
    var result = McpDiagnostics.Response(target, actual, 0.2);
    Assert.Equal(0.06, result.EstimatedLagSeconds!.Value, 5);
    Assert.InRange(result.Correlation!.Value, 0.999, 1.001);
    Assert.Contains("not causal", result.Interpretation);
  }

  [Fact]
  public void Constant_input_has_no_identifiable_response_lag() {
    var samples = Enumerable.Range(0, 256).Select(i => new DiagnosticSample(i / 100d, 5)).ToArray();
    var result = McpDiagnostics.Response(samples, samples, 0.2);
    Assert.Null(result.EstimatedLagSeconds);
    Assert.Null(result.Correlation);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(2)]
  [InlineData(16)]
  public void Spectrum_rejects_duplicate_gapped_or_out_of_order_timestamps(int mode) {
    var samples = Enumerable.Range(0, 256).Select(i => new DiagnosticSample(i / 100d, Math.Sin(i))).ToArray();
    samples[100] = samples[100] with { TimeSeconds = samples[99].TimeSeconds + mode / 100d };
    Assert.Throws<ArgumentException>(() => McpDiagnostics.Spectrum(samples, 128));
  }

  [Fact]
  public void Launcher_passes_connection_as_arguments_and_keeps_token_out_of_command_line() {
    var start = McpAgentProcess.BuildStart("/tmp/path with spaces/codex", new Uri("http://127.0.0.1:32123/mcp"),
        "private-test-token", Path.GetTempPath());
    Assert.False(start.UseShellExecute);
    Assert.DoesNotContain("private-test-token", string.Join(" ", start.ArgumentList));
    Assert.Equal("private-test-token", start.Environment["MP_MCP_TOKEN"]);
    Assert.Contains("mcp_servers.missionplanner.url=\"http://127.0.0.1:32123/mcp\"", start.ArgumentList);
    Assert.Contains("read-only", start.ArgumentList);
  }
  [Theory]
  [InlineData("42", "42\n")]
  [InlineData("plain stderr", "plain stderr\n")]
  [InlineData("{\"type\":\"item.completed\",\"item\":{\"text\":\"Analysis result\"}}", "Analysis result\n")]
  [InlineData("{\"type\":\"turn.completed\"}", "Analysis complete.\n")]
  public void Launcher_renders_jsonl_answers_and_tolerates_non_event_output(string line, string expected) =>
      Assert.Equal(expected, McpAgentProcess.FormatLine(line));
}
