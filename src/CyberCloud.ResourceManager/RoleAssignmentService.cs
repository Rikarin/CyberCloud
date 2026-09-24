using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Role assignment — the three steps a grant has, in the order a scope create has its four.
///     <see cref="IRoleAssignmentManager" />'s remarks carry the argument for why this is a third
///     entry point rather than a member of the second.
/// </summary>
/// <remarks>
///     <para>
///         <b>The order:</b>
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>Resolve.</b> Parse the address; the tenant in it must be the caller's; the role
///                 must be one of the three the schema calls a role and the principal type one the
///                 tuple store spells; the scope must exist — and for a resource, exist
///                 <i>
///                     as a
///                     confirmed index binding
///                 </i>, because that read is also what supplies the GUID the
///                 tuple is written on. Every refusal that could leak is the canonical <c>404</c>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Check.</b> <c>assignRole</c> on the scope itself for a write, <c>read</c> for
///                 a read, through the same two seams every other verb uses —
///                 <see cref="IScopeAuthorizer" /> for a scope and
///                 <see cref="IResourceAuthorizer" /> for a resource. The object is the scope and
///                 never its parent: an assignment is <i>about</i> a scope that exists, so it has a
///                 ReBAC object of its own, and a suspended owner of that object must be refused by
///                 that object's own <c>#suspended</c>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Write.</b> The tuple, through <see cref="IRoleAssignmentStore" />. Nothing after
///                 it: no lock (a grant changes none of a scope's contents), no parent edge (the
///                 object already has one), no change event (nothing a reconciler acts on moved), no
///                 <c>202</c> (one tuple write converges before the call returns).
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The write and the revoke are checked <c>FullyConsistent</c>; the read is not.</b>
///         docs/plan/07 § Consistency wants the cache bypassed for
///         <i>"anything where a stale allow is a real incident"</i>, and an owner whose ownership
///         was revoked a second ago granting themselves <c>owner</c> one scope down is the incident.
///         This is asked once per grant, which § Caching across requests calls rare.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The principal is looked up in the directory, after the check and before the write
///             (issue #86).
///         </b> Azure refuses an assignment to a principal the directory does not know, and until
///         #86 this wrote the tuple for any well-formed subject — a typo granted to nobody, and
///         nothing could ever see it. The lookup goes through <see cref="IPrincipalDirectory" />
///         rather than through <c>CyberCloud.Identity.Contracts</c>, which this assembly still does
///         not reference; that seam's remarks carry the argument. Two things about its position:
///         it is <i>after</i> <c>assignRole</c> so that a caller with no grant cannot use the
///         difference between two refusals to learn which principal ids exist, and it is asked of
///         the <b>assignment's</b> tenant, which is the caller's, so a principal from another tenant
///         is "does not exist" by construction rather than by a comparison somebody could delete.
///         The refusal is a <c>400</c> naming the principal, the same status the unknown-role and
///         unknown-type refusals above it use, because the address is the caller's own and the
///         caller has just proved they may grant here.
///     </para>
///     <para>
///         ⚠ <b>A revoke does not ask the directory.</b> The goal of a <c>DELETE</c> is the absence
///         of the tuple, and a tuple written before the check existed — or to a principal since
///         deprovisioned — must remain revocable, or it is a grant nobody can remove.
///     </para>
///     <para>
///         ⚠ <b>The collection is one check and no per-row filter</b> —
///         <see cref="IRoleAssignmentManager.ListAsync" />'s remarks say why that is the opposite of
///         a resource listing and still the right answer. What is shared with a resource listing is
///         the paging rule: ordered by address, resumed after the last address served.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c>.</b> Held by the gateway, which
///         is an Orleans <i>client</i>; <c>CC1006</c> keeps that true after the next edit.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A just-in-time role is an assignment with an <c>expiresOn</c>, and nothing more is
///             built (issue #49).
///         </b> The expiry rides on the tuple, the engine stops honouring it at that instant with no
///         write, and the store's sweep removes it and audits the end — docs/plan/07
///         § Time-bounded relations. Azure's PIM has more around it: an <i>eligible</i> assignment
///         that a principal <i>activates</i> for a bounded time, with a justification, a maximum
///         duration, and an approval. docs/plan/01's catalogue row calls the tuple with an expiry
///         "the whole feature" and describes none of that, so the eligible-to-active flow is
///         recorded as owed in the same section rather than invented here. What a tenant has
///         today is an owner granting a role that ends on its own.
///     </para>
/// </remarks>
public sealed class RoleAssignmentService(
    IScopeAuthorizer scopes,
    IResourceAuthorizer resources,
    IRoleAssignmentStore store,
    IPrincipalDirectory directory,
    IGrainFactory grains,
    IClock clock,
    ILogger<RoleAssignmentService> logger
)
    : IRoleAssignmentManager {
    /// <summary>
    ///     The seven relations a tenant may grant — docs/plan/07 § Azure RBAC, expressed in it's
    ///     <c>Owner</c>, <c>Contributor</c> and <c>Reader</c>, and the four key-vault data-plane
    ///     roles of docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A closed set here, over and above the schema's own check.</b> <c>TupleStoreGrain</c>
    ///         refuses a relation the type does not declare, but it accepts every relation it does —
    ///         <c>parent</c>, <c>suspended</c>, <c>member</c> — and each of those written through this
    ///         path would be something other than a role assignment wearing its address. A deny
    ///         assignment is Azure's <c>denyAssignments</c>, a different resource type, and it is not
    ///         built; a parent edge is the scope path's and nobody else's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The four data-plane roles are grantable at every scope, as Azure's are</b> — a
    ///         Secrets User on a resource group reads every vault in it — and granting one needs
    ///         <c>assignRole</c> like any other, so a vault's owner is the person who decides who
    ///         reads its secrets without being, by that fact, one of them.
    ///     </para>
    /// </remarks>
    public static FrozenSet<string> GrantableRoles { get; } =
        new[] {
            Relations.Owner, Relations.Contributor, Relations.Reader, Relations.KeyVaultSecretsOfficer,
            Relations.KeyVaultSecretsUser, Relations.KeyVaultCryptoOfficer, Relations.KeyVaultCryptoUser
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The principal types an assignment may name: the three subject types, <c>group</c>, which
    ///     is granted through its <c>member</c> userset, and <c>resource</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>resource</c> is a principal here and is not a subject type, and the two sets
    ///             differ on purpose.
    ///         </b> <c>SubjectTypes.All</c> is what a token can carry — what can sign in. A resource
    ///         never signs in; it acts only from its own reconcile pass, where <c>ReconcileDriver</c>
    ///         makes it the subject of every cross-resource read (<see cref="IResourceView" /> has the
    ///         rule). Granting <c>reader</c> to <c>resource:{vault}</c> on a resource group is how a
    ///         tenant lets the vault see the shares in it — Azure's system-assigned identity, without
    ///         the identity: the resource's own GUID is the principal id, so there is no second object
    ///         to create, rotate or leak. Nothing is granted to a resource implicitly.
    ///     </para>
    ///     <para>
    ///         ⚠ Its existence is answered by <see cref="IResourceGrain" /> in the assignment's
    ///         tenant rather than by <see cref="IPrincipalDirectory" />, because the directory is the
    ///         identity module's and a resource is this module's; asking identity about a resource
    ///         would be a question it has no way to answer. The tenant scoping is the same as the
    ///         directory's: the grain is reached <c>ForTenant</c>, so another tenant's resource GUID
    ///         is "does not exist" by construction.
    ///     </para>
    /// </remarks>
    public static FrozenSet<string> PrincipalTypes { get; } =
        SubjectTypes.All.Append(ObjectTypes.Group).Append(ObjectTypes.Resource).ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<Result<RoleAssignmentSnapshot>> AssignAsync(
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveAsync(request);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<RoleAssignmentSnapshot>.Failure(resolveError);
        }

        var assignment = resolved.GetValueOrThrow();

        var agreed = BodyAgrees(request.Body, assignment, clock.UtcNow);
        if (agreed.TryGetError(out var bodyError)) {
            return Result<RoleAssignmentSnapshot>.Failure(bodyError);
        }

        var expiresOn = agreed.GetValueOrThrow().ExpiresOn;

        var permitted = await AuthorizeAsync(
            assignment,
            Permissions.AssignRole,
            request.Caller,
            true,
            cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result<RoleAssignmentSnapshot>.Failure(denied);
        }

        var known = await ExistsAsync(assignment, cancellationToken);

        if (known.TryGetError(out var directoryError)) {
            // ⚠ Refused, never granted on a guess. An unanswerable directory is an unwired seam or an
            // unreachable grain, and either way the tuple this would write is one nothing checked.
            logger.LogError(
                "{Caller} asked to grant '{Role}' on '{Scope}' to {PrincipalType}:{PrincipalId}, and the "
                + "directory could not say whether the principal exists: {Message}",
                request.Caller,
                assignment.Name.Role,
                assignment.ScopePath,
                assignment.Name.PrincipalType,
                assignment.Name.PrincipalId,
                directoryError.Message
            );

            return Result<RoleAssignmentSnapshot>.Failure(directoryError);
        }

        if (!known.GetValueOrThrow()) {
            return Result<RoleAssignmentSnapshot>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{assignment.Name.PrincipalType}:{assignment.Name.PrincipalId}' is not a principal in "
                + $"tenant '{assignment.TenantId:D}', so nothing can be granted to it. A principal is a "
                + "user, a service principal, a managed identity or a group that exists in the "
                + "assignment's own tenant, named by the id its directory object carries — "
                + "docs/plan/11 § The object model — or a resource that exists in it, named by its GUID "
                + "(docs/plan/08 § What the resource manager deliberately does not do). A principal from "
                + "another tenant is not one either: a user belongs to exactly one tenant (docs/plan/11 "
                + "§ Sign-up and tenant creation), and a resource to exactly one."
            );
        }

        var existed = await store.FindAsync(assignment, cancellationToken);
        if (existed.TryGetError(out var readError)) {
            return Result<RoleAssignmentSnapshot>.Failure(readError);
        }

        var granted = await store.GrantAsync(assignment, expiresOn, cancellationToken);
        if (granted.TryGetError(out var grantError)) {
            return Result<RoleAssignmentSnapshot>.Failure(grantError);
        }

        if (expiresOn is { } until) {
            logger.LogInformation(
                "{Caller} granted '{Role}' on '{Scope}' to {PrincipalType}:{PrincipalId} until {ExpiresOn:O}.",
                request.Caller,
                assignment.Name.Role,
                assignment.ScopePath,
                assignment.Name.PrincipalType,
                assignment.Name.PrincipalId,
                until
            );
        } else {
            logger.LogInformation(
                "{Caller} granted '{Role}' on '{Scope}' to {PrincipalType}:{PrincipalId}.",
                request.Caller,
                assignment.Name.Role,
                assignment.ScopePath,
                assignment.Name.PrincipalType,
                assignment.Name.PrincipalId
            );
        }

        return Result<RoleAssignmentSnapshot>.Success(
            Snapshot(assignment, !existed.GetValueOrThrow().Granted, expiresOn)
        );
    }

    /// <inheritdoc />
    public async Task<Result<RoleAssignmentSnapshot>> ReadAsync(
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveAsync(request);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<RoleAssignmentSnapshot>.Failure(resolveError);
        }

        var assignment = resolved.GetValueOrThrow();

        var allowed = await AuthorizeAsync(assignment, Permissions.Read, request.Caller, false, cancellationToken);
        if (allowed.TryGetError(out var denied)) {
            return Result<RoleAssignmentSnapshot>.Failure(denied);
        }

        var found = await store.FindAsync(assignment, cancellationToken);
        if (found.TryGetError(out var readError)) {
            return Result<RoleAssignmentSnapshot>.Failure(readError);
        }

        var grant = found.GetValueOrThrow();

        return grant.Granted
            ? Result<RoleAssignmentSnapshot>.Success(Snapshot(assignment, false, grant.ExpiresOn))
            : NotFound<RoleAssignmentSnapshot>(assignment.Path);
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveAsync(request);
        if (resolved.TryGetError(out var resolveError)) {
            return Result.Failure(resolveError);
        }

        var assignment = resolved.GetValueOrThrow();

        var permitted = await AuthorizeAsync(
            assignment,
            Permissions.AssignRole,
            request.Caller,
            true,
            cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result.Failure(denied);
        }

        var revoked = await store.RevokeAsync(assignment, cancellationToken);
        if (revoked.TryGetError(out var revokeError)) {
            return Result.Failure(revokeError);
        }

        logger.LogInformation(
            "{Caller} revoked '{Role}' on '{Scope}' from {PrincipalType}:{PrincipalId}.",
            request.Caller,
            assignment.Name.Role,
            assignment.ScopePath,
            assignment.Name.PrincipalType,
            assignment.Name.PrincipalId
        );

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<RoleAssignmentPage>> ListAsync(
        RoleAssignmentListRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var parsed = RoleAssignmentCollectionId.ParsePath(request.Path);
        if (parsed.TryGetError(out var pathError)) {
            return Result<RoleAssignmentPage>.Failure(pathError);
        }

        var resolved = await ResolveScopeAsync(parsed.GetValueOrThrow(), request.Caller);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<RoleAssignmentPage>.Failure(resolveError);
        }

        var collection = resolved.GetValueOrThrow();

        var allowed = await AuthorizeAsync(collection, Permissions.Read, request.Caller, false, cancellationToken);
        if (allowed.TryGetError(out var denied)) {
            return Result<RoleAssignmentPage>.Failure(denied);
        }

        var listed = await store.ListAsync(collection, cancellationToken);
        if (listed.TryGetError(out var listError)) {
            return Result<RoleAssignmentPage>.Failure(listError);
        }

        // ⚠ Ordered by address and resumed by address, which is the rule every collection of this
        // API pages by (ListRequest.Continuation). The addresses are distinct — one tuple, one
        // address — so "the first row after the token" is well defined, and a grant or a revoke
        // between two pages moves only its own row.
        // ⚠ The resume filter was lost in the 2026-09-18 reformat, which turned it into a second
        // OrderBy — every page after the first repeated the first, and
        // RoleAssignmentTests.TheCollectionIsPagedByAddressAndAPageIsNeverSilentlyShort went red on it.
        var rows = listed.GetValueOrThrow()
            .OrderBy(static x => x.Path, StringComparer.Ordinal)
            .Where(x => request.Continuation.Length == 0 || string.CompareOrdinal(x.Path, request.Continuation) > 0)
            .Take(request.PageSize + 1)
            .ToList();

        var hasMore = rows.Count > request.PageSize;
        if (hasMore) {
            rows.RemoveAt(rows.Count - 1);
        }

        return Result<RoleAssignmentPage>.Success(
            new() { Assignments = [.. rows], Continuation = hasMore ? rows[^1].Path : string.Empty }
        );
    }

    // ── The principal exists ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether the principal an assignment names exists in the assignment's tenant: the directory
    ///     for a user, service principal, managed identity or group; the resource grain for a
    ///     resource.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A resource principal is spelled as its GUID in <c>N</c> form and nothing else.</b>
    ///     That is the spelling <c>ResourceViews.CallerFor</c> puts in the subject when the resource
    ///     reads, so a grant spelled any other way would be a tuple the check never matches — a
    ///     grant to nobody, which is what issue #86 closed for directory principals. It is refused
    ///     rather than normalized, because the address is the caller's own and echoing it back
    ///     corrected would leave two spellings of one assignment in the tenant's listing.
    /// </remarks>
    async Task<Result<bool>> ExistsAsync(RoleAssignmentId assignment, CancellationToken cancellationToken) {
        var name = assignment.Name;

        if (!string.Equals(name.PrincipalType, ObjectTypes.Resource, StringComparison.Ordinal)) {
            return await directory.ExistsAsync(
                assignment.TenantId,
                name.PrincipalType,
                name.PrincipalId,
                cancellationToken
            );
        }

        if (!Guid.TryParseExact(name.PrincipalId, "N", out var resourceId) || resourceId == Guid.Empty) {
            return Result<bool>.Success(false);
        }

        var snapshot = await grains
            .ForTenant(assignment.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceGrain>(GrainKeys.Resource(resourceId))
            .GetAsync(string.Empty, []);

        if (snapshot.IsSuccess) {
            return Result<bool>.Success(true);
        }

        return snapshot.Error is { Code: var code } && code == ErrorCode.ResourceNotFound
            ? Result<bool>.Success(false)
            : Result<bool>.Failure(snapshot.Error!);
    }

    // ── Step 1: resolve ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses the address, checks what the caller supplied, and confirms the scope exists —
    ///     resolving a resource's GUID on the way, because the same index read answers both.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tenant comparison is here as well as at the gateway's stage 3</b>, for the
    ///         reason <c>ScopeManagerService.Resolve</c> gives: it is the defence that still holds if
    ///         somebody deletes that one. <c>404</c> and never <c>403</c>.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The role and the principal type are refused here as a <c>400</c>, before any
    ///             grain is touched.
    ///         </b> Both are the caller's own URL and neither is a secret, so the
    ///         enumeration argument does not apply; and refusing them after the check would let a
    ///         caller with no grant at all learn which relation names exist from the difference
    ///         between two <c>404</c> messages.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Existence is read from the scope's own grain and not inferred from the check.</b>
    ///         A check on a scope that does not exist fails closed — no tuple, no parent edge, no
    ///         answer but <c>false</c> — so the inference would usually hold. It would not hold for
    ///         the residue <see cref="IScopeRelationWriter.LinkToParentAsync(ScopeId, CancellationToken)" />'s remarks
    ///         describe:
    ///         a <c>parent</c> edge aimed at a scope whose create then failed. The edge is inert for
    ///         every other purpose; through this path it would let the tenant's owner write role
    ///         tuples on a subscription that was never created.
    ///     </para>
    /// </remarks>
    async Task<Result<RoleAssignmentId>> ResolveAsync(RoleAssignmentRequest request) {
        var parsed = RoleAssignmentId.ParsePath(request.Path);
        if (parsed.TryGetError(out var pathError)) {
            return Result<RoleAssignmentId>.Failure(pathError);
        }

        var assignment = parsed.GetValueOrThrow();

        if (assignment.TenantId != request.Caller.TenantId) {
            return NotFound<RoleAssignmentId>(assignment.ScopePath);
        }

        if (!GrantableRoles.Contains(assignment.Name.Role)) {
            return Result<RoleAssignmentId>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{assignment.Name.Role}' is not a role that can be assigned. The roles are "
                + $"[{string.Join(", ", GrantableRoles.Order(StringComparer.Ordinal))}] — "
                + "docs/plan/07 § Azure RBAC, expressed in it. A deny assignment is a different "
                + "resource type and is not served by this address."
            );
        }

        if (!PrincipalTypes.Contains(assignment.Name.PrincipalType)) {
            return Result<RoleAssignmentId>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{assignment.Name.PrincipalType}' is not a principal type. The set is closed and "
                + $"is [{string.Join(", ", PrincipalTypes.Order(StringComparer.Ordinal))}], spelled "
                + "exactly so — it is a ReBAC subject type and the tuple store matches it ordinally."
            );
        }

        var scope = await ResolveScopeAsync(RoleAssignmentCollectionId.Of(assignment), request.Caller);

        return scope.TryGetError(out var scopeError)
            ? Result<RoleAssignmentId>.Failure(scopeError)
            : Result<RoleAssignmentId>.Success(scope.GetValueOrThrow().Member(assignment.Name));
    }

    /// <summary>
    ///     The scope half of <see cref="ResolveAsync" />, shared with the collection: the tenant
    ///     must be the caller's and the scope must exist — a resource as a confirmed index binding,
    ///     which is also what supplies the id its ReBAC object is named by.
    /// </summary>
    /// <returns>The scope, with a resource's id resolved; or the canonical <c>404</c>.</returns>
    async Task<Result<RoleAssignmentCollectionId>> ResolveScopeAsync(
        RoleAssignmentCollectionId collection,
        CallerContext caller
    ) {
        if (collection.TenantId != caller.TenantId) {
            return NotFound<RoleAssignmentCollectionId>(collection.ScopePath);
        }

        var tenant = grains.ForTenant(collection.TenantId.ToString("D", CultureInfo.InvariantCulture));

        if (collection.IsResourceScoped) {
            // docs/plan/06 § Identifiers: a parsed path yields Guid.Empty, and only a CONFIRMED
            // binding resolves — a name under an unexpired claim reads as "does not exist", which
            // is what it is, and a parked resource is not addressable here either.
            var bound = await tenant
                .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(collection.Resource))
                .ResolveAsync();

            return bound.IsFailure
                ? NotFound<RoleAssignmentCollectionId>(collection.ScopePath)
                : Result<RoleAssignmentCollectionId>.Success(
                    collection with { Resource = collection.Resource.WithId(bound.GetValueOrThrow()) }
                );
        }

        var scope = collection.Scope;

        var exists = scope.Kind switch {
            ScopeKind.Tenant => (await tenant
                    .GetGrain<ITenantGrain>(GrainKeys.Tenant(scope.TenantId))
                    .GetAsync()).IsSuccess,
            ScopeKind.Subscription => (await tenant
                    .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId))
                    .GetAsync()).IsSuccess,
            ScopeKind.ResourceGroup => (await tenant
                    .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(scope.SubscriptionId, scope.ResourceGroup))
                    .GetAsync()).IsSuccess,
            // #70's assignments at the new scope, issue #39 — the same existence question, asked of
            // the group's own grain.
            ScopeKind.ManagementGroup => (await tenant
                    .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(scope.ManagementGroup))
                    .GetAsync()).IsSuccess,
            _ => false
        };

        return exists
            ? Result<RoleAssignmentCollectionId>.Success(collection)
            : NotFound<RoleAssignmentCollectionId>(collection.ScopePath);
    }

    // ── Step 2: check ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The check, on whichever seam the scope's kind selects. <c>read</c> is always the
    ///     permission that decides between <c>404</c> and <c>403</c>.
    /// </summary>
    Task<Result> AuthorizeAsync(
        RoleAssignmentId assignment,
        string permission,
        CallerContext caller,
        bool fullyConsistent,
        CancellationToken cancellationToken
    ) =>
        AuthorizeAsync(
            RoleAssignmentCollectionId.Of(assignment),
            permission,
            caller,
            fullyConsistent,
            cancellationToken
        );

    Task<Result> AuthorizeAsync(
        RoleAssignmentCollectionId scope,
        string permission,
        CallerContext caller,
        bool fullyConsistent,
        CancellationToken cancellationToken
    ) =>
        scope.IsResourceScoped
            ? resources.AuthorizeAsync(
                scope.Resource,
                permission,
                Permissions.Read,
                caller,
                fullyConsistent,
                cancellationToken
            )
            : scopes.AuthorizeAsync(
                scope.Scope,
                permission,
                Permissions.Read,
                caller,
                fullyConsistent,
                cancellationToken
            );

    // ── Shared ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether the body, when it says anything, says what the address says.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every property is optional and a present one must agree — see
    ///     <see cref="RoleAssignmentBodyProperties" />. The comparison is ordinal on all three,
    ///     because all three are matched ordinally by the tuple store, and a body that said
    ///     <c>Reader</c> for an address that said <c>reader</c> is a client that has two spellings
    ///     of one thing and is about to have a worse day elsewhere.
    /// </remarks>
    static Result<AssignmentBody> BodyAgrees(string body, RoleAssignmentId assignment, DateTimeOffset now) {
        JsonDocument document;

        try {
            document = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        } catch (JsonException exception) {
            // The parser's message describes the caller's own input, not our stack —
            // docs/plan/08 § Errors bans exception detail, and this is not any.
            return Result<AssignmentBody>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The request body is not valid JSON: {exception.Message}"
            );
        }

        using (document) {
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return Result<AssignmentBody>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"The request body is a JSON {document.RootElement.ValueKind.ToString().ToLowerInvariant()}. "
                    + "A role assignment body is a JSON object, and '{}' is a complete one — the "
                    + "address already names the role and the principal."
                );
            }

            var name = assignment.Name;

            var disagreement = Agree(document.RootElement, RoleAssignmentBodyProperties.RoleDefinitionId, name.Role)
                ?? Agree(document.RootElement, RoleAssignmentBodyProperties.PrincipalType, name.PrincipalType)
                ?? Agree(document.RootElement, RoleAssignmentBodyProperties.PrincipalId, name.PrincipalId);

            if (disagreement is { } refused) {
                return Result<AssignmentBody>.Failure(refused.Error!);
            }

            return ExpiresOn(document.RootElement, now);
        }
    }

    /// <summary>
    ///     The body's <c>expiresOn</c>: absent or <c>null</c> for a permanent grant, otherwise an ISO
    ///     8601 instant with an explicit offset that is later than now.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The offset is required, not assumed.</b> <c>System.Text.Json</c> reads a timestamp
    ///     with no offset as the parsing host's local time, so <c>2026-09-24T09:00:00</c> would end
    ///     a grant at an instant that depends on which gateway replica took the request. Refusing it
    ///     costs a client one character; accepting it costs an owner a grant that ends an hour early
    ///     or late with nothing in the response to say so. The stored instant is UTC, which is what
    ///     every read renders.
    /// </remarks>
    static Result<AssignmentBody> ExpiresOn(JsonElement body, DateTimeOffset now) {
        if (!body.TryGetProperty(RoleAssignmentBodyProperties.ExpiresOn, out var value)
            || value.ValueKind == JsonValueKind.Null) {
            return Result<AssignmentBody>.Success(new(null));
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        if (value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out var parsed)
            || !HasExplicitOffset(text)) {
            return Result<AssignmentBody>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The body's '{RoleAssignmentBodyProperties.ExpiresOn}' is "
                + $"'{(value.ValueKind == JsonValueKind.String ? text : value.ValueKind.ToString().ToLowerInvariant())}', "
                + "which is not an ISO 8601 instant with an offset — for example '2026-09-24T09:00:00Z'. "
                + "It is when the grant ends, and it needs the offset so that the instant does not "
                + "depend on where it was parsed. Leave it out, or send null, for a permanent grant."
            );
        }

        var expiresOn = parsed.ToUniversalTime();

        if (expiresOn <= now) {
            return Result<AssignmentBody>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The body's '{RoleAssignmentBodyProperties.ExpiresOn}' is {expiresOn:O}, which is not later "
                + $"than now ({now.ToUniversalTime():O}). A grant that has already ended grants nothing — "
                + "docs/plan/07 § Time-bounded relations. To end a grant now, DELETE it."
            );
        }

        return Result<AssignmentBody>.Success(new(expiresOn));
    }

    static bool HasExplicitOffset(string text) {
        // The shapes ISO 8601 allows for an offset: 'Z', or ±hh:mm / ±hhmm / ±hh after the time.
        var time = text.IndexOf('T', StringComparison.OrdinalIgnoreCase);
        if (time < 0) {
            return false;
        }

        if (text.EndsWith('Z') || text.EndsWith('z')) {
            return true;
        }

        var tail = text[(time + 1)..];
        return tail.Contains('+', StringComparison.Ordinal) || tail.Contains('-', StringComparison.Ordinal);
    }

    /// <summary>What a <c>PUT</c> body says beyond what the address already does.</summary>
    /// <param name="ExpiresOn">When the grant ends, in UTC, or <see langword="null" /> for a permanent grant.</param>
    readonly record struct AssignmentBody(DateTimeOffset? ExpiresOn);

    static Result? Agree(JsonElement body, string property, string expected) {
        if (!body.TryGetProperty(property, out var value)) {
            return null;
        }

        var actual = value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? null
            : Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"The body's '{property}' is '{actual ?? value.ValueKind.ToString().ToLowerInvariant()}' and "
                + $"the address says '{expected}'. The address is the assignment — "
                + "'{role}-{principalType}-{principalId}' — so a body property is optional and, when "
                + "present, must agree with it; trusting either one silently would grant something "
                + "the caller did not spell."
            );
    }

    static RoleAssignmentSnapshot Snapshot(RoleAssignmentId assignment, bool created, DateTimeOffset? expiresOn) =>
        new() {
            Path = assignment.Path,
            Name = assignment.Name.Render(),
            Scope = assignment.ScopePath,
            RoleDefinitionId = assignment.Name.Role,
            PrincipalType = assignment.Name.PrincipalType,
            PrincipalId = assignment.Name.PrincipalId,
            Created = created,
            ExpiresOn = expiresOn
        };

    static Result<T> NotFound<T>(string path) where T : notnull =>
        Result<T>.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ Byte-identical to the sentence both authorizers produce for an object the caller may
            // not see. Two different messages would be the oracle the shared status code closed.
            $"'{path}' does not exist."
        );
}
