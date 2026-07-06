// Copyright (c) 2024 All rights reserved

using System.Net;

namespace JenkinsAsService;

public sealed class HttpJarDownloader : IJarDownloader
{
    private const string JarFilename = "agent.jar"; // intentional: decoupled from JenkinsAgentWorker
    private const string ETagFilename = "agent.jar.etag";
    private const string TempSuffix = ".tmp";
    private const string JnlpJarsPath = "jnlpJars";
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
        var jarUri = BuildJarUri(jenkinsUri);

        _logger.LogInformation("Downloading {Jar} from {Uri}", JarFilename, jarUri);

        using var client = _httpFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, jarUri);

        // Only send If-None-Match when the jar file itself exists — an orphaned .etag with no jar
        // would cause a 304 and leave the caller with a missing agent.jar.
        var storedEtag = File.Exists(jarPath) ? ReadStoredEtag(etagPath) : null;
        if (storedEtag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", storedEtag);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _logger.LogInformation("{Jar} is up to date (ETag match) — skipping download", JarFilename);
            return;
        }

        response.EnsureSuccessStatusCode();

        var bytesWritten = await StreamToFile(response, jarPath, ct);
        SaveEtag(response, etagPath);

        _logger.LogInformation("{Jar} downloaded successfully ({Bytes} bytes)", JarFilename, bytesWritten);
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
}
