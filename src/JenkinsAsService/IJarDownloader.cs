// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public interface IJarDownloader
{
    Task Download(Uri jenkinsUri, string destinationPath, CancellationToken ct);
}
