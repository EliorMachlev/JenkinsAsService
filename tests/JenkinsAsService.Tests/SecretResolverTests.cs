// Copyright (c) 2024 All rights reserved
using System.Security.Cryptography; // NOSONAR — ProtectedData is from a NuGet package; standalone analysis can't resolve it
using System.Text;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class SecretResolverTests
{
    private readonly SecretResolver _sut = new();

    // ─── Unprotected ────────────────────────────────────────────────────

    [Fact]
    public void Unprotected_returns_plaintext()
    {
        var settings = new ServiceSettings
        {
            AgentSecret = "my-plain-secret",
            SecretMode = SecretMode.Unprotected
        };

        _sut.Resolve(settings).Should().Be("my-plain-secret");
    }

    // ─── DPAPI ──────────────────────────────────────────────────────────

    [Fact]
    public void Dpapi_decrypts_machine_scoped_secret()
    {
        const string plaintext = "dpapi-test-secret";
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), SecretResolver.DpapiEntropy, DataProtectionScope.LocalMachine);
        var base64 = Convert.ToBase64String(encrypted);

        var settings = new ServiceSettings
        {
            AgentSecret = base64,
            SecretMode = SecretMode.Dpapi
        };

        _sut.Resolve(settings).Should().Be(plaintext);
    }

    [Fact]
    public void Dpapi_decrypts_user_scoped_secret()
    {
        const string plaintext = "dpapi-user-secret";
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), SecretResolver.DpapiEntropy, DataProtectionScope.CurrentUser);

        var settings = new ServiceSettings
        {
            AgentSecret = Convert.ToBase64String(encrypted),
            SecretMode = SecretMode.Dpapi,
            DpapiScope = DpapiScope.User
        };

        _sut.Resolve(settings).Should().Be(plaintext);
    }

    [Fact]
    public void Dpapi_throws_on_invalid_base64()
    {
        var settings = new ServiceSettings
        {
            AgentSecret = "not-valid-base64!!!",
            SecretMode = SecretMode.Dpapi
        };

        var act = () => _sut.Resolve(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid Base64*");
    }

    // ─── Environment Variable ───────────────────────────────────────────

    [Fact]
    public void EnvironmentVariable_throws_when_variable_missing()
    {
        var settings = new ServiceSettings
        {
            AgentSecret = "NONEXISTENT_VAR_12345",
            SecretMode = SecretMode.EnvironmentVariable
        };

        var act = () => _sut.Resolve(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'NONEXISTENT_VAR_12345'*not found*");
    }

    // ─── Credential Manager ─────────────────────────────────────────────

    [Fact]
    public void CredentialManager_throws_when_credential_missing()
    {
        var settings = new ServiceSettings
        {
            AgentSecret = "JenkinsAsService/NonExistent_" + Guid.NewGuid().ToString("N")[..8],
            SecretMode = SecretMode.CredentialManager
        };

        var act = () => _sut.Resolve(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ─── Empty secret ───────────────────────────────────────────────────

    [Fact]
    public void Throws_when_AgentSecret_is_empty()
    {
        var settings = new ServiceSettings
        {
            AgentSecret = "",
            SecretMode = SecretMode.Dpapi
        };

        var act = () => _sut.Resolve(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty*");
    }
}
