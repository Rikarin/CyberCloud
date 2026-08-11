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
///     Forwards the poll to the resource manager, which decides.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type performs no check of its own, which is the same property it had before and
///         is the reason the seam survived the change.</b> docs/plan/10 § What the gateway must never
///         do forbids the gateway from performing authorization; docs/plan/07 § The enforcement seam
///         puts the one decision inside the resource manager. So the interface above is a
///         <i>question</i> and this is a forwarder — it copies whatever answer comes back, including
///         the <c>404</c> that does not disclose existence.
///     </para>
///     <para>
///         ⚠ <b>What changed is the cost, not the boundary.</b> This used to hold the operation grain
///         itself — <c>ForTenant(…).GetGrain&lt;IOperationGrain&gt;</c> — and then ask
///         <see cref="IResourceManager.ReadAsync" /> about the operation's <i>resource</i>, because
///         "may this caller see that resource?" was the only question the interface could answer. That
///         is an index resolve, a check, a resource-grain read and an api-version projection <b>per
///         poll</b>, and <c>cyc --wait</c> polls a nine-minute cluster create continuously.
///         <see cref="IResourceManager.GetOperationAsync" /> is the method docs/plan/08 § Long-running
///         operations always implied and the interface did not have; it reads the operation and runs
///         the same check once, in the seam.
///     </para>
///     <para>
///         ⚠ <b>Cross-tenant is still closed by <c>ForTenant</c> and by nothing else</b> — the gateway
///         is an Orleans client, so <c>Orleans.Multitenant</c>'s filter never runs for this call
///         (docs/plan/00 § The tenant-separation row, corrected). The <c>ForTenant</c> now happens
///         inside <see cref="IResourceManager.GetOperationAsync" />, keyed on the same
///         <see cref="CallerContext.TenantId" /> stage 3 built from the token and handed to it here.
///         Moving the call did not move the token.
///     </para>
/// </remarks>
sealed class TenantScopedOperationReader(IResourceManager manager) : IOperationReader {
    /// <inheritdoc />
    public async Task<Result<OperationStatus>> ReadAsync(
        CallerContext caller,
        Guid operationId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);

        return await manager.GetOperationAsync(operationId, caller, cancellationToken);
    }
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
