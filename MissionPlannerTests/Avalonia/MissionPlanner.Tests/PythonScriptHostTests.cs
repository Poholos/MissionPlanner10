using System.Collections.Concurrent;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class PythonScriptHostTests {
  [Fact]
  public async Task ExecutesPythonAndExposesOfficialScriptGlobals() {
    var port = new MAVLinkInterface();
    var flightData = new object();
    var flightPlanner = new object();
    using var host = new PythonScriptHost(
        () => port,
        () => new[] { port },
        () => flightData,
        () => flightPlanner);
    var output = new ConcurrentQueue<string>();
    host.Output += output.Enqueue;

    await host.RunAsync(
        "import json\n" +
        "print('python-parity')\n" +
        "print(json.dumps({'value': 7}, sort_keys=True))\n" +
        "print(MAV is not None and cs is not None)\n" +
        "print(FlightData is not None and FlightPlanner is not None)\n" +
        "print(Script is mavutil)\n" +
        "print(len(Ports) >= 1)\n");

    string text = string.Concat(output);
    Assert.True(text.Contains("python-parity", StringComparison.Ordinal), text);
    Assert.Contains("{\"value\": 7}", text, StringComparison.Ordinal);
    Assert.Equal(4, text.Split('\n').Count(line => line.Trim() == "True"));
    Assert.Contains("Python script finished.", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AbortStopsOrdinaryPythonLoopThroughTraceHook() {
    using var host = new PythonScriptHost();
    var output = new ConcurrentQueue<string>();
    host.Output += output.Enqueue;

    Task run = host.RunAsync("print('loop-started')\nwhile True:\n    pass\n");
    Assert.True(SpinWait.SpinUntil(
        () => string.Concat(output).Contains("loop-started", StringComparison.Ordinal),
        TimeSpan.FromSeconds(2)));
    host.Abort();
    await run.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(host.IsRunning);
    Assert.Contains("Python script aborted.", string.Concat(output), StringComparison.Ordinal);
  }

  [Fact]
  public async Task ReportsPythonTracebackWithoutThrowingAcrossUiBoundary() {
    using var host = new PythonScriptHost();
    var output = new ConcurrentQueue<string>();
    host.Output += output.Enqueue;

    await host.RunAsync("raise ValueError('broken-script')\n");

    string text = string.Concat(output);
    Assert.Contains("ValueError", text, StringComparison.Ordinal);
    Assert.Contains("broken-script", text, StringComparison.Ordinal);
  }
}
