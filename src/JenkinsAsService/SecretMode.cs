// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public enum SecretMode
{
    Unprotected,
    Dpapi,
    EnvironmentVariable,
    CredentialManager
}
