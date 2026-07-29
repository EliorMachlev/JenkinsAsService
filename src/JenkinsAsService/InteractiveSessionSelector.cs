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

/// <summary>
/// One enumerated session. <paramref name="UserName"/> is null/empty when nobody is logged on.
/// <paramref name="WinStationName"/> is the WinStation (<c>"Console"</c>, <c>"RDP-Tcp#3"</c>, <c>"Services"</c>)
/// — the reliable way to tell the physical console from a remote session, since session ids carry no such
/// meaning beyond 0.
/// </summary>
internal readonly record struct SessionInfo(
    uint SessionId, string? UserName, SessionConnectState State, string? WinStationName = null)
{
    /// <summary>The WinStation name Windows gives the physical console session.</summary>
    internal const string ConsoleWinStationName = "Console";

    /// <summary>
    /// True for the physical console. With autologon configured this session exists from boot and never ends,
    /// which makes it the always-available fallback — and therefore the one to rank last among equals.
    /// </summary>
    internal bool IsConsole =>
        string.Equals(WinStationName?.Trim(), ConsoleWinStationName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this session still owns a desktop a process could be launched onto. The logged-on user — not
    /// the connect state — is the authoritative signal that a token exists, so the state test only excludes the
    /// definitively dead states (listeners, resetting, down). <see cref="SessionConnectState.Connected"/> is
    /// allowed: an autologon console session sits there while an RDP client is attached elsewhere.
    /// </summary>
    internal bool HasLiveDesktop =>
        !string.IsNullOrWhiteSpace(UserName)
        && State is SessionConnectState.Active
            or SessionConnectState.Connected
            or SessionConnectState.Disconnected;
}

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

    /// <summary>Placeholder used by <see cref="Describe"/> for an absent user or an empty session list.</summary>
    private const string NoneMarker = "<none>";

    /// <summary>Ordering weights for the console key — a remote session wins ties against the console.</summary>
    private const int RemoteRank = 0;
    private const int ConsoleRank = 1;

    /// <summary>
    /// Picks the session to launch the agent in. Three ordering keys, applied in this order:
    /// <list type="number">
    /// <item>connect state — <see cref="SessionConnectState.Active"/> first, or last under
    /// <paramref name="preferDisconnected"/>;</item>
    /// <item>console last — a remote session beats the physical console <em>at the same state rank</em>, which
    /// is what makes an autologon console desktop the fallback rather than the default. Placing this key
    /// <em>below</em> the state rank is deliberate: it must not let an operator's freshly-signed-in
    /// <c>Active</c> RDP session steal the agent while <paramref name="preferDisconnected"/> is set;</item>
    /// <item>session id — lowest, or highest (newest) under <paramref name="preferDisconnected"/>.</item>
    /// </list>
    /// The result is stable across restarts whenever the set of sessions is.
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
        sessionId = ServicesSessionId;

        // Two-phase on purpose: "nobody is signed in anywhere" and "sessions exist but not TargetUser's" call
        // for different operator actions, so the filters cannot collapse into one Where.
        var candidates = RankCandidates(sessions, preferDisconnected).ToList();
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

    /// <summary>
    /// The usable sessions in preference order — the single definition of the three ordering keys documented on
    /// <see cref="TrySelect"/>. <c>TargetUser</c> filtering is applied by the caller, which needs to tell
    /// "no usable session" apart from "no session for that user".
    /// </summary>
    private static IEnumerable<SessionInfo> RankCandidates(
        IReadOnlyList<SessionInfo> sessions, bool preferDisconnected) =>
        sessions
            .Where(IsUsableTarget)
            .OrderBy(s => StateRank(s.State, preferDisconnected))
            .ThenBy(s => s.IsConsole ? ConsoleRank : RemoteRank)
            .ThenBy(s => TieBreak(s.SessionId, preferDisconnected));

    /// <summary>The session <see cref="TrySelect"/> would choose right now, or null when none qualifies.</summary>
    private static uint? SelectBest(
        IReadOnlyList<SessionInfo> sessions, string? targetUser, bool preferDisconnected) =>
        TrySelect(sessions, targetUser, out var best, out _, preferDisconnected) ? best : null;

    /// <summary>
    /// Decides whether a <em>running</em> agent should be torn down and relaunched because its session is no
    /// longer the right one. Pure, so the policy is unit-testable; the worker performs the kill and lets the
    /// normal bring-up path re-run <see cref="TrySelect"/> (including the Session 0 headless fallback, which is
    /// why no replacement session id is returned here).
    /// </summary>
    /// <param name="currentSessionId">
    /// The session the agent is running in, or <c>null</c> when it was launched headless in Session 0.
    /// </param>
    /// <param name="reason">Operator-facing phrase for the log line; empty when no migration is due.</param>
    internal static bool ShouldMigrate(
        IReadOnlyList<SessionInfo> sessions,
        uint? currentSessionId,
        string? targetUser,
        bool preferDisconnected,
        SessionMigrationMode mode,
        out string reason)
    {
        reason = string.Empty;
        if (mode == SessionMigrationMode.Off)
        {
            return false;
        }

        var best = SelectBest(sessions, targetUser, preferDisconnected);
        var upgrades = mode == SessionMigrationMode.Always;

        if (currentSessionId is not { } current)
        {
            // Running headless. Only an "upgrade" can apply — there is no lost session to react to.
            if (!upgrades || best is not { } target)
            {
                return false;
            }

            reason = $"an interactive session is now available (session {target}) and the agent is running headless";
            return true;
        }

        // The session the agent lives in disappeared (signed out, reset, or the pinned user logged off). The
        // desktop is already gone, so relaunching costs nothing that was not lost — both non-Off modes act.
        if (!sessions.Any(s => s.SessionId == current && Qualifies(s, targetUser)))
        {
            reason = $"session {current} is no longer a usable interactive session";
            return true;
        }

        if (!upgrades || best is not { } preferred || preferred == current)
        {
            return false;
        }

        reason = $"session {preferred} now ranks above the agent's current session {current}";
        return true;
    }

    /// <summary>Primary ordering key — which connect state is favoured.</summary>
    private static int StateRank(SessionConnectState state, bool preferDisconnected)
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
    /// A session can host the agent when it is interactive (never Session 0) and still owns a live desktop —
    /// see <see cref="SessionInfo.HasLiveDesktop"/> for why the logged-on user rather than the connect state
    /// carries that verdict.
    /// </summary>
    private static bool IsUsableTarget(SessionInfo session) =>
        session.SessionId != ServicesSessionId && session.HasLiveDesktop;

    /// <summary>
    /// Full eligibility including the optional <c>TargetUser</c> pin — the same test the ranked candidate list
    /// plus the caller's user filter apply, expressed once so <see cref="ShouldMigrate"/> cannot drift from
    /// <see cref="TrySelect"/> about what "still a valid session" means.
    /// </summary>
    private static bool Qualifies(SessionInfo session, string? targetUser) =>
        IsUsableTarget(session)
        && (string.IsNullOrWhiteSpace(targetUser) || MatchesUser(session.UserName, targetUser));

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
        return interactive.Count == 0 ? NoneMarker : string.Join(", ", interactive.Select(Describe));
    }

    private static string Describe(SessionInfo session) =>
        $"{session.SessionId}:{(string.IsNullOrWhiteSpace(session.UserName) ? NoneMarker : session.UserName)}"
        + $"/{session.State}"
        + (string.IsNullOrWhiteSpace(session.WinStationName) ? "" : $"/{session.WinStationName}");
}
