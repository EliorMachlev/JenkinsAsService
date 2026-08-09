// Copyright (c) 2024 All rights reserved
using System.Security;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class SecretPurgerTests
{
    private readonly List<string> _report = [];

    /// <summary>
    /// Modes whose secret material lives OUTSIDE appsettings.json and that can be exercised safely against a
    /// throwaway target name. Deleting the config does not remove these, which is why SecretPurger exists.
    /// <para>
    /// <see cref="SecretMode.Tpm"/> is deliberately absent: its target is a fixed, machine-scoped TPM key
    /// name, so a test cannot point it somewhere harmless. Running it here would delete the key belonging to
    /// a real installation on the developer's own machine. It is covered separately, opt-in.
    /// </para>
    /// </summary>
    public static TheoryData<SecretMode> PointerModes => [SecretMode.EnvironmentVariable, SecretMode.CredentialManager];

    /// <summary>Modes whose secret (or its ciphertext) is in the config and goes when the file does.</summary>
    public static TheoryData<SecretMode> InConfigModes => [SecretMode.Unprotected, SecretMode.Dpapi];

    [Theory]
    [MemberData(nameof(InConfigModes))]
    public void In_config_modes_report_that_the_config_carries_the_secret(SecretMode mode)
    {
        var outcome = SecretPurger.Purge(mode, "whatever", _report.Add);

        outcome.Should().Be(SecretStoreOutcome.InConfigFile);
        _report.Should().ContainSingle().Which.Should().Contain(ConfigKeys.FileName);
    }

    [Theory]
    [MemberData(nameof(PointerModes))]
    public void Pointer_modes_never_claim_the_config_carries_the_secret(SecretMode mode)
    {
        // The mode routing is the part that goes wrong silently: a pointer mode falling into the in-config
        // branch would report success while leaving the real secret on the machine. A throwaway target keeps
        // this from touching whatever this machine actually has stored.
        var outcome = SecretPurger.Purge(mode, "JAS_TEST_ABSENT_" + Guid.NewGuid().ToString("N"), _report.Add);

        outcome.Should().NotBe(SecretStoreOutcome.InConfigFile,
            "{0} stores the secret outside the config and must account for it", mode);
        _report.Should().NotBeEmpty();
    }

    [Fact]
    public void Removes_the_TPM_key()
    {
        // Opt-in, like TpmSecretProtectorTests: the TPM key name is fixed and machine-scoped, so this
        // deletes the key a real installation on this machine would be using. Set JAS_PURGE_TPM=1 only on a
        // box where that is acceptable. Without it the test reports passed, meaning "did not run".
        if (Environment.GetEnvironmentVariable("JAS_PURGE_TPM") != "1" || !TpmSecretProtector.IsAvailable())
        {
            return;
        }

        TpmSecretProtector.Protect("round-trip-secret", serviceAccount: null);

        var outcome = SecretPurger.Purge(SecretMode.Tpm, configuredValue: null, _report.Add);

        outcome.Should().Be(SecretStoreOutcome.Removed);
    }

    [Fact]
    public void Removes_the_configured_machine_environment_variable()
    {
        // A name unique to this test: the purge writes to the real machine environment block.
        var name = "JAS_TEST_SECRET_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Environment.SetEnvironmentVariable(name, "s3cret", EnvironmentVariableTarget.Machine);
        }
        catch (SecurityException)
        {
            return; // not elevated - the machine block is not writable here
        }

        var outcome = SecretPurger.Purge(SecretMode.EnvironmentVariable, name, _report.Add);

        outcome.Should().Be(SecretStoreOutcome.Removed);
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine).Should().BeNull();
    }

    [Fact]
    public void Uses_the_configured_target_not_the_default()
    {
        // Secret:Value IS the target name for the pointer modes, and an operator may have changed it. Reading
        // the default instead would leave the real secret in place while reporting success.
        var name = "JAS_TEST_CUSTOM_" + Guid.NewGuid().ToString("N")[..8];

        SecretPurger.Purge(SecretMode.EnvironmentVariable, name, _report.Add);

        _report.Should().ContainSingle(m => m.Contains(name, StringComparison.Ordinal));
        _report.Should().NotContain(m =>
            m.Contains(SecretStoreNames.EnvironmentVariable, StringComparison.Ordinal));
    }

    [Fact]
    public void Falls_back_to_the_default_target_when_the_config_names_none()
    {
        // A corrupt or already-deleted config leaves no target name. The default is still worth attempting:
        // it is where an unmodified install put it.
        SecretPurger.Purge(SecretMode.EnvironmentVariable, "   ", _report.Add);

        _report.Should().ContainSingle(m =>
            m.Contains(SecretStoreNames.EnvironmentVariable, StringComparison.Ordinal));
    }

    [Fact]
    public void An_absent_environment_variable_is_reported_not_failed()
    {
        var outcome = SecretPurger.Purge(
            SecretMode.EnvironmentVariable, "JAS_TEST_NEVER_SET_" + Guid.NewGuid().ToString("N"), _report.Add);

        outcome.Should().Be(SecretStoreOutcome.AlreadyAbsent);
    }

    [Fact]
    public void Never_throws_so_an_uninstall_always_completes()
    {
        // Every mode except Tpm, each pointed at a name nothing owns, so the no-throw guarantee is checked
        // without deleting anything real. Tpm is excluded because its key name is fixed: there is no
        // harmless target to aim it at, and this suite is routinely run elevated, where it would delete the
        // key of a real installation on the developer's machine. Its no-throw path is covered by
        // Removes_the_TPM_key under JAS_PURGE_TPM=1.
        foreach (var mode in Enum.GetValues<SecretMode>().Where(m => m != SecretMode.Tpm))
        {
            var act = () => SecretPurger.Purge(mode, "JAS_TEST_ABSENT_" + Guid.NewGuid().ToString("N"), _report.Add);
            act.Should().NotThrow($"{mode} must not be able to fail an uninstall");
        }
    }

    [Fact]
    public void The_store_names_are_shared_with_the_writer_not_copied()
    {
        // SecretWriter writes to these and SecretPurger deletes them. Two copies could drift, and the failure
        // is silent in the worst direction: uninstall reports success while the credential stays usable.
        // This asserts the constants exist in one place; the compiler enforces the rest.
        SecretStoreNames.EnvironmentVariable.Should().Be("JENKINS_AGENT_SECRET");
        SecretStoreNames.CredentialTarget.Should().Be("JenkinsAsService/AgentSecret");
    }
}
