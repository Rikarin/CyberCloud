using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Contracts.Registry;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>
///     Builds the <see cref="IResourceView" /> and <see cref="IResourceWatch" /> a pass carries, each
///     bound to the resource the pass is for.
/// </summary>
/// <remarks>
///     <para>
///         <b>One factory, two seams, one owner.</b> <c>ReconcileDriver</c> calls
///         <see cref="For" /> with the resource it is about to reconcile, and everything the returned
///         objects do is done <i>as that resource</i>: the view's checks name it as the subject and
///         the watch's registrations name it as the watcher, in its own subscription. Neither takes
///         an owner from the reconciler, so a provider cannot look or listen on another resource's
///         behalf — that is the seam's whole security argument stated as a constructor.
///     </para>
///     <para>
///         ⚠ <b>The authorizer is the one the gateway uses</b> — <see cref="IResourceAuthorizer" />,
///         registered once in <c>AddCyberCloudResourceManager</c> and resolved here — so the rule
///         <see cref="IResourceView" /> writes down is enforced by the same code that enforces a
///         <c>GET</c>, with the same <c>404</c>. A second evaluator for the cross-resource case would
///         be a second place for the two to disagree, and docs/plan/07 § The enforcement seam says
///         exactly one place calls the engine.
///     </para>
/// </remarks>
public sealed class ResourceViews(
    IProviderRegistry registry,
    IGrainFactory grains,
    IResourceAuthorizer authorizer,
    IClusterConnectionFactory clusters,
    IClock clock,
    ILogger<ResourceViews> logger
) {
    /// <summary>The ReBAC subject type a resource acts as when it reads another. docs/plan/07 § The model.</summary>
    public const string ResourceSubjectType = CyberCloud.Authorization.Contracts.ObjectTypes.Resource;

    /// <summary>
    ///     The caller a resource is equivalent to when it reads another resource — the rule in
    ///     <see cref="IResourceView" />, as a value.
    /// </summary>
    /// <param name="owner">The resource doing the reading. Its GUID must be resolved.</param>
    /// <returns>
    ///     A caller in the owner's tenant whose subject is <c>resource:{owner.Id:N}</c>, correlated to
    ///     the owner's path so an audit line reads "the vault read the share".
    /// </returns>
    /// <exception cref="ArgumentException">The owner's GUID is empty — a resource that does not exist yet cannot read.</exception>
    public static CallerContext CallerFor(ResourceId owner) {
        if (owner.Id == Guid.Empty) {
            throw new ArgumentException(
                $"'{owner.Path}' has no GUID yet, so it cannot be the subject of a check. A resource "
                + "reads other resources from its reconcile pass, and by then it has one.",
                nameof(owner)
            );
        }

        return new() {
            TenantId = owner.TenantId,
            SubjectType = ResourceSubjectType,
            SubjectId = owner.Id.ToString("N", CultureInfo.InvariantCulture),
            CorrelationId = "reconcile:" + owner.CanonicalPath
        };
    }

    /// <summary>The view and the watch for one owning resource.</summary>
    /// <param name="owner">The resource whose pass is running, GUID resolved.</param>
    public (IResourceView View, IResourceWatch Watch) For(ResourceId owner) =>
        (
            new OwnedResourceView(owner, registry, grains, authorizer, clusters, logger),
            new OwnedResourceWatch(owner, grains, clock)
        );

    /// <summary>
    ///     Decides whether <paramref name="reader" /> may read <paramref name="target" />, by the rule
    ///     in <see cref="IResourceView" />. Shared by the view and by the watch fan-out so a delivery
    ///     is authorized exactly as a read is.
    /// </summary>
    /// <param name="reader">The resource that wants to see, GUID resolved.</param>
    /// <param name="target">The resource it wants to see, GUID resolved.</param>
    /// <param name="readPermission">The target type's read permission, from its registration.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Success, or the <c>404</c> the gateway would give.</returns>
    public Task<Result> MayReadAsync(
        ResourceId reader,
        ResourceId target,
        string readPermission,
        CancellationToken cancellationToken = default
    ) =>
        MayReadAsync(authorizer, reader, target, readPermission, cancellationToken);

    internal static async Task<Result> MayReadAsync(
        IResourceAuthorizer authorizer,
        ResourceId reader,
        ResourceId target,
        string readPermission,
        CancellationToken cancellationToken
    ) {
        // ── The tenant gate, before the engine ──────────────────────────────────────────────────
        //
        // ⚠ THE SAME 404 THE GATEWAY GIVES AT STEP 1, AND FOR THE SAME REASON. A resource in another
        // tenant is not "forbidden", it does not exist as far as this tenant can tell; answering
        // anything else would let a reconciler in tenant A learn which paths are live in tenant B by
        // watching which answer it got. Checked here as well as by construction (the view resolves
        // through the OWNER's tenant-qualified factory), because a target handed in with a GUID
        // already set skips the resolve.
        if (reader.TenantId != target.TenantId) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"'{target.Path}' does not exist.");
        }

        // ── FullyConsistent, where the gateway's GET is MinimizeLatency ─────────────────────────
        //
        // ⚠ THE SAME AUTHORIZER, THE OTHER MODE, AND THE DIFFERENCE IS WHO CAN RECOVER FROM A STALE
        // ANSWER. CheckGrain caches with no TTL (docs/plan/07 § Consistency: "any cached result"), so
        // a check that answered no before the grant keeps answering no at MinimizeLatency until
        // something re-evaluates that object. A user's GET tolerates that because the portal passes
        // the token its grant returned (AtLeastAsFresh); a reconciler has no token — it runs on a
        // reminder, long after the tenant clicked — and a vault denied once would be denied for as
        // long as the cache lived, which is forever. The cost is a durable walk per view call, on a
        // reminder-driven pass rather than a request; on the fan-out it lands on the tenant's write,
        // once per watcher of that type in that subscription, which is a small number by
        // construction.
        return await authorizer.AuthorizeAsync(
            target,
            readPermission,
            readPermission,
            CallerFor(reader),
            true,
            cancellationToken
        );
    }
}

