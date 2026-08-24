using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.ArduPilot.Mavlink
{
    public class GimbalManagerProtocol : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);
        private readonly CurrentState cs;
        private readonly MAVLinkInterface mavint;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private byte _systemId;
        private byte _componentId;
        private int _started;
        private int _disposed;
        private volatile bool _haveManagerInformation;

        // Stores the last GIMBAL_MANAGER_INFORMATION message for each gimbal device/component ID.
        // This index will be 1-6, or MAVLink component IDs 154, 171-175.
        // Index 0 is used to store the message of the first (lowest) gimbal ID.
        public ConcurrentDictionary<byte, MAVLink.mavlink_gimbal_manager_information_t> ManagerInfo =
            new ConcurrentDictionary<byte, MAVLink.mavlink_gimbal_manager_information_t>();

        // Stores the GIMBAL_MANAGER_STATUS message for each gimbal device/component ID.
        // This index will be 1-6, or MAVLink component IDs 154, 171-175.
        // Index 0 is used to store the message of the first (lowest) gimbal ID.
        public ConcurrentDictionary<byte, MAVLink.mavlink_gimbal_manager_status_t> ManagerStatus =
            new ConcurrentDictionary<byte, MAVLink.mavlink_gimbal_manager_status_t>();

        // Stores the GIMBAL_DEVICE_ATTITUDE_STATUS message for each gimbal device/component ID.
        // This index will be 1-6, or MAVLink component IDs 154, 171-175.
        // Index 0 is used to store the message of the first (lowest) gimbal ID.
        public ConcurrentDictionary<byte, MAVLink.mavlink_gimbal_device_attitude_status_t> GimbalStatus =
            new ConcurrentDictionary<byte, MAVLink.mavlink_gimbal_device_attitude_status_t>();

        public GimbalManagerProtocol(MAVLinkInterface mavint, CurrentState cs)
        {
            this.mavint = mavint;
            this.cs = cs;
        }

        [Obsolete("Use StartID")]
        public void Discover()
        {
            ObserveFault(StartID((byte)mavint.sysidcurrent, (byte)mavint.compidcurrent));
        }

        public async Task StartID(byte sysid, byte compid)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            _systemId = sysid;
            _componentId = compid;
            mavint.OnPacketReceived += MessagesHandler;

            const ushort informationId =
                (ushort)MAVLink.MAVLINK_MSG_ID.GIMBAL_MANAGER_INFORMATION;
            const float intervalMicroseconds = 30_000_000;
            int confirmed = 0;
            int subscription = mavint.SubscribeToPacketType(
                MAVLink.MAVLINK_MSG_ID.MESSAGE_INTERVAL,
                message =>
                {
                    MAVLink.mavlink_message_interval_t interval =
                        message.ToStructure<MAVLink.mavlink_message_interval_t>();
                    if (interval.message_id == informationId)
                    {
                        Interlocked.Exchange(ref confirmed, 1);
                        log.InfoFormat(
                            "GimbalManager: information interval response {0} us",
                            interval.interval_us);
                    }
                    return true;
                }, sysid, compid);

            try
            {
                for (int attempt = 0; attempt < 3 && !_haveManagerInformation &&
                     Volatile.Read(ref confirmed) == 0; attempt++)
                {
                    SendDiscoveryIntervalRequest(informationId, intervalMicroseconds);
                    await Task.Delay(5000, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                mavint.UnSubscribeToPacketType(subscription);
            }
        }

        private void SendDiscoveryIntervalRequest(ushort messageId, float intervalMicroseconds)
        {
            try
            {
                ObserveFault(mavint.doCommandAsync(_systemId, _componentId,
                    MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
                    messageId, intervalMicroseconds,
                    0, 0, 0, 0, 0, false));
                ObserveFault(mavint.doCommandAsync(_systemId, _componentId,
                    MAVLink.MAV_CMD.GET_MESSAGE_INTERVAL,
                    messageId, 0, 0, 0, 0, 0, 0, false));
            }
            catch (Exception ex)
            {
                log.Debug("Gimbal manager discovery failed: " + ex.Message);
            }
        }

        private static void ObserveFault(Task task)
        {
            task?.ContinueWith(faulted =>
                log.Debug("Gimbal manager request failed: " +
                          faulted.Exception?.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void MessagesHandler(object sender, MAVLink.MAVLinkMessage message)
        {
            // One protocol instance belongs to one vehicle. Attitude status may come
            // from a gimbal component, while manager information comes from the manager.
            if (message.sysid != _systemId)
                return;

            if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GIMBAL_MANAGER_INFORMATION)
            {
                if (message.compid != _componentId)
                    return;
                var gmi = (MAVLink.mavlink_gimbal_manager_information_t)message.data;

                ManagerInfo[gmi.gimbal_device_id] = gmi;
                if (!ManagerInfo.ContainsKey(0) || gmi.gimbal_device_id <= ManagerInfo[0].gimbal_device_id)
                {
                    ManagerInfo[0] = gmi;
                }
                _haveManagerInformation = true;
            }

            if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GIMBAL_MANAGER_STATUS)
            {
                var gms = (MAVLink.mavlink_gimbal_manager_status_t)message.data;
                ManagerStatus[gms.gimbal_device_id] = gms;
                if (!ManagerStatus.ContainsKey(0) || gms.gimbal_device_id <= ManagerStatus[0].gimbal_device_id)
                {
                    ManagerStatus[0] = gms;
                }
            }

            if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GIMBAL_DEVICE_ATTITUDE_STATUS)
            {
                var gds = (MAVLink.mavlink_gimbal_device_attitude_status_t)message.data;
                GimbalStatus[gds.gimbal_device_id] = gds;
                if (!GimbalStatus.ContainsKey(0) || gds.gimbal_device_id <= GimbalStatus[0].gimbal_device_id)
                {
                    GimbalStatus[0] = gds;
                }
            }
        }

        public bool HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS flags, byte gimbal_device_id = 0)
        {
            return ManagerInfo.TryGetValue(gimbal_device_id, out var info) && ((info.cap_flags & (uint)flags) != 0);
        }

        public bool HasAllCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS flags, byte gimbal_device_id = 0)
        {
            return ManagerInfo.TryGetValue(gimbal_device_id, out var info) && ((info.cap_flags & (uint)flags) == (uint)flags);
        }

        public bool HasStatusFlag(MAVLink.GIMBAL_DEVICE_FLAGS flags, byte gimbal_device_id = 0)
        {
            return GimbalStatus.TryGetValue(gimbal_device_id, out var status) &&
                   ((status.flags & (uint)flags) != 0);
        }

        public bool YawInVehicleFrame(byte gimbal_device_id = 0)
        {
            return !GimbalStatus.TryGetValue(gimbal_device_id, out var status) ||
                   YawIsInVehicleFrame(status.flags);
        }

        internal static bool YawIsInVehicleFrame(uint statusFlags)
        {
            var flags = (MAVLink.GIMBAL_DEVICE_FLAGS)statusFlags;
            bool earth = (flags & MAVLink.GIMBAL_DEVICE_FLAGS.YAW_IN_EARTH_FRAME) != 0;
            bool vehicle = (flags & MAVLink.GIMBAL_DEVICE_FLAGS.YAW_IN_VEHICLE_FRAME) != 0;
            if (!earth && !vehicle)
                vehicle = (flags & MAVLink.GIMBAL_DEVICE_FLAGS.YAW_LOCK) == 0;
            return vehicle;
        }

        /// <summary>
        /// Get the reported attitude of the gimbal. Yaw always reported relative to the earth frame.
        /// </summary>
        /// <param name="gimbal_device_id">Device ID of the gimbal. 0 means all gimbals</param>
        /// <returns></returns>
        public Quaternion GetAttitude(byte gimbal_device_id = 0)
        {
            if (!GimbalStatus.TryGetValue(gimbal_device_id, out var status))
            {
                return null;
            }

            var q = new Quaternion(status.q[0], status.q[1], status.q[2], status.q[3]);

            if (YawInVehicleFrame(gimbal_device_id))
            {
                q = Quaternion.from_euler(0, 0, cs.yaw * MathHelper.deg2rad) * q;
            }

            return q;
        }

        public Task<bool> RetractAsync(byte gimbal_device_id = 0)
        {
            if (!HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_RETRACT))
            {
                return Task.FromResult(false);
            }
            return mavint.doCommandAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW,
                float.NaN, // pitch angle
                float.NaN, // yaw angle
                float.NaN, // pitch rate
                float.NaN, // yaw rate
                (float)MAVLink.GIMBAL_MANAGER_FLAGS.RETRACT,
                0, // unused
                gimbal_device_id);
        }

        public Task<bool> NeutralAsync(byte gimbal_device_id = 0)
        {
            if (!HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_NEUTRAL))
            {
                return Task.FromResult(false);
            }
            return mavint.doCommandAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW,
                float.NaN, // pitch angle
                float.NaN, // yaw angle
                float.NaN, // pitch rate
                float.NaN, // yaw rate
                (float)MAVLink.GIMBAL_MANAGER_FLAGS.NEUTRAL,
                0, // unused
                gimbal_device_id);
        }

        public Task<bool> SetRCYawLockAsync(bool yaw_lock, byte gimbal_device_id = 0)
        {
            if ((yaw_lock && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_LOCK)) ||
                (!yaw_lock && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_FOLLOW)))
            {
                return Task.FromResult(false);
            }

            return mavint.doCommandAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW,
                float.NaN, // pitch angle
                float.NaN, // yaw angle
                float.NaN, // pitch rate
                float.NaN, // yaw rate
                yaw_lock ? (float)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK : 0,
                0, // unused
                gimbal_device_id);
        }

        /// <summary>
        /// Set the attitude of the gimbal with a quaternion. Yaw always reported relative to the earth frame.
        /// </summary>
        /// <param name="q">Gimbal attitude quaternion</param>
        /// <param name="yaw_lock">True if the gimbal should continue to point in this orientation. False if it should follow the yaw of the vehicle.</param>
        /// <param name="gimbal_device_id">Device ID of the gimbal. 0 means all gimbals</param>
        /// <returns></returns>
        public Task<bool> SetAttitudeAsync(Quaternion q, bool yaw_lock, byte gimbal_device_id = 0)
        {
            var pitch = q.get_euler_pitch() * MathHelper.rad2deg;
            var yaw = q.get_euler_yaw() * MathHelper.rad2deg;

            if (!yaw_lock)
            {
                yaw -= cs.yaw;
            }

            return SetAnglesCommandAsync(pitch, yaw, yaw_lock, gimbal_device_id);
        }

        private double wrap_180(double angle)
        {
            while (angle > 180)
            {
                angle -= 360;
            }
            while (angle < -180)
            {
                angle += 360;
            }
            return angle;
        }

        public Task<bool> SetAnglesCommandAsync(double pitch, double yaw, bool yaw_lock, byte gimbal_device_id = 0)
        {
            if (!HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.CAN_POINT_LOCATION_LOCAL) ||
                (pitch != 0 && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_PITCH_AXIS)) ||
                (yaw != 0 && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_AXIS)) ||
                (yaw != 0 && yaw_lock && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_LOCK)) ||
                (yaw != 0 && !yaw_lock && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_FOLLOW)))
            {
                return Task.FromResult(false);
            }

            return mavint.doCommandAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW,
                (float)wrap_180(pitch),
                (float)wrap_180(yaw),
                float.NaN, // pitch rate
                float.NaN, // yaw rate
                yaw_lock ? (float)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK : 0, // flags
                0, // unused
                gimbal_device_id);
        }

        public void SetAnglesStream(float pitch, float yaw, bool yaw_in_earth_frame, byte gimbal_device_id = 0)
        {
            MAVLink.mavlink_gimbal_manager_set_pitchyaw_t set = new MAVLink.mavlink_gimbal_manager_set_pitchyaw_t()
            {
                target_system = (byte)mavint.sysidcurrent,
                target_component = (byte)mavint.compidcurrent,
                gimbal_device_id = gimbal_device_id,
                pitch = pitch,
                yaw = yaw,
                pitch_rate = float.NaN,
                yaw_rate = float.NaN,
                flags = yaw_in_earth_frame ? (uint)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK : 0
            };
            mavint.sendPacket(set, mavint.sysidcurrent, mavint.compidcurrent);
        }

        public Task<bool> SetRatesCommandAsync(float pitchRate, float yawRate, bool yaw_in_earth_frame, byte gimbal_device_id = 0)
        {
            if ((pitchRate != 0 && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_PITCH_AXIS)) ||
                (yawRate != 0 && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_AXIS)) ||
                (yawRate != 0 && yaw_in_earth_frame && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_LOCK)) ||
                (yawRate != 0 && !yaw_in_earth_frame && !HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.HAS_YAW_FOLLOW)))
            {
                return Task.FromResult(false);
            }

            return mavint.doCommandAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW,
                float.NaN, // pitch angle
                float.NaN, // yaw angle
                pitchRate,
                yawRate,
                yaw_in_earth_frame ? (float)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK : 0, // flags
                0, // unused
                gimbal_device_id);
        }

        public void SetRatesStream(float pitchRate, float yawRate, bool yaw_in_earth_frame, byte gimbal_device_id = 0)
        {
            MAVLink.mavlink_gimbal_manager_set_pitchyaw_t set = new MAVLink.mavlink_gimbal_manager_set_pitchyaw_t()
            {
                target_system = (byte)mavint.sysidcurrent,
                target_component = (byte)mavint.compidcurrent,
                gimbal_device_id = gimbal_device_id,
                pitch = float.NaN,
                yaw = float.NaN,
                pitch_rate = pitchRate,
                yaw_rate = yawRate,
                flags = yaw_in_earth_frame ? (uint)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK : 0
            };
            mavint.sendPacket(set, mavint.sysidcurrent, mavint.compidcurrent);
        }

        public Task<bool> SetROILocationAsync(double lat, double lon, double alt = 0, byte gimbal_device_id = 0, MAVLink.MAV_FRAME frame = MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT)
        {
            if (!HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.CAN_POINT_LOCATION_GLOBAL))
            {
                return Task.FromResult(false);
            }

            return mavint.doCommandIntAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_SET_ROI_LOCATION,
                gimbal_device_id,
                0, 0, 0, // unused
                (int)(lat * 1e7),
                (int)(lon * 1e7),
                (float)alt,
                frame: frame);
        }

        public Task<bool> SetROINoneAsync(byte gimbal_device_id = 0)
        {
            return mavint.doCommandAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_SET_ROI_NONE,
                gimbal_device_id,
                0, 0, 0, 0, 0, 0);
        }

        public Task<bool> SetROISysIDAsync(byte sysid, byte gimbal_device_id = 0)
        {
            if (!HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.CAN_POINT_LOCATION_GLOBAL))
            {
                return Task.FromResult(false);
            }

            return mavint.doCommandAsync(
                (byte)mavint.sysidcurrent,
                (byte)mavint.compidcurrent,
                MAVLink.MAV_CMD.DO_SET_ROI_SYSID,
                sysid,
                gimbal_device_id,
                0, 0, 0, 0, 0);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _lifetime.Cancel();
            mavint.OnPacketReceived -= MessagesHandler;
        }
    }
}
