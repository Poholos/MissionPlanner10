using System;
using System.Collections.Generic;

namespace MissionPlanner
{
    /// <summary>
    /// Tracks byte ranges received by the MAVLink LOG_DATA protocol. Packets may be duplicated,
    /// delayed or delivered out of order, so the last packet offset is not a reliable measure of
    /// progress and a packet-number set cannot describe partially overlapping ranges.
    /// </summary>
    internal sealed class LogDownloadTracker
    {
        internal const uint PacketSize = 90;

        /// <summary>
        /// How far past the contiguous frontier a packet may sit and still raise the bar an end
        /// marker must clear. Genuine streams are near-contiguous (reordering spans a few packets),
        /// so real packets always qualify; a corrupt far offset must not.
        /// </summary>
        private const ulong InferenceSlack = PacketSize * 100;

        private readonly List<ByteRange> _ranges = new List<ByteRange>();
        private ulong _highestEnd;
        private ulong _pendingTotalLength;

        public uint? TotalLength { get; private set; }

        public ulong CoveredBytes
        {
            get
            {
                ulong limit = TotalLength.HasValue ? TotalLength.Value : ulong.MaxValue;
                ulong covered = 0;
                foreach (ByteRange range in _ranges)
                {
                    if (range.Start >= limit)
                        break;

                    covered += Math.Min(range.End, limit) - range.Start;
                }

                return covered;
            }
        }

        public bool IsComplete => TotalLength.HasValue && CoveredBytes >= TotalLength.Value;

        /// <summary>
        /// Records a valid LOG_DATA payload. A short packet from the initial unbounded request
        /// identifies the end of the log; callers must stop inferring the end after it is known.
        /// A short packet is only trusted as the end when it sits at the highest offset seen so
        /// far - a delayed or duplicated short retransmit of an earlier block must not set a
        /// too-small total and silently truncate the download - and is only trusted immediately
        /// when it sits near the contiguous frontier. Past the frontier it clears the bar
        /// trivially, so a packet that is corrupt and short by chance would end the download at
        /// a phantom length; it becomes a deferred candidate that the caller promotes via
        /// <see cref="AcceptPendingTotalLength"/> once the stream goes quiet.
        /// </summary>
        public bool Add(uint offset, byte count, bool inferTotalLength)
        {
            ulong end = (ulong)offset + count;
            if (end > uint.MaxValue)
                return false;

            bool nearFrontier = offset <= FrontierEnd() + InferenceSlack;
            if (inferTotalLength && count < PacketSize && end >= _highestEnd)
            {
                if (nearFrontier)
                    TotalLength = (uint)end;
                else
                    // the largest of a final run of candidates, so a smaller corrupt one
                    // arriving after the genuine end cannot truncate the log
                    _pendingTotalLength = Math.Max(_pendingTotalLength, end);
            }
            else
            {
                // the stream continued, so any prior far end candidate was corrupt
                _pendingTotalLength = 0;
            }

            // A corrupt far offset must not permanently poison end inference: only packets near
            // the contiguous frontier raise the bar an end marker must clear.
            if (nearFrontier)
                _highestEnd = Math.Max(_highestEnd, end);

            if (count == 0)
                return true;

            Merge(new ByteRange(offset, end));
            return true;
        }

        /// <summary>
        /// Promotes a deferred end candidate recorded by <see cref="Add"/> for a short packet
        /// past the contiguous frontier. Callers invoke this when the stream goes quiet: packet
        /// loss can stall the frontier far behind a genuine end, but a corrupt packet cannot
        /// keep the stream silent - it is followed by more data, which discards the candidate.
        /// Returns true when the total length became known.
        /// </summary>
        public bool AcceptPendingTotalLength()
        {
            if (TotalLength.HasValue || _pendingTotalLength == 0)
                return false;

            TotalLength = (uint)_pendingTotalLength;
            _pendingTotalLength = 0;
            return true;
        }

        /// <summary>
        /// Returns the first missing range. Before the total is known, requesting to uint.MaxValue
        /// resumes the initial stream at the first gap. Afterwards requests are bounded so one lost
        /// packet does not force the flight controller to resend the rest of a large log.
        /// </summary>
        public LogDownloadRequest NextRequest(uint maximumKnownLength)
        {
            ulong cursor = 0;
            ulong limit = TotalLength.HasValue ? TotalLength.Value : uint.MaxValue;
            ulong missingEnd = limit;

            foreach (ByteRange range in _ranges)
            {
                if (range.Start > cursor)
                {
                    missingEnd = Math.Min(range.Start, limit);
                    break;
                }

                if (range.End > cursor)
                    cursor = range.End;

                if (cursor >= limit)
                    break;
            }

            uint offset = (uint)Math.Min(cursor, uint.MaxValue);
            if (!TotalLength.HasValue)
                return new LogDownloadRequest(offset, uint.MaxValue);

            ulong remaining = missingEnd - cursor;
            uint count = (uint)Math.Min(remaining, maximumKnownLength);
            return new LogDownloadRequest(offset, count);
        }

        /// <summary>End of the contiguous range starting at offset 0, or 0 before it exists.</summary>
        private ulong FrontierEnd()
        {
            return _ranges.Count > 0 && _ranges[0].Start == 0 ? _ranges[0].End : 0;
        }

        private void Merge(ByteRange incoming)
        {
            int index = 0;
            while (index < _ranges.Count && _ranges[index].End < incoming.Start)
                index++;

            while (index < _ranges.Count && _ranges[index].Start <= incoming.End)
            {
                incoming = new ByteRange(
                    Math.Min(incoming.Start, _ranges[index].Start),
                    Math.Max(incoming.End, _ranges[index].End));
                _ranges.RemoveAt(index);
            }

            _ranges.Insert(index, incoming);
        }

        private struct ByteRange
        {
            public ByteRange(ulong start, ulong end)
            {
                Start = start;
                End = end;
            }

            public ulong Start { get; }
            public ulong End { get; }
        }
    }

    internal struct LogDownloadRequest
    {
        public LogDownloadRequest(uint offset, uint count)
        {
            Offset = offset;
            Count = count;
        }

        public uint Offset { get; }
        public uint Count { get; }
    }
}
