using System;
using System.IO;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace MissionPlanner.Services.Mcp;

/// <summary>Owns one marked TOML section. Other configuration bytes are preserved.</summary>
internal static class McpDesktopRegistration {
  internal const int DefaultPort = 47183;
  internal const string ServerName = "missionplanner10_desktop";
  private const string Begin = "# BEGIN MissionPlanner10 desktop MCP (managed)";
  private const string End = "# END MissionPlanner10 desktop MCP (managed)";
  internal static string ConfigPath {
    get {
      string? home = Environment.GetEnvironmentVariable("CODEX_HOME");
      if (string.IsNullOrWhiteSpace(home)) { home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"); }
      if (!Path.IsPathRooted(home)) { throw new InvalidOperationException("CODEX_HOME must be an absolute path for desktop registration."); }
      return Path.Combine(home, "config.toml");
    }
  }
  internal static Uri Endpoint(int port) {
    if (port is < 1024 or > 65535) { throw new ArgumentOutOfRangeException(nameof(port), "Choose a port between 1024 and 65535."); }
    return new Uri($"http://127.0.0.1:{port}/mcp");
  }
  private static string Block(int port, string newline) => string.Join(newline, new[] {
    Begin, $"[mcp_servers.{ServerName}]", $"url = \"{Endpoint(port).AbsoluteUri}\"",
    "enabled = true", "startup_timeout_sec = 5", "tool_timeout_sec = 720", End, "",
  });

  private static TomlTable Parse(string text) {
    try { return TomlSerializer.Deserialize<TomlTable>(text) ?? new(); }
    catch (Exception e) when (e is TomlException) { throw new InvalidOperationException("Cannot edit invalid Codex TOML configuration. Fix it in the client's settings first.", e); }
  }
  private static TomlTable? Entry(TomlTable model) => model.TryGetValue("mcp_servers", out var servers) && servers is TomlTable table
      && table.TryGetValue(ServerName, out var entry) ? entry as TomlTable : null;
  private static string OtherSettings(TomlTable model) {
    if (model.TryGetValue("mcp_servers", out var servers) && servers is TomlTable table) {
      table.Remove(ServerName);
      if (table.Count == 0) { model.Remove("mcp_servers"); }
    }
    return TomlSerializer.Serialize(model);
  }

  internal static string Edit(string original, int? port) {
    var before = Parse(original);
    int start = original.IndexOf(Begin, StringComparison.Ordinal), end = original.IndexOf(End, StringComparison.Ordinal);
    string outside = original;
    if (start >= 0 || end >= 0) {
      if (start < 0 || end < start || original.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0
          || original.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0) {
        throw new InvalidOperationException("Mission Planner registration markers are incomplete or duplicated; config was not changed.");
      }
      int length = end + End.Length - start;
      if (original.AsSpan(end + End.Length).StartsWith("\r\n")) { length += 2; }
      else if (original.AsSpan(end + End.Length).StartsWith("\n")) { length++; }
      string owned = original.Substring(start, length);
      var entry = Entry(Parse(owned));
      if (entry == null || !entry.TryGetValue("url", out var value) || value is not string url
          || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Port < 1024
          || owned.Replace("\r\n", "\n") != Block(endpoint.Port, "\n")) {
        throw new InvalidOperationException("The managed MCP section was edited outside Mission Planner; config was not changed.");
      }
      outside = original.Remove(start, length);
      // Markers inside a multiline string or an unrelated table cannot authorize its deletion.
      if (OtherSettings(Parse(original)) != OtherSettings(Parse(outside))) {
        throw new InvalidOperationException("Registration markers overlap other settings; config was not changed.");
      }
    } else if (Entry(before) != null) {
      throw new InvalidOperationException("An unmanaged missionplanner10_desktop entry already exists; config was not changed.");
    }
    if (port == null) { return outside; }
    string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    string changed = outside + (outside.Length > 0 && !outside.EndsWith('\n') ? newline : "") + Block(port.Value, newline);
    var after = Parse(changed);
    if (Entry(after) is not TomlTable added || !added.TryGetValue("url", out var addedUrl) || addedUrl as string != Endpoint(port.Value).AbsoluteUri
        || OtherSettings(Parse(original)) != OtherSettings(after)) {
      throw new InvalidOperationException("Cannot register MCP without changing other settings.");
    }
    return changed;
  }

  internal static bool IsRegistered(string path, int port) {
    if (!File.Exists(path)) { return false; }
    string text = File.ReadAllText(path);
    // Validate ownership and the whole document before claiming the managed endpoint is registered.
    return Edit(text, port) == text;
  }

  internal static string? Update(string path, int? port) {
    path = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string lockPath = path + ".missionplanner.lock";
    using var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) {
      throw new IOException("The client config is a symbolic link; register Mission Planner in its target through the client settings.");
    }
    byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : [];
    bool bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
    string text = new UTF8Encoding(false, true).GetString(original.AsSpan(bom ? 3 : 0));
    string changed = Edit(text, port);
    if (changed == text) { return null; }
    string temporary = path + ".mp-" + Guid.NewGuid().ToString("N") + ".tmp";
    string? backup = null;
    try {
      var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
      if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
      using (var stream = new FileStream(temporary, options)) {
        if (bom) { stream.Write(new byte[] { 0xef, 0xbb, 0xbf }); }
        stream.Write(Encoding.UTF8.GetBytes(changed)); stream.Flush(true);
      }
      byte[] current = File.Exists(path) ? File.ReadAllBytes(path) : [];
      if (!current.AsSpan().SequenceEqual(original)) { throw new IOException("Client configuration changed during registration; retry."); }
      if (File.Exists(path)) {
        backup = path + ".mp-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
        // File.Replace keeps a complete backup and updates the file atomically on the same volume.
        File.Replace(temporary, path, backup);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
      } else { File.Move(temporary, path); }
      return backup;
    } finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
  }
}
