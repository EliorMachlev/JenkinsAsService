// Copyright (c) 2024 All rights reserved
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ServiceSettingsNormalizerTests
{
    private static JsonObject Merged(string existing) =>
        JsonNode.Parse(ServiceSettingsNormalizer.NormalizeJson(existing).mergedJson)!.AsObject();

    [Fact]
    public void Adds_missing_leaf_with_its_default_value()
    {
        const string existing = """{"Jenkins":{"Connection":{"Url":"https://j:8443"}}}""";

        var (_, added, _) = ServiceSettingsNormalizer.NormalizeJson(existing);

        added.Should().Contain("Jenkins:Connection:Method");
        Merged(existing)["Jenkins"]!["Connection"]!["Method"]!.GetValue<string>().Should().Be("Auto");
    }

    [Fact]
    public void Adds_missing_whole_section()
    {
        const string existing = """{"Jenkins":{"Secret":{"Value":"s"}}}""";

        var (_, added, _) = ServiceSettingsNormalizer.NormalizeJson(existing);

        added.Should().Contain("Telemetry:ServiceName");
        Merged(existing)["Telemetry"]!["ServiceName"]!.GetValue<string>().Should().Be("JenkinsAsService");
    }

    [Fact]
    public void Preserves_existing_in_schema_value()
    {
        const string existing = """{"Jenkins":{"Secret":{"Value":"keep-me","Mode":"Tpm"}}}""";

        var merged = Merged(existing);

        merged["Jenkins"]!["Secret"]!["Value"]!.GetValue<string>().Should().Be("keep-me");
        merged["Jenkins"]!["Secret"]!["Mode"]!.GetValue<string>().Should().Be("Tpm");
    }

    [Fact]
    public void Prunes_unknown_key_within_a_known_section()
    {
        const string existing = """{"Jenkins":{"Secret":{"Value":"s","LegacyFlag":true}}}""";

        var (_, _, removed) = ServiceSettingsNormalizer.NormalizeJson(existing);

        removed.Should().Contain("Jenkins:Secret:LegacyFlag");
        Merged(existing)["Jenkins"]!["Secret"]!.AsObject().ContainsKey("LegacyFlag").Should().BeFalse();
    }

    [Fact]
    public void Prunes_unknown_top_level_section()
    {
        const string existing = """{"Jenkins":{"Secret":{"Value":"s"}},"Serilog":{"MinimumLevel":"Debug"}}""";

        var (_, _, removed) = ServiceSettingsNormalizer.NormalizeJson(existing);

        removed.Should().Contain("Serilog");
        Merged(existing).ContainsKey("Serilog").Should().BeFalse();
        Merged(existing).ContainsKey("Jenkins").Should().BeTrue();
    }

    [Fact]
    public void No_changes_when_already_schema_shaped()
    {
        // A full, in-schema config (built by normalizing an empty doc) must reconcile to no changes.
        var full = ServiceSettingsNormalizer.NormalizeJson("{}").mergedJson;

        var (_, added, removed) = ServiceSettingsNormalizer.NormalizeJson(full);

        added.Should().BeEmpty();
        removed.Should().BeEmpty();
    }

    [Fact]
    public void Shipped_appsettings_reconciles_to_no_changes()
    {
        var shipped = File.ReadAllText(FindShipped());

        var (_, added, removed) = ServiceSettingsNormalizer.NormalizeJson(shipped);

        added.Should().BeEmpty("the shipped appsettings.json must already equal the schema");
        removed.Should().BeEmpty("the shipped appsettings.json must not carry keys outside the schema");
    }

    private static string FindShipped()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JenkinsAsService", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("shipped appsettings.json not found from test base dir");
    }
}
