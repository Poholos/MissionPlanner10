using System.Collections.Concurrent;
using MissionPlanner.Comms;

namespace MissionPlanner.Tests;

/// <summary>
/// Protocol-level tests for MAVLinkInterface.GetLog against a simulated
/// vehicle on an in-memory link: ordering, loss recovery, reordered short
/// packets, cancellation and timeouts. Ported from the upstream fork's
/// log-download suite (userepo/MissionPlanner fix/log-download-mavlink).
/// </summary>
public class GetLogProtocolTests {
  private const byte VehicleSysid = 1;
  private const byte VehicleCompid = 1;
  private const ushort LogId = 3;
  private const int BlockSize = 90;

  /// <summary>
  /// Simulated vehicle end of the MAVLink log download protocol, connected
  /// to a MAVLinkInterface through an in-memory CommsInjection link.
  /// </summary>
  private sealed class FakeLogVehicle : IDisposable {
    public readonly CommsInjection Link = new();
    public readonly MAVLinkInterface Mav = new();
    public readonly List<MAVLink.mavlink_log_request_data_t> Requests = new();
    public Action<MAVLink.mavlink_log_request_data_t>? OnRequest;
    private int _endRequests;

    public int EndRequests => Volatile.Read(ref _endRequests);

    private readonly MAVLink.MavlinkParse _parse = new();
    private readonly byte[] _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    public FakeLogVehicle(byte[] log) {
      _log = log;

      Link.WriteCallback += (sender, outbytes) => {
        MAVLink.MAVLinkMessage msg;
        try {
          msg = new MAVLink.MAVLinkMessage(outbytes.ToArray());
        } catch {
          return;
        }

        if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_END) {
          Interlocked.Increment(ref _endRequests);
          return;
        }

        if (msg.msgid != (uint)MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_DATA) {
          return;
        }

        var req = msg.ToStructure<MAVLink.mavlink_log_request_data_t>();
        if (req.id != LogId) {
          return;
        }

        lock (Requests) {
          Requests.Add(req);
        }
        OnRequest?.Invoke(req);
      };

      Mav.BaseStream = Link;

      // real-time delays scaled down so the suite stays fast
      Mav.LogRetryDelayMs = 100;
      Mav.LogRepairDelayMs = 50;

      // GetLog consumes packets via OnPacketReceived, which fires from the
      // receive loop - pump it the same way the serial reader does
      _pump = Task.Run(async () => {
        while (!_stop.IsCancellationRequested) {
          try {
            await Mav.readPacketAsync().ConfigureAwait(false);
          } catch {
            try {
              await Task.Delay(1, _stop.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
            }
          }
        }
      });
    }

    public void SendBlock(uint ofs, int count) {
      var payload = new byte[BlockSize];
      Array.Copy(_log, ofs, payload, 0, count);
      Send(MAVLink.MAVLINK_MSG_ID.LOG_DATA,
          new MAVLink.mavlink_log_data_t(ofs, LogId, (byte)count, payload));
    }

    public void SendEndMarker(uint ofs) {
      Send(MAVLink.MAVLINK_MSG_ID.LOG_DATA,
          new MAVLink.mavlink_log_data_t(ofs, LogId, 0, new byte[BlockSize]));
    }

    public void Send(MAVLink.MAVLINK_MSG_ID msgid, object indata) {
      Link.AppendBuffer(_parse.GenerateMAVLinkPacket20(msgid, indata, false, VehicleSysid,
          VehicleCompid));
    }

    /// <summary>
    /// Serve a LOG_REQUEST_DATA the way ArduPilot does: 90 byte blocks with
    /// a short block at end of log, or a zero count marker when the log ends
    /// on an exact block boundary. skipBlocks are dropped, but only the
    /// first time each is requested.
    /// </summary>
    public void Serve(MAVLink.mavlink_log_request_data_t req, HashSet<uint>? skipBlocks = null) {
      ulong end = Math.Min((ulong)req.ofs + req.count, (ulong)_log.Length);
      for (ulong ofs = req.ofs; ofs < end; ofs += BlockSize) {
        uint block = (uint)(ofs / BlockSize);
        if (skipBlocks != null && skipBlocks.Remove(block)) {
          continue;
        }
        SendBlock((uint)ofs, (int)Math.Min(BlockSize, end - ofs));
      }

      if (end == (ulong)_log.Length && (ulong)req.ofs + req.count > end
          && _log.Length % BlockSize == 0) {
        SendEndMarker((uint)end);
      }
    }

    public void Dispose() {
      _stop.Cancel();
      _stop.Dispose();
      Link.Close();
      try {
        _pump.Wait(2000);
      } catch {
      }
    }
  }

  private static byte[] MakeLog(int length) {
    var data = new byte[length];
    new Random(42).NextBytes(data);
    return data;
  }

  private static async Task<byte[]> Download(FakeLogVehicle vehicle,
      CancellationToken cancel = default) {
    string file = await vehicle.Mav.GetLog(VehicleSysid, VehicleCompid, LogId, cancel);
    try {
      return await File.ReadAllBytesAsync(file, CancellationToken.None);
    } finally {
      try {
        File.Delete(file);
      } catch {
      }
    }
  }

