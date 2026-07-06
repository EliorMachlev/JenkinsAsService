// Copyright (c) 2024 All rights reserved

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace JenkinsAsService;

/// <summary>
/// Locks down the ACL of the generated <c>appsettings.json</c> so that only SYSTEM, the local
/// Administrators group and the service account can read it. Inherited access (notably the
/// <c>Users</c> Read/Execute that Program Files grants by default) is removed, because the file may
/// contain the agent secret (Unprotected or DPAPI modes).
/// </summary>
public static class ConfigAclHardener
{
    /// <summary>
    /// Replaces the file's DACL with: SYSTEM (Full), Administrators (Full), <paramref name="serviceAccount"/> (Read),
    /// and disables inheritance. No-op on non-Windows platforms.
    /// </summary>
    public static void Harden(string filePath, string serviceAccount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            HardenWindows(filePath, serviceAccount);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException)
        {
            // Best-effort: an unresolvable account or insufficient rights must not fail the install /
            // config write. The file keeps its inherited ACL; the secret is still protected by mode.
            Console.Error.WriteLine(
                $"Warning: could not harden ACL on '{filePath}' for account '{serviceAccount}': {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void HardenWindows(string filePath, string serviceAccount)
    {
        var fileInfo = new FileInfo(filePath);
        var security = new FileSecurity();

        // Replace the DACL entirely and stop inheriting from the parent directory.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new NTAccount(serviceAccount), FileSystemRights.Read, AccessControlType.Allow));

        fileInfo.SetAccessControl(security);
    }
}
