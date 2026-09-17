using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.WellKnown;
using CyberCloud.Kubernetes.Contracts.Tunnel;

namespace CyberCloud.Gateway.Host.Routing;

/// <summary>What kind of endpoint a path names.</summary>
enum RouteKind {
    /// <summary>Nothing this gateway serves.</summary>
    Unknown = 0,

    /// <summary>A resource, by id. <c>GET</c>, <c>PUT</c>, <c>PATCH</c>, <c>DELETE</c>.</summary>
    Resource,

    /// <summary>
    ///     A scope — a tenant, a subscription or a resource group. <c>GET</c> and <c>PUT</c>.
    ///     docs/plan/06 § The hierarchy.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The sixth kind, and it is a contract change rather than an addition.</b> Every kind in
    ///     this enum is asserted somewhere and <see cref="GatewayRouter.Resolve" /> is a total function
    ///     over a closed set, so a new member changes what <see cref="Unknown" /> means: paths that
    ///     used to be <see cref="ErrorCode.InvalidResourceId" /> — a <c>400</c> — are now routed.
    ///     <c>ScopeRoutingTests</c> pins both halves, the admitted shapes and the ones that still
    ///     answer <c>400</c>.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             It is separate from <see cref="Resource" /> for the reason <see cref="Action" /> is
    ///             separate from it: the dispatch target differs.
    ///         </b> A scope goes to
    ///         <c>IScopeManager</c> and a resource to <c>IResourceManager</c> — see
    ///         <c>IScopeManager</c>'s remarks on why those are two components. Folding the two kinds
    ///         together would mean <c>DispatchStage</c> re-deciding, per request, which manager a
    ///         <see cref="Resource" /> route meant, which is the decision this stage exists to make
    ///         once.
    ///     </para>
    /// </remarks>
    Scope,

    /// <summary>
    ///     A collection of scopes — a tenant's subscriptions at <c>/tenants/{t}/subscriptions</c>,
    ///     or a subscription's resource groups at <c>/tenants/{t}/subscriptions/{s}/resourceGroups</c>.
    ///     <c>GET</c> only. docs/plan/10 § Shape.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Tried after <see cref="Scope" /> and only on a <c>GET</c></b>, as
    ///     <see cref="Collection" /> is tried after <see cref="Resource" /> and
    ///     for the same reason: the grammars are disjoint — an item is 2, 4 or 6 segments and a
    ///     collection 3 or 5 — so the order decides only which message a malformed path gets, and
    ///     a write on the collection path is a <c>400</c> that names the item address a scope is
    ///     created at. <c>ScopeCollectionRoutingTests</c> pins both halves.
    ///     <para>
    ///         ⚠ <b>Separate from <see cref="Scope" /> for the reason <see cref="Collection" /> is
    ///         separate from <see cref="Resource" />:</b> the two are different addresses, decided
    ///         by the path and never by the method. Both go to <c>IScopeManager</c>; this one to
    ///         its <c>ListAsync</c>, which filters by what the caller may read and pages.
    ///     </para>
    /// </remarks>
    ScopeCollection,

    /// <summary>
    ///     A role assignment — <c>{scope}/providers/CyberCloud.Authorization/roleAssignments/{name}</c>,
    ///     on a tenant, a subscription, a resource group or a resource. <c>GET</c>, <c>PUT</c> and
    ///     <c>DELETE</c>. docs/plan/07 § Azure RBAC, expressed in it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The eighth kind, and it is tried before every other grammar.</b> On a resource group
    ///     the address is a well-formed ten-segment resource path — a resource of type
    ///     <c>CyberCloud.Authorization/roleAssignments</c> — so tried after <see cref="Resource" />
    ///     it would reach the resource manager and be refused as a type no provider serves.
    ///     <c>RoleAssignmentId</c>'s remarks carry the disjointness argument; the short form is that
    ///     the namespace is reserved and <c>ProviderRegistry.Build</c> refuses a provider that
    ///     claims it. <c>RoleAssignmentRoutingTests</c> pins the precedence and the shapes that
    ///     still answer <c>400</c>.
    ///     <para>
    ///         ⚠ <b>Separate from <see cref="Scope" /> for the reason that one is separate from
    ///         <see cref="Resource" />: the dispatch target differs.</b> An assignment goes to
    ///         <c>IRoleAssignmentManager</c>, which owns the <c>assignRole</c> check and the tuple.
    ///     </para>
    /// </remarks>
    RoleAssignment,

