using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.Gateway.Host.Routing;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 8 — to the resource manager, which owns authorization, quota and locks.
///     docs/plan/10 § Request pipeline.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The other load-bearing stage, and the reason is what is <i>not</i> here.</b>
///         docs/plan/10 § Request pipeline: <i>"Authorization inside dispatch rather than as gateway
///         middleware means the gateway cannot be bypassed by a future internal caller, and there is
///         exactly one enforcement seam."</i> There is no check in this file, no
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
    IOperationReader operations,
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
            RouteKind.Collection => await CollectionAsync(context, path, cancellationToken),
            RouteKind.Action => await ActionAsync(context, path, cancellationToken),
            // A hub request leaves the pipeline here and is served by SignalR's own middleware; the
            // pipeline's job for it was stages 1 to 5.
            RouteKind.Hub => null,
            RouteKind.OpenApi => OpenApi(context),
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
            StatusCode = StatusCodes.Status200OK,
            Json = ResponseBodies.Operation(value)
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
                : new() { StatusCode = StatusCodes.Status200OK, Json = ResponseBodies.Resource(read.GetValueOrThrow()) };
        }

        var accepted = HttpMethods.IsDelete(context.Http.Request.Method)
            ? await manager.DeleteAsync(request, cancellationToken)
            : await manager.WriteAsync(request, cancellationToken);

        return accepted.TryGetError(out var error)
            ? ResultShaper.Shape(error, path)
            : Accepted(context, accepted.GetValueOrThrow());
    }

    /// <summary>
    ///     A scope — docs/plan/06 § The hierarchy's subscription and resource group.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>201</c> on a create and <c>200</c> on a repeat, with no <c>202</c> and no
    ///         <c>Azure-AsyncOperation</c> anywhere.</b> A subscription and a resource group are one
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
    ///         ⚠ <b><c>DELETE</c> serves a resource group and refuses a subscription and a tenant,
    ///         and it answers <c>204</c> rather than <c>202</c>.</b> It does <b>not</b> cascade: a
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
            Path = context.Route.ResourcePath,
            Body = context.Body,
            Caller = context.Caller
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
                    $"{method} is not supported on a scope. A subscription and a resource group are "
                    + "read with GET, created with PUT and — for a resource group — deleted with "
                    + "DELETE; POST is an action on an existing resource and never a create "
                    + "(docs/plan/08 § The write path, end to end)."
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
    ///         ⚠ <b><c>$top</c> is parsed leniently and <c>$skipToken</c> is passed through
    ///         verbatim.</b> A <c>$top</c> that is not a number is <i>ignored</i> rather than
    ///         refused, because the page size is a hint the platform clamps anyway — see
    ///         <c>ListRequest.PageSize</c>. A <c>$skipToken</c> naming a path in another tenant
    ///         changes nothing: the manager resumes at "the next member of THIS group whose path
    ///         sorts after this string", and the group it walks came from the rebuilt address.
    ///     </para>
    /// </remarks>
    async Task<GatewayOutcome> CollectionAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        var query = context.Http.Request.Query;

        var listed = await manager.ListAsync(
            new() {
                // ⚠ The rebuilt path, carrying the TOKEN's tenant. Never context.Http.Request.Path.
                Path = context.Route.CollectionPath,
                ApiVersion = context.ApiVersion.Value,
                Caller = context.Caller,
                Top = int.TryParse(query["$top"], CultureInfo.InvariantCulture, out var top) ? top : 0,
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
            StatusCode = StatusCodes.Status202Accepted,
            Json = ResponseBodies.Resource(accepted.Resource)
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
