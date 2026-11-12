// Copyright (c) 2024 All rights reserved
using System.Text.Json;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class SecretWriterMergeTests : IDisposable
{
    private readonly string _tempDir;

    public SecretWriterMergeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_Merge_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private JsonElement Jenkins() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "appsettings.json")))
            .RootElement.GetProperty("Jenkins");

    [Fact]
    public void Merge_sets_all_provided_fields_in_correct_subsections()
    {
        SecretWriter.MergeConfig(_tempDir, new ConfigMergeFields
        {
            Method = ConnectionMethod.WebSocket,
            DpapiScope = DpapiScope.User,
            ViaFile = false,
            CustomArguments = "-noCertificateCheck",
            SanitizeEnvironment = false,
            AllowedEnvironmentVariables = "MAVEN_OPTS;GRADLE_USER_HOME",
            ProcessMitigations = new MitigationMask(false, false, false),
            DebugMode = true,
            CompactLog = true,
            RetainedLogs = 7,
            MaxRetries = 5
        });

        var j = Jenkins();
        j.GetProperty("Connection").GetProperty("Method").GetString().Should().Be("WebSocket");
        j.GetProperty("Secret").GetProperty("DpapiScope").GetString().Should().Be("User");
        j.GetProperty("Secret").GetProperty("ViaFile").GetBoolean().Should().BeFalse();
        j.GetProperty("Agent").GetProperty("CustomArguments").GetString().Should().Be("-noCertificateCheck");
        j.GetProperty("Hardening").GetProperty("SanitizeEnvironment").GetBoolean().Should().BeFalse();
        j.GetProperty("Hardening").GetProperty("AllowedEnvironmentVariables").GetString().Should().Be("MAVEN_OPTS;GRADLE_USER_HOME");
        // Written as the three-digit string (no-remote, no-low-label, prefer-System32), which is exactly
        // what MitigationMask.TryParse reads back - not a level name, and not a number the binder would coerce.
        j.GetProperty("Hardening").GetProperty("ProcessMitigations").GetString().Should().Be("000");
        j.GetProperty("Logging").GetProperty("DebugMode").GetBoolean().Should().BeTrue();
        j.GetProperty("Logging").GetProperty("CompactLog").GetBoolean().Should().BeTrue();
        j.GetProperty("Logging").GetProperty("RetainedLogs").GetInt32().Should().Be(7);
        j.GetProperty("Recovery").GetProperty("MaxRetries").GetInt32().Should().Be(5);
    }

    [Fact]
    public void Merge_preserves_secret_and_unset_fields()
    {
        const string existingJson =
            "{\"Jenkins\":{" +
            "\"Connection\":{\"Url\":\"https://j:8443\",\"Method\":\"Auto\"}," +
            "\"Secret\":{\"Value\":\"keep-me\",\"Mode\":\"Dpapi\"}," +
            "\"Agent\":{\"JavaPath\":\"C:\\\\jdk\"}}," +
            "\"Telemetry\":{\"Enabled\":true}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        // Only change one field.
        SecretWriter.MergeConfig(_tempDir, new ConfigMergeFields { MaxRetries = 3 });

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "appsettings.json"))).RootElement;
        var j = root.GetProperty("Jenkins");
        j.GetProperty("Recovery").GetProperty("MaxRetries").GetInt32().Should().Be(3);
        j.GetProperty("Secret").GetProperty("Value").GetString().Should().Be("keep-me", "the secret must be untouched");
        j.GetProperty("Secret").GetProperty("Mode").GetString().Should().Be("Dpapi");
        j.GetProperty("Connection").GetProperty("Url").GetString().Should().Be("https://j:8443");
        j.GetProperty("Agent").GetProperty("JavaPath").GetString().Should().Be(@"C:\jdk");
        root.GetProperty("Telemetry").GetProperty("Enabled").GetBoolean().Should().BeTrue("other sections survive");
    }

    [Fact]
    public void Merge_on_missing_file_creates_valid_section()
    {
        SecretWriter.MergeConfig(_tempDir, new ConfigMergeFields { Method = ConnectionMethod.Https });

        Jenkins().GetProperty("Connection").GetProperty("Method").GetString().Should().Be("Https");
    }

    // A full write over a config from the build that stored level names migrates the name to the digits
    // with the same effect - through the same helper the service uses at startup - instead of resetting it.
    [Theory]
    [InlineData("Full", "111")]
    [InlineData("AllowNetworkImages", "011")]
    [InlineData("Off", "000")]
    [InlineData("101", "101")]
    [InlineData("garbage", "111")]
    public void WriteConfig_migrates_a_legacy_mitigation_level(string stored, string expected)
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            $"{{\"Jenkins\":{{\"Hardening\":{{\"ProcessMitigations\":\"{stored}\"}}}}}}");

        SecretWriter.WriteConfig(_tempDir, "s", SecretMode.Unprotected, "https://j:8443", null, null);

        Jenkins().GetProperty("Hardening").GetProperty("ProcessMitigations").GetString().Should().Be(expected);
        ProcessMitigations.Resolve(stored).ToString().Should().Be(expected, "runtime and writer must agree");
    }

    [Fact]
    public void WriteConfig_applies_a_supplied_mitigation_mask_over_the_stored_one()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            "{\"Jenkins\":{\"Hardening\":{\"ProcessMitigations\":\"111\"}}}");

        SecretWriter.WriteConfig(_tempDir, "s", SecretMode.Unprotected, "https://j:8443", null, null,
            optionalFields: new ConfigMergeFields { ProcessMitigations = new MitigationMask(false, true, true) });

        Jenkins().GetProperty("Hardening").GetProperty("ProcessMitigations").GetString().Should().Be("011");
    }

    [Fact]
    public void WriteConfig_ignores_the_optional_dpapi_scope_in_favour_of_the_one_it_protected_with()
    {
        SecretWriter.WriteConfig(_tempDir, "s", SecretMode.Unprotected, "https://j:8443", null, null,
            DpapiScope.Machine, optionalFields: new ConfigMergeFields { DpapiScope = DpapiScope.User });

        Jenkins().GetProperty("Secret").GetProperty("DpapiScope").GetString().Should().Be("Machine");
    }

    [Fact]
    public void Merge_with_all_null_fields_is_a_noop_write()
    {
        const string existingJson = "{\"Jenkins\":{\"Secret\":{\"Value\":\"s\"}}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        SecretWriter.MergeConfig(_tempDir, new ConfigMergeFields());

        Jenkins().GetProperty("Secret").GetProperty("Value").GetString().Should().Be("s");
    }
}
