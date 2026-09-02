// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// The <c>configure-recovery</c> CLI command: sets the SCM failure actions on the installed service.
/// A verb because the MSI invokes it as an EXE custom action (see the installer .wixproj), and because an
/// operator needs it after a manual install or to restore actions someone has cleared.
/// </summary>
internal static class ConfigureRecoveryCommand
{
    internal const string Name = "configure-recovery";

    /// <summary>The service name the installer registers; matches ServiceInstall/@Name in Service.wxs.</summary>
    internal const string DefaultServiceName = "Jenkins";

    private const string UsageText = """
        Usage: JenkinsAsService configure-recovery [--service <name>]

        Configures the Windows Service Control Manager to restart the service on each of its first three
        consecutive failures, 10 seconds apart, resetting the failure count after a day without one.

        This is the OUTER half of the recovery story. The service's own watchdog restarts a crashed Jenkins
        agent; these actions cover the service process itself dying, and the case where the watchdog gives
        up after Recovery:MaxRetries and stops the service deliberately so SCM can take over.

        The MSI applies this for you. Run it by hand after a manual (non-MSI) install, or to put the
        actions back after they have been cleared.

        Options:
          --service <name>        Service to configure (default: Jenkins)
          --help                  Show this help
        """;

    /// <summary>Runs the command. Returns a process exit code.</summary>
    internal static int Run(string[] args)
    {
        if (CliCommands.WantsHelp(args))
        {
            Console.WriteLine(UsageText);
            return 0;
        }

        var (serviceName, error) = ParseArguments(args);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        return ServiceRecoveryConfigurator.Configure(serviceName!, Console.WriteLine) ? 0 : 1;
    }

    /// <summary>Parses <c>--service</c>, defaulting to the name the installer registers.</summary>
    internal static (string? ServiceName, string? Error) ParseArguments(string[] args)
    {
        var serviceName = DefaultServiceName;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case Name:
                    break;
                case "--service" when i + 1 < args.Length:
                    serviceName = CliCommands.TrimQuoted(args[++i]);
                    break;
                case "--service":
                    return (null, $"Error: {args[i]} requires a value.");
                default:
                    return (null, $"Error: unknown argument '{args[i]}'.");
            }
        }

        return string.IsNullOrWhiteSpace(serviceName)
            ? (null, "Error: --service requires a non-empty value.")
            : (serviceName, null);
    }
}
