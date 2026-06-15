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
        jenkins.GetProperty("AgentSecret").GetString().Should().Be("my-secret");
        jenkins.GetProperty("JenkinsURL").GetString().Should().Be("https://jenkins:8443");
        jenkins.GetProperty("AgentName").GetString().Should().Be("build-agent-01");
        jenkins.GetProperty("SecretMode").GetString().Should().Be("Unprotected");
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
        json.RootElement.GetProperty("Jenkins").GetProperty("AgentSecret").GetString()
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
            json.RootElement.GetProperty("Jenkins").GetProperty("AgentSecret").GetString()
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

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private JsonDocument ReadConfig()
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        path.Should().Match(p => File.Exists(p), "appsettings.json should have been written");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
