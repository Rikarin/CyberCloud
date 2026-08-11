using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The policy evaluator a platform with no policy engine registers. Step 5, M3.
/// </summary>
/// <remarks>
///     ⚠ <b>Returns <see cref="PolicyEffect.NotSupported" /> rather than
///     <see cref="PolicyEffect.Allow" />, and the difference is what an audit log has to be able to
///     state.</b> An <see cref="PolicyEffect.Allow" /> is indistinguishable from a policy engine that
///     evaluated and permitted; <see cref="PolicyEffect.NotSupported" /> says no engine ran. The write
///     path treats both as "carry on", so the step stays in its place in the order from the first day
///     — and the ordering is the thing that must not move later.
/// </remarks>
public sealed class NotSupportedPolicyEvaluator : IPolicyEvaluator {
    /// <inheritdoc />
    public Task<PolicyDecision> EvaluateAsync(
        ResourceId id,
        string apiVersion,
        string body,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(PolicyDecision.NotSupported);
}

/// <summary>
///     The <see cref="IResourceChangedSink" /> a silo with no projector registers: it logs.
/// </summary>
/// <remarks>
///     ⚠ <b>The projector is out of scope and this is what stands in for it.</b>
///     docs/plan/08 § The resource-graph projection routes <c>resource-changed</c> to a per-tenant
///     ClickHouse table. Nothing here writes to ClickHouse. What is real is the <i>emission</i> — the
///     event is built with the projection's columns and published at step 10 — so landing a projector
///     is adding a consumer rather than changing the write path.
/// </remarks>
public sealed class LoggingResourceChangedSink(ILogger<LoggingResourceChangedSink> logger) : IResourceChangedSink {
    /// <inheritdoc />
    public Task<Result> PublishAsync(ResourceChangedEvent change, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(change);

        logger.LogInformation(
            "resource-changed {Change} {Path} on {Stream} at version {Version}",
            change.Change,
            change.ResourceId,
            change.StreamNamespace,
            change.Version
        );

        return Task.FromResult(Result.Success);
    }
}

/// <summary>
///     Resolves the effective lock at a resource's scope by walking as far as the model reaches.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06 § Tags, locks makes locks <c>CanNotDelete</c> / <c>ReadOnly</c>, <i>"inherited
///         down the hierarchy"</i>, and docs/plan/08 § The write path, end to end's step 4 reads
///         <i>"locks: CanNotDelete / ReadOnly inherited from rg, sub, mg"</i>.
///     </para>
///     <para>
///         ⚠ <b>Only the resource's own lock is implemented, and the three inherited scopes are
///         stubs.</b> <c>IResourceGroupGrain</c>, <c>ISubscriptionGrain</c> and the management-group
///         tree have no lock member — management groups have no grain at all — so there is nothing to
///         read. The consequence, stated rather than discovered: <b>a subscription-level lock does not
///         currently stop a delete.</b> Closing it is three members on two existing grain interfaces
///         plus the management-group tree, all of which belong to docs/plan/06's owner rather than to
///         the resource manager.
///     </para>
/// </remarks>
public sealed class ResourceScopeLockResolver(IGrainFactory grains) : ILockResolver {
    /// <inheritdoc />
    public async Task<Result<LockLevel>> ResolveAsync(ResourceId id, CancellationToken cancellationToken = default) {
        if (id.Id == Guid.Empty) {
            // A resource that does not exist yet carries no lock of its own, and the scopes above it
            // are the stub. A create is therefore never lock-refused today — see the remarks.
            return Result<LockLevel>.Success(LockLevel.None);
        }

        var resource = grains
            .ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceGrain>(GrainKeys.Resource(id.Id));

        var snapshot = await resource.GetAsync(string.Empty, []);
        return Result<LockLevel>.Success(snapshot.IsSuccess ? snapshot.GetValueOrThrow().Lock : LockLevel.None);
    }
}

/// <summary>
///     The <see cref="ISecretResolver" /> a silo with no vault registers: it refuses.
/// </summary>
/// <remarks>
///     ⚠ <b>Refuses rather than returning an empty string.</b> An empty password reaching a rendered
///     manifest is a database with no password, applied to a real cluster, reported as a successful
///     provision. docs/plan/08 § What the resource manager deliberately does not do routes the real
///     implementation to OpenBao via <c>CyberCloud.Vault</c>, which does not exist yet.
/// </remarks>
public sealed class UnavailableSecretResolver : ISecretResolver {
    /// <inheritdoc />
    public Task<Result<string>> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(reference);

        return Task.FromResult(
            Result<string>.Failure(
                ErrorCode.InternalError,
                $"No secret resolver is wired, so '{reference}' cannot be read. The platform's vault "
                + "(CyberCloud.Vault, over OpenBao) is not built yet — docs/plan/08 § What the "
                + "resource manager deliberately does not do. This refuses rather than returning an "
                + "empty value, because an empty password in a rendered manifest is a database with "
                + "no password reported as a successful provision."
            )
        );
    }
}

/// <summary>
///     The <see cref="IClusterConnectionFactory" /> a silo with no Kubernetes wiring registers.
/// </summary>
/// <remarks>
///     ⚠ Always <see langword="null" />, which is the right answer for a clusterless provider and a
///     wiring failure for one that declared <c>RequiresCluster</c> — <c>ReconcileDriver</c>
///     distinguishes them and names the type in the error. The real implementation is a handle over
///     <c>IClusterConnectionGrain</c> and lives in <c>CyberCloud.Kubernetes</c>, which this assembly
///     deliberately does not reference (docs/plan/03 § Assembly graph rules, rule 3).
/// </remarks>
public sealed class NoClusterConnectionFactory : IClusterConnectionFactory {
    /// <inheritdoc />
    public IKubeClusterConnection? Connect(Guid clusterId) => null;
}

/// <summary>
///     The <see cref="IClusterObjectInventory" /> a silo with no informer bridge registers.
/// </summary>
/// <remarks>
///     ⚠ <b>Fails rather than returning empty, and the difference is the whole safety of the drift
///     scan.</b> An empty inventory says <i>every resource on this cluster is a stray</i> — that
///     somebody deleted all of production — and a scan that believed it would re-apply an entire
///     cluster's worth of objects. A failure says <i>do not conclude anything</i>, which is the only
///     honest answer from a component that cannot see the cluster.
///     <para>
///         The real implementation needs the per-cluster informer of docs/plan/09 § Observing. It is
///         <b>not written</b>, and the diff it feeds (<c>DriftScanner</c>) is written and tested
///         against supplied inventories.
///     </para>
/// </remarks>
public sealed class UnavailableClusterObjectInventory : IClusterObjectInventory {
    /// <inheritdoc />
    public Task<Result<ImmutableArray<ClusterObjectRecord>>> ListManagedAsync(
        Guid clusterId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<ClusterObjectRecord>>.Failure(
                ErrorCode.InternalError,
                $"No cluster object inventory is wired, so cluster {clusterId:D} cannot be scanned for "
                + "drift. This fails rather than reporting an empty cluster: an empty inventory would "
                + "mean every resource placed here is a stray, which a scan would act on by "
                + "re-applying the whole cluster. The informer-backed implementation is "
                + "docs/plan/09 § Observing and is not built."
            )
        );
}
