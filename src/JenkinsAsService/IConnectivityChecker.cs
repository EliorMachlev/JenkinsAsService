// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public interface IConnectivityChecker
{
    Task TestAsync(string host, int port, int timeoutMs, CancellationToken ct);
}
