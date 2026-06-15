// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public interface IConnectivityChecker
{
    Task Check(string host, int port, int timeoutMs, CancellationToken ct);
}
