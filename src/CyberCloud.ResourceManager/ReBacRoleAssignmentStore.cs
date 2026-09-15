using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The tuple behind a role assignment, over <see cref="ITupleStoreGrain" /> — the write half of
///     docs/plan/07 § Azure RBAC, expressed in it, whose read half
///     (<see cref="ICheckGrain.ListRoleAssignmentsAsync" />) existed alone until issue #70.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The address is the tuple, and this class is the one place the translation is made.</b>
///         The scope names the object — <see cref="ReBacScopeAuthorizer.ObjectOf" /> for a tenant, a
///         subscription or a group, <c>resource:{id:N}</c> for a resource — and
///         <see cref="RoleAssignmentName" /> names the relation and the subject. A <c>group</c>
///         principal becomes the userset <c>group:{id}#member</c>, which is docs/plan/07's own second
///         table row; every other principal type is a plain subject. Nothing else in the platform
///         spells that mapping, so nothing else can spell it differently.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Through <see cref="ITupleStoreGrain" /> and never through
///             <c>IObjectRelationsGrain.WriteAsync</c>
///         </b>, for the reason both relation writers give: a
///         tuple written straight into the forward index is one the reverse index never learns
///         about and does not bump the tenant's relation version, so no consistency token covers it
///         and no check cache is invalidated — a grant nobody could see until a silo restarted.
///         <see cref="IsGrantedAsync" /> does read the forward index directly, and that is the
///         permitted direction: it is the authority, and it is the half <c>Check</c> reads.
///     </para>
///     <para>
///         ⚠ <b>Idempotent in both directions, by the store's own contract.</b>
///         <c>TupleStoreGrain</c> makes a repeated write and a repeated delete succeed, which is what
///         lets <c>RoleAssignmentService</c> promise the same for <c>PUT</c> and <c>DELETE</c> with
///         no record of its own.
///     </para>
/// </remarks>
public sealed class ReBacRoleAssignmentStore(IGrainFactory grains, ILogger<ReBacRoleAssignmentStore> logger)
    : IRoleAssignmentStore {
    /// <summary>The ReBAC object type of a resource — the fourth scope an assignment can name.</summary>
    public const string ResourceObjectType = ObjectTypes.Resource;

    /// <summary>
    ///     The userset relation a <c>group</c> principal is granted through —
    ///     <c>group:{id}#member</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ A tuple whose subject were the bare object <c>group:{id}</c> would name the group and
    ///     grant to none of its members: <c>CyberCloudSchema</c> resolves a userset subject by
    ///     walking its relation, and an object subject is matched only against itself.
    /// </remarks>
    public const string GroupUserset = Relations.Member;

    /// <inheritdoc />
    public async Task<Result> GrantAsync(RoleAssignmentId assignment, CancellationToken cancellationToken = default) {
        var built = TupleOf(assignment);
        if (built.TryGetError(out var invalid)) {
            return Result.Failure(invalid);
        }

        var written = await Store(assignment).WriteAsync(built.GetValueOrThrow());

        if (written.TryGetError(out var failure)) {
            logger.LogError(
                "Granting '{Path}' failed: {Message}.",
                assignment.Path,
                failure.Message
            );

            return Result.Failure(failure);
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(RoleAssignmentId assignment, CancellationToken cancellationToken = default) {
        var built = TupleOf(assignment);
        if (built.TryGetError(out var invalid)) {
            return Result.Failure(invalid);
        }

        var removed = await Store(assignment).DeleteAsync(built.GetValueOrThrow());

        if (removed.TryGetError(out var failure)) {
            logger.LogError(
                "Revoking '{Path}' failed: {Message}.",
                assignment.Path,
                failure.Message
            );

            return Result.Failure(failure);
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsGrantedAsync(
        RoleAssignmentId assignment,
        CancellationToken cancellationToken = default
    ) {
        var built = TupleOf(assignment);
        if (built.TryGetError(out var invalid)) {
            return Result<bool>.Failure(invalid);
        }

        var tuple = built.GetValueOrThrow();

        // ⚠ ReadDurableAsync rather than ReadAsync, for the reason IObjectRelationsGrain gives: this
        // answer decides between a 200 and a 404 on a GET and between "created" and "already there"
        // on a PUT, and both are cheap enough to pay a durable read for the case the row changed
        // under the activation. Role assignments are rare; a wrong "already there" is a create
        // that reports it did nothing.
        var snapshot = await grains
            .ForTenant(assignment.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(tuple.Object.Type, tuple.Object.Id))
            .ReadDurableAsync();

        if (snapshot.TryGetError(out var failure)) {
            return Result<bool>.Failure(failure);
        }

        return Result<bool>.Success(snapshot.GetValueOrThrow().Subjects(tuple.Relation).Contains(tuple.Subject));
    }

    /// <summary>
    ///     The tuple an assignment is. Public so a test can compare what the store would write with
    ///     what the schema rewrites through, rather than pinning either spelling.
    /// </summary>
    /// <param name="assignment">The assignment. A resource scope must carry its resolved id.</param>
    public static Result<RelationTuple> TupleOf(RoleAssignmentId assignment) {
        var (type, id) = ObjectOf(assignment);

        if (type.Length == 0) {
            return Result<RelationTuple>.Failure(
                ErrorCode.InvalidResourceId,
                assignment.IsResourceScoped
                    ? $"'{assignment.ScopePath}' has not been resolved to a resource id. A parsed "
                    + "resource path carries Guid.Empty, and a tuple on 'resource:000…' would be a "
                    + "grant on nothing — resolve it through the path index first."
                    : "A role assignment on a scope with no kind names no ReBAC object, so there is "
                    + "nothing to write a tuple on."
            );
        }

        var name = assignment.Name;

        var subject = string.Equals(name.PrincipalType, ObjectTypes.Group, StringComparison.Ordinal)
            ? SubjectRef.Create(ObjectTypes.Group, name.PrincipalId, GroupUserset)
            : SubjectRef.Create(name.PrincipalType, name.PrincipalId);

        if (subject.TryGetError(out var subjectError)) {
            return Result<RelationTuple>.Failure(subjectError);
        }

        // ⚠ Spelled out in full. `ObjectRef` is pinned to the Kubernetes one by this assembly's
        // GlobalUsings — see the comment there — and the ReBAC one is a different type entirely.
        return RelationTuple.Create(
            CyberCloud.Authorization.Contracts.ObjectRef.Of(type, id),
            name.Role,
            subject.GetValueOrThrow()
        );
    }

    /// <summary>
    ///     The ReBAC object an assignment's scope names, or <c>("", "")</c> for a resource whose id
    ///     is unresolved.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <remarks>
    ///     ⚠ The resource id is the <c>N</c> form, matching
    ///     <see cref="ReBacResourceAuthorizer.CheckedObject" /> — the object a check on that resource
    ///     is asked on. A <c>D</c> here would be a legal id naming an object no check ever visits.
    /// </remarks>
    public static (string Type, string Id) ObjectOf(RoleAssignmentId assignment) =>
        assignment.IsResourceScoped
            ? assignment.Resource.Id == Guid.Empty
                ? ("", "")
                : (ResourceObjectType, assignment.Resource.Id.ToString("N", CultureInfo.InvariantCulture))
            : ReBacScopeAuthorizer.ObjectOf(assignment.Scope);

    ITupleStoreGrain Store(RoleAssignmentId assignment) =>
        grains
            .ForTenant(assignment.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(assignment.TenantId));
}
