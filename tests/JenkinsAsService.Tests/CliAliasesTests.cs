// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class CliAliasesTests
{
    // Asserts the allocator itself, with the secret names standing in as ordinary options. update-secret
    // passes its --secret* family as unaliased; see Update_secret_secret_options_have_no_short_alias.
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
    public void Update_secret_resolves_aliases_without_rewriting_the_callers_arguments()
    {
        var args = new[] { UpdateSecretCommand.Name, "--m", "011", "--si" };
        var original = args.ToArray();

        var parsed = UpdateSecretCommand.ParseArgs(args);

        parsed.Should().NotBeNull();
        parsed!.Mitigations.Should().Be(new MitigationMask(false, true, true));
        parsed.Silent.Should().BeTrue();
        args.Should().Equal(original, "the argument array belongs to the caller");
    }

    [Fact]
    public void Unaliased_options_get_no_alias_and_reserve_every_prefix()
    {
        var aliases = new CliAliases(["--set-data-dir", "--silent"], unaliased: ["--secret", "--secret-env"]);

        aliases.All.Keys.Should().NotContain(["--secret", "--secret-env"]);
        aliases.All.Values.Should().NotContain(["--s", "--se", "--sec"]);
        aliases["--set-data-dir"].Should().Be("--set");
        aliases["--silent"].Should().Be("--si");
        aliases.Resolve("--se").Should().Be("--se", "a reserved prefix resolves to nothing");
    }

    [Fact]
    public void Update_secret_secret_options_have_no_short_alias()
    {
        foreach (var option in new[] { "--secret", "--secret-file", "--secret-env" })
        {
            UpdateSecretCommand.Aliases.All.Should().NotContainKey(option);
            UpdateSecretCommand.UsageText.Should().NotContain($", {option} ", "the usage text must not advertise one");
        }

        // And no other option inherited the prefixes they used to own.
        UpdateSecretCommand.Aliases.All.Values.Should().NotContain(["--s", "--se", "--sec"]);
    }

    [Theory]
    [InlineData("--s")]
    [InlineData("--se")]
    [InlineData("--sec")]
    public void Update_secret_rejects_the_former_secret_aliases_as_unknown(string alias)
    {
        UpdateSecretCommand.ParseArgs([UpdateSecretCommand.Name, alias, "value"]).Should().BeNull();
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

    // ─── Explicit aliases: --svs for --skip-verification-steps ────────────────

    [Fact]
    public void Skip_verification_has_the_chosen_alias_not_the_allocated_one()
    {
        // The allocator would have given --sk, one keystroke from the reserved --s of the --secret* family.
        UpdateSecretCommand.Aliases["--skip-verification-steps"].Should().Be("--svs");
        UpdateSecretCommand.Aliases.Resolve("--svs").Should().Be("--skip-verification-steps");
        UpdateSecretCommand.Aliases.All.Values.Should().NotContain("--sk");
        UpdateSecretCommand.ParseArgs([UpdateSecretCommand.Name, "--sk"]).Should().BeNull("--sk is not an option");
    }

    [Fact]
    public void Skip_verification_is_listed_in_the_usage_text()
    {
        UpdateSecretCommand.UsageText.Should().Contain("--svs, --skip-verification-steps");
    }

    [Fact]
    public void Adding_the_explicit_alias_moved_no_other_alias()
    {
        // Every pre-existing alias, as documented in configuration.html. An explicit alias takes no prefix in
        // the allocation, so the option could be added without renaming anything anyone has typed.
        var expected = new Dictionary<string, string>
        {
            ["--mitigations"] = "--m", ["--url"] = "--u", ["--mode"] = "--mo", ["--dpapi-scope"] = "--d",
            ["--thumbprint"] = "--t", ["--service-account"] = "--ser", ["--set-data-dir"] = "--set",
            ["--agent-name"] = "--a", ["--java-path"] = "--j", ["--username"] = "--us", ["--method"] = "--me",
            ["--via-file"] = "--v", ["--custom-args"] = "--c", ["--sanitize-env"] = "--sa", ["--allowed-env"] = "--al",
            ["--debug"] = "--de", ["--compact-log"] = "--co", ["--retained-logs"] = "--r", ["--max-retries"] = "--ma",
            ["--silent"] = "--si", ["--impersonate"] = "--i", ["--merge"] = "--mer", ["--upgrade"] = "--up",
            ["--help"] = "--h",
        };

        foreach (var (option, alias) in expected)
        {
            UpdateSecretCommand.Aliases[option].Should().Be(alias, option);
        }
    }

    [Fact]
    public void An_explicit_alias_must_be_free()
    {
        // Taken by a long option, by a reserved prefix, and by another explicit alias.
        var shadowsOption = () => new CliAliases(["--url", "--skip"], explicitAliases: new Dictionary<string, string> { ["--skip"] = "--url" });
        var takesReserved = () => new CliAliases(["--skip"], ["--secret"], new Dictionary<string, string> { ["--skip"] = "--se" });
        var twice = () => new CliAliases(["--a1", "--a2"],
            explicitAliases: new Dictionary<string, string> { ["--a1"] = "--x", ["--a2"] = "--x" });

        shadowsOption.Should().Throw<InvalidOperationException>();
        takesReserved.Should().Throw<InvalidOperationException>();
        twice.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_explicit_alias_must_name_a_real_option_and_look_like_one()
    {
        var unknown = () => new CliAliases(["--url"], explicitAliases: new Dictionary<string, string> { ["--nope"] = "--n" });
        var malformed = () => new CliAliases(["--url"], explicitAliases: new Dictionary<string, string> { ["--url"] = "-u" });

        unknown.Should().Throw<InvalidOperationException>();
        malformed.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_explicit_alias_is_reserved_before_allocation()
    {
        // --svs-something's prefixes are --s, --sv, --svs; the first two go to earlier options, and the third is
        // the chosen alias of an option listed AFTER it. Reserved first, so it is not handed out twice: the
        // constructor fails instead, as for any option with no free prefix.
        var act = () => new CliAliases(["--s1", "--sv1", "--svs-something", "--skip"],
            explicitAliases: new Dictionary<string, string> { ["--skip"] = "--svs" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*--svs-something*");
    }

    [Fact]
    public void Help_is_reachable_as_dash_dash_h()
    {
        CliCommands.WantsHelp(["--h"]).Should().BeTrue();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--h")]
    [InlineData("-h")]
    public void A_help_flag_at_an_option_position_is_help(string flag)
    {
        CliCommands.WantsHelp(["update-secret", "--silent", flag], a => a == "--url").Should().BeTrue();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--h")]
    [InlineData("-h")]
    public void A_help_flag_in_a_value_position_is_a_value_not_help(string flag)
    {
        CliCommands.WantsHelp(["update-secret", "--silent", "--custom-args", flag], a => a == "--custom-args")
            .Should().BeFalse();
    }

    [Fact]
    public void A_help_flag_after_a_value_that_follows_a_value_option_is_still_help()
    {
        // --url consumes "x"; the next token is back at an option position.
        CliCommands.WantsHelp(["update-secret", "--url", "x", "--h"], a => a == "--url").Should().BeTrue();
    }
}
