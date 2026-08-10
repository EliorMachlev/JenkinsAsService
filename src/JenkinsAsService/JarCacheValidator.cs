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
    /// <summary>The request header that replays this validator to the server.</summary>
    internal string ConditionalHeader => Kind == ValidatorKind.ETag ? "If-None-Match" : "If-Modified-Since";

    /// <summary>The sidecar this kind is stored in.</summary>
    internal string SidecarFileName => FileNameFor(Kind);

    private static string FileNameFor(ValidatorKind kind) =>
        kind == ValidatorKind.ETag ? AgentJar.ETagSidecarFileName : AgentJar.ModifiedSidecarFileName;

    internal static string PathFor(string directory, ValidatorKind kind) =>
        Path.Combine(directory, FileNameFor(kind));

    /// <summary>
    /// Picks the strongest validator the response offers, or <c>null</c> when it offers none — in which case
    /// the jar simply cannot be cached and the caller must not keep a stale token around.
    /// </summary>
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
    /// Reads whichever sidecar is present in <paramref name="directory"/>, preferring the ETag-sidecar.
    /// Returns <c>null</c> when neither holds a usable value, which produces an unconditional GET.
    /// </summary>
    internal static JarCacheValidator? Read(string directory) =>
        ReadKind(directory, ValidatorKind.ETag) ?? ReadKind(directory, ValidatorKind.LastModified);

    private static JarCacheValidator? ReadKind(string directory, ValidatorKind kind)
    {
        var path = PathFor(directory, kind);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = File.ReadAllText(path).Trim();
        return value.Length == 0 ? null : new JarCacheValidator(kind, value);
    }
}
