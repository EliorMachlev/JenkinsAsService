// Copyright (c) 2024 All rights reserved
using System.Text.Json;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class SecretWriterNormalizeTests : IDisposable
{
    private readonly string _tempDir;
    private string ConfigPath => Path.Combine(_tempDir, "appsettings.json");

    public SecretWriterNormalizeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_Norm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private JsonElement Jenkins() =>
        JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement.GetProperty("Jenkins");

    [Fact]
    public void Adds_missing_keys_and_preserves_secret()
    {
        File.WriteAllText(ConfigPath, """{"Jenkins":{"Secret":{"Value":"keep","Mode":"Tpm"}}}""");

        var (added, _) = SecretWriter.NormalizeConfig(_tempDir);

        added.Should().NotBeEmpty();
        Jenkins().GetProperty("Secret").GetProperty("Value").GetString().Should().Be("keep");
        Jenkins().GetProperty("Recovery").GetProperty("MaxRetries").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Prunes_unknown_keys()
    {
        File.WriteAllText(ConfigPath,
            """{"Jenkins":{"Secret":{"Value":"s","LegacyFlag":true}},"Serilog":{"X":1}}""");

        var (_, removed) = SecretWriter.NormalizeConfig(_tempDir);

        removed.Should().Contain("Serilog").And.Contain("Jenkins:Secret:LegacyFlag");
        var root = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement;
        root.TryGetProperty("Serilog", out _).Should().BeFalse();
        Jenkins().GetProperty("Secret").TryGetProperty("LegacyFlag", out _).Should().BeFalse();
    }

    [Fact]
    public void No_change_leaves_file_untouched()
    {
        // First normalize brings the file to schema; the second must not rewrite it.
        File.WriteAllText(ConfigPath, """{"Jenkins":{"Secret":{"Value":"s"}}}""");
        SecretWriter.NormalizeConfig(_tempDir);
        var afterFirst = File.ReadAllText(ConfigPath);

        var (added, removed) = SecretWriter.NormalizeConfig(_tempDir);

        added.Should().BeEmpty();
        removed.Should().BeEmpty();
        File.ReadAllText(ConfigPath).Should().Be(afterFirst, "an unchanged reconcile must not rewrite the file");
    }
}
