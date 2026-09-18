namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>
///     The durable half of an agent tunnel: who owns the cluster, the hashes that admit its agent,
///     and when it was last heard from.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Durable, and on <c>durable-grains.txt</c> for a reason the list's own header asks
///             for.
///         </b> The test there is "can this be rebuilt". <see cref="CredentialHash" /> cannot: the
///         plaintext exists in one place, a Secret in the tenant's cluster, and losing the hash
///         means every connected cluster's agent presents a credential the platform no longer
///         recognises. The rebuild is the tenant re-running <c>listInstallCommand</c> and
///         re-installing the agent — on every cluster, at once, after a platform incident. That is a
///         customer-visible outage rather than a cache miss.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Hashes, never plaintext, and no member here ends in <c>Token</c>, <c>Secret</c> or
///             <c>Key</c>.
///         </b> CC1005 refuses a secret-shaped member in grain state; what is here would
///         pass a review even without the rule, because a SHA-256 of a 256-bit random value admits
///         nobody. See <c>AgentCredentials</c>.
///     </para>
/// </remarks>
public sealed class AgentTunnelState {
    /// <summary>The tenant whose resource the cluster is. Set on the first arm.</summary>
    public Guid OwningTenantId { get; set; }

    /// <summary>Whether <c>ArmAsync</c> has ever run.</summary>
    public bool Armed { get; set; }

    /// <summary>The hash of the current enrollment token, or empty once it is spent.</summary>
    public string EnrollmentHash { get; set; } = string.Empty;

    /// <summary>When the enrollment token stops being accepted.</summary>
    public DateTimeOffset EnrollmentExpiresAt { get; set; }

    /// <summary>
    ///     How often the agent is told to heartbeat, from the resource's <c>heartbeatSeconds</c> at
    ///     the last arm. Zero when the arm named none, and the welcome then carries
    ///     <c>KubernetesOptions.AgentHeartbeatInterval</c>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; }

    /// <summary>The hash of the long-lived credential the agent holds, or empty before enrollment.</summary>
    public string CredentialHash { get; set; } = string.Empty;

    /// <summary>When the credential was issued.</summary>
    public DateTimeOffset CredentialIssuedAt { get; set; }

    /// <summary>When the first heartbeat ever arrived — the moment the resource became <c>Succeeded</c>.</summary>
    public DateTimeOffset FirstHeartbeatAt { get; set; }

    /// <summary>When the last heartbeat arrived.</summary>
    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>The version the agent last reported.</summary>
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>The API server version the agent last reported.</summary>
    public string KubernetesVersion { get; set; } = string.Empty;

    /// <summary>Whether the cluster's resource was deleted. A revoked tunnel admits nobody until re-armed.</summary>
    public bool Revoked { get; set; }
}
