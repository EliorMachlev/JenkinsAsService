// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Transport-selection logic for the inbound agent: which transport to start on, how to flip for
/// <c>Auto</c> fallback, and whether a given agent exit should trigger that fallback. Pure functions —
/// the worker holds the effective-transport state and applies these decisions.
/// </summary>
internal static class AgentTransport
{
    /// <summary>The transport to attempt first: <c>Https</c> stays direct TCP inbound; <c>Auto</c> and
    /// <c>WebSocket</c> both start on WebSocket (only <c>Auto</c> later falls back).</summary>
    internal static ConnectionMethod InitialMethod(ConnectionMethod configured) =>
        configured == ConnectionMethod.Https ? ConnectionMethod.Https : ConnectionMethod.WebSocket;

    /// <summary>Flips between the two transports for <c>Auto</c> fallback.</summary>
    internal static ConnectionMethod Toggle(ConnectionMethod current) =>
        current == ConnectionMethod.WebSocket ? ConnectionMethod.Https : ConnectionMethod.WebSocket;

    /// <summary>
    /// True when the watchdog — not the Java agent — should own reconnection, so the agent is launched with
    /// <c>-noReconnect</c> and exits on a failed handshake instead of retrying internally forever. Only
    /// <c>Auto</c> needs this: that exit is what drives the WebSocket ↔ direct-TCP fallback
    /// (<see cref="ShouldFallback"/>). Fixed transports keep the agent's own (faster) internal reconnect,
    /// since there is no alternate transport to fall back to.
    /// </summary>
    internal static bool UsesWatchdogReconnect(ConnectionMethod configured) =>
        configured == ConnectionMethod.Auto;

    /// <summary>
    /// True only for <c>Auto</c> when a <em>real</em> agent process exited before reaching the stability
    /// window. A run that stabilised, or a pseudo-exit from a recovery skipped due to an unreachable
    /// controller (<paramref name="agentActuallyExited"/> = false), must not trigger a transport switch.
    /// </summary>
    internal static bool ShouldFallback(
        ConnectionMethod configured, bool reachedStability, bool agentActuallyExited) =>
        configured == ConnectionMethod.Auto && agentActuallyExited && !reachedStability;
}
