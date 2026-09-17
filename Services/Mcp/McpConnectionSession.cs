using System;
using System.Threading;
using ModelContextProtocol.Server;

namespace MissionPlanner.Services.Mcp;

internal sealed record McpConnectionInfo(string Id, string Name, string Transport, bool Ready, string Access) {
  public override string ToString() => $"{Name} · {Transport} · {Access}";
}

/// <summary>Revocation cancels the current grant, without granting a later reinitialization.</summary>
internal sealed class McpConnectionSession {
  private readonly object _sync = new();
  private CancellationTokenSource _grant = new();
  private bool _read, _allowed, _revoked;
  private long _accessEpoch;
  internal long AccessEpoch => Interlocked.Read(ref _accessEpoch);
  internal McpOperationJournal Operations { get; } = new();
  internal McpUiSnapshot? UiSnapshot { get; set; }
  internal McpServer Server { get; }
  internal string Credential { get; }
  internal string Transport { get; }
  internal CancellationTokenSource Lifetime { get; } = new();
  internal McpConnectionSession(McpServer server, string credential, string transport, bool read, bool allowed) {
    Server = server; Credential = credential; Transport = transport; _read = read; _allowed = allowed;
  }
  internal McpConnectionInfo Info {
    get { lock (_sync) { return new(Server.SessionId!, Server.ClientInfo?.Name ?? "Connecting…", Transport,
        Server.ClientInfo != null, Lifetime.IsCancellationRequested ? "Disconnected" : _allowed ? "Allowed" : _read ? "Read only" : "Access revoked / waiting for Allow"); } }
  }
  internal bool TryAccess(bool readOnly, out CancellationToken grant) {
    lock (_sync) { grant = _grant.Token; return !Lifetime.IsCancellationRequested && (_allowed || (_read && readOnly)); }
  }
  internal void Allow() {
    lock (_sync) {
      if (Lifetime.IsCancellationRequested || Server.ClientInfo == null) { return; }
      if (_revoked) { _grant = new(); _revoked = false; }
      _allowed = true; _read = true;
    }
  }
  internal void Revoke() {
    CancellationTokenSource grant;
    lock (_sync) { _read = _allowed = false; _revoked = true; Interlocked.Increment(ref _accessEpoch); grant = _grant; }
    grant.Cancel();
  }
  internal void Disconnect() { Revoke(); Lifetime.Cancel(); }
}
