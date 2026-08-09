// Copyright (c) 2024 All rights reserved
using System.Net;
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// A build node behind a corporate proxy depends on these rules, and a misparsed value is indistinguishable
/// from an unreachable controller at runtime — so the parsing is pinned down here rather than discovered
/// on a customer box.
/// </summary>
public class ProxyResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_means_inherit_the_system_proxy(string? value)
    {
        ProxyResolver.ClassifyMode(value).Should().Be(ProxyMode.System);
        ProxyResolver.Create(value, null).Should().BeNull("a null proxy leaves the handler default alone");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("Direct")]
    [InlineData("none")]
    [InlineData("  NONE  ")]
    public void Direct_and_none_mean_bypass_all_proxies(string value)
    {
        ProxyResolver.ClassifyMode(value).Should().Be(ProxyMode.Direct);
        ProxyResolver.Create(value, null).Should().BeNull("the caller disables proxy use instead");
    }

    [Theory]
    [InlineData("proxy.example.com:8080", "http://proxy.example.com:8080/")]
    [InlineData("http://proxy.example.com:8080", "http://proxy.example.com:8080/")]
    [InlineData("https://secure-proxy.example.com:3128", "https://secure-proxy.example.com:3128/")]
    [InlineData("10.0.0.5:3128", "http://10.0.0.5:3128/")]
    public void A_bare_host_port_is_accepted_and_defaulted_to_http(string configured, string expected)
    {
        // host:port is how proxies are conventionally written and how HTTP_PROXY is usually set; demanding
        // a scheme would reject the form most operators type.
        ProxyResolver.ClassifyMode(configured).Should().Be(ProxyMode.Explicit);
        ProxyResolver.ParseAddress(configured).Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("://")]
    [InlineData("http://")]
    public void A_malformed_address_fails_loudly(string configured)
    {
        // Fails at startup with a usable message rather than looking like an unreachable controller later.
        var act = () => ProxyResolver.ParseAddress(configured);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Connection:Proxy*");
    }

    [Fact]
    public void An_explicit_proxy_produces_a_webproxy_that_bypasses_local()
    {
        var proxy = ProxyResolver.Create("proxy.example.com:8080", null);

        proxy.Should().BeOfType<WebProxy>();
        var web = (WebProxy)proxy!;
        web.Address.Should().Be(new Uri("http://proxy.example.com:8080/"));
        web.BypassProxyOnLocal.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("*.corp.local", 1)]
    [InlineData("*.corp.local;jenkins.internal", 2)]
    [InlineData("a.example.com, b.example.com", 2)]
    [InlineData("a.example.com;;  ;b.example.com", 2)]
    public void The_bypass_list_splits_on_both_separators_and_drops_blanks(string? bypass, int expected)
    {
        ProxyResolver.ParseBypassList(bypass).Should().HaveCount(expected);
    }

    [Fact]
    public void Wildcard_bypass_entries_become_uri_shaped_anchored_regexes()
    {
        // Two traps: BypassList holds regular expressions, not wildcards, AND it matches them against the
        // whole URI rather than the host — so a host-only pattern silently never matches.
        var pattern = ProxyResolver.ToBypassRegex("*.corp.local");
        static bool Matches(string uri, string p) => System.Text.RegularExpressions.Regex.IsMatch(uri, p);

        Matches("https://jenkins.corp.local:8443/", pattern).Should().BeTrue();
        Matches("http://ci.corp.local", pattern).Should().BeTrue();
        Matches("https://evil-corp.local.example.com/", pattern).Should().BeFalse("the pattern is anchored");
        Matches("jenkins.corp.local", pattern).Should().BeFalse("bare hosts are not what BypassList is given");
    }

    [Fact]
    public void A_dot_in_a_bypass_entry_is_literal()
    {
        // Escaping before reinstating the wildcard keeps '.' from matching an arbitrary character.
        var pattern = ProxyResolver.ToBypassRegex("jenkinsxinternal");

        System.Text.RegularExpressions.Regex.IsMatch("https://jenkins.internal/", ProxyResolver.ToBypassRegex("jenkins.internal"))
            .Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch("https://jenkins.internal/", pattern).Should().BeFalse();
    }

    [Fact]
    public void The_bypass_list_is_attached_to_an_explicit_proxy()
    {
        var proxy = (WebProxy)ProxyResolver.Create("proxy:8080", "*.corp.local;jenkins.internal")!;

        proxy.BypassList.Should().HaveCount(2);
        proxy.IsBypassed(new Uri("https://jenkins.corp.local:8443")).Should().BeTrue();
        proxy.IsBypassed(new Uri("https://ci.example.com:8443")).Should().BeFalse();
    }
}
