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

    /// <summary>
    ///     The absolute <c>nextLink</c> of a collection page, or empty when there is no next page.
    /// </summary>
    /// <param name="baseUri">The public base, from configuration — never <c>Request.Host</c>.</param>
    /// <param name="collectionPath">The collection's path.</param>
    /// <param name="apiVersion">The api-version to keep paging at.</param>
    /// <param name="top">
    ///     The <c>$top</c> this request was served with — <c>ListRequest.Top</c> as the dispatcher
    ///     parsed it, so zero or negative means "the caller expressed no page size" and no <c>$top</c>
    ///     is written. ⚠ The caller's own number and not <c>ListRequest.PageSize</c>: see the remarks.
    /// </param>
    /// <param name="continuation">
    ///     The previous page's continuation, or empty. ⚠ It is a resource path and therefore contains
    ///     <c>/</c>, so it is percent-encoded — an unescaped one would produce a URL whose query
    ///     string a client re-parses into a different value than the one that was handed out.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An absolute URL rather than a token in the body, because that is what makes
    ///         <c>AsyncPageable&lt;T&gt;</c> work with no bespoke code</b> — the same argument
    ///         docs/plan/10 § Long-running operations makes for <c>Azure-AsyncOperation</c>. A client
    ///         that has to reassemble the next request from a bare token has to know the endpoint's
    ///         paging parameter, which is exactly the per-endpoint knowledge a generated SDK does not
    ///         have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is also why every parameter that shaped THIS page has to be written into the
    ///         next one's URL, and why <c>$top</c> was a bug (#76).</b> This built the next page's URL
    ///         out of <c>api-version</c> and <c>$skipToken</c> alone, so a client that followed the
    ///         link it was handed — which is the only thing a generated pager does — asked for no page
    ///         size at all from page two onwards and got <c>ListRequest.DefaultPageSize</c>. The
    ///         symptom is not an error anywhere: <c>cyc … list --all --top 10</c> examines ten members
    ///         on the first page and fifty on every page after it, silently, and the caller who chose
    ///         the number has no way to see that it stopped applying. A page-shaping parameter this
    ///         endpoint grows later — an <c>$orderby</c>, a <c>$filter</c> — has the same obligation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The caller's own <c>$top</c> is echoed, not the clamped
    ///         <c>ListRequest.PageSize</c>.</b> Echoing the clamp would make the link a different
    ///         request from the one that produced this page — <c>$top=10000</c> would come back as
    ///         <c>$top=100</c> — and would put <c>ListRequest.MaxPageSize</c>, a number the platform
    ///         reserves the right to change, into a URL a client may store and replay after it does.
    ///         Re-sending what the caller sent means page <i>n</i> is clamped by exactly the rule that
    ///         clamped page one, whatever that rule becomes. A <c>$top</c> that was not a number was
    ///         already ignored rather than refused (see <c>DispatchStage.CollectionAsync</c>), arrives
    ///         here as zero, and is therefore not echoed: reflecting a caller's junk back out of a URL
    ///         the platform generates is how a malformed value acquires the look of an endorsement.
    ///     </para>
    ///     <para>
    ///         The number is formatted invariantly and not escaped, because an <see cref="int" /> has
    ///         no character that means anything in a query string — unlike
    ///         <paramref name="continuation" />, which does and is.
    ///     </para>
    /// </remarks>
    public static string NextLink(
        string baseUri,
        string collectionPath,
        string apiVersion,
        int top,
        string continuation
    ) {
        if (continuation.Length == 0) {
            return string.Empty;
        }

        var pageSize = top > 0 ? string.Create(CultureInfo.InvariantCulture, $"&$top={top}") : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUri.TrimEnd('/')}{collectionPath}?api-version={apiVersion}{pageSize}"
            + $"&$skipToken={Uri.EscapeDataString(continuation)}"
        );
    }
}
