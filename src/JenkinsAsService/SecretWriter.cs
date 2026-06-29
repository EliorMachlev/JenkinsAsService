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
        var configSecret = ProcessSecret(secret, mode, dpapiScope);
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

    private static string ProcessSecret(string secret, SecretMode mode, DpapiScope dpapiScope) => mode switch
    {
        SecretMode.Dpapi => ProtectDpapi(secret, dpapiScope),
        SecretMode.EnvironmentVariable => StoreEnvironmentVariable(secret),
        SecretMode.CredentialManager => StoreCredentialManager(secret),
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

    private static JsonObject BuildJenkinsSection(string configSecret, SecretMode mode,
        string server, string? agentName, string? javaPath, DpapiScope dpapiScope,
        string? controllerCertThumbprint, JsonObject? existingJenkins) => new()
    {
        ["JenkinsURL"] = server,
        ["AgentSecret"] = configSecret,
        ["SecretMode"] = mode.ToString(),
        ["DpapiScope"] = dpapiScope.ToString(),
        ["AgentName"] = agentName ?? ExistingString(existingJenkins, "AgentName"),
        ["JavaPath"] = javaPath ?? ExistingString(existingJenkins, "JavaPath"),
        ["CustomArguments"] = ExistingString(existingJenkins, "CustomArguments"),
        ["ControllerCertThumbprint"] =
            controllerCertThumbprint ?? ExistingString(existingJenkins, "ControllerCertThumbprint"),
        ["DebugMode"] = ExistingValue(existingJenkins, "DebugMode", false),
        ["CompactLog"] = ExistingValue(existingJenkins, "CompactLog", false),
        ["MaxRetries"] = ExistingValue(existingJenkins, "MaxRetries", 0)
    };

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
