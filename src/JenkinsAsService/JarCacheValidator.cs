// Copyright (c) 2024 All rights reserved

using System.Globalization;

namespace JenkinsAsService;

/// <summary>Which HTTP validator the cached jar was stamped with.</summary>
internal enum ValidatorKind
{
    /// <summary>An <c>ETag</c>, held in the ETag-sidecar and replayed as <c>If-None-Match</c>.</summary>
    ETag,

    /// <summary>A <c>Last-Modified</c> date, held in the Modified-sidecar and replayed as <c>If-Modified-Since</c>.</summary>
    LastModified,
}

/// <summary>
/// The freshness token stored beside the cached <c>agent.jar</c>, and the rules for round-tripping it.
/// <para>
/// This started as an ETag-only cache, and a live controller was then observed serving
/// <c>/jnlpJars/agent.jar</c> with <c>Last-Modified</c> and <em>no</em> <c>ETag</c> — against which the cache
/// never worked at all. With nothing to store, every start re-downloaded the whole jar; and because a
/// missing validator also deleted the sidecar, the miss was self-sustaining and could never recover.
/// </para>
/// <para>
/// Each kind gets its own file — the ETag-sidecar and the Modified-sidecar — so the name on disk says which
/// validator it holds and neither has to be parsed to find out. Exactly one exists at a time: writing either
/// removes the other, because two tokens for one jar is a disagreement waiting to be resolved wrongly.
/// <c>ETag</c> is preferred when the response offers both: it is an exact-identity match, whereas
/// <c>Last-Modified</c> has one-second granularity and depends on the controller's clock.
/// </para>
/// </summary>
internal readonly record struct JarCacheValidator(ValidatorKind Kind, string Value)
{
    /// <summary>
    /// Ceiling on a sidecar file, in bytes. Real validators are tens of characters; anything larger is
    /// corruption or mischief, and is refused without being read into memory.
    /// </summary>
    private const long MaxSidecarBytes = 1024;

    /// <summary>
    /// Cached because <see cref="Enum.GetValues{T}"/> allocates a fresh array on every call, and this is
    /// walked on each download to clear the sidecar that was not chosen.
    /// </summary>
    internal static ReadOnlySpan<ValidatorKind> AllKinds => [ValidatorKind.ETag, ValidatorKind.LastModified];

    /// <summary>The request header that replays this validator to the server.</summary>
    internal string ConditionalHeader => HeaderFor(Kind);

    /// <summary>The sidecar this kind is stored in.</summary>
    internal string SidecarFileName => SidecarFileNameFor(Kind);

    internal static string HeaderFor(ValidatorKind kind) =>
        kind == ValidatorKind.ETag ? "If-None-Match" : "If-Modified-Since";

    internal static string SidecarFileNameFor(ValidatorKind kind) =>
        kind == ValidatorKind.ETag ? AgentJar.ETagSidecarFileName : AgentJar.ModifiedSidecarFileName;

    /// <summary>
    /// Picks the strongest validator the response offers, or <c>null</c> when it offers none — in which case
    /// the jar simply cannot be cached and the caller must not keep a stale token around.
    /// </summary>
    /// <remarks>
    /// Both values arrive already parsed by <c>HttpClient</c> — an <c>EntityTagHeaderValue</c> and a
    /// <see cref="DateTimeOffset"/> — so neither can carry the control characters that
    /// <see cref="Read(string)"/> has to screen for.
    /// </remarks>
    internal static JarCacheValidator? From(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(etag))
        {
            return new JarCacheValidator(ValidatorKind.ETag, etag.Trim());
        }

        // Last-Modified is a *content* header in HttpClient's model, not a response header.
        var lastModified = response.Content.Headers.LastModified;
        return lastModified is null
            ? null
            : new JarCacheValidator(ValidatorKind.LastModified, FormatHttpDate(lastModified.Value));
    }

    /// <summary>RFC 1123 / HTTP-date, in the invariant culture — the only form If-Modified-Since accepts.</summary>
    private static string FormatHttpDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads whichever sidecar is present in <paramref name="cacheDirectory"/>, preferring the ETag-sidecar.
    /// Returns <c>null</c> when neither holds a usable value, which produces an unconditional GET — the safe
    /// direction, since it costs bandwidth rather than correctness.
    /// </summary>
    internal static JarCacheValidator? Read(string cacheDirectory) =>
        ReadKind(cacheDirectory, ValidatorKind.ETag) ?? ReadKind(cacheDirectory, ValidatorKind.LastModified);

    private static JarCacheValidator? ReadKind(string cacheDirectory, ValidatorKind kind)
    {
        var file = new FileInfo(Path.Combine(cacheDirectory, SidecarFileNameFor(kind)));

        // Length is checked before the read, so an oversized file is refused rather than loaded.
        if (!file.Exists || file.Length is 0 or > MaxSidecarBytes)
        {
            return null;
        }

        string value;
        try
        {
            value = File.ReadAllText(file.FullName).Trim();
        }
        catch (IOException)
        {
            // Locked or vanished between the stat and the read: fall back to an unconditional GET.
            return null;
        }

        return value.Length > 0 && IsSafeHeaderValue(value) ? new JarCacheValidator(kind, value) : null;
    }

    /// <summary>
    /// Rejects control characters — <c>CR</c> and <c>LF</c> above all.
    /// <para>
    /// The stored value is replayed via <c>TryAddWithoutValidation</c>, which by design performs no checks,
    /// so an embedded newline would let whatever wrote the sidecar inject extra request headers. The cache
    /// lives under <c>%ProgramData%</c> alongside the build work directory and is writable by the service
    /// account the agent's own build steps run as, so "we wrote this file ourselves" is not an assumption
    /// worth resting a request on.
    /// </para>
    /// </summary>
    private static bool IsSafeHeaderValue(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
