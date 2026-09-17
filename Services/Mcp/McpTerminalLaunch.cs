using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services.Mcp;

/// <summary>A private, one-use handoff gives a real terminal the agent's argv and environment.</summary>
internal sealed class McpTerminalLaunch : IDisposable {
  internal sealed record Request(string Executable, McpAgentKind Kind, string Endpoint, string Token, string Directory, string Prompt);
  private readonly McpAgentSessionFiles _files;
  internal string HandoffPath { get; }
  internal McpTerminalLaunch(McpAgent agent, Uri endpoint, string token, string directory, string prompt) {
    if (agent.Kind is not (McpAgentKind.CodexCli or McpAgentKind.ClaudeCode)) { throw new ArgumentException("Select a terminal agent."); }
    if (!Path.IsPathRooted(directory) || !System.IO.Directory.Exists(directory)) { throw new ArgumentException("Choose an existing absolute working directory."); }
    _files = new(McpAgentKind.CodexCli, endpoint, token);
    HandoffPath = Path.Combine(_files.DirectoryPath, "launch.json");
    try {
      File.WriteAllText(HandoffPath, JsonSerializer.Serialize(new Request(agent.Executable, agent.Kind, endpoint.AbsoluteUri, token, directory, prompt)));
      if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(HandoffPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    } catch { _files.Dispose(); throw; }
  }

  internal static (string Executable, string[] Arguments) HostCommand(params string[] args) {
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find Mission Planner executable.");
    return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? (executable, new[] { typeof(Program).Assembly.Location }.Concat(args).ToArray()) : (executable, args);
  }
  internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
  internal static ProcessStartInfo TerminalStart(string terminal, string host, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(terminal) { UseShellExecute = false };
    string resolved = new FileInfo(terminal).ResolveLinkTarget(true)?.Name ?? Path.GetFileName(terminal);
    foreach (string option in resolved switch {
      "gnome-terminal" or "gnome-terminal.wrapper" => new[] { "--wait", "--" },
      "terminator" => new[] { "--no-dbus", "-x" },
      "konsole" => new[] { "--separate", "-e" },
      "xfce4-terminal" or "xfce4-terminal.wrapper" => new[] { "--disable-server", "--execute" },
      "mate-terminal" or "mate-terminal.wrapper" => new[] { "--disable-factory", "--execute" },
      "wt.exe" => new[] { "new-tab", "--" },
      _ => new[] { "-e" },
    }) { start.ArgumentList.Add(option); }
    start.ArgumentList.Add(host);
    foreach (string arg in arguments) { start.ArgumentList.Add(arg); }
    return start;
  }
  internal async Task LaunchAsync(CancellationToken ct) {
    ct.ThrowIfCancellationRequested();
    var command = HostCommand("--mcp-terminal-session", HandoffPath);
    if (OperatingSystem.IsWindows()) {
      StartWindowsConsole(command.Executable, command.Arguments); return;
    }
    ProcessStartInfo start;
    if (OperatingSystem.IsMacOS()) {
      // argv passes only the one-use file path; neither token nor user prompt enters AppleScript source.
      start = new("/usr/bin/osascript") { UseShellExecute = false };
      foreach (string arg in new[] { "-e", "on run argv\ntell application \"Terminal\"\nactivate\ndo script (item 1 of argv)\nend tell\nend run",
          "--", string.Join(" ", new[] { command.Executable }.Concat(command.Arguments).Select(ShellQuote)) }) { start.ArgumentList.Add(arg); }
    } else {
      string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
      string? terminal = new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" }
          .Select(name => McpAgentDiscovery.FindExecutable(name, paths)).FirstOrDefault(p => p != null);
      if (terminal == null) { throw new InvalidOperationException("No terminal emulator found. Install a terminal or use Copy connection settings."); }
      start = TerminalStart(terminal, command.Executable, command.Arguments);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Terminal did not start.");
    // A terminal broker may exit successfully immediately; a terminal holding the agent remains alive.
    await Task.Delay(150, ct).ConfigureAwait(false);
    if (process.HasExited && process.ExitCode != 0) { throw new InvalidOperationException($"Terminal launcher exited with code {process.ExitCode}."); }
  }

  internal static ProcessStartInfo AgentStart(Request request, string claudeConfig) {
    if (request.Kind is not (McpAgentKind.CodexCli or McpAgentKind.ClaudeCode)) { throw new ArgumentException("Unsupported terminal client."); }
    var start = new ProcessStartInfo(request.Executable) { UseShellExecute = false, WorkingDirectory = request.Directory };
    // A unique table cannot inherit a command/url collision from the user's existing MCP entries.
    string name = "missionplanner_session_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)))[..12].ToLowerInvariant();
    string[] args = request.Kind == McpAgentKind.CodexCli
        ? ["-c", $"mcp_servers.{name}.url=" + JsonSerializer.Serialize(request.Endpoint),
          "-c", $"mcp_servers.{name}.bearer_token_env_var=\"MP_MCP_TOKEN\"",
          "-c", $"mcp_servers.{name}.enabled=true", "-c", $"mcp_servers.{name}.tool_timeout_sec=720",
          "-c", "mcp_servers.missionplanner10_desktop.enabled=false"]
        : ["--mcp-config", claudeConfig, "--strict-mcp-config"];
    foreach (string arg in args) { start.ArgumentList.Add(arg); }
    if (!string.IsNullOrWhiteSpace(request.Prompt)) { start.ArgumentList.Add("--"); start.ArgumentList.Add(request.Prompt); }
    start.Environment["MP_MCP_TOKEN"] = request.Token;
    start.Environment["MCP_TOOL_TIMEOUT"] = "720000";
    return start;
  }

  internal static async Task<int> RunHandoffAsync(string path) {
    if (OperatingSystem.IsWindows()) { AttachConsole(unchecked((uint)-1)); }
    if (!Path.IsPathRooted(path) || new FileInfo(path).Length > 65536) { throw new ArgumentException("Invalid terminal handoff."); }
    var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(path).ConfigureAwait(false))
        ?? throw new ArgumentException("Invalid terminal handoff.");
    File.Delete(path); // A broker cannot launch the same request twice.
    using var files = new McpAgentSessionFiles(request.Kind, new Uri(request.Endpoint), request.Token);
    using var process = Process.Start(AgentStart(request, files.ClaudeConfigPath))
        ?? throw new InvalidOperationException("Agent did not start.");
    await process.WaitForExitAsync().ConfigureAwait(false);
    return process.ExitCode;
  }
  public void Dispose() => _files.Dispose();

  // Windows' native console preserves argv/environment without cmd.exe or PowerShell parsing.
  internal static string WindowsQuote(string value) {
    var result = new StringBuilder("\""); int slashes = 0;
    foreach (char c in value) {
      if (c == '\\') { slashes++; continue; }
      result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(c);
    }
    return result.Append('\\', slashes * 2).Append('"').ToString();
  }
  [SupportedOSPlatform("windows")]
  private static void StartWindowsConsole(string executable, string[] arguments) {
    var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
    var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(WindowsQuote)));
    if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x10, IntPtr.Zero, null, ref startup, out var process)) {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    CloseHandle(process.Thread); CloseHandle(process.Process);
  }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct StartupInfo {
    public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, CountX, CountY, Fill, Flags;
    public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
  }
  [StructLayout(LayoutKind.Sequential)]
  private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
  [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
      [MarshalAs(UnmanagedType.Bool)] bool inherit, int flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);
  [DllImport("kernel32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
}
