using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Tenancy;
using CyberCloud.Tenancy.Directory;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 3 — the token's <c>tid</c> becomes the request's tenant, and anything that disagrees
///     becomes a <c>404</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE SECURITY BOUNDARY, AND IT IS THE ONLY ONE FOR AN HTTP REQUEST.</b>
///         docs/plan/10 § Request pipeline: <i>"Step 3 is a security boundary, not a routing
///         convenience, and this was not obvious. The gateway is an Orleans client, and
///         <c>Orleans.Multitenant</c>'s call filter skips clients entirely … So the runtime will not
///         stop a gateway code path from reaching another tenant's grain by naming its key — the
///         tenant resolved at step 3 is the only thing that does."</i>
///     </para>
///     <para>
///         What that means concretely for anybody editing this file: there is no second check below.
///         Not in the resource manager, not in the grain, not in <c>Orleans.Multitenant</c>. A change
///         here that lets a caller-controlled value select the tenant is a cross-tenant read with no
///         exception and no log line, and the test that would catch it is
///         <c>TenantFromTokenTests</c> — which is the single most important test in this project.
///     </para>
///     <para>
///         ⚠ <b>The directory lookup is in-process.</b> docs/plan/05 § The tenant directory makes
///         <see cref="TenantDirectoryCache.TryLookup" /> a dictionary read against an immutable
///         snapshot that <i>cannot</i> do I/O. A miss falls through to a grain call, which is
///         measured and expected to be rare — and which is the only grain call in the first five
///         stages.
///     </para>
/// </remarks>
sealed class ResolveTenantStage(
    TenantDirectoryCache directory,
    ILogger<ResolveTenantStage> logger
)
    : IGatewayStage {
    /// <summary>The largest body the tenant check will read. Larger ones are refused at stage 7.</summary>
    /// <remarks>
    ///     ⚠ The body has to be buffered here, before routing, because <c>tenantId</c> in a body is
    ///     one of the surfaces a caller can smuggle a tenant through. Buffering is capped so that the
    ///     stage before rate limiting cannot be made expensive by an unauthenticated-sized request.
    /// </remarks>
    public const int MaxBufferedBodyBytes = 1024 * 1024;

    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.ResolveTenant;

    /// <inheritdoc />
    public async Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Http.Items[AuthenticateStage.ClaimsItemKey] is not TokenClaims claims) {
            // An anonymous route — docs/plan/10 § Shape's /openapi and /.well-known. There is no
            // tenant to establish and, crucially, no way for one to be inferred from the request:
            // the caller stays empty all the way to dispatch, so nothing tenant-scoped is reachable.
            return null;
        }

        var path = context.Http.Request.Path.Value ?? "";

        context.Body = await ReadBodyAsync(context.Http.Request, cancellationToken);

        // ── The rule. Every other surface is checked for disagreement, never read for a decision. ──
        if (TenantSmuggling.Disagrees(context.Http.Request, context.Body, claims.TenantId, out var surface)) {
            // ⚠ Logged with the TOKEN's tenant and the surface, never with the tenant that was
            // smuggled. Writing the attacker's guess into the log makes the log an index of tenant
            // ids for whoever reads it next.
            logger.LogWarning(
                "Request {RequestId} for tenant {TenantId} named a different tenant in {Surface}; "
                + "answered 404. docs/plan/10 § Request pipeline — the tenant comes from the token.",
                context.RequestId,
                claims.TenantId,
                surface
            );

            return NotFound(path);
        }

        var entry = await LookupAsync(claims.TenantId);
        if (entry is null) {
            // ⚠ The same 404. A token whose tenant is not in the directory is either a tenant that
            // was deleted or one that never existed, and neither is worth distinguishing to a caller.
            return NotFound(path);
        }

        context.Tenant = entry;

        context.Caller = new() {
            TenantId = claims.TenantId,
            SubjectType = claims.SubjectType,
            SubjectId = claims.SubjectId,
            ImpersonatedBy = claims.ImpersonatedBy,
            CorrelationId = context.CorrelationId
        };

        // docs/plan/06 § Tenant lifecycle: a suspended tenant keeps its reads and loses its writes.
        // 403 rather than 404 is right here and does not leak anything: the caller holds a token for
        // this tenant, so its existence is not news to them.
        if (entry.Status == TenantStatus.Suspended && !HttpMethods.IsGet(context.Http.Request.Method)) {
            return GatewayOutcome.Failure(
                StatusCodes.Status403Forbidden,
                new(
                    ErrorCode.TenantSuspended,
                    $"Tenant {claims.TenantId:D} is suspended. Reads continue; control-plane writes "
                    + "are rejected until it is reactivated — docs/plan/06 § Tenant lifecycle."
                )
            );
        }

        return null;
    }

    async Task<TenantDirectoryEntry?> LookupAsync(Guid tenantId) {
        if (directory.TryLookup(tenantId, out var resident)) {
            return resident;
        }

        var looked = await directory.LookupAsync(tenantId);
        return looked.TryGetValue(out var entry) ? entry : null;
    }

    static GatewayOutcome NotFound(string path) =>
        GatewayOutcome.Failure(StatusCodes.Status404NotFound, GatewayErrors.NotFound(path));

    static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken) {
        if (!HttpMethods.IsPut(request.Method)
            && !HttpMethods.IsPatch(request.Method)
            && !HttpMethods.IsPost(request.Method)) {
            return "";
        }

        request.EnableBuffering();

        using var reader = new StreamReader(
            request.Body,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );

        var buffer = new char[MaxBufferedBodyBytes];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);

        request.Body.Position = 0;

        return new(buffer, 0, read);
    }
}