    /// <summary>
    ///     The role assignments at a scope —
    ///     <c>{scope}/providers/CyberCloud.Authorization/roleAssignments</c>, on a tenant, a
    ///     subscription, a resource group or a resource. <c>GET</c> only. docs/plan/07 § Azure RBAC,
    ///     expressed in it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The ninth kind, and until it existed the address was a <c>400</c> (issue #86).</b>
    ///     Under the reserved namespace the router asked one grammar, the assignment's, and a path
    ///     with no name failed it. Now it asks two, in the order <see cref="Collection" /> is asked
    ///     after <see cref="Resource" /> and for the same reason: the grammars are disjoint — one
    ///     ends on <c>RoleAssignmentId.CollectionSuffix</c>, the other on a name after it — so the
    ///     order changes only which message a malformed path gets.
    ///     <para>
    ///         ⚠ <b>Separate from <see cref="RoleAssignment" /> for the reason
    ///         <see cref="Collection" /> is separate from <see cref="Resource" />:</b> the two are
    ///         different addresses, and which one a path is, is decided by the path and never by
    ///         the method. Both go to <c>IRoleAssignmentManager</c>; this one to its
    ///         <c>ListAsync</c>, which pages.
    ///     </para>
    /// </remarks>
    RoleAssignmentCollection,

    /// <summary>A <c>POST</c> action on an existing resource — <c>restart</c>, <c>rotateKeys</c>.</summary>
    Action,

    /// <summary>
    ///     A collection of one resource type inside one resource group. <c>GET</c> only.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not a verb on <see cref="Resource" />, because the two are different addresses.</b>
    ///     <c>ResourceCollectionId</c> and <c>ResourceId</c> partition the paths that carry the fixed
    ///     prefix — one ends on a type, the other on a name — so which of them a path is, is decided
    ///     by the path and never by the method.
    /// </remarks>
    Collection,

    /// <summary>The LRO polling endpoint. docs/plan/10 § Long-running operations.</summary>
    Operation,

    /// <summary>One of the four hubs. docs/plan/10 § SignalR.</summary>
    Hub,

    /// <summary>The generated document, per api-version.</summary>
    OpenApi,

    /// <summary>
    ///     The RFC 9116 <c>security.txt</c> — <c>GET</c> on exactly <c>/.well-known/security.txt</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One of two routes with no <c>api-version</c> — <see cref="Hub" /> is the other — and
    ///     it is not an exception to docs/plan/10 § API versioning.</b> That section versions
    ///     <i>this platform's</i> API surface. The file is RFC 9116's: its shape is the RFC's, a
    ///     scanner fetches it with no query string, and a <c>400</c> naming a date would be answered
    ///     to every one of them. Stage 6 skips the parameter for this kind for the reason it does for
    ///     a hub — the endpoint is not a versioned surface. It also carries no tenant: stage 2 lets
    ///     <c>/.well-known</c> through without a token and stage 3 leaves the caller empty, so this
    ///     is a route dispatch can serve from a constant and from nothing else.
    /// </remarks>
    SecurityTxt,

