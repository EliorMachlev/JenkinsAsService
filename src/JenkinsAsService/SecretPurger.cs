// Copyright (c) 2024 All rights reserved

using System.Security;
using AdysTech.CredentialManager;

namespace JenkinsAsService;

/// <summary>What happened to the secret material for a given <see cref="SecretMode"/>.</summary>
/// <remarks>
/// Returned as well as reported, so callers and tests can assert on the <em>result</em> rather than on the
/// wording of a log line. The distinction that matters operationally is
/// <see cref="Failed"/> versus <see cref="AlreadyAbsent"/>: one leaves a live credential on the machine and
/// needs manual cleanup, the other is a clean no-op, and a string match cannot be trusted to tell them apart.
/// </remarks>
internal enum SecretStoreOutcome
{
    /// <summary>The secret lives in <c>appsettings.json</c> (Unprotected / Dpapi), which the caller deletes.</summary>
    InConfigFile,

    /// <summary>The external store held the secret and it was removed.</summary>
    Removed,

    /// <summary>Nothing was there — an earlier purge, or a mode that was never actually used.</summary>
    AlreadyAbsent,

    /// <summary>The secret could not be removed and is still on the machine. Requires manual cleanup.</summary>
    Failed,
}

/// <summary>
/// Removes the agent secret from wherever the configured <see cref="SecretMode"/> put it.
/// <para>
/// Deleting <c>appsettings.json</c> is not enough. Only <see cref="SecretMode.Unprotected"/> and
/// <see cref="SecretMode.Dpapi"/> keep the secret material <em>in</em> the config (plaintext and ciphertext
/// respectively); the other modes store the config's value as a <em>pointer</em> — an environment variable
/// name, a Credential Manager target — with the real secret living elsewhere on the machine. Uninstalling
/// without this leaves a usable Jenkins agent credential behind on a host the product was removed from.
/// </para>
/// <para>
/// Every step is best-effort: an uninstall must finish. Nothing throws, and every outcome is reported,
/// because a secret that could not be removed is manual work the operator has to know about.
/// </para>
/// </summary>
internal static class SecretPurger
{
    /// <summary>
    /// Removes the out-of-config secret material for <paramref name="mode"/>.
    /// </summary>
    /// <param name="mode">The configured protection mode.</param>
    /// <param name="configuredValue">
    /// <c>Secret:Value</c>. For the pointer modes this <em>is</em> the target name, which is why it is read
    /// before the config is deleted; an operator may have changed it from the default.
    /// </param>
    /// <param name="report">Progress sink — one line per action taken or refused.</param>
    internal static SecretStoreOutcome Purge(SecretMode mode, string? configuredValue, Action<string> report) =>
        mode switch
        {
            SecretMode.Tpm => PurgeTpmKey(report),

            SecretMode.EnvironmentVariable =>
                PurgeEnvironmentVariable(TargetOrDefault(configuredValue, SecretStoreNames.EnvironmentVariable), report),

            SecretMode.CredentialManager =>
                PurgeCredential(TargetOrDefault(configuredValue, SecretStoreNames.CredentialTarget), report),

            // Unprotected / Dpapi: the secret (or its ciphertext) is in appsettings.json, which the caller deletes.
            _ => Report(report, SecretStoreOutcome.InConfigFile,
                $"Secret mode {mode}: the secret is in {ConfigKeys.FileName} and goes with it."),
        };

    /// <summary>
    /// The live target name, falling back to the shipped default. A corrupt or already-deleted config leaves
    /// no name, and the default is still worth attempting — it is where an unmodified install put it.
    /// </summary>
    private static string TargetOrDefault(string? configuredValue, string fallback) =>
        string.IsNullOrWhiteSpace(configuredValue) ? fallback : configuredValue.Trim();

    private static SecretStoreOutcome Report(Action<string> report, SecretStoreOutcome outcome, string message)
    {
        report(message);
        return outcome;
    }

    /// <summary>
    /// Deletes the machine-scoped TPM key. The ciphertext in the config is worthless without it, but the key
    /// is a persisted TPM object: left behind it survives uninstall, reinstall, and every later product on
    /// the box, occupying a named slot in the platform key store.
    /// </summary>
    private static SecretStoreOutcome PurgeTpmKey(Action<string> report)
    {
        const string what = "TPM key";
        try
        {
            // machineKey: true mirrors TpmSecretProtector.Protect(secret, serviceAccount) — production keys
            // are always machine-scoped. A user-scoped key belongs to a test, not to an installation.
            TpmSecretProtector.DeleteKey(machineKey: true);
            return Report(report, SecretStoreOutcome.Removed,
                $"Removed {what} '{TpmSecretProtector.KeyName}'.");
        }
        catch (Exception ex)
        {
            // Deliberately unfiltered: CNG surfaces platform failures as several unrelated exception types
            // (CryptographicException, and Win32/COM-derived ones), and an uninstall must not fail on any of
            // them. The cost of a broad catch here is a reported failure; the cost of a narrow one is a
            // half-finished uninstall.
            return Report(report, SecretStoreOutcome.Failed,
                $"Could not remove {what} '{TpmSecretProtector.KeyName}': {ex.Message}");
        }
    }

    private static SecretStoreOutcome PurgeEnvironmentVariable(string name, Action<string> report)
    {
        const string what = "machine environment variable";
        try
        {
            if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) is null)
            {
                return Report(report, SecretStoreOutcome.AlreadyAbsent, $"{what} '{name}' is already absent.");
            }

            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Machine);
            return Report(report, SecretStoreOutcome.Removed, $"Removed {what} '{name}'.");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Report(report, SecretStoreOutcome.Failed, $"Could not remove {what} '{name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the Credential Manager entry.
    /// <para>
    /// Credentials are per-user. This runs as SYSTEM from the uninstall custom action, so a credential
    /// written under a different account (<c>update-secret --impersonate</c>) is not in this user's vault and
    /// cannot be removed here — hence the explicit instruction rather than a silent pass. The credential is
    /// useless without a controller to present it to, but it is still a stored secret and the operator
    /// deserves to know it is there.
    /// </para>
    /// </summary>
    private static SecretStoreOutcome PurgeCredential(string target, Action<string> report)
    {
        const string what = "Credential Manager entry";
        try
        {
            return CredentialManager.RemoveCredentials(target, CredentialType.Generic)
                ? Report(report, SecretStoreOutcome.Removed, $"Removed {what} '{target}'.")
                : Report(report, SecretStoreOutcome.AlreadyAbsent,
                    $"{what} '{target}' was not found for this account.");
        }
        catch (Exception ex)
        {
            // Unfiltered for the same reason as the TPM path: this crosses into a third-party wrapper over
            // the Win32 credential API, and an uninstall must complete regardless of what it throws.
            return Report(report, SecretStoreOutcome.Failed,
                $"Could not remove {what} '{target}': {ex.Message}. " +
                "If it was written with --impersonate, remove it while logged on as that account: " +
                $"cmdkey /delete:{target}");
        }
    }
}
