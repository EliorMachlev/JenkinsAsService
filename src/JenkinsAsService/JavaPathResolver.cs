// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Locates <c>java.exe</c> from the configured path or <c>JAVA_HOME</c>, trying the directory itself and
/// its <c>bin</c> sub-folder. Throws when none of the candidates contain the executable.
/// </summary>
internal static class JavaPathResolver
{
    private const int MaxCandidates = 3;
    private const string JavaExeFilename = "java.exe";
    private const string JavaBinFolder = "bin";

    internal static string Resolve(string? configuredPath, string? javaHome)
    {
        var candidates = new List<string>(MaxCandidates);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(javaHome);
            candidates.Add(Path.Combine(javaHome, JavaBinFolder));
        }

        foreach (var path in candidates)
        {
            var exe = Path.Combine(path, JavaExeFilename);
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        throw new InvalidOperationException(
            "Cannot find java.exe. Set 'Agent:JavaPath' in appsettings.json or the JAVA_HOME environment variable.");
    }
}
