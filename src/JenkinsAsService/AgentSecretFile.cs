// Copyright (c) 2024 All rights reserved

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace JenkinsAsService;

/// <summary>
/// Persists the resolved JNLP secret to an ACL-restricted file so it can be handed to the Java agent
/// via <c>-secret @&lt;file&gt;</c> rather than on the command line. Passing it inline would expose the
/// secret in the process table (<c>Get-Process</c> / WMI <c>Win32_Process.CommandLine</c>) to any local
/// administrator for the lifetime of the agent; the file route keeps it off the command line entirely.
/// </summary>
internal static class AgentSecretFile
{
    internal const string FileName = ".agent-secret";

    /// <summary>
    /// Writes <paramref name="secret"/> (no BOM, no trailing newline) to <see cref="FileName"/> in
    /// <paramref name="directory"/>, overwriting any existing file, then best-effort restricts its ACL
    /// to SYSTEM / Administrators / the writing identity. Returns the full path written.
    /// </summary>
    internal static string Write(string directory, string secret, Action<string>? warn = null)
    {
        var path = Path.Combine(directory, FileName);
        // UTF-8 without BOM: a BOM would be read by the agent as part of the secret and break the handshake.
        File.WriteAllText(path, secret, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (OperatingSystem.IsWindows())
        {
            TryRestrict(path, warn);
        }

        return path;
    }

    /// <summary>Best-effort deletion of the secret file. Never throws.</summary>
    internal static void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Held open or already gone — nothing actionable at shutdown.
        }
        catch (UnauthorizedAccessException)
        {
            // ACL prevents deletion — leave it; it is already locked down.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryRestrict(string path, Action<string>? warn)
    {
        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            var security = new FileSecurity();
            // Break inheritance and drop inherited rights so only the explicit grants below apply.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // NB: do NOT reassign the owner here. This file is created at runtime by the low-privilege
            // service account (NT SERVICE\Jenkins), which owns it. Setting the owner to Administrators
            // requires SeRestorePrivilege (or Administrators membership) that the service account lacks —
            // SetAccessControl would throw and the entire DACL below (Users-denial + the writer's Modify
            // grant) would be silently lost via the catch. Breaking inheritance + the explicit ACEs below
            // already isolate the secret; owner staying as the trusted service account is acceptable.

            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, AccessControlType.Allow));

            // The writing identity (the service account) rewrites this file on every agent (re)start and
            // deletes it at shutdown, so it needs Modify — not just Read. A low-privilege virtual account
            // (e.g. NT SERVICE\Jenkins) is not in Administrators, so without an explicit write/delete grant
            // the truncate-overwrite on the first watchdog restart fails with UnauthorizedAccessException.
            using var current = WindowsIdentity.GetCurrent();
            if (current.User is not null && current.User != system && current.User != admins)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    current.User, FileSystemRights.Modify, AccessControlType.Allow));
            }

            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or IdentityNotMappedException
                or System.Security.SecurityException
                or InvalidOperationException)
        {
            // Hardening the ACL is defense-in-depth on top of the already-restricted install folder.
            // If it fails, the secret file is still usable; surface a warning and carry on.
            warn?.Invoke($"Could not restrict ACL on agent secret file '{path}': {ex.Message}");
        }
    }
}
