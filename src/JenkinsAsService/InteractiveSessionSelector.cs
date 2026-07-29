// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// <c>WTS_CONNECTSTATE_CLASS</c> — the connection state of a terminal-services session.
/// </summary>
internal enum SessionConnectState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9,
}

/// <summary>One enumerated session. <paramref name="UserName"/> is null/empty when nobody is logged on.</summary>
internal readonly record struct SessionInfo(uint SessionId, string? UserName, SessionConnectState State);

/// <summary>Why no session could be targeted — drives the operator-facing warning.</summary>
internal enum SessionSelectionFailure
{
    None = 0,

    /// <summary>No session has a logged-on user (fresh boot with nobody signed in, or console at LogonUI).</summary>
    NoInteractiveSession,

    /// <summary>Sessions exist, but none belongs to the pinned <c>TargetUser</c>.</summary>
    TargetUserNotLoggedOn,
}

/// <summary>
/// Pure session-selection policy for the interactive launch — the counterpart to <see cref="AgentTransport"/>.
/// <para>
/// Kept free of interop so the part most likely to be wrong is unit-testable; <see cref="InteractiveSessionLauncher"/>
/// supplies the enumerated sessions and performs the token call.
/// </para>
/// <para>
/// The policy deliberately does <em>not</em> use <c>WTSGetActiveConsoleSessionId</c>. That returns the physical
/// console session even when nobody is logged into it (state <c>Connected</c>, no user — the logon screen), which
/// produced <c>ERROR_NO_TOKEN</c> on a server whose only interactive desktop was an RDP session. A
/// <em>disconnected</em> session is a first-class target: closing an RDP window leaves the user logged on and the
/// desktop alive, which is exactly the setup a visible-browser build node relies on.
/// </para>
/// </summary>
internal static class InteractiveSessionSelector
{
    /// <summary>Session 0 is the non-interactive services session — never a valid target.</summary>
    internal const uint ServicesSessionId = 0;

    /// <summary>
    /// Picks the session to launch the agent in. Prefers an <see cref="SessionConnectState.Active"/> session,
    /// then the lowest session id, so the choice is stable across restarts when several qualify.
    /// </summary>
    /// <param name="sessions">Sessions as enumerated from the OS.</param>
    /// <param name="targetUser">
    /// Optional user to pin to (<c>Agent:LaunchInInteractiveSession:TargetUser</c>). Matched case-insensitively
    /// against the session's user name; an optional <c>DOMAIN\</c> prefix is ignored. When empty, any logged-on
    /// user qualifies.
    /// </param>
    /// <param name="preferDisconnected">
    /// When <c>true</c>, rank sessions that are <em>not</em> <see cref="SessionConnectState.Active"/> first.
    /// An unattended build desktop (autologon console, or an RDP session whose window was closed) sits
    /// non-Active, while an <c>Active</c> session usually belongs to somebody working at that moment — so
    /// this keeps the agent off an operator's desktop when they sign in to watch.
    /// </param>
    internal static bool TrySelect(
        IReadOnlyList<SessionInfo> sessions,
        string? targetUser,
        out uint sessionId,
        out SessionSelectionFailure failure,
        bool preferDisconnected = false)
    {
        sessionId = 0;

        var candidates = sessions
            .Where(IsUsableTarget)
            .OrderBy(s => Rank(s.State, preferDisconnected))
            .ThenBy(s => TieBreak(s.SessionId, preferDisconnected))
            .ToList();

        if (candidates.Count == 0)
        {
            failure = SessionSelectionFailure.NoInteractiveSession;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetUser))
        {
            candidates = candidates.Where(s => MatchesUser(s.UserName, targetUser)).ToList();
            if (candidates.Count == 0)
            {
                failure = SessionSelectionFailure.TargetUserNotLoggedOn;
                return false;
            }
        }

        sessionId = candidates[0].SessionId;
        failure = SessionSelectionFailure.None;
        return true;
    }

    /// <summary>Primary ordering key — which connect state is favoured.</summary>
    private static int Rank(SessionConnectState state, bool preferDisconnected)
    {
        var isActive = state == SessionConnectState.Active;
        return preferDisconnected
            ? isActive ? 1 : 0
            : isActive ? 0 : 1;
    }

    /// <summary>
    /// Secondary ordering key. Session ids are handed out in increasing order, so the <em>highest</em> id is
    /// the most recently created session. Under <c>preferDisconnected</c> that is the one to take: stale
    /// disconnected sessions accumulate over a machine's uptime, and the newest is the live desktop. The
    /// default direction stays lowest-first, which keeps a plain console-session setup on session 1.
    /// </summary>
    private static long TieBreak(uint sessionId, bool preferDisconnected) =>
        preferDisconnected ? -(long)sessionId : sessionId;

    /// <summary>
    /// A session can host the agent when it is interactive, has somebody logged on, and is in a state that
    /// still owns a desktop. The user name — not the connect state — is the authoritative signal that a token
    /// exists, so the state filter only excludes the definitively dead states (listeners, resetting, down).
    /// <c>Connected</c> is allowed: an autologon console session sits there while an RDP client is attached.
    /// </summary>
    private static bool IsUsableTarget(SessionInfo session) =>
        session.SessionId != ServicesSessionId
        && !string.IsNullOrWhiteSpace(session.UserName)
        && session.State is SessionConnectState.Active
            or SessionConnectState.Connected
            or SessionConnectState.Disconnected;

    /// <summary>Matches a session user against the configured target, tolerating a <c>DOMAIN\</c> prefix.</summary>
    private static bool MatchesUser(string? sessionUser, string targetUser)
    {
        if (string.IsNullOrWhiteSpace(sessionUser))
        {
            return false;
        }

        var target = targetUser.Trim();
        var separator = target.LastIndexOf('\\');
        if (separator >= 0)
        {
            target = target[(separator + 1)..];
        }

        return string.Equals(sessionUser.Trim(), target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Renders the enumerated sessions for the diagnostic warning, e.g. <c>1:&lt;none&gt;/Connected</c>.</summary>
    internal static string Describe(IReadOnlyList<SessionInfo> sessions)
    {
        var interactive = sessions.Where(s => s.SessionId != ServicesSessionId).ToList();
        return interactive.Count == 0
            ? "<none>"
            : string.Join(", ", interactive.Select(s =>
                $"{s.SessionId}:{(string.IsNullOrWhiteSpace(s.UserName) ? "<none>" : s.UserName)}/{s.State}"));
    }
}
