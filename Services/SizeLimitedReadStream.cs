using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services;

/// <summary>
/// Rejects a response as soon as more than the configured number of bytes is read.
/// The stream is intentionally non-seekable so callers cannot reset the accounting.
/// </summary>
internal sealed class SizeLimitedReadStream : Stream {
  private readonly Stream _inner;
  private readonly long _maximumBytes;
  private readonly bool _leaveOpen;
  private long _bytesRead;

  internal SizeLimitedReadStream(Stream inner, long maximumBytes, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(inner);
    if (!inner.CanRead) {
      throw new ArgumentException("The wrapped stream must be readable.", nameof(inner));
    }
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
    _inner = inner;
    _maximumBytes = maximumBytes;
    _leaveOpen = leaveOpen;
  }

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException();
  public override long Position {
    get => _bytesRead;
    set => throw new NotSupportedException();
  }

  public override int Read(byte[] buffer, int offset, int count) {
    int read = _inner.Read(buffer, offset, LimitCount(count));
    Account(read);
    return read;
  }

  public override int Read(Span<byte> buffer) {
    int read = _inner.Read(buffer[..LimitCount(buffer.Length)]);
    Account(read);
    return read;
  }

  public override async ValueTask<int> ReadAsync(
      Memory<byte> buffer, CancellationToken cancellationToken = default) {
    int read = await _inner.ReadAsync(
        buffer[..LimitCount(buffer.Length)], cancellationToken).ConfigureAwait(false);
    Account(read);
    return read;
  }

  public override Task<int> ReadAsync(
      byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
      ReadArrayAsync(buffer, offset, count, cancellationToken);

  private async Task<int> ReadArrayAsync(
      byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
    int read = await _inner.ReadAsync(
        buffer.AsMemory(offset, LimitCount(count)), cancellationToken).ConfigureAwait(false);
    Account(read);
    return read;
  }

  public override int ReadByte() {
    int value = _inner.ReadByte();
    if (value >= 0) {
      Account(1);
    }
    return value;
  }

  private int LimitCount(int requested) {
    if (requested <= 0) {
      return requested;
    }
    long remainingWithProbe = _maximumBytes - _bytesRead + 1;
    return (int)Math.Min(requested, Math.Max(1, remainingWithProbe));
  }

  private void Account(int count) {
    _bytesRead += count;
    if (_bytesRead > _maximumBytes) {
      throw new InvalidDataException(
          $"Stream exceeds the {_maximumBytes}-byte safety limit.");
    }
  }

  protected override void Dispose(bool disposing) {
    if (disposing && !_leaveOpen) {
      _inner.Dispose();
    }
    base.Dispose(disposing);
  }

  public override async ValueTask DisposeAsync() {
    if (!_leaveOpen) {
      await _inner.DisposeAsync().ConfigureAwait(false);
    }
    GC.SuppressFinalize(this);
  }

  public override void Flush() => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();
}
