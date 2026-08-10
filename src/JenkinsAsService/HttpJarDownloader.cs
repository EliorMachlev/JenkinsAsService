// Copyright (c) 2024 All rights reserved

using System.Net;
using System.Security.Cryptography;

namespace JenkinsAsService;

public sealed class HttpJarDownloader : IJarDownloader
{
    private const string JarFilename = AgentJar.FileName;
    private const string JnlpJarsPath = "jnlpJars";

    /// <summary>Hash prefix used in log lines — enough to correlate, short enough to read.</summary>
    private const int ShortHashLength = 12;

    /// <summary>
    /// Buffer for the jar's streaming read/write. Matches <see cref="Stream.CopyToAsync(Stream)"/>'s own
    /// default, so the copy is not the thing that re-chunks it.
    /// </summary>
    private const int StreamBufferBytes = 81920;

    /// <summary>Named <see cref="System.Net.Http.HttpClient"/> key. Referenced by <c>Program.cs</c> when
    /// registering the client (with resilience + optional cert pinning), so both sides stay in sync.</summary>
    public const string ClientName = "JarDownloader";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpJarDownloader> _logger;

    public HttpJarDownloader(IHttpClientFactory httpFactory, ILogger<HttpJarDownloader> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Whether the request may be answered from cache, or must return the body.</summary>
    private enum FetchMode
    {
        /// <summary>Replays the stored validator, so an unchanged jar comes back as a 304.</summary>
        Conditional,

        /// <summary>Sends no validator: the server's copy is written unconditionally.</summary>
        Forced,
    }

    private enum FetchOutcome
    {
        Downloaded,
        NotModified,
    }

    public async Task Download(Uri jenkinsUri, string destinationPath, CancellationToken ct)
    {
        var paths = new JarCachePaths(destinationPath);
        var jarUri = BuildJarUri(jenkinsUri);

        // Deliberately not "Downloading …": this runs before the conditional GET, so at this point no
        // download is known to be needed. The old wording appeared on every start and made a working cache
        // look broken — and, once the cache really did break, hid it in a line that had cried wolf for months.
        _logger.LogInformation("Checking {Jar} against {Uri}", JarFilename, jarUri);

        // Freshness pass: conditional GET honoring the stored validator. A 200 streams the new jar and records
        // its SHA-256, so the hash always matches by construction — no further check needed.
        if (await Fetch(jarUri, paths, FetchMode.Conditional, ct) == FetchOutcome.Downloaded)
        {
            return;
        }

        // Server reports Not-Modified. Confirm the cached jar still matches the hash we recorded when we
        // downloaded it — a mismatch means it was altered or corrupted since, so it must not be launched.
        if (await LocalJarMatchesHash(paths, ct))
        {
            _logger.LogInformation(
                "{Jar} is up to date (server reports Not Modified) and SHA-256 verified — skipping download",
                JarFilename);
            return;
        }

        // Local copy failed verification: force an unconditional re-fetch so the server's authoritative jar
        // replaces the bad one and a fresh hash is stored.
        _logger.LogWarning("{Jar} failed SHA-256 verification (tampered or corrupt) — forcing a fresh download",
            JarFilename);
        await Fetch(jarUri, paths, FetchMode.Forced, ct);
    }

    // One GET. Conditional replays the stored validator (when a jar+sidecar pair exists) so an unchanged
    // server jar returns 304; Forced always re-fetches. A 200 streams the body atomically, then persists the
    // new validator and the SHA-256 of what landed on disk.
    private async Task<FetchOutcome> Fetch(Uri jarUri, JarCachePaths paths, FetchMode mode, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, jarUri);

        AddConditionalHeader(request, paths, mode);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return FetchOutcome.NotModified;
        }

        response.EnsureSuccessStatusCode();

        var bytesWritten = await StreamToFile(response, paths, ct);
        SaveValidator(response, paths);
        var hash = await SaveHash(paths, ct);

