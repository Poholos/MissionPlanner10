using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services;

internal sealed record OsdTuningSlot(
    byte Screen,
    byte Index,
    string ParameterName,
    MAVLink.OSD_PARAM_CONFIG_TYPE Type,
    float Minimum,
    float Maximum,
    float Increment);

internal sealed record OsdTuningWriteResult(
    byte Screen,
    byte Index,
    MAVLink.OSD_PARAM_CONFIG_ERROR Result) {
  internal bool Success => Result == MAVLink.OSD_PARAM_CONFIG_ERROR.OSD_PARAM_SUCCESS;
}

/// <summary>
/// Reads and writes the ArduPilot OSD 5/6 parameter-editor slots over the active MAVLink link.
/// A service instance is pinned to one system/component pair so switching the global selection
/// cannot complete an in-flight request with a reply from another vehicle.
/// </summary>
internal sealed class OsdTuningSlotService : IDisposable {
  internal const int FirstScreen = 5;
  internal const int LastScreen = 6;
  internal const int FirstIndex = 1;
  internal const int LastIndex = 9;
  internal const int ParameterIdLength = 16;

  private static int _nextRequestId;

  private readonly MAVLinkInterface _comPort;
  private readonly byte _systemId;
  private readonly byte _componentId;
  private readonly int _showSubscription;
  private readonly int _writeSubscription;
  private readonly ConcurrentDictionary<uint, PendingShow> _pendingShows = new();
  private readonly ConcurrentDictionary<uint, PendingWrite> _pendingWrites = new();
  private int _disposed;

