// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// The tracker exists to stop one specific production failure: with <c>SessionMigration: Always</c> and an
/// interactive launch that keeps failing (wrong service identity, ERROR_ACCESS_DENIED), the agent lands headless,
/// the selector correctly says "a desktop is available, upgrade", the relaunch lands headless again — and the
/// node drops every stability window forever. Migrations bypass the crash counter, so nothing else stops it.
/// </summary>
public class SessionMigrationTrackerTests
{
    private const string OneSession = "2:build/Disconnected/RDP-Tcp#2";
    private const string TwoSessions = "1:build/Connected/Console, 2:build/Disconnected/RDP-Tcp#2";

    [Fact]
    public void The_first_migration_from_a_given_state_proceeds()
    {
        new SessionMigrationTracker().Evaluate(null, OneSession)
            .Should().Be(MigrationVerdict.Proceed);
    }

    [Fact]
    public void A_relaunch_that_changed_nothing_does_not_migrate_again()
    {
        var tracker = new SessionMigrationTracker();

        // Headless agent, one desktop available: the selector approves an upgrade and the agent is relaunched —
        // straight back into Session 0, because the launch itself is broken.
        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.Proceed);
        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.SuppressAndReport);
    }

    [Fact]
    public void The_stall_is_reported_once_and_then_stays_quiet()
    {
        var tracker = new SessionMigrationTracker();
        tracker.Evaluate(null, OneSession);

        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.SuppressAndReport);
        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.Suppress);
        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.Suppress);
    }

    [Fact]
    public void A_migration_that_actually_moved_the_agent_is_not_a_repeat()
    {
        var tracker = new SessionMigrationTracker();
        tracker.Evaluate(null, TwoSessions).Should().Be(MigrationVerdict.Proceed);

        // The relaunch landed on session 2. Later a higher-ranked session appears and the selector approves
        // another move — a different origin, so it must not be mistaken for the stalled case.
        tracker.Evaluate(2, TwoSessions).Should().Be(MigrationVerdict.Proceed);
    }

    [Fact]
    public void A_change_in_the_session_table_lifts_the_suppression()
    {
        var tracker = new SessionMigrationTracker();
        tracker.Evaluate(null, OneSession);
        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.SuppressAndReport);

        // Somebody signs in: the world changed, so the move is worth attempting again even though the agent
        // is still headless. This is what keeps the guard from disabling migration permanently.
        tracker.Evaluate(null, TwoSessions).Should().Be(MigrationVerdict.Proceed);
    }

    [Fact]
    public void Returning_to_a_previously_stalled_state_is_reported_again()
    {
        var tracker = new SessionMigrationTracker();
        tracker.Evaluate(null, OneSession);
        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.SuppressAndReport);
        tracker.Evaluate(null, TwoSessions).Should().Be(MigrationVerdict.Proceed);

        // Back to the old topology: only the most recent origin is remembered, so this is a fresh attempt
        // rather than a state the tracker still considers stalled.
        tracker.Evaluate(null, OneSession).Should().Be(MigrationVerdict.Proceed);
    }
}
