using System.Globalization;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     A Kubernetes group/version/kind, plus the <b>plural</b> the REST path needs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The plural is carried, not derived, and docs/plan/09 omits it.</b> The sketch at
///         docs/plan/09 § Cluster connections passes a <c>GroupVersionKind</c> to <c>WatchAsync</c>,
///         but every Kubernetes REST path is
///         <c>/apis/{group}/{version}/namespaces/{ns}/{plural}/{name}</c> — it is keyed by the
///         <i>plural resource name</i>, not by the kind. The two are related by convention and not by
///         a function: English pluralisation is not computable (<c>Endpoints</c> pluralises to
///         <c>endpoints</c>, <c>NetworkPolicy</c> to <c>networkpolicies</c>), and a
///         <c>CustomResourceDefinition</c> may declare <i>any</i> plural it likes. Guessing it is the
///         kind of bug that works for ninety types and 404s on the ninety-first, so it is required
///         input. A provider knows its own plurals; API discovery would be the alternative and is a
///         round trip per kind per cluster.
///     </para>
///     <para>
///         <b>The core group is the empty string.</b> <c>v1 Pod</c> is
///         <c>Group = ""</c>, <c>Version = "v1"</c>, and its path is <c>/api/v1/…</c> rather than
///         <c>/apis//v1/…</c>. <see cref="IsCoreGroup" /> is that test.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.GroupVersionKind")]
public sealed record GroupVersionKind {
    /// <summary>The API group, or the empty string for the core group.</summary>
    [Id(0)]
    public string Group { get; init; } = string.Empty;

    /// <summary>The API version within the group, for example <c>v1</c>.</summary>
    [Id(1)]
    public string Version { get; init; } = string.Empty;

    /// <summary>The kind, for example <c>Deployment</c>. What goes in the object's <c>kind</c>.</summary>
    [Id(2)]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The lower-case plural resource name the REST path uses, for example <c>deployments</c>.</summary>
    [Id(3)]
    public string Plural { get; init; } = string.Empty;

    /// <summary>Whether this is the core group, whose paths are <c>/api/…</c> not <c>/apis/…</c>.</summary>
    public bool IsCoreGroup => Group.Length == 0;

    /// <summary>The object's <c>apiVersion</c> field: <c>group/version</c>, or bare <c>version</c> for core.</summary>
    public string ApiVersion => IsCoreGroup ? Version : Group + "/" + Version;

    /// <summary>Whether every component needed to address this kind is present.</summary>
    public bool IsComplete => Version.Length > 0 && Kind.Length > 0 && Plural.Length > 0;

    /// <inheritdoc />
    public override string ToString() => ApiVersion + " " + Kind + " (" + Plural + ")";
}

/// <summary>The address of one object in one cluster.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.ObjectRef")]
public sealed record ObjectRef {
    /// <summary>Which kind.</summary>
    [Id(0)]
    public GroupVersionKind Kind { get; init; } = new();

    /// <summary>The namespace, or the empty string for a cluster-scoped object.</summary>
    [Id(1)]
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The object's name.</summary>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether this addresses a cluster-scoped object.</summary>
    public bool IsClusterScoped => Namespace.Length == 0;

    /// <inheritdoc />
    public override string ToString() => IsClusterScoped ? $"{Kind.Kind}/{Name}" : $"{Kind.Kind}/{Namespace}/{Name}";
}

/// <summary>
///     An object read back from a cluster, as JSON.
/// </summary>
/// <remarks>
///     ⚠ <b>JSON and not a <c>k8s.Models</c> type, and that is rule 3.</b> docs/plan/09
///     § Cluster connections sketches
///     <c>
///         Task&lt;Result&lt;T&gt;&gt; GetAsync&lt;T&gt;(ObjectRef)
///         where T : IKubernetesObject
///     </c>
///     . That constraint cannot be honoured: it puts
///     <c>k8s.Models.IKubernetesObject</c> in this assembly's public surface and therefore in the
///     compile-time closure of every caller — which is exactly what docs/plan/03 § Assembly graph
///     rules rule 3 forbids ("no assembly above <c>CyberCloud.Kubernetes</c> references
///     <c>k8s.Models</c>"). The two statements are in direct conflict and rule 3 wins, because it is
///     the one with a build gate behind it. Typed access to <c>k8s.Models</c> stays inside
///     <c>CyberCloud.Kubernetes</c>, where it is legal.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.KubeObject")]
public sealed record KubeObject {
    /// <summary>What the object is.</summary>
    [Id(0)]
    public ObjectRef Ref { get; init; } = new();

    /// <summary>The object's JSON, exactly as the API server returned it.</summary>
    [Id(1)]
    public string Json { get; init; } = "{}";

    /// <summary>
    ///     The object's <c>metadata.resourceVersion</c> — the cursor an informer resumes from.
    /// </summary>
    [Id(2)]
    public string ResourceVersion { get; init; } = string.Empty;
}

