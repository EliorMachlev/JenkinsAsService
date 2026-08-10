// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Every path inside the agent-binary cache directory, derived once.
/// <para>
/// The downloader needs the jar, its temp file, its hash sidecar and either validator sidecar, and threading
/// four strings through each call made the signatures say nothing. Deriving them in one place also keeps
/// <see cref="Path.Combine(string, string)"/> off the paths that run per download attempt.
/// </para>
/// </summary>
internal readonly record struct JarCachePaths
{
    /// <summary>Suffix of the in-progress download, moved onto <see cref="Jar"/> only once complete.</summary>
    private const string TempSuffix = ".tmp";

    internal JarCachePaths(string cacheDirectory)
    {
        Directory = cacheDirectory;
        Jar = Path.Combine(cacheDirectory, AgentJar.FileName);
        Hash = Path.Combine(cacheDirectory, AgentJar.HashFileName);
        Temp = Jar + TempSuffix;
    }

    /// <summary>The cache directory itself (<c>&lt;data&gt;\agent\</c>).</summary>
    internal string Directory { get; }

    /// <summary>The cached agent binary the worker launches.</summary>
    internal string Jar { get; }

    /// <summary>Sidecar holding the SHA-256 recorded at download time.</summary>
    internal string Hash { get; }

    /// <summary>Scratch file the response body streams into.</summary>
    internal string Temp { get; }

    /// <summary>The sidecar for <paramref name="kind"/>, whether or not it exists.</summary>
    internal string Sidecar(ValidatorKind kind) =>
        Path.Combine(Directory, JarCacheValidator.SidecarFileNameFor(kind));
}