  internal OsdTuningSlotService(MAVLinkInterface comPort) {
    _comPort = comPort ?? throw new ArgumentNullException(nameof(comPort));
    _systemId = (byte)comPort.sysidcurrent;
    _componentId = (byte)comPort.compidcurrent;
    if (_systemId == 0 || _componentId == 0) {
      throw new InvalidOperationException("Select a MAVLink device before reading OSD tuning slots.");
    }

    _showSubscription = comPort.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.OSD_PARAM_SHOW_CONFIG_REPLY,
        HandleShowReply, _systemId, _componentId);
    _writeSubscription = comPort.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.OSD_PARAM_CONFIG_REPLY,
        HandleWriteReply, _systemId, _componentId);
  }

  internal byte SystemId => _systemId;
  internal byte ComponentId => _componentId;

  internal async Task<IReadOnlyList<OsdTuningSlot>> ReadAllAsync(
      TimeSpan timeout, CancellationToken cancellationToken) {
    ThrowIfDisposed();
    var tasks = new List<Task<OsdTuningSlot>>(18);
    for (byte screen = FirstScreen; screen <= LastScreen; screen++) {
      for (byte index = FirstIndex; index <= LastIndex; index++) {
        tasks.Add(ReadAsync(screen, index, timeout, cancellationToken));
        // Do not burst all 18 MAVLink requests into a slow telemetry radio in one scheduler tick.
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
      }
    }
    return await Task.WhenAll(tasks).ConfigureAwait(false);
  }

  internal async Task<OsdTuningSlot> ReadAsync(
      byte screen, byte index, TimeSpan timeout, CancellationToken cancellationToken) {
    ValidateSlot(screen, index);
    ThrowIfDisposed();
    uint requestId = NextRequestId();
    var completion = new TaskCompletionSource<OsdTuningSlot>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    if (!_pendingShows.TryAdd(requestId, new PendingShow(screen, index, completion))) {
      throw new InvalidOperationException("Unable to register the OSD tuning-slot request.");
    }

    try {
      EnsureConnected();
      _comPort.sendPacket(new MAVLink.mavlink_osd_param_show_config_t(
          requestId, _systemId, _componentId, screen, index), _systemId, _componentId);
      return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    } finally {
      _pendingShows.TryRemove(requestId, out _);
    }
  }

  internal async Task<OsdTuningWriteResult> WriteAsync(
      OsdTuningSlot slot, TimeSpan timeout, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(slot);
    ValidateSlot(slot.Screen, slot.Index);
    byte[] parameterId = EncodeParameterId(slot.ParameterName);
    ThrowIfDisposed();
    uint requestId = NextRequestId();
    var completion = new TaskCompletionSource<OsdTuningWriteResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    if (!_pendingWrites.TryAdd(requestId, new PendingWrite(
            slot.Screen, slot.Index, completion))) {
      throw new InvalidOperationException("Unable to register the OSD tuning-slot update.");
    }

    try {
      EnsureConnected();
      _comPort.sendPacket(new MAVLink.mavlink_osd_param_config_t(
          requestId, slot.Minimum, slot.Maximum, slot.Increment,
          _systemId, _componentId, slot.Screen, slot.Index, parameterId, (byte)slot.Type),
          _systemId, _componentId);
      return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    } finally {
      _pendingWrites.TryRemove(requestId, out _);
    }
  }

  private bool HandleShowReply(MAVLink.MAVLinkMessage message) {
    if (message.data is not MAVLink.mavlink_osd_param_show_config_reply_t reply
        || !_pendingShows.TryRemove(reply.request_id, out PendingShow? pending)) {
      return true;
    }

    var result = (MAVLink.OSD_PARAM_CONFIG_ERROR)reply.result;
    if (result != MAVLink.OSD_PARAM_CONFIG_ERROR.OSD_PARAM_SUCCESS) {
      pending.Completion.TrySetException(new InvalidOperationException(
          $"OSD screen {pending.Screen} slot {pending.Index}: {result}."));
      return true;
    }

    pending.Completion.TrySetResult(new OsdTuningSlot(
        pending.Screen,
        pending.Index,
        DecodeParameterId(reply.param_id),
        (MAVLink.OSD_PARAM_CONFIG_TYPE)reply.config_type,
        reply.min_value,
        reply.max_value,
        reply.increment));
    return true;
  }

  private bool HandleWriteReply(MAVLink.MAVLinkMessage message) {
    if (message.data is not MAVLink.mavlink_osd_param_config_reply_t reply
        || !_pendingWrites.TryRemove(reply.request_id, out PendingWrite? pending)) {
      return true;
    }
    pending.Completion.TrySetResult(new OsdTuningWriteResult(
        pending.Screen,
        pending.Index,
        (MAVLink.OSD_PARAM_CONFIG_ERROR)reply.result));
    return true;
  }

  internal static byte[] EncodeParameterId(string parameterName) {
    string name = (parameterName ?? string.Empty).Trim();
    if (name.Length == 0) {
      throw new ArgumentException("Choose a parameter for every changed OSD slot.",
          nameof(parameterName));
    }
    if (name.Any(character => character > 0x7f || char.IsControl(character))) {
      throw new ArgumentException("MAVLink parameter names must contain printable ASCII only.",
          nameof(parameterName));
    }
    byte[] encoded = Encoding.ASCII.GetBytes(name);
    if (encoded.Length > ParameterIdLength) {
      throw new ArgumentException(
          $"MAVLink parameter names are limited to {ParameterIdLength} bytes.",
          nameof(parameterName));
    }
    var field = new byte[ParameterIdLength];
    encoded.CopyTo(field, 0);
    return field;
  }

  internal static string DecodeParameterId(byte[]? parameterId) {
    if (parameterId == null || parameterId.Length == 0) {
      return string.Empty;
    }
    int length = Array.IndexOf(parameterId, (byte)0);
    if (length < 0) {
      length = Math.Min(parameterId.Length, ParameterIdLength);
    }
    return Encoding.ASCII.GetString(parameterId, 0, length).Trim();
  }

  internal static void ValidateSlot(byte screen, byte index) {
    if (screen is < FirstScreen or > LastScreen) {
      throw new ArgumentOutOfRangeException(nameof(screen),
          $"OSD tuning slots are available on screens {FirstScreen} and {LastScreen}.");
    }
    if (index is < FirstIndex or > LastIndex) {
      throw new ArgumentOutOfRangeException(nameof(index),
          $"OSD tuning slot index must be {FirstIndex}..{LastIndex}.");
    }
  }

  private static uint NextRequestId() => unchecked((uint)Interlocked.Increment(ref _nextRequestId));

  private void EnsureConnected() {
    if (_comPort.BaseStream?.IsOpen != true) {
      throw new InvalidOperationException("The MAVLink connection is closed.");
    }
    if ((byte)_comPort.sysidcurrent != _systemId
        || (byte)_comPort.compidcurrent != _componentId) {
      throw new OperationCanceledException(
          "The selected MAVLink device changed while editing OSD tuning slots.");
    }
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
      Volatile.Read(ref _disposed) != 0, this);

  public void Dispose() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    _comPort.UnSubscribeToPacketType(_showSubscription);
    _comPort.UnSubscribeToPacketType(_writeSubscription);
    var exception = new ObjectDisposedException(nameof(OsdTuningSlotService));
    foreach (PendingShow pending in _pendingShows.Values) {
      pending.Completion.TrySetException(exception);
    }
    foreach (PendingWrite pending in _pendingWrites.Values) {
      pending.Completion.TrySetException(exception);
    }
    _pendingShows.Clear();
    _pendingWrites.Clear();
  }

  private sealed record PendingShow(
      byte Screen, byte Index, TaskCompletionSource<OsdTuningSlot> Completion);

  private sealed record PendingWrite(
      byte Screen, byte Index, TaskCompletionSource<OsdTuningWriteResult> Completion);
}
