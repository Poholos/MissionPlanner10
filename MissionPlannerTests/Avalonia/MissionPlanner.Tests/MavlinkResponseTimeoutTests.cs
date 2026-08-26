using MissionPlanner.Comms;

namespace MissionPlanner.Tests;

public sealed class MavlinkResponseTimeoutTests {
  [Fact]
  public async Task Timed_arm_stops_waiting_and_releases_exclusive_port_ownership() {
    using var stream = new CommsInjection();
    using var link = new MAVLinkInterface { BaseStream = stream };
    var elapsed = System.Diagnostics.Stopwatch.StartNew();

    await Assert.ThrowsAsync<TimeoutException>(() => link.doARMAsync(
        1, 1, true, false, TimeSpan.FromMilliseconds(25)));

    Assert.False(link.giveComport);
    Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2));
  }

  [Fact]
  public async Task Timed_mavlink_operations_reject_non_positive_limits() {
    using var link = new MAVLinkInterface();

    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => link.doARMAsync(
        1, 1, true, false, TimeSpan.Zero));
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => link.getWPAsync(
        1, 1, 0, MAVLink.MAV_MISSION_TYPE.MISSION, TimeSpan.Zero));
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => link.setWPCurrentAsync(
        1, 1, 0, TimeSpan.Zero));
  }
}
