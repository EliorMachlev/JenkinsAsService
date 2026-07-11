// Copyright (c) 2024 All rights reserved

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace JenkinsAsService.Tests;

public class HttpJarDownloaderTests : IDisposable
{
    private const string JenkinsUrl = "https://jenkins:8443"; // NOSONAR
    private const string JarFilename = "agent.jar";
    private const string ETagFilename = "agent.jar.etag";
    private const string HashFilename = "agent.jar.sha256";
    private const string ETagV1 = "\"v1\"";
    private const int TempDirSuffixLength = 8;

    private readonly string _tempDir;
    private readonly ILogger<HttpJarDownloader> _logger = Substitute.For<ILogger<HttpJarDownloader>>();

    public HttpJarDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JASJar_Test_" + Guid.NewGuid().ToString("N")[..TempDirSuffixLength]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string JarPath => Path.Combine(_tempDir, JarFilename);
    private string ETagPath => Path.Combine(_tempDir, ETagFilename);
    private string HashPath => Path.Combine(_tempDir, HashFilename);

    private static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

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
            resp.Headers.ETag = new EntityTagHeaderValue(ETagV1);
            return resp;
        });

        await CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        File.Exists(JarPath).Should().BeTrue();
        File.ReadAllText(JarPath).Should().Be("JARBYTES");
        File.Exists(ETagPath).Should().BeTrue();
        File.ReadAllText(ETagPath).Should().Be(ETagV1);
        File.Exists(HashPath).Should().BeTrue("a 200 must record the jar's SHA-256 for later verification");
        File.ReadAllText(HashPath).Should().Be(Sha256Hex("JARBYTES"));
    }

    [Fact]
    public async Task Download_skips_write_on_304_when_hash_matches()
    {
        File.WriteAllText(ETagPath, ETagV1);
        File.WriteAllText(JarPath, "OLDJAR");
        File.WriteAllText(HashPath, Sha256Hex("OLDJAR"));

        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        await CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        File.ReadAllText(JarPath).Should().Be("OLDJAR", "304 with a matching hash must not overwrite the existing jar");
        File.ReadAllText(ETagPath).Should().Be(ETagV1, "304 must not change the stored etag");
    }

    [Fact]
    public async Task Download_redownloads_on_304_when_local_hash_mismatches()
    {
        // Cached jar was tampered/corrupted after we recorded its hash: on-disk bytes no longer match.
        File.WriteAllText(ETagPath, ETagV1);
        File.WriteAllText(JarPath, "TAMPERED");
        File.WriteAllText(HashPath, Sha256Hex("ORIGINAL"));

        // Server still 304s the conditional request (its jar is unchanged); the forced re-fetch omits
        // If-None-Match, so answer that unconditional GET with the authoritative jar.
        var handler = new FakeHandler(req =>
        {
            if (req.Headers.Contains("If-None-Match"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("ORIGINAL"))
            };
            resp.Headers.ETag = new EntityTagHeaderValue(ETagV1);
            return resp;
        });

        await CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        File.ReadAllText(JarPath).Should().Be("ORIGINAL", "a hash mismatch must force a fresh download over the bad jar");
        File.ReadAllText(HashPath).Should().Be(Sha256Hex("ORIGINAL"), "the re-download must refresh the stored hash");
    }

    [Fact]
    public async Task Download_redownloads_on_304_when_hash_file_is_missing()
    {
        // Legacy/first-run state: a cached jar+etag with no recorded hash can't be verified, so re-fetch.
        File.WriteAllText(ETagPath, ETagV1);
        File.WriteAllText(JarPath, "UNVERIFIED");

        var handler = new FakeHandler(req => req.Headers.Contains("If-None-Match")
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("VERIFIED"))
            });

        await CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        File.ReadAllText(JarPath).Should().Be("VERIFIED", "an unverifiable cached jar must be re-downloaded");
        File.Exists(HashPath).Should().BeTrue("the re-download records a hash for next time");
        File.ReadAllText(HashPath).Should().Be(Sha256Hex("VERIFIED"));
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

        await CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        sentIfNoneMatch.Should().Be("\"abc\"");
    }

    [Fact]
    public async Task Download_does_not_send_if_none_match_when_etag_exists_but_jar_is_missing()
    {
        // Orphan state: stale etag left on disk but the jar was deleted.
        // Without this guard, an If-None-Match would be sent → server returns 304 → no jar downloaded → agent can't start.
        File.WriteAllText(ETagPath, "\"orphan\"");

        string? sentIfNoneMatch = null;
        var handler = new FakeHandler(req =>
        {
            sentIfNoneMatch = req.Headers.TryGetValues("If-None-Match", out var vals)
                ? string.Join(",", vals)
                : null;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("FRESHJAR"))
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"orphan\"");
            return resp;
        });

        await CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        sentIfNoneMatch.Should().BeNull("orphaned etag without jar must not send If-None-Match");
        File.Exists(JarPath).Should().BeTrue("jar must be downloaded when it was missing");
    }

    [Fact]
    public async Task Download_deletes_etag_file_on_200_without_etag_header()
    {
        File.WriteAllText(ETagPath, "\"stale\"");

        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("X"))
        });

        await CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        File.Exists(ETagPath).Should().BeFalse("a 200 without an ETag header must drop the stale etag file");
        File.Exists(JarPath).Should().BeTrue();
    }

    [Fact]
    public async Task Download_throws_on_500_and_leaves_previous_jar_and_etag_intact()
    {
        File.WriteAllText(JarPath, "GOODJAR");
        File.WriteAllText(ETagPath, ETagV1);

        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        File.ReadAllText(JarPath).Should().Be("GOODJAR", "a failed download must not overwrite the good jar");
        File.ReadAllText(ETagPath).Should().Be(ETagV1);
        File.Exists(JarPath + ".tmp").Should().BeFalse("no partial temp file should be left behind");
    }

    [Fact]
    public async Task Download_mid_stream_failure_does_not_corrupt_the_existing_jar()
    {
        // A jar the JVM has been launching happily is already on disk.
        File.WriteAllText(JarPath, "GOODJAR");
        File.WriteAllText(ETagPath, ETagV1);

        // Server responds 200 but the body stream faults partway through the copy.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingStream(Encoding.UTF8.GetBytes("PARTIAL"), throwAfter: 3))
        });

        var act = () => CreateDownloader(handler).Download(new Uri(JenkinsUrl), _tempDir, CancellationToken.None);

        // StreamContent.CopyToAsync wraps the underlying IOException in HttpRequestException.
        (await act.Should().ThrowAsync<HttpRequestException>()).WithInnerException<IOException>();
        File.ReadAllText(JarPath).Should().Be("GOODJAR",
            "a truncated download must never replace the previous, complete jar");
        File.Exists(JarPath + ".tmp").Should().BeFalse("the partial temp file must be cleaned up");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }

    // Yields a few bytes then throws, to simulate a network drop partway through the body.
    private sealed class ThrowingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _throwAfter;
        private int _position;

        public ThrowingStream(byte[] data, int throwAfter)
        {
            _data = data;
            _throwAfter = throwAfter;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _throwAfter)
            {
                throw new IOException("simulated connection reset");
            }

            var toCopy = Math.Min(count, _throwAfter - _position);
            Array.Copy(_data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
