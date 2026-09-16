using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services.Mcp;

internal static class McpFlightLogWorkflow {
  // The downloader owns exact-target/disarmed/transport guards. UI and MCP use the same path.
  internal static async Task<McpLogInfo> DownloadAsync(Func<CancellationToken, Task<string>> download,
      McpLogCatalog catalog, string directory, ushort logId, CancellationToken ct) {
    string temporary = await download(ct).ConfigureAwait(false);
    try {
      ct.ThrowIfCancellationRequested();
      Directory.CreateDirectory(directory);
      string path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{logId}-{Guid.NewGuid():N}.bin");
      File.Move(temporary, path);
      // The completed file is retained even if session revocation prevents attachment.
      return catalog.Attach(path);
    } finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
  }
}
