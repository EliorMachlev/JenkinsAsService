// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// Session-selection policy for the interactive launch. The interop around it cannot run in CI, so this is the
/// layer that has to carry the correctness — every case below was observed on, or is reachable from, the real
/// build node whose console session sits empty at the logon screen while the operator works over RDP.
/// </summary>
public class InteractiveSessionSelectorTests
{
    private static SessionInfo Services() => new(0, null, SessionConnectState.Disconnected);

    private static SessionInfo EmptyConsole() => new(1, null, SessionConnectState.Connected);

    [Fact]
    public void Picks_the_rdp_session_when_the_console_sits_empty_at_the_logon_screen()
    {
        // The exact topology from the failing box: WTSGetActiveConsoleSessionId returned 1, which has no user,
        // and querying it yielded ERROR_NO_TOKEN. The operator's desktop is session 2.
        var sessions = new[] { Services(), EmptyConsole(), new SessionInfo(2, "master", SessionConnectState.Active) };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out var failure)
            .Should().BeTrue();

        sessionId.Should().Be(2);
        failure.Should().Be(SessionSelectionFailure.None);
    }

    [Fact]
    public void Targets_a_disconnected_session_because_closing_an_rdp_window_keeps_the_desktop_alive()
    {
        // The agent must still land on the operator's desktop when nobody is currently attached — a
        // disconnected session keeps its user, its desktop and any running browser.
        var sessions = new[] { Services(), EmptyConsole(), new SessionInfo(2, "master", SessionConnectState.Disconnected) };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(2);
    }

    [Fact]
    public void Prefers_the_active_session_over_a_disconnected_one()
    {
        var sessions = new[]
        {
            new SessionInfo(3, "other", SessionConnectState.Disconnected),
            new SessionInfo(2, "master", SessionConnectState.Active),
        };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(2);
    }

    [Fact]
    public void Falls_back_to_the_lowest_session_id_when_several_are_equally_ranked()
    {
        // Stability matters more than which one: the same node must target the same desktop across restarts.
        var sessions = new[]
        {
            new SessionInfo(5, "b", SessionConnectState.Disconnected),
            new SessionInfo(2, "a", SessionConnectState.Disconnected),
        };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(2);
    }

    [Fact]
    public void Accepts_an_autologon_console_session_left_in_the_connected_state()
    {
        // An autologon console session sits in Connected (not Active) while an RDP client is attached. The
        // logged-on user, not the connect state, is what proves a token exists.
        var sessions = new[] { Services(), new SessionInfo(1, "buildbot", SessionConnectState.Connected) };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(1);
    }

    [Fact]
    public void Never_targets_session_zero_even_though_it_is_enumerated()
    {
        // Session 0 is the services session — launching there is the headless case we are trying to escape.
        var sessions = new[] { new SessionInfo(0, "SYSTEM", SessionConnectState.Active) };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out _, out var failure)
            .Should().BeFalse();

        failure.Should().Be(SessionSelectionFailure.NoInteractiveSession);
    }

    // InlineData carries the raw WTS_CONNECTSTATE_CLASS value: SessionConnectState is internal, and an
    // internal parameter type on a public xUnit theory is a compile error (CS0051).
    [Theory]
    [InlineData(6)] // Listen
    [InlineData(8)] // Down
    [InlineData(7)] // Reset
    [InlineData(9)] // Init
    [InlineData(5)] // Idle
    public void Rejects_sessions_in_states_that_no_longer_own_a_desktop(int rawState)
    {
        var sessions = new[] { new SessionInfo(2, "master", (SessionConnectState)rawState) };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out _, out var failure)
            .Should().BeFalse();

        failure.Should().Be(SessionSelectionFailure.NoInteractiveSession);
    }

    [Fact]
    public void Reports_no_interactive_session_after_a_reboot_with_nobody_logged_in()
    {
        // The post-restart case: only the services session and an empty console exist. Selection must fail
        // clearly rather than hand back a session whose token query will die with ERROR_NO_TOKEN.
        var sessions = new[] { Services(), EmptyConsole() };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out _, out var failure)
            .Should().BeFalse();

        failure.Should().Be(SessionSelectionFailure.NoInteractiveSession);
    }

    [Fact]
    public void Pins_to_the_configured_target_user_when_several_people_are_logged_in()
    {
        var sessions = new[]
        {
            new SessionInfo(2, "someoneelse", SessionConnectState.Active),
            new SessionInfo(3, "master", SessionConnectState.Disconnected),
        };

        InteractiveSessionSelector.TrySelect(sessions, "master", out var sessionId, out _)
            .Should().BeTrue();

        // Without the pin, the Active session 2 would win — the pin must beat the ranking, not tie-break it.
        sessionId.Should().Be(3);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("MASTER")]
    [InlineData("BUILDSRV\\master")]
    [InlineData("  master  ")]
    public void Matches_the_target_user_case_insensitively_and_ignores_a_domain_prefix(string configured)
    {
        // WTSQuerySessionInformation returns the bare user name, but operators write what they see in qwinsta
        // or in DOMAIN\user form; both must work or the pin silently disables the feature.
        var sessions = new[] { new SessionInfo(2, "master", SessionConnectState.Active) };

        InteractiveSessionSelector.TrySelect(sessions, configured, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(2);
    }

    [Fact]
    public void Distinguishes_target_user_absent_from_nobody_logged_in_at_all()
    {
        // Different operator action: "log in as that user" vs "log in at all". The warning depends on it.
        var sessions = new[] { new SessionInfo(2, "someoneelse", SessionConnectState.Active) };

        InteractiveSessionSelector.TrySelect(sessions, "master", out _, out var failure)
            .Should().BeFalse();

        failure.Should().Be(SessionSelectionFailure.TargetUserNotLoggedOn);
    }

    [Fact]
    public void An_empty_target_user_means_any_logged_on_user_qualifies()
    {
        var sessions = new[] { new SessionInfo(2, "master", SessionConnectState.Active) };

        InteractiveSessionSelector.TrySelect(sessions, "   ", out var sessionId, out _).Should().BeTrue();

        sessionId.Should().Be(2);
    }

    [Fact]
    public void Prefers_a_non_active_session_when_configured_to_avoid_an_operators_desktop()
    {
        // The unattended build desktop sits disconnected; the Active session is the operator who just signed
        // in to watch. Preferring non-Active keeps the agent off their desktop without needing a TargetUser.
        var sessions = new[]
        {
            new SessionInfo(1, "buildacct", SessionConnectState.Disconnected),
            new SessionInfo(2, "operator", SessionConnectState.Active),
        };

        InteractiveSessionSelector.TrySelect(
            sessions, targetUser: null, out var sessionId, out _, preferDisconnected: true).Should().BeTrue();

        sessionId.Should().Be(1);
    }

    [Fact]
    public void Prefer_non_active_still_takes_an_active_session_when_it_is_the_only_one()
    {
        // It is a preference, not a filter — a lone Active session must not be rejected.
        var sessions = new[] { new SessionInfo(2, "master", SessionConnectState.Active) };

        InteractiveSessionSelector.TrySelect(
            sessions, targetUser: null, out var sessionId, out _, preferDisconnected: true).Should().BeTrue();

        sessionId.Should().Be(2);
    }

    [Fact]
    public void Target_user_still_wins_over_the_non_active_preference()
    {
        // An explicit pin is a stronger statement than a ranking heuristic.
        var sessions = new[]
        {
            new SessionInfo(1, "someoneelse", SessionConnectState.Disconnected),
            new SessionInfo(2, "master", SessionConnectState.Active),
        };

        InteractiveSessionSelector.TrySelect(
            sessions, "master", out var sessionId, out _, preferDisconnected: true).Should().BeTrue();

        sessionId.Should().Be(2);
    }

    [Fact]
    public void Prefer_non_active_tie_breaks_on_the_highest_session_id_as_the_newest_session()
    {
        // Session ids increase as sessions are created, so the highest is the newest. Stale disconnected
        // sessions pile up over a machine's uptime; the newest one is the live desktop.
        var sessions = new[]
        {
            new SessionInfo(5, "b", SessionConnectState.Disconnected),
            new SessionInfo(3, "a", SessionConnectState.Connected),
        };

        InteractiveSessionSelector.TrySelect(
            sessions, targetUser: null, out var sessionId, out _, preferDisconnected: true).Should().BeTrue();

        sessionId.Should().Be(5);
    }

    [Fact]
    public void Default_direction_still_tie_breaks_on_the_lowest_session_id()
    {
        // Unchanged for the default ranking — a console-session setup stays pinned to session 1.
        var sessions = new[]
        {
            new SessionInfo(5, "b", SessionConnectState.Active),
            new SessionInfo(3, "a", SessionConnectState.Active),
        };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(3);
    }

    [Fact]
    public void Prefer_non_active_picks_the_newest_of_several_disconnected_sessions()
    {
        var sessions = new[]
        {
            new SessionInfo(2, "master", SessionConnectState.Disconnected),
            new SessionInfo(7, "master", SessionConnectState.Disconnected),
            new SessionInfo(4, "master", SessionConnectState.Disconnected),
        };

        InteractiveSessionSelector.TrySelect(
            sessions, targetUser: null, out var sessionId, out _, preferDisconnected: true).Should().BeTrue();

        sessionId.Should().Be(7);
    }

    // ── Console-vs-remote ranking: the autologon desktop is the fallback, not the default ────────────────

    private static SessionInfo Console(uint id, string? user, SessionConnectState state) =>
        new(id, user, state, "Console");

    private static SessionInfo Rdp(uint id, string? user, SessionConnectState state) =>
        new(id, user, state, $"RDP-Tcp#{id}");

    [Fact]
    public void An_rdp_session_outranks_the_autologon_console_at_the_same_connect_state()
    {
        // The point of the console/remote key: with autologon the console session exists from boot and would
        // otherwise win on the lowest-id tie-break forever, so a real RDP desktop could never take over.
        var sessions = new[]
        {
            Console(1, "buildacct", SessionConnectState.Disconnected),
            Rdp(3, "master", SessionConnectState.Disconnected),
        };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(3);
    }

    [Fact]
    public void The_console_key_does_not_let_an_operators_active_session_steal_the_agent()
    {
        // Ordering matters: console-vs-remote sits BELOW the connect-state rank. With PreferDisconnectedSession
        // the non-Active console must still win over an Active RDP session, or signing in to watch a run would
        // yank the agent onto the operator's desktop — the exact thing that setting exists to prevent.
        var sessions = new[]
        {
            Console(1, "buildacct", SessionConnectState.Disconnected),
            Rdp(3, "operator", SessionConnectState.Active),
        };

        InteractiveSessionSelector.TrySelect(
            sessions, targetUser: null, out var sessionId, out _, preferDisconnected: true).Should().BeTrue();

        sessionId.Should().Be(1);
    }

    [Fact]
    public void The_autologon_console_is_the_last_resort_when_every_rdp_session_has_gone()
    {
        var sessions = new[] { Services(), Console(1, "buildacct", SessionConnectState.Disconnected) };

        InteractiveSessionSelector.TrySelect(sessions, targetUser: null, out var sessionId, out _)
            .Should().BeTrue();

        sessionId.Should().Be(1);
    }

    // ── Migration policy ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Migration_is_off_by_default_even_when_the_agents_session_has_vanished()
    {
        var sessions = new[] { Console(1, "buildacct", SessionConnectState.Disconnected) };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 3, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.Off, out _).Should().BeFalse();
    }

    [Fact]
    public void Migrates_off_a_session_that_no_longer_exists()
    {
        // The RDP session the agent was launched into ended; its desktop is gone, so relaunching costs nothing
        // that was not already lost. Both non-Off modes must act on this.
        var sessions = new[] { Console(1, "buildacct", SessionConnectState.Disconnected) };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 3, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.OnSessionLost, out var reason).Should().BeTrue();

        reason.Should().Contain("3");
    }

    [Fact]
    public void Migrates_off_a_session_whose_user_signed_out_but_whose_id_lingers()
    {
        // Session still enumerated, but with no logged-on user — the token query would now fail with
        // ERROR_NO_TOKEN, so this is as dead as a removed session.
        var sessions = new[] { Rdp(3, null, SessionConnectState.Connected) };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 3, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.OnSessionLost, out _).Should().BeTrue();
    }

    [Fact]
    public void OnSessionLost_leaves_a_healthy_agent_alone_even_when_a_better_session_appears()
    {
        // The conservative mode: an upgrade is not worth killing a running build for.
        var sessions = new[]
        {
            Console(1, "buildacct", SessionConnectState.Disconnected),
            Rdp(3, "master", SessionConnectState.Disconnected),
        };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 1, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.OnSessionLost, out _).Should().BeFalse();
    }

    [Fact]
    public void Always_moves_the_agent_off_the_autologon_console_once_an_rdp_session_starts()
    {
        var sessions = new[]
        {
            Console(1, "buildacct", SessionConnectState.Disconnected),
            Rdp(3, "master", SessionConnectState.Disconnected),
        };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 1, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.Always, out var reason).Should().BeTrue();

        reason.Should().Contain("3");
    }

    [Fact]
    public void Always_leaves_the_agent_alone_when_it_is_already_on_the_best_session()
    {
        // The steady state — this check runs every stability window, so a false positive here would restart
        // the agent in a loop forever.
        var sessions = new[]
        {
            Console(1, "buildacct", SessionConnectState.Disconnected),
            Rdp(3, "master", SessionConnectState.Disconnected),
        };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 3, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.Always, out _).Should().BeFalse();
    }

    [Fact]
    public void Always_upgrades_a_headless_agent_once_a_desktop_exists()
    {
        // The post-reboot recovery: nobody was signed in, the agent fell back to Session 0, and now somebody
        // has logged on. currentSessionId is null for a headless launch.
        var sessions = new[] { Rdp(3, "master", SessionConnectState.Active) };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: null, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.Always, out var reason).Should().BeTrue();

        reason.Should().Contain("headless");
    }

    [Fact]
    public void OnSessionLost_never_upgrades_a_headless_agent()
    {
        // A headless agent has no session to lose, so the conservative mode has nothing to react to.
        var sessions = new[] { Rdp(3, "master", SessionConnectState.Active) };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: null, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.OnSessionLost, out _).Should().BeFalse();
    }

    [Fact]
    public void A_headless_agent_stays_headless_while_no_session_qualifies()
    {
        var sessions = new[] { Services(), EmptyConsole() };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: null, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.Always, out _).Should().BeFalse();
    }

    [Fact]
    public void Migrates_when_the_pinned_target_user_moves_to_a_different_session()
    {
        // TargetUser signed out of session 3 and back in on session 4: session 3 still exists with another
        // user, so only the pin makes it unusable.
        var sessions = new[]
        {
            Rdp(3, "someoneelse", SessionConnectState.Disconnected),
            Rdp(4, "master", SessionConnectState.Active),
        };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 3, targetUser: "master", preferDisconnected: false,
            SessionMigrationMode.OnSessionLost, out _).Should().BeTrue();
    }

    [Fact]
    public void Falling_back_to_session_zero_is_left_to_the_relaunch_when_no_session_survives()
    {
        // Everyone signed out. Migration still fires — the agent is sitting on a dead desktop — and the caller's
        // bring-up path is what decides between a new session and the headless Session 0 fallback.
        var sessions = new[] { Services(), EmptyConsole() };

        InteractiveSessionSelector.ShouldMigrate(
            sessions, currentSessionId: 3, targetUser: null, preferDisconnected: false,
            SessionMigrationMode.OnSessionLost, out _).Should().BeTrue();
    }

    [Fact]
    public void Describe_lists_interactive_sessions_and_hides_the_services_session()
    {
        var sessions = new[] { Services(), EmptyConsole(), new SessionInfo(2, "master", SessionConnectState.Active) };

        var description = InteractiveSessionSelector.Describe(sessions);

        description.Should().Be("1:<none>/Connected, 2:master/Active");
    }

    [Fact]
    public void Describe_reports_none_when_only_the_services_session_exists()
    {
        InteractiveSessionSelector.Describe([Services()]).Should().Be("<none>");
    }
}
