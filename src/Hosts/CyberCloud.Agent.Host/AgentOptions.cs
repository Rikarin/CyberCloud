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

    /// <summary>
    ///     The Secret, in the pod's own namespace, the credential is kept in. The chart's
    ///     <c>cluster.credentialSecretName</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Has to be the name the chart's <c>Role</c> scopes to, and this is how it gets
    ///         there.
    ///     </b> <c>rbac.yaml</c> grants <c>get</c>, <c>update</c> and <c>patch</c> on exactly
    ///     <c>cluster.credentialSecretName</c> and <c>create</c> on any (Kubernetes RBAC cannot
    ///     scope a create). The first cut exposed the value and never passed it in, so an agent
    ///     under an overridden name created a Secret it could never read again — a lockout on the
    ///     first pod restart, in the one store an enrollment cannot be recovered without.
    /// </remarks>
    public string CredentialSecretName { get; set; } = "cybercloud-agent-credential";

    /// <summary>How often to heartbeat until the platform's welcome says otherwise.</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>The longest the reconnect backoff grows to.</summary>
    public int MaxReconnectSeconds { get; set; } = 60;

    /// <summary>
    ///     How long to wait between attempts to store a credential the Secret write refused. Not
    ///     one of the chart's values; a test shortens it.
    /// </summary>
    public int CredentialStoreRetrySeconds { get; set; } = 30;

    /// <summary>The cluster id, parsed. <see cref="Guid.Empty" /> when the setting is missing or malformed.</summary>
    public Guid ParsedClusterId => Guid.TryParseExact(ClusterId, "D", out var id) ? id : Guid.Empty;

    /// <summary>Whether the two required settings are present.</summary>
    public bool IsConfigured => TunnelEndpoint.Length > 0 && ParsedClusterId != Guid.Empty;
}
