using CyberCloud.Authorization.Contracts;
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
///         ⚠ <b>No quota, no index claim, no membership record, no desired state, no operation and no
///         <c>202</c>.</b> Each of those absences is a property of a scope rather than an omission,
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
    ILogger<ScopeManagerService> logger
)
    : IScopeManager {
    /// <summary>
    ///     The property name a resource group's region arrives under.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read from <see cref="ScopeBodyProperties" /> rather than declared here, and the move
    ///     is the point.</b> The generated surfaces live in the contracts assembly and cannot see
    ///     this one, so a copy here and a copy there would be two constants agreeing by hand — the
    ///     failure this repository keeps re-finding. Issue #63 is what made a second reader exist.
    /// </remarks>
    public const string LocationProperty = ScopeBodyProperties.Location;

    /// <summary>The body property a subscription's display name arrives in.</summary>
    /// <remarks>⚠ <see cref="ScopeBodyProperties" />'s, for the reason above.</remarks>
    public const string DisplayNameProperty = ScopeBodyProperties.DisplayName;

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

        return scope.Kind == ScopeKind.Subscription
            ? await CreateSubscriptionAsync(scope, document.RootElement, request.Caller, cancellationToken)
            : await CreateGroupAsync(scope, document.RootElement, request.Caller, cancellationToken);
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
            _ => await ReadGroupAsync(scope)
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
        var assigned = await grains
            .GetGrain<IShardMapGrain>(GrainKeys.ShardMap())
            .AssignAsync(request.TenantId, request.HomeRegion);

        if (assigned.TryGetError(out var shardError)) {
            return Result<ScopeSnapshot>.Failure(shardError);
        }

        var assignment = assigned.GetValueOrThrow();

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
    ///     ⚠ <b>The permission is <c>write</c> on the TENANT and not <c>delete</c> or a bespoke
    ///     "createSubscription", and the reason is that the schema has four permissions and this is
    ///     the one that means "change what is inside".</b> docs/plan/07 § Azure RBAC, expressed in it
    ///     maps <c>write</c> to <c>Rel(contributor)</c>, and Azure's own Contributor on a scope creates
    ///     children in it. Requiring <c>delete</c> — that is, <c>owner</c> — would be stricter than
    ///     Azure and would make "may create a subscription" and "may delete the tenant" the same
    ///     right, which is a worse thing to hand out. A separable "may create a subscription and
    ///     nothing else" needs a grantable relation of its own and a role-assignment story
    ///     docs/plan/07 does not yet have — the same gap its <c>purge</c> remarks record as owed.
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

        var existed = (await subscription.GetAsync()).IsSuccess;

        // ── The parent edge, before the durable write. See the type's remarks. ──────────────────
        var linked = await relations.LinkToParentAsync(scope, cancellationToken);
        if (linked.TryGetError(out var linkError)) {
            return Result<ScopeSnapshot>.Failure(linkError);
        }

        var created = await subscription.CreateAsync(displayName);
        if (created.TryGetError(out var createError)) {
            return Result<ScopeSnapshot>.Failure(createError);
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

        var descriptor = created.GetValueOrThrow();

        return Result<ScopeSnapshot>.Success(
            new() {
                Path = scope.Path,
                Kind = ScopeKind.Subscription,
                Name = descriptor.DisplayName,
                Type = ScopeTypeNames.Subscription,
                Created = !existed,
                Version = descriptor.Version
            }
        );
    }

    // ── Create: a resource group ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Creates a resource group in a subscription.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The lock is read off the subscription's own descriptor rather than through
    ///     <c>ILockResolver</c>, and the difference is one scope rather than a shortcut.</b>
    ///     <c>ILockResolver.ResolveAsync</c> takes a <c>ResourceId</c> and walks resource → group →
    ///     subscription; a group being created has no resource below it and no group record of its own
    ///     yet, so the only link of that chain that exists is the subscription's. Calling the resolver
    ///     would mean inventing a <c>ResourceId</c> for an address that is not a resource, and reading
    ///     the same field one hop further away. The management group is not walked here for the reason
    ///     it is not walked there: docs/plan/06 § Tags, locks — a lock at that level cannot be set at
    ///     all.
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
                Version = descriptor.Version
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
        }
        catch (JsonException exception) {
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

    static Result<ScopeSnapshot> NotFound(ScopeId scope) =>
        Result<ScopeSnapshot>.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ Byte-identical to the sentence ReBacScopeAuthorizer produces for a scope the caller
            // may not see. Two different messages would be the oracle the shared status code closed.
            $"'{scope.Path}' does not exist."
        );
}
