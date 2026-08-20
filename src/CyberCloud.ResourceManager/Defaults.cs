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
///     event is built with the projection's columns and published at step 11 — so landing a projector
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
///     Resolves the effective lock at a resource's scope by walking the hierarchy upwards.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06 § Tags, locks makes locks <c>CanNotDelete</c> / <c>ReadOnly</c>, <i>"inherited
///         down the hierarchy"</i>, and docs/plan/08 § The write path, end to end's step 4 reads
///         <i>"locks: CanNotDelete / ReadOnly inherited from rg, sub, mg"</i>. This walks
///         <b>resource → resource group → subscription</b> and takes the strongest lock found at any
///         of the three.
///     </para>
///     <para>
///         ⚠ <b>The management group is the one scope that is still missing, and it is missing
///         because it does not exist.</b> docs/plan/06 § The hierarchy makes the management-group tree
///         optional and docs/plan/01 puts it at M2: there is no <c>IManagementGroupGrain</c>, no
///         grain key for one and no parent pointer from a subscription to one. So this walk stops at
///         the subscription, and <b>a lock set on a management group is not merely unread — it cannot
///         be set at all.</b> Stated here so that the day the tree lands, the missing link is a known
///         one line rather than a discovered incident. Adding it is one more <c>Strongest</c> above
///         the subscription read; nothing else about this class changes.
///     </para>
///     <para>
///         ⚠ <b>A create walks too.</b> A resource that does not exist yet has no lock of its own,
///         but the group and the subscription above it do — and a subscription-wide
///         <see cref="LockLevel.ReadOnly" /> that stopped updates while still allowing new resources
///         would be a strange kind of read-only. The resource's own read is skipped when there is no
///         resource, and the two ancestors are read exactly as they are for an update.
///     </para>
///     <para>
///         ⚠ <b>A scope that does not exist contributes <see cref="LockLevel.None" /> rather than
///         failing the walk.</b> Fail-closed sounds safer and is wrong here: the resource group and
///         subscription grains are created by an admin path that the resource manager does not drive,
///         so a platform whose lock walk refused every write against an unrecorded group would be a
///         platform where nothing could be created. Absence of a lock record is absence of a lock.
///         The <i>existence</i> of the subscription is checked separately and much earlier, at step 1
///         of the write path, where its answer is a 404 rather than a lock.
///     </para>
/// </remarks>
public sealed class ResourceScopeLockResolver(IGrainFactory grains) : ILockResolver {
    /// <inheritdoc />
    public async Task<Result<LockLevel>> ResolveAsync(ResourceId id, CancellationToken cancellationToken = default) {
        var tenant = grains.ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture));
        var effective = LockLevel.None;

        // ── The resource's own, when there is a resource ────────────────────────────────────────
        if (id.Id != Guid.Empty) {
            var snapshot = await tenant.GetGrain<IResourceGrain>(GrainKeys.Resource(id.Id)).GetAsync(string.Empty, []);
            if (snapshot.IsSuccess) {
                effective = LockLevels.Strongest(effective, snapshot.GetValueOrThrow().Lock);
            }
        }

        // ⚠ ReadOnly is the strongest lock there is, so nothing above can raise it further. The two
        // grain calls below are skipped rather than made and discarded — the walk is on the hot path
        // of every write and every delete.
        if (effective == LockLevel.ReadOnly) {
            return Result<LockLevel>.Success(effective);
        }

        // ── The resource group ─────────────────────────────────────────────────────────────────
        var group = await tenant
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(id.SubscriptionId, id.ResourceGroup))
            .GetAsync();

        if (group.IsSuccess) {
            effective = LockLevels.Strongest(effective, group.GetValueOrThrow().Lock);
        }

        if (effective == LockLevel.ReadOnly) {
            return Result<LockLevel>.Success(effective);
        }

        // ── The subscription — the top of the chain that exists. See the remarks on the mg. ─────
        var subscription = await tenant
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(id.SubscriptionId))
            .GetAsync();

        if (subscription.IsSuccess) {
            effective = LockLevels.Strongest(effective, subscription.GetValueOrThrow().Lock);
        }

        return Result<LockLevel>.Success(effective);
    }
}

