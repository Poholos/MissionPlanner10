using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace MissionPlanner.Services.Mcp;

internal sealed class MissionPlannerMcpServer : IAsyncDisposable {
  private WebApplication? _app;
  private readonly SemaphoreSlim _lifecycle = new(1, 1);
  private readonly CancellationTokenSource _stop = new();
  private int _stopped;
  private readonly int _port;
  private readonly bool _ownsLogs;
  internal bool RequiresToken { get; }
  internal Uri? Endpoint { get; private set; }
  internal string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  internal McpVehicleAccess Vehicles { get; }
  internal McpLogCatalog Logs { get; }
  internal CancellationToken Stopping => _stop.Token;
  internal event Action<string>? Activity;
  internal Func<string, CancellationToken, Task>? OpenLogAnalyzer { get; init; }

  internal MissionPlannerMcpServer(McpVehicleAccess vehicles, McpLogCatalog? logs = null,
      int port = 0, bool requiresToken = true) {
    if (port is < 0 or > 65535 || (!requiresToken && port < 1024)) {
      throw new ArgumentException("Desktop MCP requires an explicit local port between 1024 and 65535.");
    }
    Vehicles = vehicles; Logs = logs ?? new(); _ownsLogs = logs == null;
    _port = port; RequiresToken = requiresToken;
  }

  internal async Task StartAsync(Func<CancellationToken, Task<object>> mission, CancellationToken ct = default) {
    await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
    try {
      ObjectDisposedException.ThrowIf(_stopped != 0, this);
      if (_app != null) { return; }
      var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {
        Args = [], ApplicationName = typeof(MissionPlannerMcpServer).Assembly.GetName().Name,
        ContentRootPath = AppContext.BaseDirectory,
      });
      builder.Configuration.Sources.Clear();
      builder.Configuration.AddInMemoryCollection();
      builder.Configuration["AllowedHosts"] = "127.0.0.1";
      builder.Logging.ClearProviders();
      builder.WebHost.ConfigureKestrel(options => {
        options.Listen(IPAddress.Loopback, _port);
        options.Limits.MaxRequestBodySize = 256 * 1024;
        options.Limits.MaxConcurrentConnections = 16;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
      });
      var toolInstance = new MissionPlannerMcpTools(Vehicles, Logs, mission, OpenLogAnalyzer);
      builder.Services.AddMcpServer(options => { options.ServerInstructions = MissionPlannerMcpTools.Instructions; })
          .WithHttpTransport(options => { options.SessionMode = HttpServerSessionMode.Stateless; })
          .WithTools(toolInstance);
      var app = builder.Build();
      byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + Token));
      var requests = new SemaphoreSlim(4, 4);
      app.Use(async (context, next) => {
        if (_stop.IsCancellationRequested) { context.Response.StatusCode = 503; return; }
        if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != Endpoint?.Port
            || (context.Request.Headers.TryGetValue("Origin", out var origin)
                && origin.ToString() != Endpoint?.GetLeftPart(UriPartial.Authority))) {
          context.Response.StatusCode = 403; return;
        }
        byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
        if (RequiresToken && !CryptographicOperations.FixedTimeEquals(expected, supplied)) {
          context.Response.StatusCode = 401; return;
        }
        if (!await requests.WaitAsync(0, context.RequestAborted).ConfigureAwait(false)) {
          context.Response.StatusCode = 429; return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(12));
        context.RequestAborted = lifetime.Token;
        try { await next(context).ConfigureAwait(false); }
        finally {
          requests.Release();
          Activity?.Invoke($"MCP {context.Request.Method} {context.Response.StatusCode}");
        }
      });
      app.MapMcp("/mcp");
      try {
        await app.StartAsync(ct).ConfigureAwait(false);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Endpoint = new Uri(address + "/mcp");
        _app = app;
      } catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    } finally { _lifecycle.Release(); }
  }

  public async ValueTask DisposeAsync() {
    if (Interlocked.Exchange(ref _stopped, 1) != 0) { return; }
    _stop.Cancel();
    await _lifecycle.WaitAsync().ConfigureAwait(false);
    try {
      if (_app != null) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
      }
      Endpoint = null;
      if (_ownsLogs) { await Task.Run(Logs.Dispose).ConfigureAwait(false); }
    } finally { _lifecycle.Release(); }
  }
}
