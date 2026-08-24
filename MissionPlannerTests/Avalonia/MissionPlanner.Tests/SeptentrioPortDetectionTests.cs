using System.Text;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class SeptentrioPortDetectionTests {
  [Theory]
  [InlineData("$R: gecm\r\n  EchoMessage [...]\r\nUSB1>\r\n", "USB1")]
  [InlineData("noise\n com10   >\n", "COM10")]
  [InlineData("USB2>\r\n", "USB2")]
  public void Parses_receiver_prompt_ports(string response, string expected) {
    Assert.Equal(expected, Septentrio.TryParseActivePort(response));
  }

  [Fact]
  public void Does_not_accept_port_like_text_outside_a_receiver_prompt() {
    Assert.Null(Septentrio.TryParseActivePort("status: connected to COM3 but no prompt"));
    Assert.Null(Septentrio.TryParseActivePort("XCOM3>"));
  }

  [Fact]
  public async Task Detected_ports_are_scoped_to_each_receiver_connection() {
    using var first = new SeptentrioSerial("USB2");
    using var second = new SeptentrioSerial("COM10");

    Assert.Equal("USB2", await Septentrio.DetectPort(first));
    Assert.Equal("COM10", await Septentrio.DetectPort(second));

    await Septentrio.SetEnabledRTCM(
        first, Septentrio.RTCMLevel.Basic, Septentrio.RTCMSignals.Gps);
    await Septentrio.SetEnabledRTCM(
        second, Septentrio.RTCMLevel.Full, Septentrio.RTCMSignals.Galileo);

    Assert.Contains(first.Commands,
        command => command.StartsWith("setRTCMv3Output,USB2,", StringComparison.Ordinal));
    Assert.Contains(second.Commands,
        command => command.StartsWith("setRTCMv3Output,COM10,", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Missing_acknowledgement_uses_a_bounded_timeout() {
    using var serial = new SeptentrioSerial("USB1", respondToCommands: false);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    await Assert.ThrowsAsync<Septentrio.FailedAckException>(() =>
        Septentrio.SetEnabledRTCM(
            serial, Septentrio.RTCMLevel.Basic, Septentrio.RTCMSignals.Gps));

    Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(3));
  }

  private sealed class SeptentrioSerial(
      string promptPort, bool respondToCommands = true) : ICommsSerial {
    private readonly Queue<byte> _incoming = new();
    private readonly object _sync = new();

    internal List<string> Commands { get; } = [];

    public Stream BaseStream { get; } = new MemoryStream();
    public int BaudRate { get; set; } = Septentrio.DefaultBaudrate;
    public int BytesToRead {
      get {
        lock (_sync) {
          return _incoming.Count;
        }
      }
    }
    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public bool IsOpen { get; private set; } = true;
    public string PortName { get; set; } = "TEST";
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }

    public void Write(byte[] buffer, int offset, int count) {
      string command = Encoding.ASCII.GetString(buffer, offset, count);
      Commands.Add(command);
      if (!respondToCommands) {
        return;
      }
      string response = command == "gecm\n"
          ? "$R: gecm\r\n  EchoMessage [...]\r\n" + promptPort + ">\r\n"
          : "$R: " + command.TrimEnd('\r', '\n') + "\r\n" + promptPort + ">\r\n";
      lock (_sync) {
        foreach (byte value in Encoding.ASCII.GetBytes(response)) {
          _incoming.Enqueue(value);
        }
      }
    }

    public int Read(byte[] buffer, int offset, int count) {
      lock (_sync) {
        int read = Math.Min(count, _incoming.Count);
        for (int index = 0; index < read; index++) {
          buffer[offset + index] = _incoming.Dequeue();
        }
        return read;
      }
    }

    public void DiscardInBuffer() {
      lock (_sync) {
        _incoming.Clear();
      }
    }

    public void Close() => IsOpen = false;
    public void Open() => IsOpen = true;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public string ReadLine() => "";
    public void Write(string text) => Write(Encoding.ASCII.GetBytes(text), 0, text.Length);
    public void WriteLine(string text) => Write(text + "\n");
    public void toggleDTR() { }

    public void Dispose() {
      IsOpen = false;
      BaseStream.Dispose();
    }
  }
}
