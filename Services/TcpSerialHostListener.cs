using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Comms;

namespace MissionPlanner.Services;

/// <summary>
/// Owns the accept loop for a <see cref="TcpSerial"/> used as a TCP host. Only the newest
/// accepted client remains attached; superseded and late-after-stop sockets are closed promptly.
/// </summary>
internal sealed class TcpSerialHostListener : IDisposable {
  private readonly object _sync = new();
  private readonly TcpSerial _serial;
  private readonly TcpListener _listener;
  private readonly CancellationTokenSource _stop = new();
  private readonly Action<TcpSerialHostListener, string>? _connected;
  private readonly Task _acceptTask;
  private bool _disposed;

  internal TcpSerialHostListener(
      IPAddress address,
      int port,
      TcpSerial serial,
      Action<TcpSerialHostListener, string>? connected = null) {
    ArgumentNullException.ThrowIfNull(address);
    ArgumentNullException.ThrowIfNull(serial);
    _serial = serial;
    _connected = connected;
    _listener = new TcpListener(address, port);
    try {
      _listener.Start(1);
      BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
      _acceptTask = AcceptLoopAsync(_stop.Token);
    } catch {
      _listener.Stop();
      _stop.Dispose();
      throw;
    }
  }

  internal int BoundPort { get; }
  internal Task Completion => _acceptTask;

  private async Task AcceptLoopAsync(CancellationToken cancellationToken) {
    try {
      while (true) {
        TcpClient accepted = await _listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        TcpClient? previous = null;
        string remote = "client";
        bool keep;
        try {
          accepted.NoDelay = true;
          remote = accepted.Client.RemoteEndPoint?.ToString() ?? remote;
          lock (_sync) {
            keep = !_disposed;
            if (keep) {
              previous = _serial.client;
              _serial.client = accepted;
            }
          }
        } catch {
          accepted.Dispose();
          throw;
        }

        if (!keep) {
          accepted.Dispose();
          return;
        }
        previous?.Dispose();
        try {
          _connected?.Invoke(this, remote);
        } catch (Exception ex) {
          Trace.WriteLine($"TCP host connection callback failed: {ex}");
        }
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) {
    } catch (SocketException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      Trace.WriteLine($"TCP host accept loop stopped: {ex}");
    }
  }

  public void Dispose() {
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
    }
    _stop.Cancel();
    try {
      _listener.Stop();
    } catch {
    }
    if (!_acceptTask.IsCompleted && Task.CurrentId != _acceptTask.Id) {
      try {
        _acceptTask.Wait(TimeSpan.FromSeconds(1));
      } catch (AggregateException ex) {
        Trace.WriteLine($"TCP host accept loop cleanup failed: {ex.Flatten()}");
      }
    }
    _stop.Dispose();
  }
}
