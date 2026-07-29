// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// The launcher is almost entirely interop and cannot be exercised in CI (no console session, no
/// SeTcbPrivilege) — the same class as the TPM code. What <em>is</em> testable is the privilege metadata:
/// <c>LookupPrivilegeValue</c> resolves by name, so a typo in a privilege constant fails silently at runtime
/// and surfaces only as an unexplained fallback on a customer box.
/// </summary>
public class InteractiveSessionLauncherTests
{
    [Fact]
    public void Required_privileges_are_the_three_the_interop_path_needs()
    {
        // SeTcbPrivilege → WTSQueryUserToken; the other two → CreateProcessAsUser with another user's token.
        InteractiveSessionLauncher.RequiredPrivileges.Should().Equal(
            "SeTcbPrivilege",
            "SeAssignPrimaryTokenPrivilege",
            "SeIncreaseQuotaPrivilege");
    }

    [Theory]
    [InlineData("SeTcbPrivilege", "Act as part of the operating system")]
    [InlineData("SeAssignPrimaryTokenPrivilege", "Replace a process level token")]
    [InlineData("SeIncreaseQuotaPrivilege", "Adjust memory quotas for a process")]
    public void Friendly_name_matches_the_user_rights_assignment_label(string privilege, string expected)
    {
        // The warning tells the operator what to grant; these strings must match the secpol.msc labels
        // verbatim or the instruction is unfollowable.
        InteractiveSessionLauncher.FriendlyName(privilege).Should().Be(expected);
    }

    [Fact]
    public void Friendly_name_falls_back_to_the_raw_constant_for_an_unmapped_privilege()
    {
        InteractiveSessionLauncher.FriendlyName("SeDebugPrivilege").Should().Be("SeDebugPrivilege");
    }

    [Fact]
    public void Every_required_privilege_has_a_friendly_name()
    {
        foreach (var privilege in InteractiveSessionLauncher.RequiredPrivileges)
        {
            InteractiveSessionLauncher.FriendlyName(privilege).Should().NotBe(
                privilege, $"the warning for {privilege} must name the User Rights Assignment entry");
        }
    }
}