/// <summary>The <see cref="IResourceView" /> for one owning resource. Built by <see cref="ResourceViews.For" />.</summary>
public sealed class OwnedResourceView(
    ResourceId owner,
    IProviderRegistry registry,
    IGrainFactory grains,
    IResourceAuthorizer authorizer,
    IClusterConnectionFactory clusters,
    ILogger logger
) : IResourceView {
    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> ReadAsync(ResourceId target, CancellationToken cancellationToken = default) {
        var viewed = await ViewAsync(target, cancellationToken);
        return viewed.TryGetError(out var error)
            ? Result<ResourceSnapshot>.Failure(error)
            : Result<ResourceSnapshot>.Success(viewed.GetValueOrThrow().Snapshot);
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<ObjectRef>>> RenderedObjectsAsync(
        ResourceId target,
        CancellationToken cancellationToken = default
    ) {
        var viewed = await ViewAsync(target, cancellationToken);
        if (viewed.TryGetError(out var error)) {
            return Result<ImmutableArray<ObjectRef>>.Failure(error);
        }

        var (resolved, snapshot) = viewed.GetValueOrThrow();

        // A clusterless target rendered nothing, and saying so is true rather than a failure.
        if (snapshot.ClusterId == Guid.Empty) {
            return Result<ImmutableArray<ObjectRef>>.Success([]);
        }

        var connection = clusters.Connect(snapshot.ClusterId);
        if (connection is null) {
            return Result<ImmutableArray<ObjectRef>>.Failure(
                ErrorCode.InternalError,
                $"'{resolved.Path}' was placed on cluster {snapshot.ClusterId:D} and this host has no "
                + "connection to it, so its rendered objects cannot be listed. This fails rather than "
                + "answering an empty list: a vault that believed the list would snapshot nothing and "
                + "report a backup."
            );
        }

        var ns = ReconcileDriver.NamespaceFor(resolved);
        var listed = await connection.ListNamespaceAsync(ns, cancellationToken);
        if (listed.TryGetError(out var listError)) {
            return Result<ImmutableArray<ObjectRef>>.Failure(listError);
        }

        // ⚠ JOINED ON THE LABEL, NOT ON A NAME THE PROVIDER CHOSE. ADR-013 puts cybercloud.io/resource-id
        // on every object a reconciler renders, and the Labels gate asserts it against real output —
        // so the label is the one fact about another provider's objects this assembly may rely on
        // without knowing that provider's naming. It is also how the drift scan attributes objects,
        // so the two cannot disagree about whose an object is.
        var wanted = KubeLabels.GuidValue(resolved.Id);

        return Result<ImmutableArray<ObjectRef>>.Success(
            [
                .. listed.GetValueOrThrow()
                    .Where(x => x.Labels.TryGetValue(KubeLabels.ResourceId, out var id)
                        && string.Equals(id, wanted, StringComparison.Ordinal)
                    )
                    .Select(x => new ObjectRef { Kind = x.Kind, Namespace = x.Namespace, Name = x.Name })
            ]
        );
    }

    async Task<Result<(ResourceId Resolved, ResourceSnapshot Snapshot)>> ViewAsync(
        ResourceId target,
        CancellationToken cancellationToken
    ) {
        // ── The tenant gate. See ResourceViews.MayReadAsync for why it is a 404. ─────────────────
        if (target.TenantId != owner.TenantId) {
            return NotFound<(ResourceId, ResourceSnapshot)>(target);
        }

        // ── The type must be one this silo serves; its registration is where the permission lives ──
        if (!registry.TryGetType(target.Type, out var registration)) {
            return NotFound<(ResourceId, ResourceSnapshot)>(target);
        }

        var tenant = grains.ForTenant(owner.TenantId.ToString("D", CultureInfo.InvariantCulture));

        // ── Resolve the path the way a GET does, so an unresolved address finds its GUID ──────────
        //
        // ⚠ THROUGH THE OWNER'S TENANT FACTORY, WHICH IS THE SECOND HALF OF THE TENANT GATE. A path
        // in another tenant has no index entry here, so even a caller that spoofed the tenant id in
        // the address would resolve nothing.
        var resolved = target;
        if (resolved.Id == Guid.Empty) {
            var bound = await tenant.GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(target)).ResolveAsync();
            if (bound.IsFailure) {
                return NotFound<(ResourceId, ResourceSnapshot)>(target);
            }

            resolved = target.WithId(bound.GetValueOrThrow());
        }

        // ── The rule ────────────────────────────────────────────────────────────────────────────
        var allowed = await ResourceViews.MayReadAsync(
            authorizer,
            owner,
            resolved,
            registration.ReadPermission,
            cancellationToken
        );

        if (allowed.TryGetError(out var refused)) {
            logger.LogInformation(
                "'{Owner}' asked to view '{Target}' and was refused: {Message}",
                owner.Path,
                target.Path,
                refused.Message
            );

            return Result<(ResourceId, ResourceSnapshot)>.Failure(refused);
        }

        // ── The read, as the gateway performs it: newest api-version, secret pointers dropped ────
        var schema = registration.SchemaFor(registration.Newest);
        if (schema.TryGetError(out var schemaError)) {
            return Result<(ResourceId, ResourceSnapshot)>.Failure(schemaError);
        }

        var snapshot = await tenant
            .GetGrain<IResourceGrain>(GrainKeys.Resource(resolved.Id))
            .GetAsync(
                registration.Newest.Value,
                [.. schema.GetValueOrThrow().Properties.Where(x => !x.Secret).Select(x => x.JsonPointer)]
            );

        return snapshot.TryGetError(out var readError)
            ? Result<(ResourceId, ResourceSnapshot)>.Failure(readError)
            : Result<(ResourceId, ResourceSnapshot)>.Success((resolved, snapshot.GetValueOrThrow()));
    }

    static Result<T> NotFound<T>(ResourceId target) where T : notnull =>
        Result<T>.Failure(ErrorCode.ResourceNotFound, $"'{target.Path}' does not exist.");
}

