using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IronPython.Hosting;
using IronPython.Runtime.Exceptions;
using Microsoft.Scripting.Hosting;

namespace MissionPlanner.Services;

/// <summary>
/// Cross-platform host for the local Python scripts exposed by the official Flight Data page.
/// The public scope and helper names intentionally match Mission Planner's legacy Script class.
/// </summary>
public sealed class PythonScriptHost : IDisposable {
  private readonly Func<MAVLinkInterface> _activePort;
  private readonly Func<IReadOnlyList<MAVLinkInterface>> _ports;
  private readonly Func<object?> _flightData;
  private readonly Func<object?> _flightPlanner;
  private CancellationTokenSource? _cancellation;
  private int _running;
  private int _disposed;

  public PythonScriptHost()
      : this(
          () => AppState.comPort,
          () => AppState.Connections.Snapshot().Select(connection => connection.Link).ToArray(),
          () => null,
          () => null) {
  }

  internal PythonScriptHost(
      Func<MAVLinkInterface> activePort,
      Func<IReadOnlyList<MAVLinkInterface>> ports,
      Func<object?> flightData,
      Func<object?> flightPlanner) {
    _activePort = activePort ?? throw new ArgumentNullException(nameof(activePort));
    _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    _flightData = flightData ?? throw new ArgumentNullException(nameof(flightData));
    _flightPlanner = flightPlanner ?? throw new ArgumentNullException(nameof(flightPlanner));
  }

  public event Action<string>? Output;

  public bool IsRunning => Volatile.Read(ref _running) != 0;

  public Task RunFileAsync(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    if (!File.Exists(path)) {
      Emit($"Python script was not found: {path}{Environment.NewLine}");
      return Task.CompletedTask;
    }

    return RunCoreAsync(engine => engine.CreateScriptSourceFromFile(path));
  }

  public Task RunAsync(string code, string sourceName = "<Mission Planner script>") {
    ArgumentNullException.ThrowIfNull(code);
    return RunCoreAsync(engine => engine.CreateScriptSourceFromString(
        code, sourceName, Microsoft.Scripting.SourceCodeKind.File));
  }

  public void Abort() {
    try {
      _cancellation?.Cancel();
    } catch (ObjectDisposedException) {
    }
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }

    Abort();
  }

  private Task RunCoreAsync(Func<ScriptEngine, ScriptSource> sourceFactory) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    if (Interlocked.Exchange(ref _running, 1) != 0) {
      Emit($"Python script is already running.{Environment.NewLine}");
      return Task.CompletedTask;
    }

    var cancellation = new CancellationTokenSource();
    _cancellation = cancellation;
    return Task.Run(() => RunCore(sourceFactory, cancellation.Token), CancellationToken.None);
  }

  private void RunCore(Func<ScriptEngine, ScriptSource> sourceFactory, CancellationToken token) {
    ScriptEngine? engine = null;
    try {
      engine = Python.CreateEngine(new Dictionary<string, object> { ["Debug"] = true });
      ConfigureSearchPaths(engine);
      LoadAssemblies(engine);

      using var outputStream = new EventOutputStream(Emit);
      engine.Runtime.IO.SetOutput(outputStream, Encoding.UTF8);
      engine.Runtime.IO.SetErrorOutput(outputStream, Encoding.UTF8);

      ScriptScope scope = engine.CreateScope();
      var scriptApi = new PythonScriptApi(_activePort, token);
      MAVLinkInterface port = _activePort();
      scope.SetVariable("MainV2", MainV2.instance);
      scope.SetVariable("FlightPlanner", _flightPlanner());
      scope.SetVariable("FlightData", _flightData());
      scope.SetVariable("Ports", _ports());
      scope.SetVariable("MAV", port);
      scope.SetVariable("cs", port.MAV.cs);
      scope.SetVariable("Script", scriptApi);
      scope.SetVariable("mavutil", scriptApi);
      scope.SetVariable("Joystick", AppState.JoystickControl);
      scope.SetVariable("mp_should_abort", (Func<bool>)(() => token.IsCancellationRequested));
      scope.SetVariable("mp_check_abort", (Action)scriptApi.CheckAbort);

      // IronPython runs in-process so scripts keep direct access to live MAVLink objects. Its
      // hosting trace API adds cooperative cancellation at interpreted sequence points, including
      // ordinary loops that do not explicitly call Script.Sleep or mp_check_abort().
      TracebackDelegate trace = null!;
      trace = (_, _, _) => {
        token.ThrowIfCancellationRequested();
        return trace;
      };
      Python.SetTrace(engine, trace);

      sourceFactory(engine).Execute(scope);
      token.ThrowIfCancellationRequested();
      Emit($"Python script finished.{Environment.NewLine}");
    } catch (Exception) when (token.IsCancellationRequested) {
      Emit($"Python script aborted.{Environment.NewLine}");
    } catch (Exception ex) {
      string message = engine?.GetService<ExceptionOperations>().FormatException(ex) ?? ex.ToString();
      Emit(message.EndsWith(Environment.NewLine, StringComparison.Ordinal)
          ? message
          : message + Environment.NewLine);
    } finally {
      try {
        engine?.Runtime.Shutdown();
      } catch {
      }
      Volatile.Write(ref _running, 0);
      CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
      cancellation?.Dispose();
    }
  }

  private static void ConfigureSearchPaths(ScriptEngine engine) {
    var paths = engine.GetSearchPaths();
    string root = AppContext.BaseDirectory;
    AddSearchPath(paths, Path.Combine(root, "Lib.zip"));
    AddSearchPath(paths, Path.Combine(root, "lib"));
    AddSearchPath(paths, root);
    engine.SetSearchPaths(paths);
  }

  private static void AddSearchPath(ICollection<string> paths, string path) {
    if (!paths.Contains(path, StringComparer.Ordinal)) {
      paths.Add(path);
    }
  }

  private static void LoadAssemblies(ScriptEngine engine) {
    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
      if (!assembly.IsDynamic) {
        engine.Runtime.LoadAssembly(assembly);
      }
    }
  }

  private void Emit(string text) => Output?.Invoke(text);

  private sealed class EventOutputStream(Action<string> output) : Stream {
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _characters = new char[4096];

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush() {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) {
      while (count > 0) {
        _decoder.Convert(
            buffer, offset, count,
            _characters, 0, _characters.Length,
            flush: false,
            out int bytesUsed, out int charsUsed, out _);
        if (charsUsed > 0) {
          output(new string(_characters, 0, charsUsed));
        }
        offset += bytesUsed;
        count -= bytesUsed;
      }
    }
  }
}

