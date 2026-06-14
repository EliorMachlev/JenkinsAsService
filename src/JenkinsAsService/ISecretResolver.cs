// Copyright (c) 2024 All rights reserved // NOSONAR

namespace JenkinsAsService;

public interface ISecretResolver
{
    string Resolve(ServiceSettings settings);
}
