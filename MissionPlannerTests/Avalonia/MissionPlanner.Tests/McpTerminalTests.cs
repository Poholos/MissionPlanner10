using System.Text.Json;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpTerminalTests {
  [Theory]
  [InlineData(McpAgentKind.CodexCli)]
  [InlineData(McpAgentKind.ClaudeCode)]
  internal void Interactive_launch_preserves_client_settings_and_transports_prompt_as_one_argument(McpAgentKind kind) {
    const string prompt = "Analyze a log; $(touch never) 'quotes'\nsecond line";
    var request = new McpTerminalLaunch.Request("/fake/agent", kind, "http://127.0.0.1:47183/mcp", "test-token", Path.GetTempPath(), prompt);
    var start = McpTerminalLaunch.AgentStart(request, "/fake/config with spaces.json");
    Assert.False(start.RedirectStandardInput); Assert.False(start.RedirectStandardOutput); Assert.False(start.UseShellExecute);
    Assert.Equal(prompt, start.ArgumentList.Last()); Assert.Equal("--", start.ArgumentList[^2]);
    Assert.DoesNotContain("test-token", string.Join(" ", start.ArgumentList));
    foreach (string flag in new[] { "exec", "--ignore-user-config", "--sandbox", "--tools", "--permission-mode", "--ephemeral", "--dangerously-skip-permissions" }) {
      Assert.DoesNotContain(flag, start.ArgumentList);
    }
    Assert.Equal("test-token", start.Environment["MP_MCP_TOKEN"]);
  }
  [Fact]
  public async Task Terminal_handoff_runs_a_fake_agent_once_and_preserves_literal_arguments() {
    if (OperatingSystem.IsWindows()) { return; }
    string root = Path.Combine(Path.GetTempPath(), "mp-terminal-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string executable = Path.Combine(root, "fake agent"), capture = Path.Combine(root, "arguments");
    try {
      File.WriteAllText(executable, "#!/bin/sh\nprintf '%s\\n' \"$@\" > " + McpTerminalLaunch.ShellQuote(capture) + "\nexit 7\n");
      File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      using var launch = new McpTerminalLaunch(new(McpAgentKind.CodexCli, "Fake", executable, []),
          new Uri("http://127.0.0.1:47183/mcp"), "test-secret", root, "literal $() ; quote' text");
      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(launch.HandoffPath));
      Assert.Equal(7, await McpTerminalLaunch.RunHandoffAsync(launch.HandoffPath));
      Assert.False(File.Exists(launch.HandoffPath));
      string args = File.ReadAllText(capture);
      Assert.Contains("literal $() ; quote' text", args); Assert.DoesNotContain("test-secret", args);
      await Assert.ThrowsAsync<FileNotFoundException>(() => McpTerminalLaunch.RunHandoffAsync(launch.HandoffPath));
    } finally { Directory.Delete(root, true); }
  }
  [Theory]
  [InlineData(McpAgentKind.ClaudeDesktop)]
  [InlineData(McpAgentKind.LmStudio)]
  internal void Desktop_json_registration_is_idempotent_updates_owned_port_and_preserves_other_servers(McpAgentKind kind) {
    const string original = "{\"theme\":\"dark\",\"mcpServers\":{\"other\":{\"command\":\"existing\"}}}";
    string changed = McpDesktopRegistration.EditJson(original, kind, 47183);
    Assert.Equal(changed, McpDesktopRegistration.EditJson(changed, kind, 47183));
    string moved = McpDesktopRegistration.EditJson(changed, kind, 47184);
    Assert.Contains("47184", moved); Assert.DoesNotContain("47183", moved);
    using var parsed = JsonDocument.Parse(McpDesktopRegistration.EditJson(moved, kind, null));
    Assert.Equal("dark", parsed.RootElement.GetProperty("theme").GetString());
    Assert.Single(parsed.RootElement.GetProperty("mcpServers").EnumerateObject());
    Assert.Throws<InvalidOperationException>(() => McpDesktopRegistration.EditJson("{\"mcpServers\":{\"missionplanner10_desktop\":{\"url\":\"http://other\"}}}", kind, 47183));
  }
  [Fact]
  public async Task Stdio_bridge_bounds_input_frames() {
    await Assert.ThrowsAsync<IOException>(() => McpStdioBridge.ReadLineAsync(new StringReader(new string('x', 100)), 10, default));
  }
}
