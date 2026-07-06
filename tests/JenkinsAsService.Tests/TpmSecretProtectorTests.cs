// Copyright (c) 2024 All rights reserved
using System.Security.Principal;
using FluentAssertions;

namespace JenkinsAsService.Tests;

// Round-trips use a USER-scoped TPM key so the tests need no elevation; production uses machine-scoped
// keys. TPM-dependent tests early-return when no usable TPM / Platform Crypto Provider is present (e.g. CI
// runners) — note they then report as *passed*, not skipped (xUnit 2.9 has no dynamic Assert.Skip; visible
// skips would need Xunit.SkippableFact or an xUnit v3 upgrade). Treat green here as "did not regress", not
// "TPM verified", unless the run is on TPM-capable hardware.
public class TpmSecretProtectorTests : IDisposable
{
    private readonly bool _tpm = TpmSecretProtector.IsAvailable();

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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
        if (!_tpm)
        {
            return; // no TPM / Platform Crypto Provider on this host (reports as passed — see class note)
        }

        const string secret = "jnlp-secret-1234567890abcdef";

        var cipher = TpmSecretProtector.Protect(secret, serviceAccount: null, machineKey: false);
        var resolved = TpmSecretProtector.Resolve(cipher, machineKey: false);

        resolved.Should().Be(secret);
    }

    [Fact]
    public void Protect_produces_base64_ciphertext_that_differs_from_plaintext()
    {
        if (!_tpm)
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
        if (!_tpm)
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
        if (!_tpm || !IsElevated())
        {
            return; // machine-scoped TPM keys require a TPM and administrator privileges
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
