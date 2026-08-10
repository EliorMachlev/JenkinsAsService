// Copyright (c) 2024 All rights reserved

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class JarCacheValidatorTests : IDisposable
{
    private const string ETagValue = "\"v1\"";
    private const string HttpDate = "Sat, 08 Aug 2026 07:59:27 GMT";
    private const int TempDirSuffixLength = 8;

    private readonly string _tempDir;

    public JarCacheValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JASVal_Test_" + Guid.NewGuid().ToString("N")[..TempDirSuffixLength]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string ETagPath => Path.Combine(_tempDir, AgentJar.ETagSidecarFileName);
    private string ModifiedPath => Path.Combine(_tempDir, AgentJar.ModifiedSidecarFileName);

    [Fact]
    public void From_prefers_the_etag_when_both_headers_are_present()
    {
        var validator = JarCacheValidator.From(ResponseWith(etag: ETagValue, lastModified: HttpDate));

        validator.Should().NotBeNull();
        validator!.Value.Kind.Should().Be(ValidatorKind.ETag);
        validator.Value.Value.Should().Be(ETagValue);
    }

    [Fact]
    public void From_falls_back_to_last_modified_when_no_etag_is_offered()
    {
        // The real Jenkins /jnlpJars/agent.jar response shape.
        var validator = JarCacheValidator.From(ResponseWith(etag: null, lastModified: HttpDate));

        validator.Should().NotBeNull();
        validator!.Value.Kind.Should().Be(ValidatorKind.LastModified);
        validator.Value.Value.Should().Be(HttpDate);
    }

    [Fact]
    public void From_returns_null_when_the_response_offers_no_validator()
    {
        JarCacheValidator.From(ResponseWith(etag: null, lastModified: null)).Should().BeNull();
    }

    [Fact]
    public void Last_modified_is_normalised_to_a_utc_http_date()
    {
        // A controller in another zone must not produce a value the server would reject or misread.
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.LastModified =
            new DateTimeOffset(2026, 8, 8, 10, 59, 27, TimeSpan.FromHours(3));

        JarCacheValidator.From(response)!.Value.Value.Should().Be(HttpDate);
    }

    [Fact]
    public void Each_kind_maps_to_its_own_sidecar_and_conditional_header()
    {
        var etag = new JarCacheValidator(ValidatorKind.ETag, ETagValue);
        var modified = new JarCacheValidator(ValidatorKind.LastModified, HttpDate);

        etag.SidecarFileName.Should().Be("agent.jar.etag");
        etag.ConditionalHeader.Should().Be("If-None-Match");
        modified.SidecarFileName.Should().Be("agent.jar.modified");
        modified.ConditionalHeader.Should().Be("If-Modified-Since");
    }

    [Fact]
    public void Read_returns_the_etag_sidecar_when_present()
    {
        File.WriteAllText(ETagPath, ETagValue);

        JarCacheValidator.Read(_tempDir).Should().Be(new JarCacheValidator(ValidatorKind.ETag, ETagValue));
    }

    [Fact]
    public void Read_returns_the_modified_sidecar_when_present()
    {
        File.WriteAllText(ModifiedPath, HttpDate);

        JarCacheValidator.Read(_tempDir).Should().Be(new JarCacheValidator(ValidatorKind.LastModified, HttpDate));
    }

    [Fact]
    public void Read_prefers_the_etag_sidecar_if_both_somehow_exist()
    {
        // Writing either sidecar deletes the other, so this state should be unreachable — but an interrupted
        // write or a hand-edited cache directory could produce it, and the stronger validator must win.
        File.WriteAllText(ETagPath, ETagValue);
        File.WriteAllText(ModifiedPath, HttpDate);

        JarCacheValidator.Read(_tempDir)!.Value.Kind.Should().Be(ValidatorKind.ETag);
    }

    [Fact]
    public void Read_returns_null_when_no_sidecar_exists()
    {
        JarCacheValidator.Read(_tempDir).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Read_treats_an_empty_sidecar_as_absent(string contents)
    {
        // An empty file must produce an unconditional GET, never a blank conditional header for the
        // controller to make sense of.
        File.WriteAllText(ETagPath, contents);

        JarCacheValidator.Read(_tempDir).Should().BeNull();
    }

    [Fact]
    public void Read_trims_surrounding_whitespace_from_a_stored_value()
    {
        File.WriteAllText(ETagPath, ETagValue + Environment.NewLine);

        JarCacheValidator.Read(_tempDir)!.Value.Value.Should().Be(ETagValue);
    }

    [Theory]
    [InlineData("\"v1\"\r\nX-Injected: evil")]
    [InlineData("\"v1\"\nX-Injected: evil")]
    [InlineData("\"v1\"\tpadded")]
    [InlineData("\"v1\"\u007f")]
    public void Read_rejects_a_value_containing_control_characters(string malicious)
    {
        // The value is replayed through TryAddWithoutValidation, which checks nothing by design — an embedded
        // newline would let whatever wrote this file append headers to our request. The cache directory sits
        // under %ProgramData% next to the build work dir and is writable by the account build steps run as,
        // so "we wrote it ourselves" is not a safe assumption.
        File.WriteAllText(ETagPath, malicious);

        JarCacheValidator.Read(_tempDir).Should().BeNull("a value that cannot be safely sent must be discarded");
    }

    [Fact]
    public void Read_rejects_an_oversized_sidecar_without_reading_it()
    {
        // Real validators are tens of characters. The cap is enforced against the file's length, so a huge
        // file is refused rather than pulled into memory to be rejected afterwards.
        File.WriteAllText(ETagPath, new string('x', 4096));

        JarCacheValidator.Read(_tempDir).Should().BeNull();
    }

    [Fact]
    public void Read_falls_through_to_the_modified_sidecar_when_the_etag_sidecar_is_unusable()
    {
        // A corrupt ETag-sidecar must not mask a perfectly good Last-Modified one.
        File.WriteAllText(ETagPath, "\"v1\"\r\nX-Injected: evil");
        File.WriteAllText(ModifiedPath, HttpDate);

        JarCacheValidator.Read(_tempDir).Should().Be(new JarCacheValidator(ValidatorKind.LastModified, HttpDate));
    }

    [Fact]
    public void AllKinds_covers_every_declared_kind()
    {
        // AllKinds is hand-written to avoid Enum.GetValues allocating per download; if a kind is ever added
        // and not listed here, SaveValidator would silently stop clearing its sidecar.
        JarCacheValidator.AllKinds.ToArray().Should().BeEquivalentTo(Enum.GetValues<ValidatorKind>());
    }

    private static HttpResponseMessage ResponseWith(string? etag, string? lastModified)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };

        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        if (lastModified is not null)
        {
            response.Content.Headers.LastModified = DateTimeOffset.Parse(lastModified, CultureInfo.InvariantCulture);
        }

        return response;
    }
}
