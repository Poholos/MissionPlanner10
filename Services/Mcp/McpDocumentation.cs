using System;
using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;

namespace MissionPlanner.Services.Mcp;

[McpServerResourceType]
internal sealed class McpDocumentation {
  internal const string StartUri = "missionplanner://documentation/AI_START.md";
  internal static string Read(string name) {
    using var stream = typeof(McpDocumentation).Assembly.GetManifestResourceStream("MissionPlanner.McpDocs." + name)
        ?? throw new InvalidOperationException("Embedded MCP documentation is missing; repair the installation.");
    if (stream.Length > 128 * 1024) { throw new InvalidOperationException("Documentation exceeds its size limit."); }
    using var reader = new StreamReader(stream); return reader.ReadToEnd();
  }
  [McpServerResource(UriTemplate = StartUri, Name = "AI_START", MimeType = "text/markdown"), Description("Start here: Mission Planner agent workflow, permissions, units, UI and operation receipts.")]
  public string Start() => Read("AI_START.md");
  [McpServerResource(UriTemplate = "missionplanner://documentation/UI_API.md", Name = "UI_API", MimeType = "text/markdown"), Description("UI and mission-draft tools, scopes, examples, revisions and replay semantics.")]
  public string Ui() => Read("UI_API.md");
  [McpServerResource(UriTemplate = "missionplanner://documentation/DIAGNOSTICS.md", Name = "DIAGNOSTICS", MimeType = "text/markdown"), Description("Flight diagnostic tools, connection setup and analysis limits.")]
  public string Diagnostics() => Read("DIAGNOSTICS.md");
}
