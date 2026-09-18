using CyberCloud.Authorization.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Scope creation — the four steps of docs/plan/08 § The write path, end to end that a scope
///     actually has. <see cref="IScopeManager" />'s remarks carry the argument for why this is beside
///     the resource path rather than inside it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The order, and it is the same order for the same reasons:</b>
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>Resolve.</b> Parse the path; the tenant in it must be the caller's; the parent
///                 scope must exist. Every one of those refuses with the canonical <c>404</c>. This is
///                 step 1, minus the registry — a scope has no provider and no api-version.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Check.</b> <c>write</c> on the <i>parent</i> scope, through
///                 <see cref="IScopeAuthorizer" />. Step 3, and the object is the parent for the
///                 reason a resource create is checked against its group: a scope that does not exist
///                 holds no tuple, so checking it would fail closed and make every create impossible.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Lock.</b> A <c>ReadOnly</c> lock on the parent subscription refuses a new group
///                 in it. Step 4, shortened — see <see cref="CreateGroupAsync" /> on why this is one
///                 read rather than <c>ILockResolver</c>'s walk.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Parent edge, then the durable write.</b> Step 8 before step 9, and the ordering
///                 is the same trade docs/plan/08 spells out: before the durable write a failure is a
///                 clean refusal, after it a failure is a scope its own creator cannot see — and
///                 there is no operation grain on this path to re-drive the work.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             No quota, no index claim, no membership record, no desired state, no operation and no
///             <c>202</c>.
///         </b> Each of those absences is a property of a scope rather than an omission,
///         and <see cref="IScopeManager" /> names them one at a time so that a reader adding one back
///         has to disagree with a sentence rather than fill in a blank.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c>.</b> This service is held by the
///         gateway, which is an Orleans <i>client</i>, so <c>Orleans.Multitenant</c>'s call filter
///         never sees it. <c>CC1006</c> is what keeps that true after the next edit.
///     </para>
/// </remarks>
public sealed class ScopeManagerService(
    IScopeAuthorizer authorizer,
    IScopeRelationWriter relations,
    IGrainFactory grains,
    ResourceGroupReclaimer reclaimer,
    ILogger<ScopeManagerService> logger
)
    : IScopeManager {
    /// <summary>
    ///     The property name a resource group's region arrives under.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Read from <see cref="ScopeBodyProperties" /> rather than declared here, and the move
    ///         is the point.
    ///     </b> The generated surfaces live in the contracts assembly and cannot see
    ///     this one, so a copy here and a copy there would be two constants agreeing by hand — the
    ///     failure this repository keeps re-finding. Issue #63 is what made a second reader exist.
    /// </remarks>
    public const string LocationProperty = ScopeBodyProperties.Location;

    /// <summary>The body property a subscription's display name arrives in.</summary>
    /// <remarks>⚠ <see cref="ScopeBodyProperties" />'s, for the reason above.</remarks>
    public const string DisplayNameProperty = ScopeBodyProperties.DisplayName;

    /// <summary>
    ///     The body property naming the management group a subscription or a group hangs off.
    /// </summary>
    /// <remarks>⚠ <see cref="ScopeBodyProperties" />'s, for the reason above.</remarks>
    public const string ManagementGroupProperty = ScopeBodyProperties.ManagementGroup;

    /// <inheritdoc />
    public async Task<Result<ScopeSnapshot>> CreateAsync(
        ScopeRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = Resolve(request);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<ScopeSnapshot>.Failure(resolveError);
        }

        var scope = resolved.GetValueOrThrow();

        if (scope.Kind == ScopeKind.Tenant) {
            // ⚠ NOT a 404, because this is not an existence question and the caller holds a token for
            // this very tenant — its existence is not news to them. It is a 400 that says where the
            // door is, which is the answer that stops somebody adding the route.
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{scope.Path}' is a tenant, and a tenant is not created over this API. The tenant "
                + "of every request comes from the token (docs/plan/10 § Request pipeline, stage 3), "
                + "so a request that created a different tenant would have to name it in a path the "
                + "gateway has already refused. Tenant creation is a platform-operator path — "
                + "IScopeManager.CreateTenantAsync, docs/plan/06 § Platform administration."
            );
        }

        var body = Parse(request.Body);
        if (body.TryGetError(out var bodyError)) {
            return Result<ScopeSnapshot>.Failure(bodyError);
        }

        using var document = body.GetValueOrThrow();

        return scope.Kind switch {
            ScopeKind.Subscription => await CreateSubscriptionAsync(
                scope,
                document.RootElement,
                request.Caller,
                cancellationToken
            ),
            ScopeKind.ManagementGroup => await CreateManagementGroupAsync(
                scope,
                document.RootElement,
                request.Caller,
                cancellationToken
            ),
            _ => await CreateGroupAsync(scope, document.RootElement, request.Caller, cancellationToken)
        };
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(ScopeRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = Resolve(request);
        if (resolved.TryGetError(out var resolveError)) {
            return Result.Failure(resolveError);
        }

        var scope = resolved.GetValueOrThrow();

        if (scope.Kind == ScopeKind.ManagementGroup) {
            return await DeleteManagementGroupAsync(scope, request.Caller, cancellationToken);
        }

        if (scope.Kind != ScopeKind.ResourceGroup) {
            // ⚠ NOT a 404 and not a silent partial. A subscription delete is every group's delete
            // plus the meter, the quota and the shard; a tenant's is that plus the directory and the
            // shard map. Treating either as "the group case, wider" is how a tenant ends up billed
            // for a shard nothing lists.
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{scope.Path}' is a {scope.Kind} and only a resource group or a management group "
                + "can be deleted over this API. Deleting a subscription or a tenant also has to end "
                + "the meter, release the quota and unassign the shard, and none of that is built — "
                + "a delete that removed the record and left those would be worse than one that "
                + "refuses."
            );
        }

        // ⚠ THE CHECK IS ON THE GROUP ITSELF, exactly as it is for a read and unlike a create. A
        // create checks the parent because the scope does not exist yet; a delete is about one that
        // does, so it has a ReBAC object of its own — and checking the subscription instead would
        // let somebody who holds `write` there delete a group whose own `#suspended` says otherwise.
        var permitted = await authorizer.AuthorizeAsync(
            scope,
            Permissions.Write,
            Permissions.Read,
            request.Caller,
            cancellationToken: cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result.Failure(denied);
        }

        var tenant = scope.TenantId.ToString("D", CultureInfo.InvariantCulture);

        var group = grains
            .ForTenant(tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(scope.SubscriptionId, scope.ResourceGroup));

        var record = await group.GetAsync();
        if (record.IsFailure) {
            // ⚠ Success rather than 404, and this is the one place the two differ from a read's. The
            // goal of a delete is the absence of the thing, so a group that is already gone has
            // reached it — and a re-driven DELETE after a network timeout must not report a failure
            // for work that succeeded. The sweep runs again, because the way this branch is reached
            // with the caller authorized is through a tuple the first delete did not get to.
            return await SweepResourceGroupTuplesAsync(scope, cancellationToken);
        }

        // ── The locks. Both links of the chain, and the group's own is not enough. ───────────────
        //
        // ⚠ ILockResolver is not used here for the reason CreateGroupAsync does not use it: it takes
        // a ResourceId and walks resource → group → subscription, and a group is not a resource. The
        // two links that exist are read directly and combined with LockLevels.Strongest, which is
        // what stops a subscription-wide ReadOnly being downgraded by the group's weaker lock.
        var subscription = grains
            .ForTenant(tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId));

        var owner = await subscription.GetAsync();

        var effective = LockLevels.Strongest(
            record.GetValueOrThrow().Lock,
            owner.IsSuccess ? owner.GetValueOrThrow().Lock : LockLevel.None
        );

        if (effective is LockLevel.ReadOnly or LockLevel.CanNotDelete) {
            return Result.Failure(
                ErrorCode.ScopeLocked,
                $"'{scope.Path}' carries an inherited {effective} lock, so it cannot be deleted — "
                + "docs/plan/06 § Tags, locks. Clear the lock and retry."
            );
        }

        var reclaimed = await reclaimer.DeleteAsync(scope, cancellationToken);
        if (reclaimed.IsFailure) {
            return reclaimed;
        }

        return await SweepResourceGroupTuplesAsync(scope, cancellationToken);
    }

    /// <summary>
    ///     The last step of a resource group's delete: every tuple on its object, after the record
    ///     and the namespace are gone. Logged and never returned — the caller's delete has succeeded.
    /// </summary>
    /// <remarks>
    ///     ⚠ The group's object is <c>resourceGroup:{sub}-{rg}</c>, a name, so a group re-created
    ///     under the same name in the same subscription would inherit every grant the deleted one
    ///     carried — <see cref="IScopeRelationWriter.ClearAsync" />. The first version of this delete
    ///     left the tuples and said so in <c>ResourceGroupReclaimer</c>'s remarks, when the writer had
    ///     no way to remove them; the review of issue #39 found the same residue on a management
    ///     group, where a grant reaches every subscription under it, and the sweep now runs for both.
    /// </remarks>
    async Task<Result> SweepResourceGroupTuplesAsync(ScopeId scope, CancellationToken cancellationToken) {
        var cleared = await relations.ClearAsync(scope, cancellationToken);
        if (cleared.TryGetError(out var clearError)) {
            logger.LogError(
                "Resource group '{Group}' is deleted but tuples remain on its object: {Message}. They "
                + "are inert while no group has the name and the next DELETE sweeps them. Do not "
                + "re-create a group under this name until one succeeds: the tuples are its grants.",
                scope.Path,
                clearError.Message
            );
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<ScopeSnapshot>> ReadAsync(
        ScopeRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = Resolve(request);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<ScopeSnapshot>.Failure(resolveError);
        }

        var scope = resolved.GetValueOrThrow();

        // ⚠ THE CHECK IS ON THE SCOPE ITSELF AND NOT ON ITS PARENT, WHICH IS THE OPPOSITE OF A
        // CREATE. A read is about a scope that exists, so it has a ReBAC object of its own — and
        // checking the parent instead would let somebody who holds `read` on a subscription read a
        // group whose own `#suspended` says otherwise. Same branch ReBacResourceAuthorizer.
        // CheckedObject makes, arrived at from the other side.
        var allowed = await authorizer.AuthorizeAsync(
            scope,
            Permissions.Read,
            Permissions.Read,
            request.Caller,
            cancellationToken: cancellationToken
        );

        if (allowed.TryGetError(out var denied)) {
            return Result<ScopeSnapshot>.Failure(denied);
        }

        return scope.Kind switch {
            ScopeKind.Tenant => await ReadTenantAsync(scope),
            ScopeKind.Subscription => await ReadSubscriptionAsync(scope),
            ScopeKind.ManagementGroup => await ReadManagementGroupAsync(scope),
            _ => await ReadGroupAsync(scope)
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The resource collection's four moves, in the resource collection's order</b> —
    ///         <c>ResourceManagerService.ListAsync</c> is the shape and this is that shape one level
    ///         up: the parent grain's own index, ordered ordinally, resumed after the continuation
    ///         and cut at the page size; one <c>ListObjects</c> for the page; a <c>Check</c> per
    ///         member when the engine declined; then each survivor rendered by the same code a
    ///         by-id <c>GET</c> uses, so an element and a read of that element are one shape.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The continuation is the last member <i>examined</i> and not the last one
    ///             returned.
    ///         </b> A page made entirely of scopes the caller cannot read must still
    ///         advance, or a caller with narrow rights in a wide tenant loops on one page forever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A member whose grain cannot answer is dropped rather than failing the page.</b>
    ///         The tenant's listing is appended after the subscription grain's create, so the two
    ///         cannot disagree that way round; but a group mid-delete is removed from the
    ///         subscription's listing after its record is sealed, so for one moment a name is listed
    ///         and its grain answers absent. Failing the whole page because somebody else is
    ///         mid-delete would make the listing unavailable exactly while it is changing.
    ///     </para>
    /// </remarks>
    public async Task<Result<ScopeListPage>> ListAsync(
        ScopeListRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var parsed = ScopeId.ParsePath(request.ParentPath);
        if (parsed.TryGetError(out var pathError)) {
            return Result<ScopeListPage>.Failure(pathError);
        }

        var parent = parsed.GetValueOrThrow();

        // ⚠ The same tenant comparison Resolve makes for an item, and the same canonical 404: a
        // cross-tenant collection path that answered "forbidden" would confirm the other tenant's
        // scope exists, and a listing is the widest such confirmation this API has.
        if (parent.TenantId != request.Caller.TenantId) {
            return CollectionNotFound(parent);
        }

        if (parent.Kind is not (ScopeKind.Tenant or ScopeKind.Subscription)) {
            return Result<ScopeListPage>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{parent.Path}' is a {parent.Kind}, and it has no scope children to list. A "
                + "resource group holds resources, addressed by type: "
                + "'/…/resourceGroups/{name}/providers/{namespace}/{type}' — docs/plan/10 § Shape. A "
                + "management group's children are listed flat under the tenant, "
                + "'/tenants/{t}/managementGroups', each carrying its parent — docs/plan/06 § The "
                + "hierarchy."
            );
        }

        // ⚠ The member kind decides between a tenant's two collections; a request that does not say
        // gets the one the parent had before issue #39. ScopeCollectionId is where the pair is
        // checked, so a (Subscription, ManagementGroup) request is refused by its constructor rather
        // than routed to a listing that would answer the wrong question.
        ScopeCollectionId collection;

        try {
            collection = new(parent, request.MemberKind);
        } catch (ArgumentException invalid) {
            return Result<ScopeListPage>.Failure(ErrorCode.InvalidResourceId, invalid.Message);
        }

        return collection.MemberKind switch {
            ScopeKind.Subscription => await ListSubscriptionsAsync(collection, request, cancellationToken),
            ScopeKind.ManagementGroup => await ListManagementGroupsAsync(collection, request, cancellationToken),
            _ => await ListGroupsAsync(collection, request, cancellationToken)
        };
    }

    /// <inheritdoc />
    public async Task<Result<ScopeSnapshot>> CreateTenantAsync(
        TenantCreateRequest request,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (string.IsNullOrWhiteSpace(request.OwnerSubjectId)) {
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                "A tenant needs an owner at creation and one was not supplied. 'tenant' is the only "
                + "type CyberCloudSchema gives no 'parent' relation, so nothing above it can grant on "
                + "it and a tenant with no direct '#owner' tuple is permanently invisible to "
                + "everyone — see IScopeManager.CreateTenantAsync."
            );
        }

        if (request.TenantId == ReBacScopeAuthorizer.PlatformTenant) {
            // ⚠ The platform tenant is Guid.Empty and is the tenant this very check is evaluated in
            // (docs/plan/06 § Platform administration). Creating it through this method would mean
            // holding `administer` on a platform whose tuple store lives in the tenant being created.
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                "Guid.Empty is the platform tenant (docs/plan/06 § Platform administration) and is "
                + "not created through this method: the operator grant this method checks lives in "
                + "that tenant's own store, so it would have to exist before it could be created."
            );
        }

        // ── Who may. docs/plan/06 § Platform administration's platform:root#operator. ────────────
        var permitted = await authorizer.AuthorizePlatformAsync(Permissions.Administer, caller, cancellationToken);
        if (permitted.TryGetError(out var refused)) {
            return Result<ScopeSnapshot>.Failure(refused);
        }

        // ── The shard, first, because everything below writes durable state into it. ─────────────
        var shardMap = grains.GetGrain<IShardMapGrain>(GrainKeys.ShardMap());

        if (request.DurableShard.Length > 0) {
            // ⚠ The pin BEFORE the assignment, and the assignment then finds it — issue #39. A
            // re-driven create carrying the same shard finds its own pin and is a no-op; one
            // carrying a different shard is the move IShardMapGrain.PinAsync refuses by name, and
            // the refusal is returned before a single durable row is written for this tenant.
            var pinned = await shardMap.PinAsync(request.TenantId, request.DurableShard, null);

            if (pinned.TryGetError(out var pinError)) {
                return Result<ScopeSnapshot>.Failure(pinError);
            }
        }

        var assigned = await shardMap.AssignAsync(request.TenantId, request.HomeRegion);

        if (assigned.TryGetError(out var shardError)) {
            return Result<ScopeSnapshot>.Failure(shardError);
        }

        var assignment = assigned.GetValueOrThrow();

        // ── The record on EVERY silo, before the first durable row. ──────────────────────────────
        //
        // ⚠ THE SPLIT THE REVIEW OF ISSUE #39 FOUND. The tenant grain below activates on some silo,
        // and that silo builds the tenant's storage provider from ITS shard map mirror — a cache a
        // timer refreshes every fifteen seconds, read on a path that cannot fetch. For a tenant it
        // has not heard of, the mirror falls back to the hash; a pin exists to make the record
        // differ from the hash, and a drained shard makes them differ too. Without this step the
        // first rows went to the hash-chosen shard and every silo that refreshed afterwards read the
        // recorded, empty one. ShardMapPropagation.ConfirmAsync asks every silo to refresh and to
        // answer with what its mirror now resolves the tenant to, and a create that cannot get the
        // recorded shard from every silo stops here, with nothing written for the tenant.
        var propagated = await ShardMapPropagation.ConfirmAsync(grains, assignment);

        if (propagated.TryGetError(out var propagationError)) {
            return Result<ScopeSnapshot>.Failure(propagationError);
        }

        // ── The tenant's own record. Validates the slug and the region; idempotent on a re-drive. ─
        var created = await grains
            .ForTenant(request.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(request.TenantId))
            .CreateAsync(request.Slug, request.DisplayName, request.HomeRegion);

        if (created.TryGetError(out var tenantError)) {
            return Result<ScopeSnapshot>.Failure(tenantError);
        }

        var scope = ScopeId.Tenant(request.TenantId);

        // ── The owner edge, BEFORE the directory entry. ─────────────────────────────────────────
        //
        // ⚠ THE DIRECTORY ENTRY IS WHAT MAKES A TENANT REACHABLE, SO IT GOES LAST. Stage 3 of the
        // gateway resolves a token's tenant through TenantDirectoryCache and answers 404 when the
        // lookup misses, so until the entry exists no request can reach this tenant at all. Writing
        // the owner tuple first therefore means there is no window in which the tenant is reachable
        // and owned by nobody — which is step 8's argument, applied to the one scope whose
        // reachability is a separate record from its existence.
        var owner = await relations.GrantOwnerAsync(
            scope,
            request.OwnerSubjectType,
            request.OwnerSubjectId,
            cancellationToken
        );

        if (owner.TryGetError(out var ownerError)) {
            return Result<ScopeSnapshot>.Failure(ownerError);
        }

        var registered = await grains
            .GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
            .RegisterAsync(
                new() {
                    TenantId = request.TenantId,
                    Slug = request.Slug,
                    HomeRegion = request.HomeRegion,
                    HotShard = assignment.HotHashTag,
                    DurableShard = assignment.DurableShard,
                    Status = created.GetValueOrThrow().Status
                }
            );

        if (registered.TryGetError(out var directoryError)) {
            return Result<ScopeSnapshot>.Failure(directoryError);
        }

        logger.LogInformation(
            "Tenant {TenantId} ('{Slug}') created in {Region} by {Caller}, owned by {OwnerType}:{OwnerId}.",
            request.TenantId,
            request.Slug,
            request.HomeRegion,
            caller,
            request.OwnerSubjectType,
            request.OwnerSubjectId
        );

        var descriptor = created.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.Tenant,
                Name = descriptor.Slug,
                Type = ScopeTypeNames.Tenant,
                Location = descriptor.HomeRegion,
                Created = true,
                Version = descriptor.Version
            }
        );
    }

    // ── Create: a subscription ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Creates a subscription in the caller's tenant.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The permission is <c>write</c> on the TENANT and not <c>delete</c> or a bespoke
    ///         "createSubscription", and the reason is that the schema has four permissions and this is
    ///         the one that means "change what is inside".
    ///     </b> docs/plan/07 § Azure RBAC, expressed in it
    ///     maps <c>write</c> to <c>Rel(contributor)</c>, and Azure's own Contributor on a scope creates
    ///     children in it. Requiring <c>delete</c> — that is, <c>owner</c> — would be stricter than
    ///     Azure and would make "may create a subscription" and "may delete the tenant" the same
    ///     right, which is a worse thing to hand out. A separable "may create a subscription and
    ///     nothing else" needs a grantable relation of its own — the three roles are what
    ///     <c>IRoleAssignmentManager</c> can grant, and a fourth is a schema change.
    /// </remarks>
    async Task<Result<ScopeSnapshot>> CreateSubscriptionAsync(
        ScopeId scope,
        JsonElement body,
        CallerContext caller,
        CancellationToken cancellationToken
    ) {
        var tenant = grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(scope.TenantId));

        // ⚠ The parent must exist, and for a subscription the parent is the tenant. Stage 3 has
        // already resolved this tenant through the directory, so this is not the same question — the
        // directory entry and the tenant's own record are two writes and a tenant with the first and
        // not the second would take subscriptions into a record that does not exist.
        var record = await tenant.GetAsync();
        if (record.IsFailure) {
            return NotFound(scope);
        }

        var permitted = await authorizer.AuthorizeAsync(
            ScopeId.Tenant(scope.TenantId),
            Permissions.Write,
            Permissions.Read,
            caller,
            cancellationToken: cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result<ScopeSnapshot>.Failure(denied);
        }

        var displayName = Text(body, DisplayNameProperty);
        if (displayName.Length == 0) {
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A subscription needs a '{DisplayNameProperty}'. It is the name that appears on an "
                + "invoice and in every scope picker, and a subscription identified only by its GUID "
                + "is one nobody can pick out of a list."
            );
        }

        var subscription = grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId));

        var current = await subscription.GetAsync();
        var existed = current.IsSuccess;
        var currentGroup = existed ? current.GetValueOrThrow().ManagementGroup : "";

        // ── The management group, if the body names one — issue #39. ────────────────────────────
        //
        // ⚠ ABSENT IS "UNCHANGED", AND THE EMPTY STRING IS "THE ROOT" — ScopeBodyProperties
        // .ManagementGroup says why a body that never mentions the property must not move a
        // subscription. On a create, "unchanged" is the root, because there is nothing to keep.
        var requestedGroup = OptionalText(body, ManagementGroupProperty) ?? currentGroup;

        var targetParent = ScopeId.Tenant(scope.TenantId);

        if (requestedGroup.Length > 0) {
            var named = EnsureGroupName(requestedGroup);
            if (named.IsFailure) {
                return Result<ScopeSnapshot>.Failure(named.Error!);
            }

            var placed = await EnsureGroupAcceptsAsync(
                ScopeId.ManagementGroupOf(scope.TenantId, requestedGroup),
                caller,
                cancellationToken
            );

            if (placed.TryGetError(out var placeError)) {
                return Result<ScopeSnapshot>.Failure(placeError);
            }

            targetParent = placed.GetValueOrThrow();
        }

        // ── The parent edge, before the durable write. See the type's remarks. ──────────────────
        //
        // ⚠ THREE SHAPES OF THE SAME STEP. A new subscription is linked to its parent — the tenant,
        // or the group the body names. An existing one whose group is unchanged is re-linked to the
        // same parent, which the tuple store makes a no-op. An existing one being MOVED has its edge
        // relinked, delete-then-write, so the chain is never two parents long —
        // IScopeRelationWriter.RelinkParentAsync carries the argument.
        var currentParent = currentGroup.Length > 0
            ? ScopeId.ManagementGroupOf(scope.TenantId, currentGroup)
            : ScopeId.Tenant(scope.TenantId);

        var linked = !existed || currentParent == targetParent
            ? await relations.LinkToParentAsync(scope, targetParent, cancellationToken)
            : await relations.RelinkParentAsync(scope, currentParent, targetParent, cancellationToken);

        if (linked.TryGetError(out var linkError)) {
            return Result<ScopeSnapshot>.Failure(linkError);
        }

        var created = await subscription.CreateAsync(displayName);
        if (created.TryGetError(out var createError)) {
            return Result<ScopeSnapshot>.Failure(createError);
        }

        // ── The tree, after the edge: the group's membership and the subscription's own record. ──
        //
        // ⚠ The listings first and the leaf's record last, and none of the four is returned as a
        // failure — the edge is written and the subscription exists, so the caller's request has
        // succeeded, and each of these is what the next identical PUT repairs. The same trade
        // ITenantGrain.AddSubscriptionAsync makes below.
        if (!string.Equals(currentGroup, requestedGroup, StringComparison.Ordinal)) {
            await RecordAssignmentAsync(scope, currentGroup, requestedGroup);
        }

        // ⚠ AFTER the subscription exists, and it is what makes ITenantGrain.ListSubscriptionsAsync
        // answer anything at all — nothing in the platform called it, so every tenant's subscription
        // list was empty and internally consistent while being empty, which is the same shape as the
        // resource-group membership defect docs/plan/08 § The write path, end to end records at
        // step 7b. A failure here is logged and not returned: the subscription exists, is
        // addressable, and is re-listed by the next identical PUT, so refusing would turn a listing
        // gap into a create that the caller believes failed.
        var listed = await tenant.AddSubscriptionAsync(scope.SubscriptionId);
        if (listed.TryGetError(out var listError)) {
            logger.LogError(
                "Subscription {SubscriptionId} was created in tenant {TenantId} but was not added to "
                + "the tenant's listing: {Message}. The subscription is usable; the listing is short "
                + "until the next identical PUT.",
                scope.SubscriptionId,
                scope.TenantId,
                listError.Message
            );
        }

        // Re-read rather than rendered from `created`: the assignment above may have bumped the
        // version and set the group, and a PUT's body is what a GET of the same address renders.
        var after = await subscription.GetAsync();
        var descriptor = after.IsSuccess ? after.GetValueOrThrow() : created.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.Subscription,
                Name = descriptor.DisplayName,
                Type = ScopeTypeNames.Subscription,
                Created = !existed,
                Version = descriptor.Version,
                ManagementGroup = descriptor.ManagementGroup
            }
        );
    }

    /// <summary>
    ///     Whether a management group exists and the caller may place a scope under it, answering
    ///     with the group's address on success.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The group is named in the BODY, so a group that is not there is a <c>400</c> and
    ///             not the canonical <c>404</c>.
    ///         </b> The 404 rule protects an address the caller typed
    ///         into the URL from confirming a sibling's existence; a body property that names a
    ///         group the caller cannot see is answered with the same sentence whether the group is
    ///         absent or hidden — <see cref="IScopeAuthorizer.AuthorizeAsync" /> is asked first and
    ///         its refusal is passed through unchanged, so the two cases stay indistinguishable and
    ///         the code is the one a body problem carries.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>write</c> on the group, not <c>read</c>.</b> Placing a subscription under a
    ///         group hands every role holder on the group inherited rights over the subscription;
    ///         the caller has to be someone the group would let change what is inside it, which is
    ///         what <c>write</c> means on every other scope. Azure asks for the same on both ends
    ///         of a subscription move.
    ///     </para>
    /// </remarks>
    async Task<Result<ScopeId>> EnsureGroupAcceptsAsync(
        ScopeId group,
        CallerContext caller,
        CancellationToken cancellationToken
    ) {
        var permitted = await authorizer.AuthorizeAsync(
            group,
            Permissions.Write,
            Permissions.Read,
            caller,
            cancellationToken: cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return denied.Code == ErrorCode.ResourceNotFound
                ? Result<ScopeId>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{ManagementGroupProperty}' names '{group.ManagementGroup}', and "
                    + $"'{group.Path}' does not exist."
                )
                : Result<ScopeId>.Failure(denied);
        }

        var record = await grains
            .ForTenant(group.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(group.ManagementGroup))
            .GetAsync();

        return record.IsFailure
            ? Result<ScopeId>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{ManagementGroupProperty}' names '{group.ManagementGroup}', and "
                + $"'{group.Path}' does not exist."
            )
            : Result<ScopeId>.Success(group);
    }

    /// <summary>
    ///     Moves a subscription between the groups' membership lists and stamps its own record —
    ///     the tree, after the edge. Logged and never returned: see the call site.
    /// </summary>
    async Task RecordAssignmentAsync(ScopeId scope, string fromGroup, string toGroup) {
        var tenant = grains.ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture));

        if (fromGroup.Length > 0) {
            var removed = await tenant
                .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(fromGroup))
                .RemoveSubscriptionAsync(scope.SubscriptionId);

            if (removed.TryGetError(out var removeError)) {
                logger.LogError(
                    "Subscription {SubscriptionId} left management group '{Group}' but the group still "
                    + "lists it: {Message}. The edge has moved; the listing catches up on the next "
                    + "identical PUT.",
                    scope.SubscriptionId,
                    fromGroup,
                    removeError.Message
                );
            }
        }

        if (toGroup.Length > 0) {
            var added = await tenant
                .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(toGroup))
                .AddSubscriptionAsync(scope.SubscriptionId);

            if (added.TryGetError(out var addError)) {
                logger.LogError(
                    "Subscription {SubscriptionId} joined management group '{Group}' but the group does "
                    + "not list it: {Message}. The edge has moved; the listing catches up on the next "
                    + "identical PUT.",
                    scope.SubscriptionId,
                    toGroup,
                    addError.Message
                );
            }
        }

        var stamped = await tenant
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId))
            .SetManagementGroupAsync(toGroup);

        if (stamped.TryGetError(out var stampError)) {
            logger.LogError(
                "Subscription {SubscriptionId} now hangs off '{Group}' in the tuple store but its own "
                + "record says '{Previous}': {Message}. The next identical PUT re-stamps it.",
                scope.SubscriptionId,
                toGroup.Length == 0 ? "the tenant" : toGroup,
                fromGroup.Length == 0 ? "the tenant" : fromGroup,
                stampError.Message
            );
        }
    }

    // ── Create: a management group ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Creates a management group in the caller's tenant, under the tenant or under the group
    ///     the body names — issue #39.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The check is <c>write</c> on the PARENT, as for every scope create</b> — the
    ///         tenant for a root group, the parent group for a nested one — and the parent must
    ///         exist. For a nested group the parent's record is also where the depth comes from:
    ///         <c>IManagementGroupGrain.CreateAsync</c> takes the parent's depth as an argument
    ///         because a grain cannot read another grain inside its own turn, and the cap it enforces
    ///         is what keeps a legal tree inside docs/plan/07 § Check's twelve hops. This service
    ///         applies the same cap first, before the <c>parent</c> edge is written, so a refused
    ///         seventh level leaves no tuple behind — as the move refusal below does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Idempotent on the same parent, refused on a different one.</b> A group's parent
    ///         is set at creation — <c>IManagementGroupGrain</c>'s remarks say what a move would
    ///         need and why it is owed rather than built — so a repeated <c>PUT</c> with the same
    ///         body is a <c>200</c> and one naming another parent is the grain's <c>409</c>, passed
    ///         through.
    ///     </para>
    /// </remarks>
    async Task<Result<ScopeSnapshot>> CreateManagementGroupAsync(
        ScopeId scope,
        JsonElement body,
        CallerContext caller,
        CancellationToken cancellationToken
    ) {
        var tenantGrains = grains.ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture));
        var tenant = tenantGrains.GetGrain<ITenantGrain>(GrainKeys.Tenant(scope.TenantId));

        var record = await tenant.GetAsync();
        if (record.IsFailure) {
            return NotFound(scope);
        }

        var parentName = OptionalText(body, ManagementGroupProperty) ?? "";

        if (parentName.Length > 0) {
            var named = EnsureGroupName(parentName);
            if (named.IsFailure) {
                return Result<ScopeSnapshot>.Failure(named.Error!);
            }
        }

        if (string.Equals(parentName, scope.ManagementGroup, StringComparison.Ordinal)) {
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{scope.Path}' cannot be its own parent. A group hangs off another group or off the "
                + "tenant — docs/plan/06 § The hierarchy."
            );
        }

        var parent = ScopeId.Tenant(scope.TenantId);
        var parentDepth = 0;

        if (parentName.Length > 0) {
            var parentScope = ScopeId.ManagementGroupOf(scope.TenantId, parentName);

            var accepted = await EnsureGroupAcceptsAsync(parentScope, caller, cancellationToken);
            if (accepted.TryGetError(out var parentError)) {
                return Result<ScopeSnapshot>.Failure(parentError);
            }

            var parentRecord = await tenantGrains
                .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(parentName))
                .GetAsync();

            if (parentRecord.IsFailure) {
                return Result<ScopeSnapshot>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{ManagementGroupProperty}' names '{parentName}', and '{parentScope.Path}' does not exist."
                );
            }

            parent = parentScope;
            parentDepth = parentRecord.GetValueOrThrow().Depth;
        } else {
            var permitted = await authorizer.AuthorizeAsync(
                parent,
                Permissions.Write,
                Permissions.Read,
                caller,
                cancellationToken: cancellationToken
            );

            if (permitted.TryGetError(out var denied)) {
                return Result<ScopeSnapshot>.Failure(denied);
            }
        }

        var group = tenantGrains.GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(scope.ManagementGroup));

        var existing = await group.GetAsync();
        var existed = existing.IsSuccess;

        if (existed && !string.Equals(existing.GetValueOrThrow().Parent, parentName, StringComparison.Ordinal)) {
            // ⚠ Refused BEFORE the edge is written, and by this service rather than only by the
            // grain: the grain's refusal comes after LinkToParentAsync would have written a second
            // parent tuple, which is exactly the two-parent chain the schema comment forbids.
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.Conflict,
                $"'{scope.Path}' already exists under "
                + (existing.GetValueOrThrow().Parent.Length == 0
                        ? "the tenant"
                        : $"'{existing.GetValueOrThrow().Parent}'")
                + " and this request names "
                + (parentName.Length == 0 ? "the tenant" : $"'{parentName}'")
                + " as its parent. A group's parent is set at creation and a move is not built — "
                + "IManagementGroupGrain says what a safe move would need, and docs/plan/06 § The "
                + "hierarchy records it as owed."
            );
        }

        if (!existed && parentDepth + 1 > IManagementGroupGrain.MaxDepth) {
            // ⚠ The depth cap, also BEFORE the edge and for the same reason as the move above: the
            // grain refuses the seventh level, but by then LinkToParentAsync would have written
            // `managementGroup:{name}#parent@managementGroup:{parent}` for a group that does not
            // exist — inert, and the same residue class the codebase tolerates for a create that
            // fails after the edge, but this refusal is knowable from the parent's record, so it is
            // not paid for. The grain keeps its own check for callers that are not this service.
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{scope.Path}' would sit at depth "
                + (parentDepth + 1).ToString(CultureInfo.InvariantCulture)
                + " and the tree is capped at "
                + IManagementGroupGrain.MaxDepth.ToString(CultureInfo.InvariantCulture)
                + " levels. Every level is a hop in every permission check beneath it, and "
                + "docs/plan/07 § Check caps the walk at twelve — see IManagementGroupGrain."
            );
        }

        // ── The parent edge, before the durable write. See the type's remarks. ──────────────────
        var linked = await relations.LinkToParentAsync(scope, parent, cancellationToken);
        if (linked.TryGetError(out var linkError)) {
            return Result<ScopeSnapshot>.Failure(linkError);
        }

        var created = await group.CreateAsync(Text(body, DisplayNameProperty), parentName, parentDepth);
        if (created.TryGetError(out var createError)) {
            return Result<ScopeSnapshot>.Failure(createError);
        }

        // ── The listings, after the record. Logged, not returned — the group exists. ────────────
        var listed = await tenant.AddManagementGroupAsync(scope.ManagementGroup);
        if (listed.TryGetError(out var listError)) {
            logger.LogError(
                "Management group '{Group}' was created in tenant {TenantId} but was not added to the "
                + "tenant's listing: {Message}. The group is usable; the listing is short until the "
                + "next identical PUT.",
                scope.ManagementGroup,
                scope.TenantId,
                listError.Message
            );
        }

        if (parentName.Length > 0) {
            var childed = await tenantGrains
                .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(parentName))
                .AddChildAsync(scope.ManagementGroup);

            if (childed.TryGetError(out var childError)) {
                logger.LogError(
                    "Management group '{Group}' was created under '{Parent}' but the parent does not "
                    + "list it: {Message}. The edge is written; the listing catches up on the next "
                    + "identical PUT.",
                    scope.ManagementGroup,
                    parentName,
                    childError.Message
                );
            }
        }

        var descriptor = created.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.ManagementGroup,
                Name = descriptor.DisplayName,
                Type = ScopeTypeNames.ManagementGroup,
                Created = !existed,
                Version = descriptor.Version,
                ManagementGroup = descriptor.Parent
            }
        );
    }

    // ── Delete: a management group ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Deletes an empty management group: the record in one turn with the emptiness check, then
    ///     the listings, then every tuple on its object — the <c>parent</c> edge and the roles
    ///     assigned at it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The record goes first and the tuples go last, which is the create reversed</b> —
    ///         docs/plan/06 § Two-phase create, "deletion is the same in reverse". The grain's own
    ///         delete refuses while anything hangs off the group, so nothing after it runs for a
    ///         group that is not empty; and once the record is gone the tuples sit on an object that
    ///         resolves to nothing, so a crash before the last step leaves inert tuples the next
    ///         <c>DELETE</c> removes. The other order would leave, for a moment, a group with a
    ///         record and no edge — visible in the listing and readable by nobody.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The roles go too, and the first version of this delete left them.</b> The
    ///         object id is the bare name (<c>ReBacScopeAuthorizer.ObjectOf</c>), so a group
    ///         re-created under a deleted group's name inherits every grant the deleted one had, and
    ///         a group grant reaches every subscription placed under it — the review of issue #39
    ///         found it. <see cref="IScopeRelationWriter.ClearAsync" /> is the sweep.
    ///     </para>
    /// </remarks>
    async Task<Result> DeleteManagementGroupAsync(
        ScopeId scope,
        CallerContext caller,
        CancellationToken cancellationToken
    ) {
        // ⚠ On the group itself, as for a resource group's delete — it exists, so it has an object.
        var permitted = await authorizer.AuthorizeAsync(
            scope,
            Permissions.Write,
            Permissions.Read,
            caller,
            cancellationToken: cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result.Failure(denied);
        }

        var tenantGrains = grains.ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture));
        var group = tenantGrains.GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(scope.ManagementGroup));

        // ⚠ Success for a group that is already gone, and the grain answers with the parent it HAD,
        // so a re-driven DELETE after a crash between the record and the sweep still knows which
        // parent's child list and which edge to clear — ManagementGroupState.LastParent.
        var deleted = await group.DeleteAsync();
        if (deleted.TryGetError(out var deleteError)) {
            return Result.Failure(deleteError);
        }

        await SweepManagementGroupAsync(scope, deleted.GetValueOrThrow(), tenantGrains, cancellationToken);
        return Result.Success;
    }

    async Task SweepManagementGroupAsync(
        ScopeId scope,
        string parentName,
        TenantGrainFactory tenantGrains,
        CancellationToken cancellationToken
    ) {
        var unlisted = await tenantGrains
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(scope.TenantId))
            .RemoveManagementGroupAsync(scope.ManagementGroup);

        if (unlisted.TryGetError(out var unlistError)) {
            logger.LogError(
                "Management group '{Group}' is deleted but the tenant still lists it: {Message}. The "
                + "listing entry is filtered by the read and removed by the next DELETE.",
                scope.ManagementGroup,
                unlistError.Message
            );
        }

        if (parentName.Length > 0) {
            var unchilded = await tenantGrains
                .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(parentName))
                .RemoveChildAsync(scope.ManagementGroup);

            if (unchilded.TryGetError(out var unchildError)) {
                logger.LogError(
                    "Management group '{Group}' is deleted but '{Parent}' still lists it as a child: "
                    + "{Message}. Removed by the next DELETE.",
                    scope.ManagementGroup,
                    parentName,
                    unchildError.Message
                );
            }
        }

        // ⚠ EVERY tuple on the object and not only the parent edge — IScopeRelationWriter.ClearAsync
        // says why: the object id is the bare name, so a grant left on `managementGroup:{name}`
        // would be a grant on the next group created under that name, reaching every subscription
        // placed under it. The parent edge goes with them.
        var cleared = await relations.ClearAsync(scope, cancellationToken);
        if (cleared.TryGetError(out var clearError)) {
            logger.LogError(
                "Management group '{Group}' is deleted but tuples remain on its object: {Message}. "
                + "They are inert while no group has the name — the object resolves to nothing — and "
                + "the next DELETE sweeps them. Do not re-create a group under this name until one "
                + "succeeds: the tuples are its grants.",
                scope.ManagementGroup,
                clearError.Message
            );
        }
    }

    // ── Create: a resource group ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Creates a resource group in a subscription.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The lock is read off the subscription's own descriptor rather than through
    ///         <c>ILockResolver</c>, and the difference is one scope rather than a shortcut.
    ///     </b>
    ///     <c>ILockResolver.ResolveAsync</c> takes a <c>ResourceId</c> and walks resource → group →
    ///     subscription; a group being created has no resource below it and no group record of its own
    ///     yet, so the only link of that chain that exists is the subscription's. Calling the resolver
    ///     would mean inventing a <c>ResourceId</c> for an address that is not a resource, and reading
    ///     the same field one hop further away. The management group is not walked here for the reason
    ///     it is not walked there: docs/plan/06 § Tags, locks — the group exists since issue #39, its
    ///     record carries no lock, so a lock at that level cannot be set at all.
    /// </remarks>
    async Task<Result<ScopeSnapshot>> CreateGroupAsync(
        ScopeId scope,
        JsonElement body,
        CallerContext caller,
        CancellationToken cancellationToken
    ) {
        var subscription = grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId));

        // ⚠ The same question step 1 of the resource write path asks, answered the same way. The
        // grain is reached through ForTenant, which is what makes "exists" and "belongs to this
        // tenant" one question, and both answer with the canonical 404.
        var record = await subscription.GetAsync();
        if (record.IsFailure) {
            return NotFound(scope);
        }

        var permitted = await authorizer.AuthorizeAsync(
            ScopeId.Subscription(scope.TenantId, scope.SubscriptionId),
            Permissions.Write,
            Permissions.Read,
            caller,
            cancellationToken: cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result<ScopeSnapshot>.Failure(denied);
        }

        var descriptor = record.GetValueOrThrow();

        if (descriptor.Lock == LockLevel.ReadOnly) {
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.ScopeLocked,
                $"Subscription {scope.SubscriptionId:D} carries a ReadOnly lock, so no resource "
                + "group can be created in it — docs/plan/06 § Tags, locks. Clear the lock and "
                + "retry."
            );
        }

        var region = Text(body, LocationProperty);
        if (region.Length == 0) {
            return Result<ScopeSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A resource group needs a '{LocationProperty}'. It is the region its resources "
                + "default to (docs/plan/06 § The hierarchy) and there is no platform-wide default to "
                + "fall back on: a group whose region were guessed would place a tenant's data "
                + "somewhere nobody chose."
            );
        }

        var group = grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(scope.SubscriptionId, scope.ResourceGroup));

        var existed = (await group.GetAsync()).IsSuccess;

        var linked = await relations.LinkToParentAsync(scope, cancellationToken);
        if (linked.TryGetError(out var linkError)) {
            return Result<ScopeSnapshot>.Failure(linkError);
        }

        // ⚠ Through the SUBSCRIPTION and not straight at the group grain, which is where the name's
        // uniqueness and the subscription's listing come from — ISubscriptionGrain
        // .CreateResourceGroupAsync creates the group grain first and adds the listing entry after it
        // succeeds, so a group is never listed before it exists. Calling the group grain directly
        // would create a group no listing knew about, which is the emptiness step 7b of
        // docs/plan/08 § The write path, end to end was added to stop being possible.
        var created = await subscription.CreateResourceGroupAsync(scope.ResourceGroup, region);
        if (created.TryGetError(out var createError)) {
            return Result<ScopeSnapshot>.Failure(createError);
        }

        var descriptorOfGroup = created.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.ResourceGroup,
                Name = descriptorOfGroup.Name,
                Type = ScopeTypeNames.ResourceGroup,
                Location = descriptorOfGroup.Region,
                Created = !existed,
                Version = descriptorOfGroup.Version
            }
        );
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────

    async Task<Result<ScopeSnapshot>> ReadTenantAsync(ScopeId scope) {
        var record = await grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(scope.TenantId))
            .GetAsync();

        if (record.IsFailure) {
            return NotFound(scope);
        }

        var descriptor = record.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.Tenant,
                Name = descriptor.Slug,
                Type = ScopeTypeNames.Tenant,
                Location = descriptor.HomeRegion,
                Version = descriptor.Version
            }
        );
    }

    async Task<Result<ScopeSnapshot>> ReadSubscriptionAsync(ScopeId scope) {
        var record = await grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId))
            .GetAsync();

        if (record.IsFailure) {
            return NotFound(scope);
        }

        var descriptor = record.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.Subscription,
                Name = descriptor.DisplayName,
                Type = ScopeTypeNames.Subscription,
                Version = descriptor.Version,
                ManagementGroup = descriptor.ManagementGroup
            }
        );
    }

    async Task<Result<ScopeSnapshot>> ReadManagementGroupAsync(ScopeId scope) {
        var record = await grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(scope.ManagementGroup))
            .GetAsync();

        if (record.IsFailure) {
            return NotFound(scope);
        }

        var descriptor = record.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.ManagementGroup,
                Name = descriptor.DisplayName,
                Type = ScopeTypeNames.ManagementGroup,
                Version = descriptor.Version,
                ManagementGroup = descriptor.Parent
            }
        );
    }

    async Task<Result<ScopeSnapshot>> ReadGroupAsync(ScopeId scope) {
        var record = await grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(scope.SubscriptionId, scope.ResourceGroup))
            .GetAsync();

        if (record.IsFailure) {
            return NotFound(scope);
        }

        var descriptor = record.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.ResourceGroup,
                Name = descriptor.Name,
                Type = ScopeTypeNames.ResourceGroup,
                Location = descriptor.Region,
                Version = descriptor.Version
            }
        );
    }

    // ── Lists ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A tenant's subscriptions.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No check on the tenant, and the absence is the design rather than a gap.</b> The
    ///     parent is the tenant the token names, whose existence is not news to the caller, and
    ///     requiring <c>read</c> on it would hide every subscription from a caller who holds
    ///     <c>reader</c> on one subscription and nothing on the tenant — which is the ordinary shape
    ///     of a delegated grant. The per-member filter below is the whole of the authorization, and
    ///     Azure's <c>GET /subscriptions</c> answers the same question the same way.
    /// </remarks>
    async Task<Result<ScopeListPage>> ListSubscriptionsAsync(
        ScopeCollectionId collection,
        ScopeListRequest request,
        CancellationToken cancellationToken
    ) {
        var tenantScope = collection.Parent;

        var listed = await grains
            .ForTenant(tenantScope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(tenantScope.TenantId))
            .ListSubscriptionsAsync();

        if (listed.IsFailure) {
            return CollectionNotFound(tenantScope);
        }

        // ⚠ ORDERED BY THE ID IN ITS N FORM, ORDINALLY, AND THE ORDER IS WHAT MAKES PAGING WORK. The
        // continuation is "resume after this id" rather than an index, so the walk needs a total
        // order two requests agree on without either holding state. The N form is what
        // ReBacScopeAuthorizer.ObjectOf spells and what the tuple store keys, so a reader comparing
        // a continuation against a log line sees one spelling.
        var candidates = listed.GetValueOrThrow()
            .Select(id => (Key: id.ToString("N", CultureInfo.InvariantCulture),
                    Scope: ScopeId.Subscription(tenantScope.TenantId, id))
            )
            .Where(x => string.CompareOrdinal(x.Key, request.Continuation) > 0)
            .OrderBy(static x => x.Key, StringComparer.Ordinal)
            .Take(request.PageSize)
            .ToArray();

        return await PageAsync(collection, candidates, request, ReadSubscriptionAsync, cancellationToken);
    }

    /// <summary>
    ///     A tenant's management groups — every one, flat, ordered by name.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No check on the tenant, for the reason the subscription collection has none</b>: a
    ///     caller holding <c>reader</c> on one group and nothing on the tenant sees that group. The
    ///     per-member filter is the whole of the authorization, and the engine's own answer for this
    ///     collection is always the per-member path — <c>ReBacScopeAuthorizer.ListReadableAsync</c>
    ///     says why a scoped walk cannot see a flat listing of a tree.
    /// </remarks>
    async Task<Result<ScopeListPage>> ListManagementGroupsAsync(
        ScopeCollectionId collection,
        ScopeListRequest request,
        CancellationToken cancellationToken
    ) {
        var tenantScope = collection.Parent;

        var listed = await grains
            .ForTenant(tenantScope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(tenantScope.TenantId))
            .ListManagementGroupsAsync();

        if (listed.IsFailure) {
            return CollectionNotFound(tenantScope);
        }

        // Ordered by name, ordinally — a group's name is unique within its tenant by the grain
        // key's construction, and it is the continuation.
        var candidates = listed.GetValueOrThrow()
            .Select(name => (Key: name, Scope: ScopeId.ManagementGroupOf(tenantScope.TenantId, name)))
            .Where(x => string.CompareOrdinal(x.Key, request.Continuation) > 0)
            .OrderBy(static x => x.Key, StringComparer.Ordinal)
            .Take(request.PageSize)
            .ToArray();

        return await PageAsync(collection, candidates, request, ReadManagementGroupAsync, cancellationToken);
    }

    /// <summary>
    ///     A subscription's resource groups.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The check is on the subscription, first, and its refusal is the canonical 404.</b>
    ///     An empty page under a subscription the caller may not see would confirm the subscription
    ///     exists; the same sentence as a subscription that does not exist confirms nothing. The
    ///     explicit <c>GetAsync</c> after it is there because <c>ISubscriptionGrain.ListResourceGroupsAsync</c>
    ///     answers success with an empty list for a subscription nobody created — the same fact
    ///     <c>ResourceManagerService.ListAsync</c> records about the group grain — and a check that
    ///     passed on a direct tuple aimed at an id nothing created would otherwise page an empty
    ///     collection under an address that is not there.
    /// </remarks>
    async Task<Result<ScopeListPage>> ListGroupsAsync(
        ScopeCollectionId collection,
        ScopeListRequest request,
        CancellationToken cancellationToken
    ) {
        var subscriptionScope = collection.Parent;

        var permitted = await authorizer.AuthorizeAsync(
            subscriptionScope,
            Permissions.Read,
            Permissions.Read,
            request.Caller,
            cancellationToken: cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result<ScopeListPage>.Failure(denied);
        }

        var subscription = grains
            .ForTenant(subscriptionScope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscriptionScope.SubscriptionId));

        var exists = await subscription.GetAsync();
        if (exists.IsFailure) {
            return CollectionNotFound(subscriptionScope);
        }

        var listed = await subscription.ListResourceGroupsAsync();
        if (listed.IsFailure) {
            return CollectionNotFound(subscriptionScope);
        }

        // Ordered by name, ordinally — a group's name is unique within its subscription by the
        // grain's own construction, and it is the continuation.
        var candidates = listed.GetValueOrThrow()
            .Select(name => (Key: name,
                    Scope: ScopeId.Group(subscriptionScope.TenantId, subscriptionScope.SubscriptionId, name))
            )
            .Where(x => string.CompareOrdinal(x.Key, request.Continuation) > 0)
            .OrderBy(static x => x.Key, StringComparer.Ordinal)
            .Take(request.PageSize)
            .ToArray();

        return await PageAsync(collection, candidates, request, ReadGroupAsync, cancellationToken);
    }

    /// <summary>
    ///     The filter and the reads, shared by both collections: one <c>ListObjects</c> for the
    ///     page, a <c>Check</c> per member when the engine declines, then each survivor rendered by
    ///     the by-id read.
    /// </summary>
    /// <param name="collection">The collection — its parent is what the engine scopes the walk to, its member kind which walk.</param>
    /// <param name="candidates">The page, already ordered and cut, with each member's continuation key.</param>
    /// <param name="request">The request, for the caller and the page size.</param>
    /// <param name="read">The by-id read for this kind of member.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <remarks>
    ///     ⚠ <b>BOTH PERMISSIONS ARE THE READ PERMISSION</b>, so every refusal is a 404 that never
    ///     reaches the caller: it is a member that is not in the page. There is no third case for a
    ///     <c>GET</c>, and deliberately no count of what was dropped — <see cref="ScopeListPage" />.
    ///     ⚠ Not <c>fullyConsistent</c>, for the reason the resource collection is not: docs/plan/07
    ///     § Consistency reserves that for writes where a stale allow is an incident, and a read is
    ///     not on that list.
    /// </remarks>
    async Task<Result<ScopeListPage>> PageAsync(
        ScopeCollectionId collection,
        (string Key, ScopeId Scope)[] candidates,
        ScopeListRequest request,
        Func<ScopeId, Task<Result<ScopeSnapshot>>> read,
        CancellationToken cancellationToken
    ) {
        var visible = new List<ScopeId>(candidates.Length);

        var readable = candidates.Length > 0
            ? await authorizer.ListReadableAsync(
                collection,
                [.. candidates.Select(static x => x.Scope)],
                Permissions.Read,
                request.Caller,
                cancellationToken
            )
            : ScopeCollectionVisibility.Unanswered;

        if (readable.IsAnswered) {
            visible.AddRange(candidates.Select(static x => x.Scope).Where(readable.Visible.Contains));
        } else {
            foreach (var (_, scope) in candidates) {
                var authorized = await authorizer.AuthorizeAsync(
                    scope,
                    Permissions.Read,
                    Permissions.Read,
                    request.Caller,
                    cancellationToken: cancellationToken
                );

                if (authorized.IsSuccess) {
                    visible.Add(scope);
                }
            }
        }

        var items = new List<ScopeSnapshot>(visible.Count);

        foreach (var scope in visible) {
            var snapshot = await read(scope);

            if (snapshot.IsSuccess) {
                items.Add(snapshot.GetValueOrThrow());
            }
        }

        // ⚠ THE TOKEN IS THE LAST MEMBER EXAMINED AND NOT THE LAST ONE RETURNED — see the remarks
        // on ListAsync.
        var continuation = candidates.Length == request.PageSize ? candidates[^1].Key : "";

        return Result<ScopeListPage>.Success(new() { Items = items, Continuation = continuation });
    }

    // ── Shared ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses the path and checks the one thing a caller supplied: the tenant.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The tenant comparison is here as well as at the gateway's stage 3, deliberately.</b>
    ///     It is the same second defence <c>GatewayRoute</c>'s remarks describe for a resource path:
    ///     the gateway rebuilds the address from the token's tenant, and this check is the one that
    ///     still holds if somebody deletes that. <c>404</c> and never <c>403</c>, because a
    ///     cross-tenant path that answered "forbidden" would confirm the other tenant's scope exists.
    /// </remarks>
    static Result<ScopeId> Resolve(ScopeRequest request) {
        var parsed = ScopeId.ParsePath(request.Path);
        if (parsed.TryGetError(out var pathError)) {
            return Result<ScopeId>.Failure(pathError);
        }

        var scope = parsed.GetValueOrThrow();

        return scope.TenantId == request.Caller.TenantId
            ? Result<ScopeId>.Success(scope)
            : Result<ScopeId>.Failure(ErrorCode.ResourceNotFound, $"'{request.Path}' does not exist.");
    }

    static Result<JsonDocument> Parse(string body) {
        try {
            var document = JsonDocument.Parse(body.Length == 0 ? "{}" : body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Result<JsonDocument>.Success(document)
                : Result<JsonDocument>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"The request body is a JSON {document.RootElement.ValueKind.ToString().ToLowerInvariant()}. "
                    + "A scope body is a JSON object."
                );
        } catch (JsonException exception) {
            // The parser's message describes the caller's own input, not our stack —
            // docs/plan/08 § Errors bans exception detail, and this is not any.
            return Result<JsonDocument>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The request body is not valid JSON: {exception.Message}"
            );
        }
    }

    static string Text(JsonElement body, string property) =>
        body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    ///     A string property, or <see langword="null" /> when the body does not carry it — the
    ///     distinction <see cref="ScopeBodyProperties.ManagementGroup" /> rests on. A present
    ///     <c>null</c> reads as the empty string, so a client can clear with either.
    /// </summary>
    static string? OptionalText(JsonElement body, string property) =>
        body.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""
            : null;

    /// <summary>
    ///     Whether a management group name that arrived in a BODY is one the platform can address,
    ///     answered as the <c>400</c> every other body problem gets.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Before the name is turned into a <see cref="ScopeId" />, and that is the whole
    ///         point.
    ///     </b> A group named in the URL is validated by <see cref="ScopeId.ParsePath" />; a group
    ///     named in <see cref="ScopeBodyProperties.ManagementGroup" /> reaches
    ///     <see cref="ScopeId.ManagementGroupOf" /> unparsed, and the first thing to look at the name
    ///     after that is <c>GrainKeys.ManagementGroup</c> — through the authorizer's cache key or the
    ///     grain lookup — which throws <see cref="ArgumentException" /> for anything that is not
    ///     DNS-1123. Out of <see cref="IScopeManager.CreateAsync" /> that exception is the gateway's
    ///     <c>500</c>, for a body the caller can fix. The message is
    ///     <see cref="ResourceNaming.Validate" />'s, so the offending character is named, and the
    ///     target is the property's JSON pointer, so the portal can point at the field.
    /// </remarks>
    static Result EnsureGroupName(string name) =>
        ResourceNaming.Validate(name, "management group name", "/" + ManagementGroupProperty);

    static Result<ScopeSnapshot> NotFound(ScopeId scope) =>
        Result<ScopeSnapshot>.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ Byte-identical to the sentence ReBacScopeAuthorizer produces for a scope the caller
            // may not see. Two different messages would be the oracle the shared status code closed.
            $"'{scope.Path}' does not exist."
        );

    /// <summary>The canonical absence, for a collection whose parent is not there or not the caller's.</summary>
    /// <remarks>
    ///     ⚠ Names the <i>parent</i>, and with the same sentence the authorizer refuses it with, so
    ///     "the subscription does not exist", "the subscription is another tenant's" and "the caller
    ///     may not read the subscription" are one answer — which is the property.
    /// </remarks>
    static Result<ScopeListPage> CollectionNotFound(ScopeId parent) =>
        Result<ScopeListPage>.Failure(ErrorCode.ResourceNotFound, $"'{parent.Path}' does not exist.");
}
