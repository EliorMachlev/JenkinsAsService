// Copyright (c) 2024 All rights reserved

using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace JenkinsAsService.Tests;

/// <summary>
/// Verifies the nested <c>Jenkins</c> appsettings schema round-trips through <see cref="IConfiguration"/>
/// binding — the path production actually uses (<c>Configure&lt;ServiceSettings&gt;</c>) but which the
/// direct-construction unit tests never exercise, so an enum-name or sub-section typo would slip through.
/// </summary>
public class ConfigBindingTests
{
    private static ServiceSettings Bind(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = new ServiceSettings();
        config.GetSection(ConfigKeys.Section).Bind(settings);
        return settings;
    }

    [Fact]
    public void Full_nested_section_binds_every_sub_section_and_enum()
    {
        const string json = """
        {
          "Jenkins": {
            "Connection": { "Url": "https://ci.example.com:8443", "Method": "Https", "AgentName": "node-1", "ControllerCertThumbprint": "AABB", "Proxy": "proxy.corp:3128", "ProxyBypass": "*.corp.local" },
            "Secret": { "Value": "cipher", "Mode": "Tpm", "DpapiScope": "User", "ViaFile": false },
            "Agent": { "JavaPath": "C:/jdk/bin", "CustomArguments": "-noCertificateCheck", "DataDirectory": "D:/data" },
            "Hardening": { "SanitizeEnvironment": false, "AllowedEnvironmentVariables": "MAVEN_OPTS" },
            "Logging": { "DebugMode": true, "CompactLog": true, "RetainedLogs": 7 },
            "Recovery": { "MaxRetries": 5 }
          }
        }
        """;

        var s = Bind(json);

        s.Connection.Url.Should().Be("https://ci.example.com:8443");
        s.Connection.Method.Should().Be(ConnectionMethod.Https);
        s.Connection.AgentName.Should().Be("node-1");
        s.Connection.ControllerCertThumbprint.Should().Be("AABB");
        // Binding is by name and a typo is silent, so the proxy keys are asserted rather than assumed.
        s.Connection.Proxy.Should().Be("proxy.corp:3128");
        s.Connection.ProxyBypass.Should().Be("*.corp.local");
        s.Secret.Value.Should().Be("cipher");
        s.Secret.Mode.Should().Be(SecretMode.Tpm);
        s.Secret.DpapiScope.Should().Be(DpapiScope.User);
        s.Secret.ViaFile.Should().BeFalse();
        s.Agent.JavaPath.Should().Be("C:/jdk/bin");
        s.Agent.CustomArguments.Should().Be("-noCertificateCheck");
        s.Agent.DataDirectory.Should().Be("D:/data");
        s.Hardening.SanitizeEnvironment.Should().BeFalse();
        s.Hardening.AllowedEnvironmentVariables.Should().Be("MAVEN_OPTS");
        s.Logging.DebugMode.Should().BeTrue();
        s.Logging.CompactLog.Should().BeTrue();
        s.Logging.RetainedLogs.Should().Be(7);
        s.Recovery.MaxRetries.Should().Be(5);
    }

    [Fact]
    public void Blank_enum_and_bool_values_keep_the_poco_defaults()
    {
        // Mirrors the shipped appsettings.json where the enum/bool fields are blank strings or omitted.
        const string json = """
        {
          "Jenkins": {
            "Connection": { "Url": "https://ci.example.com:8443" },
            "Secret": { "Value": "s" }
          }
        }
        """;

        var s = Bind(json);

        s.Connection.Method.Should().Be(ConnectionMethod.Auto, "Auto is the POCO default when Method is absent");
        s.Secret.Mode.Should().Be(SecretMode.Unprotected);
        s.Secret.DpapiScope.Should().Be(DpapiScope.Machine);
        s.Secret.ViaFile.Should().BeTrue();
        s.Connection.Proxy.Should().BeEmpty("an absent proxy means 'inherit the system proxy'");
        s.Connection.ProxyBypass.Should().BeEmpty();
        s.Logging.RetainedLogs.Should().Be(3);
        s.Recovery.MaxRetries.Should().Be(0);
    }

    [Fact]
    public void Shipped_appsettings_json_binds_without_error()
    {
        var shippedPath = FindShippedAppSettings();
        shippedPath.Should().NotBeNull("the shipped src/JenkinsAsService/appsettings.json must be locatable from the test run");

        var config = new ConfigurationBuilder()
            .AddJsonFile(shippedPath!, optional: false)
            .Build();

        var settings = new ServiceSettings();
        var act = () => config.GetSection(ConfigKeys.Section).Bind(settings);

        act.Should().NotThrow("the shipped schema must bind cleanly to ServiceSettings");
        settings.Connection.Method.Should().Be(ConnectionMethod.Auto);
        settings.Secret.Mode.Should().Be(SecretMode.Unprotected);
    }

    // Walk up from the test binary to the repo, then to the source project's appsettings.json.
    private static string? FindShippedAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JenkinsAsService", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void Generated_full_config_binds_to_ServiceSettings_with_all_enums()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "JAS_Bind_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Simulate the installer: WriteConfig then the merge CAs.
            SecretWriter.WriteConfig(tempDir, "secret", SecretMode.Dpapi, "https://jenkins:8443", "agent-x", @"C:\jdk");
            SecretWriter.MergeConfig(tempDir, new ConfigMergeFields
            {
                Method = ConnectionMethod.WebSocket,
                DpapiScope = DpapiScope.User,
                ViaFile = false,
                CustomArguments = "-noCertificateCheck",
                SanitizeEnvironment = false,
                AllowedEnvironmentVariables = "MAVEN_OPTS",
                DebugMode = true,
                CompactLog = true,
                RetainedLogs = 9,
                MaxRetries = 6
            });

            var config = new ConfigurationBuilder()
                .SetBasePath(tempDir)
                .AddJsonFile("appsettings.json")
                .Build();
            var settings = config.GetSection("Jenkins").Get<ServiceSettings>()!;

            settings.Connection.Method.Should().Be(ConnectionMethod.WebSocket);
            settings.Connection.AgentName.Should().Be("agent-x");
            settings.Secret.Mode.Should().Be(SecretMode.Dpapi);
            settings.Secret.DpapiScope.Should().Be(DpapiScope.User);
            settings.Secret.ViaFile.Should().BeFalse();
            settings.Agent.CustomArguments.Should().Be("-noCertificateCheck");
            settings.Hardening.SanitizeEnvironment.Should().BeFalse();
            settings.Hardening.AllowedEnvironmentVariables.Should().Be("MAVEN_OPTS");
            settings.Logging.DebugMode.Should().BeTrue();
            settings.Logging.CompactLog.Should().BeTrue();
            settings.Logging.RetainedLogs.Should().Be(9);
            settings.Recovery.MaxRetries.Should().Be(6);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
