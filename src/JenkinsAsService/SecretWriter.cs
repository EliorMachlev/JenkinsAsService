// Copyright (c) 2024 All rights reserved

using System.Net;
using System.Security;
using System.Security.Cryptography; // NOSONAR — ProtectedData is from a NuGet package; standalone analysis can't resolve it
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdysTech.CredentialManager;

namespace JenkinsAsService;

public static class SecretWriter
{
    // Aliases onto the single definition in SecretStoreNames — SecretPurger deletes what this writes, so a
    // second copy of these strings could drift and leave the secret behind on uninstall.
    private const string EnvVarName = SecretStoreNames.EnvironmentVariable;
    private const string CredTargetName = SecretStoreNames.CredentialTarget;
    private const string CredUserName = SecretStoreNames.CredentialUserName;
    private const string ConfigFileName = ConfigKeys.FileName;
    private const string ConfigSectionName = ConfigKeys.Section;

    /// <summary>
    /// Processes the secret based on mode, then writes appsettings.json.
    /// Preserves existing fields (CustomArguments, DebugMode, CompactLog, MaxRetries, ProcessMitigations, ...)
    /// if the file exists, and otherwise writes their defaults (<c>"111"</c> for ProcessMitigations).
    /// <paramref name="optionalFields"/> then overrides any of them that were supplied - the same fields,
    /// with the same null-means-unchanged rule, as <see cref="MergeConfig"/>, so an option given on a full
    /// write is applied rather than parsed and dropped. Its <see cref="ConfigMergeFields.DpapiScope"/> is
    /// ignored in favour of <paramref name="dpapiScope"/>, which the secret was actually protected with.
    /// When <paramref name="serviceAccount"/> is supplied, the written file's ACL is hardened so that
    /// only SYSTEM, Administrators and that account can read it (removes inherited Users access).
    /// </summary>
    public static void WriteConfig(string basePath, string secret, SecretMode mode,
        string server, string? agentName, string? javaPath,
        DpapiScope dpapiScope = DpapiScope.Machine, string? controllerCertThumbprint = null,
        string? serviceAccount = null, ConfigMergeFields? optionalFields = null)
    {
        var configSecret = ProcessSecret(secret, mode, dpapiScope, serviceAccount);
        var configPath = ConfigPath(basePath);

        var existingRoot = ReadExistingRoot(configPath);
        var existingJenkins = existingRoot?[ConfigSectionName]?.AsObject();
        var jenkins = BuildJenkinsSection(
            configSecret, mode, server, agentName, javaPath, dpapiScope, controllerCertThumbprint, existingJenkins);

        if (optionalFields is not null)
        {
            ApplyFields(jenkins, optionalFields with { DpapiScope = null, Url = null, AgentName = null, JavaPath = null });
        }

        WriteRoot(configPath, existingRoot, jenkins);

        if (!string.IsNullOrWhiteSpace(serviceAccount))
        {
            ConfigAclHardener.Harden(configPath, serviceAccount);
        }

        // Emit every schema key on a fresh install. BuildJenkinsSection only sets the keys this writer knows,
        // so newer POCO keys and the whole Telemetry section would
        // otherwise be absent until an MSI upgrade ran the reconcile. Reconcile here to the same POCO schema
        // the upgrade path uses, so fresh-install output matches upgrade output and can't drift as the schema
        // grows. Adds missing keys at their defaults; the secret is an in-schema value (preserved verbatim),
        // and File.Replace keeps the hardened DACL just applied.
        NormalizeConfig(basePath);
    }

