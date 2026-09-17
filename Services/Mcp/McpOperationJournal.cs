using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpOperationResult(string OperationId, string Status, object? Result = null, string? Error = null);

/// <summary>Connection-local receipts survive revoke/allow. A lost response never requires a second mutation.</summary>
internal sealed class McpOperationJournal {
  private readonly object _sync = new();
  private readonly Dictionary<string, (string Fingerprint, McpOperationResult Receipt)> _entries = new();
  internal const int Capacity = 256;

  internal static void ValidateId(string id) {
    if (string.IsNullOrEmpty(id) || id.Length > 64 || !char.IsAsciiLetterOrDigit(id[0])) { throw new ArgumentException("operationId must be 1..64 ASCII letters/digits, '.', '_', ':' or '-'."); }
    foreach (char c in id) { if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-')) { throw new ArgumentException("Invalid operationId."); } }
  }

  internal McpOperationResult Get(string id) {
    ValidateId(id);
    lock (_sync) { return _entries.TryGetValue(id, out var entry) ? entry.Receipt : new(id, "unknown_operation"); }
  }

  internal async Task<McpOperationResult> RunAsync(string id, string method, object args,
      Func<Task<object>> action, CancellationToken ct) {
    ValidateId(id); ct.ThrowIfCancellationRequested();
    string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n"
        + JsonSerializer.Serialize(args, MissionPlannerMcpTools.JsonOptions))));
    lock (_sync) {
      if (_entries.TryGetValue(id, out var entry)) {
        if (entry.Fingerprint != fingerprint) { throw new InvalidOperationException("operation_conflict: operationId already has different arguments."); }
        return entry.Receipt;
      }
      if (_entries.Count >= Capacity) { throw new InvalidOperationException("operation_limit: reconnect for a new journal; do not blindly repeat uncertain operations."); }
      _entries.Add(id, (fingerprint, new(id, "running")));
    }
    McpOperationResult receipt;
    try {
      ct.ThrowIfCancellationRequested();
      receipt = new(id, "completed", await action().ConfigureAwait(false));
    } catch (OperationCanceledException) {
      // Cancellation after native dispatch is not a rollback receipt.
      receipt = new(id, "cancelled", Error: "Inspect current UI/draft before any retry; completed changes are not rolled back.");
    } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.IOException) {
      receipt = new(id, "rejected", Error: e.Message);
    } catch (Exception) {
      receipt = new(id, "outcome_unknown", Error: "Native UI operation failed. Inspect state before retrying.");
    }
    lock (_sync) { _entries[id] = (fingerprint, receipt); }
    return receipt;
  }
}
