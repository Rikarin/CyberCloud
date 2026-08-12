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
