// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Where the pointer-style <see cref="SecretMode"/>s keep the secret: the machine environment variable and
/// the Credential Manager target.
/// <para>
/// One definition, because <see cref="SecretWriter"/> writes to these and <see cref="SecretPurger"/> deletes
/// them. Declared separately they would be free to drift, and the failure is silent in the worst direction:
/// an uninstall reports success while leaving a usable Jenkins agent credential on the machine. The config's
/// <c>Secret:Value</c> normally carries the live name — these are the fallbacks for when it is gone or blank.
/// </para>
/// </summary>
internal static class SecretStoreNames
{
    /// <summary>Machine-scoped environment variable holding the secret under <see cref="SecretMode.EnvironmentVariable"/>.</summary>
    internal const string EnvironmentVariable = "JENKINS_AGENT_SECRET";

    /// <summary>Credential Manager target under <see cref="SecretMode.CredentialManager"/>.</summary>
    internal const string CredentialTarget = "JenkinsAsService/AgentSecret";

    /// <summary>User name stored alongside the credential. Write-side only; the vault keys on the target.</summary>
    internal const string CredentialUserName = "JenkinsAgent";
}
