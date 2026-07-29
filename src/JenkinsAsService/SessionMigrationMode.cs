// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// How aggressively the watchdog re-targets the interactive session while the agent is already running
/// (<c>Jenkins:Agent:LaunchInInteractiveSession:SessionMigration</c>).
/// <para>
/// A process cannot be moved between sessions, so every migration is a kill-and-relaunch of the Java agent:
/// the Jenkins node drops and any build in flight dies. That cost is why the default is <see cref="Off"/>.
/// </para>
/// </summary>
public enum SessionMigrationMode
{
    /// <summary>Default. The session is chosen once at launch and never revisited.</summary>
    Off = 0,

    /// <summary>
    /// Relaunch only when the session the agent is running in is gone (signed out, reset, ended). Nothing is
    /// lost that was not already lost — the desktop the agent was placed on no longer exists.
    /// </summary>
    OnSessionLost = 1,

    /// <summary>
    /// Also relaunch when a <em>better</em> session appears — an RDP session starting while the agent sits on
    /// the autologon console, or a headless Session 0 fallback that can now be upgraded to a real desktop.
    /// Keeps the agent on the preferred desktop at the cost of restarting it whenever that changes.
    /// </summary>
    Always = 2,
}
