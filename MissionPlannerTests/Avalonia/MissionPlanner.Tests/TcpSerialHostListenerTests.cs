using System.Net;
using System.Net.Sockets;
using MissionPlanner.Comms;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class TcpSerialHostListenerTests {
  [Fact]
  public async Task New_client_replaces_and_closes_the_previous_socket() {
    var serial = new TcpSerial();
    int callbacks = 0;
    using var host = new TcpSerialHostListener(
        IPAddress.Loopback, 0, serial, (_, _) => Interlocked.Increment(ref callbacks));
    using var first = new TcpClient();
    await first.ConnectAsync(IPAddress.Loopback, host.BoundPort);
    await WaitUntilAsync(() => serial.IsOpen && Volatile.Read(ref callbacks) == 1);
    TcpClient firstServer = serial.client;

    using var second = new TcpClient();
    await second.ConnectAsync(IPAddress.Loopback, host.BoundPort);
    await WaitUntilAsync(() => !ReferenceEquals(serial.client, firstServer)
        && Volatile.Read(ref callbacks) == 2);

    await Assert.ThrowsAnyAsync<Exception>(async () =>
        await firstServer.GetStream().WriteAsync(new byte[] { 1 }));
    Assert.True(serial.IsOpen);
    host.Dispose();
    serial.Dispose();
  }

  [Fact]
  public async Task Dispose_stops_a_pending_accept_without_faulting() {
    var serial = new TcpSerial();
    var host = new TcpSerialHostListener(IPAddress.Loopback, 0, serial);

    host.Dispose();

    await host.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(host.Completion.IsCompletedSuccessfully);
    serial.Dispose();
  }

  private static async Task WaitUntilAsync(Func<bool> predicate) {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while (!predicate()) {
      await Task.Delay(10, timeout.Token);
    }
  }
}
