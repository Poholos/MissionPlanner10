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
  internal void Codex_launch_disables_the_persistent_desktop_entry_only_when_it_exists() {
    // Codex rejects "enabled=false" for a table that has no transport; the terminal then closes immediately.
    var request = new McpTerminalLaunch.Request("/fake/codex", McpAgentKind.CodexCli, "http://127.0.0.1:47183/mcp", "t", Path.GetTempPath(), "");
    Assert.DoesNotContain("mcp_servers.missionplanner10_desktop.enabled=false", McpTerminalLaunch.AgentStart(request, "/fake/config.json").ArgumentList);
    Assert.Contains("mcp_servers.missionplanner10_desktop.enabled=false", McpTerminalLaunch.AgentStart(request, "/fake/config.json", true).ArgumentList);
    Assert.DoesNotContain("mcp_servers.missionplanner10_desktop.enabled=false", McpTerminalLaunch.AgentStart(request with { Kind = McpAgentKind.ClaudeCode }, "/fake/config.json", true).ArgumentList);
    string root = Path.Combine(Path.GetTempPath(), "mp-desktop-entry-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
      string config = Path.Combine(root, "config.toml");
      Assert.False(McpDesktopRegistration.HasDesktopEntry(config));
      File.WriteAllText(config, "[mcp_servers.other]\nurl = 'http://127.0.0.1:1/mcp'\n");
      Assert.False(McpDesktopRegistration.HasDesktopEntry(config));
      McpDesktopRegistration.Update(config, 47183);
      Assert.True(McpDesktopRegistration.HasDesktopEntry(config));
      File.WriteAllText(config, "[mcp_servers.missionplanner10_desktop]\nurl = 'http://127.0.0.1:1/mcp'\n");
      Assert.True(McpDesktopRegistration.HasDesktopEntry(config));
      File.WriteAllText(config, "this = [broken");
      Assert.False(McpDesktopRegistration.HasDesktopEntry(config));
    } finally { Directory.Delete(root, true); }
  }
  [Fact]
  internal void Terminal_kind_follows_the_executable_name_and_a_mismatch_is_rejected_before_launch() {
    // Selecting "Claude Code" for a codex binary made Codex reject --mcp-config ("a similar argument exists: --config").
    Assert.Equal(McpAgentKind.ClaudeCode, McpAgentDiscovery.TerminalKind("/home/u/.local/bin/claude"));
    Assert.Equal(McpAgentKind.ClaudeCode, McpAgentDiscovery.TerminalKind("claude.exe"));
    Assert.Equal(McpAgentKind.CodexCli, McpAgentDiscovery.TerminalKind("/opt/bin/Codex"));
    Assert.Null(McpAgentDiscovery.TerminalKind("/tmp/fake agent"));
    var endpoint = new Uri("http://127.0.0.1:47183/mcp");
    var error = Assert.Throws<ArgumentException>(() => new McpTerminalLaunch(new(McpAgentKind.ClaudeCode, "Claude Code", "/fake/codex", []), endpoint, "t", Path.GetTempPath(), ""));
    Assert.Contains("codex is not a Claude Code executable", error.Message);
    Assert.Throws<ArgumentException>(() => new McpTerminalLaunch(new(McpAgentKind.CodexCli, "Codex CLI", "/fake/claude.exe", []), endpoint, "t", Path.GetTempPath(), ""));
    using var matching = new McpTerminalLaunch(new(McpAgentKind.ClaudeCode, "Claude Code", "/fake/claude", []), endpoint, "t", Path.GetTempPath(), "");
    Assert.True(File.Exists(matching.HandoffPath));
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
