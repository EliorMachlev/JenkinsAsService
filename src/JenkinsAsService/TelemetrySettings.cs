namespace JenkinsAsService;

public sealed class TelemetrySettings
{
    public bool Enabled { get; set; }
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
    public string ServiceName { get; set; } = "JenkinsAsService";
}
