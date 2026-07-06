// Copyright (c) 2024 All rights reserved

using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace JenkinsAsService;

/// <summary>
/// Protects the agent secret with a TPM-resident RSA key via the Microsoft Platform Crypto Provider.
/// Unlike DPAPI — whose key is software-derivable by any local admin/SYSTEM — the private key never
/// leaves the TPM: it is non-exportable and bound to this physical machine, so the ciphertext can only
/// be decrypted on the same box. The key is created at machine scope by the elevated installer, and the
/// (low-privilege) service account is granted use-rights on it so the running service can decrypt.
/// </summary>
internal static class TpmSecretProtector
{
    internal const string ProviderName = "Microsoft Platform Crypto Provider";
    internal const string KeyName = "JenkinsAsService-AgentSecret";
    private const int KeyLengthBits = 2048;

    // NCRYPT_SECURITY_DESCR_PROPERTY + DACL_SECURITY_INFORMATION (0x4), passed through to NCryptSetProperty.
    private const string SecurityDescriptorProperty = "Security Descr";
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)0x4;

    // Generic access rights; NCrypt maps these onto key objects (read ⇒ load + use the key).
    private const int GenericRead = unchecked((int)0x80000000);
    private const int GenericAll = 0x10000000;

    private static CngProvider Provider => new(ProviderName);

    /// <summary>Encrypts <paramref name="secret"/> with the machine-scoped TPM key, granting
    /// <paramref name="serviceAccount"/> (if given) use-rights so the service can later decrypt.</summary>
    internal static string Protect(string secret, string? serviceAccount) =>
        Protect(secret, serviceAccount, machineKey: true);

    /// <summary>Decrypts a base64 ciphertext produced by <see cref="Protect(string, string?)"/> using the
    /// machine-scoped TPM key.</summary>
    internal static string Resolve(string base64Cipher) =>
        Resolve(base64Cipher, machineKey: true);

    // machineKey is overridable so tests can round-trip with a user-scoped key (no elevation required);
    // production always uses machine-scoped keys created by the elevated installer.
    internal static string Protect(string secret, string? serviceAccount, bool machineKey)
    {
        try
        {
            using var key = OpenOrCreateKey(machineKey);
            if (machineKey && !string.IsNullOrWhiteSpace(serviceAccount))
            {
                GrantKeyAccess(key, serviceAccount);
            }

            using var rsa = new RSACng(key);
            var cipher = rsa.Encrypt(Encoding.UTF8.GetBytes(secret), RSAEncryptionPadding.OaepSHA256);
            return Convert.ToBase64String(cipher);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "TPM protection failed. Ensure a TPM 2.0 is present and enabled, and run this elevated — " +
                "machine-scoped TPM keys require administrator privileges.", ex);
        }
    }

    internal static string Resolve(string base64Cipher, bool machineKey)
    {
        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(base64Cipher);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "AgentSecret is not valid Base64. Re-run update-secret with --mode Tpm to re-encrypt.", ex);
        }

        var openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (!CngKey.Exists(KeyName, Provider, openOptions))
        {
            throw new InvalidOperationException(
                "TPM key not found. The secret was protected on a different machine, or the TPM was " +
                "cleared/reset. Re-run update-secret with --mode Tpm on this machine.");
        }

        try
        {
            using var key = CngKey.Open(KeyName, Provider, openOptions);
            using var rsa = new RSACng(key);
            return Encoding.UTF8.GetString(rsa.Decrypt(cipher, RSAEncryptionPadding.OaepSHA256));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "TPM decryption failed. The service account may lack use-rights on the TPM key, or the " +
                "ciphertext is corrupt. Re-run update-secret with --mode Tpm and --service-account.", ex);
        }
    }

    /// <summary>True when the Platform Crypto Provider is present (a usable TPM). No elevation required.</summary>
    internal static bool IsAvailable()
    {
        try
        {
            // Existence probe for a never-created user key: returns false when the provider is present,
            // throws only when the provider/TPM is absent.
            CngKey.Exists(KeyName + "-probe", Provider, CngKeyOpenOptions.None);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    // Test-only: removes the persisted key so round-trip tests don't leave artifacts in the TPM store.
    internal static void DeleteKey(bool machineKey)
    {
        var openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (CngKey.Exists(KeyName, Provider, openOptions))
        {
            using var key = CngKey.Open(KeyName, Provider, openOptions);
            key.Delete();
        }
    }

    private static CngKey OpenOrCreateKey(bool machineKey)
    {
        var openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (CngKey.Exists(KeyName, Provider, openOptions))
        {
            return CngKey.Open(KeyName, Provider, openOptions);
        }

        var creationParams = new CngKeyCreationParameters
        {
            Provider = Provider,
            KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
            ExportPolicy = CngExportPolicies.None, // non-exportable: the private key stays inside the TPM
        };
        creationParams.Parameters.Add(
            new CngProperty("Length", BitConverter.GetBytes(KeyLengthBits), CngPropertyOptions.None));

        return CngKey.Create(CngAlgorithm.Rsa, KeyName, creationParams);
    }

    // Replaces the key's DACL: SYSTEM + Administrators full control, the service account read/use only.
    private static void GrantKeyAccess(CngKey key, string serviceAccount)
    {
        var account = ResolveAccountSid(serviceAccount);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var dacl = new DiscretionaryAcl(isContainer: false, isDS: false, capacity: 3);
        dacl.AddAccess(AccessControlType.Allow, system, GenericAll, InheritanceFlags.None, PropagationFlags.None);
        dacl.AddAccess(AccessControlType.Allow, admins, GenericAll, InheritanceFlags.None, PropagationFlags.None);
        dacl.AddAccess(AccessControlType.Allow, account, GenericRead, InheritanceFlags.None, PropagationFlags.None);

        var sd = new CommonSecurityDescriptor(
            isContainer: false, isDS: false, ControlFlags.DiscretionaryAclPresent, admins, null, null, dacl);
        var sdBytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(sdBytes, 0);

        key.SetProperty(new CngProperty(
            SecurityDescriptorProperty, sdBytes, DaclSecurityInformation | CngPropertyOptions.Persist));
    }

    private const string ServiceAccountPrefix = "NT SERVICE\\";

    /// <summary>
    /// Resolves an account name to a SID. Virtual service accounts (<c>NT SERVICE\Name</c>) are computed
    /// from the name rather than looked up, because their SID is name-derived and the OS cannot translate
    /// it until the service exists — but the installer grants key access <em>before</em> the service is
    /// created. Other identities (gMSA, domain/local accounts) are resolved normally.
    /// </summary>
    internal static SecurityIdentifier ResolveAccountSid(string account)
    {
        if (account.StartsWith(ServiceAccountPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ComputeServiceSid(account[ServiceAccountPrefix.Length..]);
        }

        try
        {
            return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException ex)
        {
            throw new InvalidOperationException(
                $"Could not resolve service account '{account}' to grant TPM key access.", ex);
        }
    }

    // Windows service SID: S-1-5-80-{five LE uint32 of SHA-1(uppercase service name, UTF-16LE)}.
    // SHA-1 here is mandated by the OS service-SID derivation, not used as a security hash.
    private static SecurityIdentifier ComputeServiceSid(string serviceName)
    {
        var nameBytes = Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant());
        var hash = SHA1.HashData(nameBytes); // nosemgrep: csharp.lang.security.weak-hash.weak-hash

        var sid = new StringBuilder("S-1-5-80");
        for (var i = 0; i < 5; i++)
        {
            sid.Append('-').Append(BitConverter.ToUInt32(hash, i * 4));
        }

        return new SecurityIdentifier(sid.ToString());
    }
}
