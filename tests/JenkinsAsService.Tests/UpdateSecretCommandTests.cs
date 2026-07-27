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
    [InlineData("")]
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

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private JsonDocument ReadConfig()
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        path.Should().Match(p => File.Exists(p), "appsettings.json should have been written");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
