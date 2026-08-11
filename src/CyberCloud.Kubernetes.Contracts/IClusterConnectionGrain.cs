namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     One cluster's connection: the live client, the informers, and the tenancy check that a
///     null-tenant grain key cannot make for it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § Cluster connections and docs/plan/06 § Grain keys. The key is
///         <c>cluster/{clusterId:N}</c> and it is <b>null-tenant</b> — there is exactly one
///         activation per cluster platform-wide, because the grain holds a live client and a set of
///         watches, and because a cluster shared between a tenant and the platform (which every
///         in-house cluster is while it is being created) would otherwise have two.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every method here is reachable from every tenant, and that is not a bug — it is the
///             cost of the null-tenant key.
///         </b>
///         <c>PlatformCrossTenantAuthorizer</c> allows any tenant →
///         null-tenant edge and logs it, precisely because the target grain is expected to enforce
///         tenancy itself. docs/plan/06 § Grain keys:
///         <i>
///             "The grain therefore carries the owning
///             tenant as state and checks it on every call … This is the single place tenancy is enforced
///             by code rather than by key, and it is called out here so nobody has to discover it."
///         </i>
///         The implementation's <c>EnsureCallerMayReach</c> is that check, it runs first in every
///         method, and <c>ClusterConnectionTenancyTests</c> asserts there is no method without it.
///     </para>
///     <para>
///         <b>The caller's tenant is not a parameter.</b> It is read from the ambient grain-call
///         context, stamped by <c>ClusterConnectionTenantFilter</c> from
///         <c>IIncomingGrainCallContext.SourceId</c>, because a caller-supplied tenant id is a claim
///         and not a fact — tenant B would pass tenant A's GUID. See that filter's remarks for
///         what happens when the source is a client rather than a grain.
///     </para>
///     <para>
///         ⚠ <b>Differences from the sketch at docs/plan/09 § Cluster connections</b>, both forced by
///         docs/plan/03 § Assembly graph rules rule 3:
///         <c>
///             GetAsync&lt;T&gt;(ObjectRef) where T :
///             IKubernetesObject
///         </c>
///         returns <see cref="KubeObject" /> (JSON) instead, and the generic
///         constraint is gone. A grain interface is referenced by the gateway and by every provider,
///         so a <c>k8s.Models</c> constraint here would put Kubernetes types in the compile-time
///         closure of the whole platform.
///     </para>
/// </remarks>
[Alias("K8s.ClusterConnection")]
public interface IClusterConnectionGrain : IGrainWithStringKey {
    /// <summary>
    ///     Registers or updates what this connection points at, and who owns it.
    /// </summary>
    /// <param name="descriptor">The cluster, its owning tenant, and how to authenticate.</param>
    /// <remarks>
    ///     ⚠ The <b>first</b> attach establishes the owning tenant. A later attach that changes
    ///     <see cref="ClusterConnectionDescriptor.OwningTenantId" /> is refused unless the caller is
    ///     the current owner or a platform operator — otherwise "check the owning tenant on every
    ///     call" would be defeated by one call that rewrites the owner.
    /// </remarks>
    [Alias("Attach")]
    Task<Result> AttachAsync(ClusterConnectionDescriptor descriptor);

    /// <summary>Pings the API server and updates <see cref="ClusterHealth" />.</summary>
    [Alias("Ping")]
    Task<Result<ClusterHealth>> PingAsync();

    /// <summary>The last known health, without touching the network.</summary>
    [Alias("GetHealth")]
    Task<Result<ClusterHealth>> GetHealthAsync();

    /// <summary>Applies a command server-side, under its field manager.</summary>
    /// <param name="command">The built, fully-labelled command.</param>
    [Alias("Apply")]
    Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command);

    /// <summary>Deletes the object a command addresses.</summary>
    /// <param name="command">The built command.</param>
    /// <param name="policy">How to cascade.</param>
    [Alias("Delete")]
    Task<Result> DeleteAsync(KubeCommand command, CascadePolicy policy);

    /// <summary>Reads one object back, as JSON.</summary>
    /// <param name="target">What to read.</param>
    [Alias("Get")]
    Task<Result<KubeObject>> GetAsync(ObjectRef target);

    /// <summary>
    ///     Establishes (or joins) the shared informer for a kind, filtered by
    ///     <see cref="KubeLabels.ManagedBySelector" />.
    /// </summary>
    /// <param name="kind">The kind to watch.</param>
    /// <param name="labelSelector">
    ///     An extra selector, ANDed with <see cref="KubeLabels.ManagedBySelector" />, which is always
    ///     applied — docs/plan/09 § Observing.
    /// </param>
    [Alias("Watch")]
    Task<Result<InformerLease>> WatchAsync(GroupVersionKind kind, string labelSelector);

    /// <summary>The leases currently held, one per watched kind.</summary>
    [Alias("ListInformers")]
    Task<Result<IReadOnlyList<InformerLease>>> ListInformersAsync();
}
