using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.Comms;

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
}