    /// <summary>
    ///     A connected cluster's agent dialling in — <c>GET</c> on exactly <c>/agent/v1/tunnel</c>,
    ///     upgraded to a WebSocket. docs/plan/09 § Cluster connections, the <c>AgentInitiated</c> row.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The third route with no <c>api-version</c> and no token, and the only one that
    ///     authenticates by something other than a JWT.</b> Stage 2 lets <c>/agent/</c> through
    ///     without a bearer token the identity host minted, because the agent holds a per-cluster
    ///     credential the tunnel grain checks — <c>AgentTunnelRelay</c>, at the endpoint, after the
    ///     pipeline. Stage 3 leaves the caller empty; stage 5 counts the upgrade against the
    ///     per-IP bucket; stage 6 skips the api-version, as for <see cref="Hub" />; stage 8 leaves
    ///     the request for the endpoint, as for a hub. What the pipeline does for it is stages 1
    ///     and 5, and the correlation id on the refusal.
    /// </remarks>
    AgentTunnel
}

/// <summary>
///     Stage 6's answer: what the path names, once the tenant is already known from the token.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Resource" />'s tenant is the <i>token's</i>, never the path's.</b>
///     <see cref="GatewayRouter" /> rebuilds the id from <c>CallerContext.TenantId</c> after stage 3
///     has already answered <c>404</c> to any disagreement. Two defences rather than one, and the
///     second is the one that still holds if somebody deletes the first: even with the check gone,
///     the id that reaches dispatch addresses the caller's own tenant, so the failure mode of a
///     regression is "the resource is not found" rather than "another tenant's resource is returned".
/// </remarks>
/// <param name="Kind">Which endpoint.</param>
/// <param name="Resource">The resource, for <see cref="RouteKind.Resource" /> and <see cref="RouteKind.Action" />.</param>
/// <param name="Action">The action name, for <see cref="RouteKind.Action" />.</param>
/// <param name="OperationId">The operation, for <see cref="RouteKind.Operation" />.</param>
/// <param name="HubName">The hub, for <see cref="RouteKind.Hub" />.</param>
/// <param name="Scope">
///     The scope, for <see cref="RouteKind.Scope" />. ⚠ Its tenant is the <i>token's</i> too, and for
///     the same reason — see the remarks on this type.
/// </param>
/// <param name="Collection">
///     The collection, for <see cref="RouteKind.Collection" />. ⚠ Its tenant is the <i>token's</i>
///     too, rebuilt for the reason <see cref="Resource" />'s is.
/// </param>
/// <param name="RoleAssignment">
///     The assignment, for <see cref="RouteKind.RoleAssignment" />. ⚠ Its tenant is the
///     <i>token's</i> too — <c>RoleAssignmentId.WithTenant</c> rebuilds whichever of its two scope
///     members is set.
/// </param>
/// <param name="RoleAssignments">
///     The collection, for <see cref="RouteKind.RoleAssignmentCollection" />. ⚠ Its tenant is the
///     <i>token's</i> too, rebuilt the same way.
/// </param>
/// <param name="Scopes">
///     The collection, for <see cref="RouteKind.ScopeCollection" />. ⚠ Its tenant is the
///     <i>token's</i> too, rebuilt through the parent scope.
/// </param>
readonly record struct GatewayRoute(
    RouteKind Kind,
    ResourceId Resource,
    string Action,
    Guid OperationId,
    string HubName,
    ScopeId Scope = default,
    ResourceCollectionId Collection = default,
    RoleAssignmentId RoleAssignment = default,
    RoleAssignmentCollectionId RoleAssignments = default,
    ScopeCollectionId Scopes = default
) {
    /// <summary>Nothing matched.</summary>
    public static GatewayRoute None { get; } = new(RouteKind.Unknown, default, "", Guid.Empty, "");

    /// <summary>The address dispatch uses — rebuilt, so it always carries the token's tenant.</summary>
    /// <remarks>
    ///     ⚠ <b>A scope's path is rebuilt here exactly as a resource's is</b>, from a
    ///     <see cref="ScopeId" /> whose <c>TenantId</c> came from <c>CallerContext</c>. The second
    ///     defence is only a defence if every kind that reaches a manager goes through it —
    ///     which is why <see cref="CollectionPath" /> below is rebuilt the same way.
    /// </remarks>
    public string ResourcePath =>
        Kind switch {
            RouteKind.Resource or RouteKind.Action => Resource.Path,
            RouteKind.Scope => Scope.Path,
            RouteKind.RoleAssignment => RoleAssignment.Path,
            _ => ""
        };

    /// <summary>The collection address dispatch uses — rebuilt, carrying the token's tenant.</summary>
    /// <remarks>
    ///     ⚠ For a <see cref="RouteKind.ScopeCollection" /> this is the collection's own path — what
    ///     a <c>nextLink</c> is built from — and not the parent's, which is what the manager takes;
    ///     <see cref="Scopes" /> carries the parent for dispatch.
    /// </remarks>
    public string CollectionPath =>
        Kind switch {
            RouteKind.Collection => Collection.Path,
            RouteKind.RoleAssignmentCollection => RoleAssignments.Path,
            RouteKind.ScopeCollection => Scopes.Path,
            _ => ""
        };
}

