// Copyright (c) 2024 All rights reserved
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// Static checks on the wizard's process-mitigation authoring: the SeedMitigation* rows in Package.wxs and
/// the Next-button Publish rows on AdvancedOptionsDlg in ConfigDialog.wxs.
/// <para>
/// No MSI is built or installed here - the dialog is never drawn by a <c>/quiet</c> CI job either, which is
/// why this exists. Instead the authored conditions are read out of the .wxs and evaluated with a small
/// evaluator for the subset of Windows Installer condition syntax they use (<c>NOT</c>/<c>AND</c>/<c>OR</c>,
/// parentheses, <c>=</c>/<c>&lt;&gt;</c> against string literals, bare-property truthiness), then the
/// wizard is simulated: seed, optionally let the "operator" change the controls, press Next. What this
/// does NOT prove is that msiexec runs the rows the way the table says - only that the table says the
/// right thing for all eight masks.
/// </para>
/// </summary>
public class InstallerMitigationAuthoringTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    private const string Mitigations = "JENKINS_MITIGATIONS";
    private const string Mode = "JENKINS_MITIGATIONS_MODE";
    private static readonly string[] Boxes = ["JENKINS_MIT_REMOTE", "JENKINS_MIT_LOWLABEL", "JENKINS_MIT_SYS32"];

    public static TheoryData<string> AllMasks => new() { "000", "001", "010", "011", "100", "101", "110", "111" };

    // ─── Command-line pass-through in a full-UI install (finding: Next overwrote it) ───

    [Theory]
    [MemberData(nameof(AllMasks))]
    public void A_mask_passed_on_the_command_line_survives_the_wizard(string mask)
    {
        var props = Seed(mask);

        props[Mode].Should().Be(mask == "111" ? "Full" : "Custom");
        for (var i = 0; i < Boxes.Length; i++)
        {
            IsSet(props, Boxes[i]).Should().Be(mask[i] == '1', $"{Boxes[i]} shows digit {i} of {mask}");
        }

        PressNext(props);

        props[Mitigations].Should().Be(mask);
    }

    [Theory]
    [InlineData(null)]       // not passed at all
    [InlineData("")]         // passed empty - restored to the default before CostFinalize
    public void With_nothing_passed_the_wizard_starts_at_Full_with_every_box_ticked(string? incoming)
    {
        var props = Seed(incoming);

        props[Mode].Should().Be("Full");
        Boxes.Should().OnlyContain(b => IsSet(props, b));

        PressNext(props);
        props[Mitigations].Should().Be("111");
    }

    [Fact]
    public void Choosing_Custom_and_touching_nothing_still_installs_all_three()
    {
        // The defect: the boxes used to start cleared, so Custom + Next silently installed 000.
        var props = Seed(null);
        props[Mode] = "Custom";

        PressNext(props);

        props[Mitigations].Should().Be("111");
    }

    [Theory]
    [MemberData(nameof(AllMasks))]
    public void Every_mask_is_reachable_by_ticking_boxes_in_Custom(string mask)
    {
        var props = Seed(null);
        props[Mode] = "Custom";
        for (var i = 0; i < Boxes.Length; i++)
        {
            // An MSI checkbox writes its CheckBoxValue when ticked and removes the property when cleared.
            if (mask[i] == '1')
            {
                props[Boxes[i]] = "1";
            }
            else
            {
                props.Remove(Boxes[i]);
            }
        }

        PressNext(props);

        props[Mitigations].Should().Be(mask);
    }

    [Theory]
    [InlineData("000")]
    [InlineData("011")]
    public void Going_back_to_Full_writes_all_three_whatever_the_boxes_show(string incoming)
    {
        var props = Seed(incoming);
        props[Mode] = "Full";

        PressNext(props);

        props[Mitigations].Should().Be("111");
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("AllowNetworkImages")]
    [InlineData("11")]
    public void A_malformed_value_does_not_preselect_Custom(string incoming)
    {
        // Explicit equality over the valid strings: a level name or a short string matches nothing.
        var props = Seed(incoming);

        props[Mode].Should().Be("Full");
        Boxes.Should().OnlyContain(b => IsSet(props, b));
    }

    // ─── Shape of the authoring the simulation relies on ───

    [Fact]
    public void Seeding_rows_are_ui_sequence_only_and_run_before_CostFinalize()
    {
        // /qn and /passive execute no UI sequence: the property must reach WriteAdvanced3 untouched there.
        var seeds = SeedRows().ToList();

        seeds.Select(s => (string)s.Attribute("Id")!).Should().BeEquivalentTo([Mode, .. Boxes]);
        seeds.Should().OnlyContain(s => (string?)s.Attribute("Sequence") == "ui");
        seeds.Should().OnlyContain(s => (string?)s.Attribute("Before") == "CostFinalize");
    }

    [Fact]
    public void JENKINS_MITIGATIONS_stays_Secure_with_its_empty_value_restore()
    {
        var package = Load("Package.wxs");

        var property = package.Descendants(Wix + "Property").Single(p => (string?)p.Attribute("Id") == Mitigations);
        ((string?)property.Attribute("Secure")).Should().Be("yes");

        var restore = package.Descendants(Wix + "SetProperty").Single(
            s => (string?)s.Attribute("Id") == Mitigations);
        ((string?)restore.Attribute("Sequence")).Should().Be("both");
        ((string?)restore.Attribute("Condition")).Should().Be($"NOT {Mitigations}");
    }

    [Fact]
    public void The_checkboxes_have_no_Property_row_so_a_cleared_digit_can_render_cleared()
    {
        Load("Package.wxs").Descendants(Wix + "Property")
            .Select(p => (string?)p.Attribute("Id"))
            .Should().NotIntersectWith(Boxes);
    }

    [Fact]
    public void The_advanced_merge_still_runs_on_fresh_install_only()
    {
        var row = Load("Package.wxs").Descendants(Wix + "Custom")
            .Single(c => (string?)c.Attribute("Action") == "WriteAdvanced3");

        ((string?)row.Attribute("Condition")).Should().Be("NOT Installed AND NOT WIX_UPGRADE_DETECTED");
    }

    // ─── Simulation ───

    private static Dictionary<string, string> Seed(string? incoming)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal) { [Mode] = "Full" };
        if (incoming is not null)
        {
            props[Mitigations] = incoming;
        }

        // The empty-value restore (Sequence="both", NOT JENKINS_MITIGATIONS), then the UI seeding rows in
        // authored order. The seeding conditions are order-independent with respect to the restore, which
        // the "" case above exercises.
        if (!IsSet(props, Mitigations))
        {
            props[Mitigations] = "111";
        }

        foreach (var row in SeedRows())
        {
            if (MsiCondition.Evaluate((string)row.Attribute("Condition")!, props))
            {
                props[(string)row.Attribute("Id")!] = (string)row.Attribute("Value")!;
            }
        }

        return props;
    }

    private static void PressNext(Dictionary<string, string> props)
    {
        var next = Load("ConfigDialog.wxs").Descendants(Wix + "Dialog")
            .Single(d => (string?)d.Attribute("Id") == "AdvancedOptionsDlg")
            .Elements(Wix + "Control").Single(c => (string?)c.Attribute("Id") == "Next");

        var rows = next.Elements(Wix + "Publish")
            .Where(p => p.Attribute("Property") is not null)
            .OrderBy(p => int.Parse((string)p.Attribute("Order")!, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        rows.Should().NotBeEmpty();

        foreach (var row in rows)
        {
            var condition = (string?)row.Attribute("Condition") ?? "1";
            if (MsiCondition.Evaluate(condition, props))
            {
                props[(string)row.Attribute("Property")!] = Format((string)row.Attribute("Value")!, props);
            }
        }
    }

    private static IEnumerable<XElement> SeedRows() =>
        Load("Package.wxs").Descendants(Wix + "SetProperty")
            .Where(s => ((string?)s.Attribute("Action"))?.StartsWith("SeedMitigation", StringComparison.Ordinal) == true);

    // [PROP] substitution - all the Formatted syntax the Next rows use.
    private static string Format(string value, IReadOnlyDictionary<string, string> props) =>
        Regex.Replace(value, @"\[([A-Za-z_][A-Za-z0-9_.]*)\]", m => props.GetValueOrDefault(m.Groups[1].Value, ""));

    private static bool IsSet(IReadOnlyDictionary<string, string> props, string name) =>
        !string.IsNullOrEmpty(props.GetValueOrDefault(name));

    private static XDocument Load(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JenkinsAsService.Installer", file);
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find src/JenkinsAsService.Installer/{file} above the test binary.");
    }

    /// <summary>
    /// The subset of Windows Installer condition syntax the mitigation rows use. Anything outside it throws,
    /// so a row rewritten in a richer form fails this test loudly instead of being mis-evaluated.
    /// </summary>
    private sealed class MsiCondition
    {
        private static readonly Regex Token = new(
            @"\s*(\(|\)|<>|=|""[^""]*""|[A-Za-z_][A-Za-z0-9_.]*|\d+)", RegexOptions.Compiled);

        private readonly List<string> _tokens;
        private readonly IReadOnlyDictionary<string, string> _props;
        private int _pos;

        private MsiCondition(string text, IReadOnlyDictionary<string, string> props)
        {
            _props = props;
            _tokens = [];
            var at = 0;
            while (at < text.Length)
            {
                if (char.IsWhiteSpace(text[at]))
                {
                    at++;
                    continue;
                }

                var m = Token.Match(text, at);
                if (!m.Success || m.Index != at)
                {
                    throw new NotSupportedException($"Unsupported condition syntax at {at}: {text}");
                }

                _tokens.Add(m.Groups[1].Value);
                at += m.Length;
            }
        }

        internal static bool Evaluate(string text, IReadOnlyDictionary<string, string> props)
        {
            var parser = new MsiCondition(text, props);
            var result = parser.Or();
            if (parser._pos != parser._tokens.Count)
            {
                throw new NotSupportedException($"Trailing tokens in condition: {text}");
            }

            return result;
        }

        private string? Peek => _pos < _tokens.Count ? _tokens[_pos] : null;

        private bool Or()
        {
            var value = And();
            while (Peek == "OR")
            {
                _pos++;
                value |= And();
            }

            return value;
        }

        private bool And()
        {
            var value = Not();
            while (Peek == "AND")
            {
                _pos++;
                value &= Not();
            }

            return value;
        }

        private bool Not()
        {
            if (Peek == "NOT")
            {
                _pos++;
                return !Not();
            }

            return Primary();
        }

        private bool Primary()
        {
            var token = Peek ?? throw new NotSupportedException("Unexpected end of condition.");
            _pos++;
            if (token == "(")
            {
                var inner = Or();
                if (Peek != ")")
                {
                    throw new NotSupportedException("Unbalanced parenthesis.");
                }

                _pos++;
                return inner;
            }

            var left = Operand(token);
            if (Peek is "=" or "<>")
            {
                var op = _tokens[_pos++];
                var right = Operand(_tokens[_pos++]);
                return op == "=" ? left == right : left != right;
            }

            // A bare integer literal is true when non-zero; a bare property is true when it has any value.
            return char.IsDigit(token[0]) ? left != "0" : !string.IsNullOrEmpty(left);
        }

        private string Operand(string token) =>
            token.StartsWith('"') ? token[1..^1]
            : char.IsDigit(token[0]) ? token
            : _props.GetValueOrDefault(token, "");
    }
}
