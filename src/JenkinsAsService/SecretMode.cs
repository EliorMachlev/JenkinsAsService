// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public enum SecretMode
{
    Unprotected,
    Dpapi,
    EnvironmentVariable,
    CredentialManager,

    /// <summary>
    /// Encrypts the secret with a non-exportable, TPM-resident RSA key via the Microsoft Platform Crypto
    /// Provider. Stronger than <see cref="Dpapi"/> machine-scope: the private key never leaves the TPM,
    /// so the ciphertext is bound to this physical machine and cannot be exfiltrated even by an admin.
    /// </summary>
    Tpm
}
