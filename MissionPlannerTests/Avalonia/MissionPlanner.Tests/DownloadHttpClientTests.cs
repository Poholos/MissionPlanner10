using System.Net;
using System.Net.Http;
using System.Text;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class DownloadHttpClientTests {
  [Fact]
  public async Task Helpers_reuse_the_supplied_client_and_dispose_every_response() {
    var contents = new List<TrackingContent>();
    var requests = new List<(HttpMethod Method, string Body, bool Configured)>();
    using var client = new HttpClient(new StubHandler(async request => {
      string body = request.Content == null
          ? ""
          : await request.Content.ReadAsStringAsync();
      bool configured = request.Headers.TryGetValues("X-Test", out IEnumerable<string>? values)
          && values.Contains("value", StringComparer.Ordinal);
      requests.Add((request.Method, body, configured));
      var content = new TrackingContent("reply-" + requests.Count);
      contents.Add(content);
      return new HttpResponseMessage(
          requests.Count == 3 ? HttpStatusCode.NotFound : HttpStatusCode.OK) {
        Content = content,
      };
    }));

    Assert.Equal("reply-1", await Download.PostAsync(client, "https://example.test/post", "data"));
    Assert.Equal("reply-2", await Download.GetAsync(client, "https://example.test/get"));
    Download.HTTPResult status = await Download.GetAsyncWithStatus(
        client, "https://example.test/status",
        request => request.Headers.TryAddWithoutValidation("X-Test", "value"));

    Assert.Equal(HttpStatusCode.NotFound, status.status);
    Assert.Equal("reply-3", status.content);
    Assert.Equal([
      (HttpMethod.Post, "data", false),
      (HttpMethod.Get, "", false),
      (HttpMethod.Get, "", true),
    ], requests);
    Assert.All(contents, content => Assert.True(content.Disposed));
  }

  [Fact]
  public async Task Successful_content_is_disposed_even_when_status_validation_throws() {
    var content = new TrackingContent("failure");
    using var client = new HttpClient(new StubHandler(_ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.InternalServerError) {Content = content})));

    await Assert.ThrowsAsync<HttpRequestException>(() =>
        Download.GetAsync(client, "https://example.test/failure"));

    Assert.True(content.Disposed);
  }

  private sealed class StubHandler(
      Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
  }

  private sealed class TrackingContent(string value)
      : ByteArrayContent(Encoding.UTF8.GetBytes(value)) {
    internal bool Disposed { get; private set; }

    protected override void Dispose(bool disposing) {
      Disposed = true;
      base.Dispose(disposing);
    }
  }
}
