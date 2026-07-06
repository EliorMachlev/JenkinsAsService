// Copyright (c) 2024 All rights reserved

using FluentAssertions;

namespace JenkinsAsService.Tests;

public class WatchdogTimingTests
{
    [Theory]
    [InlineData(1, 10)]    // base
    [InlineData(2, 20)]    // base * 2
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    [InlineData(5, 160)]
    [InlineData(6, 300)]   // 320 clamped to the 300s cap
    [InlineData(10, 300)]  // stays clamped
    public void ComputeBackoffDelaySeconds_follows_exponential_curve_and_caps(int attempt, int expected)
    {
        JenkinsAgentWorker.ComputeBackoffDelaySeconds(attempt, baseSec: 10, maxSec: 300, multiplier: 2)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 5, false)]   // MaxRetries=0 means unlimited — never trips
    [InlineData(0, 999, false)]
    [InlineData(3, 3, false)]   // at the limit — not yet exceeded
    [InlineData(3, 4, true)]    // one past the limit
    [InlineData(1, 2, true)]
    public void HasExceededMaxRetries_only_trips_past_a_positive_limit(int maxRetries, int crashCount, bool expected)
    {
        JenkinsAgentWorker.HasExceededMaxRetries(crashCount, maxRetries).Should().Be(expected);
    }
}
