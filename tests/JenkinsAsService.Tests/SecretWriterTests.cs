// Copyright (c) 2024 All rights reserved // NOSONAR
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

    [Fact]
    public void Dpapi_writes_base64_encrypted_secret()
    {
        SecretWriter.WriteConfig(_tempDir, "my-secret", SecretMode.Dpapi,
            "https://jenkins:8443", null, null);

        var json = ReadConfig();
        var agentSecret = json.RootElement.GetProperty("Jenkins").GetProperty("AgentSecret").GetString()!;

        // Verify it's valid base64 and can be decrypted back
        var encrypted = Convert.FromBase64String(agentSecret);
        var decrypted = ProtectedData.Unprotect(encrypted, SecretResolver.DpapiEntropy, DataProtectionScope.LocalMachine);
        Encoding.UTF8.GetString(decrypted).Should().Be("my-secret");
    }

    [Fact]
    public void Unprotected_writes_plaintext_secret()
    {
        SecretWriter.WriteConfig(_tempDir, "plain-secret", SecretMode.Unprotected,
            "https://jenkins:8443", "my-agent", "/usr/lib/jvm");

        var json = ReadConfig();
        var jenkins = json.RootElement.GetProperty("Jenkins");

        jenkins.GetProperty("AgentSecret").GetString().Should().Be("plain-secret");
        jenkins.GetProperty("SecretMode").GetString().Should().Be("Unprotected");
        jenkins.GetProperty("JenkinsURL").GetString().Should().Be("https://jenkins:8443");
        jenkins.GetProperty("AgentName").GetString().Should().Be("my-agent");
        jenkins.GetProperty("JavaPath").GetString().Should().Be("/usr/lib/jvm");
    }

    [Fact]
    public void Preserves_existing_config_fields()
    {
        // Write initial config with custom values
        const string existingJson =
            "{\"Jenkins\":{\"JenkinsURL\":\"https://old:8443\",\"AgentSecret\":\"old-secret\"," +
            "\"SecretMode\":\"Unprotected\",\"AgentName\":\"\",\"JavaPath\":\"\"," +
            "\"CustomArguments\":\"-noCertificateCheck\",\"DebugMode\":true," +
            "\"CompactLog\":true,\"MaxRetries\":5}}";
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), existingJson);

        // Update secret — should preserve CustomArguments, DebugMode, CompactLog, MaxRetries
        SecretWriter.WriteConfig(_tempDir, "new-secret", SecretMode.Unprotected,
            "https://new:8443", null, null);

        var json = ReadConfig();
        var jenkins = json.RootElement.GetProperty("Jenkins");

        jenkins.GetProperty("JenkinsURL").GetString().Should().Be("https://new:8443");
        jenkins.GetProperty("AgentSecret").GetString().Should().Be("new-secret");
        jenkins.GetProperty("CustomArguments").GetString().Should().Be("-noCertificateCheck");
        jenkins.GetProperty("DebugMode").GetBoolean().Should().BeTrue();
        jenkins.GetProperty("CompactLog").GetBoolean().Should().BeTrue();
        jenkins.GetProperty("MaxRetries").GetInt32().Should().Be(5);
    }

    [Fact]
    public void Writes_correct_SecretMode_string()
    {
        SecretWriter.WriteConfig(_tempDir, "secret", SecretMode.Dpapi,
            "https://jenkins:8443", null, null);

        var json = ReadConfig();
        json.RootElement.GetProperty("Jenkins").GetProperty("SecretMode").GetString().Should().Be("Dpapi");
    }

    private JsonDocument ReadConfig()
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        File.Exists(path).Should().BeTrue("appsettings.json should have been created");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
