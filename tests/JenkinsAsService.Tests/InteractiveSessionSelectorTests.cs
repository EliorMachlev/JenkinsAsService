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
    public void Prefer_non_active_keeps_the_lowest_session_id_tie_break()
    {
        var sessions = new[]
        {
            new SessionInfo(5, "b", SessionConnectState.Disconnected),
            new SessionInfo(3, "a", SessionConnectState.Connected),
        };

        InteractiveSessionSelector.TrySelect(
            sessions, targetUser: null, out var sessionId, out _, preferDisconnected: true).Should().BeTrue();

        sessionId.Should().Be(3);
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
