// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ConnectionMethodTests
{
    [Theory]
    [InlineData(ConnectionMethod.Auto, ConnectionMethod.WebSocket)]      // Auto starts on WebSocket
    [InlineData(ConnectionMethod.WebSocket, ConnectionMethod.WebSocket)]
    [InlineData(ConnectionMethod.Https, ConnectionMethod.Https)]
    public void InitialEffectiveMethod_picks_the_starting_transport(ConnectionMethod configured, ConnectionMethod expected)
    {
        JenkinsAgentWorker.InitialEffectiveMethod(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData(ConnectionMethod.WebSocket, ConnectionMethod.Https)]
    [InlineData(ConnectionMethod.Https, ConnectionMethod.WebSocket)]
    public void ToggleMethod_flips_between_the_two_transports(ConnectionMethod current, ConnectionMethod expected)
    {
        JenkinsAgentWorker.ToggleMethod(current).Should().Be(expected);
    }

    [Fact]
    public void Toggling_twice_returns_to_the_original()
    {
        var once = JenkinsAgentWorker.ToggleMethod(ConnectionMethod.WebSocket);
        JenkinsAgentWorker.ToggleMethod(once).Should().Be(ConnectionMethod.WebSocket);
    }

    [Fact]
    public void Method_defaults_to_Auto()
    {
        new ServiceSettings().Connection.Method.Should().Be(ConnectionMethod.Auto);
    }

    [Theory]
    // Auto + a real fast exit (didn't stabilise) → fall back.
    [InlineData(ConnectionMethod.Auto, false, true, true)]
    // Auto but the run reached stability → keep the transport.
    [InlineData(ConnectionMethod.Auto, true, true, false)]
    // Auto but no real exit (recovery skipped — controller unreachable) → no toggle. Regression guard.
    [InlineData(ConnectionMethod.Auto, false, false, false)]
    // Explicit transports never fall back.
    [InlineData(ConnectionMethod.WebSocket, false, true, false)]
    [InlineData(ConnectionMethod.Https, false, true, false)]
    public void ShouldFallbackTransport_only_on_real_unstable_auto_exit(
        ConnectionMethod configured, bool reachedStability, bool agentActuallyExited, bool expected)
    {
        JenkinsAgentWorker.ShouldFallbackTransport(configured, reachedStability, agentActuallyExited)
            .Should().Be(expected);
    }
}