/// <summary>
///     One object found by a listing, reduced to what a caller can decide with: what it is, what it
///     is called, and what it is labelled.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No body, and that is the point rather than an economy.</b> The one caller — a
///         namespace reclaim — asks <i>is there anything here</i> and <i>is it ours</i>. A body would
///         make this a copy of the namespace over a grain call, and every field in it would be a
///         field somebody could be tempted to join the control plane against, which is exactly the
///         join that is blind to the unlabelled objects the reclaim exists to see.
///     </para>
///     <para>
///         ⚠ <b><see cref="Labels" /> is absent-as-empty, and here that is safe in the only direction
///         it matters.</b> An object whose <c>metadata.labels</c> is missing reads as carrying no
///         <c>managed-by</c>, so it counts as somebody else's — the conservative answer. The
///         opposite mapping (absent as "no labels means ours") is the one that would authorise a
///         delete, and it is not expressible here.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.KubeObjectSummary")]
public sealed record KubeObjectSummary {
    /// <summary>The kind it was listed as.</summary>
    [Id(0)]
    public GroupVersionKind Kind { get; init; } = new();

    /// <summary>The namespace it was found in.</summary>
    [Id(1)]
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The object's name.</summary>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Its labels, exactly as the API server holds them. Empty when it has none.</summary>
    [Id(3)]
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public override string ToString() => $"{Kind.Kind}/{Name}";
}

/// <summary>
///     One field another manager owns that we tried to set — the raw material of a drift event.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.FieldConflict")]
public sealed record FieldConflict {
    /// <summary>The field path the API server named, for example <c>.spec.replicas</c>.</summary>
    [Id(0)]
    public string Field { get; init; } = string.Empty;

    /// <summary>The field manager that owns it — <c>kubectl-edit</c>, a tenant's controller, …</summary>
    [Id(1)]
    public string OwnedBy { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => $"{Field} owned by '{OwnedBy}'";
}

/// <summary>
///     A drift event with a name — ADR-013's "<i>that</i> becomes a drift event with a name".
/// </summary>
/// <remarks>
///     This is what a conflict turns into rather than an exception, so that docs/plan/08's drift
///     detection has something to consume and the portal has something to show that names the field
///     and the manager that took it.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.DriftEvent")]
public sealed record DriftEvent {
    /// <summary>The resource whose desired state lost the field.</summary>
    [Id(0)]
    public Guid ResourceId { get; init; }

    /// <summary>The cluster it happened in.</summary>
    [Id(1)]
    public Guid ClusterId { get; init; }

    /// <summary>The object that was being applied.</summary>
    [Id(2)]
    public ObjectRef Target { get; init; } = new();

    /// <summary>Our field manager — the one that was refused.</summary>
    [Id(3)]
    public string FieldManager { get; init; } = string.Empty;

    /// <summary>Every field the API server reported a conflict on.</summary>
    [Id(4)]
    public IReadOnlyList<FieldConflict> Conflicts { get; init; } = [];

    /// <summary>When it was detected.</summary>
    [Id(5)]
    public DateTimeOffset DetectedAt { get; init; }

    /// <summary>A one-line human summary, for a portal row and a log line.</summary>
    public string Describe() =>
        $"{Conflicts.Count} field(s) on {Target} are owned by another manager and were not "
        + $"overwritten: {string.Join(", ", Conflicts)}";
}

/// <summary>The result of one apply.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.ApplyOutcome")]
public sealed record ApplyOutcome {
    /// <summary>What happened.</summary>
    [Id(0)]
    public ApplyResult Result { get; init; } = ApplyResult.Unknown;

    /// <summary>What was applied.</summary>
    [Id(1)]
    public ObjectRef Target { get; init; } = new();

    /// <summary>The object's <c>resourceVersion</c> after the apply, when there was one.</summary>
    [Id(2)]
    public string ResourceVersion { get; init; } = string.Empty;

    /// <summary>
    ///     The drift event, when <see cref="Result" /> is <see cref="ApplyResult.Conflict" />.
    ///     <see langword="null" /> otherwise.
    /// </summary>
    [Id(3)]
    public DriftEvent? Drift { get; init; }

    /// <summary>
    ///     A human-facing explanation. For <see cref="ApplyResult.Suspended" /> this is the "cannot
    ///     reach your cluster" text docs/plan/09 § Cluster connections asks the portal to show.
    /// </summary>
    [Id(4)]
    public string Message { get; init; } = string.Empty;

    /// <summary>The <c>sha256:…</c> of the body that was applied, for cheap no-op detection next time.</summary>
    [Id(5)]
    public string ReconcileHash { get; init; } = string.Empty;
}

/// <summary>Connection health — docs/plan/09 § Cluster connections.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.ClusterHealth")]
public sealed record ClusterHealth {
    /// <summary>The cluster.</summary>
    [Id(0)]
    public Guid ClusterId { get; init; }

    /// <summary>Healthy, Degraded, or never pinged.</summary>
    [Id(1)]
    public ClusterHealthState State { get; init; } = ClusterHealthState.Unknown;

