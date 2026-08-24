using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace MissionPlanner.ArduPilot.Mavlink
{
    internal interface IMessageRateTransport
    {
        bool IsCommandChannelBusy { get; }
        int Subscribe(MAVLink.MAVLINK_MSG_ID messageId,
            Func<MAVLink.MAVLinkMessage, bool> handler, byte sysid, byte compid);
        void Unsubscribe(int subscriptionId);
        bool HasEverReceived(uint messageId, byte sysid, byte compid);
        int GetLinkQualityPercent(byte sysid, byte compid);
        Task<bool> SetIntervalAsync(uint messageId, byte sysid, byte compid,
            int intervalMicroseconds, bool requireAcknowledgement);
        Task<bool> GetIntervalAsync(uint messageId, byte sysid, byte compid);
    }

    internal sealed class MavlinkMessageRateTransport : IMessageRateTransport
    {
        private readonly MAVLinkInterface _port;

        internal MavlinkMessageRateTransport(MAVLinkInterface port)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
        }

        public bool IsCommandChannelBusy => _port.giveComport;

        public int Subscribe(MAVLink.MAVLINK_MSG_ID messageId,
            Func<MAVLink.MAVLinkMessage, bool> handler, byte sysid, byte compid)
        {
            return _port.SubscribeToPacketType(messageId, handler, sysid, compid);
        }

        public void Unsubscribe(int subscriptionId)
        {
            _port.UnSubscribeToPacketType(subscriptionId);
        }

        public bool HasEverReceived(uint messageId, byte sysid, byte compid)
        {
            try
            {
                return _port.MAVlist[sysid, compid].packetspersecondbuild.ContainsKey(messageId);
            }
            catch
            {
                return false;
            }
        }

        public int GetLinkQualityPercent(byte sysid, byte compid)
        {
            try
            {
                return _port.MAVlist[sysid, compid].cs.linkqualitygcs;
            }
            catch
            {
                return 100;
            }
        }

        public Task<bool> SetIntervalAsync(uint messageId, byte sysid, byte compid,
            int intervalMicroseconds, bool requireAcknowledgement)
        {
            return _port.doCommandAsync(sysid, compid,
                MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
                messageId, intervalMicroseconds,
                0, 0, 0, 0, 0, requireAcknowledgement);
        }

        public Task<bool> GetIntervalAsync(uint messageId, byte sysid, byte compid)
        {
            return _port.doCommandAsync(sysid, compid,
                MAVLink.MAV_CMD.GET_MESSAGE_INTERVAL,
                messageId, 0, 0, 0, 0, 0, 0, false);
        }
    }

    /// <summary>
    /// Represents a disposable request for a minimum MAVLink message rate.
    /// </summary>
    public sealed class MessageRateLease : IDisposable
    {
        private readonly MessageRateManager _manager;

        internal readonly uint MessageId;
        internal readonly byte SystemId;
        internal readonly byte ComponentId;
        internal readonly double Hertz;
        internal readonly string Owner;
        internal int Released;

        internal MessageRateLease(MessageRateManager manager, uint messageId,
            byte systemId, byte componentId, double hertz, string owner)
        {
            _manager = manager;
            MessageId = messageId;
            SystemId = systemId;
            ComponentId = componentId;
            Hertz = hertz;
            Owner = owner ?? "";
        }

        public void Dispose()
        {
            _manager.Release(this);
        }
    }

    /// <summary>
    /// Coordinates per-message MAVLink streaming rates. The fastest active lease wins,
    /// and releasing the final lease restores the autopilot's default rate.
    /// </summary>
    public sealed class MessageRateManager : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IMessageRateTransport _transport;
        private readonly object _lock = new object();
        private readonly TimeSpan _monitorInterval;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _wakeWorker = new SemaphoreSlim(0, 1);

        private readonly Dictionary<(uint messageId, byte sysid, byte compid), List<MessageRateLease>> _leases
            = new Dictionary<(uint, byte, byte), List<MessageRateLease>>();
        private readonly HashSet<(uint messageId, byte sysid, byte compid)> _unsupported
            = new HashSet<(uint, byte, byte)>();
        private readonly HashSet<(uint messageId, byte sysid, byte compid)> _pendingRestores
            = new HashSet<(uint, byte, byte)>();
        private readonly Dictionary<(byte sysid, byte compid), int> _intervalSubscriptions
            = new Dictionary<(byte, byte), int>();
        private readonly Dictionary<(uint messageId, byte sysid, byte compid), int> _packetSubscriptions
            = new Dictionary<(uint, byte, byte), int>();
        private readonly Dictionary<(uint messageId, byte sysid, byte compid), long> _packetCounts
            = new Dictionary<(uint, byte, byte), long>();
        private readonly Dictionary<(uint messageId, byte sysid, byte compid), (long count, long ticks)> _snapshots
            = new Dictionary<(uint, byte, byte), (long, long)>();

        private Task _worker;
        private bool _drainingRestores;
        private int _disposed;

        public MessageRateManager(MAVLinkInterface port)
            : this(new MavlinkMessageRateTransport(port), TimeSpan.FromSeconds(30))
        {
        }

        internal MessageRateManager(IMessageRateTransport transport, TimeSpan monitorInterval)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (monitorInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(monitorInterval));
            _monitorInterval = monitorInterval;
        }

        public MessageRateLease Subscribe(byte sysid, byte compid,
            MAVLink.MAVLINK_MSG_ID messageId, double hertz, string owner = null)
        {
            ThrowIfDisposed();
            int intervalMicroseconds = HertzToIntervalMicroseconds(hertz);
            uint id = (uint)messageId;
            if (id > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(messageId),
                    "MESSAGE_INTERVAL only supports message IDs up to 65535.");

            var lease = new MessageRateLease(this, id, sysid, compid, hertz, owner);
            if (sysid == 0 || compid == 0)
                return lease;

            var key = (id, sysid, compid);
            bool unsupported;
            lock (_lock)
            {
                if (!_leases.TryGetValue(key, out List<MessageRateLease> active))
                {
                    active = new List<MessageRateLease>();
                    _leases[key] = active;
                }
                active.Add(lease);
                _pendingRestores.Remove(key);
                unsupported = _unsupported.Contains(key);

                EnsureIntervalSubscriptionLocked(sysid, compid);
                if (!unsupported)
                    EnsurePacketSubscriptionLocked(key);
                intervalMicroseconds = FastestIntervalLocked(key).Value;
            }

            if (!unsupported)
            {
                SendSetWithoutAcknowledgement(key, intervalMicroseconds);
                if (!_transport.HasEverReceived(id, sysid, compid))
                    SendGetWithoutAcknowledgement(key);
            }
            else
            {
                log.InfoFormat(
                    "RateManager: {0} subscribed to unsupported msg {1} ({2},{3}); waiting for reconnect",
                    owner ?? "", id, sysid, compid);
            }

            EnsureWorkerStarted();
            return lease;
        }

        internal void Release(MessageRateLease lease)
        {
            if (lease == null || Interlocked.CompareExchange(ref lease.Released, 1, 0) != 0)
                return;
            if (lease.SystemId == 0 || lease.ComponentId == 0 || Volatile.Read(ref _disposed) != 0)
                return;

            var key = (lease.MessageId, lease.SystemId, lease.ComponentId);
            int? nextInterval = null;
            bool restore = false;
            lock (_lock)
            {
                if (!_leases.TryGetValue(key, out List<MessageRateLease> active))
                    return;

                active.Remove(lease);
                if (active.Count == 0)
                {
                    _leases.Remove(key);
                    RemovePacketSubscriptionLocked(key);
                    if (_unsupported.Remove(key))
                    {
                        _pendingRestores.Remove(key);
                    }
                    else
                    {
                        _pendingRestores.Add(key);
                        restore = true;
                    }
                }
                else if (!_unsupported.Contains(key))
                {
                    nextInterval = FastestIntervalLocked(key);
                }
            }

            if (nextInterval.HasValue)
                SendSetWithoutAcknowledgement(key, nextInterval.Value);
            if (restore)
                SignalWorker();

            TryRemoveIntervalSubscription(lease.SystemId, lease.ComponentId);
            log.InfoFormat("RateManager: {0} released msg {1} ({2},{3}){4}",
                lease.Owner, lease.MessageId, lease.SystemId, lease.ComponentId,
                restore ? " -- restoring default" : "");
        }

        /// <summary>
        /// Clears connection-specific observations and re-applies every active lease.
        /// </summary>
        public void OnConnectionOpen()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            List<((uint messageId, byte sysid, byte compid) key, int interval)> active;
            lock (_lock)
            {
                _unsupported.Clear();
                _pendingRestores.Clear();
                _snapshots.Clear();
                active = new List<((uint, byte, byte), int)>();
                foreach (var key in _leases.Keys.ToList())
                {
                    EnsureIntervalSubscriptionLocked(key.sysid, key.compid);
                    EnsurePacketSubscriptionLocked(key);
                    int? interval = FastestIntervalLocked(key);
                    if (interval.HasValue)
                        active.Add((key, interval.Value));
                }
            }

            foreach (var request in active)
            {
                SendSetWithoutAcknowledgement(request.key, request.interval);
                if (!_transport.HasEverReceived(
                        request.key.messageId, request.key.sysid, request.key.compid))
                    SendGetWithoutAcknowledgement(request.key);
            }
            EnsureWorkerStarted();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _lifetime.Cancel();
            List<int> subscriptions;
            Task worker;
            lock (_lock)
            {
                subscriptions = _intervalSubscriptions.Values
                    .Concat(_packetSubscriptions.Values).ToList();
                _intervalSubscriptions.Clear();
                _packetSubscriptions.Clear();
                _packetCounts.Clear();
                _snapshots.Clear();
                _leases.Clear();
                _unsupported.Clear();
                _pendingRestores.Clear();
                worker = _worker;
            }

            foreach (int subscription in subscriptions)
            {
                try
                {
                    _transport.Unsubscribe(subscription);
                }
                catch
                {
                }
            }

            if (worker == null || worker.IsCompleted)
            {
                _wakeWorker.Dispose();
                _lifetime.Dispose();
            }
            else
            {
                worker.ContinueWith(_ =>
                    {
                        _wakeWorker.Dispose();
                        _lifetime.Dispose();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private void EnsureWorkerStarted()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            lock (_lock)
            {
                if (_worker == null || _worker.IsCompleted)
                    _worker = Task.Run(RunWorkerAsync);
            }
        }

        private async Task RunWorkerAsync()
        {
            CancellationToken token = _lifetime.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _wakeWorker.WaitAsync(_monitorInterval, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                        break;
                    Tick();
                    await ProcessPendingRestoresAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.Error("RateManager: worker failed", ex);
                }
            }
        }

        private void SignalWorker()
        {
            EnsureWorkerStarted();
            try
            {
                if (_wakeWorker.CurrentCount == 0)
                    _wakeWorker.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Tick()
        {
            List<((uint messageId, byte sysid, byte compid) key, int interval)> requests;
            lock (_lock)
            {
                requests = new List<((uint, byte, byte), int)>();
                foreach (var key in _leases.Keys.ToList())
                {
                    if (_unsupported.Contains(key))
                        continue;
                    int? interval = FastestIntervalLocked(key);
                    if (interval.HasValue && !IsRateSatisfiedLocked(key, interval.Value))
                        requests.Add((key, interval.Value));

                    if (_packetCounts.TryGetValue(key, out long count))
                        _snapshots[key] = (count, Stopwatch.GetTimestamp());
                }
            }

            foreach (var request in requests)
            {
                SendSetWithoutAcknowledgement(request.key, request.interval);
                if (!_transport.HasEverReceived(
                        request.key.messageId, request.key.sysid, request.key.compid))
                    SendGetWithoutAcknowledgement(request.key);
            }
        }

        private bool IsRateSatisfiedLocked(
            (uint messageId, byte sysid, byte compid) key, int desiredInterval)
        {
            if (!_packetCounts.TryGetValue(key, out long count) ||
                !_snapshots.TryGetValue(key, out (long count, long ticks) snapshot))
                return false;

            double elapsed = (double)(Stopwatch.GetTimestamp() - snapshot.ticks) /
                             Stopwatch.Frequency;
            if (elapsed < 1)
                return true;

            long received = count - snapshot.count;
            if (received <= 0)
                return false;

            double observedHertz = received / elapsed;
            int linkQuality = _transport.GetLinkQualityPercent(key.sysid, key.compid);
            double quality = linkQuality > 0 ? Math.Min(1, linkQuality / 100.0) : 1;
            double lossCompensation = quality > 0.5 ? 1 / quality : 2;
            double estimatedHertz = observedHertz * lossCompensation;
            double desiredHertz = IntervalMicrosecondsToHertz(desiredInterval);
            return estimatedHertz >= desiredHertz * 0.8;
        }

        private void EnsureIntervalSubscriptionLocked(byte sysid, byte compid)
        {
            var target = (sysid, compid);
            if (_intervalSubscriptions.ContainsKey(target))
                return;

            int subscription = _transport.Subscribe(
                MAVLink.MAVLINK_MSG_ID.MESSAGE_INTERVAL,
                message =>
                {
                    MAVLink.mavlink_message_interval_t interval =
                        message.ToStructure<MAVLink.mavlink_message_interval_t>();
                    OnMessageInterval(interval.message_id, sysid, compid, interval.interval_us);
                    return true;
                }, sysid, compid);
            _intervalSubscriptions[target] = subscription;
        }

        private void TryRemoveIntervalSubscription(byte sysid, byte compid)
        {
            int subscription;
            lock (_lock)
            {
                if (_leases.Keys.Any(key => key.sysid == sysid && key.compid == compid) ||
                    !_intervalSubscriptions.TryGetValue((sysid, compid), out subscription))
                    return;
                _intervalSubscriptions.Remove((sysid, compid));
            }

            try
            {
                _transport.Unsubscribe(subscription);
            }
            catch
            {
            }
        }

        private void OnMessageInterval(ushort messageId, byte sysid, byte compid,
            int intervalMicroseconds)
        {
            if (intervalMicroseconds != 0)
                return;

            var key = ((uint)messageId, sysid, compid);
            lock (_lock)
            {
                if (!_leases.ContainsKey(key))
                    return;
                _unsupported.Add(key);
                RemovePacketSubscriptionLocked(key);
            }
            log.WarnFormat("RateManager: msg {0} ({1},{2}) is unsupported", messageId, sysid, compid);
        }

        private void EnsurePacketSubscriptionLocked(
            (uint messageId, byte sysid, byte compid) key)
        {
            if (_packetSubscriptions.ContainsKey(key))
                return;

            _packetCounts[key] = 0;
            _snapshots[key] = (0, Stopwatch.GetTimestamp());
            int subscription = _transport.Subscribe(
                (MAVLink.MAVLINK_MSG_ID)key.messageId,
                _ =>
                {
                    lock (_lock)
                    {
                        if (_packetCounts.TryGetValue(key, out long count))
                            _packetCounts[key] = count + 1;
                    }
                    return true;
                }, key.sysid, key.compid);
            _packetSubscriptions[key] = subscription;
        }

        private void RemovePacketSubscriptionLocked(
            (uint messageId, byte sysid, byte compid) key)
        {
            if (_packetSubscriptions.TryGetValue(key, out int subscription))
            {
                _packetSubscriptions.Remove(key);
                try
                {
                    _transport.Unsubscribe(subscription);
                }
                catch
                {
                }
            }
            _packetCounts.Remove(key);
            _snapshots.Remove(key);
        }

        internal async Task ProcessPendingRestoresAsync()
        {
            lock (_lock)
            {
                if (_drainingRestores || _pendingRestores.Count == 0 ||
                    Volatile.Read(ref _disposed) != 0)
                    return;
                _drainingRestores = true;
            }

            try
            {
                List<(uint messageId, byte sysid, byte compid)> pending;
                lock (_lock)
                    pending = _pendingRestores.ToList();

                foreach (var key in pending)
                {
                    CancellationToken token = _lifetime.Token;
                    token.ThrowIfCancellationRequested();

                    bool shouldRestore;
                    lock (_lock)
                    {
                        shouldRestore = _pendingRestores.Contains(key) &&
                                        !_leases.ContainsKey(key);
                    }
                    if (!shouldRestore)
                        continue;

                    for (int attempt = 0;
                         attempt < 5 && _transport.IsCommandChannelBusy;
                         attempt++)
                        await Task.Delay(200, token).ConfigureAwait(false);
                    if (_transport.IsCommandChannelBusy)
                        continue;

                    try
                    {
                        bool accepted = await _transport.SetIntervalAsync(
                            key.messageId, key.sysid, key.compid, 0, true)
                            .ConfigureAwait(false);
                        if (accepted)
                        {
                            lock (_lock)
                                _pendingRestores.Remove(key);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.WarnFormat(
                            "RateManager: restore failed for msg {0} ({1},{2}): {3}",
                            key.messageId, key.sysid, key.compid, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_lock)
                    _drainingRestores = false;
            }
        }

        private void SendSetWithoutAcknowledgement(
            (uint messageId, byte sysid, byte compid) key, int intervalMicroseconds)
        {
            lock (_lock)
            {
                if (_packetCounts.TryGetValue(key, out long count))
                    _snapshots[key] = (count, Stopwatch.GetTimestamp());
            }

            try
            {
                ObserveFault(_transport.SetIntervalAsync(
                    key.messageId, key.sysid, key.compid, intervalMicroseconds, false),
                    "SET_MESSAGE_INTERVAL");
            }
            catch (Exception ex)
            {
                log.Debug("RateManager: SET_MESSAGE_INTERVAL failed: " + ex.Message);
            }
        }

        private void SendGetWithoutAcknowledgement(
            (uint messageId, byte sysid, byte compid) key)
        {
            try
            {
                ObserveFault(_transport.GetIntervalAsync(
                    key.messageId, key.sysid, key.compid), "GET_MESSAGE_INTERVAL");
            }
            catch (Exception ex)
            {
                log.Debug("RateManager: GET_MESSAGE_INTERVAL failed: " + ex.Message);
            }
        }

        private static void ObserveFault(Task task, string operation)
        {
            if (task == null)
                return;
            task.ContinueWith(faulted =>
                log.Debug("RateManager: " + operation + " failed: " +
                          faulted.Exception?.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private int? FastestIntervalLocked(
            (uint messageId, byte sysid, byte compid) key)
        {
            if (!_leases.TryGetValue(key, out List<MessageRateLease> active) || active.Count == 0)
                return null;
            return active.Min(lease => HertzToIntervalMicroseconds(lease.Hertz));
        }

        internal static int HertzToIntervalMicroseconds(double hertz)
        {
            if (double.IsNaN(hertz) || double.IsInfinity(hertz) || hertz <= 0)
                throw new ArgumentOutOfRangeException(nameof(hertz),
                    "Message rate must be a finite positive value.");

            double interval = 1e6 / hertz;
            if (interval <= 1)
                return 1;
            if (interval >= int.MaxValue)
                return int.MaxValue;
            return (int)Math.Round(interval, MidpointRounding.AwayFromZero);
        }

        internal static double IntervalMicrosecondsToHertz(int intervalMicroseconds)
        {
            return intervalMicroseconds > 0 ? 1e6 / intervalMicroseconds : 0;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(MessageRateManager));
        }
    }
}