/// <summary>
///     The <see cref="ISecretResolver" /> a silo with no vault registers: it refuses.
/// </summary>
/// <remarks>
///     ⚠ <b>Refuses rather than returning an empty string.</b> An empty password reaching a rendered
///     manifest is a database with no password, applied to a real cluster, reported as a successful
///     provision. docs/plan/08 § What the resource manager deliberately does not do routes the real
///     implementation to OpenBao via <c>CyberCloud.Vault</c>.
///     <para>
///         ⚠ <b>THIS IS NOW A <i>WIRING</i> FAILURE RATHER THAN A MISSING FEATURE, AND THE MESSAGE
///         SAYS SO.</b> <c>CyberCloud.Vault</c> exists and <c>OpenBaoSecretResolver</c> reads a real
///         value; reaching this type means the host did not call <c>AddOpenBaoSecretResolver</c>. The
///         remarks here used to end "which does not exist yet", and that sentence outliving the
///         assembly is the failure mode this paragraph replaces.
///     </para>
///     <para>
///         ⚠ <b>And this stays the <c>TryAdd</c> default, which is a layering fact rather than a
///         preference.</b> Registering the OpenBao resolver here would need
///         <c>CyberCloud.ResourceManager → CyberCloud.Vault</c>, and <c>CyberCloud.Vault</c> reaches
///         this assembly for <see cref="ISecretResolver" /> itself — module-layering.txt's third
///         direction refuses the cycle. So a silo with a vault opts in and every other silo keeps
///         this. <c>VaultSeamWiringTests</c> asserts both halves.
///     </para>
/// </remarks>
public sealed class UnavailableSecretResolver : ISecretResolver {
    /// <inheritdoc />
    public Task<Result<string>> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(reference);

        return Task.FromResult(
            Result<string>.Failure(
                ErrorCode.InternalError,
                $"No secret resolver is wired, so '{reference}' cannot be read. docs/plan/08 § What "
                + "the resource manager deliberately does not do routes secrets to OpenBao, and "
                + "CyberCloud.Vault is the client — but this host registered neither. Call "
                + "ISiloBuilder.AddOpenBaoSecretResolver() beside AddCyberCloudResourceManager(), "
                + "with CyberCloud:Vault:Address and CyberCloud:Vault:Role configured. This refuses "
                + "rather than returning an empty value, because an empty password in a rendered "
                + "manifest is a database with no password reported as a successful provision."
            )
        );
    }
}

