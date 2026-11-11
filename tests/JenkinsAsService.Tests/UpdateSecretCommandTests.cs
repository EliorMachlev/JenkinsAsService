// Copyright (c) 2024 All rights reserved
using System.Text.Json;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class UpdateSecretCommandTests : IDisposable
{
    private readonly string _tempDir;

    public UpdateSecretCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_CLI_Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ─── Help ────────────────────────────────────────────────────────────────

    [Fact]
    public void Help_exits_zero()
    {
        UpdateSecretCommand.Run(["update-secret", "--help"], _tempDir).Should().Be(0);
    }

    // ─── Silent mode — required field validation ──────────────────────────────

    [Fact]
    public void Silent_missing_secret_returns_1()
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--url", "https://jenkins:8443", "--mode", "Unprotected"],
            _tempDir);
        code.Should().Be(1);
    }

    [Fact]
    public void Silent_missing_url_returns_1()
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--secret", "s3cr3t", "--mode", "Unprotected"],
            _tempDir);
        code.Should().Be(1);
    }

    [Fact]
    public void Silent_missing_mode_returns_1()
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--secret", "s3cr3t", "--url", "https://jenkins:8443"],
            _tempDir);
        code.Should().Be(1);
    }

    [Fact]
    public void Silent_invalid_mode_returns_1()
    {
        // C4 fix: typo in --mode must fail rather than silently default to Dpapi
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--secret", "s3cr3t", "--url", "https://jenkins:8443", "--mode", "Dapi"],
            _tempDir);
        code.Should().Be(1);
    }

    // ─── ParseMode ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Dpapi",               SecretMode.Dpapi)]
    [InlineData("dpapi",               SecretMode.Dpapi)]
    [InlineData("Unprotected",         SecretMode.Unprotected)]
    [InlineData("EnvironmentVariable", SecretMode.EnvironmentVariable)]
    [InlineData("CredentialManager",   SecretMode.CredentialManager)]
    public void ParseMode_recognises_all_valid_modes(string input, SecretMode expected)
    {
        UpdateSecretCommand.ParseMode(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Dapi")]
    [InlineData("plaintext")]
    [InlineData("DPAPI")] // exact-case variant already covered above; test a clearly wrong value
    public void ParseMode_returns_null_for_invalid_input(string input)
    {
        // Must return null, not silently default to Dpapi
        UpdateSecretCommand.ParseMode(input == "DPAPI" ? "NotAMode" : input).Should().BeNull();
    }

    // ─── Silent mode — successful write ──────────────────────────────────────

    [Fact]
    public void Silent_unprotected_writes_config()
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent",
             "--secret", "my-secret",
             "--url", "https://jenkins:8443",
             "--mode", "Unprotected",
             "--agent-name", "build-agent-01"],
            _tempDir);

        code.Should().Be(0);

        var json = ReadConfig();
        var jenkins = json.RootElement.GetProperty("Jenkins");
        jenkins.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("my-secret");
        jenkins.GetProperty("Connection").GetProperty("Url").GetString().Should().Be("https://jenkins:8443");
        jenkins.GetProperty("Connection").GetProperty("AgentName").GetString().Should().Be("build-agent-01");
        jenkins.GetProperty("Secret").GetProperty("Mode").GetString().Should().Be("Unprotected");
    }

    // ─── --secret-file ────────────────────────────────────────────────────────

    [Fact]
    public void SecretFile_reads_secret_and_deletes_file()
    {
        var secretFile = Path.Combine(_tempDir, "secret.tmp");
        File.WriteAllText(secretFile, "file-secret\n"); // trailing newline trimmed

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent",
             "--secret-file", secretFile,
             "--url", "https://jenkins:8443",
             "--mode", "Unprotected"],
            _tempDir);

        code.Should().Be(0);
        File.Exists(secretFile).Should().BeFalse("file should be deleted after reading");

        var json = ReadConfig();
        json.RootElement.GetProperty("Jenkins").GetProperty("Secret").GetProperty("Value").GetString()
            .Should().Be("file-secret");
    }

    [Fact]
    public void SecretFile_missing_file_returns_1()
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent",
             "--secret-file", Path.Combine(_tempDir, "nonexistent.tmp"),
             "--url", "https://jenkins:8443",
             "--mode", "Unprotected"],
            _tempDir);

        code.Should().Be(1);
    }

    // ─── --secret-env ─────────────────────────────────────────────────────────

    [Fact]
    public void SecretEnv_reads_secret_from_env_var()
    {
        const string varName = "JAS_TEST_SECRET_TEMP";
        Environment.SetEnvironmentVariable(varName, "env-secret");
        try
        {
            var code = UpdateSecretCommand.Run(
                ["update-secret", "--silent",
                 "--secret-env", varName,
                 "--url", "https://jenkins:8443",
                 "--mode", "Unprotected"],
                _tempDir);

            code.Should().Be(0);
            var json = ReadConfig();
            json.RootElement.GetProperty("Jenkins").GetProperty("Secret").GetProperty("Value").GetString()
                .Should().Be("env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void SecretEnv_missing_env_var_returns_1()
    {
        const string varName = "JAS_TEST_SECRET_DEFINITELY_NOT_SET_XYZ";
        Environment.SetEnvironmentVariable(varName, null); // ensure not set

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent",
             "--secret-env", varName,
             "--url", "https://jenkins:8443",
             "--mode", "Unprotected"],
            _tempDir);

        code.Should().Be(1);
    }

    // ─── Unknown option ───────────────────────────────────────────────────────

    [Fact]
    public void Unknown_option_returns_1()
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--not-a-real-option"],
            _tempDir);
        code.Should().Be(1);
    }

    // ─── Merge mode ───────────────────────────────────────────────────────────

    [Fact]
    public void Merge_applies_optional_fields_without_a_secret()
    {
        // Seed a config as WriteConfig would have.
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            "{\"Jenkins\":{\"Secret\":{\"Value\":\"pre-written\",\"Mode\":\"Dpapi\"}}}");

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge",
             "--method", "WebSocket",
             "--max-retries", "4",
             "--debug", "1",
             "--via-file", "0",
             "--custom-args", "-noCertificateCheck"],
            _tempDir);

        code.Should().Be(0);
        var j = ReadConfig().RootElement.GetProperty("Jenkins");
        j.GetProperty("Connection").GetProperty("Method").GetString().Should().Be("WebSocket");
        j.GetProperty("Recovery").GetProperty("MaxRetries").GetInt32().Should().Be(4);
        j.GetProperty("Logging").GetProperty("DebugMode").GetBoolean().Should().BeTrue();
        j.GetProperty("Secret").GetProperty("ViaFile").GetBoolean().Should().BeFalse();
        j.GetProperty("Agent").GetProperty("CustomArguments").GetString().Should().Be("-noCertificateCheck");
        j.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("pre-written", "merge never touches the secret");
    }

    [Fact]
    public void Merge_with_empty_quoted_bool_treats_as_false()
    {
        // Mirrors an unchecked MSI checkbox: --debug "" arrives as an empty argument.
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), "{\"Jenkins\":{}}");

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge", "--debug", "", "--sanitize-env", "1"],
            _tempDir);

        code.Should().Be(0);
        var logging = ReadConfig().RootElement.GetProperty("Jenkins").GetProperty("Logging");
        logging.GetProperty("DebugMode").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("Auto", ConnectionMethod.Auto)]
    [InlineData("websocket", ConnectionMethod.WebSocket)]
    [InlineData("Https", ConnectionMethod.Https)]
    public void ParseMethod_recognises_valid_methods(string input, ConnectionMethod expected)
    {
        UpdateSecretCommand.ParseMethod(input).Should().Be(expected);
    }

    [Fact]
    public void ParseMethod_returns_null_for_invalid()
    {
        UpdateSecretCommand.ParseMethod("Carrier-Pigeon").Should().BeNull();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void ParseBool_maps_known_values(string input, bool expected)
    {
        UpdateSecretCommand.ParseBool(input).Should().Be(expected);
    }

    [Fact]
    public void ParseInt_parses_and_rejects()
    {
        UpdateSecretCommand.ParseInt("7").Should().Be(7);
        UpdateSecretCommand.ParseInt("nope").Should().BeNull();
        UpdateSecretCommand.ParseInt("").Should().BeNull();
    }

    // ─── SanitizePathArgument ────────────────────────────────────────────────
    // Guards the DATAFOLDER command-line escaping bug: a directory property resolves as "D:\Jenkins\", and
    // the quoted "\"" on the CA command line is parsed as an escaped quote, so the exe receives 'D:\Jenkins"'.

    [Theory]
    [InlineData("D:\\Jenkins\"", "D:\\Jenkins")]  // mangled: stray trailing quote from \"-escaping
    [InlineData("D:\\Jenkins\\", "D:\\Jenkins")]  // clean trailing separator normalised away
    [InlineData("D:\\Jenkins", "D:\\Jenkins")]    // already clean
    [InlineData("  D:\\My Data\"  ", "D:\\My Data")] // spaces in path + surrounding whitespace
    [InlineData("D:\\", "D:\\")]                    // drive root preserved
    public void SanitizePathArgument_recovers_mangled_paths(string input, string expected)
    {
        UpdateSecretCommand.SanitizePathArgument(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"")]
    public void SanitizePathArgument_returns_null_for_empty(string? input)
    {
        UpdateSecretCommand.SanitizePathArgument(input).Should().BeNull();
    }

    [Fact]
    public void SetDataDir_with_escaped_trailing_quote_writes_clean_path()
    {
        // Reproduces the installer bug: the mangled 'D:\Jenkins"' must be stored as 'D:\Jenkins',
        // not 'D:\Jenkins"' (which serialised as "D:\\Jenkins"").
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--set-data-dir", "D:\\Jenkins\""],
            _tempDir);

        code.Should().Be(0);
        ReadConfig().RootElement.GetProperty("Jenkins").GetProperty("Agent")
            .GetProperty("DataDirectory").GetString().Should().Be("D:\\Jenkins");
    }

    // ─── Upgrade mode ─────────────────────────────────────────────────────────

    [Fact]
    public void Upgrade_with_existing_secret_reconciles_and_preserves()
    {
        // Config predating a new key, has extra/unknown keys, and carries a secret.
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            "{\"Jenkins\":{\"Secret\":{\"Value\":\"live-secret\",\"Mode\":\"Tpm\",\"LegacyFlag\":true}}}");

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--upgrade",
             "--secret", "", "--url", "", "--mode", "Dpapi"],
            _tempDir);

        code.Should().Be(0);
        var j = ReadConfig().RootElement.GetProperty("Jenkins");
        j.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("live-secret", "the secret is preserved");
        j.GetProperty("Secret").TryGetProperty("LegacyFlag", out _).Should().BeFalse("unknown keys are pruned");
        j.GetProperty("Recovery").GetProperty("MaxRetries").GetInt32().Should().Be(0, "missing keys are added");
    }

    [Fact]
    public void Upgrade_without_secret_writes_from_args()
    {
        // No secret on disk → repair-write from supplied args.
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            "{\"Jenkins\":{\"Connection\":{\"Url\":\"https://old:8443\"}}}");

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--upgrade",
             "--secret", "repaired", "--url", "https://new:8443", "--mode", "Unprotected"],
            _tempDir);

        code.Should().Be(0);
        var j = ReadConfig().RootElement.GetProperty("Jenkins");
        j.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("repaired");
        j.GetProperty("Connection").GetProperty("Url").GetString().Should().Be("https://new:8443");
    }

    [Fact]
    public void Upgrade_without_secret_and_no_args_fails()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            "{\"Jenkins\":{\"Connection\":{\"Url\":\"https://old:8443\"}}}");

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--upgrade", "--secret", "", "--url", "", "--mode", "Dpapi"],
            _tempDir);

        code.Should().Be(1, "no existing secret and none supplied must fail, as a fresh silent install does");
    }

    [Fact]
    public void Upgrade_against_corrupt_config_leaves_file_untouched()
    {
        // Corrupt JSON that would-be hold a secret: must never be repair-written over with defaults —
        // it may be unparseable but still carry a secret we simply cannot read.
        const string corrupt = "{ broken";
        var configPath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(configPath, corrupt);

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--upgrade", "--secret", "repaired", "--url", "https://new:8443", "--mode", "Unprotected"],
            _tempDir);

        code.Should().Be(0, "an unreadable config must never fail or roll back the upgrade");
        File.ReadAllText(configPath).Should().Be(corrupt, "the corrupt file must not be overwritten with defaults");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private JsonDocument ReadConfig()
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        path.Should().Match(p => File.Exists(p), "appsettings.json should have been written");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Theory]
    [InlineData("111")]
    [InlineData("011")]
    [InlineData("101")]
    [InlineData("000")]
    public void ParseMitigations_accepts_three_binary_digits(string value) =>
        UpdateSecretCommand.ParseMitigations(value)?.ToString().Should().Be(value);

    [Theory]
    [InlineData("true")]
    [InlineData("Full")]
    [InlineData("11")]
    [InlineData("1112")]
    public void ParseMitigations_rejects_a_malformed_value(string value) =>
        CaptureStderr(() => UpdateSecretCommand.ParseMitigations(value).Should().BeNull())
            .Should().Contain("--mitigations").And.Contain("each 0 or 1").And.NotContain($"'{value}'");

    // An unset installer property reaches the custom action as "" - every [PROPERTY] expansion is quoted
    // for exactly that reason - so empty must mean "leave it alone" and must NOT write a parse error into
    // the MSI log of an install that did nothing wrong. Asserted for EVERY enum option, not just the newest:
    // they share one body now, and this is the property that made sharing it worthwhile.
    public static TheoryData<string, Func<string?, object?>> UnsetEnumOptions => new()
    {
        { "mode", v => UpdateSecretCommand.ParseMode(v) },
        { "dpapi-scope", v => UpdateSecretCommand.ParseDpapiScope(v) },
        { "method", v => UpdateSecretCommand.ParseMethod(v) },
        { "mitigations", v => UpdateSecretCommand.ParseMitigations(v) }
    };

    // Console.Error is process-wide, so every capture must restore it even when the assertion inside
    // throws - otherwise one failing test silently swallows the stderr of every test that runs after it.
    // Written once rather than as a set/try/finally at each site.
    private static string CaptureStderr(Action action)
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return stderr.ToString();
    }

    [Theory]
    [MemberData(nameof(UnsetEnumOptions))]
    public void An_unset_enum_option_is_no_change_and_is_silent(string option, Func<string?, object?> parse)
    {
        foreach (var unset in new[] { null, "", "   " })
        {
            var stderr = CaptureStderr(() => parse(unset).Should().BeNull($"--{option} '{unset}' means unset"));

            stderr.Should().BeEmpty($"--{option} '{unset}' is not an error");
        }
    }

    [Theory]
    [MemberData(nameof(UnsetEnumOptions))]
    public void A_misspelled_enum_option_still_reports_the_valid_values(string option, Func<string?, object?> parse)
    {
        var stderr = CaptureStderr(() => parse("Carrier-Pigeon").Should().BeNull());

        // The silent-empty rule must not have made real typos silent too.
        stderr.Should().Contain(option).And.Contain("Valid:");
    }

    // ─── A supplied-but-unusable value is fatal ───────────────────────────────
    //
    // These parse paths run as MSI deferred custom actions whose Return is the default "check", so the
    // exit code decides whether a typo fails the install or is silently ignored. It used to be ignored:
    // `--max-retries fifty` installed a service with the default retry count and reported success. The
    // pair of theories below pins BOTH halves of the rule, because fixing one half alone is what would
    // break the installer - make empty fatal and every silent install dies on its first unset option.

    public static TheoryData<string, string> RejectedValues => new()
    {
        { "--mode", "Dapi" },
        { "--dpapi-scope", "Machyne" },
        { "--method", "Carrier-Pigeon" },
        { "--mitigations", "AllowNetworkImage" },
        { "--via-file", "maybe" },
        { "--sanitize-env", "sometimes" },
        { "--debug", "loud" },
        { "--compact-log", "ish" },
        { "--retained-logs", "several" },
        { "--max-retries", "fifty" }
    };

    [Theory]
    [MemberData(nameof(RejectedValues))]
    public void A_supplied_value_that_cannot_be_used_fails_the_command(string option, string value)
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge", option, value], _tempDir);

        code.Should().Be(1, $"{option} {value} is not usable");
        File.Exists(Path.Combine(_tempDir, "appsettings.json"))
            .Should().BeFalse("a rejected command must not half-write a config");
    }

    [Theory]
    [MemberData(nameof(RejectedValues))]
    public void An_empty_value_for_the_same_option_is_accepted_and_changes_nothing(string option, string _)
    {
        // The counterpart to the theory above: an untouched installer property arrives as "", and every
        // one of these options must tolerate that. Run them all in one command, exactly as WriteAdvanced
        // does, so the merge path is exercised the way the MSI actually calls it.
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge", option, ""], _tempDir);

        code.Should().Be(0, $"{option} \"\" means unset, not invalid");
    }

    [Fact]
    public void An_option_with_no_value_at_all_fails_the_command()
    {
        // --method consumes the next token; at the end of the argument list there is none. This reported
        // the problem and then exited 0 anyway.
        var code = UpdateSecretCommand.Run(["update-secret", "--silent", "--merge", "--method"], _tempDir);

        code.Should().Be(1);
    }

    [Fact]
    public void Every_bad_option_is_reported_before_the_command_gives_up()
    {
        var stderr = CaptureStderr(() => UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge", "--method", "Pigeon", "--max-retries", "lots"],
            _tempDir).Should().Be(1));

        // Parsing deliberately continues past the first rejection: an operator fixing a scripted install
        // one message per run is the reason this is worth asserting. Each is reported by option name; the
        // rejected values themselves are never echoed.
        stderr.Should().Contain("--method");
        stderr.Should().Contain("--max-retries");
        stderr.Should().NotContain("Pigeon").And.NotContain("lots");
    }

    // ─── Invalid input is named, never echoed ─────────────────────────────────
    //
    // Requirement: an error or warning about an invalid value says which option failed and why, and never
    // prints the value. Asserted with a sentinel on every path that rejects a user-supplied value, across
    // stdout AND stderr, because a value that is only ever printed to the "other" stream still leaks.

    private const string Sentinel = "SENTINEL-9f3a7c";

    public static TheoryData<string[]> SentinelFailures => new()
    {
        // Rejected parser values (the shared ParseOrFail / ParseEnumOption / ParseMitigations messages).
        new[] { "--silent", "--merge", "--mode", Sentinel },
        new[] { "--silent", "--merge", "--dpapi-scope", Sentinel },
        new[] { "--silent", "--merge", "--method", Sentinel },
        new[] { "--silent", "--merge", "--mitigations", Sentinel },
        new[] { "--silent", "--merge", "--via-file", Sentinel },
        new[] { "--silent", "--merge", "--max-retries", Sentinel },
        // An unrecognised argument may be a misplaced value - a secret given without its option.
        new[] { "--silent", Sentinel },
        // Secret-source pointers: neither the variable name nor the file path is echoed.
        new[] { "--silent", "--secret-env", Sentinel, "--url", "https://jenkins:8443", "--mode", "Unprotected" },
        new[] { "--silent", "--secret-file", Path.Combine(Path.GetTempPath(), Sentinel + ".tmp"),
                "--url", "https://jenkins:8443", "--mode", "Unprotected" },
    };

    [Theory]
    [MemberData(nameof(SentinelFailures))]
    public void A_rejected_value_is_never_echoed(string[] options)
    {
        Environment.SetEnvironmentVariable(Sentinel, null);

        var (code, stdout, stderr) = CaptureBoth(() =>
            UpdateSecretCommand.Run([UpdateSecretCommand.Name, .. options], _tempDir));

        code.Should().Be(1);
        stderr.Should().NotBeEmpty("the failure must still be reported");
        stderr.Should().NotContain(Sentinel);
        stdout.Should().NotContain(Sentinel);
    }

    [Fact]
    public void The_secret_env_message_names_the_option_and_the_reason()
    {
        Environment.SetEnvironmentVariable(Sentinel, null);

        var (_, _, stderr) = CaptureBoth(() => UpdateSecretCommand.Run(
            [UpdateSecretCommand.Name, "--silent", "--secret-env", Sentinel,
             "--url", "https://jenkins:8443", "--mode", "Unprotected"], _tempDir));

        stderr.Should().Contain("--secret-env").And.Contain("not set or empty");
    }

    [Fact]
    public void Other_commands_do_not_echo_a_rejected_argument()
    {
        ConfigureRecoveryCommand.ParseArguments([ConfigureRecoveryCommand.Name, Sentinel]).Error
            .Should().NotBeNull().And.NotContain(Sentinel);
        GrantDataAccessCommand.ParseArguments([GrantDataAccessCommand.Name, "--account", "svc", Sentinel]).Error
            .Should().NotBeNull().And.NotContain(Sentinel);
    }

    // ─── Legacy level names are a hard failure on the command line ────────────

    [Theory]
    [InlineData("Full")]
    [InlineData("AllowNetworkImages")]
    [InlineData("Off")]
    public void A_legacy_level_name_on_the_command_line_is_rejected(string value)
    {
        // Migrated when READ from a stored config; a new command line (or msiexec JENKINS_MITIGATIONS) must
        // state the digits. Rejection fails the custom action instead of installing a guess.
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge", "--mitigations", value], _tempDir);

        code.Should().Be(1);
        File.Exists(Path.Combine(_tempDir, "appsettings.json")).Should().BeFalse();
    }

    // ─── --mitigations (and the other optional fields) on a full write ─────────

    [Fact]
    public void Silent_full_write_applies_mitigations()
    {
        // Used to be parsed and then silently dropped outside --merge.
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--secret", "s", "--url", "https://jenkins:8443",
             "--mode", "Unprotected", "--mitigations", "011"],
            _tempDir);

        code.Should().Be(0);
        ReadConfig().RootElement.GetProperty("Jenkins").GetProperty("Hardening")
            .GetProperty("ProcessMitigations").GetString().Should().Be("011");
    }

    [Fact]
    public void Silent_full_write_applies_the_other_optional_fields_too()
    {
        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--secret", "s", "--url", "https://jenkins:8443",
             "--mode", "Unprotected", "--method", "WebSocket", "--max-retries", "4", "--debug", "1"],
            _tempDir);

        code.Should().Be(0);
        var j = ReadConfig().RootElement.GetProperty("Jenkins");
        j.GetProperty("Connection").GetProperty("Method").GetString().Should().Be("WebSocket");
        j.GetProperty("Recovery").GetProperty("MaxRetries").GetInt32().Should().Be(4);
        j.GetProperty("Logging").GetProperty("DebugMode").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Silent_full_write_without_mitigations_keeps_the_existing_value()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            "{\"Jenkins\":{\"Hardening\":{\"ProcessMitigations\":\"101\"}}}");

        var code = UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--secret", "s", "--url", "https://jenkins:8443", "--mode", "Unprotected"],
            _tempDir);

        code.Should().Be(0);
        ReadConfig().RootElement.GetProperty("Jenkins").GetProperty("Hardening")
            .GetProperty("ProcessMitigations").GetString().Should().Be("101");
    }

    [Fact]
    public void A_value_that_reads_dash_dash_h_is_not_a_help_request()
    {
        // --custom-args consumes "--h": it is the value, not a request for usage. Before, WantsHelp scanned
        // every argument, so this printed usage and exited 0 without updating anything.
        UpdateSecretCommand.ParseArgs(["update-secret", "--merge", "--custom-args", "--h"])!
            .CustomArgs.Should().Be("--h");

        var (_, stdout, _) = CaptureBoth(() => UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge", "--custom-args", "--h"], _tempDir));

        stdout.Should().NotContain(UpdateSecretCommand.UsageText);
    }

    [Fact]
    public void An_alias_option_consumes_its_value_for_help_detection_too()
    {
        // --cu is the alias of --custom-args, resolved the same way the parser resolves it.
        var alias = UpdateSecretCommand.Aliases["--custom-args"];

        var (_, stdout, _) = CaptureBoth(() => UpdateSecretCommand.Run(
            ["update-secret", "--silent", "--merge", alias, "--h"], _tempDir));

        stdout.Should().NotContain(UpdateSecretCommand.UsageText);
    }

    private static (int Code, string Stdout, string Stderr) CaptureBoth(Func<int> action)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = action();
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