/// <summary>
///     Turns a URL path into a <see cref="GatewayRoute" />. docs/plan/10 § Request pipeline, stage 6.
/// </summary>
static class GatewayRouter {
    /// <summary>The <c>/hubs/</c> prefix docs/plan/10 § Shape gives the SignalR endpoints.</summary>
    public const string HubPrefix = "/hubs/";

    /// <summary>The LRO polling prefix docs/plan/10 § Long-running operations gives.</summary>
    public const string OperationsPrefix = "/operations/";

    /// <summary>The generated OpenAPI document.</summary>
    public const string OpenApiPath = "/openapi";

    /// <summary>The agent tunnel — <see cref="TunnelCodec.TunnelPath" />, the one path under <c>/agent/</c>.</summary>
    public const string AgentTunnelPath = TunnelCodec.TunnelPath;

    /// <summary>The prefix stage 2 exempts from bearer authentication for the agent's sake.</summary>
    public const string AgentPrefix = "/agent/";

    /// <summary>The RFC 9116 file. <see cref="SecurityTxt.Path" />, and the only <c>/.well-known</c> path routed.</summary>
    /// <remarks>
    ///     ⚠ Stage 2 exempts the whole <c>/.well-known</c> prefix from authentication — docs/plan/10
    ///     § Shape reserves it for the OIDC discovery proxy, which is not built — but exemption is
    ///     not routing. Every other path under the prefix still arrives here, matches nothing, and
    ///     falls through to the resource parser exactly as it did before this constant existed.
    /// </remarks>
    public const string SecurityTxtPath = SecurityTxt.Path;

    /// <summary>
    ///     Resolves a path against a tenant that has already been established from the token.
    /// </summary>
    /// <param name="path">The URL path, decoded.</param>
    /// <param name="method">The HTTP method, upper case.</param>
    /// <param name="tenantId">
    ///     The tenant from the token. ⚠ The parsed path's own tenant segment is <b>discarded</b> —
    ///     stage 3 has already refused any request where the two disagree, and rebuilding from this
    ///     value is what makes that refusal impossible to bypass by deleting the check.
    /// </param>
    /// <returns>
    ///     The route, or a failure. ⚠ A malformed resource path is
    ///     <see cref="ErrorCode.InvalidResourceId" /> — a <c>400</c> — because it is a client bug and
    ///     not a hidden resource. A well-formed path to something that does not exist is a
    ///     <c>404</c>, and that decision belongs to the resource manager, not here.
    /// </returns>
    public static Result<GatewayRoute> Resolve(string path, string method, Guid tenantId) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(method);

        if (path.StartsWith(HubPrefix, StringComparison.Ordinal)) {
            var hub = path[HubPrefix.Length..].TrimEnd('/');

            return HubNames.IsKnown(hub)
                ? Result<GatewayRoute>.Success(new(RouteKind.Hub, default, "", Guid.Empty, hub))
                : Result<GatewayRoute>.Failure(GatewayErrors.NotFound(path));
        }

