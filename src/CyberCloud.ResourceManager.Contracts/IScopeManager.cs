namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The one place a tenant's intent to create a <i>scope</i> enters the platform —
///     docs/plan/06 § The hierarchy's subscription and resource group.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHY THIS IS BESIDE <see cref="IResourceManager" /> AND NOT INSIDE IT. The decision is
///         expensive to reverse, so the argument is here rather than in a commit message.</b>
///     </para>
///     <para>
///         docs/plan/08 § The write path, end to end is twelve steps <i>for a resource</i>, and eight
///         of the twelve have nothing to act on for a scope. A scope has no provider, so step 1's
///         registry lookup resolves nothing; no JSON Schema per api-version, so step 2 validates
///         against nothing; no meter, so step 6 reserves nothing (a subscription is the meter, and a
///         group is free); no <c>IResourceIndexGrain</c> entry, so step 7 claims nothing — a group's
///         name is made unique by <c>ISubscriptionGrain</c>'s single-threaded activation, which
///         <c>ISubscriptionGrain.CreateResourceGroupAsync</c>'s own remarks call "a second mutex
///         guarding a lock we already hold"; no resource group of its own, so step 7b records no
///         membership; no reconciler and no desired/observed split, so step 9 submits nothing and
///         step 10 starts an operation that would converge at the instant it began.
///     </para>
///     <para>
///         What is left is steps 3, 4, 8 and 11 — the check, the lock, the ReBAC edge and the change
///         event — and those <b>are</b> kept, in that order, by
///         <c>ScopeManagerService</c>. Routing a scope through <see cref="IResourceManager.WriteAsync" />
///         would mean giving <see cref="WriteTrace.Canonical" /> a second legal shape, so the trace
///         would stop meaning "no step moved" and start meaning "no step moved, for one of the two
///         kinds of request, depending on which". The trace exists precisely because the ordering is
///         the security property; weakening it to buy one shared entry point is the wrong trade.
///     </para>
///     <para>
///         ⚠ <b>What that costs, stated so it can be paid rather than discovered.</b> There are now
///         two components a caller's intent can enter through, and docs/plan/08's
///         <i>"everything a tenant does passes through it exactly once"</i> is a sentence about
///         resources that a reader may take as a sentence about the platform. The mitigations are
///         that both live in one assembly, both take the same <see cref="CallerContext" />, both
///         answer through the same <see cref="ErrorCode.ResourceNotFound" /> seam, and the scope
///         path's <b>only</b> authorization is <see cref="IScopeAuthorizer" /> — the same engine
///         behind the same 404-never-403 rule. What is <i>not</i> mitigated is that a future step
///         added to one has to be considered for the other, and nothing in the compiler says so.
///     </para>
///     <para>
///         ⚠ <b>A service and not a grain</b>, for the reason <see cref="IResourceManager" /> is one:
///         every step it performs is a call to some other grain, and it is held by the gateway, which
///         is an Orleans <i>client</i> and therefore permanently outside
///         <c>Orleans.Multitenant</c>'s call filter. Every grain reference goes through
///         <c>ForTenant</c>, which is what <c>CC1006</c> checks.
///     </para>
/// </remarks>
public interface IScopeManager {
    /// <summary>
    ///     Creates a subscription or a resource group. <c>PUT</c> on a scope path. Idempotent.
    /// </summary>
    /// <param name="request">The request, as the gateway parsed it off the URL and the body.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The scope as it now stands, or the first step's failure.
    ///     <para>
    ///         ⚠ A refusal the caller may not see through is
    ///         <see cref="ErrorCode.ResourceNotFound" /> and never
    ///         <see cref="ErrorCode.AuthorizationFailed" /> — docs/plan/07 § The enforcement seam,
    ///         <b>404, never 403</b>. A subscription id is exactly as enumerable as a resource name
    ///         and leaks more, because it is the billing boundary.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="ScopeKind.Tenant" /> is refused here, and the refusal is a decision rather
    ///         than a gap — see <see cref="CreateTenantAsync" />.
    ///     </para>
    /// </returns>
    Task<Result<ScopeSnapshot>> CreateAsync(ScopeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a scope. <c>GET</c> on a scope path.</summary>
    /// <param name="request">The request. <see cref="ScopeRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The scope, or <see cref="ErrorCode.ResourceNotFound" /> — for a scope that does not exist
    ///     <i>and</i> for one the caller may not see, which is the same answer on purpose.
    /// </returns>
    Task<Result<ScopeSnapshot>> ReadAsync(ScopeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates a tenant. <b>The bootstrap path, and it is deliberately not reachable over HTTP.</b>
    /// </summary>
    /// <param name="request">Who the tenant is, where it lives, and who will own it.</param>
    /// <param name="caller">
    ///     The platform operator asking. ⚠ Checked against <c>platform:root#operator</c> —
    ///     docs/plan/06 § Platform administration — and nothing else in the platform checks that
    ///     relation today.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>WHO MAY CREATE A TENANT: A PLATFORM OPERATOR, THROUGH A SEAM OUTSIDE THE REQUEST
    ///         PIPELINE. The two rejected answers are worth more than the chosen one, so both are
    ///         written down.</b>
    ///     </para>
    ///     <para>
    ///         <b>Rejected — a <c>PUT /tenants/{newTenantId}</c> route.</b> It is not a design
    ///         preference, it is arithmetically impossible without breaching the gateway's one
    ///         security boundary. Stage 3 (<c>ResolveTenantStage</c>) resolves the tenant from the
    ///         token's <c>tid</c> and answers <c>404</c> to every surface that names a different one;
    ///         <c>TenantSmuggling.PathTenant</c> reads the tenant straight out of the
    ///         <c>/tenants/{id}</c> prefix for exactly that comparison. A request that creates tenant
    ///         B necessarily names B in its path and carries a token for something that is not B, so
    ///         it is refused before routing runs. Exempting the route would mean a caller-controlled
    ///         value selecting the tenant — <c>ResolveTenantStage</c>'s own remarks call that
    ///         <i>"a cross-tenant read with no exception and no log line"</i>, and it is the one thing
    ///         that file says must never be added. A tenant-create route is not worth spending it on.
    ///     </para>
    ///     <para>
    ///         <b>Rejected for now — a self-service sign-up flow.</b> It is the right eventual answer
    ///         and it does not change this seam: a sign-up is an unauthenticated front end that ends
    ///         by calling something exactly like this method with a platform-operator identity of its
    ///         own. Building it here would mean deciding e-mail verification, abuse limits, payment
    ///         capture and slug squatting — docs/plan/22's and docs/plan/11's, none of them this
    ///         issue's — and every one of those decisions would land <i>in front of</i> this method
    ///         rather than inside it.
    ///     </para>
    ///     <para>
    ///         <b>Chosen — a platform operator, off the request path.</b> docs/plan/06 § Platform
    ///         administration already names the principal (<c>platform:root#operator@user:X</c>) and
    ///         <c>CyberCloudSchema</c> already defines the type, the relation and the
    ///         <c>administer</c> permission over it; this is the first caller of any of
    ///         them. § Platform administration's own answer — that tenants are a
    ///         <c>CyberCloud.Platform/tenants</c> resource under the platform tenant, so admin "is not
    ///         a second API, it is a provider" — is compatible with this and is where this should
    ///         eventually move. It cannot be where it <i>starts</i>: that route's own path names the
    ///         platform tenant's subscription and resource group, and those two are scopes, which is
    ///         the thing that has to exist before any resource can be created at all. A bootstrap that
    ///         bootstraps itself is not a bootstrap.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE TENANT'S OWNER IS NAMED IN THE REQUEST AND IS NOT THE OPERATOR.</b>
    ///         <c>tenant</c> is the only type in <c>CyberCloudSchema</c> with no <c>parent</c>
    ///         relation — nothing is above it — so a <c>Check</c> on a tenant resolves through nothing
    ///         and only a <i>direct</i> tuple can grant on one. Something must therefore write
    ///         <c>tenant:{t}#owner@{subject}</c> at create or the tenant is permanently invisible to
    ///         everyone. Defaulting that subject to the operator would make platform staff the
    ///         standing owner of every customer tenant, which is the impersonation problem
    ///         § Platform administration is careful about, arrived at by accident and with no
    ///         notification and no time box. So <see cref="TenantCreateRequest.OwnerSubjectId" /> is
    ///         required and the operator does not appear in the tuple at all.
    ///     </para>
    /// </remarks>
    Task<Result<ScopeSnapshot>> CreateTenantAsync(
        TenantCreateRequest request,
        CallerContext caller,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Where the scope path asks whether a caller may do something. docs/plan/07 § The enforcement
///     seam — the <i>same</i> seam <see cref="IResourceAuthorizer" /> is, asked about a different
///     object type.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An interface here rather than a direct <c>ICheckGrain</c> call</b>, for the reason
///         <see cref="IResourceAuthorizer" /> is one: this assembly deliberately does not reference
///         <c>CyberCloud.Authorization.Contracts</c>, so nothing that references it can name the
///         engine. The gateway holds <see cref="IScopeManager" /> and must not gain an
///         <c>AssemblyRef</c> row for the authorization contracts —
///         <c>GatewayIsolationTests</c> reads that table.
///     </para>
///     <para>
///         ⚠ <b>The two-permission shape is what makes 404-versus-403 decidable</b>, unchanged from
///         the resource seam: 404 when the caller cannot read, 403 only when they can read but not
///         act, because a 403 on an unreadable scope is an enumeration oracle.
///     </para>
/// </remarks>
public interface IScopeAuthorizer {
    /// <summary>Whether a caller may act on a scope, and what to say if not.</summary>
    /// <param name="scope">The scope, or the address of one that does not exist yet.</param>
    /// <param name="actionPermission">The permission the verb needs.</param>
    /// <param name="readPermission">
    ///     The permission a read needs. ⚠ Consulted only when the action is refused, to choose between
    ///     <c>404</c> and <c>403</c>.
    /// </param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="fullyConsistent">Whether to bypass every cache. docs/plan/07 § Consistency.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    ///     Success when allowed. On refusal, <see cref="ErrorCode.ResourceNotFound" /> when the caller
    ///     cannot read, and <see cref="ErrorCode.AuthorizationFailed" /> when they can.
    /// </returns>
    Task<Result> AuthorizeAsync(
        ScopeId scope,
        string actionPermission,
        string readPermission,
        CallerContext caller,
        bool fullyConsistent = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Whether a caller holds a permission on the platform itself —
    ///     <c>platform:root</c>, docs/plan/06 § Platform administration.
    /// </summary>
    /// <param name="permission">The permission. The schema defines exactly one on this type.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <remarks>
    ///     ⚠ <b>Always fully consistent, and the implementation does not take the flag.</b>
    ///     docs/plan/07 § Consistency wants a cache bypass for <i>"anything where a stale allow is a
    ///     real incident"</i>, and a revoked platform operator creating tenants out of a warm cache is
    ///     the definition of one. It is also not a hot path: it is asked once per tenant ever created.
    ///     <para>
    ///         ⚠ <b>Refusal is <see cref="ErrorCode.AuthorizationFailed" /> and <i>not</i> the
    ///         canonical 404, which is the one place the scope path departs from
    ///         <see cref="AuthorizeAsync" /> above.</b> The 404 rule exists because a 403 would confirm
    ///         that a named object exists. <c>platform:root</c> is a singleton whose existence is
    ///         documented, so there is nothing to leak, and answering "that does not exist" to an
    ///         operator whose grant has lapsed sends them to look for a missing tenant instead of at
    ///         their own permissions.
    ///     </para>
    /// </remarks>
    Task<Result> AuthorizePlatformAsync(
        string permission,
        CallerContext caller,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Where the scope path records a scope's place in the authorization hierarchy — the
///     <c>parent</c> edge docs/plan/07 § The model's <c>From("parent", …)</c> rewrites follow.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NOTHING IN THE PLATFORM HAS EVER WRITTEN A SCOPE'S PARENT EDGE, AND THE CHAIN
///         docs/plan/07 § Azure RBAC, expressed in it DESCRIBES THEREFORE STOPPED ONE LEVEL UP FROM
///         WHERE IT WAS DOCUMENTED.</b> <c>ReBacResourceRelationWriter</c> writes
///         <c>resource:{id}#parent@resourceGroup:{sub}-{rg}</c> and that was the whole of it: no
///         <c>resourceGroup:{sub}-{rg}#parent@subscription:{s}</c> and no
///         <c>subscription:{s}#parent@tenant:{t}</c> existed anywhere outside a test's own setup. So
///         <c>subscription:S#owner@user:U</c> — row one of that table, the tuple the document uses as
///         its worked example — granted <b>nothing</b> on any resource group or resource beneath it on
///         a real silo, because the rewrite had no edge to follow. Every test that appeared to prove
///         inheritance wrote the group's <c>owner</c> tuple directly
///         (<c>IsolationCluster.CreateAsync</c> still does), which answers a narrower question than it
///         looks like it answers.
///     </para>
///     <para>
///         ⚠ <b>An interface here rather than an <c>ITupleStoreGrain</c> call</b>, for the reason
///         <see cref="IResourceRelationWriter" /> is one: providers reference this assembly and must
///         not be able to name a tuple type.
///     </para>
/// </remarks>
public interface IScopeRelationWriter {
    /// <summary>
    ///     Records <c>subscription:{s}#parent@tenant:{t}</c> or
    ///     <c>resourceGroup:{sub}-{rg}#parent@subscription:{s}</c>. Idempotent.
    /// </summary>
    /// <param name="scope">The scope. ⚠ <see cref="ScopeKind.Tenant" /> has no parent and is refused.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    ///     ⚠ <b>Written <i>before</i> the scope's durable state, for the reason step 8 of
    ///     docs/plan/08 § The write path, end to end gives.</b> After it, a failure leaves a scope
    ///     that is durable and invisible to the person who just created it, and there is no operation
    ///     grain here to re-drive the work. Before it, a failure is a clean refusal with nothing
    ///     durable written. The residue of the chosen order is a <c>parent</c> tuple aimed at a scope
    ///     that was never created — inert, because the object it names resolves to nothing and the
    ///     next attempt writes the identical tuple.
    /// </remarks>
    Task<Result> LinkToParentAsync(ScopeId scope, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records a direct <c>#owner</c> tuple on a scope. Idempotent.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <param name="subjectType">The ReBAC subject type — <c>user</c>, <c>servicePrincipal</c>…</param>
    /// <param name="subjectId">The subject.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    ///     ⚠ <b>Used for a tenant and for nothing else, and the asymmetry is the schema's rather than
    ///     a choice.</b> A subscription and a resource group inherit <c>owner</c> through
    ///     <c>From("parent", "owner")</c>, so their creator — who had to hold a permission on the
    ///     parent to get this far — can already see what they made, and a direct tuple would be the
    ///     per-scope role row docs/plan/07 § The model's whole argument is against. A <c>tenant</c>
    ///     has no <c>parent</c> relation, so there is nothing to inherit through and a direct tuple is
    ///     the only thing that can make a new tenant visible to anyone.
    /// </remarks>
    Task<Result> GrantOwnerAsync(
        ScopeId scope,
        string subjectType,
        string subjectId,
        CancellationToken cancellationToken = default
    );
}
