using MissionPlanner.ArduPilot.Mavlink;

namespace MissionPlanner.Tests;

public sealed class MessageRateManagerTests {
  [Theory]
  [InlineData(2, 500_000)]
  [InlineData(3, 333_333)]
  [InlineData(2_000_000, 1)]
  [InlineData(0.000001, int.MaxValue)]
  public void Hertz_conversion_is_bounded_and_rounded(
      double hertz, int expectedMicroseconds) {
    Assert.Equal(expectedMicroseconds,
        MessageRateManager.HertzToIntervalMicroseconds(hertz));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  public void Invalid_rates_are_rejected(double hertz) {
    using var manager = new MessageRateManager(
        new FakeTransport(), TimeSpan.FromHours(1));

    Assert.Throws<ArgumentOutOfRangeException>(() =>
        manager.Subscribe(1, 1, MAVLink.MAVLINK_MSG_ID.CAMERA_FOV_STATUS, hertz));
  }

  [Fact]
  public async Task Fastest_lease_wins_and_last_release_restores_default_once() {
    var transport = new FakeTransport();
    using var manager = new MessageRateManager(transport, TimeSpan.FromHours(1));

    MessageRateLease slow = manager.Subscribe(
        7, 100, MAVLink.MAVLINK_MSG_ID.CAMERA_FOV_STATUS, 2, "slow");
    MessageRateLease fast = manager.Subscribe(
        7, 100, MAVLink.MAVLINK_MSG_ID.CAMERA_FOV_STATUS, 5, "fast");

    Assert.Equal([500_000, 200_000],
        transport.SetRequests.Select(request => request.Interval).ToArray());
    Assert.All(transport.SetRequests, request => Assert.False(request.RequireAck));

    fast.Dispose();
    Assert.Equal(500_000, transport.SetRequests.Last().Interval);

    slow.Dispose();
    await WaitUntil(() => transport.SetRequests.Any(request =>
        request.Interval == 0 && request.RequireAck));
    int restoreCount = transport.SetRequests.Count(request =>
        request.Interval == 0 && request.RequireAck);

    slow.Dispose();
    await Task.Delay(25);
    Assert.Equal(restoreCount, transport.SetRequests.Count(request =>
        request.Interval == 0 && request.RequireAck));
  }

  [Fact]
  public void Disposal_is_idempotent_and_rejects_new_leases() {
    var manager = new MessageRateManager(
        new FakeTransport(), TimeSpan.FromHours(1));

    manager.Dispose();
    manager.Dispose();

    Assert.Throws<ObjectDisposedException>(() => manager.Subscribe(
        1, 1, MAVLink.MAVLINK_MSG_ID.CAMERA_SETTINGS, 1));
  }

  [Fact]
  public void Camera_streaming_leases_follow_reported_capabilities() {
    uint flags = (uint)(MAVLink.CAMERA_CAP_FLAGS.HAS_BASIC_ZOOM
        | MAVLink.CAMERA_CAP_FLAGS.CAPTURE_VIDEO);

    Assert.Equal([
      MAVLink.MAVLINK_MSG_ID.CAMERA_FOV_STATUS,
      MAVLink.MAVLINK_MSG_ID.CAMERA_SETTINGS,
      MAVLink.MAVLINK_MSG_ID.CAMERA_CAPTURE_STATUS,
    ], CameraProtocol.StreamingMessageIds(flags));
    Assert.Equal([MAVLink.MAVLINK_MSG_ID.CAMERA_FOV_STATUS],
        CameraProtocol.StreamingMessageIds(0));
  }

  [Theory]
  [InlineData(0, true)]
  [InlineData((uint)MAVLink.GIMBAL_DEVICE_FLAGS.YAW_LOCK, false)]
  [InlineData((uint)MAVLink.GIMBAL_DEVICE_FLAGS.YAW_IN_EARTH_FRAME, false)]
  [InlineData((uint)MAVLink.GIMBAL_DEVICE_FLAGS.YAW_IN_VEHICLE_FRAME, true)]
  public void Gimbal_yaw_frame_comes_from_device_attitude_flags(
      uint flags, bool expectedVehicleFrame) {
    Assert.Equal(expectedVehicleFrame,
        GimbalManagerProtocol.YawIsInVehicleFrame(flags));
  }

  private static async Task WaitUntil(Func<bool> condition) {
    DateTime deadline = DateTime.UtcNow.AddSeconds(2);
    while (!condition()) {
      if (DateTime.UtcNow >= deadline) {
        throw new TimeoutException("Expected asynchronous rate-manager operation did not complete.");
      }
      await Task.Delay(10);
    }
  }

  private sealed class FakeTransport : IMessageRateTransport {
    private readonly object _gate = new();
    private readonly List<SetRequest> _setRequests = [];
    private int _nextSubscription;

    internal readonly record struct SetRequest(
        uint MessageId, byte SystemId, byte ComponentId,
        int Interval, bool RequireAck);

    internal IReadOnlyList<SetRequest> SetRequests {
      get {
        lock (_gate) {
          return _setRequests.ToArray();
        }
      }
    }

    public bool IsCommandChannelBusy { get; set; }

    public int Subscribe(MAVLink.MAVLINK_MSG_ID messageId,
        Func<MAVLink.MAVLinkMessage, bool> handler, byte sysid, byte compid) =>
        Interlocked.Increment(ref _nextSubscription);

    public void Unsubscribe(int subscriptionId) {
    }

    public bool HasEverReceived(uint messageId, byte sysid, byte compid) => false;

    public int GetLinkQualityPercent(byte sysid, byte compid) => 100;

    public Task<bool> SetIntervalAsync(uint messageId, byte sysid, byte compid,
        int intervalMicroseconds, bool requireAcknowledgement) {
      lock (_gate) {
        _setRequests.Add(new SetRequest(
            messageId, sysid, compid, intervalMicroseconds, requireAcknowledgement));
      }
      return Task.FromResult(true);
    }

    public Task<bool> GetIntervalAsync(uint messageId, byte sysid, byte compid) =>
        Task.FromResult(true);
  }
}
