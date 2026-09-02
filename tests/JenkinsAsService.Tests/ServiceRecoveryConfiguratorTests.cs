// Copyright (c) 2024 All rights reserved

using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// Covers the SCM interop as far as a test can without installing a service: the SCM connection, the
/// SafeHandle, and the error path. The successful ChangeServiceConfig2 call needs a real installed service
/// and is asserted for real by <c>Test-MsiLifecycle.ps1</c>, which reads the actions back with
/// <c>sc.exe qfailure</c> after a genuine install.
/// </summary>
public class ServiceRecoveryConfiguratorTests
{
    [Fact]
    public void Configure_reports_failure_for_a_service_that_does_not_exist()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // SCM is a Windows-only concept
        }

        var messages = new List<string>();

        // Reaches OpenSCManager and OpenService for real — a marshalling or SafeHandle mistake surfaces
        // here as an exception rather than the clean false this asserts.
        ServiceRecoveryConfigurator.Configure("JAS_NoSuchService_" + Guid.NewGuid().ToString("N"), messages.Add)
            .Should().BeFalse();

        messages.Should().ContainSingle()
            .Which.Should().StartWith("Error:").And.Contain("OpenService");
    }

    /// <summary>
    /// The values are the contract with the operator: three restarts 10s apart, count reset after a day.
    /// They previously lived in util:ServiceConfig authoring; pinning them keeps the move from silently
    /// changing recovery behaviour.
    /// </summary>
    [Fact]
    public void Recovery_settings_match_the_documented_policy()
    {
        ServiceRecoveryConfigurator.RestartAttempts.Should().Be(3);
        ServiceRecoveryConfigurator.RestartDelayMs.Should().Be(10_000);
        ServiceRecoveryConfigurator.ResetPeriodSeconds.Should().Be(86_400);
    }
}
