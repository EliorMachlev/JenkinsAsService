// Copyright (c) 2024 All rights reserved
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// Static checks on how JENKINS_SKIP_VERIFICATION reaches update-secret: authored as a derived property that is
/// the skip flag or nothing, passed QUOTED like every other expansion, so an unset one arrives as an empty
/// argument the parser ignores. Each command line is formatted the way msiexec would, split the way the exe's
/// runtime splits it, and parsed by the real parser - so a change on either side that breaks the hand-off
/// fails here rather than as a 1603 in CI's real-install job. Test-MsiPlatform.ps1 checks the same wiring in
/// the BUILT packages.
/// </summary>
public class InstallerVerificationAuthoringTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private const string SkipProperty = "JENKINS_SKIP_VERIFICATION";
    private const string SkipArg = "JasSkipVerificationArg";

    [Fact]
    public void The_skip_property_is_a_secure_checkbox_property_with_no_default_and_no_restore()
    {
        var package = Load("Package.wxs");

        var property = package.Descendants(Wix + "Property").Single(p => Id(p) == SkipProperty);
        ((string?)property.Attribute("Secure")).Should().Be("yes", "it must survive the UI-to-execute handoff");
        property.Attribute("Value").Should().BeNull("empty is the value 'unchecked'");
        package.Descendants(Wix + "SetProperty").Should().NotContain(s => Id(s) == SkipProperty,
            "a checkbox property must not have its empty value 'restored' - that would make unticking impossible");
    }

    [Fact]
    public void The_derived_argument_is_the_flag_only_for_exactly_1_and_is_computed_in_the_execute_sequence()
    {
        var row = Load("Package.wxs").Descendants(Wix + "SetProperty").Single(s => Id(s) == SkipArg);

        ((string?)row.Attribute("Value")).Should().Be("--svs");
        ((string?)row.Attribute("Condition")).Should().Be($"{SkipProperty} = \"1\"");
        ((string?)row.Attribute("Sequence")).Should().Be("execute", "that is where deferred command lines are formatted");
        ((string?)row.Attribute("Before")).Should().Be("CostFinalize");
        UpdateSecretCommand.Aliases.Resolve("--svs").Should().Be("--skip-verification-steps");
    }

    [Theory]
    [InlineData("WriteConfig")]
    [InlineData("UpgradeConfig")]
    public void The_custom_actions_that_write_a_url_carry_the_quoted_skip_argument(string action)
    {
        Command(action).Should().Contain($"\"[{SkipArg}]\"");
    }

    [Fact]
    public void Every_custom_action_command_line_stays_within_the_msi_limit()
    {
        foreach (var action in Load("Package.wxs").Descendants(Wix + "CustomAction").Where(c => c.Attribute("ExeCommand") is not null))
        {
            ((string)action.Attribute("ExeCommand")!).Length.Should().BeLessThanOrEqualTo(255, Id(action));
        }
    }

    public static TheoryData<string> UpdateSecretActions => new()
    {
        "WriteConfig", "WriteAdvanced1", "WriteAdvanced2", "WriteAdvanced3", "UpgradeConfig", "SetDataDir"
    };

    [Theory]
    [MemberData(nameof(UpdateSecretActions))]
    public void Every_update_secret_command_line_parses_with_every_property_empty(string action)
    {
        // Nothing set at all - including JasSkipVerificationArg, which then arrives as "".
        var args = Split(Format(Command(action), _ => ""));

        args[0].Should().Be(UpdateSecretCommand.Name);
        var parsed = UpdateSecretCommand.ParseArgs(args);
        parsed.Should().NotBeNull($"{action} must parse when every property is unset");
        parsed!.SkipVerification.Should().BeFalse("verification is on unless JENKINS_SKIP_VERIFICATION=1");
    }

    [Theory]
    [InlineData("WriteConfig")]
    [InlineData("UpgradeConfig")]
    public void With_the_box_ticked_the_command_line_skips_verification(string action)
    {
        var values = new Dictionary<string, string>
        {
            [SkipArg] = "--svs",
            ["JENKINS_URL"] = "https://jenkins.example.com:8443",
            ["JENKINS_SECRET"] = "s",
            ["JENKINS_SECRET_MODE"] = "Dpapi",
            ["SERVICE_ACCOUNT"] = @"NT SERVICE\Jenkins",
            ["JENKINS_JAVA_PATH"] = @"C:\Program Files\Java\jdk-21\bin",
        };

        var parsed = UpdateSecretCommand.ParseArgs(Split(Format(Command(action), p => values.GetValueOrDefault(p, ""))));

        parsed.Should().NotBeNull();
        parsed!.SkipVerification.Should().BeTrue();
        parsed.Url.Should().Be("https://jenkins.example.com:8443");
    }

    [Fact]
    public void The_agent_name_still_reaches_the_config_on_a_fresh_install()
    {
        // It moved from WriteConfig to WriteAdvanced3 to make room under the 255-character limit; both run on
        // every fresh install, so it must be in exactly one of them.
        var carriers = new[] { "WriteConfig", "WriteAdvanced3" }.Where(a => Command(a).Contains("[JENKINS_AGENT_NAME]")).ToList();
        carriers.Should().Equal("WriteAdvanced3");

        var parsed = UpdateSecretCommand.ParseArgs(Split(Format(Command("WriteAdvanced3"),
            p => p == "JENKINS_AGENT_NAME" ? "build-01" : "")));
        parsed!.AgentName.Should().Be("build-01");
        parsed.Merge.Should().BeTrue();
    }

    [Fact]
    public void The_wizard_offers_the_box_on_Advanced_Options()
    {
        var box = Load("ConfigDialog.wxs").Descendants(Wix + "Dialog").Single(d => Id(d) == "AdvancedOptionsDlg")
            .Elements(Wix + "Control").Single(c => (string?)c.Attribute("Property") == SkipProperty);

        ((string?)box.Attribute("Type")).Should().Be("CheckBox");
        ((string?)box.Attribute("CheckBoxValue")).Should().Be("1", "the value the derived argument tests for");
    }

    [Fact]
    public void The_java_path_prefill_is_the_bin_folder_of_JAVA_HOME()
    {
        // A JDK root would now fail verification on every wizard install with JAVA_HOME set.
        var row = Load("Package.wxs").Descendants(Wix + "SetProperty").Single(s => Id(s) == "JENKINS_JAVA_PATH");

        ((string?)row.Attribute("Value")).Should().Be(@"[%JAVA_HOME]\bin");
        ((string?)row.Attribute("Condition")).Should().Contain("%JAVA_HOME", "an unset JAVA_HOME must not prefill a bare \\bin");
    }

    [Fact]
    public void The_bundle_forwards_the_skip_property()
    {
        // MsiProperties.wxi generates every package's MsiProperty rows from this one list.
        var forwarded = Load(Path.Combine("..", "JenkinsAsService.Bundle", "Bundle.wxs")).DescendantNodes()
            .OfType<XProcessingInstruction>()
            .Single(pi => pi.Target == "define" && pi.Data.TrimStart().StartsWith("JasForwardedSettings", StringComparison.Ordinal))
            .Data;

        forwarded.Split('=', 2)[1].Split(';').Select(s => s.Trim()).Should().Contain(SkipProperty);
    }

    // ─── Helpers ───

    private static string Command(string action) =>
        (string)Load("Package.wxs").Descendants(Wix + "CustomAction").Single(c => Id(c) == action).Attribute("ExeCommand")!;

    private static string? Id(XElement e) => (string?)e.Attribute("Id");

    // [PROP] substitution, as msiexec formats a deferred EXE custom action's command line.
    private static string Format(string command, Func<string, string> value) =>
        Regex.Replace(command, @"\[([A-Za-z_][A-Za-z0-9_.]*)\]", m => value(m.Groups[1].Value));

    /// <summary>
    /// Splits a command line by the CommandLineToArgvW rules the .NET runtime applies to its own: whitespace
    /// separates outside quotes; 2n backslashes before a quote give n backslashes and the quote toggles quoting;
    /// 2n+1 give n and a literal quote; other backslashes are literal. A quoted empty string is an argument.
    /// </summary>
    private static string[] Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var run = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    run++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', run);
                    i--;
                }

                started = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            args.Add(current.ToString());
        }

        return [.. args];
    }

    private static XDocument Load(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.GetFullPath(Path.Join(dir.FullName, "src", "JenkinsAsService.Installer", file));
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {file} relative to src/JenkinsAsService.Installer above the test binary.");
    }
}
