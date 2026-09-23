using CyberCloud.Billing.Contracts;
using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.Gateway.Host.WellKnown;
using CyberCloud.Identity.Validation;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 8 — to the resource manager, which owns authorization, quota and locks.
///     docs/plan/10 § Request pipeline.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The other load-bearing stage, and the reason is what is <i>not</i> here.</b>
///         docs/plan/10 § Request pipeline:
///         <i>
///             "Authorization inside dispatch rather than as gateway
///             middleware means the gateway cannot be bypassed by a future internal caller, and there is
///             exactly one enforcement seam."
///         </i> There is no check in this file, no
///         <c>IResourceAuthorizer</c> in this assembly's reference set, and
///         <c>GatewayIsolationTests</c> asserts both. A permission check appearing here would not be
///         a duplicate — it would be a <i>second</i> seam, and the one that gets updated when a rule
///         changes is whichever one the author remembered.
///     </para>
///     <para>
///         ⚠ <b>The address dispatched on is rebuilt from the token's tenant.</b>
///         <see cref="GatewayRoute.ResourcePath" /> renders a <see cref="ResourceId" /> whose
///         <c>TenantId</c> came from <c>CallerContext</c>, not from the URL. Stage 3 has already
///         refused a disagreement; this is the second defence, and it is the one that still holds if
///         somebody deletes the first.
///     </para>
/// </remarks>
sealed class DispatchStage(
    IResourceManager manager,
    IScopeManager scopes,
    IRoleAssignmentManager roles,
    IResourceGraphQuery graph,
    ICostQuery costs,
    IOperationReader operations,
    IHubTicketStore tickets,
    GatewayOptions options
)
    : IGatewayStage {
    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.Dispatch;

    /// <inheritdoc />
    public async Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Http.Request.Path.Value ?? "";

        return context.Route.Kind switch {
            RouteKind.Operation => await OperationAsync(context, path, cancellationToken),
            RouteKind.Resource => await ResourceAsync(context, path, cancellationToken),
            RouteKind.Scope => await ScopeAsync(context, path, cancellationToken),
            RouteKind.ScopeCollection => await ScopeCollectionAsync(context, path, cancellationToken),
            RouteKind.RoleAssignment => await RoleAssignmentAsync(context, path, cancellationToken),
            RouteKind.RoleAssignmentCollection => await RoleAssignmentCollectionAsync(context, path, cancellationToken),
            RouteKind.ResourceGraphQuery => await ResourceGraphQueryAsync(context, path, cancellationToken),
            RouteKind.CostQuery => await CostQueryAsync(context, path, cancellationToken),
            RouteKind.Collection => await CollectionAsync(context, path, cancellationToken),
            RouteKind.Action => await ActionAsync(context, path, cancellationToken),
            // A hub request leaves the pipeline here and is served by SignalR's own middleware; the
            // pipeline's job for it was stages 1 to 5.
            RouteKind.Hub => null,
            // The ticket that hub's WebSocket will carry — minted here, from the claims stage 2
            // parked, because this process is the one that validated them. HubTickets says why it is
            // the gateway's and not the console's connect action's.
            RouteKind.HubTicket => await HubTicketAsync(context, cancellationToken),
            // An agent's upgrade leaves the same way a hub's does: the endpoint after the pipeline
            // admits it or answers 401 — AgentTunnelEndpoint.
            RouteKind.AgentTunnel => null,
            RouteKind.OpenApi => OpenApi(context),
            // A constant, and the only plain-text body this gateway writes. No caller, no tenant, no
            // grain — SecurityTxt's remarks say why it is embedded rather than read at start-up.
            RouteKind.SecurityTxt => GatewayOutcome.PlainText(SecurityTxt.Content),
            _ => GatewayOutcome.Failure(StatusCodes.Status404NotFound, GatewayErrors.NotFound(path))
        };
    }

    async Task<GatewayOutcome> OperationAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var status = await operations.ReadAsync(context.Caller, context.Route.OperationId, cancellationToken);

        if (status.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var value = status.GetValueOrThrow();

        var outcome = new GatewayOutcome {
            StatusCode = StatusCodes.Status200OK, Json = ResponseBodies.Operation(value)
        };

        // ⚠ Retry-After only while the operation is running. Sending it on a terminal status tells a
        // polite client to keep polling something that will never change again.
        return value.IsTerminal
            ? outcome
            : outcome.WithHeader(
                GatewayHeaders.RetryAfter,
                options.OperationRetryAfterSeconds.ToString(CultureInfo.InvariantCulture)
            );
    }

    async Task<GatewayOutcome> ResourceAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var request = Build(context, WriteVerbFor(context.Http.Request.Method));

        if (HttpMethods.IsGet(context.Http.Request.Method)) {
            var read = await manager.ReadAsync(request, cancellationToken);

            return read.TryGetError(out var readError)
                ? ResultShaper.Shape(readError, path)
                : new() {
                    StatusCode = StatusCodes.Status200OK, Json = ResponseBodies.Resource(read.GetValueOrThrow())
                };
        }

        var accepted = HttpMethods.IsDelete(context.Http.Request.Method)
            ? await manager.DeleteAsync(request, cancellationToken)
            : await manager.WriteAsync(request, cancellationToken);

        return accepted.TryGetError(out var error)
            ? ResultShaper.Shape(error, path)
            : Accepted(context, accepted.GetValueOrThrow());
    }

    /// <summary>
    ///     A scope — docs/plan/06 § The hierarchy's management group, subscription and resource group.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>201</c> on a create and <c>200</c> on a repeat, with no <c>202</c> and no
    ///             <c>Azure-AsyncOperation</c> anywhere.
    ///         </b> A subscription and a resource group are one
    ///         grain activation each and converge before the call returns, so there is nothing to
    ///         poll — and a <c>202</c> here would advertise an operation URL that answers <c>404</c>
    ///         to every client polite enough to follow it, which is the mistake
    ///         <see cref="ActionAsync" /> already avoids for a synchronous action.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No authorization here, exactly as for a resource.</b> The check is
    ///         <c>IScopeManager</c>'s, against the same engine behind the same seam. A permission
    ///         check appearing in this file would be a <i>second</i> seam —
    ///         <c>GatewayIsolationTests</c> reads this project's source for that.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>DELETE</c> serves a resource group and refuses a subscription and a tenant,
    ///             and it answers <c>204</c> rather than <c>202</c>.
    ///         </b> It does <b>not</b> cascade: a
    ///         group that still holds resources is refused, naming them, because a cascade is a
    ///         per-resource delete with each resource's own lock, authorization, soft-delete window
    ///         and failable teardown, and one that skipped those would be a way to delete a locked
    ///         resource by deleting its group. Since the group is therefore already empty when this
    ///         runs, there is nothing to poll — <c>IScopeManager.DeleteAsync</c>'s remarks carry the
    ///         whole argument.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> ScopeAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var method = context.Http.Request.Method;

        var request = new ScopeRequest {
            // ⚠ The rebuilt path, carrying the TOKEN's tenant. Never context.Http.Request.Path.
            Path = context.Route.ResourcePath, Body = context.Body, Caller = context.Caller
        };

        if (HttpMethods.IsGet(method)) {
            var read = await scopes.ReadAsync(request, cancellationToken);

            return read.TryGetError(out var readError)
                ? ResultShaper.Shape(readError, path)
                : new() { StatusCode = StatusCodes.Status200OK, Json = ResponseBodies.Scope(read.GetValueOrThrow()) };
        }

        if (HttpMethods.IsDelete(method)) {
            var removed = await scopes.DeleteAsync(request, cancellationToken);

            // ⚠ 204 and not 202, unlike a resource delete. There is no operation to poll: every
            // member is already gone — IScopeManager.DeleteAsync refuses otherwise — so what the
            // call did was seal a grain, reclaim a namespace per cluster and drop a listing entry,
            // all of it finished by the time this returns. Handing back a 202 and an Operation-Id
            // that resolves to nothing would be a poll loop for every client polite enough to
            // follow it.
            return removed.TryGetError(out var removeError)
                ? ResultShaper.Shape(removeError, path)
                : new GatewayOutcome { StatusCode = StatusCodes.Status204NoContent };
        }

        if (!HttpMethods.IsPut(method)) {
            return new GatewayOutcome {
                StatusCode = StatusCodes.Status405MethodNotAllowed,
                Error = new(
                    ErrorCode.InvalidRequestBody,
                    $"{method} is not supported on a scope. A management group, a subscription and a "
                    + "resource group are read with GET, created with PUT and — for a management group "
                    + "or a resource group — deleted with DELETE; POST is an action on an existing "
                    + "resource and never a create (docs/plan/08 § The write path, end to end)."
                )
            }.WithHeader(GatewayHeaders.Allow, "GET, PUT, DELETE");
        }

        var created = await scopes.CreateAsync(request, cancellationToken);

        if (created.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var snapshot = created.GetValueOrThrow();

        return new() {
            StatusCode = snapshot.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
            Json = ResponseBodies.Scope(snapshot)
        };
    }

    /// <summary>
    ///     The scope collection <c>GET</c> — a tenant's subscriptions or a subscription's resource
    ///     groups, paged and filtered to what the caller may read. docs/plan/10 § Shape.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The same page parameters as a resource collection, read the same way and
    ///             echoed into <c>nextLink</c> the same way
    ///         </b> — <c>$top</c> parsed leniently and used
    ///         twice (#76), <c>$skipToken</c> passed through verbatim — for the reasons
    ///         <see cref="CollectionAsync" /> gives. A client that pages one collection of this API
    ///         pages this one with no branch.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The manager takes the <i>parent's</i> path and the link is built from the
    ///             <i>collection's</i>.
    ///         </b> A scope collection has no grain, so
    ///         <c>IScopeManager.ListAsync</c> is addressed at the tenant or subscription whose
    ///         listing it reads; the <c>nextLink</c> has to be the URL the caller requested, which
    ///         is the collection's. Both come off the rebuilt route, carrying the token's tenant.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No check here and no filter here</b>, for the reason no other dispatch has one:
    ///         which members the page holds is the manager's question, behind the one seam.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> ScopeCollectionAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var query = context.Http.Request.Query;
        var top = int.TryParse(query["$top"], CultureInfo.InvariantCulture, out var asked) ? asked : 0;

        var listed = await scopes.ListAsync(
            new() {
                // ⚠ The rebuilt parent path, carrying the TOKEN's tenant. Never context.Http.Request.Path.
                ParentPath = context.Route.Scopes.Parent.Path,
                // Which of the parent's collections — a tenant has two since issue #39.
                MemberKind = context.Route.Scopes.MemberKind,
                Caller = context.Caller,
                Top = top,
                Continuation = query["$skipToken"].ToString()
            },
            cancellationToken
        );

        if (listed.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var page = listed.GetValueOrThrow();

        return new() {
            StatusCode = StatusCodes.Status200OK,
            Json = ResponseBodies.ScopeCollection(
                page,
                GatewayRouterPaths.NextLink(
                    options.PublicBaseUri,
                    context.Route.CollectionPath,
                    context.ApiVersion.Value,
                    top,
                    page.Continuation
                )
            )
        };
    }

    /// <summary>
    ///     A role assignment — docs/plan/07 § Azure RBAC, expressed in it, the write half.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>201</c> on a grant, <c>200</c> on a repeat, <c>204</c> on a revoke, and no
    ///             <c>202</c> anywhere.
    ///         </b> A role assignment is one tuple write and it converges before
    ///         the call returns, so there is nothing to poll — the same argument
    ///         <see cref="ScopeAsync" /> makes for a scope, one grain call shorter.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No authorization here, exactly as for a scope and a resource.</b> The
    ///         <c>assignRole</c> check is <c>IRoleAssignmentManager</c>'s, against the same engine
    ///         behind the same seam — and this is the one verb where the temptation to check at the
    ///         gateway is worth naming, because "only an owner may grant" reads like a routing rule.
    ///         It is not: whether the caller is an owner is a walk over tuples, and
    ///         <c>GatewayIsolationTests</c> reads this project's source to keep that walk out of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>PATCH</c> and <c>POST</c> are <c>405</c>.</b> An assignment has no mutable
    ///         property — its address is its whole content — so a merge patch would have nothing to
    ///         merge, and there is no action on one.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> RoleAssignmentAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var method = context.Http.Request.Method;

        var request = new RoleAssignmentRequest {
            // ⚠ The rebuilt path, carrying the TOKEN's tenant. Never context.Http.Request.Path.
            Path = context.Route.ResourcePath, Body = context.Body, Caller = context.Caller
        };

        if (HttpMethods.IsGet(method)) {
            var read = await roles.ReadAsync(request, cancellationToken);

            return read.TryGetError(out var readError)
                ? ResultShaper.Shape(readError, path)
                : new() {
                    StatusCode = StatusCodes.Status200OK, Json = ResponseBodies.RoleAssignment(read.GetValueOrThrow())
                };
        }

        if (HttpMethods.IsDelete(method)) {
            var revoked = await roles.RevokeAsync(request, cancellationToken);

            return revoked.TryGetError(out var revokeError)
                ? ResultShaper.Shape(revokeError, path)
                : new GatewayOutcome { StatusCode = StatusCodes.Status204NoContent };
        }

        if (!HttpMethods.IsPut(method)) {
            return new GatewayOutcome {
                StatusCode = StatusCodes.Status405MethodNotAllowed,
                Error = new(
                    ErrorCode.InvalidRequestBody,
                    $"{method} is not supported on a role assignment. It is read with GET, granted "
                    + "with PUT and revoked with DELETE; it has no mutable property to PATCH and no "
                    + "action to POST — docs/plan/07 § Azure RBAC, expressed in it."
                )
            }.WithHeader(GatewayHeaders.Allow, "GET, PUT, DELETE");
        }

        var assigned = await roles.AssignAsync(request, cancellationToken);

        if (assigned.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var snapshot = assigned.GetValueOrThrow();

        return new() {
            StatusCode = snapshot.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
            Json = ResponseBodies.RoleAssignment(snapshot)
        };
    }

    /// <summary>
    ///     The role assignment collection <c>GET</c> — what is assigned at a scope, direct and
    ///     inherited, paged. docs/plan/07 § Azure RBAC, expressed in it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The same page parameters as a resource collection, read the same way and echoed
    ///             into <c>nextLink</c> the same way
    ///         </b> — <c>$top</c> parsed leniently, <c>$skipToken</c>
    ///         passed through verbatim, and the caller's own <c>$top</c> in the link rather than the
    ///         clamp, for the reasons <see cref="CollectionAsync" /> gives. A client that pages one
    ///         collection of this API pages this one with no branch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No check here, and no per-row filter here or anywhere.</b> Whether the caller
    ///         may read the scope is <c>IRoleAssignmentManager.ListAsync</c>'s question, and its
    ///         remarks say why one check on the scope is the whole of it.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> RoleAssignmentCollectionAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var query = context.Http.Request.Query;
        var top = int.TryParse(query["$top"], CultureInfo.InvariantCulture, out var asked) ? asked : 0;

        var listed = await roles.ListAsync(
            new() {
                // ⚠ The rebuilt path, carrying the TOKEN's tenant. Never context.Http.Request.Path.
                Path = context.Route.CollectionPath,
                Caller = context.Caller,
                Top = top,
                Continuation = query["$skipToken"].ToString()
            },
            cancellationToken
        );

        if (listed.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var page = listed.GetValueOrThrow();

        return new() {
            StatusCode = StatusCodes.Status200OK,
            Json = ResponseBodies.RoleAssignments(
                page,
                GatewayRouterPaths.NextLink(
                    options.PublicBaseUri,
                    context.Route.CollectionPath,
                    context.ApiVersion.Value,
                    top,
                    page.Continuation
                )
            )
        };
    }

    /// <summary>
    ///     The resource graph query <c>POST</c> —
    ///     <c>
    /// { "query": "resources | …", "$top": n,
    ///     "$skipToken": "…" }
    ///     </c> to <c>IResourceGraphQuery</c>, answered in the collection
    ///     envelope. docs/plan/08 § The resource-graph projection, the query half of #54.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The page parameters are read from the body first and the query string second,
    ///             and the <c>nextLink</c> carries them in the query string.
    ///         </b> A <c>POST</c> has a body
    ///         to put <c>$top</c> in, and Azure Resource Graph's clients put it there; but a
    ///         <c>nextLink</c> is a URL, and the collection rule of this API — the link is the whole
    ///         next request (#76) — means the offset has to survive in it. So a client follows the
    ///         link by <c>POST</c>ing the same body to it, and a body that repeats <c>$skipToken</c>
    ///         wins over the URL's, which lets a client that tracks the token itself ignore the link.
    ///         <c>ResourceGraphQueryRoutingTests</c> pins both readings.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No check here, and no filter here.</b> What the caller may read is inside the
    ///         query the service builds — the access column ANDed in — behind the one seam; this
    ///         stage hands over the caller and the text and renders what comes back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>GET</c> on the address is <c>405</c> with <c>Allow: POST</c>.</b> The scope
    ///         and the role assignment are the other addresses that answer <c>405</c>, and for the
    ///         same reason: the address exists and the verb is the wrong one. A query is a program,
    ///         and <c>ResourceGraphAddress</c>'s remarks say why a URL is not where one goes.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> ResourceGraphQueryAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        if (!HttpMethods.IsPost(context.Http.Request.Method)) {
            return new GatewayOutcome {
                StatusCode = StatusCodes.Status405MethodNotAllowed,
                Error = new(
                    ErrorCode.InvalidRequestBody,
                    $"{context.Http.Request.Method} is not supported on the resource graph. A query is a POST with "
                    + """{ "query": "resources | …" } as the body — docs/plan/08 § The resource-graph projection."""
                )
            }.WithHeader(GatewayHeaders.Allow, "POST");
        }

        var parsed = ResourceGraphQueryBody.Parse(context.Body, context.Http.Request.Query);

        if (parsed.TryGetError(out var bodyError)) {
            return ResultShaper.Shape(bodyError, path);
        }

        var body = parsed.GetValueOrThrow();

        var answered = await graph.QueryAsync(
            new() {
                Query = body.Query,
                // ⚠ The caller as stage 2 and 3 established it, tenant included. The address carries
                // nothing else, and the token's tenant is the one the service queries.
                Caller = context.Caller,
                Top = body.Top,
                Continuation = body.Continuation
            },
            cancellationToken
        );

        if (answered.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var page = answered.GetValueOrThrow();

        return new() {
            StatusCode = StatusCodes.Status200OK,
            Json = ResponseBodies.ResourceGraphPage(
                page,
                GatewayRouterPaths.NextLink(
                    options.PublicBaseUri,
                    context.Route.CollectionPath,
                    context.ApiVersion.Value,
                    body.Top,
                    page.Continuation
                )
            )
        };
    }

    /// <summary>
    ///     The cost query <c>POST</c> — a period and a grouping to <see cref="ICostQuery" />, for the
    ///     subscription or group the address names. docs/plan/22 § Cost visibility, issue #38.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No check and no filter here</b>, for the reason the resource graph's query has
    ///         none: the cost grain prices the scope and removes every row the caller may not read,
    ///         behind the one seam. This stage copies the caller's subject across — the only two
    ///         fields of it the grain needs — and renders what comes back; a caller who may read
    ///         nothing gets the grain's <c>404</c>, which is the absent subscription's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tenant is the token's, twice.</b> The address was rebuilt with it at stage 6,
    ///         and the grain call is qualified with it here; the request itself carries no tenant.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> CostQueryAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        if (!HttpMethods.IsPost(context.Http.Request.Method)) {
            return new GatewayOutcome {
                StatusCode = StatusCodes.Status405MethodNotAllowed,
                Error = new(
                    ErrorCode.InvalidRequestBody,
                    $"{context.Http.Request.Method} is not supported on the cost query. A query is a POST with "
                    + """{ "from": "…", "to": "…", "groupBy": "…" } as the body — docs/plan/22 § Cost visibility."""
                )
            }.WithHeader(GatewayHeaders.Allow, "POST");
        }

        var parsed = CostQueryBody.Parse(context.Body);

        if (parsed.TryGetError(out var bodyError)) {
            return ResultShaper.Shape(bodyError, path);
        }

        var body = parsed.GetValueOrThrow();
        var scope = context.Route.CostQuery.Scope;

        var answered = await costs.QueryAsync(
            context.Caller.TenantId,
            new() {
                Caller = new() { SubjectType = context.Caller.SubjectType, SubjectId = context.Caller.SubjectId },
                SubscriptionId = scope.SubscriptionId,
                ResourceGroup = scope.Kind == ScopeKind.ResourceGroup ? scope.ResourceGroup : string.Empty,
                From = body.From,
                To = body.To,
                Grouping = body.Grouping
            },
            cancellationToken
        );

        return answered.TryGetError(out var error)
            ? ResultShaper.Shape(error, path)
            : new() { StatusCode = StatusCodes.Status200OK, Json = CostQueryBody.Render(answered.GetValueOrThrow()) };
    }

    /// <summary>
    ///     The collection <c>GET</c>. Straight to <c>IResourceManager.ListAsync</c>, which owns the
    ///     per-member filter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>There is no check here either, and on this endpoint the temptation is larger.</b>
    ///         A listing is the one response whose <i>size</i> depends on authorization, so the
    ///         obvious shortcut is to ask once at this layer and hand the manager a pre-filtered set.
    ///         That would be the second enforcement seam docs/plan/10 § Request pipeline exists to
    ///         prevent, and it would be the one that gets forgotten when the rule changes:
    ///         <c>GatewayIsolationTests</c> asserts this assembly cannot even name
    ///         <c>IResourceAuthorizer</c>.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>$top</c> is parsed leniently and <c>$skipToken</c> is passed through
    ///             verbatim.
    ///         </b> A <c>$top</c> that is not a number is <i>ignored</i> rather than
    ///         refused, because the page size is a hint the platform clamps anyway — see
    ///         <c>ListRequest.PageSize</c>. A <c>$skipToken</c> naming a path in another tenant
    ///         changes nothing: the manager resumes at "the next member of THIS group whose path
    ///         sorts after this string", and the group it walks came from the rebuilt address.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             And the parsed <c>$top</c> is used twice — for this page and for the
    ///             <c>nextLink</c> of the next one (#76).
    ///         </b> A page-shaping parameter that reaches the
    ///         manager but not the link the client is told to follow applies to page one and to
    ///         nothing after it, with no error anywhere; the argument is on
    ///         <c>GatewayRouterPaths.NextLink</c>. Reading it into a local rather than parsing it
    ///         twice is what keeps the request and the link describing the same request.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> CollectionAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var query = context.Http.Request.Query;
        var top = int.TryParse(query["$top"], CultureInfo.InvariantCulture, out var asked) ? asked : 0;

        var listed = await manager.ListAsync(
            new() {
                // ⚠ The rebuilt path, carrying the TOKEN's tenant. Never context.Http.Request.Path.
                Path = context.Route.CollectionPath,
                ApiVersion = context.ApiVersion.Value,
                Caller = context.Caller,
                Top = top,
                Continuation = query["$skipToken"].ToString()
            },
            cancellationToken
        );

        if (listed.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var page = listed.GetValueOrThrow();

        return new() {
            StatusCode = StatusCodes.Status200OK,
            Json = ResponseBodies.Collection(
                page,
                GatewayRouterPaths.NextLink(
                    options.PublicBaseUri,
                    context.Route.CollectionPath,
                    context.ApiVersion.Value,
                    top,
                    page.Continuation
                )
            )
        };
    }

    async Task<GatewayOutcome> ActionAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var accepted = await manager.ActionAsync(Build(context, WriteVerb.Post), cancellationToken);

        if (accepted.TryGetError(out var error)) {
            return ResultShaper.Shape(error, path);
        }

        var value = accepted.GetValueOrThrow();

        // ⚠ 200 AND THE ACTION'S OWN BODY, WITH NO Azure-AsyncOperation AND NO Retry-After. An action
        // that did its work has nothing to poll, and Accepted() below would advertise an operation id
        // of Guid.Empty — a URL that answers 404 to every client polite enough to follow it.
        //
        // ⚠ Cache-Control: no-store, because this is the response a `secret: true` action's value
        // leaves in. docs/plan/08 § The provider registry makes such an action "never cached", and a
        // credential sitting in a proxy or a browser's disk cache is the reason.
        return value.Completed
            ? new GatewayOutcome {
                StatusCode = StatusCodes.Status200OK,
                Json = value.ActionResponse.Length == 0 ? "{}" : value.ActionResponse
            }.WithHeader(GatewayHeaders.CacheControl, "no-store")
            : Accepted(context, value);
    }

    /// <summary>
    ///     The <c>202</c> of docs/plan/10 § Long-running operations, with both headers.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>Azure-AsyncOperation</c> and <c>Retry-After</c> together, always.</b> That pair is
    ///     what makes <c>Operation&lt;T&gt;</c> in an Azure-shaped SDK and <c>--wait</c> in the CLI
    ///     work without a line of bespoke code — docs/plan/10 § Long-running operations. A <c>202</c>
    ///     missing either one is a <c>202</c> every client has to special-case.
    /// </remarks>
    GatewayOutcome Accepted(GatewayRequestContext context, WriteAccepted accepted) =>
        new GatewayOutcome {
            StatusCode = StatusCodes.Status202Accepted, Json = ResponseBodies.Resource(accepted.Resource)
        }
                .WithHeader(
                    GatewayHeaders.AsyncOperation,
                    GatewayRouterPaths.AsyncOperation(
                        options.PublicBaseUri,
                        accepted.OperationId,
                        context.ApiVersion.Value
                    )
                )
                .WithHeader(
                    GatewayHeaders.RetryAfter,
                    accepted.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture)
                );

    async Task<GatewayOutcome> HubTicketAsync(GatewayRequestContext context, CancellationToken cancellationToken) {
        if (context.Http.Items[AuthenticateStage.ClaimsItemKey] is not TokenClaims claims) {
            // Unreachable through the pipeline: stage 2 answers 401 before a request with no claims
            // gets this far, and the ticket route is not one of the anonymous three. Refusing rather
            // than minting from an empty caller, because a ticket with no tenant behind it would be
            // the one thing this route must never hand out.
            return GatewayOutcome.Failure(
                StatusCodes.Status401Unauthorized,
                BearerTokenErrors.Unauthenticated("a hub ticket needs the caller's own token")
            )
                .WithHeader("WWW-Authenticate", "Bearer");
        }

        var ticket = await tickets.IssueAsync(claims, context.Route.HubName, cancellationToken);

        // ⚠ Cache-Control: no-store, for the reason a `secret: true` action's response carries it:
        // this body is a credential, thirty seconds of one, and a proxy that kept it would hand the
        // next requester somebody else's hub.
        return new GatewayOutcome {
            StatusCode = StatusCodes.Status200OK, Json = HubTickets.Body(ticket, context.Route.HubName)
        }
            .WithHeader(GatewayHeaders.CacheControl, "no-store");
    }

    static GatewayOutcome OpenApi(GatewayRequestContext context) =>
        new() {
            StatusCode = StatusCodes.Status200OK,
            Json = "{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"Cyber Cloud\",\"version\":\""
                + context.ApiVersion.Value
                + "\"},\"paths\":{}}"
        };

    WriteRequest Build(GatewayRequestContext context, WriteVerb verb) =>
        new() {
            // ⚠ The rebuilt path, carrying the TOKEN's tenant. Never context.Http.Request.Path.
            Path = context.Route.ResourcePath,
            ApiVersion = context.ApiVersion.Value,
            Verb = verb,
            Body = context.Body.Length == 0 ? "{}" : context.Body,
            Caller = context.Caller,
            IfMatch = context.Http.Request.Headers.IfMatch.ToString(),
            Action = context.Route.Action
        };

    static WriteVerb WriteVerbFor(string method) =>
        method switch {
            _ when HttpMethods.IsPut(method) => WriteVerb.Put,
            _ when HttpMethods.IsPatch(method) => WriteVerb.Patch,
            _ when HttpMethods.IsPost(method) => WriteVerb.Post,
            _ when HttpMethods.IsDelete(method) => WriteVerb.Delete,
            _ => WriteVerb.Unknown
        };
}
