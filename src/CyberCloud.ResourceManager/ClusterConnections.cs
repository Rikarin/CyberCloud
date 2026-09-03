namespace CyberCloud.ResourceManager;

/// <summary>
///     The <see cref="IClusterConnectionRegistrar" /> that writes to the connection grain.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>In this assembly and not in <c>CyberCloud.Kubernetes</c>, and the reason is a cycle
///         rather than a preference.</b> The obvious home is beside <c>ClusterConnectionHandle</c>,
///         which is where the grain's other caller lives — but <c>module-layering.txt</c> already
///         carries <c>CyberCloud.ResourceManager -&gt; CyberCloud.Kubernetes</c>, so the reverse edge
///         would be two modules that have become one, and rule 7 refuses a cycle. What makes this
///         legal here is that <c>IClusterConnectionGrain</c> is a <i>contract</i>: this assembly
///         already references <c>CyberCloud.Kubernetes.Contracts</c> for
///         <see cref="IKubeClusterConnection" />, and no <c>k8s.Models</c> type is bound anywhere on
///         this path, which is what rule 3 is about.
///     </para>
///     <para>
///         ⚠ <b>The grain reference is unqualified, and that is <c>CC1006</c>'s documented second
///         form rather than a hole in it.</b> A cluster connection is null-tenant
///         (docs/plan/06 § Grain keys) — there is exactly one activation per cluster platform-wide,
///         because the grain holds a live client and a set of watches. Calling <c>ForTenant</c> here
///         would fork one per tenant. The rule's own message names the alternative: <i>"or a
///         <c>GrainKeys</c> null-tenant key for a platform grain"</i>, which
///         <see cref="GrainKeys.ClusterConnection" /> is. <c>ClusterConnectionHandle</c> takes the same
///         route for the same reason.
///     </para>
///     <para>
///         ⚠ <b>The grain enforces the tenancy this key cannot.</b> Its <c>EnsureCallerMayReach</c>
///         runs first in every method and reads the caller's tenant from the ambient grain-call
///         context, so a first attach establishes the owner and a later one that changes it is refused
///         unless the caller is that owner or a platform operator. Nothing here re-implements that,
///         and nothing here may.
///     </para>
/// </remarks>
/// <param name="grains">The grain factory.</param>
public sealed class GrainClusterConnectionRegistrar(IGrainFactory grains) : IClusterConnectionRegistrar {
    /// <inheritdoc />
    public Task<Result> AttachAsync(
        ClusterConnectionDescriptor descriptor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        return grains
            .GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(descriptor.ClusterId))
            .AttachAsync(descriptor);
    }
}

/// <summary>
///     The <see cref="IClusterConnectionFactory" /> that hands a reconciler a handle to the connection
///     grain.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until this existed the only implementations in the tree were test fakes</b>, and
///         <see cref="NoClusterConnectionFactory" /> — which answers <see langword="null" /> always —
///         was what every host registered. So a type declaring <c>RequiresCluster</c> could not
///         reconcile in production at all: the driver refused the pass by name, which is the right
///         refusal and was not a substitute for a connection.
///     </para>
///     <para>
///         ⚠ <b>Cheap, because it is called on every reconcile pass.</b> Taking a grain reference
///         opens no socket — the socket is the grain's — so this allocates a handle and nothing else.
///     </para>
/// </remarks>
/// <param name="grains">The grain factory the handles forward through.</param>
public sealed class GrainClusterConnectionFactory(IGrainFactory grains) : IClusterConnectionFactory {
    /// <inheritdoc />
    public IKubeClusterConnection? Connect(Guid clusterId) =>
        clusterId == Guid.Empty ? null : new GrainClusterConnection(grains, clusterId);
}

/// <summary>
///     <see cref="IKubeClusterConnection" /> over <see cref="IClusterConnectionGrain" />.
/// </summary>
/// <remarks>
///     ⚠ <b>The same methods <c>CyberCloud.Kubernetes.Connections.ClusterConnectionHandle</c>
///     forwards, and the duplication is the cycle's cost.</b> That type cannot be reached from here
///     without <c>CyberCloud.ResourceManager -&gt; CyberCloud.Kubernetes</c> becoming a two-way edge.
///     The forwarding is mechanical and the interface is four methods wide, which is what makes
///     paying it cheaper than merging two modules — and <see cref="IKubeClusterConnection" /> is
///     deliberately smaller than the grain for exactly this reason.
///     <para>
///         ⚠ <b><c>ListNamespaceAsync</c> is overridden here rather than left to the interface's
///         fail-closed default, and forgetting to would have been invisible.</b> The default refuses,
///         so a resource-group delete would report "this connection cannot enumerate a namespace"
///         against the one connection type that can — a refusal, so nothing would be destroyed, but
///         also a feature that never works in production and works in every test.
///     </para>
/// </remarks>
/// <param name="grains">The grain factory.</param>
/// <param name="clusterId">The cluster this handle addresses.</param>
sealed class GrainClusterConnection(IGrainFactory grains, Guid clusterId) : IKubeClusterConnection {
    /// <inheritdoc />
    public Guid ClusterId => clusterId;

    IClusterConnectionGrain Grain =>
        grains.GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(clusterId));

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) =>
        Grain.ApplyAsync(command);

    /// <inheritdoc />
    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
        Grain.GetAsync(target);

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) =>
        Grain.DeleteAsync(command, policy);

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListNamespaceAsync(
        string ns,
        CancellationToken cancellationToken = default
    ) =>
        Grain.ListNamespaceAsync(ns);
}
