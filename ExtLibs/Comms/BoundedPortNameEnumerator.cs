using System;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Comms
{
    /// <summary>
    /// Runs the platform serial-port probe on one dedicated background thread. A platform driver
    /// can block forever, so timed-out callers stop waiting and reuse the same outstanding probe
    /// instead of consuming more ThreadPool workers or creating an unbounded number of threads.
    /// </summary>
    internal sealed class BoundedPortNameEnumerator
    {
        private readonly Func<string[]> _provider;
        private readonly object _sync = new object();
        private Task<string[]> _attempt;
        private bool _timeoutObserved;
        private string[] _lastSuccessful = Array.Empty<string>();

        internal BoundedPortNameEnumerator(Func<string[]> provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        internal PortNameEnumerationResult TryEnumerate(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

            Task<string[]> attempt;
            lock (_sync)
            {
                if (_attempt != null && !_attempt.IsCompleted && _timeoutObserved)
                {
                    return PortNameEnumerationResult.Timeout(_lastSuccessful);
                }

                if (_attempt == null)
                {
                    _timeoutObserved = false;
                    _attempt = Task.Factory.StartNew(
                        _provider,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                }

                attempt = _attempt;
            }

            bool completed;
            try
            {
                completed = attempt.Wait(timeoutMilliseconds);
            }
            catch (AggregateException)
            {
                // The provider exception is unwrapped below so callers get the original failure.
                completed = true;
            }

            if (!completed)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_attempt, attempt))
                        _timeoutObserved = true;
                    return PortNameEnumerationResult.Timeout(_lastSuccessful);
                }
            }

            try
            {
                string[] ports = attempt.GetAwaiter().GetResult() ?? Array.Empty<string>();
                string[] snapshot = (string[])ports.Clone();
                lock (_sync)
                {
                    if (ReferenceEquals(_attempt, attempt))
                    {
                        _lastSuccessful = snapshot;
                        _attempt = null;
                        _timeoutObserved = false;
                    }
                }
                return PortNameEnumerationResult.Success(snapshot);
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    string[] fallback = (string[])_lastSuccessful.Clone();
                    if (ReferenceEquals(_attempt, attempt))
                    {
                        _attempt = null;
                        _timeoutObserved = false;
                    }
                    return PortNameEnumerationResult.Failure(ex, fallback);
                }
            }
        }
    }

    internal sealed class PortNameEnumerationResult
    {
        private PortNameEnumerationResult(
            bool succeeded, bool timedOut, string[] ports, Exception error)
        {
            Succeeded = succeeded;
            TimedOut = timedOut;
            Ports = ports ?? Array.Empty<string>();
            Error = error;
        }

        internal bool Succeeded { get; }
        internal bool TimedOut { get; }
        internal string[] Ports { get; }
        internal Exception Error { get; }

        internal static PortNameEnumerationResult Success(string[] ports) =>
            new PortNameEnumerationResult(true, false, ports, null);

        internal static PortNameEnumerationResult Timeout(string[] fallback) =>
            new PortNameEnumerationResult(false, true, (string[])fallback.Clone(), null);

        internal static PortNameEnumerationResult Failure(Exception error, string[] fallback) =>
            new PortNameEnumerationResult(false, false, (string[])fallback.Clone(), error);
    }
}