        _logger.LogInformation("{Jar} downloaded successfully ({Bytes} bytes, sha256 {Hash})",
            JarFilename, bytesWritten, Shorten(hash));
        return FetchOutcome.Downloaded;
    }

    /// <summary>
    /// Replays the stored validator, if there is one to replay.
    /// <para>
    /// The jar file must exist as well: an orphaned sidecar would earn a 304 and leave the caller without an
    /// <c>agent.jar</c> at all. Finding nothing usable is not an error — it simply means a full GET.
    /// </para>
    /// </summary>
    private void AddConditionalHeader(HttpRequestMessage request, JarCachePaths paths, FetchMode mode)
    {
        if (mode != FetchMode.Conditional || !File.Exists(paths.Jar))
        {
            return;
        }

        if (JarCacheValidator.Read(paths.Directory) is not { } validator)
        {
            _logger.LogDebug("No usable cache validator for {Jar} — requesting the full jar", JarFilename);
            return;
        }

        request.Headers.TryAddWithoutValidation(validator.ConditionalHeader, validator.Value);
        _logger.LogDebug("Conditional GET for {Jar} with {Header}: {Value}",
            JarFilename, validator.ConditionalHeader, validator.Value);
    }

    private static Uri BuildJarUri(Uri jenkinsUri) =>
        new(jenkinsUri, $"{JnlpJarsPath}/{JarFilename}");

    private static string Shorten(string hash) => hash[..Math.Min(ShortHashLength, hash.Length)];

    // Stream to a temp file then atomically move it into place, so a failure mid-download (network drop,
    // cancellation) can never leave a truncated agent.jar behind the still-valid previous validator — which
    // would 304 on the next start and launch a corrupt jar.
    private static async Task<long> StreamToFile(HttpResponseMessage response, JarCachePaths paths, CancellationToken ct)
    {
        try
        {
            long length;
            await using (var fs = new FileStream(paths.Temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             StreamBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(fs, ct);
                length = fs.Length;
            }

            File.Move(paths.Temp, paths.Jar, overwrite: true);
            return length;
        }
        catch
        {
            // Best-effort cleanup of the partial download; a leftover .tmp is harmless (never used).
            TryDelete(paths.Temp);
            throw;
        }
    }

    /// <summary>
    /// Records the freshness validator the server just gave us in its own sidecar — the ETag-sidecar if an
    /// <c>ETag</c> was offered, otherwise the Modified-sidecar. The other one is always removed: exactly one
    /// token describes the jar on disk, and a stale second copy is a disagreement waiting to be resolved the
    /// wrong way. A response carrying neither validator leaves nothing cacheable, so both go.
    /// </summary>
    private void SaveValidator(HttpResponseMessage response, JarCachePaths paths)
    {
        var chosen = JarCacheValidator.From(response);

        foreach (var kind in JarCacheValidator.AllKinds)
        {
            if (chosen?.Kind != kind)
            {
                TryDelete(paths.Sidecar(kind));
            }
        }

        if (chosen is not { } validator)
        {
            _logger.LogWarning(
                "Controller sent neither ETag nor Last-Modified for {Jar} — it cannot be cached and will be " +
                "re-downloaded on every start", JarFilename);
            return;
        }

        _logger.LogDebug("Saving {Kind} validator {Value} for {Jar}", validator.Kind, validator.Value, JarFilename);
        File.WriteAllText(paths.Sidecar(validator.Kind), validator.Value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort: a sidecar we failed to remove is re-validated (or overwritten) on the next pass.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    // Records the SHA-256 (uppercase hex) of the freshly written jar next to it, so a later run can detect
    // any change to the cached file. Trust-on-first-use: this attests the bytes we downloaded, not upstream
    // authenticity — pair with Connection:ControllerCertThumbprint to also pin who we downloaded from.
    private async Task<string> SaveHash(JarCachePaths paths, CancellationToken ct)
    {
        var hash = await ComputeSha256(paths.Jar, ct);
        await File.WriteAllTextAsync(paths.Hash, hash, ct);
        _logger.LogDebug("Saved SHA-256 {Hash} for {Jar}", hash, JarFilename);
        return hash;
    }

    // True only when both the jar and its stored hash exist and the jar hashes to the stored value.
    private static async Task<bool> LocalJarMatchesHash(JarCachePaths paths, CancellationToken ct)
    {
        if (!File.Exists(paths.Jar) || !File.Exists(paths.Hash))
        {
            return false;
        }

        string stored;
        try
        {
            stored = (await File.ReadAllTextAsync(paths.Hash, ct)).Trim();
        }
        catch (IOException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var actual = await ComputeSha256(paths.Jar, ct);

        // Ordinal, case-insensitive: both sides are hex from Convert.ToHexString, and a culture-aware
        // comparison on a security check is a defect waiting for the wrong locale.
        return string.Equals(stored, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256(string jarPath, CancellationToken ct)
    {
        // Sequential + async: this reads a ~1.5 MB file on every start and on every 304.
        await using var fs = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
