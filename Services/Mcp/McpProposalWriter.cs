using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services.Mcp;

internal static class McpProposalWriter {
  internal static readonly SemaphoreSlim Gate = new(1, 1);
  internal static async Task<string> ApplyAsync(McpVehicleAccess vehicles, ParameterProposal proposal, CancellationToken ct) {
    await Gate.WaitAsync(ct).ConfigureAwait(false);
    try {
      if (proposal.Status != "Pending review in Mission Planner") {
        throw new InvalidOperationException("This proposal has already been processed; request a fresh proposal.");
      }
      var target = vehicles.Resolve(proposal.TargetId, true);
      void ValidateTarget() {
        vehicles.Resolve(proposal.TargetId, true);
        McpVehicleAccess.RequireDisarmed(target);
        if (target.Connection.Link.ReadOnly) {
          throw new InvalidOperationException("Vehicle must remain disarmed with a writable connection.");
        }
      }
      ValidateTarget();
      foreach (var change in proposal.Changes) { McpVehicleAccess.ValidateChange(target, change); }
      string directory = Path.Combine(AppPaths.StateRoot, "agent-parameter-audit");
      Directory.CreateDirectory(directory);
      string prefix = Path.Combine(directory, proposal.Id);
      // Persist the exact review and recovery values before the first network write.
      await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(proposal,
          new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
      await File.WriteAllLinesAsync(prefix + "-before.param", target.State.param.Snapshot()
          .Where(p => !McpVehicleAccess.Sensitive(p.Name))
          .Select(p => p.Name + "," + p.Value.ToString("R", CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
      proposal.Status = "Applying";
      var results = new List<string>();
      int applied = 0;
      try {
        foreach (var change in proposal.Changes) {
          ct.ThrowIfCancellationRequested(); ValidateTarget();
          McpVehicleAccess.ValidateChange(target, change);
          bool written = await Task.Run(() => target.Connection.Link.SetParamIfUnchangedAsync(
              target.State.sysid, target.State.compid, change.Name, change.Expected, change.Proposed,
              ValidateTarget, ct), ct).ConfigureAwait(false);
          double actual = target.State.param[change.Name]?.Value ?? double.NaN;
          var type = target.State.param[change.Name]?.TypeAP;
          double expectedWire = type == MAVLink.MAV_PARAM_TYPE.REAL32 ? (double)(float)change.Proposed : change.Proposed;
          if (!written || actual != expectedWire) {
            throw new InvalidOperationException($"{change.Name}: not verified; vehicle may have applied a value. Read it again before retrying.");
          }
          applied++;
          results.Add($"{change.Name}: acknowledged {actual.ToString("R", CultureInfo.InvariantCulture)}");
          await File.WriteAllLinesAsync(prefix + "-result.txt", results, CancellationToken.None).ConfigureAwait(false);
        }
        proposal.Status = $"Applied {applied}/{proposal.Changes.Length}; check reboot-required metadata and validate in flight";
      } catch (Exception e) {
        proposal.Status = $"Stopped after {applied}/{proposal.Changes.Length}: {e.Message}";
        // Never automatically roll back, retry, reboot or claim a batch is atomic.
      }
      results.Add(proposal.Status);
      await File.WriteAllLinesAsync(prefix + "-result.txt", results, CancellationToken.None).ConfigureAwait(false);
      return proposal.Status + ". Backup and audit: " + directory;
    } finally { Gate.Release(); }
  }
}
