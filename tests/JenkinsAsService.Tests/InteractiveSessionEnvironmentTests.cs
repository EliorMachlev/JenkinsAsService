// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// An interactive launch runs the agent as the <em>session</em> user via <c>CreateProcessAsUser</c>, but that API
/// does not build an environment for the token — pass NULL and the child inherits the <em>service's</em> block.
/// So the child's environment is built from <c>CreateEnvironmentBlock(userToken)</c> instead, and the sanitizer
/// filters that by name exactly as it filtered the service's. The interop is untestable in CI; this rule is not.
/// </summary>
public class InteractiveSessionEnvironmentTests
{
    /// <summary>Stands in for what <c>CreateEnvironmentBlock</c> returns: machine variables merged with the
    /// target user's, every profile path already resolved by the OS.</summary>
    private static Dictionary<string, string> UserBlock(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERNAME"] = "master",
            ["USERPROFILE"] = @"C:\Users\master",
            ["LOCALAPPDATA"] = @"C:\Users\master\AppData\Local",
            ["APPDATA"] = @"C:\Users\master\AppData\Roaming",
            ["TEMP"] = @"C:\Users\master\AppData\Local\Temp",
            ["SystemRoot"] = @"C:\Windows",
            ["PATH"] = @"C:\Windows\system32;C:\Users\master\bin",
        };

        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }

        return dict;
    }

    private static HardeningSettings Hardening(bool sanitize = true, string allowed = "") =>
        new() { SanitizeEnvironment = sanitize, AllowedEnvironmentVariables = allowed };

    [Fact]
    public void The_child_gets_the_session_users_values_not_the_services()
    {
        var env = InteractiveSessionLauncher.BuildChildEnvironment(UserBlock(), Hardening());

        // The failure this exists to prevent: LocalSystem's C:\Windows\system32\config\systemprofile.
        env["USERPROFILE"].Should().Be(@"C:\Users\master");
        env["LOCALAPPDATA"].Should().Be(@"C:\Users\master\AppData\Local");
        env["TEMP"].Should().Be(@"C:\Users\master\AppData\Local\Temp");
        env["USERNAME"].Should().Be("master");
    }

    [Fact]
    public void Machine_variables_merged_into_the_user_block_come_through_too()
    {
        // CreateEnvironmentBlock returns system + user, so a machine-wide JAVA_HOME is not lost by sourcing
        // the environment from the user instead of the service.
        var env = InteractiveSessionLauncher.BuildChildEnvironment(
            UserBlock(("JAVA_HOME", @"C:\jdk-21")), Hardening());

        env["JAVA_HOME"].Should().Be(@"C:\jdk-21");
        env["SystemRoot"].Should().Be(@"C:\Windows");
    }

    [Fact]
    public void The_allow_list_filters_the_users_block_the_same_way_it_filtered_the_services()
    {
        // Sanitizing is by name, so it does not care which side a variable came from. A secret sitting in the
        // target user's own environment must be stripped just like one in the service's.
        var env = InteractiveSessionLauncher.BuildChildEnvironment(
            UserBlock(("AWS_SECRET_ACCESS_KEY", "leak-me"), ("OneDrive", @"C:\Users\master\OneDrive")),
            Hardening());

        env.Should().NotContainKey("AWS_SECRET_ACCESS_KEY");
        env.Should().NotContainKey("OneDrive");
        env.Should().ContainKey("USERPROFILE", "allow-listed variables survive");
    }

    [Fact]
    public void AllowedEnvironmentVariables_still_extends_the_list()
    {
        var env = InteractiveSessionLauncher.BuildChildEnvironment(
            UserBlock(("GRADLE_USER_HOME", @"C:\gradle"), ("MAVEN_OPTS", "-Xmx1g")),
            Hardening(allowed: "GRADLE_USER_HOME;MAVEN_OPTS"));

        env["GRADLE_USER_HOME"].Should().Be(@"C:\gradle");
        env["MAVEN_OPTS"].Should().Be("-Xmx1g");
    }

    [Fact]
    public void Sanitizing_off_passes_the_users_whole_block()
    {
        var env = InteractiveSessionLauncher.BuildChildEnvironment(
            UserBlock(("OneDrive", @"C:\Users\master\OneDrive")), Hardening(sanitize: false));

        env.Should().ContainKey("OneDrive");
        env["USERPROFILE"].Should().Be(@"C:\Users\master");
    }

    [Fact]
    public void Lookups_are_case_insensitive_like_a_real_environment_block()
    {
        var env = InteractiveSessionLauncher.BuildChildEnvironment(UserBlock(), Hardening());

        env.Should().ContainKey("userprofile");
        env.TryGetValue("TeMp", out var temp).Should().BeTrue();
        temp.Should().Be(@"C:\Users\master\AppData\Local\Temp");
    }
}
