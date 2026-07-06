// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Transport the Jenkins inbound agent uses to reach the controller.
/// </summary>
public enum ConnectionMethod
{
    /// <summary>
    /// Try <see cref="WebSocket"/> first and fall back to <see cref="Https"/> if a connection dies before
    /// stabilising; whichever transport stays up is kept. Self-correcting if one transport is blocked.
    /// </summary>
    Auto,

    /// <summary>WebSocket transport (<c>agent.jar -webSocket</c>) — tunnels over the HTTP(S) port; proxy/firewall friendly.</summary>
    WebSocket,

    /// <summary>Direct TCP inbound (no <c>-webSocket</c>) — the classic JNLP inbound port the controller advertises.</summary>
    Https
}
