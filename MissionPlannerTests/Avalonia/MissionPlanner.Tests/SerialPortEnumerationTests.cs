using System.Diagnostics;
using MissionPlanner.Comms;

namespace MissionPlanner.Tests;

public sealed class SerialPortEnumerationTests {
  [Fact]
  public void Successful_probe_returns_an_independent_snapshot() {
    string[] source = ["COM1", "COM2"];
    var enumerator = new BoundedPortNameEnumerator(() => source);

    PortNameEnumerationResult result = enumerator.TryEnumerate(1000);
    source[0] = "changed";

    Assert.True(result.Succeeded);
    Assert.False(result.TimedOut);
    Assert.Null(result.Error);
    Assert.Equal(["COM1", "COM2"], result.Ports);
  }

  [Fact]
  public void Provider_failure_is_contained_and_a_later_probe_can_retry() {
    int calls = 0;
    var enumerator = new BoundedPortNameEnumerator(() => {
      if (Interlocked.Increment(ref calls) == 1) {
        throw new InvalidOperationException("driver failed");
      }
      return ["COM7"];
    });

    PortNameEnumerationResult failed = enumerator.TryEnumerate(1000);
    PortNameEnumerationResult recovered = enumerator.TryEnumerate(1000);

    Assert.False(failed.Succeeded);
    Assert.False(failed.TimedOut);
    Assert.IsType<InvalidOperationException>(failed.Error);
    Assert.True(recovered.Succeeded);
    Assert.Equal(["COM7"], recovered.Ports);
    Assert.Equal(2, calls);
  }

  [Fact]
  public void Timed_out_probe_is_single_flight_and_recovers_when_driver_returns() {
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    int calls = 0;
    var enumerator = new BoundedPortNameEnumerator(() => {
      Interlocked.Increment(ref calls);
      entered.Set();
      release.Wait();
      return ["COM9"];
    });

    PortNameEnumerationResult first = enumerator.TryEnumerate(25);
    Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
    var stopwatch = Stopwatch.StartNew();
    PortNameEnumerationResult second = enumerator.TryEnumerate(1000);
    stopwatch.Stop();

    Assert.True(first.TimedOut);
    Assert.True(second.TimedOut);
    Assert.Equal(1, Volatile.Read(ref calls));
    Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
        $"Repeated timed-out probe took {stopwatch.Elapsed}.");

    release.Set();
    PortNameEnumerationResult recovered = default!;
    Assert.True(SpinWait.SpinUntil(() => {
      recovered = enumerator.TryEnumerate(1000);
      return recovered.Succeeded;
    }, TimeSpan.FromSeconds(2)));
    Assert.Equal(["COM9"], recovered.Ports);
    Assert.Equal(1, Volatile.Read(ref calls));
  }
}
