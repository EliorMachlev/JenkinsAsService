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
          --url <value>           Jenkins controller URL (with explicit port)
          --mode <value>          Secret protection mode: Dpapi, EnvironmentVariable, CredentialManager, Unprotected
          --agent-name <value>    Agent node name (default: hostname)
          --java-path <value>     Path to Java bin folder (default: JAVA_HOME)
          --impersonate           Run Credential Manager write as a different user account.
                                  Interactive: prompts for username and password.
                                  Silent: requires --username; password from env JAS_IMPERSONATE_PASSWORD.
          --username <value>      Service account for impersonation (e.g. DOMAIN\svc_jenkins)
          --silent                Non-interactive; requires --secret/--secret-file/--secret-env, --url, --mode
          --help                  Show this help
        """;

    // Env var used to pass impersonation password in silent mode (avoids command-line exposure)
    private const string ImpersonatePasswordEnv = "JAS_IMPERSONATE_PASSWORD";

    private const string ConfigFileName = "appsettings.json";
    private const string ConfigSectionName = "Jenkins";
    private const char DomainSeparator = '\\';
    private const string LocalDomain = ".";

    // LogonUser logon-type / provider constants (advapi32)
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    private sealed record ParsedArgs(
        string? SecretArg,
        string? SecretFile,
        string? SecretEnv,
        string? Url,
        SecretMode? Mode,
        string? AgentName,
        string? JavaPath,
        bool Silent,
        bool Impersonate,
        string? Username);

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

        return parsed.Silent
            ? RunSilent(basePath, parsed)
            : RunInteractive(basePath, parsed);
    }

    // ─── Argument parsing ────────────────────────────────────────────────────

    private static ParsedArgs? ParseArgs(string[] args)
    {
        string? secretArg = null, secretFile = null, secretEnv = null;
        string? url = null, agentName = null, javaPath = null, username = null;
        SecretMode? mode = null;
        bool silent = false, impersonate = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--secret":      secretArg  = Next(args, ref i); break;
                case "--secret-file": secretFile = Next(args, ref i); break;
                case "--secret-env":  secretEnv  = Next(args, ref i); break;
                case "--url":         url        = Next(args, ref i); break;
                case "--mode":        mode       = ParseMode(Next(args, ref i)); break;
                case "--agent-name":  agentName  = Next(args, ref i); break;
                case "--java-path":   javaPath   = Next(args, ref i); break;
                case "--username":    username   = Next(args, ref i); break;
                case "--silent":      silent     = true; break;
                case "--impersonate": impersonate = true; break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    Console.Error.WriteLine(UsageText);
                    return null;
            }
        }

        return new ParsedArgs(secretArg, secretFile, secretEnv, url, mode, agentName, javaPath, silent, impersonate, username);
    }

    // ─── Silent mode ────────────────────────────────────────────────────────

    private static int RunSilent(string basePath, ParsedArgs args)
    {
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

        SecretWriter.WriteConfig(basePath, secret, args.Mode.Value, args.Url, args.AgentName, args.JavaPath);
        var configPath = Path.Combine(basePath, ConfigFileName);
        Console.WriteLine($"Configuration written to {configPath} (mode: {args.Mode.Value})");
        return 0;
    }

    private static int RunSilentImpersonated(string basePath, string secret, ParsedArgs args)
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
                SecretWriter.WriteConfig(basePath, secret, args.Mode!.Value, args.Url!, args.AgentName, args.JavaPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        var configPath = Path.Combine(basePath, ConfigFileName);
        Console.WriteLine($"Configuration written to {configPath} (mode: {args.Mode!.Value})");
        return 0;
    }

    // ─── Interactive mode ────────────────────────────────────────────────────

    private static int RunInteractive(string basePath, ParsedArgs args)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        TryReadExistingConfig(basePath, configPath,
            out var existingUrl, out var existingAgentName, out var existingJavaPath, out var existingMode);

        var url = PromptUrl(args.Url, existingUrl);
        if (url is null)
        {
            return 1;
        }

        var secret = PromptSecret(args.SecretArg, args.SecretFile, args.SecretEnv);
        if (secret is null)
        {
            return 1;
        }

        var mode = PromptMode(args.Mode, existingMode);
        var agentName = PromptText("Agent name", args.AgentName, existingAgentName, "hostname");
        var javaPath = PromptText("Java path", args.JavaPath, existingJavaPath, "JAVA_HOME");

        return WriteInteractiveConfig(basePath, configPath, secret, mode, url, agentName, javaPath, args);
    }

    private static int WriteInteractiveConfig(string basePath, string configPath,
        string secret, SecretMode mode, string url,
        string? agentName, string? javaPath, ParsedArgs args)
    {
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
                    SecretWriter.WriteConfig(basePath, secret, mode, url, agentName, javaPath));
            }
            else
            {
                SecretWriter.WriteConfig(basePath, secret, mode, url, agentName, javaPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Configuration saved to {configPath}");
        Console.WriteLine($"  Mode: {mode}");
        Console.WriteLine($"  URL:  {url}");
        return 0;
    }

    // ─── Prompt helpers ──────────────────────────────────────────────────────

    private static string? PromptUrl(string? argUrl, string? existingUrl)
    {
        if (!string.IsNullOrWhiteSpace(argUrl))
        {
            return argUrl;
        }

        var def = string.IsNullOrWhiteSpace(existingUrl) ? "" : $" [{existingUrl}]";
        Console.Write($"Jenkins URL (with port){def}: ");
        var input = Console.ReadLine()?.Trim();
        var url = string.IsNullOrWhiteSpace(input) ? existingUrl : input;

        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Error: Jenkins URL is required.");
            return null;
        }

        return url;
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
        Console.Write($"Select [1-4] (default: {(int)existingMode + 1}): ");
        return Console.ReadLine()?.Trim() switch
        {
            "1" => SecretMode.Dpapi,
            "2" => SecretMode.EnvironmentVariable,
            "3" => SecretMode.CredentialManager,
            "4" => SecretMode.Unprotected,
            _   => existingMode
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

    // ─── Shared helpers ──────────────────────────────────────────────────────

    // Reads the existing appsettings.json (if present) so interactive prompts can offer defaults.
    // Falls back to empty strings / Dpapi on a missing or corrupt file.
    private static void TryReadExistingConfig(string basePath, string configPath,
        out string existingUrl, out string existingAgentName, out string existingJavaPath,
        out SecretMode existingMode)
    {
        existingUrl = "";
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
            existingUrl = section["JenkinsURL"] ?? "";
            existingAgentName = section["AgentName"] ?? "";
            existingJavaPath = section["JavaPath"] ?? "";
            if (Enum.TryParse<SecretMode>(section["SecretMode"], out var parsed))
            {
                existingMode = parsed;
            }
        }
        catch { /* Corrupt config — use defaults */ }
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

            var s = File.ReadAllText(secretFile, System.Text.Encoding.UTF8).Trim();
            try { File.Delete(secretFile); } catch { /* best-effort delete */ }
            return s;
        }

        if (secretEnv is not null)
        {
            var s = Environment.GetEnvironmentVariable(secretEnv);
            if (string.IsNullOrWhiteSpace(s))
            {
                Console.Error.WriteLine($"Error: environment variable '{secretEnv}' is not set or empty.");
                return null;
            }

            return s;
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

        Console.Error.WriteLine($"Error: unknown mode '{value}'. Valid: Dpapi, EnvironmentVariable, CredentialManager, Unprotected");
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
                break; // stdin redirected
            }

            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Length--;
                Console.Write("\b \b");
            }
            else if (key.Key != ConsoleKey.Backspace)
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        return sb.ToString();
    }

    // ─── Impersonation ───────────────────────────────────────────────────────

    // Runs action under the identity of the given local/domain account.
    // The account must have "Log on locally" rights on this machine.
    // Used for Credential Manager mode so secrets are stored in the service account's vault.
    private static void RunImpersonated(string username, string password, Action action)
    {
        string domain = LocalDomain;
        string user = username;

        if (username.Contains(DomainSeparator))
        {
            var parts = username.Split(DomainSeparator, 2);
            domain = parts[0];
            user = parts[1];
        }

        if (!NativeMethods.LogonUser(user, domain, password,
                Logon32LogonInteractive, Logon32ProviderDefault, out var token))
        {
            int err = Marshal.GetLastWin32Error();
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
