using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The write half of the authorization seam — the <c>parent</c> edge, over
///     <see cref="ITupleStoreGrain" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The edge this writes is the one that made a create readable, and until it existed a
///         create was not.</b> docs/plan/07 § The model:
///         <i>
///             "<c>From(x, y)</c> is Zanzibar's tupleset-to-userset … It is the whole of hierarchical
///             inheritance and it is why a role assignment at a subscription grants on every resource
///             group in it without any tuple being written per resource."
///         </i>
///         The sentence is about <b>role</b> tuples, and it is true: no role tuple is written per
///         resource. It is <i>not</i> about the <c>parent</c> tuple, which is the pointer
///         <c>From("parent", …)</c> follows — and a resource with no <c>parent</c> tuple is a resource
///         the walk cannot leave. That distinction is the whole of defect 1.
///     </para>
///     <para>
///         ⚠ <b>Through <see cref="ITupleStoreGrain" /> and never through
///         <c>IObjectRelationsGrain</c> directly.</b> That grain's own remarks say why: a tuple
///         written straight into the forward index is one the reverse index never learns about and
///         does not bump the tenant's relation version, so no consistency token covers it and no check
///         cache is invalidated. A resource created that way would be readable by a caller whose cache
///         happened to be cold and invisible to one whose was not.
///     </para>
///     <para>
///         ⚠ <b>Both directions are idempotent, and both need to be.</b> The link is written before
///         durable state exists and the write path may be retried; the unlink is driven from
///         <c>OperationGrain</c>'s reminder, which re-runs after a silo loss. <c>TupleStoreGrain</c>
///         makes a repeated write and a repeated delete succeed, so neither path needs a "did I
///         already?" flag of its own.
///     </para>
/// </remarks>
public sealed class ReBacResourceRelationWriter(IGrainFactory grains, ILogger<ReBacResourceRelationWriter> logger)
    : IResourceRelationWriter {
    /// <summary>
    ///     The relation name of the scope one level up. docs/plan/07 § The model's <c>parent</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A string, for exactly the reason <c>ReBacResourceAuthorizer.ResourceGroupObjectType</c>
    ///     is a string.</b> <c>CyberCloud.Authorization.Relations.Parent</c> lives in
    ///     <c>CyberCloud.Authorization</c> and this assembly references only its <c>.Contracts</c>, so
    ///     the two constants cannot be shared. A mismatch here would not fail loudly — the tuple would
    ///     be written against a relation the schema does not rewrite from, and the resource would be
    ///     invisible with no error anywhere. The isolation suite asserts the two strings agree, which
    ///     is the same guard the <c>resourcegroup</c>/<c>resourceGroup</c> casing bug earned.
    /// </remarks>
    public const string ParentRelation = "parent";

    /// <inheritdoc />
    public Task<Result> LinkToParentAsync(ResourceId id, CancellationToken cancellationToken = default) =>
        ApplyAsync(id, link: true);

    /// <inheritdoc />
    public Task<Result> UnlinkFromParentAsync(ResourceId id, CancellationToken cancellationToken = default) =>
        ApplyAsync(id, link: false);

    async Task<Result> ApplyAsync(ResourceId id, bool link) {
        if (id.Id == Guid.Empty) {
            // Not a domain outcome: the caller asked to link an address that has no identity yet.
            // docs/plan/06 § Identifiers — a parsed path yields Guid.Empty, and the GUID arrives at
            // the quota step. Answering "success" would silently skip the edge.
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{id.Path}' has no resource id, so there is no ReBAC object to attach a parent to. "
                + "The GUID is minted at the write path's quota step and the parent edge is written "
                + "after the index claim — docs/plan/08 § The write path, end to end."
            );
        }

        // ⚠ Spelled out in full. `ObjectRef` is pinned to the Kubernetes one by this assembly's
        // GlobalUsings — see the comment there — and the ReBAC one is a different type entirely.
        var built = RelationTuple.Create(
            CyberCloud.Authorization.Contracts.ObjectRef.Of(ReBacResourceAuthorizer.ResourceObjectType, id.Id),
            ParentRelation,
            SubjectRef.Of(ReBacResourceAuthorizer.ResourceGroupObjectType, ReBacResourceAuthorizer.GroupObjectId(id))
        );

        if (built.TryGetError(out var invalid)) {
            logger.LogError(
                "The parent tuple for {Path} is not a valid tuple: {Message}.",
                id.Path,
                invalid.Message
            );

            return Result.Failure(invalid);
        }

        var store = grains
            .ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(id.TenantId));

        var tuple = built.GetValueOrThrow();

        var applied = link
            ? await store.WriteAsync(tuple)
            : await store.DeleteAsync(tuple);

        if (applied.TryGetError(out var failure)) {
            logger.LogError(
                "{Direction} the parent edge of {Path} failed: {Message}.",
                link ? "Writing" : "Removing",
                id.Path,
                failure.Message
            );

            return Result.Failure(failure);
        }

        return Result.Success;
    }
}
