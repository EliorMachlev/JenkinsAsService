// Copyright (c) 2024 All rights reserved

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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
          --help                  Show this help
        """;

    // Env var used to pass impersonation password in silent mode (avoids command-line exposure)
    private const string ImpersonatePasswordEnv = "JAS_IMPERSONATE_PASSWORD";

    private const string ConfigFileName = "appsettings.json";
    private const string ConfigSectionName = ConfigKeys.Section;
    private const string EventLogSource = EventLogSourceInstaller.DefaultSource;
    private const string EventLogName = EventLogSourceInstaller.DefaultLogName;
    private const char DomainSeparator = '\\';
    private const string LocalDomain = ".";

    // LogonUser logon-type / provider constants (advapi32)
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

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
                state.SetDataDir = Next(args, ref i);
                return true;
            case "--agent-name":
                state.AgentName = Next(args, ref i);
                return true;
            case "--java-path":
                state.JavaPath = Next(args, ref i);
                return true;
            case "--username":
                state.Username = Next(args, ref i);
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
        var configPath = Path.Combine(basePath, ConfigFileName);
        Console.WriteLine($"Configuration written to {configPath} (mode: {args.Mode.Value})");
        return 0;
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
            RunImpersonated(args.Username, password, () =>
                SecretWriter.WriteConfig(basePath, secret, args.Mode!.Value, args.Url!, args.AgentName, args.JavaPath,
                    args.DpapiScope ?? DpapiScope.Machine, args.Thumbprint, args.ServiceAccount));
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        var configPath = Path.Combine(basePath, ConfigFileName);
        Console.WriteLine($"Configuration written to {configPath} (mode: {args.Mode!.Value})");
        return 0;
    }

    // ─── Interactive mode ─────────────────────────────────────────────────────

    private static int RunInteractive(string basePath, ParseState args)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        TryReadExistingConfig(basePath, configPath,
            out var existingServer, out var existingAgentName, out var existingJavaPath, out var existingMode);

        var server = PromptServer(args.Url, existingServer);
        if (server is null)
        {
            return 1;
        }

        var secret = PromptSecret(args.SecretArg, args.SecretFile, args.SecretEnv);
        if (secret is null)
        {
            return 1;
        }

        var selectedMode = PromptMode(args.Mode, existingMode);
        var agentName = PromptText("Agent name", args.AgentName, existingAgentName, "hostname");
        var javaPath = PromptText("Java path", args.JavaPath, existingJavaPath, "JAVA_HOME");

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
                var credentials = PromptImpersonationCredentials(args.Username);
                if (credentials is null)
                {
                    return 1;
                }

                RunImpersonated(credentials.Value.Username, credentials.Value.Password, () =>
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

    // ─── Prompt helpers ───────────────────────────────────────────────────────

    private static string? PromptServer(string? argServer, string? existingServer)
    {
        if (!string.IsNullOrWhiteSpace(argServer))
        {
            return argServer;
        }

        var def = string.IsNullOrWhiteSpace(existingServer) ? "" : $" [{existingServer}]";
        Console.Write($"Jenkins URL (with port){def}: ");
        var input = Console.ReadLine()?.Trim();
        var server = string.IsNullOrWhiteSpace(input) ? existingServer : input;

        if (string.IsNullOrWhiteSpace(server))
        {
            Console.Error.WriteLine("Error: Jenkins URL is required.");
            return null;
        }

        return server;
    }

    private static string? PromptSecret(string? secretArg, string? secretFile, string? secretEnv)
    {
        var secret = ResolveSecretInput(secretArg, secretFile, secretEnv);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }

        Console.Write("Agent secret: ");
        secret = ReadMaskedInput();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine("Error: Agent secret is required.");
            return null;
        }

        return secret;
    }

    private static SecretMode PromptMode(SecretMode? modeArg, SecretMode existingMode)
    {
        if (modeArg.HasValue)
        {
            return modeArg.Value;
        }

        Console.WriteLine();
        Console.WriteLine("Secret protection mode:");
        Console.WriteLine("  1. DPAPI — machine-scoped encryption (recommended)");
        Console.WriteLine("  2. Environment Variable — system env var");
        Console.WriteLine("  3. Credential Manager — Windows credential vault");
        Console.WriteLine("  4. Unprotected — plaintext (dev only)");
        Console.WriteLine("  5. TPM — hardware-backed, non-exportable key (strongest; needs TPM 2.0)");
        Console.Write("Select [1-5]: ");
        return Console.ReadLine()?.Trim() switch
        {
            "1" => SecretMode.Dpapi,
            "2" => SecretMode.EnvironmentVariable,
            "3" => SecretMode.CredentialManager,
            "4" => SecretMode.Unprotected,
            "5" => SecretMode.Tpm,
            _ => existingMode
        };
    }

    private static string? PromptText(string label, string? argValue, string? existing, string fallbackLabel)
    {
        if (argValue is not null)
        {
            return argValue;
        }

        var def = string.IsNullOrWhiteSpace(existing) ? fallbackLabel : existing;
        Console.Write($"{label} (default: {def}): ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(input) ? existing : input;
    }

    private static (string Username, string Password)? PromptImpersonationCredentials(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Write("Impersonate as (e.g. DOMAIN\\ServiceAccount): ");
            username = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Error.WriteLine("Error: username is required for impersonation.");
            return null;
        }

        Console.Write($"Password for {username}: ");
        var password = ReadMaskedInput();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Error: password is required for impersonation.");
            return null;
        }

        return (username, password);
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
                // best-effort delete — if it fails the file remains but operation continues
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

    private static string ReadMaskedInput()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                return sb.ToString(); // stdin redirected — return what was captured
            }

            if (key.Key == ConsoleKey.Enter)
            {
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
            }
            else
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    // ─── Impersonation ────────────────────────────────────────────────────────

    // Runs action under the identity of the given local/domain account.
    // The account must have "Log on locally" rights on this machine.
    // Used for Credential Manager mode so secrets are stored in the service account's vault.
    private static void RunImpersonated(string username, string password, Action action)
    {
        var domain = LocalDomain;
        var user = username;

        if (username.Contains(DomainSeparator))
        {
            var parts = username.Split(DomainSeparator, 2);
            domain = parts[0];
            user = parts[1];
        }

        if (!NativeMethods.LogonUser(user, domain, password,
                Logon32LogonInteractive, Logon32ProviderDefault, out var token))
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"LogonUser failed for '{username}' (Win32 error {err}). " +
                "Verify the credentials and that the account has 'Log on locally' rights.");
        }

        using (token)
        {
            WindowsIdentity.RunImpersonated(token, action);
        }
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool LogonUser(
            string lpszUsername,
            string lpszDomain,
            string lpszPassword,
            int dwLogonType,
            int dwLogonProvider,
            out SafeAccessTokenHandle phToken);
    }
}
