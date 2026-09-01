using System.Net;
using System.Net.Sockets;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class StartupUdpListenerTests {
  [Fact]
  public void Defaults_restore_both_upstream_mavlink_udp_ports() {
    StartupUdpListenerOptions options = StartupUdpListenerOptions.Default;

    Assert.True(options.Enabled);
    Assert.Equal(new[] { 14550, 14551 }, options.Ports);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  [InlineData(65536)]
  public void Invalid_saved_port_falls_back_to_the_documented_default(int value) {
    Assert.Equal(14550,
        StartupUdpListenerOptions.NormalizePort(value, 14550));
  }

  [Fact]
  public void Duplicate_ports_are_opened_once_and_disabled_options_open_nothing() {
    using var manager = new MavLinkConnectionManager(new MAVLinkInterface());
    int port = ReserveUdpPort();

    StartupUdpListenerStartResult duplicate = StartupUdpListenerService.Start(
        new StartupUdpListenerOptions(true, port, port), manager);
    StartupUdpListenerStartResult disabled = StartupUdpListenerService.Start(
        new StartupUdpListenerOptions(false, 14550, 14551), manager);

    Assert.Single(duplicate.Opened);
    Assert.Equal(new[] { port }, duplicate.RequestedPorts);
    Assert.Empty(duplicate.Failures);
    Assert.Empty(disabled.Opened);
    Assert.Empty(disabled.RequestedPorts);
    Assert.Single(manager.Snapshot(), connection => !connection.IsPrimary);
  }

  [Fact]
  public async Task Both_startup_ports_receive_independent_mavlink_vehicles() {
    int firstPort = ReserveUdpPort();
    int secondPort;
    do {
      secondPort = ReserveUdpPort();
    } while (secondPort == firstPort);

    using var manager = new MavLinkConnectionManager(new MAVLinkInterface());
    StartupUdpListenerStartResult result = StartupUdpListenerService.Start(
        new StartupUdpListenerOptions(true, firstPort, secondPort), manager);

    Assert.Empty(result.Failures);
    Assert.Equal(2, result.Opened.Count);
    Assert.Equal(3, manager.Snapshot().Count);
    Assert.NotSame(result.Opened[0].Link, result.Opened[1].Link);

    using var firstSender = new UdpClient();
    using var secondSender = new UdpClient();
    firstSender.Connect(IPAddress.Loopback, firstPort);
    secondSender.Connect(IPAddress.Loopback, secondPort);
    using var cancellation = new CancellationTokenSource();
    Task firstLoop = SendHeartbeatsAsync(firstSender, 41, cancellation.Token);
    Task secondLoop = SendHeartbeatsAsync(secondSender, 42, cancellation.Token);

    try {
      await WaitUntilAsync(() =>
          HasVehicle(result.Opened[0].Link, 41) &&
          HasVehicle(result.Opened[1].Link, 42), TimeSpan.FromSeconds(4));

      Assert.False(HasVehicle(result.Opened[0].Link, 42));
      Assert.False(HasVehicle(result.Opened[1].Link, 41));
      Assert.All(result.Opened, connection => Assert.True(connection.IsOpen));
    } finally {
      cancellation.Cancel();
      foreach (Task loop in new[] { firstLoop, secondLoop }) {
        try {
          await loop;
        } catch (OperationCanceledException) {
        }
      }
    }
  }

  private static bool HasVehicle(MAVLinkInterface link, byte systemId) =>
      link.MAVlist.ToArray().Any(mav =>
          mav.sysid == systemId && mav.compid == 1 &&
          mav.lastvalidpacket > DateTime.MinValue);

  private static async Task SendHeartbeatsAsync(
      UdpClient sender, byte systemId, CancellationToken cancellationToken) {
    var parser = new MAVLink.MavlinkParse();
    int sequence = 0;
    while (!cancellationToken.IsCancellationRequested) {
      byte[] packet = parser.GenerateMAVLinkPacket20(
          MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
          new MAVLink.mavlink_heartbeat_t(
              0,
              (byte)MAVLink.MAV_TYPE.QUADROTOR,
              (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
              0,
              (byte)MAVLink.MAV_STATE.ACTIVE,
              3),
          false, systemId, 1, sequence++);
      await sender.SendAsync(packet, cancellationToken);
      await Task.Delay(40, cancellationToken);
    }
  }

  private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) {
    DateTime deadline = DateTime.UtcNow + timeout;
    while (!condition()) {
      if (DateTime.UtcNow >= deadline) {
        throw new TimeoutException("Startup UDP listeners did not receive both vehicles.");
      }
      await Task.Delay(20);
    }
  }

  private static int ReserveUdpPort() {
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
  }
}
