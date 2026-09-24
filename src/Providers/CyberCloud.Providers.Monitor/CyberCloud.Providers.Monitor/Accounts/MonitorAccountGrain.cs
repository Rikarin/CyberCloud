using CyberCloud.Core.Time;
using CyberCloud.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Providers.Monitor.Accounts;

/// <summary>What <see cref="MonitorAccountGrain" /> keeps: the workspace that claimed the account, and when.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.MonitorAccountState")]
public sealed class MonitorAccountState {
    /// <summary>The claiming workspace's GUID, or <see cref="Guid.Empty" /> while the account is free.</summary>
    [Id(0)]
    public Guid Holder { get; set; }

    /// <summary>When the claim was made, for the operator reading the row.</summary>
    [Id(1)]
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>The one activation per <c>accountID</c> that says which workspace holds it.</summary>
/// <remarks>
///     ⚠ <b>The key is parsed on activation, not trusted.</b> The contract's remarks say why the grain
///     is null-tenant; a tenant-qualified activation of it would be a ledger per tenant, which is the
///     very thing that can't see a collision. <c>Orleans.Multitenant</c> spells a tenant-qualified key
///     <c>{tenant}|metrics-account/…</c>, which <see cref="GrainKeys.Parse" /> refuses, so that mistake
///     throws here instead of answering "free".
/// </remarks>
/// <param name="state">The claim, in the durable tier's null-tenant shard.</param>
/// <param name="clock">When a claim was made.</param>
/// <param name="logger">Where a refused claim is recorded for the operator.</param>
public sealed class MonitorAccountGrain(
    [PersistentState("monitor-account", StorageTiers.Durable)]
    IPersistentState<MonitorAccountState> state,
    IClock clock,
    ILogger<MonitorAccountGrain> logger
)
    : Grain, IMonitorAccountGrain {
    string account = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        var key = this.GetPrimaryKeyString();
        var parsed = GrainKeys.Parse(key);

        if (parsed.TryGetError(out var error) || parsed.GetValueOrThrow().Kind != GrainKeyKind.MetricsAccount) {
            throw new InvalidOperationException(
                $"'{key}' is not a metrics-account grain key. The shape is "
                + $"'{GrainKeys.MetricsAccountPrefix}{{accountId}}', reached WITHOUT ForTenant — docs/plan/06 "
                + "§ Grain keys. A tenant-qualified ledger can't see another tenant's claim. "
                + (error is null ? string.Empty : error.Message)
            );
        }

        account = parsed.GetValueOrThrow().Name;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> ClaimAsync(Guid workspaceId) {
        if (workspaceId == Guid.Empty) {
            throw new ArgumentException(
                "A claim needs the workspace's resolved GUID. Guid.Empty is what a path-parsed ResourceId "
                + "carries before the index answers, and claiming for it would lock the account to nobody.",
                nameof(workspaceId)
            );
        }

        if (state.State.Holder == workspaceId) {
            return true;
        }

        if (state.State.Holder != Guid.Empty) {
            // ⚠ Logged with both GUIDs because this is the only place they meet. The caller hears
            // "no" and nothing else — see IMonitorAccountGrain's remarks.
            logger.LogWarning(
                "Metrics account {Account} is held by workspace {Holder} since {ClaimedAt}; workspace {Workspace} "
                + "folds to the same account and was refused.",
                account,
                state.State.Holder,
                state.State.ClaimedAt,
                workspaceId
            );

            return false;
        }

        state.State.Holder = workspaceId;
        state.State.ClaimedAt = clock.UtcNow;

        try {
            await state.WriteStateAsync();
        } catch {
            // ⚠ Not claimed until it is durable. A write that failed and left Holder set in memory
            // would answer "yes" to IsHeldByAsync on this activation and "free" on the next.
            state.State.Holder = Guid.Empty;
            state.State.ClaimedAt = default;
            throw;
        }

        return true;
    }

    /// <inheritdoc />
    public Task<bool> IsHeldByAsync(Guid workspaceId) =>
        Task.FromResult(workspaceId != Guid.Empty && state.State.Holder == workspaceId);
}
