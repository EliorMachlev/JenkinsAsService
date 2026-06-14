using System.Net;
using System.Security;
using System.Security.Cryptography;
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
    /// </summary>
    public static void WriteConfig(string basePath, string secret, SecretMode mode,
        string url, string? agentName, string? javaPath)
    {
        var configSecret = ProcessSecret(secret, mode);
        var configPath = Path.Combine(basePath, ConfigFileName);

        var existingJenkins = ReadExistingJenkinsSection(configPath);
        var jenkins = BuildJenkinsSection(configSecret, mode, url, agentName, javaPath, existingJenkins);

        var root = new JsonObject { [ConfigSectionName] = jenkins };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, root.ToJsonString(options), Encoding.UTF8);
    }

    private static string ProcessSecret(string secret, SecretMode mode) => mode switch
    {
        SecretMode.Dpapi => ProtectDpapi(secret),
        SecretMode.EnvironmentVariable => StoreEnvironmentVariable(secret),
        SecretMode.CredentialManager => StoreCredentialManager(secret),
        SecretMode.Unprotected => secret,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SecretMode.")
    };

    // Read existing config to preserve user-customized fields.
    private static JsonObject? ReadExistingJenkinsSection(string configPath)
    {
        if (!File.Exists(configPath))
            return null;

        try
        {
            var existing = JsonNode.Parse(File.ReadAllText(configPath));
            return existing?[ConfigSectionName]?.AsObject();
        }
        catch (JsonException)
        {
            // Corrupt file — overwrite entirely
            return null;
        }
    }

    private static JsonObject BuildJenkinsSection(string configSecret, SecretMode mode,
        string url, string? agentName, string? javaPath, JsonObject? existingJenkins) => new()
    {
        ["JenkinsURL"] = url,
        ["AgentSecret"] = configSecret,
        ["SecretMode"] = mode.ToString(),
        ["AgentName"] = agentName ?? existingJenkins?["AgentName"]?.GetValue<string>() ?? "",
        ["JavaPath"] = javaPath ?? existingJenkins?["JavaPath"]?.GetValue<string>() ?? "",
        ["CustomArguments"] = existingJenkins?["CustomArguments"]?.GetValue<string>() ?? "",
        ["DebugMode"] = existingJenkins?["DebugMode"]?.GetValue<bool>() ?? false,
        ["CompactLog"] = existingJenkins?["CompactLog"]?.GetValue<bool>() ?? false,
        ["MaxRetries"] = existingJenkins?["MaxRetries"]?.GetValue<int>() ?? 0
    };

    private static string ProtectDpapi(string secret)
    {
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(plainBytes, SecretResolver.DpapiEntropy, DataProtectionScope.LocalMachine);
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
