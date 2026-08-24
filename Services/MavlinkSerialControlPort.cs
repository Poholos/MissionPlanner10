using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MissionPlanner.Comms;

namespace MissionPlanner.Services;

/// <summary>
/// Synchronous ICommsSerial adapter over MAVLink SERIAL_CONTROL. It is intentionally bound to the
/// same guarded target rules as the TCP bridge: one selected disarmed autopilot on one live link.
/// Unlike the inherited adapter, it owns no polling thread and never uses Thread.Abort.
/// </summary>
internal sealed class MavlinkSerialControlPort : ICommsSerial {
  private const int BufferLimit = 8192;
  private const ushort RequestTimeoutMs = 100;
  private readonly SerialControlTarget _target;
  private readonly MAVLink.SERIAL_CONTROL_DEV _device;
  private readonly Queue<byte> _buffer = new();
  private readonly AutoResetEvent _dataAvailable = new(false);
  private readonly int _subscription;
  private int _open;
  private int _disposed;
  private int _baudRate;
  private long _lastRequestMs;

  private MavlinkSerialControlPort(
      SerialControlTarget target, MAVLink.SERIAL_CONTROL_DEV device, int baudRate) {
    _target = target;
    _device = device;
    _baudRate = baudRate;
    _subscription = target.Link.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.SERIAL_CONTROL,
        OnSerialControl,
        target.SystemId,
        target.ComponentId);
  }

  internal static bool TryCreate(
      MAVLink.SERIAL_CONTROL_DEV device, int baudRate,
      out MavlinkSerialControlPort? port, out string error) {
    if (!SerialControlTargetGuard.TryCapture(out SerialControlTarget? target, out error)
        || target == null) {
      port = null;
      return false;
    }
    port = new MavlinkSerialControlPort(target, device, baudRate);
    return true;
  }

  public int BaudRate {
    get => _baudRate;
    set {
      _baudRate = value;
      if (IsOpen) {
        EnsureCurrent();
        _target.Link.SendSerialControl(_device, RequestTimeoutMs, null, (uint)value);
      }
    }
  }

  public int BytesToRead {
    get {
      RequestDataIfOpen();
      lock (_buffer) {
        return _buffer.Count;
      }
    }
  }

  public int BytesToWrite => 0;
  public int DataBits { get; set; } = 8;
  public bool DtrEnable { get; set; }
  public bool IsOpen => Volatile.Read(ref _open) != 0 && Volatile.Read(ref _disposed) == 0;
  public string PortName { get; set; } = "MAVLink/TELEM1";
  public int ReadBufferSize { get; set; } = BufferLimit;
  public int ReadTimeout { get; set; } = 1500;
  public bool RtsEnable { get; set; }
  public int WriteBufferSize { get; set; } = 4096;
  public int WriteTimeout { get; set; } = 1500;
  public Stream BaseStream => throw new NotSupportedException(
      "MAVLink SERIAL_CONTROL is a packet tunnel and has no local Stream.");

  public void Open() {
    ThrowIfDisposed();
    EnsureCurrent();
    if (Interlocked.Exchange(ref _open, 1) != 0) {
      return;
    }
    try {
      _target.Link.SendSerialControl(_device, RequestTimeoutMs, null, (uint)_baudRate);
      Interlocked.Exchange(ref _lastRequestMs, Environment.TickCount64);
    } catch {
      Volatile.Write(ref _open, 0);
      throw;
    }
  }

  public void Close() {
    if (Interlocked.Exchange(ref _open, 0) == 0) {
      return;
    }
    try {
      _target.Link.SendSerialControl(_device, 0, null, 0, close: true);
    } catch {
      // Physical loss cannot acknowledge release; Close must still wake readers and return.
    }
    _dataAvailable.Set();
  }

  public void DiscardInBuffer() {
    lock (_buffer) {
      _buffer.Clear();
    }
  }

  public int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    if (offset + count > buffer.Length) {
      throw new ArgumentException("Read range exceeds the destination buffer.");
    }
    if (count == 0) {
      return 0;
    }

    buffer[offset] = (byte)ReadByte();
    int read = 1;
    lock (_buffer) {
      while (read < count && _buffer.Count > 0) {
        buffer[offset + read++] = _buffer.Dequeue();
      }
    }
    return read;
  }

  public int ReadByte() {
    EnsureOpenAndCurrent();
    long deadline = Environment.TickCount64 + Math.Max(1, ReadTimeout);
    while (true) {
      lock (_buffer) {
        if (_buffer.Count > 0) {
          return _buffer.Dequeue();
        }
      }
      RequestDataIfOpen();
      int remaining = (int)Math.Min(int.MaxValue, deadline - Environment.TickCount64);
      if (remaining <= 0 || !_dataAvailable.WaitOne(Math.Min(remaining, RequestTimeoutMs))) {
        if (Environment.TickCount64 >= deadline) {
          throw new TimeoutException("MAVLink SERIAL_CONTROL read timed out.");
        }
      }
      EnsureOpenAndCurrent();
    }
  }

  public int ReadChar() => ReadByte();

  public string ReadExisting() {
    var data = new List<byte>();
    lock (_buffer) {
      while (_buffer.Count > 0) {
        data.Add(_buffer.Dequeue());
      }
    }
    return Encoding.UTF8.GetString(data.ToArray());
  }

  public string ReadLine() {
    var result = new StringBuilder();
    while (true) {
      char value = (char)ReadByte();
      result.Append(value);
      if (value == '\n') {
        return result.ToString();
      }
    }
  }

  public void Write(string text) {
    ArgumentNullException.ThrowIfNull(text);
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    Write(bytes, 0, bytes.Length);
  }

  public void Write(byte[] buffer, int offset, int count) {
    EnsureOpenAndCurrent();
    ArgumentNullException.ThrowIfNull(buffer);
    if (offset < 0 || count < 0 || offset + count > buffer.Length) {
      throw new ArgumentOutOfRangeException(nameof(count));
    }
    var copy = new byte[count];
    Array.Copy(buffer, offset, copy, 0, count);
    _target.Link.SendSerialControl(_device, 0, copy);
  }

  public void WriteLine(string text) => Write(text + "\r\n");
  public void toggleDTR() { }

  private bool OnSerialControl(MAVLink.MAVLinkMessage packet) {
    if (!IsOpen) {
      return true;
    }
    var message = packet.ToStructure<MAVLink.mavlink_serial_control_t>();
    if (message.device != (byte)_device || message.data == null) {
      return true;
    }
    int count = Math.Min(message.count, message.data.Length);
    lock (_buffer) {
      for (int index = 0; index < count; index++) {
        while (_buffer.Count >= Math.Min(BufferLimit, Math.Max(1, ReadBufferSize))) {
          _buffer.Dequeue();
        }
        _buffer.Enqueue(message.data[index]);
      }
    }
    if (count > 0) {
      _dataAvailable.Set();
    }
    return true;
  }

  private void RequestDataIfOpen() {
    if (IsOpen) {
      EnsureCurrent();
      long now = Environment.TickCount64;
      long previous = Interlocked.Read(ref _lastRequestMs);
      if (now - previous < 40
          || Interlocked.CompareExchange(ref _lastRequestMs, now, previous) != previous) {
        return;
      }
      _target.Link.SendSerialControl(_device, RequestTimeoutMs, null);
    }
  }

  private void EnsureOpenAndCurrent() {
    if (!IsOpen) {
      throw new InvalidOperationException("The MAVLink serial-control port is not open.");
    }
    EnsureCurrent();
  }

  private void EnsureCurrent() {
    if (!SerialControlTargetGuard.IsCurrent(_target)) {
      throw new SerialControlTargetChangedException();
    }
  }

  private void ThrowIfDisposed() {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    Close();
    _target.Link.UnSubscribeToPacketType(_subscription);
    _dataAvailable.Dispose();
  }
}
