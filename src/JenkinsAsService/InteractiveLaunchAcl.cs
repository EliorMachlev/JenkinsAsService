// Copyright (c) 2024 All rights reserved

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace JenkinsAsService;

/// <summary>
/// Grants the interactive session's user the access the Java agent needs, for the case the rest of the service
/// never has to think about: <strong>the agent runs as a different identity than the service</strong>.
/// <para>
/// In a normal Session 0 launch the child inherits the service's identity, so every runtime path the service
/// created is already accessible. An interactive launch uses <c>CreateProcessAsUser</c> with the session user's
/// token, so the agent runs as that user — who has no rights to the secret file (DACL: SYSTEM, Administrators,
/// and the <em>writing</em> identity) and none to the data directory (granted by the MSI to the <em>service</em>
/// account). Both are silent, confusing failures: an unreadable secret looks like a bad secret, and an
/// unwritable work directory looks like a broken agent.
/// </para>
/// <para>
/// Every grant is best-effort and narrow. Note what is deliberately <em>not</em> granted: write access to the
/// data directory root, which holds the logs and the secret file, and write access to <c>agent\</c>, which holds
/// <c>agent.jar</c>. The agent identity gets <c>Modify</c> on <c>work\</c> and read-only on <c>agent\</c>, which
/// preserves the binary/workspace separation that exists so a build step cannot clobber the jar the watchdog
/// launches. Directory traversal to reach them needs no grant — <c>Everyone</c> holds
/// <c>SeChangeNotifyPrivilege</c> (bypass traverse checking) by default.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InteractiveLaunchAcl
{
    /// <summary>
    /// Applies every grant the session user needs. No-op when the session user <em>is</em> the service identity
    /// (the matched-identity setup, where nothing is needed).
    /// </summary>
    /// <param name="sessionUser">SID of the user whose session the agent is being launched into.</param>
    internal static void GrantTo(SecurityIdentifier sessionUser, ServiceSettings settings, ILogger logger)
    {
        using (var current = WindowsIdentity.GetCurrent())
        {
            if (current.User is { } serviceUser && sessionUser.Equals(serviceUser))
            {
                return; // service and agent are the same account — every path is already accessible
            }
        }

        var dataDir = DataPaths.ResolveDataDirectory(settings.Agent.DataDirectory);

        if (settings.Secret.ViaFile)
        {
            var secretPath = Path.Combine(dataDir, AgentSecretFile.FileName);
            if (File.Exists(secretPath))
            {
                AgentSecretFile.GrantRead(secretPath, sessionUser, msg => logger.LogWarning("{Warning}", msg));
            }
        }

        GrantDirectory(
            DataPaths.ResolveWorkDirectory(dataDir), sessionUser, FileSystemRights.Modify, logger);
        GrantDirectory(
            DataPaths.ResolveAgentDirectory(dataDir), sessionUser, FileSystemRights.ReadAndExecute, logger);
    }

    private static void GrantDirectory(
        string path, SecurityIdentifier user, FileSystemRights rights, ILogger logger)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            var security = dir.GetAccessControl();
            // Inherit onto existing and future children so the agent keeps access as it creates its workspace.
            security.AddAccessRule(new FileSystemAccessRule(
                user, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dir.SetAccessControl(security);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or IdentityNotMappedException
                or System.Security.SecurityException
                or InvalidOperationException
                or IOException)
        {
            logger.LogWarning(
                "Could not grant {Rights} on '{Path}' to the interactive session user: {Message}. The agent " +
                "may fail to use its work directory unless that account already has access.",
                rights, path, ex.Message);
        }
    }
}
