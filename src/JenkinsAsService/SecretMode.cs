namespace JenkinsAsService;

public enum SecretMode
{
    Unprotected,
    Dpapi,
    EnvironmentVariable,
    CredentialManager
}