    /// <summary>
    /// Surgically sets <c>Jenkins:DataDirectory</c> in appsettings.json, preserving every other field and
    /// top-level section. Used by the installer's second custom action (the value can't be appended to the
    /// main <c>WriteConfig</c> command without exceeding the MSI 255-char custom-action limit). No-op-safe:
    /// creates the section/file if absent.
    /// </summary>
    public static void SetDataDirectory(string basePath, string dataDirectory)
    {
        var configPath = ConfigPath(basePath);
        var existingRoot = ReadExistingRoot(configPath);
        var existingJenkins = existingRoot?[ConfigSectionName]?.AsObject();

        var jenkins = existingJenkins is null
            ? new JsonObject()
            : (JsonObject)existingJenkins.DeepClone();

        if (jenkins[ConfigKeys.Agent.Name] is not JsonObject agent)
        {
            agent = new JsonObject();
            jenkins[ConfigKeys.Agent.Name] = agent;
        }
        agent[ConfigKeys.Agent.DataDirectory] = dataDirectory;

        WriteRoot(configPath, existingRoot, jenkins);
    }

    /// <summary>
    /// Surgically merges the supplied optional fields into the <c>Jenkins</c> section of appsettings.json,
    /// creating any missing sub-section. Every field not supplied (null) — and every other top-level
    /// section, plus the already-written secret — is preserved verbatim. Used by the installer's advanced
    /// custom actions. The file's existing ACL is preserved (File.WriteAllText truncates in place).
    /// </summary>
    public static void MergeConfig(string basePath, ConfigMergeFields fields)
    {
        var configPath = ConfigPath(basePath);
        var existingRoot = ReadExistingRoot(configPath);
        var jenkins = existingRoot?[ConfigSectionName] is JsonObject existingJenkins
            ? (JsonObject)existingJenkins.DeepClone()
            : new JsonObject();

        ApplyFields(jenkins, fields);
        WriteRoot(configPath, existingRoot, jenkins);
    }

    // The one statement of where each optional field lives, shared by MergeConfig and WriteConfig.
    private static void ApplyFields(JsonObject jenkins, ConfigMergeFields fields)
    {
        if (fields.Url is { } url)
            GetOrCreate(jenkins, ConfigKeys.Connection.Name)[ConfigKeys.Connection.Url] = url;
        if (fields.AgentName is { } agentName)
            GetOrCreate(jenkins, ConfigKeys.Connection.Name)[ConfigKeys.Connection.AgentName] = agentName;
        if (fields.JavaPath is { } javaPath)
            GetOrCreate(jenkins, ConfigKeys.Agent.Name)[ConfigKeys.Agent.JavaPath] = javaPath;
        if (fields.Method is { } method)
            GetOrCreate(jenkins, ConfigKeys.Connection.Name)[ConfigKeys.Connection.Method] = method.ToString();
        if (fields.DpapiScope is { } scope)
            GetOrCreate(jenkins, ConfigKeys.Secret.Name)[ConfigKeys.Secret.DpapiScope] = scope.ToString();
        if (fields.ViaFile is { } viaFile)
            GetOrCreate(jenkins, ConfigKeys.Secret.Name)[ConfigKeys.Secret.ViaFile] = viaFile;
        if (fields.CustomArguments is { } customArgs)
            GetOrCreate(jenkins, ConfigKeys.Agent.Name)[ConfigKeys.Agent.CustomArguments] = customArgs;
        if (fields.SanitizeEnvironment is { } sanitize)
            GetOrCreate(jenkins, ConfigKeys.Hardening.Name)[ConfigKeys.Hardening.SanitizeEnvironment] = sanitize;
        if (fields.AllowedEnvironmentVariables is { } allowed)
            GetOrCreate(jenkins, ConfigKeys.Hardening.Name)[ConfigKeys.Hardening.AllowedEnvironmentVariables] = allowed;
        if (fields.ProcessMitigations is { } mitigations)
            GetOrCreate(jenkins, ConfigKeys.Hardening.Name)[ConfigKeys.Hardening.ProcessMitigations] = mitigations.ToString();
        if (fields.DebugMode is { } debug)
            GetOrCreate(jenkins, ConfigKeys.Logging.Name)[ConfigKeys.Logging.DebugMode] = debug;
        if (fields.CompactLog is { } compact)
            GetOrCreate(jenkins, ConfigKeys.Logging.Name)[ConfigKeys.Logging.CompactLog] = compact;
        if (fields.RetainedLogs is { } retained)
            GetOrCreate(jenkins, ConfigKeys.Logging.Name)[ConfigKeys.Logging.RetainedLogs] = retained;
        if (fields.MaxRetries is { } maxRetries)
            GetOrCreate(jenkins, ConfigKeys.Recovery.Name)[ConfigKeys.Recovery.MaxRetries] = maxRetries;
    }

