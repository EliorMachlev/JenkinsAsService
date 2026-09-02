// Copyright (c) 2024 All rights reserved

using System.Reflection;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class CliCommandsTests
{
    /// <summary>
    /// The guard this class exists for. A command type that is never registered still compiles, and its
    /// only symptom is the exe starting in SERVICE mode instead — which for an MSI custom action is a
    /// failed install, found at install time rather than build time. <c>configure-recovery</c> shipped
    /// exactly that way for one commit.
    /// </summary>
    [Fact]
    public void Every_command_type_is_dispatchable()
    {
        var declared = typeof(CliCommands).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsAbstract && t.IsSealed && t.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(t => new
            {
                t.Name,
                Verb = (string?)t.GetField("Name", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.GetRawConstantValue(),
            })
            .Where(c => c.Verb is not null)
            .ToList();

        declared.Should().HaveCountGreaterThan(1, "the reflection filter must actually be finding command types");

        foreach (var c in declared)
        {
            CliCommands.All.Should().ContainKey(c.Verb!,
                $"{c.Name} declares the verb '{c.Verb}' but nothing dispatches it");
        }
    }

    [Fact]
    public void The_msi_invoked_verbs_are_registered()
    {
        // Named literally rather than through the constants: these four strings are also authored in
        // Package.wxs, where a rename on this side alone would go unnoticed until an install failed.
        CliCommands.All.Keys.Should().BeEquivalentTo(
            ["update-secret", "purge", "grant-data-access", "configure-recovery"]);
    }

    [Fact]
    public void No_arguments_falls_through_to_service_mode()
    {
        CliCommands.TryResolve([], out _).Should().BeFalse();
    }

    [Fact]
    public void An_unknown_first_argument_falls_through_to_service_mode()
    {
        CliCommands.TryResolve(["--not-a-verb"], out _).Should().BeFalse();
    }

    [Fact]
    public void Verb_matching_is_case_sensitive()
    {
        // Ordinal by design: these are argv tokens from a custom-action command line, not user-facing text.
        CliCommands.TryResolve(["Purge"], out _).Should().BeFalse();
        CliCommands.TryResolve(["purge"], out _).Should().BeTrue();
    }
}
