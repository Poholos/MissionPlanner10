using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MissionPlanner.Services;

internal static class SystemAwakeService {
  private const uint EsContinuous = 0x80000000;
  private const uint EsSystemRequired = 0x00000001;
  private static readonly object Sync = new();
  private static Process? _inhibitor;
  private static bool _started;

  internal static void Start() {
    lock (Sync) {
      if (_started) {
        return;
      }
      _started = true;
      if (OperatingSystem.IsWindows()) {
        _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
        return;
      }
      AwakeCommand? command = CommandForCurrentPlatform();
      if (command == null) {
        return;
      }
      try {
        var start = new ProcessStartInfo(command.FileName) {
          UseShellExecute = false,
          CreateNoWindow = true,
          RedirectStandardError = false,
          RedirectStandardOutput = false,
        };
        foreach (string argument in command.Arguments) {
          start.ArgumentList.Add(argument);
        }
        _inhibitor = Process.Start(start);
      } catch {
        _inhibitor = null;
      }
    }
  }

  internal static void Stop() {
    Process? inhibitor;
    lock (Sync) {
      if (!_started) {
        return;
      }
      _started = false;
      inhibitor = _inhibitor;
      _inhibitor = null;
    }
    if (OperatingSystem.IsWindows()) {
      _ = SetThreadExecutionState(EsContinuous);
    }
    if (inhibitor == null) {
      return;
    }
    try {
      if (!inhibitor.HasExited) {
        inhibitor.Kill(entireProcessTree: true);
        inhibitor.WaitForExit(2000);
      }
    } catch {
    } finally {
      inhibitor.Dispose();
    }
  }

  internal static AwakeCommand? BuildLinuxCommand(string inhibitPath, string idleCommandPath) {
    if (!File.Exists(inhibitPath) || !File.Exists(idleCommandPath)) {
      return null;
    }
    return new AwakeCommand(inhibitPath, [
      "--what=sleep",
      "--who=Mission Planner",
      "--why=Ground control station is running",
      "--mode=block",
      "--",
      idleCommandPath,
      "-f",
      "/dev/null",
    ]);
  }

  internal static AwakeCommand BuildMacCommand(string caffeinatePath, int processId) =>
      new(caffeinatePath, ["-i", "-w", processId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

  private static AwakeCommand? CommandForCurrentPlatform() {
    if (OperatingSystem.IsLinux()) {
      string inhibit = FirstExisting("/usr/bin/systemd-inhibit", "/bin/systemd-inhibit");
      string idle = FirstExisting("/usr/bin/tail", "/bin/tail");
      return BuildLinuxCommand(inhibit, idle);
    }
    if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/caffeinate")) {
      return BuildMacCommand("/usr/bin/caffeinate", Environment.ProcessId);
    }
    return null;
  }

  private static string FirstExisting(params string[] paths) {
    foreach (string path in paths) {
      if (File.Exists(path)) {
        return path;
      }
    }
    return string.Empty;
  }

  [DllImport("kernel32.dll")]
  private static extern uint SetThreadExecutionState(uint executionState);
}

internal sealed record AwakeCommand(string FileName, IReadOnlyList<string> Arguments);