    /// <summary>
    /// Reconciles appsettings.json to the current schema (adds missing keys at their POCO defaults,
    /// prunes keys/sections the schema no longer defines) via <see cref="ServiceSettingsNormalizer"/>.
    /// Rewrites the file only when something changed, atomically via a temp file + <c>File.Replace</c>,
    /// which preserves the destination file's DACL — so the hardened ACL survives. Never reads, resolves,
    /// or rewrites the secret value (it is an in-schema key, preserved by the reconcile). Returns the
    /// dotted paths added and removed, for the caller to log.
    /// </summary>
    public static (IReadOnlyList<string> added, IReadOnlyList<string> removed) NormalizeConfig(string basePath)
    {
        var configPath = ConfigPath(basePath);
        var existingJson = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";

        var (merged, added, removed) = ServiceSettingsNormalizer.NormalizeJson(existingJson);

        if (added.Count == 0 && removed.Count == 0)
        {
            return (added, removed); // nothing changed — leave the file (and its ACL/ordering) untouched
        }

        if (File.Exists(configPath))
        {
            // Atomic replace: File.Replace keeps the destination's security descriptor (DACL), so the
            // ConfigAclHardener-applied ACL is preserved across the rewrite.
            var tempPath = configPath + ".tmp";
            File.WriteAllText(tempPath, merged, Encoding.UTF8);
            try
            {
                File.Replace(tempPath, configPath, null);
            }
            catch
            {
                // Don't orphan the temp file if the replace itself fails.
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
                {
                    // best-effort cleanup — surface the original failure
                }

                throw;
            }
        }
        else
        {
            File.WriteAllText(configPath, merged, Encoding.UTF8);
        }

        return (added, removed);
    }

    /// <summary>
    /// The config file inside <paramref name="basePath"/> (the application base directory). Path.Join, not
    /// Path.Combine: Combine silently discards everything before a rooted later segment, Join only
    /// concatenates, so the result can never leave the base directory whatever the segments hold. For the
    /// inputs that occur - a directory with or without a trailing separator, or empty - both give the same
    /// path (pinned by <c>SecretWriterConfigPathTests</c>), and null still throws as Combine did.
    /// </summary>
    internal static string ConfigPath(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        return Path.Join(basePath, ConfigFileName);
    }