    /// <summary>When a ping last succeeded. Default when none ever has.</summary>
    [Id(2)]
    public DateTimeOffset LastSuccessAt { get; init; }

    /// <summary>When a ping was last attempted.</summary>
    [Id(3)]
    public DateTimeOffset LastAttemptAt { get; init; }

    /// <summary>How many pings have failed in a row. Zero when the last one succeeded.</summary>
    [Id(4)]
    public int ConsecutiveFailures { get; init; }

    /// <summary>Why, when <see cref="State" /> is not <see cref="ClusterHealthState.Healthy" />.</summary>
    [Id(5)]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    ///     Whether reconciles against this cluster are suspended — true exactly when
    ///     <see cref="State" /> is <see cref="ClusterHealthState.Degraded" />.
    /// </summary>
    public bool ReconcilesSuspended => State == ClusterHealthState.Degraded;
}

/// <summary>
///     A lease on a shared informer — what <c>WatchAsync</c> hands back so a caller knows the watch
///     is established and which cursor it is at.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.InformerLease")]
public sealed record InformerLease {
    /// <summary>The cluster the informer runs against.</summary>
    [Id(0)]
    public Guid ClusterId { get; init; }

    /// <summary>The kind being watched.</summary>
    [Id(1)]
    public GroupVersionKind Kind { get; init; } = new();

    /// <summary>
    ///     The label selector in force. Always includes
    ///     <see cref="KubeLabels.ManagedBySelector" /> — docs/plan/09 § Observing.
    /// </summary>
    [Id(2)]
    public string LabelSelector { get; init; } = string.Empty;

    /// <summary>
    ///     The <c>resourceVersion</c> the informer has consumed up to. This is the cursor a
    ///     re-establishment resumes from rather than re-listing — docs/plan/09 § Observing.
    /// </summary>
    [Id(3)]
    public string ResourceVersion { get; init; } = string.Empty;

    /// <summary>The stream events are published to — <c>cc.{tenant}.k8s.{cluster}.{kind}</c>.</summary>
    [Id(4)]
    public string StreamNamespace { get; init; } = string.Empty;

    /// <summary>How many callers hold this shared informer. One informer per kind, not per caller.</summary>
    [Id(5)]
    public int Subscribers { get; init; }

    /// <summary>
    ///     How long re-establishment was staggered by — docs/plan/09 § Observing's mitigation for the
    ///     synchronized list storm. Zero on a first establishment.
    /// </summary>
    [Id(6)]
    public TimeSpan StaggerDelay { get; init; }
}

/// <summary>
///     Everything the platform needs to reach one cluster, and the tenant that owns it.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="OwningTenantId" /> is the whole security model of this grain.</b>
///     <c>IClusterConnectionGrain</c> is null-tenant (docs/plan/06 § Grain keys), so its key carries
///     no tenant and Orleans' own separation cannot help. This field, held as grain <i>state</i> and
///     checked on every call, is the substitute — "the single place tenancy is enforced by code
///     rather than by key".
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.ClusterConnectionDescriptor")]
public sealed record ClusterConnectionDescriptor {
    /// <summary>The cluster resource's GUID. Also the grain key — <c>cluster/{clusterId:N}</c>.</summary>
    [Id(0)]
    public Guid ClusterId { get; init; }

    /// <summary>The tenant that owns this cluster. Checked on every call.</summary>
    [Id(1)]
    public Guid OwningTenantId { get; init; }

    /// <summary>How the platform authenticates.</summary>
    [Id(2)]
    public ClusterConnectionKind Kind { get; init; } = ClusterConnectionKind.Unknown;

    /// <summary>
    ///     Where the credential lives — a Vault path, never the credential itself.
    /// </summary>
    /// <remarks>
    ///     ⚠ A kubeconfig is a credential. docs/plan/23 § The architecture gates has a "Secrets" gate
    ///     banning <c>[Id]</c> members named <c>*Secret</c>/<c>*Token</c>/<c>*Key</c> outside the
    ///     vault assembly; carrying the kubeconfig here would put a cluster-admin credential into
    ///     grain state, a PostgreSQL row and every trace of a grain call. A reference is carried and
    ///     resolved at connect time.
    /// </remarks>
    [Id(3)]
    public string CredentialRef { get; init; } = string.Empty;

    /// <summary>The API server URL, for display and for the <see cref="ClusterConnectionKind.ServiceAccountToken" /> kind.</summary>
    [Id(4)]
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>A human-facing display name, for the portal and for log lines.</summary>
    [Id(5)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    ///     Whether this cluster is the platform's own — docs/plan/09 § The platform's own cluster.
    ///     Self-managed resources are excluded from tenant-facing reconciliation.
    /// </summary>
    [Id(6)]
    public bool SelfManaged { get; init; }

    /// <summary>The cluster's grain key within the (null) tenant — <c>cluster/{clusterId:N}</c>.</summary>
    public string GrainKey => GrainKeys.ClusterConnection(ClusterId);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"cluster {ClusterId:D} ({Kind}) owned by tenant {OwningTenantId:D}"
        );
}
