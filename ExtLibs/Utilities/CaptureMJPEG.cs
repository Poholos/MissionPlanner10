using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using log4net;

namespace MissionPlanner.Utilities
{
    public class CaptureMJPEG
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object Sync = new object();
        private static readonly object LifecycleSync = new object();

        private static Thread asyncthread;
        private static HttpWebRequest activeRequest;
        private static volatile bool running;

        public static string URL = @"http://127.0.0.1:56781/map.jpg";

        private static DateTime lastimage = DateTime.Now;
        private static int fps;
        private static event EventHandler<Bitmap> _onNewImage;

        public static event EventHandler<Bitmap> onNewImage
        {
            add { _onNewImage += value; }
            remove { _onNewImage -= value; }
        }

        public static void runAsync()
        {
            lock (LifecycleSync)
            {
                Thread previous;
                HttpWebRequest request;
                lock (Sync)
                {
                    running = false;
                    previous = asyncthread;
                    request = activeRequest;
                }

                AbortRequest(request);
                if (previous != null && previous != Thread.CurrentThread &&
                    previous.IsAlive && !previous.Join(TimeSpan.FromSeconds(2)))
                {
                    log.Warn("The previous MJPEG reader did not stop; refusing to start a duplicate reader.");
                    return;
                }

                lock (Sync)
                {
                    running = true;
                    asyncthread = new Thread(getUrl)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = "mjpg stream reader"
                    };
                    asyncthread.Start();
                }
            }
        }

        public static void Stop()
        {
            HttpWebRequest request;
            lock (Sync)
            {
                running = false;
                request = activeRequest;
            }
            AbortRequest(request);
        }

        public static string ReadLine(BinaryReader br)
        {
            if (br == null)
                throw new ArgumentNullException(nameof(br));

            return MjpegMultipartReader.ReadAsciiLine(br, MjpegMultipartReader.MaxHeaderLineBytes)
                   ?? string.Empty;
        }

        private static void getUrl()
        {
            while (running)
            {
                HttpWebRequest request = null;
                try
                {
#pragma warning disable SYSLIB0014
                    request = (HttpWebRequest)WebRequest.Create(URL);
#pragma warning restore SYSLIB0014
                    request.Method = "GET";
                    request.KeepAlive = true;
                    request.AllowReadStreamBuffering = false;
                    request.AutomaticDecompression =
                        DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    request.Headers.Add("Accept-Encoding", "gzip,deflate");
                    request.Accept = "multipart/x-mixed-replace";
                    request.Timeout = 10000;
                    request.ReadWriteTimeout = 10000;

                    lock (Sync)
                    {
                        if (!running)
                            return;
                        activeRequest = request;
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var dataStream = response.GetResponseStream())
                    {
                        log.Debug(response.StatusDescription);
                        if (dataStream == null)
                            throw new InvalidDataException("MJPEG response does not contain a stream.");

                        try { dataStream.ReadTimeout = 10000; }
                        catch (InvalidOperationException) { }

                        using (var reader = new BinaryReader(dataStream))
                        {
                            var multipart = new MjpegMultipartReader(
                                reader, response.Headers["Content-Type"] ?? response.ContentType);
                            byte[] jpeg;
                            while (running && multipart.TryReadFrame(out jpeg))
                                PublishJpeg(jpeg);
                        }
                    }
                }
                catch (WebException ex)
                {
                    if (running)
                        log.Error(ex);
                }
                catch (Exception ex)
                {
                    if (running)
                        log.Error(ex);
                }
                finally
                {
                    lock (Sync)
                    {
                        if (ReferenceEquals(activeRequest, request))
                            activeRequest = null;
                    }
                    Publish(null);
                }

                if (running)
                    Thread.Sleep(250);
            }
        }

        private static void PublishJpeg(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0)
                return;

            try
            {
                using (var stream = new MemoryStream(jpeg, false))
                using (var bitmap = new Bitmap(stream))
                {
                    fps++;
                    if (lastimage.Second != DateTime.Now.Second)
                    {
                        log.Debug("MJPEG " + fps);
                        fps = 0;
                        lastimage = DateTime.Now;
                    }

                    Publish((Bitmap)bitmap.Clone());
                }
            }
            catch (Exception ex)
            {
                log.Info(ex);
            }
        }

        private static void Publish(Bitmap bitmap)
        {
            EventHandler<Bitmap> handlers = _onNewImage;
            if (handlers == null)
                return;

            foreach (EventHandler<Bitmap> handler in handlers.GetInvocationList())
            {
                try { handler(null, bitmap); }
                catch (Exception ex) { log.Warn("MJPEG frame subscriber failed", ex); }
            }
        }

        private static void AbortRequest(HttpWebRequest request)
        {
            if (request == null)
                return;

            try { request.Abort(); }
            catch (Exception ex) { log.Debug("Unable to abort MJPEG request", ex); }
        }
    }

    internal sealed class MjpegMultipartReader
    {
        internal const int MaxHeaderLineBytes = 16 * 1024;
        internal const int DefaultMaxFrameBytes = 32 * 1024 * 1024;
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBoundaryBytes = 256;

        private readonly BinaryReader _reader;
        private readonly string _boundary;
        private readonly int _maxFrameBytes;
        private bool _atHeaders;
        private bool _completed;

        internal MjpegMultipartReader(BinaryReader reader, string contentType,
            int maxFrameBytes = DefaultMaxFrameBytes)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            if (maxFrameBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
            _maxFrameBytes = maxFrameBytes;

            _boundary = ExtractBoundary(contentType);
            if (string.IsNullOrEmpty(_boundary))
            {
                _boundary = DetectBoundaryFromBody(reader);
                _atHeaders = true;
            }
        }

        internal bool TryReadFrame(out byte[] frame)
        {
            frame = null;
            if (_completed)
                return false;

            if (!_atHeaders && !SeekToBoundary())
            {
                _completed = true;
                return false;
            }
            _atHeaders = false;

            Dictionary<string, string> headers = ReadHeaders();
            string lengthText;
            if (headers.TryGetValue("Content-Length", out lengthText))
            {
                int length;
                if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out length) || length <= 0)
                {
                    throw new InvalidDataException("Invalid MJPEG Content-Length: " + lengthText);
                }
                if (length > _maxFrameBytes)
                    throw new InvalidDataException("MJPEG frame exceeds the configured size limit.");

                frame = ReadExact(length);
                return true;
            }

            frame = ReadUntilNextBoundary();
            return true;
        }

        internal static string ExtractBoundary(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
                return null;

            string[] parts = contentType.Split(';');
            for (int index = 1; index < parts.Length; index++)
            {
                string part = parts[index].Trim();
                int equals = part.IndexOf('=');
                if (equals <= 0 || !part.Substring(0, equals).Trim().Equals(
                        "boundary", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = part.Substring(equals + 1).Trim();
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                if (value.Length == 0 || value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidDataException("Invalid MJPEG boundary.");
                return ValidateBoundary(value);
            }

            return null;
        }

        internal static string DetectBoundaryFromBody(BinaryReader reader)
        {
            string line;
            while ((line = ReadAsciiLine(reader, MaxHeaderLineBytes)) != null)
            {
                if (!line.StartsWith("--", StringComparison.Ordinal) || line.Length <= 2)
                    continue;

                string token = line.Substring(2);
                if (token.EndsWith("--", StringComparison.Ordinal))
                    token = token.Substring(0, token.Length - 2);
                if (token.Length > 0)
                    return ValidateBoundary(token);
            }

            throw new EndOfStreamException("Could not detect an MJPEG boundary in the response.");
        }

        private static string ValidateBoundary(string boundary)
        {
            if (Encoding.ASCII.GetByteCount(boundary) > MaxBoundaryBytes)
                throw new InvalidDataException("MJPEG boundary exceeds the configured size limit.");
            for (int index = 0; index < boundary.Length; index++)
            {
                if (boundary[index] < 0x20 || boundary[index] > 0x7e)
                    throw new InvalidDataException("MJPEG boundary contains invalid characters.");
            }
            return boundary;
        }

        internal static string ReadAsciiLine(BinaryReader reader, int maxBytes)
        {
            var bytes = new List<byte>();
            while (bytes.Count <= maxBytes)
            {
                int value;
                try { value = reader.ReadByte(); }
                catch (EndOfStreamException) { value = -1; }

                if (value < 0)
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                if (value == '\n')
                {
                    if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r')
                        bytes.RemoveAt(bytes.Count - 1);
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                bytes.Add((byte)value);
            }

            throw new InvalidDataException("MJPEG header line exceeds the configured size limit.");
        }

        private bool SeekToBoundary()
        {
            string delimiter = "--" + _boundary;
            string line;
            while ((line = ReadAsciiLine(_reader, MaxHeaderLineBytes)) != null)
            {
                if (line.Equals(delimiter, StringComparison.Ordinal))
                    return true;
                if (line.Equals(delimiter + "--", StringComparison.Ordinal))
                    return false;
            }
            return false;
        }

        private Dictionary<string, string> ReadHeaders()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int totalBytes = 0;
            string line;
            while ((line = ReadAsciiLine(_reader, MaxHeaderLineBytes)) != null)
            {
                totalBytes += line.Length + 2;
                if (totalBytes > MaxHeaderBytes)
                    throw new InvalidDataException("MJPEG headers exceed the configured size limit.");
                if (line.Length == 0)
                    return headers;

                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            throw new EndOfStreamException("MJPEG stream ended while reading part headers.");
        }

        private byte[] ReadExact(int length)
        {
            var bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = _reader.Read(bytes, offset, length - offset);
                if (read <= 0)
                    throw new EndOfStreamException(
                        "MJPEG stream ended before the Content-Length body was complete.");
                offset += read;
            }
            return bytes;
        }

        private byte[] ReadUntilNextBoundary()
        {
            byte[] separator = Encoding.ASCII.GetBytes("\n--" + _boundary);
            int[] failure = BuildFailureTable(separator);
            int matched = 0;

            using (var frame = new MemoryStream())
            {
                while (true)
                {
                    int value;
                    try { value = _reader.ReadByte(); }
                    catch (EndOfStreamException)
                    {
                        throw new EndOfStreamException(
                            "MJPEG stream ended before the next multipart boundary.");
                    }

                    byte current = (byte)value;
                    frame.WriteByte(current);
                    while (matched > 0 && current != separator[matched])
                        matched = failure[matched - 1];
                    if (current == separator[matched])
                        matched++;

                    if (matched == separator.Length)
                    {
                        frame.SetLength(frame.Length - separator.Length);
                        if (frame.Length > 0)
                        {
                            byte[] buffer = frame.GetBuffer();
                            if (buffer[frame.Length - 1] == '\r')
                                frame.SetLength(frame.Length - 1);
                        }

                        string suffix = ReadAsciiLine(_reader, MaxHeaderLineBytes) ?? string.Empty;
                        _completed = suffix.Trim().Equals("--", StringComparison.Ordinal);
                        _atHeaders = !_completed;
                        return frame.ToArray();
                    }

                    if (frame.Length > _maxFrameBytes + separator.Length)
                        throw new InvalidDataException("MJPEG frame exceeds the configured size limit.");
                }
            }
        }

        private static int[] BuildFailureTable(byte[] pattern)
        {
            var table = new int[pattern.Length];
            int matched = 0;
            for (int index = 1; index < pattern.Length; index++)
            {
                while (matched > 0 && pattern[index] != pattern[matched])
                    matched = table[matched - 1];
                if (pattern[index] == pattern[matched])
                    matched++;
                table[index] = matched;
            }
            return table;
        }
    }
}
