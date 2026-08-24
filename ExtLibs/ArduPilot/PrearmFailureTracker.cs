using System;
using System.Collections.Generic;

namespace MissionPlanner.ArduPilot
{
    internal sealed class PrearmFailureTracker
    {
        private DateTime _lastHealthy = DateTime.MaxValue;

        internal string Update(bool healthy, bool enabled, bool present,
            IReadOnlyList<(DateTime time, string message)> messages, DateTime now)
        {
            if (healthy || !enabled || !present)
            {
                _lastHealthy = now;
                return null;
            }

            if (_lastHealthy > now)
            {
                _lastHealthy = now;
                return null;
            }

            for (int index = messages.Count - 1; index >= 0; index--)
            {
                (DateTime time, string message) candidate = messages[index];
                if (candidate.time > _lastHealthy &&
                    candidate.message?.IndexOf("prearm", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate.message;
                }
            }

            return null;
        }
    }
}
