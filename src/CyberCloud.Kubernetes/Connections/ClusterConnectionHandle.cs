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
///         precisely the failure
///         <c>NullTenantGrainTests.TenantQualifyingAPlatformGrainIsRefusedRatherThanSilentlyForked</c>
///         exists to make loud for the other two platform grains.
///     </para>
/// </remarks>
/// <param name="grains">The grain factory the handle forwards through.</param>
/// <param name="clusterId">The cluster this handle addresses.</param>
/// <param name="attach">
///     Opens a terminal once the grain has allowed it, or <see langword="null" /> for a handle that
///     serves requests only — which refuses <see cref="AttachAsync" /> by name.
/// </param>
public sealed class ClusterConnectionHandle(IGrainFactory grains, Guid clusterId, IKubeAttachDialer? attach = null)
    : IKubeClusterConnection {
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

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListNamespaceAsync(
        string ns,
        CancellationToken cancellationToken = default
    ) =>
        Grain.ListNamespaceAsync(ns);

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        CancellationToken cancellationToken = default
    ) =>
        Grain.ListAsync(kind, ns, labelSelector);

    /// <inheritdoc />
    public Task<Result> SetOwnerAsync(
        ObjectRef target,
        OwnerRef? owner,
        CancellationToken cancellationToken = default
    ) =>
        Grain.SetOwnerAsync(target, owner);

    /// <inheritdoc />
    public Task<Result<IKubeTerminal>> AttachAsync(
        ObjectRef pod,
        string container,
        CancellationToken cancellationToken = default
    ) =>
        attach is null
            ? Task.FromResult(
                Result<IKubeTerminal>.Failure(
                    ErrorCode.InternalError,
                    $"This handle to cluster {clusterId:D} was built without an IKubeAttachDialer, so "
                    + $"it cannot attach to '{pod}'. AddCyberCloudKubernetes registers one."
                )
            )
            : attach.AttachAsync(clusterId, pod, container, cancellationToken);

    /// <inheritdoc />
    public Task<Result<string>> ReadLogsAsync(
        ObjectRef pod,
        string container,
        int tailLines,
        CancellationToken cancellationToken = default
    ) =>
        Grain.ReadLogsAsync(pod, container, tailLines);
}
