namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     How the platform authenticates to a cluster — the table at docs/plan/09 § Cluster connections.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.ClusterConnectionKind")]
public enum ClusterConnectionKind {
    /// <summary><c>default</c>. Not a connection kind.</summary>
    Unknown = 0,

    /// <summary>
    ///     A kubeconfig in Vault, with a service account the tenant created from our manifest. BYO
    ///     clusters; the simplest and the one that works everywhere.
    /// </summary>
    Kubeconfig = 1,

    /// <summary>
    ///     A projected, short-lived token; the platform holds only the issuer and the CA. BYO
    ///     clusters that can reach us.
    /// </summary>
    ServiceAccountToken = 2,

    /// <summary>
    ///     An agent in the tenant's cluster dials <b>out</b> to the gateway over gRPC and the platform
    ///     sends requests down that channel — for BYO clusters behind NAT with no inbound path.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>NOT IMPLEMENTED, deliberately, and the enum member exists anyway.</b> docs/plan/09
    ///     § Cluster connections budgets this at 1.5 EM in M2 and warns that it "is not optional and
    ///     is easy to defer into a crisis" — most on-prem clusters have no inbound path, so this is
    ///     the kind the brief's "connection string to kubernetes" actually needs. The member is
    ///     declared so that the enum is the document's closed set rather than the subset that happens
    ///     to be built, and so that persisted state naming it round-trips instead of failing to
    ///     deserialize when it lands. Attaching a connection of this kind fails with a message
    ///     pointing at the document.
    /// </remarks>
    AgentInitiated = 3,

    /// <summary>
    ///     Cluster API objects in the management cluster; credentials come from the CAPI-generated
    ///     kubeconfig secret. Clusters we create — docs/plan/09 § Kubernetes in Kubernetes.
    /// </summary>
    InHouse = 4
}

/// <summary>
///     Connection health — docs/plan/09 § Cluster connections, "a first-class resource property".
/// </summary>
/// <remarks>
///     ⚠ The distinction this enum carries is the point: <i>our</i> failure and <i>unreachable</i> are
///     different, and conflating them makes a tenant's network outage look like a platform bug.
///     <see cref="Degraded" /> suspends reconciles; it never fails them.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.ClusterHealthState")]
public enum ClusterHealthState {
    /// <summary>No ping has been attempted yet. Not an error, and not healthy either.</summary>
    Unknown = 0,

    /// <summary>The cluster answered a ping within the staleness window.</summary>
    Healthy = 1,

    /// <summary>
    ///     The cluster has not answered a ping for longer than the staleness window (90 seconds,
    ///     docs/plan/09 § Cluster connections). Reconciles against it are
    ///     <b>
    ///         suspended, not
    ///         failed
    ///     </b>
    ///     , and the portal says "cannot reach your cluster".
    /// </summary>
    Degraded = 2
}

/// <summary>What one server-side apply did.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.ApplyResult")]
public enum ApplyResult {
    /// <summary><c>default</c>. Not an outcome.</summary>
    Unknown = 0,

    /// <summary>The object did not exist and now does.</summary>
    Created = 1,

    /// <summary>The object existed and at least one field we own changed.</summary>
    Updated = 2,

    /// <summary>
    ///     The object existed and no field we own changed — the cheap case docs/plan/09 § The command
    ///     builder's <c>reconcile-hash</c> annotation exists to detect.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>"We own" is the whole of it, and it is narrower than "the object did not change".</b>
    ///     A controller writing <c>status</c>, or adding an annotation of its own, leaves this
    ///     <see cref="Unchanged" />: the object moved, our fields did not. <c>KubeApiClient</c> reads
    ///     ownership out of <c>metadata.managedFields</c> rather than watching
    ///     <c>metadata.resourceVersion</c>, which is shared by every writer — see
    ///     <c>OwnedFieldSnapshot</c> for why the shared cursor gets this wrong on any
    ///     controller-managed kind.
    /// </remarks>
    Unchanged = 3,

    /// <summary>
    ///     ⚠ <b>Another field manager owns a field we tried to set.</b> ADR-013's stated payoff:
    ///     "if a tenant hand-edits a field we own, the next apply reports a conflict rather than
    ///     silently reverting, and <i>that</i> becomes a drift event with a name". The apply did
    ///     <b>not</b> take effect and the field was <b>not</b> force-overwritten. See
    ///     <see cref="ApplyOutcome.Drift" />.
    /// </summary>
    Conflict = 4,

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The cluster is <see cref="ClusterHealthState.Degraded" /> and the write was not
    ///         attempted.
    ///     </b>
    ///     This is a <i>success</i> carrying "not now", not a failure: docs/plan/09
    ///     § Cluster connections requires an unreachable cluster to suspend reconciles rather than
    ///     fail them, so that a tenant's network outage does not produce a failed operation and a
    ///     "provisioning failed" in the portal.
    /// </summary>
    Suspended = 5
}

/// <summary>How a delete cascades — docs/plan/09 § The command builder, <c>WithOwner</c>.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.CascadePolicy")]
public enum CascadePolicy {
    /// <summary>
    ///     <c>Background</c> — the default. The object goes immediately and the garbage collector
    ///     deletes dependents afterwards.
    /// </summary>
    Background = 0,

    /// <summary>
    ///     <c>Foreground</c>. The object stays, with a deletion finalizer, until its dependents are
    ///     gone. Slower, and the right choice when "deleted" must mean the pods are actually stopped
    ///     — docs/plan/06 § Two-phase create's "never silently gone while its pods still run and its
    ///     meter still ticks".
    /// </summary>
    Foreground = 1,

    /// <summary><c>Orphan</c>. Dependents are left behind. Almost never what a reconciler wants.</summary>
    Orphan = 2
}
