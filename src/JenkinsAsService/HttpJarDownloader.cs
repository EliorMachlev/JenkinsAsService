// Copyright (c) 2024 All rights reserved

using System.Net;
using System.Security.Cryptography;

namespace JenkinsAsService;

public sealed class HttpJarDownloader : IJarDownloader
{
    private const string JarFilename = "agent.jar"; // intentional: decoupled from JenkinsAgentWorker
    private const string ETagFilename = "agent.jar.etag";
    private const string HashFilename = "agent.jar.sha256";
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
        var etagPath = Path.Combine(destinationPath, ETagFilename);
        var hashPath = Path.Combine(destinationPath, HashFilename);
        var jarUri = BuildJarUri(jenkinsUri);

        _logger.LogInformation("Downloading {Jar} from {Uri}", JarFilename, jarUri);

        // Freshness pass: conditional GET honoring the stored ETag. A 200 streams the new jar and records
        // its SHA-256, so the hash always matches by construction — no further check needed.
        if (await Fetch(jarUri, jarPath, etagPath, hashPath, conditional: true, ct) == FetchOutcome.Downloaded)
        {
            return;
        }

        // Server reports Not-Modified. Confirm the cached jar still matches the hash we recorded when we
        // downloaded it — a mismatch means it was altered or corrupted since, so it must not be launched.
        if (await LocalJarMatchesHash(jarPath, hashPath, ct))
        {
            _logger.LogInformation("{Jar} is up to date (ETag match) and SHA-256 verified — skipping download", JarFilename);
            return;
        }

        // Local copy failed verification: force an unconditional re-download (no If-None-Match) so the
        // server's authoritative jar replaces the bad one and a fresh hash is stored.
        _logger.LogWarning("{Jar} failed SHA-256 verification (tampered or corrupt) — forcing a fresh download", JarFilename);
        await Fetch(jarUri, jarPath, etagPath, hashPath, conditional: false, ct);
    }

    private enum FetchOutcome
    {
        Downloaded,
        NotModified,
    }

    // One GET. Conditional adds If-None-Match (when a jar+etag pair exists) so an unchanged server jar
    // returns 304; unconditional always re-fetches. A 200 streams the body atomically, then persists the
    // ETag and the SHA-256 of what landed on disk.
    private async Task<FetchOutcome> Fetch(
        Uri jarUri, string jarPath, string etagPath, string hashPath, bool conditional, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, jarUri);

        // Only send If-None-Match when the jar file itself exists — an orphaned .etag with no jar
        // would cause a 304 and leave the caller with a missing agent.jar.
        var storedEtag = conditional && File.Exists(jarPath) ? ReadStoredEtag(etagPath) : null;
        if (storedEtag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", storedEtag);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return FetchOutcome.NotModified;
        }

        response.EnsureSuccessStatusCode();

        var bytesWritten = await StreamToFile(response, jarPath, ct);
        SaveEtag(response, etagPath);
        var hash = await SaveHash(jarPath, hashPath, ct);

        _logger.LogInformation("{Jar} downloaded successfully ({Bytes} bytes, sha256 {Hash})",
            JarFilename, bytesWritten, hash[..Math.Min(ShortHashLength, hash.Length)]);
        return FetchOutcome.Downloaded;
    }

    private static Uri BuildJarUri(Uri jenkinsUri) =>
        new Uri(jenkinsUri, $"{JnlpJarsPath}/{JarFilename}");

    private static string? ReadStoredEtag(string etagPath)
    {
        if (!File.Exists(etagPath))
        {
            return null;
        }

        var value = File.ReadAllText(etagPath).Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Stream to a temp file then atomically move it into place, so a failure mid-download (network drop,
    // cancellation) can never leave a truncated agent.jar behind the still-valid previous .etag — which
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
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the partial download; a leftover .tmp is harmless (never used).
        }
        catch (UnauthorizedAccessException)
        {
            // Same — the stale temp file is never read.
        }
    }

    private void SaveEtag(HttpResponseMessage response, string etagPath)
    {
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrEmpty(etag))
        {
            // No ETag from server — drop any stale etag file so next call is an unconditional GET.
            if (File.Exists(etagPath))
            {
                File.Delete(etagPath);
            }

            return;
        }

        _logger.LogDebug("Saving ETag {ETag} for {Jar}", etag, JarFilename);
        File.WriteAllText(etagPath, etag);
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
