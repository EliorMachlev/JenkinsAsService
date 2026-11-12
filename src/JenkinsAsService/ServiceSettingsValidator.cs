// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Validates the mandatory <see cref="ServiceSettings"/> required to start the agent: the controller URL
/// (present, parseable, with an explicit non-default port) and a non-empty secret.
/// </summary>
internal static class ServiceSettingsValidator
{
    internal static void Validate(ServiceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Connection.Url))
        {
            throw new InvalidOperationException("'Connection:Url' is a mandatory field.");
        }

        if (string.IsNullOrWhiteSpace(settings.Secret.Value))
        {
            throw new InvalidOperationException("'Secret:Value' is a mandatory field.");
        }

        // Validate the URL format up front with a friendly message (consistent with the other checks),
        // rather than letting a raw UriFormatException surface later from the worker/HTTP client. The rule
        // itself lives in ControllerUrl, shared with update-secret's verification before the value is written.
        var problem = ControllerUrl.Check(settings.Connection.Url);
        if (problem != ControllerUrl.Problem.None)
        {
            throw new InvalidOperationException(
                $"Connection:Url {ControllerUrl.Describe(problem)}. Example: {ControllerUrl.Example}");
        }
    }
}
