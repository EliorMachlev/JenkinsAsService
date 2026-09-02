// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Reads the installed <c>appsettings.json</c> defensively, for the CLI verbs that run against an existing
/// installation rather than creating one.
/// <para>
/// Those verbs run as MSI custom actions during uninstall and upgrade, when the config may be absent,
/// half-written or unreadable — none of which may throw out of a custom action. The file is also the thing
/// <c>purge</c> is about to delete, so it is read once, up front, and never assumed.
/// </para>
/// </summary>
internal static class InstalledConfig
{
    /// <summary>
    /// Returns the <c>Jenkins</c> section of the config beside <paramref name="basePath"/>, or
    /// <see langword="null"/> if it could not be read. A missing file is not an error — it binds empty,
    /// which correctly means "everything at its default".
    /// </summary>
    internal static IConfigurationSection? TryReadJenkinsSection(string basePath, Action<string>? onError = null)
    {
        try
        {
            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(ConfigKeys.FileName, optional: true)
                .Build()
                .GetSection(ConfigKeys.Section);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            onError?.Invoke($"Could not read {ConfigKeys.FileName} ({ex.Message}).");
            return null;
        }
    }
}