        if (path.StartsWith(OperationsPrefix, StringComparison.Ordinal)) {
            var rest = path[OperationsPrefix.Length..].TrimEnd('/');

            // The `D` form only — the same rule ResourceId applies to every GUID it parses, and for
            // the same reason: five spellings of one id are five cache entries and five audit rows.
            return GuidFormat.TryParseD(rest, out var operationId)
                ? Result<GatewayRoute>.Success(new(RouteKind.Operation, default, "", operationId, ""))
                : Result<GatewayRoute>.Failure(GatewayErrors.NotFound(path));
        }

        if (string.Equals(path, OpenApiPath, StringComparison.Ordinal)) {
            return Result<GatewayRoute>.Success(new(RouteKind.OpenApi, default, "", Guid.Empty, ""));
        }

        if (string.Equals(path, AgentTunnelPath, StringComparison.Ordinal)) {
            // GET only: a WebSocket upgrade is a GET. Any other verb here is the canonical 404, and
            // any other path under the exempted prefix falls through to the resource parser's 400 —
            // the exemption is not routing, exactly as for /.well-known below.
            return HttpMethods.IsGet(method)
                ? Result<GatewayRoute>.Success(new(RouteKind.AgentTunnel, default, "", Guid.Empty, ""))
                : Result<GatewayRoute>.Failure(GatewayErrors.NotFound(path));
        }

        if (string.Equals(path, SecurityTxtPath, StringComparison.Ordinal)) {
            // GET only. Anything else on this path is the canonical 404 rather than a 405: RFC 9116
            // § 3 defines one method for the file, and every unsupported shape in this API that is
            // not a scope answers 404 — GatewayHeaders.Allow's remarks keep that list at one.
            return HttpMethods.IsGet(method)
                ? Result<GatewayRoute>.Success(new(RouteKind.SecurityTxt, default, "", Guid.Empty, ""))
                : Result<GatewayRoute>.Failure(GatewayErrors.NotFound(path));
        }

        // ── A role assignment, before everything else that has a tenant prefix. ─────────────────
        //
        // ⚠ FIRST, AND THE ORDER IS NOT FREE HERE — which is the one difference from the scope
        // grammar below. On a resource group, {scope}/providers/CyberCloud.Authorization/
        // roleAssignments/{name} is a well-formed ten-segment resource path, so tried after
        // ResolveResource it would be a resource of a type no provider serves and the resource
        // manager would refuse it. What keeps this from being a precedence rule nobody wrote down is
        // that the overlap is exactly one reserved namespace, ProviderRegistry.Build refuses a
        // provider that claims it, and RoleAssignmentIdTests sweeps the other direction: no
        // assignment path parses as a scope, and no scope or resource path parses as an assignment.
        //
        // ⚠ AND UNDER THAT NAMESPACE ONLY THE TWO ASSIGNMENT GRAMMARS ARE ASKED. A path that names
        // the namespace and parses as neither — `…/roleAssignments/reader`, a trailing segment, a
        // trailing slash — is a 400 that names the grammar, never a fall-through into the resource
        // or collection grammars. Both of those accept it as a type no provider serves and answer
        // the canonical 404, which sends a client looking for a missing assignment when their URL is
        // wrong. RoleAssignmentId.IsUnderNamespace's remarks carry the argument.
        //
        // ⚠ THE COLLECTION IS ASKED SECOND AND ONLY ON A GET, as ResolveResource asks its collection
        // (issue #86). The two grammars are disjoint — an assignment ends on a name, the collection
        // on the suffix — so the order decides only the message: a PUT or DELETE on the collection
        // path gets a 400 that says the grant needs a name, which is what an ARM client that emitted
        // `PUT …/roleAssignments/{guid}` and lost the segment most needs to read.
        if (RoleAssignmentId.IsUnderNamespace(path)) {
            var assignment = RoleAssignmentId.ParsePath(path);

            if (assignment.TryGetError(out var assignmentError)) {
                if (!RoleAssignmentCollectionId.TryParsePath(path, out var collection)) {
                    return Result<GatewayRoute>.Failure(assignmentError);
                }

                if (!HttpMethods.IsGet(method)) {
                    return Result<GatewayRoute>.Failure(
                        ErrorCode.InvalidResourceId,
                        $"'{path}' is the role assignment collection, which is read with GET only. A "
                        + "grant is a PUT and a revoke a DELETE on one assignment — "
                        + "'{scope}" + RoleAssignmentId.Suffix + "{role}-{principalType}-{principalId}' "
                        + "— and the name is derived from those three parts rather than chosen "
                        + "(docs/plan/07 § Azure RBAC, expressed in it)."
                    );
                }

                return Result<GatewayRoute>.Success(
                    new(
                        RouteKind.RoleAssignmentCollection,
                        default,
                        "",
                        Guid.Empty,
                        "",
                        RoleAssignments: collection.WithTenant(tenantId)
                    )
                );
            }

            return Result<GatewayRoute>.Success(
                new(
                    RouteKind.RoleAssignment,
                    default,
                    "",
                    Guid.Empty,
                    "",
                    // ⚠ NAMED, for the reason Collection is below — four optional address kinds
                    // now, and the positional form would put an assignment into Scope and compile.
                    RoleAssignment: assignment.GetValueOrThrow().WithTenant(tenantId)
                )
            );
        }

