// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public static class UpdateSecretCommand
{
    internal const string Name = "update-secret";

    // One row per option: the single place a name, its value parser and its target field are stated. The
    // aliases below are derived from these names, so an option cannot be parseable yet have no short form.
    // Raw is null when the value was missing (already reported by Next) and empty when the installer field
    // was untouched; ParseOrFail is the one statement of what each means.
    private delegate void ValueHandler(ParseState state, string option, string? raw);

    // mitigations is first, deliberately: allocation order decides who gets the shorter alias (see
    // CliAliases), and this puts --m on mitigations ahead of --mode and --method.
    private static readonly (string Name, ValueHandler Apply)[] ValueOptions =
    [
        ("--mitigations", (s, o, v) => s.Mitigations = ParseOrFail(s, o, v, ParseMitigations)),
        ("--secret", (s, _, v) => s.SecretArg = v),
        ("--secret-file", (s, _, v) => s.SecretFile = v),
        ("--secret-env", (s, _, v) => s.SecretEnv = v),
        ("--url", (s, _, v) => s.Url = v),
        ("--mode", (s, o, v) => s.Mode = ParseOrFail(s, o, v, ParseMode)),
        ("--dpapi-scope", (s, o, v) => s.DpapiScope = ParseOrFail(s, o, v, ParseDpapiScope)),
        ("--thumbprint", (s, _, v) => s.Thumbprint = v),
        ("--service-account", (s, _, v) => s.ServiceAccount = v),
        ("--set-data-dir", (s, _, v) => s.SetDataDir = SanitizePathArgument(v)),
        ("--agent-name", (s, _, v) => s.AgentName = v),
        ("--java-path", (s, _, v) => s.JavaPath = SanitizePathArgument(v)),
        ("--username", (s, _, v) => s.Username = v),
        ("--method", (s, o, v) => s.Method = ParseOrFail(s, o, v, ParseMethod)),
        ("--via-file", (s, o, v) => s.ViaFile = ParseOrFail(s, o, v, ParseBool, TrueOrFalse)),
        ("--custom-args", (s, _, v) => s.CustomArgs = v),
        ("--sanitize-env", (s, o, v) => s.SanitizeEnv = ParseOrFail(s, o, v, ParseBool, TrueOrFalse)),
        ("--allowed-env", (s, _, v) => s.AllowedEnv = v),
        ("--debug", (s, o, v) => s.DebugMode = ParseOrFail(s, o, v, ParseBool, TrueOrFalse)),
        ("--compact-log", (s, o, v) => s.CompactLog = ParseOrFail(s, o, v, ParseBool, TrueOrFalse)),
        ("--retained-logs", (s, o, v) => s.RetainedLogs = ParseOrFail(s, o, v, ParseInt, WholeNumber)),
        ("--max-retries", (s, o, v) => s.MaxRetries = ParseOrFail(s, o, v, ParseInt, WholeNumber)),
    ];

    private static readonly (string Name, Action<ParseState> Apply)[] FlagOptions =
    [
        ("--silent", s => s.Silent = true),
        ("--impersonate", s => s.Impersonate = true),
        ("--merge", s => s.Merge = true),
        ("--upgrade", s => s.Upgrade = true),
    ];

    private static readonly Dictionary<string, ValueHandler> ValueHandlers =
        ValueOptions.ToDictionary(o => o.Name, o => o.Apply, StringComparer.Ordinal);

    private static readonly Dictionary<string, Action<ParseState>> FlagHandlers =
        FlagOptions.ToDictionary(o => o.Name, o => o.Apply, StringComparer.Ordinal);

    // The secret inputs keep their full names only. Three options sharing one prefix made the short forms
    // (--s / --se / --sec) easy to swap, and the one carrying a plaintext secret is the last place to guess.
    private static bool IsSecretOption(string name) => name.StartsWith("--secret", StringComparison.Ordinal);

    // Long option -> short alias (--m for --mitigations), allocated in table order. The --secret* options
    // are reserved but get no alias - see CliAliases.
    internal static readonly CliAliases Aliases = new(
        ValueOptions.Select(o => o.Name).Concat(FlagOptions.Select(o => o.Name)).Append("--help")
            .Where(name => !IsSecretOption(name)),
        ValueOptions.Select(o => o.Name).Where(IsSecretOption));

    internal const string UsageText = """
        Usage: JenkinsAsService update-secret [options]

        Options:
          --secret <value>                        The Jenkins agent secret (plaintext)
          --secret-file <path>                    Read secret from file (file is deleted after reading)
          --secret-env <var>                      Read secret from named environment variable
                                                  The three secret options have no short alias, deliberately.
          --u, --url <value>                      Jenkins controller address (include port)
          --mo, --mode <value>                    Protection mode: Dpapi, Tpm, EnvironmentVariable, CredentialManager, Unprotected
                                                  Tpm encrypts with a non-exportable TPM-resident key (pass --service-account
                                                  so the service can decrypt at runtime).
          --d, --dpapi-scope <value>              DPAPI scope when --mode Dpapi: Machine (default) or User.
                                                  User-scope requires writing as the service account (see --impersonate).
          --t, --thumbprint <value>               SHA-256 thumbprint of the Jenkins controller cert to pin (jar download)
          --ser, --service-account <acct>         Service account (e.g. 'NT SERVICE\Jenkins') granted read on the
                                                  written config; appsettings.json ACL is hardened to deny Users.
          --set, --set-data-dir <path>            Only set Jenkins:DataDirectory in appsettings.json, then exit (used
                                                  by the installer; the writable data dir, separate from the binary).
          --a, --agent-name <value>               Agent node label (default: hostname)
          --j, --java-path <value>                Java installation directory (default: JAVA_HOME)
          --i, --impersonate                      Run Credential Manager write as a different user account.
                                                  Interactive: prompts for username and password.
                                                  Silent: requires --username; password from env JAS_IMPERSONATE_PASSWORD.
          --us, --username <value>                Windows account for impersonation (DOMAIN\account)
          --si, --silent                          Non-interactive; requires --secret/--secret-file/--secret-env, --url, --mode
          --mer, --merge                          Merge only the optional fields below into an existing appsettings.json
                                                  (no secret written). Used by the installer's advanced custom actions.
                                                  Without --merge, the optional fields below are applied by the full
                                                  write too (silent or interactive). --set-data-dir, and --upgrade when
                                                  it preserves an existing secret, change nothing else.
          --me, --method <value>                  Connection transport: Auto, WebSocket, Https
          --v, --via-file <bool>                  Pass secret to agent via file (true/false)
          --c, --custom-args <value>              Extra java.exe arguments
          --sa, --sanitize-env <bool>             Sanitize the agent child environment (true/false)
          --al, --allowed-env <value>             Extra env var names to pass through (semicolon/comma separated)
          --m, --mitigations <digits>             Image-load mitigations applied to the service, and inherited by the
                                                  build, as three 0/1 digits: no-remote, no-low-label, prefer-System32.
                                                  "111" (default) applies all three; "011" drops only the network-DLL
                                                  block (a build loading tools or DLLs from a share); "101" drops only
                                                  the low-integrity block; "000" applies none.
          --up, --upgrade                         Reconcile appsettings.json to the current schema on MSI upgrade:
                                                  add new settings at their defaults, prune settings the schema no longer
                                                  defines, and preserve existing values and the secret. If no secret is
                                                  present, falls back to a full write from --secret/--url/--mode.
          --de, --debug <bool>                    Verbose Java agent logging (true/false)
          --co, --compact-log <bool>              Compact JSON log format (true/false)
          --r, --retained-logs <int>              Number of rolled log files to keep
          --ma, --max-retries <int>               Max auto-recovery attempts (0 = infinite)
          --h, --help                             Show this help
        """;

    // Env var used to pass impersonation password in silent mode (avoids command-line exposure)
    private const string ImpersonatePasswordEnv = "JAS_IMPERSONATE_PASSWORD";

    // What an option accepts, for the two parsers that do not name their own valid values. Each is used
    // by several options, and this text is what an operator sees on a typo.
    private const string TrueOrFalse = "true or false";
    private const string WholeNumber = "a whole number";

    private const string ConfigFileName = ConfigKeys.FileName;
    private const string ConfigSectionName = ConfigKeys.Section;
    private const string EventLogSource = EventLogSourceInstaller.DefaultSource;
    private const string EventLogName = EventLogSourceInstaller.DefaultLogName;

    // Mutable accumulator used by ParseArgs; returned directly to avoid a 10-parameter constructor.
    // Internal so tests can assert what was parsed rather than how the argument array looks afterwards.
    internal sealed class ParseState
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
        public MitigationMask? Mitigations;
        public bool? DebugMode;
        public bool? CompactLog;
        public int? RetainedLogs;
        public int? MaxRetries;

        /// <summary>
        /// Set when an option was supplied with a value that could not be used. Parsing continues so the
        /// operator sees every bad option at once rather than one per run, but the command then exits 1.
        /// </summary>
        public bool Failed;
    }

    public static int Run(string[] args)
    {
        return Run(args, null);
    }

    public static int Run(string[] args, string? basePath)
    {
        basePath ??= AppContext.BaseDirectory;

        if (CliCommands.WantsHelp(args))
        {
            Console.WriteLine(UsageText);
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
            SecretWriter.MergeConfig(basePath, OptionalFields(parsed));
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

    // The optional fields as one value, for --merge AND the full write. Built in one place so an option
    // cannot be honoured by one path and parsed-then-dropped by the other, which is what --mitigations did.
    private static ConfigMergeFields OptionalFields(ParseState s) => new()
    {
        Method = s.Method,
        DpapiScope = s.DpapiScope,
        ViaFile = s.ViaFile,
        CustomArguments = s.CustomArgs,
        SanitizeEnvironment = s.SanitizeEnv,
        AllowedEnvironmentVariables = s.AllowedEnv,
        ProcessMitigations = s.Mitigations,
        DebugMode = s.DebugMode,
        CompactLog = s.CompactLog,
        RetainedLogs = s.RetainedLogs,
        MaxRetries = s.MaxRetries
    };

    // ─── Argument parsing ────────────────────────────────────────────────────

    internal static ParseState? ParseArgs(string[] args)
    {
        var state = new ParseState();
        for (int i = 1; i < args.Length; i++)
        {
            // Resolved here, at an option position only: a value that happens to read "--m" is
            // consumed by Next() and never reaches this line. Resolved into a local, never written back:
            // the array belongs to the caller.
            var option = Aliases.Resolve(args[i]);
            if (!TryApplyValueArg(option, args, ref i, state) && !TryApplyFlagArg(option, state))
            {
                // The token is not echoed: an unrecognised argument is as likely to be a misplaced value
                // (a secret passed without its option) as a misspelled option.
                Console.Error.WriteLine($"Error: argument {i} is not a recognised option.");
                Console.Error.WriteLine(UsageText);
                return null;
            }
        }

        // A rejected value is fatal, not something to shrug off and carry on from. These run as MSI
        // deferred custom actions whose Return is the default "check", so exiting 0 after ignoring a
        // mistyped option installs a configuration the operator did not ask for and reports success -
        // the one outcome worse than a failed install, because nothing prompts anyone to look.
        return state.Failed ? null : state;
    }

    private static bool TryApplyValueArg(string option, string[] args, ref int i, ParseState state)
    {
        // The resolved name is passed in, read ONCE before Next() advances i: it is what the error message
        // must report, and re-reading it per message is how "--debug" ends up reporting itself as "--method".
        if (!ValueHandlers.TryGetValue(option, out var apply))
        {
            return false;
        }

        apply(state, option, Next(option, args, ref i, state));
        return true;
    }

    private static bool TryApplyFlagArg(string arg, ParseState state)
    {
        if (!FlagHandlers.TryGetValue(arg, out var apply))
        {
            return false;
        }

        apply(state);
        return true;
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
            args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount, OptionalFields(args));
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
                    args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount, OptionalFields(args)));
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
                        args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount, OptionalFields(args)));
            }
            else
            {
                SecretWriter.WriteConfig(basePath, secret, selectedMode, server, agentName, javaPath,
                    args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount, OptionalFields(args));
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

        // None of the messages below names the file or the variable. The rule for invalid input is to say
        // which option failed and why, never to echo what was given - and here what was given is a pointer
        // to the secret. ex.Message is left out for the same reason: IO exceptions embed the path.
        if (secretFile is not null)
        {
            if (!File.Exists(secretFile))
            {
                Console.Error.WriteLine("Error: --secret-file: the file does not exist.");
                return null;
            }

            string secret;
            try
            {
                secret = File.ReadAllText(secretFile, System.Text.Encoding.UTF8).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: --secret-file: the file could not be read ({ex.GetType().Name}).");
                return null;
            }

            try
            {
                File.Delete(secretFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort delete — the operation continues, but a plaintext secret is now left on disk,
                // so surface it: the caller should remove the file manually.
                Console.Error.WriteLine(
                    $"Warning: --secret-file: the file was read but could not be deleted ({ex.GetType().Name}). "
                    + "Delete it manually.");
            }

            return secret;
        }

        if (secretEnv is not null)
        {
            var secret = Environment.GetEnvironmentVariable(secretEnv);
            if (string.IsNullOrWhiteSpace(secret))
            {
                Console.Error.WriteLine("Error: --secret-env: the named environment variable is not set or empty.");
                return null;
            }

            return secret;
        }

        return null;
    }

    private static string? Next(string option, string[] args, ref int i, ParseState state)
    {
        if (++i < args.Length)
        {
            return args[i];
        }

        Console.Error.WriteLine($"Error: {option} requires a value.");
        state.Failed = true;
        return null;
    }

    /// <summary>
    /// Applies a value parser and records a rejected value on <paramref name="state"/>, which makes the
    /// command exit 1.
    /// <para>
    /// THE ONE STATEMENT OF THE UNSET RULE, which every value option obeys. <c>null</c> (option absent, or
    /// its value missing - already reported by <see cref="Next"/>) and empty are NOT errors: every
    /// <c>[PROPERTY]</c> in a custom-action command line is quoted so that an unset optional setting
    /// arrives as <c>""</c>, and rejecting that would fail an install that did nothing wrong. A non-empty
    /// value the parser rejects IS an error, and must not be silently dropped - these run as deferred
    /// custom actions with the default <c>Return="check"</c>, so exiting 0 after ignoring a typo installs a
    /// configuration nobody asked for and calls it success.
    /// </para>
    /// </summary>
    /// <param name="expected">
    /// What the option accepts, for parsers that do not print their own message (bool and int). Enum
    /// parsers pass <c>null</c> here because they already list their valid values.
    /// </param>
    private static T? ParseOrFail<T>(ParseState state, string option, string? raw,
        Func<string?, T?> parse, string? expected = null)
        where T : struct
    {
        var parsed = parse(raw);
        if (parsed is not null || string.IsNullOrWhiteSpace(raw))
        {
            return parsed;
        }

        if (expected is not null)
        {
            // The rejected value is not echoed: name the option and what it accepts, nothing more.
            Console.Error.WriteLine($"Error: {option} expects {expected}.");
        }

        state.Failed = true;
        return null;
    }

    /// <summary>
    /// Shared body of every enum-valued option. Null on anything not recognised, so a caller fails
    /// explicitly instead of silently defaulting to whatever the enum's zero value happens to be. Empty is
    /// "unset" and is silent, per <see cref="ParseOrFail"/>; the guard is repeated here because these
    /// parsers are also called directly, including by tests. Where the setting is genuinely required
    /// (<c>--mode</c>), the required-field check names it, which is the more useful message anyway.
    /// </summary>
    private static T? ParseEnumOption<T>(string? value, string option, string valid)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        Console.Error.WriteLine($"Error: --{option}: unrecognised value. Valid: {valid}");
        return null;
    }

    internal static SecretMode? ParseMode(string? value) =>
        ParseEnumOption<SecretMode>(value, "mode",
            "Dpapi, Tpm, EnvironmentVariable, CredentialManager, Unprotected");

    internal static DpapiScope? ParseDpapiScope(string? value) =>
        ParseEnumOption<DpapiScope>(value, "dpapi-scope", "Machine, User");

    /// <summary>
    /// The three-digit mitigation mask. Empty is "unset" and silent, per <see cref="ParseOrFail"/>; a
    /// non-empty value that is not three 0/1 digits names the format rather than being ignored.
    /// </summary>
    internal static MitigationMask? ParseMitigations(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (MitigationMask.TryParse(value, out var mask))
        {
            return mask;
        }

        // Level names from the previous build (Full / AllowNetworkImages / Off) are deliberately NOT accepted
        // here: they are migrated when found in a stored config, but a new command line states digits.
        Console.Error.WriteLine(
            "Error: --mitigations: expected three characters, each 0 or 1. Valid: digits in the order "
            + "no-remote, no-low-label, prefer-System32, e.g. 111, 011 or 101.");
        return null;
    }

    internal static ConnectionMethod? ParseMethod(string? value) =>
        ParseEnumOption<ConnectionMethod>(value, "method", "Auto, WebSocket, Https");

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
