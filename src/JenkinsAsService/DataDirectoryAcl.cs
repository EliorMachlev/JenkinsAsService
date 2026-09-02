// Copyright (c) 2024 All rights reserved

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace JenkinsAsService;

/// <summary>
/// Grants the (low-privilege) service account Modify access to the runtime data directory.
/// <para>
/// Deliberately the opposite shape to <see cref="ConfigAclHardener"/>: that one replaces the config file's
/// DACL and switches inheritance off, because the file may hold the secret. Here the DACL is added to and
/// inheritance left on, so the folder keeps its inherited SYSTEM/Administrators full control while becoming
/// writable by the agent identity. The install folder stays read-only.
/// </para>
/// <para>Applied by the MSI's <c>grant-data-access</c> custom action; see the installer .wixproj for why.</para>
/// </summary>
public static class DataDirectoryAcl
{
    /// <summary>
    /// Adds an inheritable Modify grant for <paramref name="serviceAccount"/> to
    /// <paramref name="directoryPath"/>, preserving every existing entry and the folder's inheritance.
    /// Idempotent — re-running replaces the account's own entry rather than accumulating duplicates.
    /// No-op on non-Windows platforms.
    /// </summary>
    /// <returns><see langword="true"/> when the grant was applied (or is not applicable to this platform).</returns>
    public static bool Grant(string directoryPath, string serviceAccount, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            GrantWindows(directoryPath, serviceAccount);
            log?.Invoke($"Granted '{serviceAccount}' Modify access to '{directoryPath}'.");
            return true;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or IOException)
        {
            // Reported rather than thrown, so the caller decides whether this fails the install.
            log?.Invoke(
                $"Error: could not grant '{serviceAccount}' access to '{directoryPath}': {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantWindows(string directoryPath, string serviceAccount)
    {
        // Normally already created by the DataFolderAcl component; created here so a missing directory is
        // not an install failure for the very thing this action exists to set up.
        var directory = Directory.CreateDirectory(directoryPath);

        var security = directory.GetAccessControl(AccessControlSections.Access);

        // SetAccessRule, not AddAccessRule: a re-run (repair, upgrade, changed identity) must replace this
        // account's entry, not stack another one. No SetAccessRuleProtection call, so inheritance stays on.
        security.SetAccessRule(new FileSystemAccessRule(
            new NTAccount(serviceAccount),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        directory.SetAccessControl(security);
    }
}
