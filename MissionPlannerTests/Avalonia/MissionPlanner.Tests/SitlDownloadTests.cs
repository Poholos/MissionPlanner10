using System.Net;
using System.Net.Http;
using System.Text;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class SitlDownloadTests {
  [Fact]
  public async Task Downloader_rejects_non_https_before_sending_a_request() {
    int requests = 0;
    using var client = new HttpClient(new StubHandler(_ => {
      requests++;
      return new HttpResponseMessage(HttpStatusCode.OK);
    }));

    await Assert.ThrowsAsync<InvalidDataException>(() => SitlLauncher.DownloadToFileAsync(
        client, new Uri("http://example.test/sitl"), TemporaryPath(), 16));

    Assert.Equal(0, requests);
  }

  [Fact]
  public async Task Downloader_rejects_an_https_downgrade_redirect() {
    using var client = new HttpClient(new StubHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) {
          RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.test/sitl"),
          Content = new ByteArrayContent([1, 2, 3]),
        }));

    await Assert.ThrowsAsync<InvalidDataException>(() => SitlLauncher.DownloadToFileAsync(
        client, new Uri("https://example.test/sitl"), TemporaryPath(), 16));
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task Oversized_download_preserves_existing_file_and_removes_partial(
      bool declareLength) {
    await WithDirectory(async directory => {
      string destination = Path.Combine(directory, "ArduCopter");
      await File.WriteAllTextAsync(destination, "known-good");
      HttpContent content = declareLength
          ? new ByteArrayContent(Encoding.UTF8.GetBytes("too-large"))
          : new UnknownLengthContent(Encoding.UTF8.GetBytes("too-large"));
      using var client = new HttpClient(new StubHandler(_ =>
          new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

      await Assert.ThrowsAsync<InvalidDataException>(() => SitlLauncher.DownloadToFileAsync(
          client, new Uri("https://example.test/ArduCopter"), destination, 4));

      Assert.Equal("known-good", await File.ReadAllTextAsync(destination));
      Assert.Empty(Directory.GetFiles(directory, "*.part"));
    });
  }

  [Fact]
  public async Task Successful_download_atomically_replaces_existing_file() {
    await WithDirectory(async directory => {
      string destination = Path.Combine(directory, "ArduPlane");
      await File.WriteAllTextAsync(destination, "old");
      using var client = new HttpClient(new StubHandler(_ =>
          new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("new-binary")),
          }));

      await SitlLauncher.DownloadToFileAsync(
          client, new Uri("https://example.test/ArduPlane"), destination, 32);

      Assert.Equal("new-binary", await File.ReadAllTextAsync(destination));
      Assert.Empty(Directory.GetFiles(directory, "*.part"));
    });
  }

  private static string TemporaryPath() =>
      Path.Combine(Path.GetTempPath(), "mp-sitl-" + Guid.NewGuid().ToString("N"));

  private static async Task WithDirectory(Func<string, Task> action) {
    string directory = TemporaryPath();
    Directory.CreateDirectory(directory);
    try {
      await action(directory);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  private sealed class StubHandler(
      Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(send(request));
  }

  private sealed class UnknownLengthContent(byte[] bytes) : HttpContent {
    protected override Task SerializeToStreamAsync(
        Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

    protected override bool TryComputeLength(out long length) {
      length = 0;
      return false;
    }
  }
}
