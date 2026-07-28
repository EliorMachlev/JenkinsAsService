// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

// Console prompt primitives for the interactive `update-secret` flow. Pure input I/O — no
// config-write and no secret-source resolution (the command resolves --secret/-file/-env first
// and passes the result to PromptSecret). Keeping these here isolates raw-console handling
// (masked input, key interception) from the command's dispatch and write orchestration.
internal static class InteractiveConfigPrompts
{
    public static string? PromptServer(string? argServer, string? existingServer)
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

    // Falls back to a masked prompt only when the non-interactive secret sources produced nothing.
    public static string? PromptSecret(string? preResolvedSecret)
    {
        if (!string.IsNullOrWhiteSpace(preResolvedSecret))
        {
            return preResolvedSecret;
        }

        Console.Write("Agent secret: ");
        var secret = ReadMaskedInput();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine("Error: Agent secret is required.");
            return null;
        }

        return secret;
    }

    public static SecretMode PromptMode(SecretMode? modeArg, SecretMode existingMode)
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

    public static string? PromptText(string label, string? argValue, string? existing, string fallbackLabel)
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

    public static (string Username, string Password)? PromptImpersonationCredentials(string? username)
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
}
