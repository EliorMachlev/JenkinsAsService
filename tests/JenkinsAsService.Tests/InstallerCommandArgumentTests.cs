// Copyright (c) 2024 All rights reserved

using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// Argument parsing for the two verbs the MSI invokes as EXE custom actions in place of the WiX Util
/// extension (see the installer .wixproj). Both are formatted from MSI properties, so the awkward inputs
/// are the ones a directory property or an empty property produces.
/// </summary>
public class InstallerCommandArgumentTests
{
    [Fact]
    public void GrantDataAccess_parses_path_and_account()
    {
        var (path, account, error) = GrantDataAccessCommand.ParseArguments(
            ["grant-data-access", "--path", @"D:\Jenkins Data", "--account", @"NT SERVICE\Jenkins"]);

        error.Should().BeNull();
        path.Should().Be(@"D:\Jenkins Data");
        account.Should().Be(@"NT SERVICE\Jenkins");
    }

    /// <summary>
    /// [DATAFOLDER] resolves with a trailing backslash, and the authored <c>"[DATAFOLDER]\"</c> collapses to
    /// a path with a stray trailing quote once CommandLineToArgvW has had it. Same trap SetDataDir carries.
    /// </summary>
    [Fact]
    public void GrantDataAccess_recovers_a_directory_property_with_its_trailing_quote()
    {
        var (path, _, error) = GrantDataAccessCommand.ParseArguments(
            ["--path", @"D:\Jenkins\""", "--account", "svc"]);

        error.Should().BeNull();
        path.Should().Be(@"D:\Jenkins");
    }

    [Fact]
    public void GrantDataAccess_defers_the_path_when_it_is_not_given()
    {
        // Null means "use the configured data directory", which is how a relocated Agent:DataDirectory
        // stays honoured. An account, by contrast, has no safe default.
        var (path, account, error) = GrantDataAccessCommand.ParseArguments(["--account", "svc"]);

        error.Should().BeNull();
        path.Should().BeNull();
        account.Should().Be("svc");
    }

    public static TheoryData<string[]> AccountlessArguments => new()
    {
        new[] { "grant-data-access" },
        new[] { "--path", @"D:\Jenkins" },
        new[] { "--account", "" },
        new[] { "--account", "   " },
    };

    [Theory]
    [MemberData(nameof(AccountlessArguments))]
    public void GrantDataAccess_requires_an_account(string[] args)
    {
        // Falling back to a built-in name would silently ACL the wrong identity on any install that
        // overrode SERVICE_ACCOUNT — the exact failure the removed SERVICE_ACCOUNT_NAME split invited.
        GrantDataAccessCommand.ParseArguments(args).Error.Should().NotBeNull();
    }

    [Fact]
    public void GrantDataAccess_rejects_a_flag_with_no_value()
    {
        GrantDataAccessCommand.ParseArguments(["--account", "svc", "--path"])
            .Error.Should().Contain("--path");
    }

    [Fact]
    public void GrantDataAccess_rejects_an_unknown_argument()
    {
        GrantDataAccessCommand.ParseArguments(["--account", "svc", "--wat"])
            .Error.Should().Contain("--wat");
    }

    [Fact]
    public void ConfigureRecovery_defaults_to_the_installed_service_name()
    {
        var (serviceName, error) = ConfigureRecoveryCommand.ParseArguments(["configure-recovery"]);

        error.Should().BeNull();
        serviceName.Should().Be(ConfigureRecoveryCommand.DefaultServiceName);
    }

    [Fact]
    public void ConfigureRecovery_accepts_an_explicit_service()
    {
        ConfigureRecoveryCommand.ParseArguments(["--service", "Jenkins2"])
            .ServiceName.Should().Be("Jenkins2");
    }

    [Fact]
    public void ConfigureRecovery_rejects_an_unknown_argument()
    {
        ConfigureRecoveryCommand.ParseArguments(["--nope"]).Error.Should().NotBeNull();
    }

    /// <summary>
    /// The MSI authors these commands as literal strings in CustomAction.Target, whose ICE03 limit is 255
    /// characters. Asserted here because exceeding it is a build-time failure that only the MSI build sees.
    /// </summary>
    [Theory]
    [InlineData(@"grant-data-access --path ""[DATAFOLDER]\"" --account ""[SERVICE_ACCOUNT]""")]
    [InlineData("configure-recovery")]
    public void Authored_custom_action_commands_fit_the_MSI_target_limit(string exeCommand)
    {
        exeCommand.Length.Should().BeLessThan(255);
    }
}
