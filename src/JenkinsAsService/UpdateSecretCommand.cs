using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace JenkinsAsService;

public static class UpdateSecretCommand
{
    private const string Usage = """
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

    public static int Run(string[] args, string? basePath = null)
    {
        basePath ??= AppContext.BaseDirectory;

        if (Array.Exists(args, a => a is "--help" or "-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

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
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }

        if (silent)
            return RunSilent(basePath, secretArg, secretFile, secretEnv, url, mode, agentName, javaPath, impersonate, username);

        return RunInteractive(basePath, secretArg, secretFile, secretEnv, url, mode, agentName, javaPath, impersonate, username);
    }

    // ─── Silent mode ────────────────────────────────────────────────────────

    private static int RunSilent(string basePath,
        string? secretArg, string? secretFile, string? secretEnv,
        string? url, SecretMode? mode,
        string? agentName, string? javaPath,
        bool impersonate, string? username)
    {
        var secret = ResolveSecretInput(secretArg, secretFile, secretEnv);

        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine("Error: --secret, --secret-file, or --secret-env is required in --silent mode.");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Error: --url is required in --silent mode.");
            return 1;
        }
        if (mode is null)
        {
            Console.Error.WriteLine("Error: --mode is required in --silent mode.");
            return 1;
        }

        if (impersonate)
        {
            if (string.IsNullOrWhiteSpace(username))
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
                RunImpersonated(username, password, () =>
                    SecretWriter.WriteConfig(basePath, secret, mode.Value, url, agentName, javaPath));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }
        else
        {
            SecretWriter.WriteConfig(basePath, secret, mode.Value, url, agentName, javaPath);
        }

        return 0;
    }

    // ─── Interactive mode ────────────────────────────────────────────────────

    private static int RunInteractive(string basePath,
        string? secretArg, string? secretFile, string? secretEnv,
        string? url, SecretMode? mode,
        string? agentName, string? javaPath,
        bool impersonate, string? username)
    {
        string existingUrl = "", existingAgentName = "", existingJavaPath = "";
        SecretMode existingMode = SecretMode.Dpapi;

        var configPath = Path.Combine(basePath, "appsettings.json");
        if (File.Exists(configPath))
        {
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(basePath)
                    .AddJsonFile("appsettings.json", optional: true)
                    .Build();
                var section = config.GetSection("Jenkins");
                existingUrl = section["JenkinsURL"] ?? "";
                existingAgentName = section["AgentName"] ?? "";
                existingJavaPath = section["JavaPath"] ?? "";
                if (Enum.TryParse<SecretMode>(section["SecretMode"], out var parsed))
                    existingMode = parsed;
            }
            catch { /* Corrupt config — use defaults */ }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            var def = string.IsNullOrWhiteSpace(existingUrl) ? "" : $" [{existingUrl}]";
            Console.Write($"Jenkins URL (with port){def}: ");
            var input = Console.ReadLine()?.Trim();
            url = string.IsNullOrWhiteSpace(input) ? existingUrl : input;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Error: Jenkins URL is required.");
            return 1;
        }

        var secret = ResolveSecretInput(secretArg, secretFile, secretEnv);
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Write("Agent secret: ");
            secret = ReadMaskedInput();
            Console.WriteLine();
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine("Error: Agent secret is required.");
            return 1;
        }

        if (mode is null)
        {
            Console.WriteLine();
            Console.WriteLine("Secret protection mode:");
            Console.WriteLine("  1. DPAPI — machine-scoped encryption (recommended)");
            Console.WriteLine("  2. Environment Variable — system env var");
            Console.WriteLine("  3. Credential Manager — Windows credential vault");
            Console.WriteLine("  4. Unprotected — plaintext (dev only)");
            Console.Write($"Select [1-4] (default: {(int)existingMode + 1}): ");
            mode = Console.ReadLine()?.Trim() switch
            {
                "1" => SecretMode.Dpapi,
                "2" => SecretMode.EnvironmentVariable,
                "3" => SecretMode.CredentialManager,
                "4" => SecretMode.Unprotected,
                _   => existingMode
            };
        }

        if (agentName is null)
        {
            var def = string.IsNullOrWhiteSpace(existingAgentName) ? "hostname" : existingAgentName;
            Console.Write($"Agent name (default: {def}): ");
            var input = Console.ReadLine()?.Trim();
            agentName = string.IsNullOrWhiteSpace(input) ? existingAgentName : input;
        }

        if (javaPath is null)
        {
            var def = string.IsNullOrWhiteSpace(existingJavaPath) ? "JAVA_HOME" : existingJavaPath;
            Console.Write($"Java path (default: {def}): ");
            var input = Console.ReadLine()?.Trim();
            javaPath = string.IsNullOrWhiteSpace(input) ? existingJavaPath : input;
        }

        // Impersonation: prompt for credentials if --impersonate and not already provided
        string? impersonatePassword = null;
        if (impersonate)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                Console.Write("Impersonate as (e.g. DOMAIN\\ServiceAccount): ");
                username = Console.ReadLine()?.Trim();
            }
            if (string.IsNullOrWhiteSpace(username))
            {
                Console.Error.WriteLine("Error: username is required for impersonation.");
                return 1;
            }
            Console.Write($"Password for {username}: ");
            impersonatePassword = ReadMaskedInput();
            Console.WriteLine();
            if (string.IsNullOrWhiteSpace(impersonatePassword))
            {
                Console.Error.WriteLine("Error: password is required for impersonation.");
                return 1;
            }
        }

        try
        {
            if (impersonate && impersonatePassword is not null)
            {
                RunImpersonated(username!, impersonatePassword, () =>
                    SecretWriter.WriteConfig(basePath, secret, mode.Value, url, agentName, javaPath));
            }
            else
            {
                SecretWriter.WriteConfig(basePath, secret, mode.Value, url, agentName, javaPath);
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

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string? ResolveSecretInput(string? secretArg, string? secretFile, string? secretEnv)
    {
        if (secretArg is not null) return secretArg;

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
        if (++i < args.Length) return args[i];
        Console.Error.WriteLine($"Error: {args[i - 1]} requires a value.");
        return null;
    }

    // Returns null (instead of defaulting to Dpapi) so callers can fail explicitly on invalid input.
    internal static SecretMode? ParseMode(string? value)
    {
        if (value is null) return null;
        if (Enum.TryParse<SecretMode>(value, ignoreCase: true, out var mode))
            return mode;
        Console.Error.WriteLine($"Error: unknown mode '{value}'. Valid: Dpapi, EnvironmentVariable, CredentialManager, Unprotected");
        return null;
    }

    private static string ReadMaskedInput()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { break; } // stdin redirected
            if (key.Key == ConsoleKey.Enter) break;
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
        string domain = ".";
        string user = username;

        if (username.Contains('\\'))
        {
            var parts = username.Split('\\', 2);
            domain = parts[0];
            user = parts[1];
        }

        const int Logon32LogonInteractive = 2;
        const int Logon32ProviderDefault  = 0;

        if (!NativeMethods.LogonUser(user, domain, password,
                Logon32LogonInteractive, Logon32ProviderDefault, out var token))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"LogonUser failed for '{username}' (Win32 error {err}). " +
                "Verify the credentials and that the account has 'Log on locally' rights.");
        }

        using (token)
            WindowsIdentity.RunImpersonated(token, action);
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
