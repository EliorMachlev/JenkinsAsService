// Copyright (c) 2024 All rights reserved

using System.Net;
using System.Security.Cryptography;

namespace JenkinsAsService;

public sealed class HttpJarDownloader : IJarDownloader
{
    private const string JarFilename = AgentJar.FileName;
    private const string HashFilename = AgentJar.HashFileName;
    private const string TempSuffix = ".tmp";
    private const string JnlpJarsPath = "jnlpJars";
    private const int ShortHashLength = 12;
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

    public async Task Download(Uri jenkinsUri, string destinationPath, CancellationToken ct)
    {
        var jarPath = Path.Combine(destinationPath, JarFilename);
        var hashPath = Path.Combine(destinationPath, HashFilename);
        var jarUri = BuildJarUri(jenkinsUri);

        // Deliberately not "Downloading …": this runs before the conditional GET, so at this point no
        // download is known to be needed. The old wording appeared on every start and made a working cache
        // look broken — and, once the cache really did break, hid it in a line that had cried wolf for months.
        _logger.LogInformation("Checking {Jar} against {Uri}", JarFilename, jarUri);

        // Freshness pass: conditional GET honoring the stored validator. A 200 streams the new jar and records
        // its SHA-256, so the hash always matches by construction — no further check needed.
        if (await Fetch(jarUri, jarPath, destinationPath, hashPath, conditional: true, ct) == FetchOutcome.Downloaded)
        {
            return;
        }

        // Server reports Not-Modified. Confirm the cached jar still matches the hash we recorded when we
        // downloaded it — a mismatch means it was altered or corrupted since, so it must not be launched.
        if (await LocalJarMatchesHash(jarPath, hashPath, ct))
        {
            _logger.LogInformation("{Jar} is up to date (server reports Not Modified) and SHA-256 verified — skipping download", JarFilename);
            return;
        }

        // Local copy failed verification: force an unconditional re-download (no If-None-Match) so the
        // server's authoritative jar replaces the bad one and a fresh hash is stored.
        _logger.LogWarning("{Jar} failed SHA-256 verification (tampered or corrupt) — forcing a fresh download", JarFilename);
        await Fetch(jarUri, jarPath, destinationPath, hashPath, conditional: false, ct);
    }

    private enum FetchOutcome
    {
        Downloaded,
        NotModified,
    }

    // One GET. Conditional replays the stored validator (when a jar+validator pair exists) so an unchanged
    // server jar returns 304; unconditional always re-fetches. A 200 streams the body atomically, then
    // persists the new validator and the SHA-256 of what landed on disk.
    private async Task<FetchOutcome> Fetch(
        Uri jarUri, string jarPath, string cacheDir, string hashPath, bool conditional, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, jarUri);

        // Only send the conditional header when the jar file itself exists — an orphaned validator with no
        // jar would cause a 304 and leave the caller with a missing agent.jar.
        var stored = conditional && File.Exists(jarPath) ? JarCacheValidator.Read(cacheDir) : null;
        if (stored is { } validator)
        {
            request.Headers.TryAddWithoutValidation(validator.ConditionalHeader, validator.Value);
            _logger.LogDebug("Conditional GET for {Jar} with {Header}: {Value}",
                JarFilename, validator.ConditionalHeader, validator.Value);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return FetchOutcome.NotModified;
        }

        response.EnsureSuccessStatusCode();

        var bytesWritten = await StreamToFile(response, jarPath, ct);
        SaveValidator(response, cacheDir);
        var hash = await SaveHash(jarPath, hashPath, ct);

        _logger.LogInformation("{Jar} downloaded successfully ({Bytes} bytes, sha256 {Hash})",
            JarFilename, bytesWritten, hash[..Math.Min(ShortHashLength, hash.Length)]);
        return FetchOutcome.Downloaded;
    }

    private static Uri BuildJarUri(Uri jenkinsUri) =>
        new Uri(jenkinsUri, $"{JnlpJarsPath}/{JarFilename}");

    // Stream to a temp file then atomically move it into place, so a failure mid-download (network drop,
    // cancellation) can never leave a truncated agent.jar behind the still-valid previous validator — which
    // would 304 on the next start and launch a corrupt jar.
    private static async Task<long> StreamToFile(HttpResponseMessage response, string jarPath, CancellationToken ct)
    {
        var tempPath = jarPath + TempSuffix;
        try
        {
            long length;
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, ct);
                length = fs.Length;
            }

            File.Move(tempPath, jarPath, overwrite: true);
            return length;
        }
        catch
        {
            // Best-effort cleanup of the partial download; a leftover .tmp is harmless (never used).
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Records the freshness validator the server just gave us in its own sidecar — the ETag-sidecar if an
    /// <c>ETag</c> was offered, otherwise the Modified-sidecar. The other one is always removed: exactly one
    /// token describes the jar on disk, and a stale second copy is a disagreement waiting to be resolved the
    /// wrong way. A response carrying neither validator leaves nothing cacheable, so both go.
    /// </summary>
    private void SaveValidator(HttpResponseMessage response, string cacheDir)
    {
        var chosen = JarCacheValidator.From(response);

        foreach (var kind in Enum.GetValues<ValidatorKind>())
        {
            if (chosen?.Kind != kind)
            {
                TryDelete(JarCacheValidator.PathFor(cacheDir, kind));
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
        File.WriteAllText(Path.Combine(cacheDir, validator.SidecarFileName), validator.Value);
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
    private async Task<string> SaveHash(string jarPath, string hashPath, CancellationToken ct)
    {
        var hash = await ComputeSha256(jarPath, ct);
        await File.WriteAllTextAsync(hashPath, hash, ct);
        _logger.LogDebug("Saved SHA-256 {Hash} for {Jar}", hash, JarFilename);
        return hash;
    }

    // True only when both the jar and its stored hash exist and the jar hashes to the stored value.
    private static async Task<bool> LocalJarMatchesHash(string jarPath, string hashPath, CancellationToken ct)
    {
        if (!File.Exists(jarPath) || !File.Exists(hashPath))
        {
            return false;
        }

        string stored;
        try
        {
            stored = (await File.ReadAllTextAsync(hashPath, ct)).Trim();
        }
        catch (IOException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var actual = await ComputeSha256(jarPath, ct);
        return string.Equals(stored, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256(string jarPath, CancellationToken ct)
    {
        await using var fs = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
