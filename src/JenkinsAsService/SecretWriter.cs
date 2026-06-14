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

    /// <summary>
    /// Processes the secret based on mode, then writes appsettings.json.
    /// Preserves existing fields (CustomArguments, DebugMode, CompactLog, MaxRetries) if the file exists.
    /// </summary>
    public static void WriteConfig(string basePath, string secret, SecretMode mode,
        string url, string? agentName, string? javaPath)
    {
        var configSecret = mode switch
        {
            SecretMode.Dpapi => ProtectDpapi(secret),
            SecretMode.EnvironmentVariable => StoreEnvironmentVariable(secret),
            SecretMode.CredentialManager => StoreCredentialManager(secret),
            SecretMode.Unprotected => secret,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SecretMode.")
        };

        var configPath = Path.Combine(basePath, "appsettings.json");

        // Read existing config to preserve user-customized fields
        JsonObject? existingJenkins = null;
        if (File.Exists(configPath))
        {
            try
            {
                var existing = JsonNode.Parse(File.ReadAllText(configPath));
                existingJenkins = existing?["Jenkins"]?.AsObject();
            }
            catch (JsonException)
            {
                // Corrupt file — overwrite entirely
            }
        }

        var jenkins = new JsonObject
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

        var root = new JsonObject { ["Jenkins"] = jenkins };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, root.ToJsonString(options), Encoding.UTF8);
    }

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
            CredentialManager.SaveCredentials(CredTargetName, new NetworkCredential("JenkinsAgent", secret));
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
