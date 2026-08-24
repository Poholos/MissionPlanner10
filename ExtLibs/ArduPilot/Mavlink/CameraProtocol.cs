using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Core.Geometry;
using GeoAPI.DataStructures;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.ArduPilot.Mavlink
{
    /// <summary>
    /// Handles communication and control for camera operations via MAVLink protocol. 
    /// This includes starting/stopping video capture, taking pictures, and fetching camera settings and status.
    /// </summary>
    public class CameraProtocol : IDisposable
    {
        // Logger for capturing runtime information and errors
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Reference to the parent MAVState, used for MAVLink communication
        private MAVState parent;

        // Tracks whether we have received a `CAMERA_INFORMATION` message yet
        private bool have_camera_information = false;

        private readonly object _leaseLock = new object();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private List<MessageRateLease> _streamingLeases = new List<MessageRateLease>();
        private MessageRateLease _trackingLease;
        private int _desiredRateHz;
        private int _appliedRateHz = -1;
        private int _desiredTrackingRateHz;
        private int _appliedTrackingRateHz;
        private int _started;
        private int _disposed;

        public bool HasCameraInformation => have_camera_information;

        public MAVLink.mavlink_camera_information_t CameraInformation { get; private set; }
        public MAVLink.mavlink_camera_settings_t CameraSettings { get; private set; }
        public MAVLink.mavlink_camera_capture_status_t CameraCaptureStatus { get; private set; }
        public MAVLink.mavlink_camera_fov_status_t CameraFOVStatus { get; private set; }
        public MAVLink.mavlink_camera_tracking_image_status_t CameraTrackingImageStatus { get; private set; }

        public static ConcurrentDictionary<(byte, byte, byte), MAVLink.mavlink_video_stream_information_t> VideoStreams { get; private set; } = new ConcurrentDictionary<(byte, byte, byte), MAVLink.mavlink_video_stream_information_t>();

        public static string GStreamerPipeline(MAVLink.mavlink_video_stream_information_t stream)
        {
            var type = (MAVLink.VIDEO_STREAM_TYPE)stream.type;
            var uri = System.Text.Encoding.UTF8.GetString(stream.uri).Split('\0')[0];

            // Allow a uri that starts with "gst://" to be used directly as a GStreamer pipeline
            // (this is my personal hack to allow for custom pipelines for testing)
            if (uri.StartsWith("gst://"))
            {
                return uri.Substring("gst://".Length);
            }

            // For the UDP transports, extract the port number from the URI. The URI should be only the port number,
            // but we will attempt to handle malformed ones like "udp://127.0.0.1:5600" as well.
            int port = 0;
            if (type == MAVLink.VIDEO_STREAM_TYPE.RTPUDP || type == MAVLink.VIDEO_STREAM_TYPE.MPEG_TS)
            {
                if (!int.TryParse(uri, out port))
                {
                    var match = Regex.Match(uri, ":(\\d+)"); // Match a colon followed by digits
                    if (match.Success)
                    {
                        port = int.Parse(match.Groups[1].Value);
                    }
                }
                if (port < 1 || port > 65535)
                {
                    return "";
                }
            }

            // Otherwise, correctly generate a pipeline based on the stream type
            switch (type)
            {
            case MAVLink.VIDEO_STREAM_TYPE.RTSP:
                uri = "rtsp://" + Regex.Replace(uri, "^.*://", "");
                return $"rtspsrc location={uri} latency=41 udp-reconnect=1 timeout=0 do-retransmission=false ! application/x-rtp ! decodebin3 ! queue leaky=2 ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink sync=false";

            case MAVLink.VIDEO_STREAM_TYPE.RTPUDP:
                // Assume unknown encodings are H264
                string encoding_name = stream.encoding == (byte)MAVLink.VIDEO_STREAM_ENCODING.H265 ? "H265" : "H264";
                return $"udpsrc port={port} buffer-size=90000 ! application/x-rtp,media=(string)video,clock-rate=(int)90000,encoding-name=(string){encoding_name} ! decodebin3 ! queue max-size-buffers=1 leaky=2 ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink sync=false";

            case MAVLink.VIDEO_STREAM_TYPE.TCP_MPEG:
                var match = Regex.Match(uri, @"^(?:.*://)?([^:/]+):(\d+)");
                if (match.Success)
                {
                    return $"tcpclientsrc host={match.Groups[1].Value} port={match.Groups[2].Value} ! decodebin ! queue max-size-buffers=1 leaky=2 ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink sync=false";
                }
                return "";

            case MAVLink.VIDEO_STREAM_TYPE.MPEG_TS:
                return $"udpsrc port={port} buffer-size=90000 ! tsparse ! tsdemux ! decodebin ! queue max-size-buffers=1 leaky=2 ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink sync=false";
            default:
                return "";
            }
        }

        /// <summary>
        /// True if the camera has different modes, like image mode and video mode
        /// </summary>
        public bool HasModes { get => (CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.HAS_MODES) > 0; }

        /// <summary>
        /// True if the camera supports zoom in/out commands.
        /// </summary>
        public bool HasZoom { get => (CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.HAS_BASIC_ZOOM) > 0; }

        /// <summary>
        /// True if the camera supports focus control commands.
        /// </summary>
        public bool HasFocus { get => (CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.HAS_BASIC_FOCUS) > 0; }

        /// <summary>
        /// True if the camera is currently able to capture a video, based on the capabilities and the current mode.
        /// </summary>
        public bool CanCaptureVideo
        {
            get
            {
                // If we don't have video capture at all, return false immediately
                if ((CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.CAPTURE_VIDEO) == 0)
                {
                    return false;
                }
                // If we don't have modes, or if we are in video mode return true
                if (!HasModes || CameraSettings.mode_id == (byte)MAVLink.CAMERA_MODE.VIDEO)
                {
                    return true;
                }
                // If we are in image mode, see if we can capture a video in image mode
                if (CameraSettings.mode_id == (byte)MAVLink.CAMERA_MODE.IMAGE ||
                    CameraSettings.mode_id == (byte)MAVLink.CAMERA_MODE.IMAGE_SURVEY)
                {
                    return (CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.CAN_CAPTURE_VIDEO_IN_IMAGE_MODE) > 0;
                }
                // Unknown mode, assume we cannot capture a video
                return false;
            }
        }

        /// <summary>
        /// True if the camera is currently able to capture an image, based on the capabilities and the current mode.
        /// </summary>
        public bool CanCaptureImage
        {
            get
            {
                // If we don't have image capture at all, return false immediately
                if ((CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.CAPTURE_IMAGE) == 0)
                {
                    return false;
                }
                // If we don't have modes, or we are in image mode, return true;
                // (includes image-survey, even though it's not explicitly mentioned whether manual
                //  image capture is available in this mode)
                if (!HasModes ||
                    CameraSettings.mode_id == (byte)MAVLink.CAMERA_MODE.IMAGE ||
                    CameraSettings.mode_id == (byte)MAVLink.CAMERA_MODE.IMAGE_SURVEY)
                {
                    return true;
                }
                // If we are in video mode, see if we can capture an image in video mode
                if (CameraSettings.mode_id == (byte)MAVLink.CAMERA_MODE.VIDEO)
                {
                    return (CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.CAN_CAPTURE_IMAGE_IN_VIDEO_MODE) > 0;
                }
                // Unknown mode, assume we cannot capture an image
                return false;
            }
        }

        public bool UseFOVStatus { get; set; } = true;

        public float _hfov = float.NaN;
        /// <summary>
        /// Horizontal field of view of the camera, in degrees. Uses the latest received value from the camera if available and `UseFOVStatus` is true.
        /// </summary>
        public float HFOV
        {
            get
            {
                if (!UseFOVStatus || float.IsNaN(CameraFOVStatus.hfov))
                {
                    return _hfov;
                }
                return CameraFOVStatus.hfov;
            }
            set
            {
                _hfov = value;
            }
        }

        public float _vfov = float.NaN;
        public float VFOV
        {
            get
            {
                if (!UseFOVStatus || float.IsNaN(CameraFOVStatus.vfov))
                {
                    return _vfov;
                }
                return CameraFOVStatus.vfov;
            }
            set
            {
                _vfov = value;
            }
        }

        /// <summary>
        /// Initializes camera discovery and asks the target to announce camera information
        /// approximately every 30 seconds.
        /// </summary>
        public async Task StartID(MAVState mavState)
        {
            if (mavState == null)
                throw new ArgumentNullException(nameof(mavState));
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            parent = mavState;
            MAVLinkInterface port = mavState.parent;
            if (port == null)
                return;
            port.OnPacketReceived += ParseMessages;

            const ushort cameraInformationId =
                (ushort)MAVLink.MAVLINK_MSG_ID.CAMERA_INFORMATION;
            const float intervalMicroseconds = 30_000_000;
            int confirmed = 0;
            int subscription = port.SubscribeToPacketType(
                MAVLink.MAVLINK_MSG_ID.MESSAGE_INTERVAL,
                message =>
                {
                    MAVLink.mavlink_message_interval_t interval =
                        message.ToStructure<MAVLink.mavlink_message_interval_t>();
                    if (interval.message_id == cameraInformationId)
                    {
                        Interlocked.Exchange(ref confirmed, 1);
                        log.InfoFormat(
                            "Camera: CAMERA_INFORMATION interval response {0} us",
                            interval.interval_us);
                    }
                    return true;
                }, parent.sysid, parent.compid);

            try
            {
                for (int attempt = 0; attempt < 3 && !have_camera_information &&
                     Volatile.Read(ref confirmed) == 0; attempt++)
                {
                    SendDiscoveryIntervalRequest(port, cameraInformationId,
                        intervalMicroseconds);
                    await Task.Delay(5000, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                port.UnSubscribeToPacketType(subscription);
            }
        }

        private void SendDiscoveryIntervalRequest(MAVLinkInterface port,
            ushort messageId, float intervalMicroseconds)
        {
            try
            {
                ObserveFault(port.doCommandAsync(parent.sysid, parent.compid,
                    MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
                    messageId, intervalMicroseconds,
                    0, 0, 0, 0, 0, false), "camera discovery SET");
                ObserveFault(port.doCommandAsync(parent.sysid, parent.compid,
                    MAVLink.MAV_CMD.GET_MESSAGE_INTERVAL,
                    messageId, 0, 0, 0, 0, 0, 0, false), "camera discovery GET");
            }
            catch (Exception ex)
            {
                log.Debug("Camera discovery request failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Compatibility one-shot request for plugins that used the previous API.
        /// </summary>
        public Task RequestCameraInformationAsync()
        {
            if (parent?.parent == null)
                return Task.CompletedTask;
            Task request = parent.parent.doCommandAsync(parent.sysid, parent.compid,
                MAVLink.MAV_CMD.REQUEST_MESSAGE,
                (float)MAVLink.MAVLINK_MSG_ID.CAMERA_INFORMATION,
                0, 0, 0, 0, 0, 0, false);
            RequestVideoStreamInformation();
            return request;
        }

        /// <summary>
        /// Event handler for OnPacketReceived.
        /// Parses incoming MAVLink messages related to camera operations and updates internal state accordingly.
        /// </summary>
        /// <param name="sender">MAVLink interface</param>
        /// <param name="message">MAVLink message to parse</param>
        public void ParseMessages(object sender, MAVLink.MAVLinkMessage message)
        {
            if (Volatile.Read(ref _disposed) != 0 || parent == null ||
                message.sysid != parent.sysid || message.compid != parent.compid)
                return;

            switch ((MAVLink.MAVLINK_MSG_ID)message.msgid)
            {
            case MAVLink.MAVLINK_MSG_ID.CAMERA_INFORMATION:
                CameraInformation = (MAVLink.mavlink_camera_information_t)message.data;
                if (!have_camera_information)
                {
                    have_camera_information = true;
                    ApplyDesiredRates();
                    if ((CameraInformation.flags &
                         (int)MAVLink.CAMERA_CAP_FLAGS.HAS_VIDEO_STREAM) != 0)
                        RequestVideoStreamWithRetry();
                }
                break;
            case MAVLink.MAVLINK_MSG_ID.CAMERA_SETTINGS:
                CameraSettings = (MAVLink.mavlink_camera_settings_t)message.data;
                break;
            case MAVLink.MAVLINK_MSG_ID.CAMERA_CAPTURE_STATUS:
                CameraCaptureStatus = (MAVLink.mavlink_camera_capture_status_t)message.data;
                break;
            case MAVLink.MAVLINK_MSG_ID.VIDEO_STREAM_INFORMATION:
                var video_stream_info = (MAVLink.mavlink_video_stream_information_t)message.data;
                VideoStreams[(parent.sysid, parent.compid, video_stream_info.stream_id)] = video_stream_info;
                break;
            case MAVLink.MAVLINK_MSG_ID.CAMERA_FOV_STATUS:
                CameraFOVStatus = (MAVLink.mavlink_camera_fov_status_t)message.data;
                break;
            case MAVLink.MAVLINK_MSG_ID.CAMERA_TRACKING_IMAGE_STATUS:
                CameraTrackingImageStatus = (MAVLink.mavlink_camera_tracking_image_status_t)message.data;
                break;
            }
        }

        public void UpdateRateIfChanged(int rateHz)
        {
            lock (_leaseLock)
                _desiredRateHz = Math.Max(0, rateHz);
            if (have_camera_information)
                ApplyDesiredRates();
        }

        [Obsolete("Use UpdateRateIfChanged")]
        public void RequestMessageIntervals(int rateHz)
        {
            UpdateRateIfChanged(rateHz);
        }

        public void SubscribeTracking(int rateHz)
        {
            lock (_leaseLock)
                _desiredTrackingRateHz = Math.Max(0, rateHz);
            if (have_camera_information)
                ApplyDesiredTrackingRate();
        }

        [Obsolete("Use SubscribeTracking")]
        public void RequestTrackingMessageInterval(int rateHz)
        {
            SubscribeTracking(rateHz);
        }

        public void StopTracking()
        {
            MessageRateLease old;
            lock (_leaseLock)
            {
                _desiredTrackingRateHz = 0;
                _appliedTrackingRateHz = 0;
                old = _trackingLease;
                _trackingLease = null;
            }
            old?.Dispose();
        }

        private void ApplyDesiredRates()
        {
            int desired;
            lock (_leaseLock)
            {
                desired = _desiredRateHz;
                if (desired == _appliedRateHz)
                    return;
            }

            if (desired <= 0)
            {
                ReleaseStreamingLeases();
            }
            else
            {
                TakeStreamingLeases(desired);
            }
            ApplyDesiredTrackingRate();
        }

        internal static IReadOnlyList<MAVLink.MAVLINK_MSG_ID> StreamingMessageIds(uint flags)
        {
            var messages = new List<MAVLink.MAVLINK_MSG_ID>
            {
                MAVLink.MAVLINK_MSG_ID.CAMERA_FOV_STATUS
            };
            uint settingsCapabilities =
                (uint)(MAVLink.CAMERA_CAP_FLAGS.HAS_MODES |
                       MAVLink.CAMERA_CAP_FLAGS.HAS_BASIC_ZOOM |
                       MAVLink.CAMERA_CAP_FLAGS.HAS_BASIC_FOCUS);
            if ((flags & settingsCapabilities) != 0)
                messages.Add(MAVLink.MAVLINK_MSG_ID.CAMERA_SETTINGS);

            uint captureCapabilities =
                (uint)(MAVLink.CAMERA_CAP_FLAGS.CAPTURE_VIDEO |
                       MAVLink.CAMERA_CAP_FLAGS.CAPTURE_IMAGE);
            if ((flags & captureCapabilities) != 0)
                messages.Add(MAVLink.MAVLINK_MSG_ID.CAMERA_CAPTURE_STATUS);
            return messages;
        }

        private void TakeStreamingLeases(int rateHz)
        {
            if (parent?.parent == null || Volatile.Read(ref _disposed) != 0)
                return;

            var replacement = new List<MessageRateLease>();
            try
            {
                foreach (MAVLink.MAVLINK_MSG_ID messageId in
                         StreamingMessageIds(CameraInformation.flags))
                {
                    replacement.Add(parent.parent.RateManager.Subscribe(
                        parent.sysid, parent.compid, messageId, rateHz,
                        $"Camera({parent.sysid},{parent.compid})"));
                }
            }
            catch (Exception ex)
            {
                foreach (MessageRateLease lease in replacement)
                    lease.Dispose();
                log.Error("Camera rate subscription failed", ex);
                return;
            }

            List<MessageRateLease> old;
            lock (_leaseLock)
            {
                old = _streamingLeases;
                _streamingLeases = replacement;
                _appliedRateHz = rateHz;
            }
            foreach (MessageRateLease lease in old)
                lease.Dispose();
        }

        private void ReleaseStreamingLeases()
        {
            List<MessageRateLease> old;
            lock (_leaseLock)
            {
                old = _streamingLeases;
                _streamingLeases = new List<MessageRateLease>();
                _appliedRateHz = 0;
            }
            foreach (MessageRateLease lease in old)
                lease.Dispose();
        }

        private void ApplyDesiredTrackingRate()
        {
            int desired;
            lock (_leaseLock)
            {
                desired = _desiredTrackingRateHz;
                if (desired == _appliedTrackingRateHz)
                    return;
            }
            if (desired <= 0)
            {
                StopTracking();
                return;
            }

            MessageRateLease replacement;
            try
            {
                replacement = parent.parent.RateManager.Subscribe(
                    parent.sysid, parent.compid,
                    MAVLink.MAVLINK_MSG_ID.CAMERA_TRACKING_IMAGE_STATUS,
                    desired, $"Camera({parent.sysid},{parent.compid})");
            }
            catch (Exception ex)
            {
                log.Error("Camera tracking rate subscription failed", ex);
                return;
            }

            MessageRateLease old;
            lock (_leaseLock)
            {
                old = _trackingLease;
                _trackingLease = replacement;
                _appliedTrackingRateHz = desired;
            }
            old?.Dispose();
        }

        private void RequestVideoStreamWithRetry()
        {
            if (parent?.parent == null)
                return;
            MAVLinkInterface port = parent.parent;
            byte sysid = parent.sysid;
            byte compid = parent.compid;
            Task.Run(async () =>
            {
                try
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (_lifetime.IsCancellationRequested ||
                            parent?.parent?.BaseStream?.IsOpen != true ||
                            VideoStreams.Keys.Any(key =>
                                key.Item1 == sysid && key.Item2 == compid))
                            return;

                        RequestVideoStreamInformation();
                        await Task.Delay(5000, _lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    log.Debug("Video stream discovery failed: " + ex.Message);
                }
            });
        }

        public void RequestVideoStreamInformation()
        {
            if (parent?.parent == null)
                return;
            try
            {
                ObserveFault(parent.parent.doCommandAsync(
                    parent.sysid, parent.compid,
                    MAVLink.MAV_CMD.REQUEST_MESSAGE,
                    (float)MAVLink.MAVLINK_MSG_ID.VIDEO_STREAM_INFORMATION,
                    0, 0, 0, 0, 0, 0, false), "video stream request");
            }
            catch (Exception ex)
            {
                log.Debug("Video stream request failed: " + ex.Message);
            }
        }

        private static void ObserveFault(Task task, string operation)
        {
            task?.ContinueWith(faulted =>
                log.Debug("Camera " + operation + " failed: " +
                          faulted.Exception?.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _lifetime.Cancel();
            if (parent?.parent != null)
                parent.parent.OnPacketReceived -= ParseMessages;
            ReleaseStreamingLeases();
            StopTracking();

            if (parent != null)
            {
                foreach (var key in VideoStreams.Keys.Where(key =>
                             key.Item1 == parent.sysid && key.Item2 == parent.compid).ToList())
                    VideoStreams.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Sends command to capture one image.
        /// </summary>
        /// <param name="camera">The index of the camera to trigger, defaults to 0 meaning "all cameras".</param>
        private int _image_sequence = 1;
        public Task TakeSinglePictureAsync(int camera = 0)
        {
            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.IMAGE_START_CAPTURE,
                camera,
                0, // Interval
                1, // One image
                _image_sequence++, // Sequence number (prevents retries from accidentally double-triggering)
                0, 0, 0
            );
        }

        /// <summary>
        /// Start capturing images at a specified rate.
        /// </summary>
        /// <param name="interval">Seconds between each image</param>
        /// <param name="camera">Camera index to trigger (optional). Defaults to 0 for "all cameras"</param>
        public Task StartIntervalCaptureAsync(float interval, int camera = 0)
        {
            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.IMAGE_START_CAPTURE,
                camera,
                interval, // Interval
                0, // "Capture forever"
                0, // Sequence (unused in interval, set to 0)
                0, 0, // Reserved (set to 0)
                float.NaN // Reserved (set to NaN)
            );
        }

        /// <summary>
        /// Stop capturing images
        /// </summary>
        /// <param name="camera">Camera index to trigger (optional). Defaults to 0 for "all cameras"</param>
        public Task StopIntervalCaptureAsync(int camera = 0)
        {
            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.IMAGE_STOP_CAPTURE,
                camera,
                float.NaN, float.NaN, float.NaN, // Reserved (set to NaN)
                0, 0, // Reserved (set to 0)
                float.NaN // Reserved (set to NaN)
            );
        }

        /// <summary>
        /// Start capturing video
        /// </summary>
        /// <param name="stream_id">Stream ID to record (optional). Defaults to 0 for "all streams"</param>
        public Task StartRecordingAsync(int stream_id = 0)
        {
            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.VIDEO_START_CAPTURE,
                stream_id,
                float.NaN, // Frequency of CAMERA_CAPTURE_STATUS messages sent while recording (this parameter is not actually implemented in ArduPilot, and we are requesting CAMERA_CAPTURE_STATUS at all times anyway)
                float.NaN, float.NaN, // Reserved (set to NaN)
                0, 0, // Reserved (set to 0)
                float.NaN // Reserved (set to NaN)
            );
        }

        /// <summary>
        /// Stop capturing video
        /// </summary>
        /// <param name="stream_id">Stream ID to stop (optional). Defaults to 0 for "all streams"</param>
        public Task StopRecordingAsync(int stream_id = 0)
        {
            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.VIDEO_STOP_CAPTURE,
                stream_id,
                float.NaN, float.NaN, float.NaN, // Reserved (set to NaN)
                0, 0, // Reserved (set to 0)
                float.NaN // Reserved (set to NaN)
            );
        }

        /// <summary>
        /// Control the camera zoom level.
        /// </summary>
        /// <param name="zoom_level">The zoom level to set. The range of valid values depend on the zoom type.</param>
        /// <param name="zoom_type">The type of zoom to perform</param>
        public Task SetZoomAsync(float zoom_level, MAVLink.CAMERA_ZOOM_TYPE zoom_type = MAVLink.CAMERA_ZOOM_TYPE.ZOOM_TYPE_RANGE)
        {
            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.SET_CAMERA_ZOOM,
                (float)zoom_type,
                zoom_level,
                0, 0, 0, 0, 0
            );
        }

        /// <summary>
        /// Command the camera to track a point in the image.
        /// </summary>
        /// <param name="x">x position in the image, -1 to 1 (positive right)</param>
        /// <param name="y">y position in the image, -1 to 1 (positive down)</param>
        /// <returns></returns>
        public Task<bool> SetTrackingPointAsync(float x, float y)
        {
            // Check capabilities.
            if ((CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.HAS_TRACKING_POINT) == 0)
            {
                return Task.FromResult(false);
            }
            // Map -1:1 to 0:1
            x = (x + 1) / 2;
            y = (y + 1) / 2;
            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.CAMERA_TRACK_POINT,
                x, y,
                0, 0, 0, 0, 0
            );
        }


        /// <summary>
        /// Command the camera to track a rectangle in the image.
        /// </summary>
        /// <param name="x1">x position of one corner of the rectangle, -1 to 1 (positive right)</param>
        /// <param name="y1">y position of one corner of the rectangle, -1 to 1 (positive down)</param>
        /// <param name="x2">x position of the other corner of the rectangle, -1 to 1 (positive right)</param>
        /// <param name="y2">y position of the other corner of the rectangle, -1 to 1 (positive down)</param>
        /// <returns></returns>
        public Task<bool> SetTrackingRectangleAsync(float x1, float y1, float x2, float y2)
        {
            // Check capabilities.
            if ((CameraInformation.flags & (int)MAVLink.CAMERA_CAP_FLAGS.HAS_TRACKING_RECTANGLE) == 0)
            {
                return Task.FromResult(false);
            }

            // Map -1:1 to 0:1
            x1 = (x1 + 1) / 2;
            y1 = (y1 + 1) / 2;
            x2 = (x2 + 1) / 2;
            y2 = (y2 + 1) / 2;

            // Ensure x1 < x2 and y1 < y2
            if (x1 > x2)
            {
                (x2, x1) = (x1, x2);
            }
            if (y1 > y2)
            {
                (y2, y1) = (y1, y2);
            }

            return parent.parent.doCommandAsync(
                parent.sysid, parent.compid,
                MAVLink.MAV_CMD.CAMERA_TRACK_RECTANGLE,
                x1, y1, x2, y2,
                0, 0, 0
            );
        }

        /// <summary>
        /// Calculate the lat/lon/alt-msl of a point in the image, given its x/y position in the image.
        /// <param name="x">x position in the image, -1 to 1 (positive right)</param>
        /// <param name="y">y position in the image, -1 to 1 (positive down)</param>
        /// <returns>PointLatLngAlt with the calculated position, or null if the calculation failed</returns>
        public PointLatLngAlt CalculateImagePointLocation(double x, double y)
        {
            var imagePosition = new PointLatLngAlt(CameraFOVStatus.lat_image * 1e-7, CameraFOVStatus.lon_image * 1e-7, CameraFOVStatus.alt_image * 1e-3);
            if (x == 0 && y == 0)
            {
                return imagePosition;
            }

            var camPosition = new PointLatLngAlt(CameraFOVStatus.lat_camera * 1e-7, CameraFOVStatus.lon_camera * 1e-7, CameraFOVStatus.alt_camera * 1e-3);

            var height = camPosition.Alt - imagePosition.Alt;
            if (height < 0)
            {
                return null;
            }

            var dist = camPosition.GetDistance(imagePosition);
            var down_elevation = Math.Atan2(height, dist); // zero means pointing level, pi/2 is straight down
            down_elevation += y / 2 * VFOV * Math.PI / 180;
            down_elevation = Math.Max(0.0001, down_elevation);
            var out_distance = height * Math.Cos(down_elevation) / Math.Sin(down_elevation);
            out_distance = Math.Min(out_distance, 1e5);

            var side_angle = x / 2 * HFOV * Math.PI / 180;
            var side_distance = Math.Sqrt(out_distance * out_distance + height * height) * Math.Tan(side_angle);

            var bearing = camPosition.GetBearing(imagePosition);
            var pos = camPosition.newpos(bearing, out_distance).newpos(bearing + 90, side_distance);
            pos.Alt = imagePosition.Alt;
            return pos;
        }


        /// <summary>
        /// Calculate the 3D unit vector of a pixel in the camera frame, given its x/y position in the image.
        /// </summary>
        /// <param name="x">x position in the image, -1 to 1 (positive right)</param>
        /// <param name="y">y position in the image, -1 to 1 (positive down)</param>
        /// <returns></returns>
        private Vector3 CalculateImagePointVectorCameraFrame(double x, double y)
        {
            var vector = new Vector3(1, 0, 0); // Camera-frame vector pointing straight ahead
            if (!float.IsNaN(HFOV) && !float.IsNaN(VFOV) && (x != 0 || y != 0))
            {
                var hfov = HFOV * Math.PI / 180;
                var vfov = VFOV * Math.PI / 180;

                vector.y = Math.Tan(x * hfov / 2); // x in the image is toward the right side of the plane (positive y in camera frame)
                vector.z = Math.Tan(y * vfov / 2); // y in the image is down (z in camera frame)
                vector.normalize();
            }

            return vector;
        }

        /// <summary>
        /// Calculate the 3D unit vector of a pixel in the world frame, given its x/y position in the image.
        /// </summary>
        /// <param name="x">x position in the image, -1 to 1 (positive right)</param>
        /// <param name="y">y position in the image, -1 to 1 (positive down)</param>
        /// <returns></returns>
        public Vector3 CalculateImagePointVector(double x, double y)
        {
            if (CameraFOVStatus.q == null)
            {
                return new Vector3(1);
            }
            var v = CalculateImagePointVectorCameraFrame(x, y);
            var q = new Quaternion(CameraFOVStatus.q[0], CameraFOVStatus.q[1], CameraFOVStatus.q[2], CameraFOVStatus.q[3]);
            return q.body_to_earth(v);
        }

        /// <summary>
        /// Calculate a rotation quaternion that will rotate the camera to point at a pixel in the image.
        /// </summary>
        /// <param name="x">x position in the image, -1 to 1 (positive right)</param>
        /// <param name="y">y position in the image, -1 to 1 (positive down)</param>
        /// <returns></returns>
        public Quaternion CalculateImagePointRotation(double x, double y)
        {
            var v1 = CalculateImagePointVectorCameraFrame(0, 0);
            var v2 = CalculateImagePointVectorCameraFrame(x, y);

            if (v1 == -v2)
            {
                return Quaternion.from_axis_angle(new Vector3(0, 0, 1), Math.PI); // 180 degree rotation around z axis
            }

            // The axis of rotation is the cross product of the two vectors
            var axis = v1 % v2;
            if(axis.length() == 0)
            {
                return new Quaternion();
            }
            axis.normalize();

            return Quaternion.from_axis_angle(axis, Math.Acos(v1 * v2));
        }
    }
}
