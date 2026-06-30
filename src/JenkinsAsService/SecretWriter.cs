// Copyright (c) 2024 All rights reserved

using System.Net;
using System.Security;
using System.Security.Cryptography; // NOSONAR — ProtectedData is from a NuGet package; standalone analysis can't resolve it
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdysTech.CredentialManager;

namespace JenkinsAsService;

public static class SecretWriter
{
    private const string EnvVarName = "JENKINS_AGENT_SECRET";
    private const string CredTargetName = "JenkinsAsService/AgentSecret";
    private const string CredUserName = "JenkinsAgent";
    private const string ConfigFileName = "appsettings.json";
    private const string ConfigSectionName = "Jenkins";

    /// <summary>
    /// Processes the secret based on mode, then writes appsettings.json.
    /// Preserves existing fields (CustomArguments, DebugMode, CompactLog, MaxRetries) if the file exists.
    /// When <paramref name="serviceAccount"/> is supplied, the written file's ACL is hardened so that
    /// only SYSTEM, Administrators and that account can read it (removes inherited Users access).
    /// </summary>
    public static void WriteConfig(string basePath, string secret, SecretMode mode,
        string server, string? agentName, string? javaPath,
        DpapiScope dpapiScope = DpapiScope.Machine, string? controllerCertThumbprint = null,
        string? serviceAccount = null)
    {
        var configSecret = ProcessSecret(secret, mode, dpapiScope, serviceAccount);
        var configPath = Path.Combine(basePath, ConfigFileName);

        var existingRoot = ReadExistingRoot(configPath);
        var existingJenkins = existingRoot?[ConfigSectionName]?.AsObject();
        var jenkins = BuildJenkinsSection(
            configSecret, mode, server, agentName, javaPath, dpapiScope, controllerCertThumbprint, existingJenkins);

        var root = new JsonObject();
        if (existingRoot != null)
        {
            foreach (var kvp in existingRoot)
            {
                if (kvp.Key != ConfigSectionName)
                {
                    root[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
        }

        root[ConfigSectionName] = jenkins;

        var options = new JsonSerializerOptions { WriteIndented = true };
        // basePath is always the trusted application base directory (AppContext.BaseDirectory);
        // no CLI option sets it and the filename is a constant, so there is no path-traversal vector.
        // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
        File.WriteAllText(configPath, root.ToJsonString(options), Encoding.UTF8);

        if (!string.IsNullOrWhiteSpace(serviceAccount))
        {
            ConfigAclHardener.Harden(configPath, serviceAccount);
        }
    }

    /// <summary>
    /// Surgically sets <c>Jenkins:DataDirectory</c> in appsettings.json, preserving every other field and
    /// top-level section. Used by the installer's second custom action (the value can't be appended to the
    /// main <c>WriteConfig</c> command without exceeding the MSI 255-char custom-action limit). No-op-safe:
    /// creates the section/file if absent.
    /// </summary>
    public static void SetDataDirectory(string basePath, string dataDirectory)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        var existingRoot = ReadExistingRoot(configPath);
        var existingJenkins = existingRoot?[ConfigSectionName]?.AsObject();

        var jenkins = existingJenkins is null
            ? new JsonObject()
            : (JsonObject)existingJenkins.DeepClone();

        if (jenkins["Agent"] is not JsonObject agent)
        {
            agent = new JsonObject();
            jenkins["Agent"] = agent;
        }
        agent["DataDirectory"] = dataDirectory;

        var root = new JsonObject();
        if (existingRoot != null)
        {
            foreach (var kvp in existingRoot)
            {
                if (kvp.Key != ConfigSectionName)
                {
                    root[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
        }

        root[ConfigSectionName] = jenkins;

        var options = new JsonSerializerOptions { WriteIndented = true };
        // basePath is the trusted application base directory; filename is a constant — no traversal vector.
        // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
        File.WriteAllText(configPath, root.ToJsonString(options), Encoding.UTF8);
    }

    private static string ProcessSecret(string secret, SecretMode mode, DpapiScope dpapiScope,
        string? serviceAccount) => mode switch
    {
        SecretMode.Dpapi => ProtectDpapi(secret, dpapiScope),
        SecretMode.EnvironmentVariable => StoreEnvironmentVariable(secret),
        SecretMode.CredentialManager => StoreCredentialManager(secret),
        SecretMode.Tpm => TpmSecretProtector.Protect(secret, serviceAccount),
        SecretMode.Unprotected => secret,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SecretMode.")
    };

    // Read the full existing config root to preserve user-customized fields
    // and any non-Jenkins top-level sections (e.g. Telemetry, Logging).
    private static JsonObject? ReadExistingRoot(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        }
        catch (JsonException)
        {
            // Corrupt file — overwrite entirely
            return null;
        }
    }

    // Builds the nested Jenkins section (Connection / Secret / Agent / Hardening / Logging / Recovery),
    // preserving any existing values in sub-sections this writer does not explicitly set.
    private static JsonObject BuildJenkinsSection(string configSecret, SecretMode mode,
        string server, string? agentName, string? javaPath, DpapiScope dpapiScope,
        string? controllerCertThumbprint, JsonObject? existingJenkins)
    {
        var conn = ExistingObject(existingJenkins, "Connection");
        var secret = ExistingObject(existingJenkins, "Secret");
        var agent = ExistingObject(existingJenkins, "Agent");
        var hardening = ExistingObject(existingJenkins, "Hardening");
        var logging = ExistingObject(existingJenkins, "Logging");
        var recovery = ExistingObject(existingJenkins, "Recovery");

        return new JsonObject
        {
            ["Connection"] = new JsonObject
            {
                ["Url"] = server,
                ["Method"] = ExistingMethod(conn),
                ["AgentName"] = agentName ?? ExistingString(conn, "AgentName"),
                ["ControllerCertThumbprint"] =
                    controllerCertThumbprint ?? ExistingString(conn, "ControllerCertThumbprint")
            },
            ["Secret"] = new JsonObject
            {
                ["Value"] = configSecret,
                ["Mode"] = mode.ToString(),
                ["DpapiScope"] = dpapiScope.ToString(),
                ["ViaFile"] = ExistingValue(secret, "ViaFile", true)
            },
            ["Agent"] = new JsonObject
            {
                ["JavaPath"] = javaPath ?? ExistingString(agent, "JavaPath"),
                ["CustomArguments"] = ExistingString(agent, "CustomArguments"),
                ["DataDirectory"] = ExistingString(agent, "DataDirectory")
            },
            ["Hardening"] = new JsonObject
            {
                ["SanitizeEnvironment"] = ExistingValue(hardening, "SanitizeEnvironment", true),
                ["AllowedEnvironmentVariables"] = ExistingString(hardening, "AllowedEnvironmentVariables")
            },
            ["Logging"] = new JsonObject
            {
                ["DebugMode"] = ExistingValue(logging, "DebugMode", false),
                ["CompactLog"] = ExistingValue(logging, "CompactLog", false),
                ["RetainedLogs"] = ExistingValue(logging, "RetainedLogs", 3)
            },
            ["Recovery"] = new JsonObject
            {
                ["MaxRetries"] = ExistingValue(recovery, "MaxRetries", 0)
            }
        };
    }

    private static JsonObject? ExistingObject(JsonObject? existing, string key) =>
        existing?[key] as JsonObject;

    // Preserve an existing Connection:Method, else default to Auto (so the enum binds to a valid value).
    private static string ExistingMethod(JsonObject? connection)
    {
        var value = ExistingString(connection, "Method");
        return string.IsNullOrEmpty(value) ? nameof(ConnectionMethod.Auto) : value;
    }

    private static string ExistingString(JsonObject? existing, string key) =>
        existing?[key]?.GetValue<string>() ?? "";

    private static T ExistingValue<T>(JsonObject? existing, string key, T fallback) where T : struct =>
        existing?[key]?.GetValue<T>() ?? fallback;

    private static string ProtectDpapi(string secret, DpapiScope scope)
    {
        var protectionScope = scope == DpapiScope.User
            ? DataProtectionScope.CurrentUser
            : DataProtectionScope.LocalMachine;
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(plainBytes, SecretResolver.DpapiEntropy, protectionScope);
        return Convert.ToBase64String(encrypted);
    }

    private static string StoreEnvironmentVariable(string secret)
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, secret, EnvironmentVariableTarget.Machine);
        }
        catch (SecurityException ex)
        {
            throw new InvalidOperationException(
                "Setting a machine-level environment variable requires administrator privileges.", ex);
        }

        return EnvVarName;
    }

    private static string StoreCredentialManager(string secret)
    {
        try
        {
            CredentialManager.SaveCredentials(CredTargetName, new NetworkCredential(CredUserName, secret));
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new InvalidOperationException(
                $"Failed to save credential to Windows Credential Manager target '{CredTargetName}'. " +
                "Ensure the process has sufficient privileges.", ex);
        }

        return CredTargetName;
    }
}
