// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public interface ISecretResolver
{
    string Resolve(ServiceSettings settings);
}
