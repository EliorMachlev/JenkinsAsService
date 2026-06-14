using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace JenkinsAsService.Tests;

public class HttpJarDownloaderTests : IDisposable
{
    private const string JenkinsUrl = "https://jenkins:8443";
    private const string JarFilename = "agent.jar";
    private const string ETagFilename = "agent.jar.etag";

    private readonly string _tempDir;
    private readonly ILogger<HttpJarDownloader> _logger = Substitute.For<ILogger<HttpJarDownloader>>();

    public HttpJarDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JASJar_Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string JarPath => Path.Combine(_tempDir, JarFilename);
    private string ETagPath => Path.Combine(_tempDir, ETagFilename);

    private HttpJarDownloader CreateDownloader(FakeHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new HttpJarDownloader(factory, _logger);
    }

    [Fact]
    public async Task Download_gets_file_on_200_and_saves_etag()
    {
        var handler = new FakeHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("JARBYTES"))
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return resp;
        });

        await CreateDownloader(handler).DownloadAsync(JenkinsUrl, _tempDir, CancellationToken.None);

        File.Exists(JarPath).Should().BeTrue();
        File.ReadAllText(JarPath).Should().Be("JARBYTES");
        File.Exists(ETagPath).Should().BeTrue();
        File.ReadAllText(ETagPath).Should().Be("\"v1\"");
    }

    [Fact]
    public async Task Download_skips_write_on_304()
    {
        File.WriteAllText(ETagPath, "\"v1\"");
        File.WriteAllText(JarPath, "OLDJAR");

        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        await CreateDownloader(handler).DownloadAsync(JenkinsUrl, _tempDir, CancellationToken.None);

        File.ReadAllText(JarPath).Should().Be("OLDJAR", "304 must not overwrite the existing jar");
        File.ReadAllText(ETagPath).Should().Be("\"v1\"", "304 must not change the stored etag");
    }

    [Fact]
    public async Task Download_sends_if_none_match_when_etag_exists()
    {
        // Both jar and etag must exist — etag alone is treated as an orphaned state and ignored.
        File.WriteAllText(JarPath, "EXISTINGJAR");
        File.WriteAllText(ETagPath, "\"abc\"");

        string? sentIfNoneMatch = null;
        var handler = new FakeHandler(req =>
        {
            sentIfNoneMatch = req.Headers.TryGetValues("If-None-Match", out var vals)
                ? string.Join(",", vals)
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("X"))
            };
        });

        await CreateDownloader(handler).DownloadAsync(JenkinsUrl, _tempDir, CancellationToken.None);

        sentIfNoneMatch.Should().Be("\"abc\"");
    }

    [Fact]
    public async Task Download_deletes_etag_file_on_200_without_etag_header()
    {
        File.WriteAllText(ETagPath, "\"stale\"");

        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("X"))
        });

        await CreateDownloader(handler).DownloadAsync(JenkinsUrl, _tempDir, CancellationToken.None);

        File.Exists(ETagPath).Should().BeFalse("a 200 without an ETag header must drop the stale etag file");
        File.Exists(JarPath).Should().BeTrue();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
