// Copyright (c) 2024 All rights reserved // NOSONAR

namespace JenkinsAsService;

public enum SecretMode
{
    Unprotected,
    Dpapi,
    EnvironmentVariable,
    CredentialManager
}
