// Copyright (c) 2024 All rights reserved // NOSONAR

namespace JenkinsAsService;

public sealed class TelemetrySettings
{
    public bool Enabled { get; set; }
    public string OtlpEndpoint { get; set; } = "";
    public string ServiceName { get; set; } = "JenkinsAsService";
}
