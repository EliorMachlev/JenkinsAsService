using System.Security.Cryptography;
using System.Text;
using AdysTech.CredentialManager;

namespace JenkinsAsService;

public sealed class SecretResolver : ISecretResolver
{
    public string Resolve(ServiceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentSecret))
            throw new InvalidOperationException("'AgentSecret' is empty — nothing to resolve.");

        return settings.SecretMode switch
        {
            SecretMode.Unprotected => settings.AgentSecret,
            SecretMode.Dpapi => ResolveDpapi(settings.AgentSecret),
            SecretMode.EnvironmentVariable => ResolveEnvironmentVariable(settings.AgentSecret),
            SecretMode.CredentialManager => ResolveCredentialManager(settings.AgentSecret),
            _ => throw new InvalidOperationException($"Unknown SecretMode: '{settings.SecretMode}'.")
        };
    }

    private static string ResolveDpapi(string base64Cipher)
    {
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(base64Cipher);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "AgentSecret is not valid Base64. Re-run the installer with DPAPI mode to re-encrypt.", ex);
        }

        try
        {
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "DPAPI decryption failed. The secret was encrypted on a different machine or the key has been rotated.", ex);
        }
    }

    private static string ResolveEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"System environment variable '{variableName}' not found or empty. " +
                "Create it with the agent secret value, or change SecretMode.");
        return value;
    }

    private static string ResolveCredentialManager(string targetName)
    {
        var credential = CredentialManager.GetCredentials(targetName);
        if (credential?.Password is null or "")
            throw new InvalidOperationException(
                $"Windows Credential Manager entry '{targetName}' not found or has no password. " +
                "Re-run the installer with Credential Manager mode, or add it manually via cmdkey.");
        return credential.Password;
    }
}
