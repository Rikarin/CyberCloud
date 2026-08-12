using CyberCloud.Core.Resources;

namespace CyberCloud.Kubernetes.Connections;

/// <summary>
///     <see cref="IKubeClusterConnection" /> over <see cref="IClusterConnectionGrain" /> — what a
///     reconciler passes to <c>KubeCommand.For(...)</c>.
/// </summary>
/// <remarks>
///     <para>
///         The indirection is what keeps docs/plan/03 § Assembly graph rules rule 3 true for
///         providers. A reconciler needs a "connection" to build a command against; if that were the
///         grain interface itself, the reconciler would be coupled to the grain's whole surface, and
///         a reconciler unit test would need an Orleans cluster to build a command.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The grain key is built by <see cref="GrainKeys.ClusterConnection" /> and the grain is
///             resolved from the UNQUALIFIED factory.
///         </b>
///         A cluster connection is null-tenant
///         (docs/plan/06 § Grain keys), so calling <c>ForTenant(...)</c> here would silently create a
///         second activation per tenant of a grain there is supposed to be exactly one of — which is
///         precisely the failure <c>NullTenantGrainTests.TenantQualifyingAPlatformGrainIsRefusedRatherThanSilentlyForked</c>
///         exists to make loud for the other two platform grains.
///     </para>
/// </remarks>
public sealed class ClusterConnectionHandle(IGrainFactory grains, Guid clusterId) : IKubeClusterConnection {
    /// <inheritdoc />
    public Guid ClusterId => clusterId;

    /// <summary>The grain this handle forwards to.</summary>
    public IClusterConnectionGrain Grain =>
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
}
