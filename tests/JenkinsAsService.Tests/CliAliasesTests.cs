// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class CliAliasesTests
{
    [Fact]
    public void Uses_first_letter_then_two_then_three()
    {
        var aliases = new CliAliases(["--secret", "--secret-file", "--secret-env", "--set-data-dir"]);

        aliases["--secret"].Should().Be("--s");
        aliases["--secret-file"].Should().Be("--se");
        aliases["--secret-env"].Should().Be("--sec");
        aliases["--set-data-dir"].Should().Be("--set");
    }

    [Fact]
    public void Ignores_hyphens_when_taking_a_prefix()
    {
        // The alias is typed, so it is letters only: "compact-log" -> --co, never "--c-".
        new CliAliases(["--custom-args", "--compact-log"])["--compact-log"].Should().Be("--co");
    }

    [Fact]
    public void Never_shadows_a_long_option()
    {
        // "--u" is free, but "--url" is a real option, so nothing may be aliased to it.
        var aliases = new CliAliases(["--url", "--urgent"]);

        aliases.All.Values.Should().NotContain("--url");
        aliases["--urgent"].Should().Be("--ur");
    }

    [Fact]
    public void Throws_when_no_alias_of_three_letters_is_free()
    {
        var act = () => new CliAliases(["--abc1", "--abc2", "--abc3", "--abc4"]);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resolve_maps_an_alias_to_its_long_form_and_leaves_everything_else()
    {
        var aliases = new CliAliases(["--mitigations", "--mode"]);

        aliases.Resolve("--m").Should().Be("--mitigations");
        aliases.Resolve("--mo").Should().Be("--mode");
        aliases.Resolve("--mitigations").Should().Be("--mitigations");
        aliases.Resolve("111").Should().Be("111");
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
            aliases.All.Values.Should().OnlyHaveUniqueItems();
            aliases.All.Values.Should().NotIntersectWith(aliases.All.Keys);
        }
    }

    [Fact]
    public void Update_secret_usage_text_lists_every_alias()
    {
        // The usage text spells aliases out by hand; this is what stops it drifting from the table.
        foreach (var (option, alias) in UpdateSecretCommand.Aliases.All)
        {
            UpdateSecretCommand.UsageText.Should().Contain($"{alias}, {option}");
        }
    }

    [Fact]
    public void Update_secret_resolves_aliases_in_place_of_long_options()
    {
        var args = new[] { UpdateSecretCommand.Name, "--m", "101", "--si" };

        typeof(UpdateSecretCommand)
            .GetMethod("ParseArgs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [args]).Should().NotBeNull();

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