/// <summary>The <see cref="IResourceWatch" /> for one owning resource. Built by <see cref="ResourceViews.For" />.</summary>
public sealed class OwnedResourceWatch(ResourceId owner, IGrainFactory grains, IClock clock) : IResourceWatch {
    /// <inheritdoc />
    public Task<Result> SubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default) =>
        type.IsEmpty
            ? Task.FromResult(Result.Failure(ErrorCode.InvalidResourceType, "A watch names one resource type, and this one is empty."))
            : Index(type).SubscribeAsync(new() { ResourceId = owner.Id, Path = owner.Path, Since = clock.UtcNow });

    /// <inheritdoc />
    public Task<Result> UnsubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default) =>
        type.IsEmpty
            ? Task.FromResult(Result.Failure(ErrorCode.InvalidResourceType, "A watch names one resource type, and this one is empty."))
            : Index(type).UnsubscribeAsync(owner.Id);

    // ⚠ THE OWNER'S TENANT AND THE OWNER'S SUBSCRIPTION, AND NOTHING THE RECONCILER SAID. "In my
    // subscription" is the seam's scope, and it is a fact about the owner the driver established.
    IResourceWatchGrain Index(ResourceTypeName type) =>
        grains.ForTenant(owner.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceWatchGrain>(GrainKeys.WatchIndex(owner.SubscriptionId, type));
}