/// <summary>
///     The <see cref="ISecretWriter" /> a host with no vault registers: it refuses.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Refuses rather than succeeding without writing, and the difference is a service that
///         comes up open.</b> A no-op mint returns "the credential exists" to a reconciler that then
///         renders a manifest against a secret nobody wrote — and on
///         <c>CyberCloud.Storage/accounts</c> an S3 gateway with no identities file
///         <i>authenticates nobody and authorises everybody</i>. So the refusal is the safe answer for
///         the same reason <see cref="UnavailableSecretResolver" />'s is, one step earlier in the
///         story.
///     </para>
///     <para>
///         ⚠ <b>Its message names the wiring rather than the feature</b>, because <c>OpenBaoSecretWriter</c>
///         exists and writes a real value. Reaching this type means the host did not call
///         <c>AddOpenBaoSecretResolver</c>, which installs both halves.
///     </para>
/// </remarks>
public sealed class UnavailableSecretWriter : ISecretWriter {
    /// <inheritdoc />
    public Task<Result<SecretMint>> MintAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fields);

        return Task.FromResult(
            Result<SecretMint>.Failure(
                ErrorCode.InternalError,
                $"No secret writer is wired, so a credential cannot be minted at '{path}'. "
                + "docs/plan/12 § The pattern, once, piece 5 puts credential provisioning in the "
                + "tenant's Vault and CyberCloud.Vault is the client — but this host registered "
                + "neither. Call ISiloBuilder.AddOpenBaoSecretResolver() beside "
                + "AddCyberCloudResourceManager(), with CyberCloud:Vault:Address and "
                + "CyberCloud:Vault:Role configured. This refuses rather than reporting a mint that "
                + "did not happen, because a resource whose credential silently does not exist is a "
                + "data plane that lets everybody in."
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
///     The <see cref="IClusterConnectionRegistrar" /> a host with no cluster fabric registers.
/// </summary>
/// <remarks>
///     ⚠ <b>It refuses, which turns a cluster that would have converged unreachable into a cluster
///     that does not converge.</b> That is deliberate and it is the harsher of the two answers.
///     <c>ReconcileDriver</c> turns this failure into the pass's outcome, so a
///     <c>CyberCloud.ContainerService/managedClusters</c> create on a host with no registrar reports
///     the missing wiring instead of reporting <c>Succeeded</c> for a cluster nothing can then place a
///     resource in. The second is what the platform did before the seam existed, and the tenant found
///     out one resource later.
/// </remarks>
public sealed class UnavailableClusterConnectionRegistrar : IClusterConnectionRegistrar {
    /// <inheritdoc />
    public Task<Result> AttachAsync(
        ClusterConnectionDescriptor descriptor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);

        return Task.FromResult(
            Result.Failure(
                ErrorCode.InternalError,
                $"No cluster-connection registrar is wired, so {descriptor} cannot be registered and "
                + "nothing could later be placed in it. A silo that serves "
                + "CyberCloud.ContainerService/managedClusters registers "
                + "GrainClusterConnectionRegistrar and calls AddCyberCloudKubernetes, which is what "
                + "activates the connection grain this writes to."
            )
        );
    }
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

/// <summary>
///     The <see cref="INamespaceInventory" /> every silo registers, because there is no other one.
/// </summary>
/// <remarks>
///     ⚠ <b>Fails rather than returning empty, and here the stakes are higher than the drift
///     inventory's.</b> An empty namespace listing is not a wrong report — it is a licence to run a
///     recursive delete over whatever is actually in there: a tenant's database, an operator's
///     <c>Secret</c>, and the volume claims docs/plan/08 § Soft delete keeps so that a restore has
///     something to restore from. <c>NamespaceReclaim.Decide</c> reads "no occupants" as
///     <c>Deletable</c>, which is correct given a <i>complete</i> listing and catastrophic given a
///     silent one, so the only safe stub is one that never answers.
///     <para>
///         ⚠ <b>The real one is not a smaller version of this and is not owed to
///         <c>UnavailableClusterObjectInventory</c>'s informer.</b> Listing a namespace's whole
///         contents is a discovery of every namespaced <c>APIResource</c> the cluster serves, CRDs
///         included, and a list per kind. <see cref="INamespaceInventory" /> says what that costs.
///     </para>
/// </remarks>
public sealed class UnavailableNamespaceInventory : INamespaceInventory {
    /// <inheritdoc />
    public Task<Result<ImmutableArray<NamespaceOccupant>>> ListAllAsync(
        Guid clusterId,
        string ns,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<NamespaceOccupant>>.Failure(
                ErrorCode.InternalError,
                $"No namespace inventory is wired, so nothing can say what namespace '{ns}' on cluster "
                + $"{clusterId:D} holds. This fails rather than reporting an empty namespace, because "
                + "an empty namespace is the one answer that authorizes deleting it — and deleting a "
                + "namespace is a recursive delete of every object inside, including the volume claims "
                + "a soft-deleted resource is restored from. Nothing may be reclaimed on a guess."
            )
        );
}