/// <summary>Mission Planner helpers exposed to Python as both <c>Script</c> and <c>mavutil</c>.</summary>
public sealed class PythonScriptApi {
  private readonly Func<MAVLinkInterface> _activePort;
  private readonly CancellationToken _cancellation;
  private MAVLink.mavlink_rc_channels_override_t _rc = new() {
    chan1_raw = ushort.MaxValue,
    chan2_raw = ushort.MaxValue,
    chan3_raw = ushort.MaxValue,
    chan4_raw = ushort.MaxValue,
    chan5_raw = ushort.MaxValue,
    chan6_raw = ushort.MaxValue,
    chan7_raw = ushort.MaxValue,
    chan8_raw = ushort.MaxValue,
  };

  internal PythonScriptApi(Func<MAVLinkInterface> activePort, CancellationToken cancellation) {
    _activePort = activePort;
    _cancellation = cancellation;
  }

  public enum Conditional {
    NONE = 0,
    LT,
    LTEQ,
    EQ,
    GT,
    GTEQ,
    NEQ,
  }

  public object? mavlink_connection(
      string device,
      int baud = 115200,
      int source_system = 255,
      bool write = false,
      bool append = false,
      bool robust_parsing = true,
      bool notimestamps = false,
      bool input = true) => null;

  public object? recv_match(string? condition = null, string? type = null, bool blocking = false) =>
      null;

  public void CheckAbort() => _cancellation.ThrowIfCancellationRequested();

  public void Sleep(int milliseconds) {
    ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
    if (_cancellation.WaitHandle.WaitOne(milliseconds)) {
      CheckAbort();
    }
  }

  public bool ChangeParam(string parameter, float value) {
    CheckAbort();
    return _activePort().setParam(parameter, value);
  }

  public float GetParam(string parameter) {
    CheckAbort();
    MAVLink.MAVLinkParam? value = _activePort().MAV.param[parameter];
    return value == null ? 0.0f : (float)value;
  }

  public bool ChangeMode(string mode) {
    CheckAbort();
    _activePort().setMode(mode);
    return true;
  }

  public bool WaitFor(string message, int timeout) {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentOutOfRangeException.ThrowIfNegative(timeout);
    long deadline = Environment.TickCount64 + timeout;
    while (!_activePort().MAV.cs.messages.Any(item => item.message.Contains(
        message, StringComparison.Ordinal))) {
      CheckAbort();
      if (Environment.TickCount64 >= deadline) {
        return false;
      }
      Sleep(5);
    }
    return true;
  }

  public bool SendRC(int channel, short pwm, bool sendnow) {
    CheckAbort();
    MAVLinkInterface port = _activePort();
    ushort value = checked((ushort)pwm);
    switch (channel) {
      case 1:
        port.MAV.cs.rcoverridech1 = pwm;
        _rc.chan1_raw = value;
        break;
      case 2:
        port.MAV.cs.rcoverridech2 = pwm;
        _rc.chan2_raw = value;
        break;
      case 3:
        port.MAV.cs.rcoverridech3 = pwm;
        _rc.chan3_raw = value;
        break;
      case 4:
        port.MAV.cs.rcoverridech4 = pwm;
        _rc.chan4_raw = value;
        break;
      case 5:
        port.MAV.cs.rcoverridech5 = pwm;
        _rc.chan5_raw = value;
        break;
      case 6:
        port.MAV.cs.rcoverridech6 = pwm;
        _rc.chan6_raw = value;
        break;
      case 7:
        port.MAV.cs.rcoverridech7 = pwm;
        _rc.chan7_raw = value;
        break;
      case 8:
        port.MAV.cs.rcoverridech8 = pwm;
        _rc.chan8_raw = value;
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(channel), channel,
            "Mission Planner Python RC override supports channels 1 through 8.");
    }

    _rc.target_component = port.MAV.compid;
    _rc.target_system = port.MAV.sysid;
    if (sendnow) {
      port.sendPacket(_rc, _rc.target_system, _rc.target_component);
      Sleep(20);
      port.sendPacket(_rc, _rc.target_system, _rc.target_component);
    }
    return true;
  }
}