    private static JsonObject GetOrCreate(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    // Rebuilds the root: every non-Jenkins top-level section is preserved, the Jenkins section replaced.
    private static void WriteRoot(string configPath, JsonObject? existingRoot, JsonObject jenkins)
    {
        var root = new JsonObject();
        if (existingRoot != null)
        {
            foreach (var kvp in existingRoot)
            {
                if (kvp.Key != ConfigSectionName)
                {
                    root[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
        }

        root[ConfigSectionName] = jenkins;

        var options = new JsonSerializerOptions { WriteIndented = true };
        // basePath is the trusted application base directory; filename is a constant — no traversal vector.
        // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
        File.WriteAllText(configPath, root.ToJsonString(options), Encoding.UTF8);
    }

    private static string ProcessSecret(string secret, SecretMode mode, DpapiScope dpapiScope,
        string? serviceAccount) => mode switch
    {
        SecretMode.Dpapi => ProtectDpapi(secret, dpapiScope),
        SecretMode.EnvironmentVariable => StoreEnvironmentVariable(secret),
        SecretMode.CredentialManager => StoreCredentialManager(secret),
        SecretMode.Tpm => TpmSecretProtector.Protect(secret, serviceAccount),
        SecretMode.Unprotected => secret,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SecretMode.")
    };

    // Read the full existing config root to preserve user-customized fields
    // and any non-Jenkins top-level sections (e.g. Telemetry, Logging).
    private static JsonObject? ReadExistingRoot(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        }
        catch (JsonException)
        {
            // Corrupt file — overwrite entirely
            return null;
        }
    }

    // Builds the nested Jenkins section (Connection / Secret / Agent / Hardening / Logging / Recovery),
    // preserving any existing values in sub-sections this writer does not explicitly set.
    private static JsonObject BuildJenkinsSection(string configSecret, SecretMode mode,
        string server, string? agentName, string? javaPath, DpapiScope dpapiScope,
        string? controllerCertThumbprint, JsonObject? existingJenkins)
    {
        var conn = ExistingObject(existingJenkins, ConfigKeys.Connection.Name);
        var secret = ExistingObject(existingJenkins, ConfigKeys.Secret.Name);
        var agent = ExistingObject(existingJenkins, ConfigKeys.Agent.Name);
        var hardening = ExistingObject(existingJenkins, ConfigKeys.Hardening.Name);
        var logging = ExistingObject(existingJenkins, ConfigKeys.Logging.Name);
        var recovery = ExistingObject(existingJenkins, ConfigKeys.Recovery.Name);

        return new JsonObject
        {
            [ConfigKeys.Connection.Name] = new JsonObject
            {
                [ConfigKeys.Connection.Url] = server,
                [ConfigKeys.Connection.Method] = ExistingMethod(conn),
                [ConfigKeys.Connection.AgentName] = agentName ?? ExistingString(conn, ConfigKeys.Connection.AgentName),
                [ConfigKeys.Connection.ControllerCertThumbprint] =
                    controllerCertThumbprint ?? ExistingString(conn, ConfigKeys.Connection.ControllerCertThumbprint)
            },
            [ConfigKeys.Secret.Name] = new JsonObject
            {
                [ConfigKeys.Secret.Value] = configSecret,
                [ConfigKeys.Secret.Mode] = mode.ToString(),
                [ConfigKeys.Secret.DpapiScope] = dpapiScope.ToString(),
                [ConfigKeys.Secret.ViaFile] = ExistingValue(secret, ConfigKeys.Secret.ViaFile, true)
            },
            [ConfigKeys.Agent.Name] = new JsonObject
            {
                [ConfigKeys.Agent.JavaPath] = javaPath ?? ExistingString(agent, ConfigKeys.Agent.JavaPath),
                [ConfigKeys.Agent.CustomArguments] = ExistingString(agent, ConfigKeys.Agent.CustomArguments),
                [ConfigKeys.Agent.DataDirectory] = ExistingString(agent, ConfigKeys.Agent.DataDirectory)
            },
            [ConfigKeys.Hardening.Name] = new JsonObject
            {
                [ConfigKeys.Hardening.SanitizeEnvironment] = ExistingValue(hardening, ConfigKeys.Hardening.SanitizeEnvironment, true),
                [ConfigKeys.Hardening.AllowedEnvironmentVariables] = ExistingString(hardening, ConfigKeys.Hardening.AllowedEnvironmentVariables),
                [ConfigKeys.Hardening.ProcessMitigations] = ExistingMitigations(hardening)
            },
            [ConfigKeys.Logging.Name] = new JsonObject
            {
                [ConfigKeys.Logging.DebugMode] = ExistingValue(logging, ConfigKeys.Logging.DebugMode, false),
                [ConfigKeys.Logging.CompactLog] = ExistingValue(logging, ConfigKeys.Logging.CompactLog, false),
                [ConfigKeys.Logging.RetainedLogs] = ExistingValue(logging, ConfigKeys.Logging.RetainedLogs, 3)
            },
            [ConfigKeys.Recovery.Name] = new JsonObject
            {
                [ConfigKeys.Recovery.MaxRetries] = ExistingValue(recovery, ConfigKeys.Recovery.MaxRetries, 0)
            }
        };
    }

    private static JsonObject? ExistingObject(JsonObject? existing, string key) =>
        existing?[key] as JsonObject;

    // Preserve an existing Hardening:ProcessMitigations, else write the all-on mask. A level name written
    // by the previous build (Full / AllowNetworkImages / Off) is migrated to the digits with the same
    // effect - through the same helper the service uses at startup, so the two cannot disagree - rather
    // than reset to 111. Anything else that is not three 0/1 digits is replaced with 111, so a full write
    // repairs a value nothing can read instead of preserving it.
    private static string ExistingMitigations(JsonObject? hardening)
    {
        var value = ExistingString(hardening, ConfigKeys.Hardening.ProcessMitigations);
        if (MitigationMask.TryParse(value, out var mask) || MitigationMask.TryFromLegacyName(value, out mask))
        {
            return mask.ToString();
        }

        return MitigationMask.All.ToString();
    }

    // Preserve an existing Connection:Method, else default to Auto (so the enum binds to a valid value).
    private static string ExistingMethod(JsonObject? connection)
    {
        var value = ExistingString(connection, ConfigKeys.Connection.Method);
        return string.IsNullOrEmpty(value) ? nameof(ConnectionMethod.Auto) : value;
    }

    private static string ExistingString(JsonObject? existing, string key)
    {
        try
        {
            return existing?[key]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // Wrong JSON type for this key in a hand-edited config — fall back rather than crash the write.
            return "";
        }
    }

    private static T ExistingValue<T>(JsonObject? existing, string key, T fallback) where T : struct
    {
        try
        {
            return existing?[key]?.GetValue<T>() ?? fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // e.g. "RetainedLogs": "three" in a hand-edited config — use the fallback instead of throwing.
            return fallback;
        }
    }

    private static string ProtectDpapi(string secret, DpapiScope scope)
    {
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(plainBytes, SecretResolver.DpapiEntropy, scope.ToProtectionScope());
        return Convert.ToBase64String(encrypted);
    }

    private static string StoreEnvironmentVariable(string secret)
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, secret, EnvironmentVariableTarget.Machine);
        }
        catch (SecurityException ex)
        {
            throw new InvalidOperationException(
                "Setting a machine-level environment variable requires administrator privileges.", ex);
        }

        return EnvVarName;
    }

