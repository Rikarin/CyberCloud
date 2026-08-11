using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace CyberCloud.Metering.Sinks;

/// <summary>
///     The <see cref="IUsageSink" /> a silo with no ClickHouse registers: it refuses.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It refuses rather than logging and returning success, and the difference matters more
///         here than for any other default in the tree.</b> <c>LoggingResourceChangedSink</c>
///         (docs/plan/08 § The resource-graph projection) can afford to log and shrug because the
///         projection is a list view that will catch up on the next change. Usage does not catch up:
///         the window has passed, the resource may be gone, and docs/plan/22 § Effort is explicit
///         that "usage that was never recorded cannot be recovered".
///     </para>
///     <para>
///         Refusing is also <i>safe</i>, which is what makes it the right default rather than merely
///         the loud one. <c>UsageRollupGrain.CloseHourAsync</c> commits to the durable ledger before
///         it calls the sink, so a refusal here leaves the record of account intact, the hour open
///         and the raw records in durable grain state; the next reminder retries. A host that runs
///         for a week with no sink loses no usage — it accumulates raw records and settles them the
///         moment a real sink is registered. A sink that had silently succeeded would have thrown
///         them away.
///     </para>
/// </remarks>
public sealed class UnavailableUsageSink(ILogger<UnavailableUsageSink> logger) : IUsageSink {
    /// <inheritdoc />
    public Task<Result> WriteRawAsync(
        ImmutableArray<UsageEvent> usage,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refuse("usage_raw", usage.IsDefault ? 0 : usage.Length));

    /// <inheritdoc />
    public Task<Result> WriteHourlyAsync(
        ImmutableArray<UsageAggregate> aggregates,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refuse("usage_hourly", aggregates.IsDefault ? 0 : aggregates.Length));

    Result Refuse(string table, int rows) {
        logger.LogWarning(
            "No usage sink is registered, so {Rows} row(s) for {Table} were not written. The records "
            + "are safe — the ledger has them and the rollup is holding the raw copies — and the hour "
            + "stays open until a sink takes them. Register an IUsageSink; see its remarks for what a "
            + "ClickHouse implementation owes.",
            rows,
            table
        );

        return Result.Failure(
            ErrorCode.InternalError,
            $"No IUsageSink is registered, so {rows} row(s) destined for {table} cannot be written. "
            + "docs/plan/22 § The pipeline routes the rollup to ClickHouse; nothing in this build "
            + "does. The hour stays open and the records stay in durable grain state until one does."
        );
    }
}

/// <summary>
///     An <see cref="IUsageSink" /> that keeps everything in memory. For tests, and for a
///     single-silo development host.
/// </summary>
/// <remarks>
///     ⚠ <b>Not a production sink and not a stand-in for one.</b> It holds rows in a process that
///     will restart, and it has none of the properties <see cref="IUsageSink" /> says a ClickHouse
///     implementation owes — no <c>ReplacingMergeTree</c>, no per-tenant separation, no retention.
///     What it is for is proving that the rollup produces the right rows in the right order, which
///     is the half of the pipeline this repository can actually test.
/// </remarks>
public sealed class InMemoryUsageSink : IUsageSink {
    readonly ConcurrentQueue<UsageAggregate> hourly = new();
    readonly ConcurrentQueue<UsageEvent> raw = new();

    /// <summary>Every raw row written, in write order.</summary>
    public IReadOnlyCollection<UsageEvent> Raw => raw;

    /// <summary>Every hourly row written, in write order.</summary>
    public IReadOnlyCollection<UsageAggregate> Hourly => hourly;

    /// <summary>
    ///     When set, every write fails — so a test can assert that a sink outage leaves the hour open
    ///     and the records in durable state rather than losing them.
    /// </summary>
    public bool Fail { get; set; }

    /// <summary>Forgets everything and stops failing.</summary>
    public void Reset() {
        raw.Clear();
        hourly.Clear();
        Fail = false;
    }

    /// <inheritdoc />
    public Task<Result> WriteRawAsync(
        ImmutableArray<UsageEvent> usage,
        CancellationToken cancellationToken = default
    ) {
        if (Fail) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the usage sink is down"));
        }

        if (!usage.IsDefaultOrEmpty) {
            foreach (var record in usage) {
                raw.Enqueue(record);
            }
        }

        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> WriteHourlyAsync(
        ImmutableArray<UsageAggregate> aggregates,
        CancellationToken cancellationToken = default
    ) {
        if (Fail) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the usage sink is down"));
        }

        if (!aggregates.IsDefaultOrEmpty) {
            foreach (var aggregate in aggregates) {
                hourly.Enqueue(aggregate);
            }
        }

        return Task.FromResult(Result.Success);
    }
}

/// <summary>
///     The <see cref="IMeteredResourceSource" /> a silo with no resource-graph projection registers:
///     it refuses.
/// </summary>
/// <remarks>
///     ⚠ <b>Refuses rather than reporting an empty subscription, for the reason
///     <see cref="IMeteredResourceSource.ListAsync" /> gives.</b> An empty list is a claim — "this
///     subscription holds nothing" — and it is indistinguishable afterwards from a window nobody
///     could read. <c>UsageSamplerGrain.SampleAsync</c> propagates the failure and the window is
///     retried on the next reminder; the alternative writes a permanent zero. The same argument as
///     <c>UnavailableClusterObjectInventory</c> in <c>CyberCloud.ResourceManager</c>: the plausible
///     default is the dangerous one.
/// </remarks>
public sealed class UnavailableMeteredResourceSource : IMeteredResourceSource {
    /// <inheritdoc />
    public Task<Result<ImmutableArray<MeteredResource>>> ListAsync(
        Guid tenantId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<MeteredResource>>.Failure(
                ErrorCode.InternalError,
                $"No IMeteredResourceSource is registered, so what subscription {subscriptionId:D} "
                + "holds is unknown. docs/plan/22 § Two kinds of meter derives state-based meters "
                + "from the platform's own record of the resource — the resource-graph projection of "
                + "docs/plan/08 § The resource-graph projection — and nothing in this build fills "
                + "one. Reporting an empty subscription instead would write a permanent zero for a "
                + "window that may have had usage."
            )
        );
}
