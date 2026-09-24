using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The one place a tenant grants, reads back, lists, or revokes a role — the write half of
///     docs/plan/07 § Azure RBAC, expressed in it, which until this existed had only a read half.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             NOTHING IN THE PLATFORM COULD WRITE A ROLE TUPLE, AND THAT MADE THE M1 EXIT STORY
///             UNBUILDABLE (issue #70).
///         </b> The only grant above <c>ITupleStoreGrain</c> was
///         <see cref="IScopeRelationWriter.GrantOwnerAsync" />, called once at tenant creation, and
///         nothing anywhere wrote <c>contributor</c> or <c>reader</c>. So
///         <i>
///             "invite a colleague and
///             grant them Reader on one resource group"
///         </i> had no request that did the second half.
///         This is that request.
///     </para>
///     <para>
///         ⚠
///         <b>
///             BESIDE <see cref="IScopeManager" /> AND NOT A MEMBER OF IT, for the reason that one
///             is beside <see cref="IResourceManager" />.
///         </b> A role assignment is not a scope — it is a
///         tuple <i>on</i> one — and the four things a scope create does (resolve, check, lock, parent
///         edge) are not what an assignment does. Its steps are: resolve the address to a ReBAC
///         object, check <c>assignRole</c> on that object, write or delete the tuple. No lock, because
///         a lock is about a scope's <i>contents</i> and a grant changes none of them; no parent edge,
///         because the object already has one. What all three managers share is the
///         <see cref="CallerContext" /> and the 404-never-403 seam, and the mitigations
///         <see cref="IScopeManager" />'s remarks list for the second entry point are the same for
///         the third.
///     </para>
///     <para>
///         ⚠ <b>The permission is <c>assignRole</c> and nothing weaker.</b> <c>CyberCloudSchema</c>
///         defines it on every scope type as <c>Rel(owner) &amp; !Rel(suspended)</c>, so only an owner
///         of the scope may grant on it and a suspended owner may not — which is exactly Azure's
///         <c>Microsoft.Authorization/roleAssignments/write</c> sitting in <c>Owner</c> and in no
///         built-in role beneath it. A contributor holding <c>assignRole</c> would be a contributor
///         who can make themselves an owner.
///     </para>
///     <para>
///         ⚠ <b>The name is the tuple, so a repeated <c>PUT</c> is the same tuple.</b>
///         <c>RoleAssignmentName</c> carries the decision; what it means here is that
///         <see cref="AssignAsync" /> needs no "did I already?" record of its own, and that the body's
///         properties are optional and must agree with the address when present.
///     </para>
///     <para>
///         ⚠ <b>A service and not a grain</b>, held by the gateway, with every grain reference through
///         <c>ForTenant</c> — the same position and the same rule as its two neighbours.
///     </para>
/// </remarks>
public interface IRoleAssignmentManager {
    /// <summary>
    ///     Grants a role at a scope. <c>PUT</c> on a role assignment path. Idempotent.
    /// </summary>
    /// <param name="request">The request, as the gateway parsed it off the URL and the body.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The assignment as it now stands, with <see cref="RoleAssignmentSnapshot.Created" /> saying
    ///     whether this call wrote it; or the first step's failure.
    ///     <para>
    ///         ⚠ A refusal the caller may not see through is
    ///         <see cref="ErrorCode.ResourceNotFound" /> and never
    ///         <see cref="ErrorCode.AuthorizationFailed" />, exactly as for a scope: a caller who cannot
    ///         read the scope learns nothing about it, and one who can read it but does not hold
    ///         <c>assignRole</c> gets the <c>403</c>.
    ///     </para>
    /// </returns>
    Task<Result<RoleAssignmentSnapshot>> AssignAsync(
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Reads one assignment. <c>GET</c> on a role assignment path.</summary>
    /// <param name="request">The request. <see cref="RoleAssignmentRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The assignment, or <see cref="ErrorCode.ResourceNotFound" /> — for a tuple that is not
    ///     written <i>and</i> for a scope the caller may not read, which is the same answer on purpose.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Direct tuples only.</b> An assignment inherited from a parent scope has no tuple at
    ///     this one and reads as absent here, which is what docs/plan/07 § Azure RBAC, expressed in
    ///     it's third table row means by <i>"no role tuples written per resource"</i>. The inherited
    ///     view is <see cref="ListAsync" />, which reports it at this scope with
    ///     <see cref="RoleAssignmentSnapshot.Inherited" /> set and its own address at the ancestor.
    /// </remarks>
    Task<Result<RoleAssignmentSnapshot>> ReadAsync(
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Lists what is assigned at a scope, direct and inherited. <c>GET</c> on a role assignment
    ///     collection path. Paged.
    /// </summary>
    /// <param name="request">The request, as the gateway parsed it off the URL and the query.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     One page, ordered by each assignment's address; or <see cref="ErrorCode.ResourceNotFound" />
    ///     for a scope that does not exist <i>and</i> for one the caller may not read, which is the
    ///     same answer on purpose.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             One check, on the scope, and no per-member filter — which is the opposite of
    ///             <see cref="IResourceManager.ListAsync" />.
    ///         </b> A resource listing hides the members
    ///         the caller may not read because each member is an object with its own tuples. An
    ///         assignment is not an object; it is a tuple <i>on</i> the scope, and Azure's
    ///         <c>roleAssignments/read</c> sits in Reader for exactly that reason. So a caller who
    ///         holds <c>read</c> on the scope sees every assignment visible there, inherited ones
    ///         included, and a caller who does not sees the canonical <c>404</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Inherited rows carry the ancestor's address, not this scope's.</b> The tuple
    ///         <c>subscription:S#owner@user:U</c> is one assignment however many groups and
    ///         resources it reaches, and its <c>id</c> is the one address a <c>GET</c> or a
    ///         <c>DELETE</c> answers for it. Rendering it at every scope that inherits it would mint
    ///         addresses nothing serves. What this scope contributes is <c>inherited: true</c>, which
    ///         is <c>ICheckGrain.ListRoleAssignmentsAsync</c>'s mark put on the wire.
    ///     </para>
    /// </remarks>
    Task<Result<RoleAssignmentPage>> ListAsync(
        RoleAssignmentListRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Revokes a role. <c>DELETE</c> on a role assignment path. Idempotent.</summary>
    /// <param name="request">The request. <see cref="RoleAssignmentRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the revoke.</param>
    /// <returns>
    ///     Success once the tuple is gone — including when it was never there, because the goal of a
    ///     revoke is the absence of the grant and a re-driven <c>DELETE</c> must not report a failure
    ///     for work that succeeded.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Checked fully consistently.</b> docs/plan/07 § Consistency puts a revoke among the
    ///     cases where a stale allow is a real incident, and the caller of this method is the one
    ///     whose own grant might have been revoked a moment ago.
    /// </remarks>
    Task<Result> RevokeAsync(RoleAssignmentRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
///     Where the role assignment path reads and writes the tuple an assignment is. The write half
///     of the seam <see cref="IScopeRelationWriter" /> is, for roles rather than for
///     <c>parent</c> edges.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An interface here rather than an <c>ITupleStoreGrain</c> call</b>, for the reason
///         <see cref="IScopeRelationWriter" /> and <see cref="IResourceRelationWriter" /> are:
///         this assembly deliberately does not reference <c>CyberCloud.Authorization.Contracts</c>,
///         so nothing that references it — the gateway, every provider — can name a tuple type.
///         <c>GatewayIsolationTests</c> reads the gateway's reference table for exactly that.
///     </para>
///     <para>
///         ⚠ <b>Takes the whole address</b>, because the address <i>is</i> the tuple: the scope
///         names the object and <c>RoleAssignmentName</c> names the relation and the subject. For a
///         resource-scoped assignment the caller resolves <c>ResourceId.Id</c> through the path
///         index first — a parsed resource path carries <see cref="Guid.Empty" />, and a tuple on
///         <c>resource:00000000…</c> would be a grant on nothing.
///     </para>
/// </remarks>
public interface IRoleAssignmentStore {
    /// <summary>Writes the tuple. Idempotent: a tuple already present is a success.</summary>
    /// <param name="assignment">The assignment. A resource scope must carry its resolved id.</param>
    /// <param name="expiresOn">
    ///     When the grant ends, or <see langword="null" /> for a permanent one. A tuple already
    ///     present takes this expiry, whatever it had — so a grant is extended, shortened, or made
    ///     permanent by writing it again. docs/plan/07 § Time-bounded relations.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Result> GrantAsync(
        RoleAssignmentId assignment,
        DateTimeOffset? expiresOn,
        CancellationToken cancellationToken = default
    );

    /// <summary>Removes the tuple. Idempotent: a tuple already absent is a success.</summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    Task<Result> RevokeAsync(RoleAssignmentId assignment, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether the tuple is written <b>at this scope</b>, and until when. Direct only — an
    ///     inherited grant reads as absent.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The grant as it stands in the object's forward index. A tuple whose expiry has passed
    ///     reads as absent, from that instant, whether or not the sweep has removed it yet.
    /// </returns>
    Task<Result<RoleAssignmentGrant>> FindAsync(RoleAssignmentId assignment, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every assignment visible at a scope — the tuples written on it and the ones inherited
    ///     from its ancestors — each rendered with its own address.
    /// </summary>
    /// <param name="collection">The scope. A resource scope must carry its resolved id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The assignments, unordered and unpaged. Ordering and paging are the manager's, because
    ///     the store's job ends at the translation between tuples and addresses.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>An inherited row's address is the ancestor's</b>, which the store reverses from the
    ///     ReBAC object the view reports — a scope object's id is the scope spelled backwards, and a
    ///     resource object's id is a resource whose own grain knows its path. Every row is therefore
    ///     an address a <c>GET</c> on this seam answers; a row that could not be given one is a
    ///     failure rather than a row with a hole in it.
    /// </remarks>
    Task<Result<IReadOnlyList<RoleAssignmentSnapshot>>> ListAsync(
        RoleAssignmentCollectionId collection,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     One assignment's tuple as the store found it — <see cref="IRoleAssignmentStore.FindAsync" />.
/// </summary>
/// <param name="Granted">Whether the tuple is written, and live, at the scope.</param>
/// <param name="ExpiresOn">When it stops granting, or <see langword="null" /> for a permanent grant or none.</param>
/// <remarks>
///     Not a wire type: the store is a service beside the manager, in the gateway, and nothing
///     carries this across a grain call.
/// </remarks>
public readonly record struct RoleAssignmentGrant(bool Granted, DateTimeOffset? ExpiresOn) {
    /// <summary>No tuple at the scope.</summary>
    public static RoleAssignmentGrant Absent { get; } = new(false, null);
}

/// <summary>
///     Where the role assignment path asks whether a principal exists before it grants to one —
///     the directory half of docs/plan/11 § The object model, seen through the one question this
///     seam needs answered.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A seam in this assembly rather than a reference to
///             <c>CyberCloud.Identity.Contracts</c>, for the reason the vault's is
///             (<see cref="ISecretResolver" />) and the cluster registrar's is.
///         </b> <c>module-layering.txt</c> gives the resource manager no edge to identity and
///         identity no edge back, and a directory lookup is the textbook case of the thing that
///         file says should go through a seam. The interface therefore takes what
///         <see cref="CallerContext" /> already takes — a type and an id as two strings, never a
///         <c>SubjectRef</c> — and the implementation that resolves them through
///         <c>IUserGrain</c>, <c>IServicePrincipalGrain</c>, <c>IManagedIdentityGrain</c> and
///         <c>IGroupGrain</c> lives in the host that references both assemblies: the gateway's
///         <c>GrainPrincipalDirectory</c>. The default this assembly registers refuses, as every
///         other unwired seam here does, so a host that composes the manager and forgets the
///         directory grants nothing rather than granting to anybody.
///     </para>
///     <para>
///         ⚠ <b>The tenant is a parameter and it is the whole of the cross-tenant rule.</b> Every
///         principal grain is tenant-qualified, so a user in tenant B looked up under tenant A is
///         an activation that has never been created — "does not exist", with no second check to
///         forget. docs/plan/11 § Sign-up and tenant creation: a user belongs to exactly one tenant,
///         and the same human in two tenants is two user objects with two GUIDs.
///     </para>
/// </remarks>
public interface IPrincipalDirectory {
    /// <summary>
    ///     Whether a principal exists in a tenant's directory and can be granted to.
    /// </summary>
    /// <param name="tenantId">The tenant the assignment is in — the only tenant that is searched.</param>
    /// <param name="principalType">
    ///     The ReBAC subject type as the address spells it: <c>user</c>, <c>servicePrincipal</c>,
    ///     <c>managedIdentity</c> or <c>group</c>.
    /// </param>
    /// <param name="principalId">The subject id as the address spells it.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    ///     <c>true</c> when the principal is in this tenant's directory and is not deprovisioned or
    ///     deleted; <c>false</c> for one that is absent, in another tenant, or spelled in a form no
    ///     directory object can have. A failure means the question could not be answered — an
    ///     unwired seam, an unreachable grain — and the caller must refuse the grant rather than
    ///     read it as either answer.
    /// </returns>
    Task<Result<bool>> ExistsAsync(
        Guid tenantId,
        string principalType,
        string principalId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     A role assignment request as it reaches <see cref="IRoleAssignmentManager" />, after the
///     gateway has authenticated and resolved the region.
/// </summary>
/// <remarks>
///     ⚠ A separate record from <see cref="ScopeRequest" /> for the reason that one is separate
///     from <see cref="WriteRequest" />: the two carry the same three fields today, and a shared type
///     would let a scope request be handed to the assignment manager and compile. The path grammar
///     is the difference, and a distinct type is how the compiler sees it.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.RoleAssignmentRequest")]
public sealed record RoleAssignmentRequest {
    /// <summary>The role assignment path from the URL.</summary>
    /// <remarks>
    ///     ⚠ The path the gateway <i>rebuilt</i> from the token's tenant, never the one off the wire —
    ///     see <c>GatewayRoute</c>'s remarks. The manager re-parses it and compares the tenant again
    ///     regardless, which is the second of the two defences.
    /// </remarks>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The request body, as JSON text. Empty for a read or a revoke.</summary>
    [Id(1)]
    public string Body { get; init; } = "{}";

    /// <summary>Who is asking.</summary>
    [Id(2)]
    public CallerContext Caller { get; init; } = new();
}

/// <summary>
///     A role assignment as the API renders it — the response body of a <c>PUT</c> or a <c>GET</c>.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.RoleAssignmentSnapshot")]
public sealed record RoleAssignmentSnapshot {
    /// <summary>The assignment's own address.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The name — <c>{role}-{principalType}-{principalId}</c>.</summary>
    [Id(1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The scope's path: everything before <c>/providers/CyberCloud.Authorization/…</c>.</summary>
    [Id(2)]
    public string Scope { get; init; } = string.Empty;

    /// <summary>The role — <c>owner</c>, <c>contributor</c> or <c>reader</c>.</summary>
    [Id(3)]
    public string RoleDefinitionId { get; init; } = string.Empty;

    /// <summary>The principal's ReBAC subject type.</summary>
    [Id(4)]
    public string PrincipalType { get; init; } = string.Empty;

    /// <summary>The principal's id.</summary>
    [Id(5)]
    public string PrincipalId { get; init; } = string.Empty;

    /// <summary>
    ///     Whether this call wrote the tuple. ⚠ <c>false</c> for a repeated identical <c>PUT</c>,
    ///     which is still a success — that is what idempotent means. Always <c>false</c> on a read.
    /// </summary>
    [Id(6)]
    public bool Created { get; init; }

    /// <summary>
    ///     Whether the assignment is inherited from an ancestor of the scope it was listed at,
    ///     rather than written on that scope. Always <c>false</c> from a <c>PUT</c> or a by-name
    ///     <c>GET</c>, which address the tuple itself.
    /// </summary>
    /// <remarks>
    ///     ⚠ When <c>true</c>, <see cref="Path" /> and <see cref="Scope" /> are the
    ///     <b>ancestor's</b> — the tuple's own address, which a <c>GET</c> or <c>DELETE</c> answers
    ///     — and not the scope the listing was asked at. <see cref="IRoleAssignmentManager.ListAsync" />'s
    ///     remarks say why.
    /// </remarks>
    [Id(7)]
    public bool Inherited { get; init; }

    /// <summary>
    ///     When the grant ends, or <see langword="null" /> for a permanent one — the body's
    ///     <c>expiresOn</c>, docs/plan/07 § Time-bounded relations.
    /// </summary>
    /// <remarks>
    ///     An assignment past this instant isn't rendered at all: a <c>GET</c> answers <c>404</c>, a
    ///     listing leaves it out, and every check denies it — whether or not the sweep has removed
    ///     the tuple yet.
    /// </remarks>
    [Id(8)]
    public DateTimeOffset? ExpiresOn { get; init; }
}

/// <summary>
///     A role assignment collection <c>GET</c> as it reaches
///     <see cref="IRoleAssignmentManager.ListAsync" /> — the scope, the caller and the page
///     parameters.
/// </summary>
/// <remarks>
///     ⚠ Its page rules are <see cref="ListRequest" />'s, spelled in the same two constants, so a
///     client that pages one collection of this API pages every collection the same way — and
///     <see cref="Continuation" /> is named as that record names it, for the reason that record
///     gives (<c>CC1005</c>).
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.RoleAssignmentListRequest")]
public sealed record RoleAssignmentListRequest {
    /// <summary>The collection path from the URL — a <c>RoleAssignmentCollectionId</c>.</summary>
    /// <remarks>
    ///     ⚠ The path the gateway <i>rebuilt</i> from the token's tenant, never the one off the wire,
    ///     as for <see cref="RoleAssignmentRequest.Path" />.
    /// </remarks>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>Who is asking.</summary>
    [Id(1)]
    public CallerContext Caller { get; init; } = new();

    /// <summary>
    ///     How many assignments to return. Zero means <see cref="ListRequest.DefaultPageSize" />;
    ///     anything above <see cref="ListRequest.MaxPageSize" /> is clamped to it rather than refused.
    /// </summary>
    [Id(2)]
    public int Top { get; init; }

    /// <summary>
    ///     Where to resume, from a previous page's <see cref="RoleAssignmentPage.Continuation" />,
    ///     or empty for the first page.
    /// </summary>
    /// <remarks>
    ///     ⚠ The token is the last address of the previous page, and paging resumes at the first
    ///     assignment whose address sorts after it, ordinally. A grant or a revoke between two pages
    ///     therefore changes what the caller sees and cannot make the walk skip or repeat an
    ///     unrelated row — the same rule, and the same reason, as <see cref="ListRequest.Continuation" />.
    ///     A token naming an address in another tenant changes nothing: the rows it is compared
    ///     against came from the rebuilt scope.
    /// </remarks>
    [Id(3)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>The page size this request actually gets.</summary>
    public int PageSize =>
        Top switch {
            <= 0 => ListRequest.DefaultPageSize,
            > ListRequest.MaxPageSize => ListRequest.MaxPageSize,
            _ => Top
        };
}

/// <summary>One page of a role assignment collection <c>GET</c>.</summary>
/// <remarks>
///     ⚠ Unlike <see cref="ResourceListPage" />, a page here is never short for a reason the caller
///     is not told: every row visible at the scope is visible to a caller who may read the scope at
///     all, so a page is short only at the end of the listing. A client still stops on an empty
///     <see cref="Continuation" /> and never on a short page, because that is the rule every
///     collection of this API shares.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.RoleAssignmentPage")]
public sealed record RoleAssignmentPage {
    /// <summary>The assignments, ordered by their own address, ordinally.</summary>
    [Id(0)]
    public ImmutableArray<RoleAssignmentSnapshot> Assignments { get; init; } = [];

    /// <summary>
    ///     What to pass as <see cref="RoleAssignmentListRequest.Continuation" /> for the next page,
    ///     or empty when this page reached the end.
    /// </summary>
    [Id(1)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>Whether there is another page.</summary>
    public bool HasMore => Continuation.Length > 0;
}

/// <summary>
///     The properties a role assignment <c>PUT</c> body may carry, and the closed set of values two
///     of them take.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Here rather than in the manager, for the reason <see cref="ScopeBodyProperties" />
///             is here: a generated surface would read these, and a constant it cannot see is a
///             constant it retypes.
///         </b>
///     </para>
///     <para>
///         ⚠ <b>Every property is optional, because the address already says everything but when.</b>
///         <c>RoleAssignmentName</c> is <c>{role}-{principalType}-{principalId}</c>, so a body of
///         <c>{}</c> is a complete request for a permanent grant. Of the three properties that name
///         the tuple, one that is present must agree with the address, and a disagreement is a
///         <c>400</c> naming both — trusting either one silently would grant something the caller
///         did not spell. <see cref="ExpiresOn" /> is the fourth and names nothing; its remarks say
///         what it does instead.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="RoleDefinitionId" /> takes a role <i>name</i> and there are no role
///             definitions to address.
///         </b> Azure's is a path to a definition, because Azure has custom
///         roles; here the three built-in roles are three relations in <c>CyberCloudSchema</c>, and
///         docs/plan/07 § Azure RBAC, expressed in it says the reverse mapping is not expressible as a
///         table. The property keeps Azure's name so a client reads it as the thing it is.
///     </para>
/// </remarks>
public static class RoleAssignmentBodyProperties {
    /// <summary>The principal's id — <c>principalId</c>.</summary>
    public const string PrincipalId = "principalId";

    /// <summary>
    ///     The principal's ReBAC subject type — <c>principalType</c>. One of
    ///     <c>user</c>, <c>servicePrincipal</c>, <c>managedIdentity</c> or <c>group</c>, spelled
    ///     exactly so.
    /// </summary>
    public const string PrincipalType = "principalType";

    /// <summary>The role — <c>roleDefinitionId</c>. <c>owner</c>, <c>contributor</c> or <c>reader</c>.</summary>
    public const string RoleDefinitionId = "roleDefinitionId";

    /// <summary>
    ///     When the grant ends — <c>expiresOn</c>, an ISO 8601 instant with an offset, or
    ///     <c>null</c>. Issue #49's just-in-time roles.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Unlike the other three, this one isn't in the address and doesn't have to
    ///         agree with anything.</b> The name is the tuple and an expiry is a property of the
    ///         tuple rather than part of it, so a <c>PUT</c> carrying it sets it and a <c>PUT</c>
    ///         without it makes the assignment permanent — a <c>PUT</c> states the whole assignment,
    ///         and one that silently kept an old expiry would leave a grant ending at a time the
    ///         caller never sent.
    ///     </para>
    ///     <para>
    ///         It must be later than now, and it needs an explicit offset: a local time with none
    ///         would end the grant at an instant that depends on which host parsed it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An end that's sooner than the grant's current one must be at least a minute
    ///         away</b> (<c>TupleExpiry.ShorteningNotice</c>), or the <c>PUT</c> is a <c>400</c>. A
    ///         check grain trusts the cache fences it last read for that long, and a grant that has
    ///         to end sooner is revoked instead. docs/plan/07 § Time-bounded relations.
    ///     </para>
    /// </remarks>
    public const string ExpiresOn = "expiresOn";
}
