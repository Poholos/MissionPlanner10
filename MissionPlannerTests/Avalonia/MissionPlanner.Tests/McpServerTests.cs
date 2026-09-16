using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MissionPlanner.Services.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MissionPlanner.Tests;

public sealed class McpServerTests {
  [Fact]
  public async Task Real_http_client_discovers_tools_reads_log_and_receives_useful_errors() {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    await using var server = new MissionPlannerMcpServer(new McpVehicleAccess(() => []));
    await server.StartAsync(_ => Task.FromResult<object>(new { draft = true }), timeout.Token);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions {
      Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp,
    }, http);
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    Assert.Contains(tools, t => t.Name == "read_parameters");
    Assert.Contains(tools, t => t.Name == "log_batch_spectrum");
    Assert.DoesNotContain(tools, t => t.Name.Contains("arm") || t.Name == "apply_parameter_changes");
    var info = await client.CallToolAsync("diagnostics_info", cancellationToken: timeout.Token);
    Assert.NotEqual(true, info.IsError);
    Assert.Contains("MissionPlanner", Assert.IsType<TextContentBlock>(info.Content[0]).Text);
    var error = await client.CallToolAsync("read_telemetry", new Dictionary<string, object?> {
      ["targetId"] = "invalid", ["fields"] = "roll",
    }, cancellationToken: timeout.Token);
    Assert.True(error.IsError);
    Assert.Contains("Unknown target", Assert.IsType<TextContentBlock>(error.Content[0]).Text);

    string path = TemporaryLog();
    try {
      string id = server.Logs.Attach(path).Id;
      var first = await client.CallToolAsync("read_log_records", new Dictionary<string, object?> {
        ["logId"] = id, ["types"] = "PARM", ["count"] = 1,
      }, cancellationToken: timeout.Token);
      using var parsed = JsonDocument.Parse(Assert.IsType<TextContentBlock>(first.Content[0]).Text);
      Assert.Single(parsed.RootElement.GetProperty("rows").EnumerateArray());
      Assert.False(parsed.RootElement.GetProperty("complete").GetBoolean());
      int next = parsed.RootElement.GetProperty("nextLine").GetInt32();
      var second = await client.CallToolAsync("read_log_records", new Dictionary<string, object?> {
        ["logId"] = id, ["types"] = "PARM", ["startLine"] = next,
      }, cancellationToken: timeout.Token);
      using var parsed2 = JsonDocument.Parse(Assert.IsType<TextContentBlock>(second.Content[0]).Text);
      Assert.Single(parsed2.RootElement.GetProperty("rows").EnumerateArray());
      Assert.Equal("0.8", parsed2.RootElement.GetProperty("rows")[0].GetProperty("fields").GetProperty("Value").GetString()!.Trim());
    } finally { await server.DisposeAsync(); File.Delete(path); }
  }

  [Fact]
  public async Task Authentication_origin_and_host_are_enforced_and_each_server_gets_an_independent_port() {
    await using var first = new MissionPlannerMcpServer(new McpVehicleAccess(() => []));
    await using var second = new MissionPlannerMcpServer(new McpVehicleAccess(() => []));
    await first.StartAsync(_ => Task.FromResult<object>(new { }));
    await second.StartAsync(_ => Task.FromResult<object>(new { }));
    Assert.NotEqual(first.Endpoint!.Port, second.Endpoint!.Port);
    Assert.NotEqual(first.Token, second.Token);
    using var http = new HttpClient();
    Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(first.Endpoint)).StatusCode);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.Token);
    Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(first.Endpoint)).StatusCode);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Token);
    http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
    Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(first.Endpoint)).StatusCode);
    http.DefaultRequestHeaders.Remove("Origin");
    http.DefaultRequestHeaders.Host = "untrusted.example";
    Assert.Contains((await http.GetAsync(first.Endpoint)).StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden });
    http.DefaultRequestHeaders.Host = null;
    var uri = first.Endpoint;
    await first.DisposeAsync();
    await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(uri));
  }

  [Fact]
  public async Task Catalog_rejects_unknown_handles_and_changed_files() {
    using var catalog = new McpLogCatalog();
    string path = TemporaryLog();
    try {
      var info = catalog.Attach(path);
      await File.AppendAllTextAsync(path, "\nMSG, changed\n");
      await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.Read(info.Id, (log, _) => log.Count, default));
      await Assert.ThrowsAsync<ArgumentException>(() => catalog.Read("../../secret", (log, _) => log.Count, default));
    } finally { File.Delete(path); }
  }

  internal static string TemporaryLog() {
    string path = Path.Combine(Path.GetTempPath(), $"mp-mcp-{Guid.NewGuid():N}.log");
    File.WriteAllLines(path, [
      "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
      "FMT, 130, 40, PARM, QNff, TimeUS,Name,Value,Default",
      "PARM, 1000000, ATC_RAT_RLL_P, 0.5, 0.2",
      "PARM, 2000000, ATC_RAT_RLL_P, 0.8, 0.2",
    ]);
    return path;
  }
}
