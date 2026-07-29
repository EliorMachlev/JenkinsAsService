// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>What the watchdog should do with a migration the policy has already approved.</summary>
internal enum MigrationVerdict
{
    /// <summary>Kill and relaunch the agent.</summary>
    Proceed,

    /// <summary>Repeat of a migration that changed nothing — skip it, and tell the operator why (once).</summary>
    SuppressAndReport,

    /// <summary>Still suppressed for the same reason, already reported.</summary>
    Suppress,
}

/// <summary>
/// Guards against a session migration that cannot succeed repeating forever.
/// <para>
/// A migration is a kill and relaunch, and the relaunch re-runs full bring-up — which falls back to a headless
/// Session 0 launch whenever the interactive launch fails. So when the *launch* is what is broken (wrong service
/// identity, <c>ERROR_ACCESS_DENIED</c> from the termsrv access check, privilege not granted) rather than the
/// session having moved, <see cref="InteractiveSessionSelector.ShouldMigrate"/> keeps answering "yes, upgrade
/// this headless agent" and every stability window drops the Jenkins node and kills the running build. Migrations
/// deliberately bypass the crash counter, so <c>Recovery:MaxRetries</c> would never stop it.
/// </para>
/// <para>
/// The tracker remembers the state each migration was ordered from — the agent's session plus the rendered
/// session table — and refuses an identical repeat. It self-clears: any change to either half is a new origin,
/// so a genuinely-moved session still migrates.
/// </para>
/// </summary>
internal sealed class SessionMigrationTracker
{
    private (uint? Session, string Sessions)? _lastOrigin;
    private bool _reportedStall;

    /// <summary>
    /// Records an approved migration and says whether it may go ahead.
    /// </summary>
    /// <param name="currentSession">The agent's session, or <c>null</c> when it is running headless.</param>
    /// <param name="sessionSnapshot">
    /// The rendered session table (<see cref="InteractiveSessionSelector.Describe(IReadOnlyList{SessionInfo})"/>).
    /// Used as the "has anything changed out there?" fingerprint — it already carries every field selection
    /// depends on (id, user, connect state, WinStation).
    /// </param>
    internal MigrationVerdict Evaluate(uint? currentSession, string sessionSnapshot)
    {
        var origin = (Session: currentSession, Sessions: sessionSnapshot);
        if (_lastOrigin == origin)
        {
            if (_reportedStall)
            {
                return MigrationVerdict.Suppress;
            }

            _reportedStall = true;
            return MigrationVerdict.SuppressAndReport;
        }

        _lastOrigin = origin;
        _reportedStall = false;
        return MigrationVerdict.Proceed;
    }
}
