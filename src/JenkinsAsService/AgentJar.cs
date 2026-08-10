// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Names of the cached Jenkins agent binary and its integrity sidecars.
/// <para>
/// These were previously declared separately in <see cref="HttpJarDownloader"/> and
/// <see cref="JenkinsAgentWorker"/>, described as "intentionally decoupled". They are the opposite of
/// decoupled: the downloader writes this file and the worker launches it from the same directory, so the two
/// names must be <em>identical</em> or the service starts a jar that was never downloaded. One definition
/// makes that agreement structural instead of a convention nobody can see.
/// </para>
/// </summary>
internal static class AgentJar
{
    /// <summary>The agent binary, as served by the controller under <c>/jnlpJars/</c>.</summary>
    internal const string FileName = "agent.jar";

    /// <summary>ETag-sidecar: the cached jar's <c>ETag</c>, replayed as <c>If-None-Match</c>.</summary>
    internal const string ETagSidecarFileName = FileName + ".etag";

    /// <summary>
    /// Modified-sidecar: the cached jar's <c>Last-Modified</c> date, replayed as <c>If-Modified-Since</c>.
    /// Used when the controller offers no ETag. See <see cref="JarCacheValidator"/>.
    /// </summary>
    internal const string ModifiedSidecarFileName = FileName + ".modified";

    /// <summary>Sidecar holding the SHA-256 recorded at download time, re-verified before reuse.</summary>
    internal const string HashFileName = FileName + ".sha256";
}
