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

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RoleAssignmentSnapshot>>> ListAsync(
        RoleAssignmentCollectionId collection,
        CancellationToken cancellationToken = default
    ) {
        var (type, id) = ObjectOf(collection);

        if (type.Length == 0) {
            return Result<IReadOnlyList<RoleAssignmentSnapshot>>.Failure(
                ErrorCode.InvalidResourceId,
                collection.IsResourceScoped
                    ? $"'{collection.ScopePath}' has not been resolved to a resource id, so there is "
                    + "no ReBAC object to list role assignments on — resolve it through the path "
                    + "index first."
                    : "A role assignment collection on a scope with no kind names no ReBAC object."
            );
        }

        var tenant = grains.ForTenant(collection.TenantId.ToString("D", CultureInfo.InvariantCulture));

        // ⚠ THE VIEW IS ICheckGrain's AND NOT A SECOND WALK WRITTEN HERE. ListRoleAssignmentsAsync
        // reports an inherited role only where this type's own rewrite actually inherits it, so a
        // row here is a grant the evaluator would honour — a walk of `parent` edges written beside
        // it would be a second opinion about which roles inherit, and the one nobody re-reads when
        // the schema changes.
        var listed = await tenant
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(type, id))
            .ListRoleAssignmentsAsync(true);

        if (listed.TryGetError(out var listError)) {
            return Result<IReadOnlyList<RoleAssignmentSnapshot>>.Failure(listError);
        }

        var addresses = new Dictionary<string, RoleAssignmentCollectionId>(StringComparer.Ordinal) {
            [type + ":" + id] = collection
        };

        List<RoleAssignmentSnapshot> rows = [];

        foreach (var assignment in listed.GetValueOrThrow()) {
            var writtenOn = assignment.Inherited ? assignment.InheritedFrom : assignment.Scope;
            var key = writtenOn.Type + ":" + writtenOn.Id;

            if (!addresses.TryGetValue(key, out var scope)) {
                var reversed = await ScopeOfAsync(tenant, collection.TenantId, writtenOn);
                if (reversed.TryGetError(out var reverseError)) {
                    return Result<IReadOnlyList<RoleAssignmentSnapshot>>.Failure(reverseError);
                }

                scope = reversed.GetValueOrThrow();
                addresses[key] = scope;
            }

            var principal = PrincipalOf(assignment.Principal);
            var address = scope.Member(new(assignment.RoleName, principal.Type, principal.Id));

            rows.Add(
                new() {
                    Path = address.Path,
                    Name = address.Name.Render(),
                    Scope = address.ScopePath,
                    RoleDefinitionId = assignment.RoleName,
                    PrincipalType = principal.Type,
                    PrincipalId = principal.Id,
                    Created = false,
                    Inherited = assignment.Inherited
                }
            );
        }

        return Result<IReadOnlyList<RoleAssignmentSnapshot>>.Success(rows);
    }

    /// <summary>
    ///     The principal an assignment's subject names, in the address's two-string spelling —
    ///     <see cref="TupleOf" />'s subject half, reversed.
    /// </summary>
    /// <param name="subject">The tuple's subject.</param>
    /// <remarks>
    ///     ⚠ Only the <c>group:{id}#member</c> userset folds back to a <c>group</c> principal. A
    ///     userset on any other relation is not something this path ever writes; it is rendered as
    ///     the object it names so that a hand-written tuple is at least visible, rather than
    ///     silently reported as a grant to the whole group.
    /// </remarks>
    public static (string Type, string Id) PrincipalOf(SubjectRef subject) =>
        subject.IsUserset
        && string.Equals(subject.Object.Type, ObjectTypes.Group, StringComparison.Ordinal)
        && string.Equals(subject.Relation, GroupUserset, StringComparison.Ordinal)
            ? (ObjectTypes.Group, subject.Id)
            : (subject.Type, subject.Id);

    /// <summary>
    ///     The scope a ReBAC object is — <see cref="ObjectOf(RoleAssignmentCollectionId)" />
    ///     reversed, which is what gives an inherited row the address of the scope its tuple is
    ///     written on.
    /// </summary>
    /// <remarks>
    ///     A scope object's id is the scope spelled forwards — <c>tenant:{N}</c>,
    ///     <c>managementGroup:{name}</c>, <c>subscription:{N}</c>, <c>resourceGroup:{N}-{name}</c> —
    ///     so four of the five reverse with no lookup. A resource object's id is a GUID with no path in it, and its own grain
    ///     is
    ///     what knows the path: one <c>IResourceGrain.GetAsync</c> with no api-version and no
    ///     pointers, which projects nothing and answers the envelope. ⚠ The empty api-version is
    ///     deliberate — the read wants the address and not a body, and an unknown version projects
    ///     nothing by that method's own contract.
    /// </remarks>
    static async Task<Result<RoleAssignmentCollectionId>> ScopeOfAsync(
        TenantGrainFactory tenant,
        Guid tenantId,
        CyberCloud.Authorization.Contracts.ObjectRef @object
    ) {
        if (string.Equals(@object.Type, ObjectTypes.Tenant, StringComparison.Ordinal)
            && GuidFormat.TryParseN(@object.Id, out var tenantObject)) {
            return Result<RoleAssignmentCollectionId>.Success(
                RoleAssignmentCollectionId.OnScope(ScopeId.Tenant(tenantObject))
            );
        }

        if (string.Equals(@object.Type, ObjectTypes.Subscription, StringComparison.Ordinal)
            && GuidFormat.TryParseN(@object.Id, out var subscription)) {
            return Result<RoleAssignmentCollectionId>.Success(
                RoleAssignmentCollectionId.OnScope(ScopeId.Subscription(tenantId, subscription))
            );
        }

        if (string.Equals(@object.Type, ObjectTypes.ResourceGroup, StringComparison.Ordinal)
            && @object.Id.Length > 33
            && @object.Id[32] == '-'
            && GuidFormat.TryParseN(@object.Id[..32], out var groupSubscription)
            && ResourceNaming.IsValid(@object.Id[33..])) {
            return Result<RoleAssignmentCollectionId>.Success(
                RoleAssignmentCollectionId.OnScope(ScopeId.Group(tenantId, groupSubscription, @object.Id[33..]))
            );
        }

        // A management group's object id is its name and nothing else — ReBacScopeAuthorizer.ObjectOf.
        if (string.Equals(@object.Type, ObjectTypes.ManagementGroup, StringComparison.Ordinal)
            && ResourceNaming.IsValid(@object.Id)) {
            return Result<RoleAssignmentCollectionId>.Success(
                RoleAssignmentCollectionId.OnScope(ScopeId.ManagementGroupOf(tenantId, @object.Id))
            );
        }

        if (string.Equals(@object.Type, ResourceObjectType, StringComparison.Ordinal)
            && GuidFormat.TryParseN(@object.Id, out var resourceId)) {
            var resource = await tenant.GetGrain<IResourceGrain>(GrainKeys.Resource(resourceId)).GetAsync("", []);
            if (resource.TryGetError(out var readError)) {
                return Result<RoleAssignmentCollectionId>.Failure(readError);
            }

            var parsed = ResourceId.ParsePath(resource.GetValueOrThrow().Path);
            if (parsed.TryGetError(out var pathError)) {
                return Result<RoleAssignmentCollectionId>.Failure(pathError);
            }

            return Result<RoleAssignmentCollectionId>.Success(
                RoleAssignmentCollectionId.OnResource(parsed.GetValueOrThrow().WithId(resourceId))
            );
        }

        // A role tuple on an object that is not a scope cannot come through this path, and a
        // listing that rendered it under an address nothing serves would be a row a GET denies.
        return Result<RoleAssignmentCollectionId>.Failure(
            ErrorCode.InternalError,
            $"A role assignment is written on '{@object.Type}:{@object.Id}', which is not a scope "
            + "this API addresses, so it cannot be listed under an address. Only a tenant, a "
            + "management group, a subscription, a resource group or a resource carries role "
            + "assignments — docs/plan/07 § Azure RBAC, expressed in it."
        );
    }

    /// <summary>
    ///     The ReBAC object a collection's scope names, or <c>("", "")</c> for a resource whose id
    ///     is unresolved — <see cref="ObjectOf(RoleAssignmentId)" /> for the scope alone.
    /// </summary>
    /// <param name="collection">The collection.</param>
    public static (string Type, string Id) ObjectOf(RoleAssignmentCollectionId collection) =>
        collection.IsResourceScoped
            ? collection.Resource.Id == Guid.Empty
                ? ("", "")
                : (ResourceObjectType, collection.Resource.Id.ToString("N", CultureInfo.InvariantCulture))
            : ReBacScopeAuthorizer.ObjectOf(collection.Scope);

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
        ObjectOf(RoleAssignmentCollectionId.Of(assignment));

    ITupleStoreGrain Store(RoleAssignmentId assignment) =>
        grains
            .ForTenant(assignment.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(assignment.TenantId));
}
