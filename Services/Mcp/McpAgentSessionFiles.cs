using System;
using System.IO;
using System.Text.Json;

namespace MissionPlanner.Services.Mcp;

/// <summary>Per-run workspace. Never writes the agent's own user config or authentication.</summary>
internal sealed class McpAgentSessionFiles : IDisposable {
  internal string DirectoryPath { get; }
  internal string ClaudeConfigPath => Path.Combine(DirectoryPath, "mcp.json");
  internal McpAgentSessionFiles(McpAgentKind kind, Uri endpoint, string token, string? parent = null) {
    DirectoryPath = Path.Combine(parent ?? Path.GetTempPath(), "missionplanner-agent-" + Guid.NewGuid().ToString("N"));
    if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(DirectoryPath); }
    else { Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    try {
      if (kind == McpAgentKind.ClaudeCode) {
        File.WriteAllText(ClaudeConfigPath, JsonSerializer.Serialize(new {
          mcpServers = new { missionplanner = new { type = "http", url = endpoint.AbsoluteUri, timeout = 720000,
            headers = new { Authorization = "Bearer " + token } } },
        }));
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(ClaudeConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
      }
    } catch { Dispose(); throw; }
  }
  public void Dispose() { if (Directory.Exists(DirectoryPath)) { Directory.Delete(DirectoryPath, true); } }
}
