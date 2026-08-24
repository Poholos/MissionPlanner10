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

        private readonly List<ByteRange> _ranges = new List<ByteRange>();

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
        /// </summary>
        public bool Add(uint offset, byte count, bool inferTotalLength)
        {
            ulong end = (ulong)offset + count;
            if (end > uint.MaxValue)
                return false;

            if (inferTotalLength && count < PacketSize)
                TotalLength = (uint)end;

            if (count == 0)
                return true;

            Merge(new ByteRange(offset, end));
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