        // ── A scope, before the resource/action split. docs/plan/06 § The hierarchy. ────────────
        //
        // ⚠ THE TWO GRAMMARS ARE DISJOINT AND THE ORDER IS THEREFORE FREE — which is worth stating,
        // because if it were not free this would be a precedence rule nobody wrote down. A scope
        // address is 2, 4 or 6 segments; a resource address is at least 10 and must contain
        // `/providers/`. ScopeIdTests drives the whole overlap rather than asserting the claim. The
        // management group (issue #39) is a fourth scope shape at 4 segments and changes none of
        // this: the segment counts are what keep the grammars apart, and it added no count.
        //
        // ⚠ IT IS BEFORE THE POST BRANCH, WHICH CHANGES WHAT A POST TO A SCOPE ANSWERS AND IMPROVES
        // IT. ResolveAction strips the last segment unconditionally, so `POST /tenants/{t}/
        // subscriptions/{s}` used to be read as an action named `{s}` on the resource
        // `/tenants/{t}/subscriptions` and refused as a malformed resource id. It is now routed as a
        // scope and refused by the manager with a sentence that names the verb — docs/plan/08 § The
        // write path, end to end: POST "appears only for actions on an existing resource … never for
        // creation".
        if (ScopeId.TryParsePath(path, out var scope)) {
            return Result<GatewayRoute>.Success(
                new(
                    RouteKind.Scope,
                    default,
                    "",
                    Guid.Empty,
                    "",
                    // ⚠ The token's tenant, never the path's — the same rebuild ResolveResource does
                    // and for the same reason. Stage 3 has already refused any disagreement; this is
                    // the defence that still holds if somebody deletes that one.
                    scope with { TenantId = tenantId }
                )
            );
        }

        // ── A scope collection, after the item and only on a GET. ───────────────────────────────
        //
        // ⚠ THE THIRD SCOPE GRAMMAR, AND THE ORDER IS FREE HERE TOO: an item is 2, 4 or 6 segments
        // and a collection is 3 or 5, so nothing parses as both — ScopeCollectionIdTests sweeps it.
        // What the order decides is the message, and the item parser's is the one nearly every
        // caller needs.
        //
        // ⚠ A WRITE ON THE COLLECTION PATH IS A 400 THAT NAMES THE ITEM ADDRESS, and it is checked
        // here rather than left to fall through: on a PUT the path would otherwise reach the
        // resource parser, whose "not a resource path" sentence sends a client that dropped the
        // subscription id from `PUT /tenants/{t}/subscriptions/{s}` to look at the wrong grammar.
        // 400 and not 405, for the reason the resource collection gives: a 405 says the address
        // exists and the verb does not, which is one more fact than a malformed write earns.
        if (ScopeCollectionId.TryParsePath(path, out var scopes)) {
            if (!HttpMethods.IsGet(method)) {
                return Result<GatewayRoute>.Failure(ErrorCode.InvalidResourceId, ScopeCollectionWriteRefusal(scopes));
            }

            return Result<GatewayRoute>.Success(
                new(
                    RouteKind.ScopeCollection,
                    default,
                    "",
                    Guid.Empty,
                    "",
                    // ⚠ NAMED, for the reason every other optional address kind is: five of them
                    // now, and the positional form would put a collection into Scope and compile.
                    // The token's tenant, never the path's — rebuilt through the parent.
                    Scopes: scopes with { Parent = scopes.Parent with { TenantId = tenantId } }
                )
            );
        }

