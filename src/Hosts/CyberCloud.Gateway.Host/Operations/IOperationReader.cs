using CyberCloud.Gateway.Host.Http;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Operations;

/// <summary>
///     The <c>GET /operations/{opId}</c> of docs/plan/10 § Long-running operations.
/// </summary>
interface IOperationReader {
    /// <summary>Reads an operation's status.</summary>
    /// <param name="caller">Who is asking. ⚠ Its tenant is the token's and selects the grain.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The status, or <see cref="ErrorCode.ResourceNotFound" /> — for an operation that does not
    ///     exist, one in another tenant, and one whose resource the caller may not read, which are
    ///     the same answer on purpose.
    /// </returns>
    Task<Result<OperationStatus>> ReadAsync(
        CallerContext caller,
        Guid operationId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Reads the operation grain for the token's tenant, and asks the resource manager whether the
///     caller may see it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two boundaries, and neither is an authorization decision made here.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Cross-tenant</b> is closed by <c>ForTenant</c> and by nothing else. The gateway is
///             an Orleans client, so <c>Orleans.Multitenant</c>'s filter never runs for this call —
///             docs/plan/00 § The tenant-separation row, corrected. The tenant in the key comes from
///             the caller context, which stage 3 built from the token.
///         </item>
///         <item>
///             <b>Within-tenant</b> is closed by asking the one seam. An operation names the resource
///             it is driving, and "may this caller see that resource?" is precisely what
///             <see cref="IResourceManager.ReadAsync" /> answers — with a <c>404</c> that does not
///             disclose existence, decided inside the resource manager where docs/plan/07 § The
///             enforcement seam puts it. So this type performs no check of its own; it forwards a
///             question and copies the answer.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>What this costs, named rather than hidden:</b> polling an operation costs a resource
///         read as well as the operation read. The alternative — a <c>GetOperationAsync</c> on
///         <see cref="IResourceManager" /> that checks once — is the better design and is owed;
///         docs/plan/08 § Long-running operations describes the endpoint but the interface has no
///         method for it, which is a gap in the plan rather than a decision.
///     </para>
/// </remarks>
sealed class TenantScopedOperationReader(IGrainFactory grains, IResourceManager manager) : IOperationReader {
    /// <inheritdoc />
    public async Task<Result<OperationStatus>> ReadAsync(
        CallerContext caller,
        Guid operationId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);

        // ⚠ ForTenant, from the token's tenant. CC1006 fails the build on a raw GetGrain here, which
        // is the whole reason this project references CyberCloud.Analyzers.
        var operation = grains
            .ForTenant(caller.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

        var status = await operation.GetAsync();
        if (status.TryGetError(out var error)) {
            return Result<OperationStatus>.Failure(error);
        }

        var value = status.GetValueOrThrow();

        if (value.ResourcePath.Length == 0) {
            // An operation with no resource is one that was never started. Same 404 as any other
            // absent thing.
            return NotFound(operationId);
        }

        // The one seam. A caller who cannot read the resource cannot read its operation, and the
        // decision — including whether it is 404 or 403 — is made inside the resource manager.
        var readable = await manager.ReadAsync(
            new WriteRequest { Path = value.ResourcePath, ApiVersion = "", Caller = caller },
            cancellationToken
        );

        if (readable.TryGetError(out var readError)) {
            return Result<OperationStatus>.Failure(readError);
        }

        return Result<OperationStatus>.Success(value);
    }

    static Result<OperationStatus> NotFound(Guid operationId) =>
        Result<OperationStatus>.Failure(GatewayErrors.NotFound(
            GatewayRouterPaths.Operation(operationId)
        ));
}

/// <summary>The URLs docs/plan/10 § Long-running operations gives, built in one place.</summary>
static class GatewayRouterPaths {
    /// <summary>The operation endpoint's path.</summary>
    /// <param name="operationId">The operation.</param>
    public static string Operation(Guid operationId) =>
        string.Create(CultureInfo.InvariantCulture, $"/operations/{operationId:D}");

    /// <summary>The absolute <c>Azure-AsyncOperation</c> URL, with the api-version the caller used.</summary>
    /// <param name="baseUri">
    ///     The public base, from configuration. ⚠ Not <c>Request.Host</c>: behind Envoy that is
    ///     whatever the client sent, and a caller who can set it can make the platform hand every
    ///     other caller a polling URL pointing at their own host.
    /// </param>
    /// <param name="operationId">The operation.</param>
    /// <param name="apiVersion">The api-version to keep polling at.</param>
    public static string AsyncOperation(string baseUri, Guid operationId, string apiVersion) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUri.TrimEnd('/')}/operations/{operationId:D}?api-version={apiVersion}"
        );
}
