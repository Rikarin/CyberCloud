using System.Collections.Concurrent;

namespace CyberCloud.Providers.Monitor.Accounts;

/// <summary><see cref="IMonitorAccounts" /> over <see cref="IMonitorAccountGrain" />, remembering every "yes".</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Unqualified <c>GetGrain</c>, on purpose.</b> The ledger is null-tenant —
///         <see cref="GrainKeys.MetricsAccount" /> says why — and CC1006 knows the builder, so no
///         <c>ForTenant</c> belongs here.
///     </para>
///     <para>
///         ⚠ <b>A "yes" is cached for the life of the process, and that is only safe because a claim is
///         never released.</b> Every metrics query asks, and the answer can't change once it's yes:
///         the grain has no release, and a workspace's GUID doesn't change. A "no" isn't cached — the
///         workspace may simply not have reconciled yet. If the ledger ever learns to release, this
///         cache becomes a cross-tenant read and must go with it.
///     </para>
/// </remarks>
/// <param name="grains">The silo's or the gateway's grain factory.</param>
public sealed class GrainMonitorAccounts(IGrainFactory grains) : IMonitorAccounts {
    readonly ConcurrentDictionary<uint, Guid> held = new();

    /// <inheritdoc />
    public async Task<bool> ClaimAsync(uint accountId, Guid workspaceId, CancellationToken cancellationToken = default) {
        if (Remembered(accountId, workspaceId)) {
            return true;
        }

        var holds = await Account(accountId).ClaimAsync(workspaceId).WaitAsync(cancellationToken);

        return Remember(accountId, workspaceId, holds);
    }

    /// <inheritdoc />
    public async Task<bool> IsHeldByAsync(uint accountId, Guid workspaceId, CancellationToken cancellationToken = default) {
        if (Remembered(accountId, workspaceId)) {
            return true;
        }

        var holds = await Account(accountId).IsHeldByAsync(workspaceId).WaitAsync(cancellationToken);

        return Remember(accountId, workspaceId, holds);
    }

    bool Remembered(uint accountId, Guid workspaceId) =>
        held.TryGetValue(accountId, out var holder) && holder == workspaceId;

    bool Remember(uint accountId, Guid workspaceId, bool holds) {
        if (holds) {
            held[accountId] = workspaceId;
        }

        return holds;
    }

    IMonitorAccountGrain Account(uint accountId) => grains.GetGrain<IMonitorAccountGrain>(GrainKeys.MetricsAccount(accountId));
}
