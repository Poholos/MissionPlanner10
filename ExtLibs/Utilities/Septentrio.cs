using log4net;
using MissionPlanner.Comms;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Class to interact with Septentrio receivers.
    /// </summary>
    public class Septentrio
    {
        private sealed class ReceiverState
        {
            internal string ActivePort = DefaultOutputPorts;
        }

        private static readonly object ReceiverStatesSync = new object();
        private static readonly ConditionalWeakTable<ICommsSerial, ReceiverState> ReceiverStates =
            new ConditionalWeakTable<ICommsSerial, ReceiverState>();
        private static readonly Regex ReceiverPrompt = new Regex(
            @"(?:^|[\r\n])\s*(COM\d+|USB\d+)\s*>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// An exception representing a missing acknowledgement.
        /// </summary>
        public class FailedAckException : Exception { }

        /// <summary>
        /// Selection of messages from what MSM level to output.
        /// </summary>
        public enum RTCMLevel
        {
            /// <summary>
            /// MSM3 messages.
            /// </summary>
            Lite = 3,

            /// <summary>
            /// MSM4 messages.
            /// </summary>
            Basic = 4,

            /// <summary>
            /// MSM7 messages.
            /// </summary>
            Full = 7,
        }

        /// <summary>
        /// Flags to choose what constellations to output RTCM messages for.
        /// </summary>
        [Flags]
        public enum RTCMSignals
        {
            /// <summary>
            /// Output RTCM messages for no constellation.
            /// </summary>
            None =    0b_0000_0000,

            /// <summary>
            /// Output RTCM messages for the GPS constellation.
            /// </summary>
            Gps =     0b_0000_0001,

            /// <summary>
            /// Output RTCM messages for the GLONASS constellation.
            /// </summary>
            Glonass = 0b_0000_0010,

            /// <summary>
            /// Output RTCM messages for the BeiDou constellation.
            /// </summary>
            Beidou =  0b_0000_0100,

            /// <summary>
            /// Output RTCM messages for the Galileo constellation.
            /// </summary>
            Galileo = 0b_0000_1000,
        }

        /// <summary>
        /// Configure the receiver connected on `receiverPort` as a base station.
        /// </summary>
        /// <exception cref="FailedAckException" />
        /// <exception cref="IOException" />
        public static async Task ConfigureBaseReceiver(ICommsSerial receiverPort)
        {
            if (receiverPort == null)
                throw new ArgumentNullException(nameof(receiverPort));

            ResetReceiverState(receiverPort);
            await receiverPort.BaseStream.FlushAsync();

            receiverPort.BaudRate = 115200;
            receiverPort.ReadTimeout = 200;
            receiverPort.WriteTimeout = 200;

            string activePort = await ConfigureBaudAndDetectPort(receiverPort);
            log.Info("Detected active Septentrio port: " + activePort);

            await SendAck(receiverPort, "setPVTMode,Static,All,Auto\n");
            await SendAck(receiverPort,
                $"setDataInOut,{activePort},Auto,+RTCMv3\n");
        }

        /// <summary>
        /// Set the fixed base position for the receiver.
        /// </summary>
        /// <exception cref="FailedAckException" />
        public static async Task SetBasePosition(ICommsSerial receiverPort, float latitude, float longitude, float altitude)
        {
            await receiverPort.BaseStream.FlushAsync();

            await SendAck(receiverPort, "setStaticPosGeodetic,Geodetic1," + latitude.ToString("0.000000000", System.Globalization.CultureInfo.InvariantCulture) + "," + longitude.ToString("0.000000000", System.Globalization.CultureInfo.InvariantCulture) + "," + altitude.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ",WGS84" + "\n");
            await SendAck(receiverPort, "setPVTMode,Static,,Geodetic1\n");
        }

        /// <summary>
        /// Set the base position for the receiver to be automatically calculated.
        /// </summary>
        /// <exception cref="FailedAckException" />
        public static async Task SetAutoBasePosition(ICommsSerial receiverPort)
        {
            await receiverPort.BaseStream.FlushAsync();

            await SendAck(receiverPort, "setPVTMode,Static,,auto\n");
        }

        /// <summary>
        /// Configure the baud rate of the serial port. In case the receiver is connected over serial, this automatically sets the correct baud rate.
        /// </summary>
        /// <exception cref="FailedAckException" />
        private static async Task<string> ConfigureBaudAndDetectPort(ICommsSerial receiverPort)
        {
            bool receiverAcknowledged = false;
            string activePort = DefaultOutputPorts;

            // All the baud rates we expect the receiver could be running at
            var bauds = new[] { receiverPort.BaudRate, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800 };

            foreach (var baud in bauds)
            {
                receiverPort.BaudRate = baud;
                
                // Try to set the port settings on a best effort basis
                try
                {
                    string detectedPort = await TryDetectPort(receiverPort);
                    if (detectedPort != null)
                    {
                        activePort = detectedPort;
                        SetActivePort(receiverPort, activePort);
                        if (activePort.StartsWith("COM", StringComparison.Ordinal))
                        {
                            await SendAck(receiverPort,
                                $"setCOMSettings,{activePort},baud{DefaultBaudrate},bits8,No,bit1,none\n");
                        }
                    }
                    else
                    {
                        await SendAck(receiverPort,
                            $"setCOMSettings,{DefaultOutputPorts},baud{DefaultBaudrate},bits8,No,bit1,none\n");
                    }
                    receiverAcknowledged = true;
                    break;
                } catch { }
            }

            if (!receiverAcknowledged)
                throw new FailedAckException();

            receiverPort.BaudRate = DefaultBaudrate;
            return activePort;
        }

        /// <summary>
        /// Set the level of generated RTCM messages.
        /// </summary>
        /// <exception cref="FailedAckException" />
        public static Task SetEnabledRTCM(ICommsSerial receiverPort, RTCMLevel level, RTCMSignals signals)
        {
            if (receiverPort == null)
                throw new ArgumentNullException(nameof(receiverPort));

            int messageLevel;
            string messages = "RTCM1006+RTCM1033+RTCM1230";
            
            switch (level)
            {
                case RTCMLevel.Lite:
                    messageLevel = 3;
                    break;
                case RTCMLevel.Basic:
                    messageLevel = 4;
                    break;
                case RTCMLevel.Full:
                default:
                    messageLevel = 7;
                    break;
            }

            if ((signals & RTCMSignals.Gps) == RTCMSignals.Gps)
                messages += "+RTCM107" + messageLevel;
            if ((signals & RTCMSignals.Glonass) == RTCMSignals.Glonass)
                messages += "+RTCM108" + messageLevel;
            if ((signals & RTCMSignals.Galileo) == RTCMSignals.Galileo)
                messages += "+RTCM109" + messageLevel;
            if ((signals & RTCMSignals.Beidou) == RTCMSignals.Beidou)
                messages += "+RTCM112" + messageLevel;

            return SendAck(receiverPort,
                $"setRTCMv3Output,{GetActivePort(receiverPort)},{messages}\n");
        }

        /// <summary>
        /// Detect the receiver-side port represented by the current serial connection.
        /// The result is cached only for this connection object.
        /// </summary>
        public static async Task<string> DetectPort(ICommsSerial receiverPort)
        {
            if (receiverPort == null)
                throw new ArgumentNullException(nameof(receiverPort));

            string detectedPort = await TryDetectPort(receiverPort);
            if (detectedPort == null)
                return GetActivePort(receiverPort);

            SetActivePort(receiverPort, detectedPort);
            return detectedPort;
        }

        internal static string TryParseActivePort(string response)
        {
            if (string.IsNullOrEmpty(response))
                return null;

            Match match = ReceiverPrompt.Match(response);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        private static async Task<string> TryDetectPort(ICommsSerial receiverPort)
        {
            if (receiverPort.BytesToRead > 0)
                receiverPort.DiscardInBuffer();

            await receiverPort.BaseStream.FlushAsync();
            byte[] command = Encoding.ASCII.GetBytes("gecm\n");
            receiverPort.Write(command, 0, command.Length);

            var response = new StringBuilder();
            byte[] buffer = new byte[256];
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < AckTimeout)
            {
                int available = receiverPort.BytesToRead;
                if (available > 0)
                {
                    int read = receiverPort.Read(buffer, 0, Math.Min(available, buffer.Length));
                    if (read > 0)
                    {
                        response.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        string detectedPort = TryParseActivePort(response.ToString());
                        if (detectedPort != null)
                            return detectedPort;

                        if (response.Length > MaxResponseBytes)
                            response.Remove(0, response.Length - MaxResponseBytes);
                    }
                }

                await Task.Delay(PollIntervalMilliseconds);
            }

            return null;
        }

        /// <summary>
        /// Set the interval of generated RTCM messages.
        /// </summary>
        /// <exception cref="FailedAckException" />
        public static Task SetRTCMInterval(ICommsSerial receiverPort, float interval)
        {
            return SendAck(receiverPort, "setRTCMv3Interval,MSM3+MSM4+MSM7+RTCM1005|6+RTCM1033+RTCM1230," + interval.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        /// <summary>
        /// Send a command to the receiver on `receiverPort` and confirm its acknowledgement.
        /// </summary>
        /// <exception cref="FailedAckException" />
        private static async Task SendAck(ICommsSerial receiverPort, String command)
        {
            await receiverPort.BaseStream.FlushAsync();
            byte[] commandBytes = Encoding.ASCII.GetBytes(command);
            receiverPort.Write(commandBytes, 0, commandBytes.Length);

            string acknowledgement = command.TrimEnd('\r', '\n');
            var response = new StringBuilder();
            byte[] buffer = new byte[256];
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < AckTimeout)
            {
                int available = receiverPort.BytesToRead;
                if (available > 0)
                {
                    int read = receiverPort.Read(buffer, 0, Math.Min(available, buffer.Length));
                    if (read > 0)
                    {
                        response.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (response.ToString().IndexOf(
                                acknowledgement, StringComparison.OrdinalIgnoreCase) >= 0)
                            return;

                        if (response.Length > MaxResponseBytes)
                            response.Remove(0, response.Length - MaxResponseBytes);
                    }
                }

                await Task.Delay(PollIntervalMilliseconds);
            }

            log.Error("Waiting for command acknowledgement timed out");
            throw new FailedAckException();
        }

        private static string GetActivePort(ICommsSerial receiverPort)
        {
            lock (ReceiverStatesSync)
                return ReceiverStates.GetOrCreateValue(receiverPort).ActivePort;
        }

        private static void SetActivePort(ICommsSerial receiverPort, string activePort)
        {
            lock (ReceiverStatesSync)
                ReceiverStates.GetOrCreateValue(receiverPort).ActivePort = activePort;
        }

        private static void ResetReceiverState(ICommsSerial receiverPort)
        {
            lock (ReceiverStatesSync)
            {
                ReceiverStates.Remove(receiverPort);
                ReceiverStates.Add(receiverPort, new ReceiverState());
            }
        }

        private static readonly ILog log = LogManager.GetLogger(typeof(Septentrio));

        /// <summary>
        /// The maximum time to wait for the receiver to acknowledge a message.
        /// If the receiver didn't acknowledge a message in this time, we assume it wasn't received correctly.
        /// </summary>
        private const int AckTimeout = 1000;
        private const int PollIntervalMilliseconds = 20;
        private const int MaxResponseBytes = 4096;
        private const string DefaultOutputPorts = "USB1+USB2+COM1+COM2";

        /// <summary>
        /// The default baud rate for Septentrio receivers.
        /// </summary>
        public const int DefaultBaudrate = 115200;
    }
}
