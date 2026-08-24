using System.Reflection;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.Tests;

public sealed class PrearmFailureTrackerTests {
  [Fact]
  public void Ignores_stale_messages_and_returns_latest_failure_since_last_healthy_state() {
    var tracker = new PrearmFailureTracker();
    DateTime now = DateTime.UtcNow;
    var messages = new List<(DateTime time, string message)> {
      (now.AddMinutes(-2), "PreArm: stale GPS failure"),
    };

    Assert.Null(tracker.Update(healthy: false, enabled: true, present: true, messages, now));
    messages.Add((now.AddSeconds(1), "PreArm: Compass not calibrated"));
    messages.Add((now.AddSeconds(2), "unrelated status"));
    messages.Add((now.AddSeconds(3), "PREARM: RC not found"));

    Assert.Equal("PREARM: RC not found", tracker.Update(
        healthy: false, enabled: true, present: true, messages, now.AddSeconds(4)));
  }

  [Fact]
  public void Healthy_state_resets_the_failure_window() {
    var tracker = new PrearmFailureTracker();
    DateTime now = DateTime.UtcNow;
    var messages = new List<(DateTime time, string message)>();
    tracker.Update(healthy: true, enabled: true, present: true, messages, now);
    messages.Add((now.AddSeconds(1), "PreArm: first"));
    Assert.Equal("PreArm: first", tracker.Update(
        healthy: false, enabled: true, present: true, messages, now.AddSeconds(2)));

    tracker.Update(healthy: true, enabled: true, present: true, messages, now.AddSeconds(3));
    Assert.Null(tracker.Update(
        healthy: false, enabled: true, present: true, messages, now.AddSeconds(4)));
  }

  [Fact]
  public void Repeated_high_priority_message_refreshes_its_display_timeout() {
    var state = new CurrentState();
    state.messageHigh = "Bad GPS Health";
    FieldInfo timestamp = typeof(CurrentState).GetField(
        "_messageHighTime", BindingFlags.Instance | BindingFlags.NonPublic)!;
    timestamp.SetValue(state, DateTime.MinValue);
    Assert.Equal("", state.messageHigh);

    state.messageHigh = "Bad GPS Health";

    Assert.Equal("Bad GPS Health", state.messageHigh);
  }
}
