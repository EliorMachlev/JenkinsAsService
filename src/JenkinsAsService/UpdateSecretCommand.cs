// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public static class UpdateSecretCommand
{
    private const string UsageText = """
        Usage: JenkinsAsService update-secret [options]

        Options:
          --secret <value>        The Jenkins agent secret (plaintext)
          --secret-file <path>    Read secret from file (file is deleted after reading)
          --secret-env <var>      Read secret from named environment variable
          --url <value>           Jenkins controller address (include port)
          --mode <value>          Protection mode: Dpapi, Tpm, EnvironmentVariable, CredentialManager, Unprotected
                                  Tpm encrypts with a non-exportable TPM-resident key (pass --service-account
                                  so the service can decrypt at runtime).
          --dpapi-scope <value>   DPAPI scope when --mode Dpapi: Machine (default) or User.
                                  User-scope requires writing as the service account (see --impersonate).
          --thumbprint <value>    SHA-256 thumbprint of the Jenkins controller cert to pin (jar download)
          --service-account <acct> Service account (e.g. 'NT SERVICE\Jenkins') granted read on the
                                  written config; appsettings.json ACL is hardened to deny Users.
          --set-data-dir <path>   Only set Jenkins:DataDirectory in appsettings.json, then exit (used
                                  by the installer; the writable data dir, separate from the binary).
          --agent-name <value>    Agent node label (default: hostname)
          --java-path <value>     Java installation directory (default: JAVA_HOME)
          --impersonate           Run Credential Manager write as a different user account.
                                  Interactive: prompts for username and password.
                                  Silent: requires --username; password from env JAS_IMPERSONATE_PASSWORD.
          --username <value>      Windows account for impersonation (DOMAIN\account)
          --silent                Non-interactive; requires --secret/--secret-file/--secret-env, --url, --mode
          --merge                 Merge only the optional fields below into an existing appsettings.json
                                  (no secret written). Used by the installer's advanced custom actions.
          --method <value>        Connection transport: Auto, WebSocket, Https
          --via-file <bool>       Pass secret to agent via file (true/false)
          --custom-args <value>   Extra java.exe arguments
          --sanitize-env <bool>   Sanitize the agent child environment (true/false)
          --allowed-env <value>   Extra env var names to pass through (semicolon/comma separated)
          --upgrade               Reconcile appsettings.json to the current schema on MSI upgrade:
                                  add new settings at their defaults, prune settings the schema no longer
                                  defines, and preserve existing values and the secret. If no secret is
                                  present, falls back to a full write from --secret/--url/--mode.
          --purge                 Uninstall cleanup: delete the runtime data directory and appsettings.json,
                                  then exit. Used by the installer's uninstall custom action to remove what
                                  Windows Installer cannot (both are created at runtime, not installed).
          --debug <bool>          Verbose Java agent logging (true/false)
          --compact-log <bool>    Compact JSON log format (true/false)
          --retained-logs <int>   Number of rolled log files to keep
          --max-retries <int>     Max auto-recovery attempts (0 = infinite)
          --help                  Show this help
        """;

    // Env var used to pass impersonation password in silent mode (avoids command-line exposure)
    private const string ImpersonatePasswordEnv = "JAS_IMPERSONATE_PASSWORD";

    private const string PurgeFlag = "--purge";

    private const string ConfigFileName = ConfigKeys.FileName;
    private const string ConfigSectionName = ConfigKeys.Section;
    private const string EventLogSource = EventLogSourceInstaller.DefaultSource;
    private const string EventLogName = EventLogSourceInstaller.DefaultLogName;

    // Mutable accumulator used by ParseArgs; returned directly to avoid a 10-parameter constructor.
    private sealed class ParseState
    {
        public string? SecretArg;
        public string? SecretFile;
        public string? SecretEnv;
        public string? Url;
        public SecretMode? Mode;
        public DpapiScope? DpapiScope;
        public string? Thumbprint;
        public string? ServiceAccount;
        public string? SetDataDir;
        public string? AgentName;
        public string? JavaPath;
        public string? Username;
        public bool Silent;
        public bool Impersonate;
        public bool Merge;
        public bool Upgrade;
        public ConnectionMethod? Method;
        public bool? ViaFile;
        public string? CustomArgs;
        public bool? SanitizeEnv;
        public string? AllowedEnv;
        public bool? DebugMode;
        public bool? CompactLog;
        public int? RetainedLogs;
        public int? MaxRetries;
    }

    public static int Run(string[] args)
    {
        return Run(args, null);
    }

    public static int Run(string[] args, string? basePath)
    {
        basePath ??= AppContext.BaseDirectory;

        if (Array.Exists(args, a => a is "--help" or "-h"))
        {
            Console.WriteLine(UsageText);
            return 0;
        }

        // Uninstall cleanup. Checked before ParseArgs because it shares none of the write path's required
        // arguments - there is no secret, URL or mode to supply when the product is being removed.
        if (Array.Exists(args, a => a == PurgeFlag))
        {
            PurgeInstallation(basePath);
            return 0;
        }

        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            return 1;
        }

        // Focused operation: just persist the data directory and exit. Used by the installer's second
        // custom action — the value can't be appended to the main write command (MSI 255-char CA limit).
        if (parsed.SetDataDir is not null)
        {
            SecretWriter.SetDataDirectory(basePath, parsed.SetDataDir);
            Console.WriteLine($"Data directory set to {parsed.SetDataDir}");
            return 0;
        }

        // Installer-only path: surgically merge optional fields into an already-written config, then exit.
        if (parsed.Merge)
        {
            SecretWriter.MergeConfig(basePath, new ConfigMergeFields
            {
                Method = parsed.Method,
                DpapiScope = parsed.DpapiScope,
                ViaFile = parsed.ViaFile,
                CustomArguments = parsed.CustomArgs,
                SanitizeEnvironment = parsed.SanitizeEnv,
                AllowedEnvironmentVariables = parsed.AllowedEnv,
                DebugMode = parsed.DebugMode,
                CompactLog = parsed.CompactLog,
                RetainedLogs = parsed.RetainedLogs,
                MaxRetries = parsed.MaxRetries
            });
            Console.WriteLine("Optional configuration merged into appsettings.json.");
            return 0;
        }

        // Installer-only path: reconcile the config to the current schema on MSI upgrade, then decide
        // whether the secret must be (re)written. Runs as SYSTEM; never resolves/decrypts the secret.
        if (parsed.Upgrade)
        {
            return RunUpgrade(basePath, parsed);
        }

        return parsed.Silent
            ? RunSilent(basePath, parsed)
            : RunInteractive(basePath, parsed);
    }

    // ─── Argument parsing ────────────────────────────────────────────────────

    private static ParseState? ParseArgs(string[] args)
    {
        var state = new ParseState();
        for (int i = 1; i < args.Length; i++)
        {
            if (!TryApplyValueArg(args, ref i, state) && !TryApplyFlagArg(args[i], state))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                Console.Error.WriteLine(UsageText);
                return null;
            }
        }

        return state;
    }

    private static bool TryApplyValueArg(string[] args, ref int i, ParseState state)
    {
        switch (args[i])
        {
            case "--secret":
                state.SecretArg = Next(args, ref i);
                return true;
            case "--secret-file":
                state.SecretFile = Next(args, ref i);
                return true;
            case "--secret-env":
                state.SecretEnv = Next(args, ref i);
                return true;
            case "--url":
                state.Url = Next(args, ref i);
                return true;
            case "--mode":
                state.Mode = ParseMode(Next(args, ref i));
                return true;
            case "--dpapi-scope":
                state.DpapiScope = ParseDpapiScope(Next(args, ref i));
                return true;
            case "--thumbprint":
                state.Thumbprint = Next(args, ref i);
                return true;
            case "--service-account":
                state.ServiceAccount = Next(args, ref i);
                return true;
            case "--set-data-dir":
                state.SetDataDir = SanitizePathArgument(Next(args, ref i));
                return true;
            case "--agent-name":
                state.AgentName = Next(args, ref i);
                return true;
            case "--java-path":
                state.JavaPath = SanitizePathArgument(Next(args, ref i));
                return true;
            case "--username":
                state.Username = Next(args, ref i);
                return true;
            case "--method":
                state.Method = ParseMethod(Next(args, ref i));
                return true;
            case "--via-file":
                state.ViaFile = ParseBool(Next(args, ref i));
                return true;
            case "--custom-args":
                state.CustomArgs = Next(args, ref i);
                return true;
            case "--sanitize-env":
                state.SanitizeEnv = ParseBool(Next(args, ref i));
                return true;
            case "--allowed-env":
                state.AllowedEnv = Next(args, ref i);
                return true;
            case "--debug":
                state.DebugMode = ParseBool(Next(args, ref i));
                return true;
            case "--compact-log":
                state.CompactLog = ParseBool(Next(args, ref i));
                return true;
            case "--retained-logs":
                state.RetainedLogs = ParseInt(Next(args, ref i));
                return true;
            case "--max-retries":
                state.MaxRetries = ParseInt(Next(args, ref i));
                return true;
            default:
                return false;
        }
    }

    private static bool TryApplyFlagArg(string arg, ParseState state)
    {
        switch (arg)
        {
            case "--silent":
                state.Silent = true;
                return true;
            case "--impersonate":
                state.Impersonate = true;
                return true;
            case "--merge":
                state.Merge = true;
                return true;
            case "--upgrade":
                state.Upgrade = true;
                return true;
            default:
                return false;
        }
    }

    // ─── Silent mode ─────────────────────────────────────────────────────────

    private static int RunSilent(string basePath, ParseState args)
    {
        // Installer-only path: runs elevated (SYSTEM) during install, so pre-create the event source
        // here while we have the privilege. The low-privilege service account cannot do this later.
        EventLogSourceInstaller.Ensure(EventLogSource, EventLogName);

        var secret = ResolveSecretInput(args.SecretArg, args.SecretFile, args.SecretEnv);

        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine("Error: --secret, --secret-file, or --secret-env is required in --silent mode.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(args.Url))
        {
            Console.Error.WriteLine("Error: --url is required in --silent mode.");
            return 1;
        }

        if (args.Mode is null)
        {
            Console.Error.WriteLine("Error: --mode is required in --silent mode.");
            return 1;
        }

        if (args.Impersonate)
        {
            return RunSilentImpersonated(basePath, secret, args);
        }

        SecretWriter.WriteConfig(basePath, secret, args.Mode.Value, args.Url, args.AgentName, args.JavaPath,
            args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount);
        return ReportConfigWritten(basePath, args.Mode.Value);
    }

    private static int RunSilentImpersonated(string basePath, string secret, ParseState args)
    {
        if (string.IsNullOrWhiteSpace(args.Username))
        {
            Console.Error.WriteLine("Error: --username is required with --impersonate in --silent mode.");
            return 1;
        }

        var password = Environment.GetEnvironmentVariable(ImpersonatePasswordEnv);
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine($"Error: env var {ImpersonatePasswordEnv} must be set with --impersonate in --silent mode.");
            return 1;
        }

        try
        {
            ImpersonationRunner.Run(args.Username, password, () =>
                SecretWriter.WriteConfig(basePath, secret, args.Mode!.Value, args.Url!, args.AgentName, args.JavaPath,
                    args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount));
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        return ReportConfigWritten(basePath, args.Mode!.Value);
    }

    // Shared success tail for the two non-interactive write paths.
    private static int ReportConfigWritten(string basePath, SecretMode mode)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        Console.WriteLine($"Configuration written to {configPath} (mode: {mode})");
        return 0;
    }

    // ─── Upgrade mode ────────────────────────────────────────────────────────

    // Distinguishes the three states RunUpgrade must react to: a present secret is preserved, no secret is
    // a safe repair-write, and an unreadable file is left alone (it may hold a secret we simply can't parse).
    private enum ConfigState { HasSecret, NoSecret, Unreadable }

    private static ConfigState InspectConfig(string basePath)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        if (!File.Exists(configPath))
        {
            return ConfigState.NoSecret; // nothing to lose -> repair is safe
        }

        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(ConfigFileName, optional: true)
                .Build();
            var value = config.GetSection(ConfigSectionName)[ConfigKeys.Secret.ValuePath];
            return string.IsNullOrWhiteSpace(value) ? ConfigState.NoSecret : ConfigState.HasSecret;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException
                                       or InvalidOperationException or InvalidDataException)
        {
            // InvalidDataException: ConfigurationBuilder wraps a malformed JSON file's JsonException here.
            return ConfigState.Unreadable;
        }
    }

    private static int RunUpgrade(string basePath, ParseState args)
    {
        // 1. Reconcile appsettings.json to the current schema (add new keys, prune obsolete ones).
        //    Secret-agnostic — the secret's value, being an in-schema key, is preserved untouched.
        //    Best-effort: a normalize failure must not roll back the upgrade (the service still runs on
        //    whatever config is already on disk / its own defaults).
        try
        {
            var (added, removed) = SecretWriter.NormalizeConfig(basePath);
            foreach (var path in added)
            {
                Console.WriteLine($"Added missing setting: {path}");
            }
            foreach (var path in removed)
            {
                Console.WriteLine($"Removed obsolete setting: {path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Warning: could not reconcile appsettings.json ({ex.Message}); continuing without changes.");
        }

        // 2. Decryption-free, identity-independent secret-presence check: only inspect the raw string.
        //    A present-but-undecryptable secret (User-DPAPI / TPM / Credential Manager under any identity)
        //    counts as present and is left alone. A broken/incorrect secret likewise keeps failing, as
        //    it did before the upgrade.
        switch (InspectConfig(basePath))
        {
            case ConfigState.HasSecret:
                Console.WriteLine("Existing secret present — configuration preserved.");
                return 0;
            case ConfigState.Unreadable:
                // Do NOT repair-write over an unparseable file — it may hold a secret we cannot read.
                Console.Error.WriteLine("Warning: existing appsettings.json is unreadable — leaving it untouched. Re-run update-secret to repair.");
                return 0;
            default: // NoSecret (missing or parseable-empty): repair by writing from the supplied args.
                     // Fails when none were supplied, exactly as a fresh silent install with no secret fails.
                Console.WriteLine("No existing secret found — writing configuration from supplied arguments.");
                return RunSilent(basePath, args);
        }
    }

    // ─── Interactive mode ─────────────────────────────────────────────────────

    private static int RunInteractive(string basePath, ParseState args)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        TryReadExistingConfig(basePath, configPath,
            out var existingServer, out var existingAgentName, out var existingJavaPath, out var existingMode);

        var server = InteractiveConfigPrompts.PromptServer(args.Url, existingServer);
        if (server is null)
        {
            return 1;
        }

        var secret = InteractiveConfigPrompts.PromptSecret(
            ResolveSecretInput(args.SecretArg, args.SecretFile, args.SecretEnv));
        if (secret is null)
        {
            return 1;
        }

        var selectedMode = InteractiveConfigPrompts.PromptMode(args.Mode, existingMode);
        var agentName = InteractiveConfigPrompts.PromptText("Agent name", args.AgentName, existingAgentName, "hostname");
        var javaPath = InteractiveConfigPrompts.PromptText("Java path", args.JavaPath, existingJavaPath, "JAVA_HOME");

        return WriteInteractiveConfig(basePath, secret, selectedMode, server, agentName, javaPath, args);
    }

    private static int WriteInteractiveConfig(string basePath,
        string secret, SecretMode selectedMode, string server,
        string? agentName, string? javaPath, ParseState args)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        try
        {
            if (args.Impersonate)
            {
                var credentials = InteractiveConfigPrompts.PromptImpersonationCredentials(args.Username);
                if (credentials is null)
                {
                    return 1;
                }

                ImpersonationRunner.Run(credentials.Value.Username, credentials.Value.Password, () =>
                    SecretWriter.WriteConfig(basePath, secret, selectedMode, server, agentName, javaPath,
                        args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount));
            }
            else
            {
                SecretWriter.WriteConfig(basePath, secret, selectedMode, server, agentName, javaPath,
                    args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount);
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Configuration saved to {configPath}");
        Console.WriteLine($"  Mode: {selectedMode}");
        Console.WriteLine($"  URL:  {server}");
        return 0;
    }

    // ─── Shared helpers ───────────────────────────────────────────────────────

    // Reads existing appsettings.json so interactive prompts can offer defaults.
    // Falls back to empty strings / Dpapi on a missing or corrupt file.
    /// <summary>
    /// Removes the runtime data directory and the generated config. The data directory is read from the
    /// config being deleted - an operator who moved it via <c>Agent:DataDirectory</c> must have <em>their</em>
    /// directory removed, not the default one. A missing or unreadable config falls back to the default
    /// location, which is where the data would be in that case anyway.
    /// </summary>
    private static void PurgeInstallation(string basePath)
    {
        string? configured = null;
        try
        {
            configured = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(ConfigFileName, optional: true)
                .Build()
                .GetSection(ConfigSectionName)[ConfigKeys.Agent.DataDirectoryPath];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not read {ConfigFileName} ({ex.Message}); using the default data directory.");
        }

        UninstallCleanup.Purge(basePath, DataPaths.ComputeDataDirectory(configured), Console.WriteLine);
    }

    private static void TryReadExistingConfig(string basePath, string configPath,
        out string existingServer, out string existingAgentName, out string existingJavaPath,
        out SecretMode existingMode)
    {
        existingServer = "";
        existingAgentName = "";
        existingJavaPath = "";
        existingMode = SecretMode.Dpapi;

        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(ConfigFileName, optional: true)
                .Build();
            var section = config.GetSection(ConfigSectionName);
            existingServer = section[ConfigKeys.Connection.UrlPath] ?? "";
            existingAgentName = section[ConfigKeys.Connection.AgentNamePath] ?? "";
            existingJavaPath = section[ConfigKeys.Agent.JavaPathPath] ?? "";
            if (Enum.TryParse<SecretMode>(section[ConfigKeys.Secret.ModePath], out var parsed))
            {
                existingMode = parsed;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Corrupt JSON in config — use defaults
        }
        catch (IOException)
        {
            // File I/O error reading config — use defaults
        }
        catch (InvalidOperationException)
        {
            // Invalid config structure — use defaults
        }
    }

    private static string? ResolveSecretInput(string? secretArg, string? secretFile, string? secretEnv)
    {
        if (secretArg is not null)
        {
            return secretArg;
        }

        if (secretFile is not null)
        {
            if (!File.Exists(secretFile))
            {
                Console.Error.WriteLine($"Error: secret file not found: {secretFile}");
                return null;
            }

            var secret = File.ReadAllText(secretFile, System.Text.Encoding.UTF8).Trim();
            try
            {
                File.Delete(secretFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort delete — the operation continues, but a plaintext secret is now left on disk,
                // so surface it: the caller should remove the file manually.
                Console.Error.WriteLine(
                    $"Warning: could not delete secret file '{secretFile}' ({ex.Message}). Delete it manually.");
            }

            return secret;
        }

        if (secretEnv is not null)
        {
            var secret = Environment.GetEnvironmentVariable(secretEnv);
            if (string.IsNullOrWhiteSpace(secret))
            {
                Console.Error.WriteLine($"Error: environment variable '{secretEnv}' is not set or empty.");
                return null;
            }

            return secret;
        }

        return null;
    }

    private static string? Next(string[] args, ref int i)
    {
        if (++i < args.Length)
        {
            return args[i];
        }

        Console.Error.WriteLine($"Error: {args[i - 1]} requires a value.");
        return null;
    }

    // Returns null (instead of defaulting to Dpapi) so callers can fail explicitly on invalid input.
    internal static SecretMode? ParseMode(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (Enum.TryParse<SecretMode>(value, ignoreCase: true, out var mode))
        {
            return mode;
        }

        Console.Error.WriteLine($"Error: unknown mode '{value}'. Valid: Dpapi, Tpm, EnvironmentVariable, CredentialManager, Unprotected");
        return null;
    }

    // Returns null on invalid input so callers can fail explicitly.
    internal static DpapiScope? ParseDpapiScope(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (Enum.TryParse<DpapiScope>(value, ignoreCase: true, out var scope))
        {
            return scope;
        }

        Console.Error.WriteLine($"Error: unknown dpapi-scope '{value}'. Valid: Machine, User");
        return null;
    }

    // Returns null on invalid input so a typo fails rather than silently defaulting.
    internal static ConnectionMethod? ParseMethod(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (Enum.TryParse<ConnectionMethod>(value, ignoreCase: true, out var method))
        {
            return method;
        }

        Console.Error.WriteLine($"Error: unknown method '{value}'. Valid: Auto, WebSocket, Https");
        return null;
    }

    // Maps checkbox-style values to bool. An empty string (unchecked MSI checkbox) is false.
    // Unrecognised values return null so the caller leaves the field unchanged.
    internal static bool? ParseBool(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" or "" => false,
            null => null,
            _ => null
        };

    internal static int? ParseInt(string? value) =>
        int.TryParse(value, out var n) ? n : null;

    // Recovers a filesystem path passed on an MSI custom-action command line. A WiX directory property such as
    // DATAFOLDER resolves with a trailing backslash (e.g. "D:\Jenkins\"); once wrapped in quotes on the command
    // line the closing \" is parsed by CommandLineToArgvW as an escaped quote, so the process actually receives
    // 'D:\Jenkins"'. Strip that stray trailing quote and normalise a trailing separator so the stored path is
    // clean (drive roots like "D:\" are preserved). Returns null when nothing meaningful remains.
    internal static string? SanitizePathArgument(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var sanitized = value.Trim().TrimEnd('"').Trim();
        return sanitized.Length == 0 ? null : Path.TrimEndingDirectorySeparator(sanitized);
    }
}
