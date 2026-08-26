using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.Comms;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

public class UdpSharedListenerTests {
  [Fact]
  public async Task Shared_listeners_both_receive_the_same_broadcast_datagram() {
    using UdpClient first = UdpSerial.CreateSharedListener(0);
    int port = Assert.IsType<IPEndPoint>(first.Client.LocalEndPoint).Port;
    using UdpClient second = UdpSerial.CreateSharedListener(port);
    using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
    byte[] payload = Encoding.ASCII.GetBytes("shared MAVLink broadcast");

    Task<UdpReceiveResult> firstReceive = first.ReceiveAsync()
        .WaitAsync(TimeSpan.FromSeconds(5));
    Task<UdpReceiveResult> secondReceive = second.ReceiveAsync()
        .WaitAsync(TimeSpan.FromSeconds(5));
    await sender.SendAsync(payload, payload.Length,
        new IPEndPoint(IPAddress.Broadcast, port));

    UdpReceiveResult[] received = await Task.WhenAll(firstReceive, secondReceive);
    Assert.All(received, datagram => Assert.Equal(payload, datagram.Buffer));
  }

  [Fact]
  public async Task Cancelled_primary_initialization_retains_the_bound_udp_listener() {
    using UdpClient reserved = UdpSerial.CreateSharedListener(0);
    int port = Assert.IsType<IPEndPoint>(reserved.Client.LocalEndPoint).Port;
    reserved.Close();
    using var listener = new PreconfiguredUdpListener(port.ToString()) {
      KeepSocketOpenOnCancel = true,
    };

    Task opening = Task.Run(listener.Open);
    Assert.True(SpinWait.SpinUntil(
        () => listener.client?.Client?.LocalEndPoint is IPEndPoint,
        TimeSpan.FromSeconds(2)));
    listener.CancelConnect = true;
    await opening.WaitAsync(TimeSpan.FromSeconds(2));

    Assert.True(listener.IsOpen);
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    await sender.SendAsync([0x01], 1, new IPEndPoint(IPAddress.Loopback, port));
    Assert.True(SpinWait.SpinUntil(() => listener.BytesToRead > 0, TimeSpan.FromSeconds(2)));
  }
}
