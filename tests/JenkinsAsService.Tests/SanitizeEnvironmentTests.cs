// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class SanitizeEnvironmentTests
{
    private static Dictionary<string, string?> SampleEnv() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = @"C:\Windows;C:\Windows\System32",
        ["SystemRoot"] = @"C:\Windows",
        ["JAVA_HOME"] = @"C:\jdk",
        ["AWS_SECRET_ACCESS_KEY"] = "super-secret",
        ["JENKINS_SECRET"] = "leak-me",
        ["GRADLE_USER_HOME"] = @"C:\gradle",
    };

    [Fact]
    public void Strips_variables_not_on_the_allow_list()
    {
        var env = SampleEnv();

        JenkinsAgentWorker.SanitizeEnvironment(env, extraAllowed: null);

        env.Should().NotContainKey("AWS_SECRET_ACCESS_KEY");
        env.Should().NotContainKey("JENKINS_SECRET");
    }

    [Fact]
    public void Keeps_curated_runtime_variables()
    {
        var env = SampleEnv();

        JenkinsAgentWorker.SanitizeEnvironment(env, extraAllowed: null);

        env.Should().ContainKey("PATH");
        env.Should().ContainKey("SystemRoot");
        env.Should().ContainKey("JAVA_HOME");
    }

    [Fact]
    public void Keeps_caller_supplied_extra_variables()
    {
        var env = SampleEnv();

        JenkinsAgentWorker.SanitizeEnvironment(env, extraAllowed: "GRADLE_USER_HOME;MAVEN_OPTS");

        env.Should().ContainKey("GRADLE_USER_HOME");
        env.Should().NotContainKey("JENKINS_SECRET");
    }

    [Theory]
    [InlineData("gradle_user_home")]
    [InlineData("GRADLE_USER_HOME")]
    [InlineData("  GRADLE_USER_HOME  ")]
    public void Extra_allow_list_is_case_insensitive_and_trimmed(string allowed)
    {
        var env = SampleEnv();

        JenkinsAgentWorker.SanitizeEnvironment(env, allowed);

        env.Should().ContainKey("GRADLE_USER_HOME");
    }

    [Fact]
    public void Empty_environment_stays_empty()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        JenkinsAgentWorker.SanitizeEnvironment(env, extraAllowed: null);

        env.Should().BeEmpty();
    }
}
