using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class ProximityConcurrencyTests {
  [Fact]
  public void Raw_samples_are_detached_snapshots() {
    var state = new Proximity.directionState();
    state.Add(1, MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_NONE,
        10, DateTime.Now, age: 60);

    List<Proximity.directionState.data> snapshot = state.GetRaw();
    state.Add(2, MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_45,
        20, DateTime.Now, age: 60);

    Assert.Single(snapshot);
    Assert.Equal(2, state.GetRaw().Count);
  }

  [Fact]
  public void Readers_and_packet_updates_can_run_concurrently() {
    var state = new Proximity.directionState();
    Exception? failure = null;

    Parallel.For(0, 5_000, (index, loop) => {
      try {
        if ((index & 1) == 0) {
          state.Add((uint)(index % 32), index % 360, 5, index,
              DateTime.Now, age: 60);
        } else {
          _ = state.GetRaw().Sum(sample => sample.Distance);
          _ = state.GetClosest();
          _ = state.GetWarnings(index);
        }
      } catch (Exception ex) {
        Interlocked.CompareExchange(ref failure, ex, null);
        loop.Stop();
      }
    });

    Assert.Null(failure);
  }

  [Fact]
  public void Expired_samples_are_removed_from_every_snapshot() {
    var state = new Proximity.directionState();
    state.Add(1, MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_NONE,
        10, DateTime.Now.AddMinutes(-1), age: 1);

    Assert.Empty(state.GetRaw());
    Assert.Equal(double.MaxValue, state.GetClosest());
  }
}
