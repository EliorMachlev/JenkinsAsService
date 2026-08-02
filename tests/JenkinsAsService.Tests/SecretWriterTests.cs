// Copyright (c) 2024 All rights reserved
using System.Security.Cryptography; // NOSONAR — ProtectedData is from a NuGet package; standalone analysis can't resolve it
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class SecretWriterTests : IDisposable
{
    private const int TempDirSuffixLength = 8;
    private readonly string _tempDir;

    public SecretWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_Test_" + Guid.NewGuid().ToString("N")[..TempDirSuffixLength]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private JsonElement Jenkins() => ReadConfig().RootElement.GetProperty("Jenkins");

    [Fact]
    public void Dpapi_writes_base64_encrypted_secret()
    {
        SecretWriter.WriteConfig(_tempDir, "my-secret", SecretMode.Dpapi,
            "https://jenkins:8443", null, null);

        var agentSecret = Jenkins().GetProperty("Secret").GetProperty("Value").GetString()!;

        // Verify it's valid base64 and can be decrypted back
        var encrypted = Convert.FromBase64String(agentSecret);
        var decrypted = ProtectedData.Unprotect(encrypted, SecretResolver.DpapiEntropy, DataProtectionScope.LocalMachine);
        Encoding.UTF8.GetString(decrypted).Should().Be("my-secret");
    }

    [Fact]
    public void Dpapi_user_scope_writes_currentuser_encrypted_secret()
    {
        SecretWriter.WriteConfig(_tempDir, "user-secret", SecretMode.Dpapi,
            "https://jenkins:8443", null, null, DpapiScope.User);

        var secret = Jenkins().GetProperty("Secret");
        secret.GetProperty("DpapiScope").GetString().Should().Be("User");

        var encrypted = Convert.FromBase64String(secret.GetProperty("Value").GetString()!);
        var decrypted = ProtectedData.Unprotect(encrypted, SecretResolver.DpapiEntropy, DataProtectionScope.CurrentUser);
        Encoding.UTF8.GetString(decrypted).Should().Be("user-secret");
    }

    [Fact]
    public void Writes_dpapi_scope_and_controller_thumbprint_fields()
    {
        SecretWriter.WriteConfig(_tempDir, "secret", SecretMode.Unprotected,
            "https://jenkins:8443", null, null, DpapiScope.Machine, "AB:CD:EF");

        var jenkins = Jenkins();
        jenkins.GetProperty("Secret").GetProperty("DpapiScope").GetString().Should().Be("Machine");
        jenkins.GetProperty("Connection").GetProperty("ControllerCertThumbprint").GetString().Should().Be("AB:CD:EF");
    }

    [Fact]
    public void Unprotected_writes_plaintext_secret()
    {
        SecretWriter.WriteConfig(_tempDir, "plain-secret", SecretMode.Unprotected,
            "https://jenkins:8443", "my-agent", "/usr/lib/jvm");

        var jenkins = Jenkins();

        jenkins.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("plain-secret");
        jenkins.GetProperty("Secret").GetProperty("Mode").GetString().Should().Be("Unprotected");
        jenkins.GetProperty("Connection").GetProperty("Url").GetString().Should().Be("https://jenkins:8443");
        jenkins.GetProperty("Connection").GetProperty("AgentName").GetString().Should().Be("my-agent");
        jenkins.GetProperty("Agent").GetProperty("JavaPath").GetString().Should().Be("/usr/lib/jvm");
    }

    [Fact]
    public void Preserves_existing_config_fields()
    {
        // Seed a nested config with custom values in sub-sections the writer does not explicitly set.
        const string existingJson =
            "{\"Jenkins\":{" +
            "\"Connection\":{\"Url\":\"https://old:8443\",\"AgentName\":\"\"}," +
            "\"Secret\":{\"Value\":\"old-secret\",\"Mode\":\"Unprotected\"}," +
            "\"Agent\":{\"CustomArguments\":\"-noCertificateCheck\"}," +
            "\"Logging\":{\"DebugMode\":true,\"CompactLog\":true}," +
            "\"Recovery\":{\"MaxRetries\":5}}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        // Update secret — should preserve CustomArguments, DebugMode, CompactLog, MaxRetries
        SecretWriter.WriteConfig(_tempDir, "new-secret", SecretMode.Unprotected,
            "https://new:8443", null, null);

        var jenkins = Jenkins();

        jenkins.GetProperty("Connection").GetProperty("Url").GetString().Should().Be("https://new:8443");
        jenkins.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("new-secret");
        jenkins.GetProperty("Agent").GetProperty("CustomArguments").GetString().Should().Be("-noCertificateCheck");
        jenkins.GetProperty("Logging").GetProperty("DebugMode").GetBoolean().Should().BeTrue();
        jenkins.GetProperty("Logging").GetProperty("CompactLog").GetBoolean().Should().BeTrue();
        jenkins.GetProperty("Recovery").GetProperty("MaxRetries").GetInt32().Should().Be(5);
    }

    [Fact]
    public void Writes_default_connection_method_when_absent()
    {
        SecretWriter.WriteConfig(_tempDir, "secret", SecretMode.Unprotected,
            "https://jenkins:8443", null, null);

        Jenkins().GetProperty("Connection").GetProperty("Method").GetString().Should().Be("Auto");
    }

    [Fact]
    public void Preserves_existing_connection_method()
    {
        const string existingJson =
            "{\"Jenkins\":{\"Connection\":{\"Url\":\"https://old:8443\",\"Method\":\"WebSocket\"}," +
            "\"Secret\":{\"Value\":\"old\",\"Mode\":\"Unprotected\"}}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        SecretWriter.WriteConfig(_tempDir, "new", SecretMode.Unprotected, "https://new:8443", null, null);

        Jenkins().GetProperty("Connection").GetProperty("Method").GetString().Should().Be("WebSocket");
    }

    [Fact]
    public void Writes_correct_SecretMode_string()
    {
        SecretWriter.WriteConfig(_tempDir, "secret", SecretMode.Dpapi,
            "https://jenkins:8443", null, null);

        Jenkins().GetProperty("Secret").GetProperty("Mode").GetString().Should().Be("Dpapi");
    }

    [Fact]
    public void WriteConfig_preserves_existing_data_directory()
    {
        const string existingJson =
            "{\"Jenkins\":{\"Connection\":{\"Url\":\"https://old:8443\"}," +
            "\"Secret\":{\"Value\":\"old\",\"Mode\":\"Unprotected\"}," +
            "\"Agent\":{\"DataDirectory\":\"D:\\\\JenkinsData\"}}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        SecretWriter.WriteConfig(_tempDir, "new", SecretMode.Unprotected, "https://new:8443", null, null);

        Jenkins().GetProperty("Agent").GetProperty("DataDirectory").GetString().Should().Be(@"D:\JenkinsData");
    }

    [Fact]
    public void WriteConfig_survives_a_wrong_typed_existing_field()
    {
        // A hand-edited config where RetainedLogs is a string and CustomArguments is a number. WriteConfig
        // must fall back to defaults for the bad-typed fields rather than throwing and aborting the write.
        const string existingJson =
            "{\"Jenkins\":{\"Connection\":{\"Url\":\"https://old:8443\"}," +
            "\"Secret\":{\"Value\":\"old\",\"Mode\":\"Unprotected\"}," +
            "\"Agent\":{\"CustomArguments\":123}," +
            "\"Logging\":{\"RetainedLogs\":\"three\"}}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        var act = () => SecretWriter.WriteConfig(_tempDir, "new", SecretMode.Unprotected, "https://new:8443", null, null);

        act.Should().NotThrow();
        var jenkins = Jenkins();
        jenkins.GetProperty("Logging").GetProperty("RetainedLogs").GetInt32().Should().Be(3, "the bad value falls back to the default");
        jenkins.GetProperty("Agent").GetProperty("CustomArguments").GetString().Should().Be("");
        jenkins.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("new");
    }

    [Fact]
    public void WriteConfig_fresh_install_emits_every_schema_key()
    {
        // A fresh install has no existing file: the hand-built section must still be reconciled to the full
        // POCO schema, so keys the writer doesn't set and the whole Telemetry section are present at their
        // defaults — matching what an MSI upgrade would produce.
        SecretWriter.WriteConfig(_tempDir, "fresh-secret", SecretMode.Unprotected,
            "https://jenkins:8443", "agent-x", @"C:\jdk");

        var root = ReadConfig().RootElement;
        var jenkins = root.GetProperty("Jenkins");

        var hardening = jenkins.GetProperty("Hardening");
        hardening.GetProperty("SanitizeEnvironment").GetBoolean().Should().BeTrue("default is deny-by-default");
        jenkins.GetProperty("Secret").GetProperty("ViaFile").GetBoolean().Should().BeTrue("default keeps the secret off argv");

        root.TryGetProperty("Telemetry", out _).Should().BeTrue("the Telemetry section is part of the schema");

        // The reconcile must not disturb the values the writer set — especially the secret.
        jenkins.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("fresh-secret");
        jenkins.GetProperty("Connection").GetProperty("Url").GetString().Should().Be("https://jenkins:8443");
        jenkins.GetProperty("Connection").GetProperty("AgentName").GetString().Should().Be("agent-x");
    }

    [Fact]
    public void WriteConfig_fresh_install_output_binds_and_needs_no_further_reconcile()
    {
        // The fresh-install file should already be schema-complete: a follow-up NormalizeConfig must be a
        // no-op (nothing added, nothing removed), proving the writer and the upgrade path now agree.
        SecretWriter.WriteConfig(_tempDir, "secret", SecretMode.Unprotected, "https://jenkins:8443", null, null);

        var (added, removed) = SecretWriter.NormalizeConfig(_tempDir);

        added.Should().BeEmpty("fresh install already emitted every schema key");
        removed.Should().BeEmpty("fresh install emitted no keys outside the schema");
    }

    [Fact]
    public void SetDataDirectory_sets_field_and_preserves_other_fields_and_sections()
    {
        const string existingJson =
            "{\"Jenkins\":{\"Connection\":{\"Url\":\"https://j:8443\"}," +
            "\"Secret\":{\"Value\":\"keep\"},\"Agent\":{\"CustomArguments\":\"-x\"}}," +
            "\"Telemetry\":{\"Enabled\":true}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        SecretWriter.SetDataDirectory(_tempDir, @"C:\ProgramData\JenkinsAsService");

        var root = ReadConfig().RootElement;
        var jenkins = root.GetProperty("Jenkins");
        jenkins.GetProperty("Agent").GetProperty("DataDirectory").GetString().Should().Be(@"C:\ProgramData\JenkinsAsService");
        jenkins.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("keep", "the secret must be untouched");
        jenkins.GetProperty("Agent").GetProperty("CustomArguments").GetString().Should().Be("-x");
        root.GetProperty("Telemetry").GetProperty("Enabled").GetBoolean().Should().BeTrue("other sections survive");
    }

    [Fact]
    public void SetDataDirectory_creates_section_when_file_absent()
    {
        SecretWriter.SetDataDirectory(_tempDir, @"C:\Data");

        Jenkins().GetProperty("Agent").GetProperty("DataDirectory").GetString().Should().Be(@"C:\Data");
    }

    private JsonDocument ReadConfig()
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        File.Exists(path).Should().BeTrue("appsettings.json should have been created");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
