// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

// Console prompt primitives for the interactive `update-secret` flow. Pure input I/O — no
// config-write and no secret-source resolution (the command resolves --secret/-file/-env first
// and passes the result to PromptSecret). Keeping these here isolates raw-console handling
// (masked input, key interception) from the command's dispatch and write orchestration. The
// verification Retry / Skip / Cancel exchange takes its reader and writers as parameters, so it is
// unit-tested without a console.
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

    /// <summary>What the operator chose after a failed verification check.</summary>
    internal enum VerificationChoice
    {
        Retry,
        Skip,
        Cancel
    }

    /// <summary>Outcome of <see cref="VerifyWithRetry"/>: whether to write, and the values to write.</summary>
    internal sealed record VerifiedValues(bool Proceed, string Url, string? JavaPath);

    /// <summary>
    /// Verifies the URL and Java path the interactive flow is about to write. On a failed check it shows the
    /// reason and offers Retry (re-ask that value - Enter keeps it, to re-run the check once the controller is
    /// back), Skip (write that value unchecked; the other check still runs) or Cancel (write nothing). Input and
    /// output are parameters so the whole exchange is testable without a console.
    /// </summary>
    internal static VerifiedValues VerifyWithRetry(UpdateSecretVerifier verifier, string url, string? javaPath,
        TextReader input, TextWriter output, TextWriter error)
    {
        var skipped = new HashSet<VerifiedValue>();
        while (true)
        {
            var failure = verifier.Verify(
                skipped.Contains(VerifiedValue.Url) ? null : url,
                skipped.Contains(VerifiedValue.JavaPath) ? null : javaPath).FirstOrDefault();
            if (failure is null)
            {
                return new VerifiedValues(true, url, javaPath);
            }

            error.WriteLine($"Error: {failure.Message}");
            switch (PromptVerificationChoice(input, output))
            {
                case VerificationChoice.Retry when failure.Value == VerifiedValue.Url:
                    // Shown without userinfo: the URL is not a secret, credentials embedded in it are.
                    output.Write($"Jenkins URL (with port) [{ControllerUrl.WithoutUserInfo(url)}] (Enter keeps it): ");
                    url = KeepIfBlank(input.ReadLine(), url);
                    break;
                case VerificationChoice.Retry:
                    output.Write($"Java path [{javaPath}] (Enter keeps it, - clears it to use JAVA_HOME): ");
                    var answer = input.ReadLine()?.Trim();
                    javaPath = answer == "-" ? null : KeepIfBlank(answer, javaPath!);
                    break;
                case VerificationChoice.Skip:
                    output.WriteLine($"Verification of {OptionName(failure.Value)} skipped; it will be written unchecked.");
                    skipped.Add(failure.Value);
                    break;
                default:
                    error.WriteLine("Cancelled; nothing was written.");
                    return new VerifiedValues(false, url, javaPath);
            }
        }
    }

    /// <summary>
    /// Asks Retry / Skip / Cancel until it gets one. End of input is Cancel: with nobody left to answer, the
    /// one safe outcome is to write nothing.
    /// </summary>
    internal static VerificationChoice PromptVerificationChoice(TextReader input, TextWriter output)
    {
        while (true)
        {
            output.Write("[R]etry, [S]kip this check, or [C]ancel without writing? ");
            var answer = input.ReadLine();
            switch (answer?.Trim().ToLowerInvariant())
            {
                case null:
                    output.WriteLine();
                    return VerificationChoice.Cancel;
                case "r" or "retry":
                    return VerificationChoice.Retry;
                case "s" or "skip":
                    return VerificationChoice.Skip;
                case "c" or "cancel":
                    return VerificationChoice.Cancel;
                default:
                    output.WriteLine("Please answer R, S or C.");
                    break;
            }
        }
    }

    private static string KeepIfBlank(string? answer, string current) =>
        string.IsNullOrWhiteSpace(answer) ? current : answer.Trim();

    private static string OptionName(VerifiedValue value) => value == VerifiedValue.Url ? "--url" : "--java-path";

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
