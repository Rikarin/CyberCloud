namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The one place a tenant grants, reads back, or revokes a role — the write half of
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
///         nothing anywhere wrote <c>contributor</c> or <c>reader</c>. So <i>"invite a colleague and
///         grant them Reader on one resource group"</i> had no request that did the second half.
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
    ///     view is <c>ICheckGrain.ListRoleAssignmentsAsync</c>, and it is not on this path.
    /// </remarks>
    Task<Result<RoleAssignmentSnapshot>> ReadAsync(
        RoleAssignmentRequest request,
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
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Result> GrantAsync(RoleAssignmentId assignment, CancellationToken cancellationToken = default);

    /// <summary>Removes the tuple. Idempotent: a tuple already absent is a success.</summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    Task<Result> RevokeAsync(RoleAssignmentId assignment, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether the tuple is written <b>at this scope</b>. Direct only — an inherited grant
    ///     answers <c>false</c>.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><c>true</c> if the exact tuple is in the object's forward index.</returns>
    Task<Result<bool>> IsGrantedAsync(RoleAssignmentId assignment, CancellationToken cancellationToken = default);
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
///         ⚠ <b>Every property is optional, because the address already says everything.</b>
///         <c>RoleAssignmentName</c> is <c>{role}-{principalType}-{principalId}</c>, so a body of
///         <c>{}</c> is a complete request. A property that is present must agree with the address,
///         and a disagreement is a <c>400</c> naming both — trusting either one silently would grant
///         something the caller did not spell.
///     </para>
///     <para>
///         ⚠ <b><see cref="RoleDefinitionId" /> takes a role <i>name</i> and there are no role
///         definitions to address.</b> Azure's is a path to a definition, because Azure has custom
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
}