  [Fact]
  public async Task Complete_log_downloads_in_order_and_ends_the_session() {
    byte[] log = MakeLog(1234);
    using var vehicle = new FakeLogVehicle(log);
    vehicle.OnRequest = req => vehicle.Serve(req);

    byte[] result = await Download(vehicle);

    Assert.Equal(log, result);
    Assert.True(vehicle.EndRequests > 0, "LOG_REQUEST_END not sent after a completed download");
  }

  [Fact]
  public async Task Log_sized_at_an_exact_block_multiple_completes() {
    byte[] log = MakeLog(900);
    using var vehicle = new FakeLogVehicle(log);
    vehicle.OnRequest = req => vehicle.Serve(req);

    byte[] result = await Download(vehicle);

    Assert.Equal(log, result);
  }

  [Fact]
  public async Task Dropped_blocks_are_refetched() {
    byte[] log = MakeLog(1000);
    using var vehicle = new FakeLogVehicle(log);
    var drop = new HashSet<uint> { 3, 7 };
    vehicle.OnRequest = req => vehicle.Serve(req, drop);

    byte[] result = await Download(vehicle);

    Assert.Equal(log, result);
    lock (vehicle.Requests) {
      Assert.True(vehicle.Requests.Skip(1).Any(),
          "dropped blocks were never re-requested");
    }
  }

  [Fact]
  public async Task Stray_short_retransmit_does_not_truncate_the_download() {
    byte[] log = MakeLog(1234);
    using var vehicle = new FakeLogVehicle(log);
    bool first = true;
    vehicle.OnRequest = req => {
      if (!first) {
        vehicle.Serve(req);
        return;
      }

      first = false;
      ulong end = Math.Min((ulong)req.ofs + req.count, (ulong)log.Length);
      for (ulong ofs = req.ofs; ofs < end; ofs += BlockSize) {
        // a stale short retransmit mid stream must not become the log end
        if (ofs == 8 * BlockSize) {
          vehicle.SendBlock(3 * BlockSize, 40);
        }
        vehicle.SendBlock((uint)ofs, (int)Math.Min(BlockSize, end - ofs));
      }
    };

    byte[] result = await Download(vehicle);

    Assert.True(log.Length == result.Length,
        $"stray short packet truncated the download to {result.Length} of {log.Length} bytes");
    Assert.Equal(log, result);
  }

  [Fact]
  public async Task Scattered_gaps_recover_by_chained_repair_requests() {
    byte[] log = MakeLog(300 * BlockSize);
    using var vehicle = new FakeLogVehicle(log);
    // every 6th block dropped on first delivery: 50 scattered single-block gaps
    var drop = new HashSet<uint>(Enumerable.Range(0, 50).Select(i => (uint)(i * 6)));
    vehicle.OnRequest = req => vehicle.Serve(req, drop);

    // at this delay, waiting out one silence window per gap needs 25+ seconds;
    // chaining the next repair request on completion finishes in about a second
    vehicle.Mav.LogRepairDelayMs = 500;

    Task<byte[]> download = Download(vehicle);
    Task finished = await Task.WhenAny(download, Task.Delay(10_000));

    Assert.True(finished == download,
        "repair phase crawled - gaps must be re-requested by chaining, not one per silence window");
    Assert.Equal(log, await download);
  }

  [Fact]
  public async Task Oversized_count_is_ignored() {
    byte[] log = MakeLog(1234);
    using var vehicle = new FakeLogVehicle(log);
    vehicle.OnRequest = req => {
      // count larger than the 90-byte payload must be skipped, not written
      vehicle.Send(MAVLink.MAVLINK_MSG_ID.LOG_DATA,
          new MAVLink.mavlink_log_data_t(0, LogId, 200, new byte[BlockSize]));
      vehicle.Serve(req);
    };

    byte[] result = await Download(vehicle);

    Assert.Equal(log, result);
  }

  [Fact]
  public async Task Empty_log_produces_an_empty_file() {
    using var vehicle = new FakeLogVehicle(Array.Empty<byte>());
    vehicle.OnRequest = _ => vehicle.SendEndMarker(0);

    byte[] result = await Download(vehicle);

    Assert.Empty(result);
  }

  [Fact]
  public async Task Cancel_stops_the_download_and_ends_the_session() {
    byte[] log = MakeLog(100 * BlockSize);
    using var vehicle = new FakeLogVehicle(log);
    // stream a few blocks, then go silent so the download hangs mid way
    vehicle.OnRequest = _ => {
      for (uint block = 0; block < 5; block++) {
        vehicle.SendBlock(block * BlockSize, BlockSize);
      }
    };

    using var cts = new CancellationTokenSource(300);
    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => vehicle.Mav.GetLog(VehicleSysid, VehicleCompid, LogId, cts.Token));

    Assert.True(vehicle.EndRequests > 0,
        "LOG_REQUEST_END not sent - the vehicle would keep streaming LOG_DATA");
  }

  [Fact]
  public async Task Silent_vehicle_times_out() {
    using var vehicle = new FakeLogVehicle(MakeLog(10));

    // no OnRequest wired - total silence; LogRetryDelayMs with 10 retries
    await Assert.ThrowsAsync<TimeoutException>(
        () => vehicle.Mav.GetLog(VehicleSysid, VehicleCompid, LogId));
  }
}
