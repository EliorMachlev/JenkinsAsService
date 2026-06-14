// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public interface IJarDownloader
{
    Task DownloadAsync(string jenkinsUrl, string destinationPath, CancellationToken ct);
}