        return string.Equals(method, HttpMethods.Post, StringComparison.Ordinal)
            ? ResolveAction(path, tenantId)
            : ResolveResource(path, method, tenantId);
    }

    /// <summary>
    ///     The sentence a write on a scope collection path answers with: where the door is.
    /// </summary>
    /// <param name="collection">The collection the write addressed.</param>
    /// <remarks>
    ///     ⚠ Public so the test that pins it reads the same string rather than a retyped one. The
    ///     address is the item template one segment below the collection, which is the URL a client
    ///     that dropped the id from a <c>PUT</c> most needs to read back.
    /// </remarks>
    public static string ScopeCollectionWriteRefusal(ScopeCollectionId collection) =>
        collection.MemberKind switch {
            ScopeKind.Subscription =>
                "A subscription is created by PUT at its own address, /tenants/{t}/subscriptions/{s}. "
                + "The collection is read with GET only — docs/plan/10 § Shape.",
            ScopeKind.ManagementGroup =>
                "A management group is created by PUT at its own address, "
                + "/tenants/{t}/managementGroups/{name}. The collection is read with GET only — "
                + "docs/plan/10 § Shape.",
            _ =>
                "A resource group is created by PUT at its own address, "
                + "/tenants/{t}/subscriptions/{s}/resourceGroups/{rg}. The collection is read with GET "
                + "only — docs/plan/10 § Shape."
        };

    /// <summary>
    ///     A non-<c>POST</c> path is a resource or a collection, and the path alone says which.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The collection is tried second and only on a <c>GET</c>, and the order is not
    ///             arbitrary.
    ///         </b> The two grammars are disjoint — <c>ResourceCollectionId</c>'s remarks
    ///         set out why an even tail is a resource and an odd one is a collection — so neither
    ///         parser can accept the other's path and the order cannot change which one matches. What
    ///         it does change is the <i>message</i> a malformed path gets, and
    ///         <c>ResourceId.ParsePath</c>'s is the one nearly every caller needs.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A <c>PUT</c>, <c>PATCH</c> or <c>DELETE</c> on a collection path is a <c>400</c>
    ///             with the resource parser's message and is deliberately not a <c>405</c>.
    ///         </b> There is
    ///         no bulk write and no bulk delete: docs/plan/06 § Two-phase create makes a resource
    ///         group the lifecycle unit and a resource the thing written, and an endpoint that
    ///         deleted "every widget here" would tear down an unknown number of resources the caller
    ///         never named — the same refusal <c>ResourceManagerService.DeleteAsync</c> makes for a
    ///         parent with children.
    ///     </para>
    /// </remarks>
    static Result<GatewayRoute> ResolveResource(string path, string method, Guid tenantId) {
        var parsed = ResourceId.ParsePath(path);

        if (parsed.TryGetError(out var error)) {
            if (HttpMethods.IsGet(method) && ResourceCollectionId.TryParsePath(path, out var collection)) {
                return Result<GatewayRoute>.Success(
                    new(
                        RouteKind.Collection,
                        default,
                        "",
                        Guid.Empty,
                        "",
                        // ⚠ NAMED, because this record now carries two optional address kinds. The
                        // positional form put a collection into Scope and still compiled for as long as
                        // the two were one parameter apart.
                        Collection: collection with { TenantId = tenantId }
                    )
                );
            }

            return Result<GatewayRoute>.Failure(error);
        }

        return Result<GatewayRoute>.Success(
            new(
                RouteKind.Resource,
                parsed.GetValueOrThrow() with { TenantId = tenantId },
                "",
                Guid.Empty,
                ""
            )
        );
    }

    /// <summary>
    ///     A <c>POST</c> is always <c>{resource}/{action}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The last segment is stripped unconditionally, and the alternative is ambiguous.</b>
    ///     Resource types nest (<c>servers/databases/{name}</c>), so
    ///     <c>…/servers/main/restart</c> parses equally well as the resource <c>restart</c> of type
    ///     <c>servers/main</c>. Nothing in the path distinguishes them. docs/plan/08 § The write path,
    ///     end to end removes the ambiguity from the other end — <c>POST</c>
    ///     <i>
    ///         "appears only for
    ///         actions on an existing resource … never for creation"
    ///     </i> — so on this verb the tail is an
    ///     action by definition, and the registry decides whether it is a <i>known</i> one.
    /// </remarks>
    static Result<GatewayRoute> ResolveAction(string path, Guid tenantId) {
        var lastSlash = path.LastIndexOf('/');

        if (lastSlash <= 0 || lastSlash == path.Length - 1) {
            return Result<GatewayRoute>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{path}' is not an action on a resource. A POST addresses "
                + "'{resource}/{action}' — docs/plan/08 § The write path, end to end."
            );
        }

        var parsed = ResourceId.ParsePath(path[..lastSlash]);
        if (parsed.TryGetError(out var error)) {
            return Result<GatewayRoute>.Failure(error);
        }

        return Result<GatewayRoute>.Success(
            new(
                RouteKind.Action,
                parsed.GetValueOrThrow() with { TenantId = tenantId },
                path[(lastSlash + 1)..],
                Guid.Empty,
                ""
            )
        );
    }

    /// <summary>
    ///     What stage 5 needs to know about a request <i>before</i> stage 6 has run.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is a wrinkle in docs/plan/10 § Request pipeline and it is worth naming.</b> Rate
    ///     limiting is stage 5 and routing is stage 6, but the rate limiter has to know whether the
    ///     request is a long-poll or a hub handshake, because docs/plan/10 § Rate limiting exempts
    ///     both from the request-count buckets. Waiting for stage 6 would mean rate limiting after
    ///     routing, and routing reads the provider registry — which is exactly the work stage 5 exists
    ///     to keep off a flood's path. So the classification here is a
    ///     <b>
    ///         prefix test on the raw
    ///         path
    ///     </b>: no registry, no allocation beyond a substring, and it cannot fail.
    /// </remarks>
    public static RequestClass Classify(string path, string method, IQueryCollection query) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(query);

        if (path.StartsWith(HubPrefix, StringComparison.Ordinal)) {
            return RequestClass.Hub;
        }

        // docs/plan/10 § Rate limiting: "Counting a 30-second long-poll as one request against a
        // 5-minute window is how you accidentally rate-limit your own portal."
        if (path.StartsWith(OperationsPrefix, StringComparison.Ordinal) && query.ContainsKey("wait")) {
            return RequestClass.LongPoll;
        }

        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            ? RequestClass.Read
            : RequestClass.Write;
    }
}

/// <summary>Which of docs/plan/10 § Rate limiting's regimes a request falls under.</summary>
enum RequestClass {
    /// <summary>An ordinary read. Counted against the subscription-read bucket.</summary>
    Read = 0,

    /// <summary>An ordinary write. Counted against the subscription-write bucket.</summary>
    Write,

    /// <summary>A long-poll. ⚠ Exempt from the count buckets; gets a concurrency slot.</summary>
    LongPoll,

    /// <summary>A SignalR handshake or negotiate. ⚠ Same exemption, same reason.</summary>
    Hub
}
