// Copyright (c) 2024 All rights reserved

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JenkinsAsService;

/// <summary>
/// Reconciles an existing appsettings.json against the current POCO schema: adds any schema key that
/// is absent (at its default), preserves the value of every key that is present, and prunes any key or
/// top-level section the schema does not define. Pure and deterministic — no I/O, no secret handling.
/// </summary>
internal static class ServiceSettingsNormalizer
{
    // Enums as strings ("Auto", "Unprotected", "Machine") so defaults match the shipped appsettings.json.
    private static readonly JsonSerializerOptions SchemaOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    internal static (string mergedJson, IReadOnlyList<string> addedPaths, IReadOnlyList<string> removedPaths)
        NormalizeJson(string existingJson)
    {
        var schema = new JsonObject
        {
            [ConfigKeys.Section] = SerializeDefaults(new ServiceSettings()),
            [ConfigKeys.TelemetrySection] = SerializeDefaults(new TelemetrySettings())
        };

        JsonObject existing;
        try
        {
            if (JsonNode.Parse(existingJson) is not JsonObject parsed)
            {
                // Unparseable or non-object root (missing/corrupt file, or e.g. a top-level array) — a no-op,
                // never a destructive rewrite. Hand the original text back untouched.
                return (existingJson, [], []);
            }

            existing = parsed;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (existingJson, [], []);
        }

        var added = new List<string>();
        var removed = new List<string>();
        var merged = Reconcile(schema, existing, prefix: "", added, removed);

        return (merged.ToJsonString(OutputOptions), added, removed);
    }

    private static JsonObject SerializeDefaults<T>(T poco) =>
        JsonNode.Parse(JsonSerializer.Serialize(poco, SchemaOptions))!.AsObject();

    // Builds a new object shaped by `schema`, taking values from `existing` where a key exists and
    // recursing into sub-objects. Records dotted paths added (schema-only) and removed (existing-only).
    private static JsonObject Reconcile(JsonObject schema, JsonObject existing, string prefix,
        List<string> added, List<string> removed)
    {
        var result = new JsonObject();

        foreach (var (key, schemaValue) in schema)
        {
            var path = prefix.Length == 0 ? key : prefix + ":" + key;

            if (schemaValue is JsonObject schemaChild)
            {
                if (existing[key] is JsonObject existingChild)
                {
                    result[key] = Reconcile(schemaChild, existingChild, path, added, removed);
                }
                else
                {
                    result[key] = (JsonObject)schemaChild.DeepClone();
                    RecordAddedLeaves(schemaChild, path, added);
                    if (existing.ContainsKey(key))
                    {
                        removed.Add(path); // existing had a non-object where the schema wants a section
                    }
                }
            }
            else if (existing.ContainsKey(key))
            {
                result[key] = existing[key]?.DeepClone(); // preserve existing value verbatim
            }
            else
            {
                result[key] = schemaValue?.DeepClone();    // add default
                added.Add(path);
            }
        }

        foreach (var (key, _) in existing)
        {
            if (!schema.ContainsKey(key))
            {
                removed.Add(prefix.Length == 0 ? key : prefix + ":" + key);
            }
        }

        return result;
    }

    private static void RecordAddedLeaves(JsonObject subtree, string prefix, List<string> added)
    {
        foreach (var (key, value) in subtree)
        {
            var path = prefix + ":" + key;
            if (value is JsonObject child)
            {
                RecordAddedLeaves(child, path, added);
            }
            else
            {
                added.Add(path);
            }
        }
    }
}
