using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services.Mcp;

internal sealed class McpAgentProcess {
  internal static ProcessStartInfo BuildStart(string executable, Uri endpoint, string token, string workingDirectory,
      McpAgentKind kind = McpAgentKind.CodexCli, string? claudeConfig = null) {
    if (string.IsNullOrWhiteSpace(executable)) { throw new ArgumentException("Select the agent executable."); }
    if (kind == McpAgentKind.OpenAiDesktop) { throw new ArgumentException("Desktop applications use the desktop launcher."); }
    if (OperatingSystem.IsWindows() && !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) {
      throw new ArgumentException("Select the native codex.exe or claude.exe, not a shell script.");
    }
    var start = new ProcessStartInfo(executable) {
      UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
      RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    string[] arguments = kind == McpAgentKind.ClaudeCode
        ? new[] { "-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence",
          "--strict-mcp-config", "--mcp-config", claudeConfig ?? throw new ArgumentException("Missing session MCP config."),
          "--tools", "", "--allowedTools", "mcp__missionplanner__*", "--permission-mode", "dontAsk",
          "--setting-sources", "", "--settings", "{\"disableAllHooks\":true}", "--disable-slash-commands" }
        : new[] { "exec", "--json", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config",
        "--sandbox", "read-only", "-c", "mcp_servers.missionplanner.url=" + JsonSerializer.Serialize(endpoint.AbsoluteUri),
        "-c", "mcp_servers.missionplanner.bearer_token_env_var=\"MP_MCP_TOKEN\"",
        "-c", "mcp_servers.missionplanner.tool_timeout_sec=720", "-c", "mcp_servers.missionplanner.required=true", "-" };
    foreach (string argument in arguments) {
      start.ArgumentList.Add(argument);
    }
    start.Environment["MP_MCP_TOKEN"] = token;
    if (kind == McpAgentKind.ClaudeCode) { start.Environment["MCP_TOOL_TIMEOUT"] = "720000"; }
    return start;
  }

  internal static async Task<int> RunAsync(string executable, Uri endpoint, string token, string task,
      Action<string> output, CancellationToken ct, McpAgentKind kind = McpAgentKind.CodexCli, string? sessionParent = null) {
    if (task.Length is < 5 or > 32000) { throw new ArgumentException("Task must contain 5..32000 characters."); }
    ct.ThrowIfCancellationRequested();
    using var files = new McpAgentSessionFiles(kind, endpoint, token, sessionParent);
    using var process = new Process { StartInfo = BuildStart(executable, endpoint, token, files.DirectoryPath, kind, files.ClaudeConfigPath) };
    process.Start();
    using var registration = ct.Register(() => {
      try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
      catch (InvalidOperationException) { }
      catch (System.ComponentModel.Win32Exception) { }
    });
    async Task Drain(StreamReader reader) {
      var buffer = new char[4096];
      var line = new System.Text.StringBuilder();
      bool truncated = false;
      while (true) {
        int read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (read == 0) {
          if (line.Length > 0 && !truncated) { output(FormatLine(line.ToString().Replace(token, "[redacted]", StringComparison.Ordinal))); }
          return;
        }
        for (int i = 0; i < read; i++) {
          if (buffer[i] == '\n') {
            if (!truncated) { output(FormatLine(line.ToString().Replace(token, "[redacted]", StringComparison.Ordinal))); }
            else { output("[Agent output line exceeded 64 KiB and was omitted]\n"); }
            line.Clear(); truncated = false;
          } else if (!truncated) {
            line.Append(buffer[i]);
            if (line.Length >= 65536) { line.Clear(); truncated = true; }
          }
        }
      }
    }
    Task stdout = Drain(process.StandardOutput), stderr = Drain(process.StandardError);
    try {
      await process.StandardInput.WriteLineAsync((MissionPlannerMcpTools.Instructions + "\n\nTask:\n" + task).AsMemory(), ct).ConfigureAwait(false);
      process.StandardInput.Close();
      await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(ct)).ConfigureAwait(false);
      return process.ExitCode;
    } finally {
      try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
      catch (InvalidOperationException) { }
      catch (System.ComponentModel.Win32Exception) { }
      await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
      try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
  }
  internal static string FormatLine(string line) {
    try {
      using var parsed = JsonDocument.Parse(line);
      var root = parsed.RootElement;
      if (!root.TryGetProperty("type", out var type)) { return line + "\n"; }
      if (type.GetString() == "assistant" && root.TryGetProperty("message", out var message)
          && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array) {
        var text = new System.Text.StringBuilder();
        foreach (var block in content.EnumerateArray()) {
          if (block.TryGetProperty("text", out var value)) { text.AppendLine(value.GetString()); }
          else if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "tool_use"
              && block.TryGetProperty("name", out var name)) { text.AppendLine("Tool: " + name.GetString()); }
        }
        return text.ToString();
      }
      if (type.GetString() == "result") {
        return root.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True
            ? "Agent failed: " + (root.TryGetProperty("result", out var result) ? result.GetString() : line) + "\n"
            : "Analysis complete.\n";
      }
      if (root.TryGetProperty("item", out var item)) {
        if (item.TryGetProperty("text", out var text) && type.GetString() == "item.completed") {
          return text.GetString() + "\n";
        }
        if (item.TryGetProperty("tool", out var tool)) { return type.GetString() + ": " + tool.GetString() + "\n"; }
      }
      if (root.TryGetProperty("error", out var error)) { return error.ToString() + "\n"; }
      return type.GetString() switch {
        "thread.started" => "Agent connected.\n",
        "turn.completed" => "Analysis complete.\n",
        "turn.failed" => line + "\n",
        "error" => line + "\n",
        _ => "",
      };
    } catch (Exception e) when (e is JsonException or InvalidOperationException) { return line + "\n"; }
  }

}
