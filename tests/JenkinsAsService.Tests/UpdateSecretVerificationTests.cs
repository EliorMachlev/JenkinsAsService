// Copyright (c) 2024 All rights reserved
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;

namespace JenkinsAsService.Tests;

/// <summary>
/// update-secret verifies the URL and Java path it is about to write: the URL against the startup rule and by a
/// TCP connect, an explicit Java path by looking for java.exe. Failure exits 1 and writes nothing, by default
/// and in silent runs too; --skip-verification-steps (--svs) is the escape. Reachability is tested against
/// real loopback sockets only - a listener for "reachable", a just-released port for "unreachable" - so these
/// tests neither resolve a name nor leave the machine.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class UpdateSecretVerificationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TcpListener _listener;
    private readonly string _reachableUrl;
    private readonly string _unreachableUrl;

    public UpdateSecretVerificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_Verify_Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        // Accepts connections from its backlog without ever calling Accept: a TCP connect is all the check does.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _reachableUrl = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _unreachableUrl = $"https://127.0.0.1:{TestVerifiers.ClosedLoopbackPort()}";
    }

    public void Dispose()
    {
        _listener.Stop();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string ConfigPath => Path.Combine(_tempDir, "appsettings.json");

    // ─── The shared URL rule ───────────────────────────────────────────────────

    [Theory]
    [InlineData("https://jenkins.example.com:8443", "None")]
    [InlineData("https://jenkins.example.com:443", "None")]   // explicit default port: fine
    [InlineData("http://[::1]:8080/jenkins", "None")]
    [InlineData("https://jenkins.example.com", "NoExplicitPort")]
    [InlineData("https://jenkins.example.com:/x", "NoExplicitPort")]
    [InlineData("http://[::1]/jenkins", "NoExplicitPort")]   // the colons are the literal's
    [InlineData("not-a-url", "NotAbsolute")]
    [InlineData("", "NotAbsolute")]
    public void The_url_rule_is_absolute_with_an_explicit_port(string url, string expected)
    {
        // By name: the enum is internal, and a public test method cannot take it as a parameter.
        ControllerUrl.Check(url).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("https://jenkins.example.com")]
    public void The_service_and_update_secret_reject_the_same_urls(string url)
    {
        // One rule, two callers: the startup validator and the verifier must agree on every input.
        var startup = () => ServiceSettingsValidator.Validate(new ServiceSettings
        {
            Connection = new() { Url = url },
            Secret = new() { Value = "s" }
        });

        startup.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{ControllerUrl.Describe(ControllerUrl.Check(url))}*");
        TestVerifiers.AlwaysReachable().VerifyUrl(url)!.Message
            .Should().Contain(ControllerUrl.Describe(ControllerUrl.Check(url)));
    }

    [Theory]
    [InlineData("https://user:pa55@jenkins:8443/x", "https://jenkins:8443/x")]
    [InlineData("https://user@jenkins:8443", "https://jenkins:8443")]
    [InlineData("https://a:b@c@jenkins:8443", "https://jenkins:8443")]
    [InlineData("https://jenkins:8443/path@thing", "https://jenkins:8443/path@thing")] // '@' outside the authority
    [InlineData("https://jenkins:8443", "https://jenkins:8443")]
    [InlineData("user:pass@jenkins", "jenkins")]                                         // unparseable, still stripped
    public void Userinfo_is_stripped_from_a_url_before_it_is_shown(string url, string shown)
    {
        ControllerUrl.WithoutUserInfo(url).Should().Be(shown);
    }

    // ─── The verifier ──────────────────────────────────────────────────────────

    [Fact]
    public void A_reachable_url_passes()
    {
        TestVerifiers.RealTcp().VerifyUrl(_reachableUrl).Should().BeNull();
    }

    [Fact]
    public void An_unreachable_url_fails_naming_the_option_the_reason_and_the_url()
    {
        var failure = TestVerifiers.RealTcp().VerifyUrl(_unreachableUrl);

        failure.Should().NotBeNull();
        failure!.Value.Should().Be(VerifiedValue.Url);
        failure.Message.Should().Contain("--url").And.Contain("not reachable").And.Contain(_unreachableUrl)
            .And.Contain("SYSTEM").And.Contain("Connection:Proxy").And.Contain("--svs");
    }

    [Fact]
    public void A_malformed_url_fails_before_any_connect()
    {
        var checker = Substitute.For<IConnectivityChecker>();
        var failure = new UpdateSecretVerifier(checker, 100).VerifyUrl("https://jenkins.example.com");

        failure!.Message.Should().Contain("--url").And.Contain("explicit port").And.Contain("https://jenkins.example.com");
        checker.ReceivedCalls().Should().BeEmpty("a URL the service would refuse is not worth a connect");
    }

    [Fact]
    public void Credentials_in_the_url_are_never_shown()
    {
        const string sentinel = "SENTINEL-c7e1";
        var url = $"https://ci-user:{sentinel}@127.0.0.1:{TestVerifiers.ClosedLoopbackPort()}";

        var message = TestVerifiers.RealTcp().VerifyUrl(url)!.Message;

        message.Should().NotContain(sentinel).And.NotContain("ci-user");
        message.Should().Contain("https://127.0.0.1:", "the URL itself is shown, minus its userinfo");
    }

    [Fact]
    public void A_java_path_holding_java_exe_passes()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "java.exe"), []);

        UpdateSecretVerifier.VerifyJavaPath(_tempDir).Should().BeNull();
    }

    [Fact]
    public void A_java_path_without_java_exe_fails_naming_the_path()
    {
        var missing = Path.Combine(_tempDir, "no-jdk-here");

        var failure = UpdateSecretVerifier.VerifyJavaPath(missing);

        failure!.Value.Should().Be(VerifiedValue.JavaPath);
        failure.Message.Should().Contain("--java-path").And.Contain("java.exe was not found").And.Contain(missing);
    }

    [Fact]
    public void A_jdk_root_is_pointed_at_its_bin_folder()
    {
        // JAVA_HOME holds the JDK root; Agent:JavaPath is the folder containing java.exe.
        var bin = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "java.exe"), []);

        UpdateSecretVerifier.VerifyJavaPath(_tempDir)!.Message.Should().Contain($"It is in '{bin}'");
    }

    [Fact]
    public void An_explicit_java_path_gets_no_JAVA_HOME_fallback()
    {
        var home = Path.Combine(_tempDir, "home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllBytes(Path.Combine(home, "bin", "java.exe"), []);
        var previous = Environment.GetEnvironmentVariable("JAVA_HOME");
        Environment.SetEnvironmentVariable("JAVA_HOME", home);
        try
        {
            UpdateSecretVerifier.VerifyJavaPath(Path.Combine(_tempDir, "elsewhere")).Should().NotBeNull(
                "the path being written must hold java.exe itself, whatever JAVA_HOME happens to be");
        }
        finally
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", previous);
        }
    }

    [Fact]
    public void Blank_values_are_not_verified()
    {
        var checker = Substitute.For<IConnectivityChecker>();

        new UpdateSecretVerifier(checker, 100).Verify(null, "  ").Should().BeEmpty();
        checker.ReceivedCalls().Should().BeEmpty();
    }

    // ─── The command: fail by default, silent runs included ───────────────────

    private (int Code, string Stdout, string Stderr) Run(string[] args, UpdateSecretVerifier verifier, TextReader? input = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = UpdateSecretCommand.Run([UpdateSecretCommand.Name, .. args], _tempDir, verifier, input);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void A_silent_write_to_an_unreachable_url_fails_and_writes_nothing()
    {
        var recorded = new List<string>();

        var (code, _, stderr) = Run(
            ["--silent", "--secret", "s", "--url", _unreachableUrl, "--mode", "Unprotected"],
            TestVerifiers.RealTcp(recorded));

        code.Should().Be(1);
        File.Exists(ConfigPath).Should().BeFalse();
        stderr.Should().Contain("--url").And.Contain(_unreachableUrl).And.Contain("nothing was written");
        recorded.Should().ContainSingle("the reason is also recorded where an MSI install can find it")
            .Which.Should().Contain(_unreachableUrl);
    }

    [Fact]
    public void A_silent_write_to_a_reachable_url_succeeds()
    {
        var (code, stdout, _) = Run(
            ["--silent", "--secret", "s", "--url", _reachableUrl, "--mode", "Unprotected"], TestVerifiers.RealTcp());

        code.Should().Be(0);
        stdout.Should().Contain("Verified --url");
        Jenkins().GetProperty("Connection").GetProperty("Url").GetString().Should().Be(_reachableUrl);
    }

    [Fact]
    public void A_bad_java_path_fails_a_silent_write()
    {
        var (code, _, stderr) = Run(
            ["--silent", "--secret", "s", "--url", _reachableUrl, "--mode", "Unprotected",
             "--java-path", Path.Combine(_tempDir, "nope")],
            TestVerifiers.RealTcp());

        code.Should().Be(1);
        stderr.Should().Contain("--java-path");
        File.Exists(ConfigPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("--skip-verification-steps")]
    [InlineData("--svs")]
    public void The_skip_flag_writes_unchecked_and_says_so(string flag)
    {
        var missingJava = Path.Combine(_tempDir, "nope");

        var (code, stdout, _) = Run(
            ["--silent", "--secret", "s", "--url", _unreachableUrl, "--mode", "Unprotected",
             "--java-path", missingJava, flag],
            TestVerifiers.RealTcp());

        code.Should().Be(0);
        stdout.Should().Contain("Verification skipped (--skip-verification-steps)")
            .And.Contain("--url and --java-path");
        Jenkins().GetProperty("Agent").GetProperty("JavaPath").GetString().Should().Be(missingJava);
    }

    [Fact]
    public void A_failed_verification_does_not_consume_the_secret_file()
    {
        var secretFile = Path.Combine(_tempDir, "secret.tmp");
        File.WriteAllText(secretFile, "file-secret");

        var (code, _, _) = Run(
            ["--silent", "--secret-file", secretFile, "--url", _unreachableUrl, "--mode", "Unprotected"],
            TestVerifiers.RealTcp());

        code.Should().Be(1);
        File.Exists(secretFile).Should().BeTrue("a run that writes nothing must not delete its only copy of the secret");
    }

    [Fact]
    public void The_secret_is_never_part_of_a_verification_message()
    {
        const string sentinel = "SENTINEL-5b02";
        var recorded = new List<string>();

        var (code, stdout, stderr) = Run(
            ["--silent", "--secret", sentinel, "--url", _unreachableUrl, "--mode", "Unprotected"],
            TestVerifiers.RealTcp(recorded));

        code.Should().Be(1);
        stdout.Should().NotContain(sentinel);
        stderr.Should().NotContain(sentinel);
        recorded.Should().NotContain(m => m.Contains(sentinel));
    }

    [Fact]
    public void Merge_verifies_a_supplied_url_and_writes_it_when_good()
    {
        File.WriteAllText(ConfigPath, "{\"Jenkins\":{\"Secret\":{\"Value\":\"kept\"}}}");

        Run(["--silent", "--merge", "--url", _unreachableUrl], TestVerifiers.RealTcp()).Code.Should().Be(1);
        Run(["--silent", "--merge", "--url", _reachableUrl, "--agent-name", "node-7"], TestVerifiers.RealTcp())
            .Code.Should().Be(0);

        var connection = Jenkins().GetProperty("Connection");
        connection.GetProperty("Url").GetString().Should().Be(_reachableUrl);
        connection.GetProperty("AgentName").GetString().Should().Be("node-7");
        Jenkins().GetProperty("Secret").GetProperty("Value").GetString().Should().Be("kept");
    }

    [Fact]
    public void Merge_without_a_url_or_java_path_verifies_nothing()
    {
        // The installer's WriteAdvanced custom actions: nothing they write is verifiable, so nothing connects.
        File.WriteAllText(ConfigPath, "{\"Jenkins\":{}}");
        var checker = Substitute.For<IConnectivityChecker>();

        Run(["--silent", "--merge", "--url", "", "--java-path", "", "--agent-name", "", "--debug", "1"],
            new UpdateSecretVerifier(checker, 100)).Code.Should().Be(0);

        checker.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void An_upgrade_that_keeps_the_existing_config_verifies_nothing()
    {
        // Nothing new is written, so an unreachable URL in the arguments is irrelevant.
        File.WriteAllText(ConfigPath, "{\"Jenkins\":{\"Secret\":{\"Value\":\"live\"}}}");

        Run(["--silent", "--upgrade", "--secret", "", "--url", _unreachableUrl, "--mode", "Dpapi"],
            TestVerifiers.RealTcp()).Code.Should().Be(0);
    }

    [Fact]
    public void An_upgrade_that_repair_writes_verifies_what_it_writes()
    {
        File.WriteAllText(ConfigPath, "{\"Jenkins\":{\"Connection\":{\"Url\":\"https://old:8443\"}}}");

        Run(["--silent", "--upgrade", "--secret", "s", "--url", _unreachableUrl, "--mode", "Unprotected"],
            TestVerifiers.RealTcp()).Code.Should().Be(1);
    }

    [Fact]
    public void Set_data_dir_verifies_nothing()
    {
        var checker = Substitute.For<IConnectivityChecker>();

        Run(["--silent", "--set-data-dir", _tempDir, "--url", _unreachableUrl], new UpdateSecretVerifier(checker, 100))
            .Code.Should().Be(0);
        checker.ReceivedCalls().Should().BeEmpty();
    }

    // ─── The installer's quoted, possibly-empty skip argument ─────────────────

    [Fact]
    public void An_empty_argument_at_an_option_position_is_ignored()
    {
        // WriteConfig ends in "[JasSkipVerificationArg]", which is "" unless JENKINS_SKIP_VERIFICATION=1.
        var parsed = UpdateSecretCommand.ParseArgs([UpdateSecretCommand.Name, "--silent", "--url", "https://j:1", ""]);

        parsed.Should().NotBeNull();
        parsed!.SkipVerification.Should().BeFalse();
        parsed.Url.Should().Be("https://j:1");
    }

    [Fact]
    public void The_installer_form_of_the_skip_argument_turns_verification_off()
    {
        UpdateSecretCommand.ParseArgs([UpdateSecretCommand.Name, "--silent", "--url", "https://j:1", "--svs"])!
            .SkipVerification.Should().BeTrue();
    }

    [Fact]
    public void An_empty_value_is_still_consumed_by_its_option()
    {
        // The ignore applies at option positions only: "--url" "" is an unset URL, not a URL of "--silent".
        var parsed = UpdateSecretCommand.ParseArgs([UpdateSecretCommand.Name, "--url", "", "--silent"]);

        parsed!.Url.Should().BeEmpty();
        parsed.Silent.Should().BeTrue();
    }

    private JsonElement Jenkins() =>
        JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement.GetProperty("Jenkins");
}
