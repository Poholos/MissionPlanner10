using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services.Mcp;

/// <summary>Desktop stdio clients connect to the explicitly opened local HTTP listener.</summary>
internal static class McpStdioBridge {
  internal static async Task<int> RunAsync(int port, TextReader input, TextWriter output, CancellationToken ct = default) {
    var endpoint = McpDesktopRegistration.Endpoint(port);
    using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(12) };
    string? session = null;
    try {
      while (await ReadLineAsync(input, 256 * 1024, ct).ConfigureAwait(false) is string line) {
        if (string.IsNullOrWhiteSpace(line)) { continue; }
        using var message = JsonDocument.Parse(line);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(line, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (session != null) { request.Headers.Add("Mcp-Session-Id", session); }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) {
          if (message.RootElement.TryGetProperty("id", out var id)) {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id,
              error = new { code = -32000, message = $"Mission Planner HTTP {(int)response.StatusCode}; reopen/allow the connection in Mission Planner." } }).AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
          }
          return 1;
        }
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) { session = string.Join("", ids); }
        if ((int)response.StatusCode == 202) { continue; }
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") {
          var data = new StringBuilder(); int received = 0;
          while (await ReadLineAsync(reader, 4 * 1024 * 1024, ct).ConfigureAwait(false) is string eventLine) {
            received += eventLine.Length;
            if (received > 4 * 1024 * 1024) { throw new IOException("MCP response exceeded limit."); }
            if (eventLine.StartsWith("data:", StringComparison.Ordinal)) { data.AppendLine(eventLine[5..].TrimStart()); }
            if (eventLine.Length == 0 && data.Length > 0) {
              using var parsed = JsonDocument.Parse(data.ToString());
              await output.WriteLineAsync(JsonSerializer.Serialize(parsed.RootElement).AsMemory(), ct).ConfigureAwait(false);
              await output.FlushAsync(ct).ConfigureAwait(false); data.Clear();
            }
          }
        } else {
          string value = await ReadLineAsync(reader, 4 * 1024 * 1024, ct).ConfigureAwait(false) ?? "";
          using var parsed = JsonDocument.Parse(value);
          await output.WriteLineAsync(JsonSerializer.Serialize(parsed.RootElement).AsMemory(), ct).ConfigureAwait(false);
          await output.FlushAsync(ct).ConfigureAwait(false);
        }
      }
      return 0;
    } finally {
      if (session != null) {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.Add("Mcp-Session-Id", session); request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        try { using var response = await http.SendAsync(request, stop.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
      }
    }
  }
  internal static async Task<string?> ReadLineAsync(TextReader reader, int limit, CancellationToken ct) {
    var text = new StringBuilder(); var character = new char[1];
    while (await reader.ReadAsync(character.AsMemory(), ct).ConfigureAwait(false) != 0) {
      if (character[0] == '\n') { return text.ToString().TrimEnd('\r'); }
      text.Append(character[0]);
      if (text.Length > limit) { throw new IOException("MCP frame exceeded limit."); }
    }
    return text.Length == 0 ? null : text.ToString();
  }
}
