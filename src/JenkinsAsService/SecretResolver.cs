// Copyright (c) 2024 All rights reserved

using System.Security.Cryptography;
using System.Text;
using AdysTech.CredentialManager;

namespace JenkinsAsService;

public sealed class SecretResolver : ISecretResolver
{
    // Shared entropy for DPAPI — must match SecretWriter.ProtectDpapi (write side, same assembly)
    internal static readonly byte[] DpapiEntropy =
        [0x4A, 0x65, 0x6E, 0x6B, 0x69, 0x6E, 0x73, 0x41, 0x73, 0x53, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65];

    public string Resolve(ServiceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentSecret))
        {
            throw new InvalidOperationException("'AgentSecret' is empty — nothing to resolve.");
        }

        return settings.SecretMode switch
        {
            SecretMode.Unprotected => settings.AgentSecret,
            SecretMode.Dpapi => ResolveDpapi(settings.AgentSecret, settings.DpapiScope),
            SecretMode.EnvironmentVariable => ResolveEnvironmentVariable(settings.AgentSecret),
            SecretMode.CredentialManager => ResolveCredentialManager(settings.AgentSecret),
            SecretMode.Tpm => TpmSecretProtector.Resolve(settings.AgentSecret),
            _ => throw new InvalidOperationException($"Unknown SecretMode: '{settings.SecretMode}'.")
        };
    }

    private static string ResolveDpapi(string base64Cipher, DpapiScope scope)
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

        var protectionScope = scope == DpapiScope.User
            ? DataProtectionScope.CurrentUser
            : DataProtectionScope.LocalMachine;

        try
        {
            var plain = ProtectedData.Unprotect(encrypted, DpapiEntropy, protectionScope);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            var hint = scope == DpapiScope.User
                ? "The secret must be encrypted by the same identity that runs the service (User-scope DPAPI). " +
                  "Re-run 'update-secret --impersonate' as the service account."
                : "The secret was encrypted on a different machine or the key has been rotated.";
            throw new InvalidOperationException($"DPAPI decryption failed. {hint}", ex);
        }
    }

    private static string ResolveEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"System environment variable '{variableName}' not found or empty. " +
                "Create it with the agent secret value, or change SecretMode.");
        }

        return value;
    }

    private static string ResolveCredentialManager(string targetName)
    {
        var credential = CredentialManager.GetCredentials(targetName);
        if (string.IsNullOrEmpty(credential?.Password))
        {
            throw new InvalidOperationException(
                $"Windows Credential Manager entry '{targetName}' not found or has no password. " +
                "Re-run the installer with Credential Manager mode, or add it manually via cmdkey.");
        }

        return credential.Password;
    }
}
