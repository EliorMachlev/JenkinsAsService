// Copyright (c) 2024 All rights reserved
using System.Text.Json;
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// SecretWriter builds the config path with Path.Join instead of Path.Combine (Combine drops everything
/// before a rooted later segment; Join never does). These pin that the switch changed nothing for the
/// inputs that actually occur: the base directory with or without a trailing separator, and empty.
/// </summary>
public class SecretWriterConfigPathTests : IDisposable
{
    private readonly string _tempDir;

    public SecretWriterConfigPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_CfgPath_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(@"C:\Program Files\JenkinsAsService")]
    [InlineData(@"C:\Program Files\JenkinsAsService\")]
    [InlineData("C:/Program Files/JenkinsAsService/")]
    [InlineData(@"\\server\share\JenkinsAsService\")]
    [InlineData("relative")]
    [InlineData("")]
    public void ConfigPath_matches_what_Path_Combine_produced(string basePath)
    {
        SecretWriter.ConfigPath(basePath).Should().Be(Path.Combine(basePath, ConfigKeys.FileName));
    }

    [Fact]
    public void ConfigPath_of_the_application_base_directory_is_unchanged()
    {
        // AppContext.BaseDirectory, the only base production passes, always ends in a separator.
        var basePath = AppContext.BaseDirectory;

        SecretWriter.ConfigPath(basePath).Should().Be(Path.Combine(basePath, ConfigKeys.FileName));
    }

    [Fact]
    public void ConfigPath_rejects_null_as_Path_Combine_did()
    {
        var act = () => SecretWriter.ConfigPath(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    public static TheoryData<bool> TrailingSeparator => new() { false, true };

    private string Base(bool trailingSeparator) =>
        trailingSeparator ? _tempDir + Path.DirectorySeparatorChar : _tempDir;

    private string ExpectedConfig => Path.Combine(_tempDir, ConfigKeys.FileName);

    private JsonElement Jenkins() =>
        JsonDocument.Parse(File.ReadAllText(ExpectedConfig)).RootElement.GetProperty(ConfigKeys.Section);

    [Theory]
    [MemberData(nameof(TrailingSeparator))]
    public void WriteConfig_writes_the_config_in_the_base_directory(bool trailingSeparator)
    {
        SecretWriter.WriteConfig(Base(trailingSeparator), "s3cret", SecretMode.Unprotected,
            "https://ci.example.com:8443/", "agent-1", javaPath: null);

        File.Exists(ExpectedConfig).Should().BeTrue();
        Directory.GetFiles(_tempDir).Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(TrailingSeparator))]
    public void SetDataDirectory_and_MergeConfig_update_the_same_file(bool trailingSeparator)
    {
        SecretWriter.SetDataDirectory(Base(trailingSeparator), @"D:\JenkinsData");
        SecretWriter.MergeConfig(Base(trailingSeparator), new ConfigMergeFields { MaxRetries = 3 });

        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(ExpectedConfig);
        Jenkins().GetProperty(ConfigKeys.Agent.Name).GetProperty(ConfigKeys.Agent.DataDirectory).GetString()
            .Should().Be(@"D:\JenkinsData");
        Jenkins().GetProperty(ConfigKeys.Recovery.Name).GetProperty(ConfigKeys.Recovery.MaxRetries).GetInt32()
            .Should().Be(3);
    }

    [Theory]
    [MemberData(nameof(TrailingSeparator))]
    public void NormalizeConfig_reconciles_the_config_in_the_base_directory(bool trailingSeparator)
    {
        File.WriteAllText(ExpectedConfig, """{"Jenkins":{"Secret":{"Value":"keep"}}}""");

        var (added, _) = SecretWriter.NormalizeConfig(Base(trailingSeparator));

        added.Should().NotBeEmpty();
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(ExpectedConfig);
        Jenkins().GetProperty(ConfigKeys.Secret.Name).GetProperty(ConfigKeys.Secret.Value).GetString()
            .Should().Be("keep");
    }
}