    private static string StoreCredentialManager(string secret)
    {
        try
        {
            CredentialManager.SaveCredentials(CredTargetName, new NetworkCredential(CredUserName, secret));
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new InvalidOperationException(
                $"Failed to save credential to Windows Credential Manager target '{CredTargetName}'. " +
                "Ensure the process has sufficient privileges.", ex);
        }

        return CredTargetName;
    }
}

/// <summary>
/// Optional configuration fields for <see cref="SecretWriter.MergeConfig"/>. Each member is nullable;
/// a <c>null</c> member leaves that field unchanged in the existing appsettings.json.
/// </summary>
public sealed record ConfigMergeFields
{
    // Merge-only: a full write takes these three as its own arguments, and ignores them here (the interactive
    // flow may have re-asked the URL or Java path after a failed verification, so the parsed value is stale).
    public string? Url { get; init; }
    public string? AgentName { get; init; }
    public string? JavaPath { get; init; }

    public ConnectionMethod? Method { get; init; }
    public DpapiScope? DpapiScope { get; init; }
    public bool? ViaFile { get; init; }
    public string? CustomArguments { get; init; }
    public bool? SanitizeEnvironment { get; init; }
    public string? AllowedEnvironmentVariables { get; init; }
    public MitigationMask? ProcessMitigations { get; init; }
    public bool? DebugMode { get; init; }
    public bool? CompactLog { get; init; }
    public int? RetainedLogs { get; init; }
    public int? MaxRetries { get; init; }
}
