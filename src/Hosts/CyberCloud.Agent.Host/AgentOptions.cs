namespace CyberCloud.Agent.Host;

/// <summary>
///     What the chart tells the agent — bound from <c>CyberCloud:Agent</c>, which the chart sets as
///     <c>CyberCloud__Agent__*</c> environment variables.
/// </summary>
public sealed class AgentOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:Agent";

    /// <summary>The WebSocket URL to dial — <c>wss://{gateway}/agent/v1/tunnel</c>. Required.</summary>
    public string TunnelEndpoint { get; set; } = string.Empty;

    /// <summary>The connected-cluster resource's id, <c>D</c> form. Required.</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    ///     Where the chart mounts the one-time enrollment token. Read only when no credential has
    ///     been stored yet; absent after the first successful connection has spent it.
    /// </summary>
    public string EnrollmentTokenFile { get; set; } = "/var/run/cybercloud/enrollment-token";

    /// <summary>How often to heartbeat until the platform's welcome says otherwise.</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>The longest the reconnect backoff grows to.</summary>
    public int MaxReconnectSeconds { get; set; } = 60;

    /// <summary>The cluster id, parsed. <see cref="Guid.Empty" /> when the setting is missing or malformed.</summary>
    public Guid ParsedClusterId => Guid.TryParseExact(ClusterId, "D", out var id) ? id : Guid.Empty;

    /// <summary>Whether the two required settings are present.</summary>
    public bool IsConfigured => TunnelEndpoint.Length > 0 && ParsedClusterId != Guid.Empty;
}
