// Copyright (c) 2024 All rights reserved
using System.Security.Principal;
using FluentAssertions;

namespace JenkinsAsService.Tests;

// Round-trips use a USER-scoped TPM key so the tests need no elevation; production uses machine-scoped keys.
//
// TPM-dependent tests cannot run without a TPM / Platform Crypto Provider (e.g. CI runners), and xUnit 2.9 has
// no dynamic Assert.Skip — so they bail out and report as *passed*. Green here therefore means "did not
// regress", NOT "TPM verified".
//
// To stop that being indistinguishable from real coverage, set JAS_REQUIRE_TPM=1 on a TPM-capable machine:
// the bail-out then FAILS instead of passing quietly, so a job that is supposed to exercise the TPM cannot
// silently stop doing so (a provider that disappears after an OS change would otherwise go unnoticed).
public class TpmSecretProtectorTests : IDisposable
{
    /// <summary>Set to <c>1</c>/<c>true</c> on hardware where TPM coverage is mandatory.</summary>
    internal const string RequireTpmVariable = "JAS_REQUIRE_TPM";

    private readonly bool _tpm = TpmSecretProtector.IsAvailable();

    /// <summary>
    /// Whether the caller demanded real TPM coverage. Read per-call rather than cached so a test can be
    /// reasoned about in isolation.
    /// </summary>
    internal static bool TpmCoverageRequired()
    {
        var value = Environment.GetEnvironmentVariable(RequireTpmVariable);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <c>true</c> when the test body must be skipped. Fails the test instead when
    /// <see cref="RequireTpmVariable"/> demands coverage, so "no TPM" can never masquerade as a pass.
    /// </summary>
    private static bool ShouldBailOut(bool available, string requirement)
    {
        if (available)
        {
            return false;
        }

        TpmCoverageRequired().Should().BeFalse(
            $"{RequireTpmVariable} is set, so this run must exercise the TPM, but {requirement}");
        return true;
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void The_require_tpm_switch_is_read_from_the_environment(string? value, bool expected)
    {
        // The whole point of the switch is that a TPM job cannot silently stop testing the TPM. If this
        // parsing were wrong the switch would read as "not required" and restore exactly that blind spot,
        // so it is verified directly rather than inferred from a run that happens to have hardware.
        var original = Environment.GetEnvironmentVariable(RequireTpmVariable);
        try
        {
            Environment.SetEnvironmentVariable(RequireTpmVariable, value);
            TpmCoverageRequired().Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RequireTpmVariable, original);
        }
    }

    public void Dispose()
    {
        if (_tpm)
        {
            try { TpmSecretProtector.DeleteKey(machineKey: false); } catch { /* best-effort cleanup */ }
            try { TpmSecretProtector.DeleteKey(machineKey: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Protect_then_resolve_round_trips_the_secret()
    {
        if (ShouldBailOut(_tpm, "no TPM / Platform Crypto Provider is available on this host"))
        {
            return;
        }

        const string secret = "jnlp-secret-1234567890abcdef";

        var cipher = TpmSecretProtector.Protect(secret, serviceAccount: null, machineKey: false);
        var resolved = TpmSecretProtector.Resolve(cipher, machineKey: false);

        resolved.Should().Be(secret);
    }

    [Fact]
    public void Protect_produces_base64_ciphertext_that_differs_from_plaintext()
    {
        if (ShouldBailOut(_tpm, "no TPM / Platform Crypto Provider is available on this host"))
        {
            return;
        }

        const string secret = "another-secret";

        var cipher = TpmSecretProtector.Protect(secret, serviceAccount: null, machineKey: false);

        cipher.Should().NotBe(secret);
        var act = () => Convert.FromBase64String(cipher);
        act.Should().NotThrow("the ciphertext must be valid base64");
    }

    [Fact]
    public void Resolve_with_no_key_present_throws_a_clear_error()
    {
        if (ShouldBailOut(_tpm, "no TPM / Platform Crypto Provider is available on this host"))
        {
            return;
        }

        TpmSecretProtector.DeleteKey(machineKey: false); // ensure the key is absent

        // A syntactically valid base64 blob, but no key exists to decrypt it.
        var act = () => TpmSecretProtector.Resolve(Convert.ToBase64String([1, 2, 3, 4]), machineKey: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*TPM key not found*");
    }

    [Fact]
    public void Resolve_with_invalid_base64_throws_a_clear_error()
    {
        var act = () => TpmSecretProtector.Resolve("not valid base64!!!", machineKey: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Base64*");
    }

    [Fact]
    public void Resolves_virtual_service_account_sid_without_the_service_existing()
    {
        // Computed from the name (SHA-1 service-SID algorithm); must match `sc showsid Jenkins`.
        // NTAccount.Translate cannot resolve this until the service is registered, which is why the
        // installer (granting key access before service creation) relies on the computed value.
        var sid = TpmSecretProtector.ResolveAccountSid(@"NT SERVICE\Jenkins");

        sid.Value.Should().Be("S-1-5-80-1814819820-3735751029-1300315571-615649680-2171313575");
    }

    [Fact]
    public void Resolves_virtual_service_account_prefix_case_insensitively()
    {
        TpmSecretProtector.ResolveAccountSid(@"nt service\Jenkins").Value
            .Should().Be("S-1-5-80-1814819820-3735751029-1300315571-615649680-2171313575");
    }

    [Fact]
    public void Machine_key_round_trips_and_grants_service_account_access()
    {
        // Machine-scoped TPM keys need a TPM *and* elevation; an unelevated TPM box legitimately skips this.
        if (ShouldBailOut(_tpm, "no TPM / Platform Crypto Provider is available on this host") || !IsElevated())
        {
            return;
        }

        // Grant the current (elevated) identity use-rights, exercising the SID translation + DACL set,
        // then decrypt under that same identity.
        var account = WindowsIdentity.GetCurrent().Name;
        const string secret = "machine-scoped-secret";

        var cipher = TpmSecretProtector.Protect(secret, account, machineKey: true);
        var resolved = TpmSecretProtector.Resolve(cipher, machineKey: true);

        resolved.Should().Be(secret);
    }
}
