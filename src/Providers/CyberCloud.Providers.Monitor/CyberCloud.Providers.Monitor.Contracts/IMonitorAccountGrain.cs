using Orleans.Concurrency;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     The claim on one VictoriaMetrics <c>accountID</c>: which monitor workspace, in any tenant, got
///     it first. Null tenant, Durable, key <see cref="GrainKeys.MetricsAccount" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHY A LEDGER, WHEN <see cref="MonitorWorkspaces.AccountId" /> WAS CHOSEN SO NONE WOULD BE
///         NEEDED.</b> The account is the workspace GUID folded to 32 bits, and a fold isn't a
///         bijection: two workspaces sharing an account read and write each other's metrics. The
///         review of #41 found the metrics explorer made that a read path a tenant can drive —
///         create workspaces in a loop until one folds onto a victim's account, then list
///         <c>__name__</c> — so the fold is kept and the collision is refused. The reconciler claims
///         the account before it applies anything, and the explorer's metrics reads ask whether the
///         workspace they were handed holds it.
///     </para>
///     <para>
///         ⚠ <b>A CLAIM IS NEVER RELEASED.</b> A delete withdraws the <c>VMUser</c> and the row and
///         deletes nothing in vmstorage, so the series under an account outlive the workspace that
///         wrote them by up to their retention. Handing the account to a new workspace after a purge
///         would hand it the old tenant's retained metrics. A soft delete and a restore keep the same
///         GUID, so they keep the claim.
///     </para>
///     <para>
///         ⚠ <b>Two booleans, never a GUID.</b> Every caller is platform code running for some tenant,
///         and the null tenant is reachable from all of them — <c>PlatformCrossTenantAuthorizer</c>
///         allows the edge. An answer that named the holder would tell tenant B the GUID of tenant
///         A's workspace; "yes, you hold it" and "no" tell it nothing it can use.
///     </para>
/// </remarks>
[Alias("CyberCloud.Monitor.IMonitorAccountGrain")]
public interface IMonitorAccountGrain : IGrainWithStringKey {
    /// <summary>
    ///     Claims the account for a workspace if nobody holds it, and reports whether that workspace
    ///     holds it now.
    /// </summary>
    /// <param name="workspaceId">The workspace's resolved GUID, never <see cref="Guid.Empty" />.</param>
    /// <returns>
    ///     <c>true</c> if the account was free and is now this workspace's, or already was;
    ///     <c>false</c> if another workspace holds it.
    /// </returns>
    Task<bool> ClaimAsync(Guid workspaceId);

    /// <summary>Reports whether a workspace holds the account, without claiming it.</summary>
    /// <param name="workspaceId">The workspace's resolved GUID.</param>
    /// <returns><c>true</c> if this workspace claimed the account; <c>false</c> if another did or nobody has.</returns>
    [ReadOnly]
    Task<bool> IsHeldByAsync(Guid workspaceId);
}

/// <summary>
///     Reaches <see cref="IMonitorAccountGrain" /> for the reconciler and the explorer's handlers, with
///     the key written once.
/// </summary>
/// <remarks>
///     The same seam shape as <see cref="IAlertControlPlane" />: the workspace reconciler runs in the
///     silo and the query handlers in the gateway, and both take this rather than an
///     <c>IGrainFactory</c>, so a test can hand either one a ledger without a cluster.
/// </remarks>
public interface IMonitorAccounts {
    /// <summary>
    ///     Claims <paramref name="accountId" /> for <paramref name="workspaceId" /> if nobody holds it,
    ///     and reports whether the workspace holds it now.
    /// </summary>
    /// <param name="accountId">The account, from <see cref="MonitorWorkspaces.AccountId" />.</param>
    /// <param name="workspaceId">The workspace's resolved GUID.</param>
    /// <param name="cancellationToken">Cancels the wait, not the claim, which is one grain turn.</param>
    /// <returns><c>true</c> if the workspace holds the account.</returns>
    Task<bool> ClaimAsync(uint accountId, Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Reports whether <paramref name="workspaceId" /> holds <paramref name="accountId" />, without claiming it.</summary>
    /// <param name="accountId">The account, from <see cref="MonitorWorkspaces.AccountId" />.</param>
    /// <param name="workspaceId">The workspace's resolved GUID.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><c>true</c> if the workspace claimed the account; <c>false</c> if another did or nobody has.</returns>
    Task<bool> IsHeldByAsync(uint accountId, Guid workspaceId, CancellationToken cancellationToken = default);
}
