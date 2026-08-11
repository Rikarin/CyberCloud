using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="IQuotaGrain" /> — Coordinator, Durable, key <c>sub/{subscriptionId:N}</c>.
/// </summary>
public sealed class QuotaGrain(
    [PersistentState("quota", StorageTiers.Durable)] IPersistentState<QuotaState> state,
    IClock clock)
    : Grain, IQuotaGrain
{
    /// <summary>
    ///     How long a reservation lives without being committed or released.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the mechanism behind docs/plan/06 § Quota's "expires on its own if the
    ///     operation grain dies".</b> It is deliberately longer than the index claim's five minutes:
    ///     an index claim guards a <i>name</i>, which a user retries within seconds, while a quota
    ///     lease guards capacity for a create that docs/plan/08 gives up to 60 minutes. Expiring
    ///     quota out from under a live provisioning run would let a second create succeed and then
    ///     put the subscription over its limit when the first one commits.
    /// </remarks>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(60);

    /// <summary>
    ///     The defaults docs/plan/06 § Quota calls "defaults per subscription tier".
    /// </summary>
    /// <remarks>
    ///     Flat rather than tiered, because subscription tiers are docs/plan/22's and do not exist
    ///     yet. A per-tenant override is <see cref="IQuotaGrain.SetLimitAsync" />, which is what the
    ///     <c>CyberCloud.Platform/quotaOverrides</c> resource will write through.
    /// </remarks>
    static readonly Dictionary<QuotaMeter, decimal> Defaults = new()
    {
        [QuotaMeter.Vcpu] = 100m,
        [QuotaMeter.MemoryGb] = 400m,
        [QuotaMeter.StorageGb] = 10_000m,
        [QuotaMeter.PublicIps] = 20m,
        [QuotaMeter.Clusters] = 5m,
        [QuotaMeter.Resources] = 1_000m,
    };

    Guid subscriptionId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _ = TenancyGrainKeys.TenantOf(this);
        subscriptionId = TenancyGrainKeys.Decode(this, GrainKeyKind.Subscription).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<QuotaLease>> TryReserveAsync(QuotaMeter meter, decimal amount, Guid operationId)
    {
        if (meter == QuotaMeter.Unknown)
        {
            return Result<QuotaLease>.Failure(ErrorCode.InvalidRequestBody, "A reservation needs a meter.");
        }

        if (amount <= 0)
        {
            return Result<QuotaLease>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A reservation must be positive; {amount} is not. Giving quota back is "
                + "ReturnAsync, which is a different operation with a different audit meaning.");
        }

        if (operationId == Guid.Empty)
        {
            return Result<QuotaLease>.Failure(
                ErrorCode.InvalidRequestBody,
                "A lease is owned by an operation — docs/plan/06 § Quota. Without an owner there is "
                + "nothing to release it when the operation fails, which is the whole difference "
                + "between a reservation and a counter increment.");
        }

        var now = clock.UtcNow;
        var expired = Sweep(now);

        var usage = UsageOf(meter);
        if (usage.Committed + usage.Reserved + amount > usage.Limit)
        {
            if (expired)
            {
                await state.WriteStateAsync();
            }

            return Result<QuotaLease>.Failure(
                ErrorCode.QuotaExceeded,
                $"Subscription {subscriptionId:D} would exceed its {meter} quota: "
                + $"{usage.Committed} committed + {usage.Reserved} reserved + {amount} requested "
                + $"> {usage.Limit}.");
        }

        var lease = new QuotaLease
        {
            LeaseId = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            Meter = meter,
            Amount = amount,
            OperationId = operationId,
            ReservedAt = now,
            ExpiresAt = now + LeaseDuration,
        };

        state.State.Leases[lease.LeaseId] = lease;
        await state.WriteStateAsync();

        return Result<QuotaLease>.Success(lease);
    }

    /// <inheritdoc />
    public async Task<Result<QuotaUsage>> CommitAsync(Guid leaseId)
    {
        var now = clock.UtcNow;
        Sweep(now);

        if (!state.State.Leases.TryGetValue(leaseId, out var lease))
        {
            return Result<QuotaUsage>.Failure(
                ErrorCode.Conflict,
                $"Lease {leaseId:D} is not live — it was released, already committed, or it "
                + "expired. A committed lease is not re-committable and an expired one must be "
                + "re-reserved, because the quota it held has been given to somebody else.");
        }

        state.State.Leases.Remove(leaseId);
        state.State.Committed[lease.Meter] = Committed(lease.Meter) + lease.Amount;

        await state.WriteStateAsync();
        return Result<QuotaUsage>.Success(UsageOf(lease.Meter));
    }

    /// <inheritdoc />
    public async Task<Result<QuotaUsage>> ReleaseAsync(Guid leaseId)
    {
        var now = clock.UtcNow;
        Sweep(now);

        if (!state.State.Leases.TryGetValue(leaseId, out var lease))
        {
            // Releasing a lease that has already gone is a no-op rather than an error: the failure
            // path is re-driven from a reminder and must be safe to run twice. There is no meter to
            // report on — the lease that named one is gone — so the reply carries Unknown, and a
            // caller that wants figures asks GetUsageAsync for the meter it cares about.
            return Result<QuotaUsage>.Success(UsageOf(QuotaMeter.Unknown));
        }

        state.State.Leases.Remove(leaseId);
        await state.WriteStateAsync();

        return Result<QuotaUsage>.Success(UsageOf(lease.Meter));
    }

    /// <inheritdoc />
    public async Task<Result<QuotaUsage>> ReturnAsync(QuotaMeter meter, decimal amount)
    {
        if (amount <= 0)
        {
            return Result<QuotaUsage>.Failure(
                ErrorCode.InvalidRequestBody, $"A return must be positive; {amount} is not.");
        }

        var committed = Committed(meter);
        if (amount > committed)
        {
            return Result<QuotaUsage>.Failure(
                ErrorCode.Conflict,
                $"Returning {amount} of {meter} would take committed usage below zero "
                + $"({committed} is committed). A negative meter is a billing figure nobody can "
                + "explain, so it is refused rather than clamped.");
        }

        state.State.Committed[meter] = committed - amount;
        await state.WriteStateAsync();

        return Result<QuotaUsage>.Success(UsageOf(meter));
    }

    /// <inheritdoc />
    public async Task<Result<QuotaUsage>> SetLimitAsync(QuotaMeter meter, decimal limit)
    {
        if (meter == QuotaMeter.Unknown)
        {
            return Result<QuotaUsage>.Failure(ErrorCode.InvalidRequestBody, "A limit needs a meter.");
        }

        if (limit < 0)
        {
            return Result<QuotaUsage>.Failure(
                ErrorCode.InvalidRequestBody, $"A limit cannot be negative; {limit} is.");
        }

        state.State.Limits[meter] = limit;
        await state.WriteStateAsync();

        return Result<QuotaUsage>.Success(UsageOf(meter));
    }

    /// <inheritdoc />
    public async Task<Result<QuotaUsage>> GetUsageAsync(QuotaMeter meter)
    {
        if (Sweep(clock.UtcNow))
        {
            await state.WriteStateAsync();
        }

        return Result<QuotaUsage>.Success(UsageOf(meter));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<QuotaLease>>> ListLeasesAsync()
    {
        if (Sweep(clock.UtcNow))
        {
            await state.WriteStateAsync();
        }

        return Result<IReadOnlyList<QuotaLease>>.Success(
            [.. state.State.Leases.Values.OrderBy(x => x.ReservedAt)]);
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Drops expired leases. Returns whether anything went.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>On read, not on a timer.</b> The failure being modelled is "the operation grain
    ///     dies", and a timer that has to fire for the quota to come back is a timer whose silo can
    ///     die in the same incident. Sweeping whenever the grain is asked anything means an
    ///     abandoned lease frees its quota the moment somebody needs it, even if this grain has been
    ///     deactivated for the whole lease.
    /// </remarks>
    bool Sweep(DateTimeOffset now)
    {
        var dead = state.State.Leases
            .Where(x => x.Value.ExpiresAt <= now)
            .Select(x => x.Key)
            .ToList();

        foreach (var leaseId in dead)
        {
            state.State.Leases.Remove(leaseId);
        }

        return dead.Count > 0;
    }

    decimal Committed(QuotaMeter meter) =>
        state.State.Committed.TryGetValue(meter, out var value) ? value : 0m;

    QuotaUsage UsageOf(QuotaMeter meter) => new()
    {
        Meter = meter,
        Committed = Committed(meter),
        Reserved = state.State.Leases.Values.Where(x => x.Meter == meter).Sum(x => x.Amount),
        Limit = state.State.Limits.TryGetValue(meter, out var limit)
            ? limit
            : Defaults.TryGetValue(meter, out var fallback) ? fallback : 0m,
    };
}
