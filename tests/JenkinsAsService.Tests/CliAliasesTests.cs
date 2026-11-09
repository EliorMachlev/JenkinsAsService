// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class CliAliasesTests
{
    [Fact]
    public void Build_uses_first_letter_then_two_then_three()
    {
        var map = CliAliases.Build(["--secret", "--secret-file", "--secret-env", "--set-data-dir"]);

        map["--secret"].Should().Be("--s");
        map["--secret-file"].Should().Be("--se");
        map["--secret-env"].Should().Be("--sec");
        map["--set-data-dir"].Should().Be("--set");
    }

    [Fact]
    public void Build_ignores_hyphens_when_taking_a_prefix()
    {
        // "compact-log" is c-o-m..., not "c-" + something: the alias is typed, so it must be letters only.
        CliAliases.Build(["--custom-args", "--compact-log"])["--compact-log"].Should().Be("--co");
    }

    [Fact]
    public void Build_throws_when_no_alias_of_three_letters_is_free()
    {
        var act = () => CliAliases.Build(["--abc1", "--abc2", "--abc3", "--abc4"]);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resolve_maps_an_alias_to_its_long_form_and_leaves_everything_else()
    {
        var reverse = CliAliases.Reverse(CliAliases.Build(["--mitigations", "--mode"]));

        CliAliases.Resolve("--m", reverse).Should().Be("--mitigations");
        CliAliases.Resolve("--mo", reverse).Should().Be("--mode");
        CliAliases.Resolve("--mitigations", reverse).Should().Be("--mitigations");
        CliAliases.Resolve("111", reverse).Should().Be("111");
    }

    [Fact]
    public void Mitigations_owns_dash_dash_m_in_update_secret()
    {
        UpdateSecretCommand.Aliases["--mitigations"].Should().Be("--m");
        UpdateSecretCommand.Aliases["--mode"].Should().Be("--mo");
        UpdateSecretCommand.Aliases["--method"].Should().Be("--me");
    }

    [Fact]
    public void Every_alias_is_unique_and_never_shadows_a_long_option()
    {
        foreach (var aliases in new[] { UpdateSecretCommand.Aliases, GrantDataAccessCommand.Aliases, ConfigureRecoveryCommand.Aliases })
        {
            aliases.Values.Should().OnlyHaveUniqueItems();
            aliases.Values.Should().NotIntersectWith(aliases.Keys);
        }
    }

    [Fact]
    public void Update_secret_accepts_aliases_in_place_of_long_options()
    {
        var args = new[] { UpdateSecretCommand.Name, "--m", "101", "--si" };
        var parsed = typeof(UpdateSecretCommand)
            .GetMethod("ParseArgs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [args]);

        parsed.Should().NotBeNull();
        args[1].Should().Be("--mitigations");
        args[3].Should().Be("--silent");
    }

    [Fact]
    public void Other_commands_accept_aliases()
    {
        ConfigureRecoveryCommand.ParseArguments([ConfigureRecoveryCommand.Name, "--s", "Foo"]).ServiceName.Should().Be("Foo");

        var (path, account, error) = GrantDataAccessCommand.ParseArguments(
            [GrantDataAccessCommand.Name, "--p", "C:\\data", "--a", "NT SERVICE\\Jenkins"]);
        error.Should().BeNull();
        path.Should().NotBeNull();
        account.Should().Be("NT SERVICE\\Jenkins");
    }

    [Fact]
    public void Help_is_reachable_as_dash_dash_h()
    {
        CliCommands.WantsHelp(["--h"]).Should().BeTrue();
    }
}
