using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Metering;

/// <summary>
///     The <see cref="IUsageEmitter" /> M1 ships: a tenant-qualified call to the subscription's
///     <see cref="IUsageRollupGrain" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read <see cref="IUsageEmitter" />'s remarks for why this is a grain call and not the
///         NATS hop docs/plan/22 § The pipeline draws.</b> In short: docs/plan/04 § Streams' own rule
///         is that a thing needing an answer is a grain call, an emitter that cannot learn whether
///         the record landed cannot retry, and the stream's two contributions — buffering across a
///         consumer outage and fan-out to a second consumer — have no consumer to serve in M1.
///     </para>
///     <para>
///         ⚠ <b>CC1006 applies here and is satisfied rather than suppressed.</b> This class holds an
///         <c>IGrainFactory</c> and is not a grain, so <c>Orleans.Multitenant</c>'s call filter never
///         sees it and it must establish the tenant itself. Every reference below goes through
///         <c>ForTenant</c>, with the tenant taken from the record's own <c>TenantId</c> — which is
///         also why a record whose tenant does not match the rollup's is refused by the grain
///         (<c>UsageRollupGrain.Validate</c>) rather than trusted here.
///     </para>
///     <para>
///         <b>A batch is split by (tenant, subscription) and each group is one call.</b> The sampler
///         produces one group; a future event-based batcher might not, and a per-record call would
///         turn a hundred requests into a hundred grain calls into one activation that serialises
///         them anyway.
///     </para>
/// </remarks>
public sealed class GrainUsageEmitter(IGrainFactory grains) : IUsageEmitter {
    /// <inheritdoc />
    public async Task<Result<UsageIngestReceipt>> EmitAsync(
        UsageEvent usage,
        CancellationToken cancellationToken = default
    ) {
        if (usage is null) {
            return Result<UsageIngestReceipt>.Failure(ErrorCode.InvalidRequestBody, "An emission needs a record.");
        }

        var many = await EmitAsync([usage], cancellationToken);

        if (many.TryGetError(out var error)) {
            return Result<UsageIngestReceipt>.Failure(error);
        }

        var receipts = many.GetValueOrThrow();
        return receipts.Length == 1
            ? Result<UsageIngestReceipt>.Success(receipts[0])
            : Result<UsageIngestReceipt>.Failure(
                ErrorCode.InternalError,
                $"One record produced {receipts.Length} receipts."
            );
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<UsageIngestReceipt>>> EmitAsync(
        ImmutableArray<UsageEvent> usage,
        CancellationToken cancellationToken = default
    ) {
        if (usage.IsDefaultOrEmpty) {
            return Result<ImmutableArray<UsageIngestReceipt>>.Success([]);
        }

        // Receipts come back in the caller's order, whatever order the groups went in — a caller
        // matching receipt i to record i is the obvious reading and would otherwise be wrong.
        var byKey = new Dictionary<string, UsageIngestReceipt>(StringComparer.Ordinal);

        foreach (var group in usage.GroupBy(x => (x.TenantId, x.SubscriptionId))) {
            if (group.Key.TenantId == Guid.Empty || group.Key.SubscriptionId == Guid.Empty) {
                return Result<ImmutableArray<UsageIngestReceipt>>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "A usage record names a tenant and a subscription; one of them is empty. An "
                    + "unattributed record cannot be billed."
                );
            }

            var rollup = grains
                .ForTenant(group.Key.TenantId.ToString("D", CultureInfo.InvariantCulture))
                .GetGrain<IUsageRollupGrain>(GrainKeys.Subscription(group.Key.SubscriptionId));

            var ingested = await rollup.IngestAsync([.. group]);

            if (ingested.TryGetError(out var error)) {
                return Result<ImmutableArray<UsageIngestReceipt>>.Failure(error);
            }

            foreach (var receipt in ingested.GetValueOrThrow()) {
                byKey[receipt.IdempotencyKey] = receipt;
            }
        }

        var ordered = ImmutableArray.CreateBuilder<UsageIngestReceipt>(usage.Length);
        foreach (var record in usage) {
            ordered.Add(
                byKey.TryGetValue(record.IdempotencyKey, out var receipt)
                    ? receipt
                    : new(UsageIngestOutcome.Unknown, record.IdempotencyKey)
            );
        }

        return Result<ImmutableArray<UsageIngestReceipt>>.Success(ordered.DrainToImmutable());
    }
}
