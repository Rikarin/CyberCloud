using System.Collections.Immutable;

namespace CyberCloud.Metering.Contracts;

/// <summary>
///     Where a usage record goes the moment it exists — the emission seam a provider calls.
/// </summary>
/// <remarks>
///     <para>
///         This is the entry point of docs/plan/22 § The pipeline, and it is the only one. Both kinds
///         of meter arrive through it: the sampler calls it for state-based records and a provider
///         calls it for event-based ones. Nothing writes to the rollup, the ledger or the sink
///         directly.
///     </para>
///     <para>
///         ⚠ <b>The transport is a grain call, and docs/plan/22 § The pipeline draws a NATS hop here.</b>
///         The document routes <c>UsageEvent</c> through <c>cc.{tenant}.usage.{meter}</c> (JetStream,
///         durable, 7-day retention) to a rollup worker. What ships is a direct call to
///         <c>IUsageRollupGrain</c>, and the reasoning is worth stating because it is a deviation:
///     </para>
///     <list type="bullet">
///         <item>
///             docs/plan/04 § Streams' own rule is <i>"a stream is for fan-out of facts, never for
///             issuing commands. A command is a grain call, because a grain call has a result and a
///             stream does not, and 'did it work' is not a question you want to answer by correlating
///             a second stream."</i> An emitter must know whether the record landed — otherwise it
///             cannot retry, and "never lost" reduces to hope. That makes this a command by
///             docs/plan/04's own test.
///         </item>
///         <item>
///             The stream's contribution is <b>buffering across a rollup outage</b> and <b>fan-out to
///             a second consumer</b>. Neither exists in M1: there is one consumer, and the rollup
///             grain is durable, so a record that lands is already as safe as JetStream would make it.
///         </item>
///         <item>
///             ⚠ <b>What is owed.</b> When a second consumer appears — the near-real-time cost view of
///             docs/plan/22 § Cost visibility is the obvious one — this seam grows a JetStream
///             implementation and the grain call becomes one subscriber. Nothing above this interface
///             changes when it does, which is why the seam is here rather than the call being inlined.
///             Delivery stays at-least-once either way (a retried grain call is a redelivery too), so
///             <see cref="UsageEvent.IdempotencyKey" /> is the answer in both shapes.
///         </item>
///     </list>
/// </remarks>
public interface IUsageEmitter {
    /// <summary>Emits one record.</summary>
    /// <param name="usage">
    ///     The record, built by <see cref="UsageEvent.ForSample" /> or
    ///     <see cref="UsageEvent.ForEvent" /> so it carries a key consistent with its contents.
    /// </param>
    /// <param name="cancellationToken">Cancels the emission.</param>
    /// <returns>
    ///     What the rollup did — <see cref="UsageIngestOutcome.Accepted" /> or
    ///     <see cref="UsageIngestOutcome.Duplicate" />, both success — or a failure the caller must
    ///     retry. ⚠ A failure means the record is <b>not</b> recorded; dropping it here is the one
    ///     way to lose usage that cannot be recovered.
    /// </returns>
    Task<Result<UsageIngestReceipt>> EmitAsync(UsageEvent usage, CancellationToken cancellationToken = default);

    /// <summary>Emits a batch, which is what one sampler pass produces.</summary>
    /// <param name="usage">The records. May be empty.</param>
    /// <param name="cancellationToken">Cancels the emission.</param>
    /// <returns>One receipt per record, in order, or the first failure.</returns>
    Task<Result<ImmutableArray<UsageIngestReceipt>>> EmitAsync(
        ImmutableArray<UsageEvent> usage,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     What exists in a subscription — the sampler's only input.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a view of the resource graph and it must never become a view of a metrics
///         pipeline.</b> docs/plan/22 § Two kinds of meter: <i>"State-based meters must be derived
///         from the platform's own record of the resource, not from Kubernetes metrics. A stopped VM
///         still has a disk; a <c>Deployment</c> scaled to zero still has a
///         <c>PersistentVolumeClaim</c>. Metrics know about running pods; the resource graph knows
///         what exists. Getting this backwards under-bills storage."</i>
///     </para>
///     <para>
///         The shape enforces as much of that as a type can. <see cref="MeteredResource.Quantities" />
///         is in <see cref="QuotaMeter" /> units — the units the write path <i>admitted</i> the
///         resource in, which by construction describe what was provisioned rather than what is
///         running. There is no replica count, no CPU utilisation and no pod phase on
///         <see cref="MeteredResource" />; the one field that mentions running-ness,
///         <see cref="MeteredResource.RunState" />, is documented as an output and is asserted to
///         make no difference.
///     </para>
///     <para>
///         ⚠ <b>What the real implementation owes.</b> There is none in M1. The intended source is
///         the per-tenant resource-graph projection of docs/plan/08 § The resource-graph projection —
///         the ClickHouse table a projector fills from the <c>resource-changed</c> stream — filtered
///         to one subscription and joined to the committed quota draws the registry declared. Until
///         that projector exists, a host registers whatever it has and the default refuses.
///     </para>
/// </remarks>
public interface IMeteredResourceSource {
    /// <summary>Everything that exists in one subscription, right now.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The resources, or a failure.
    ///     <para>
    ///         ⚠ <b>An empty array and a failure are very different and an implementation must not
    ///         confuse them.</b> Empty means "this subscription genuinely holds nothing" and produces
    ///         no usage, correctly. A failure means "we could not find out", and the sampler must
    ///         skip the window and retry rather than record a zero — a zero written for a window that
    ///         had usage is the one loss docs/plan/22's opening property forbids, and it is
    ///         indistinguishable from an idle subscription afterwards.
    ///     </para>
    /// </returns>
    Task<Result<ImmutableArray<MeteredResource>>> ListAsync(
        Guid tenantId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Where the rollup's output goes — docs/plan/22 § The pipeline's <c>usage_raw</c> and
///     <c>usage_hourly</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ClickHouse is the destination and nothing in this repository writes to one.</b> This
///         is the seam it attaches to, the same split docs/plan/08 § The resource-graph projection
///         gets through <c>IResourceChangedSink</c>: what is implemented is the <i>production</i> of
///         correct rows, so landing a real sink is adding a consumer rather than changing the
///         pipeline.
///     </para>
///     <para>
///         <b>What a ClickHouse implementation owes, precisely:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b><c>usage_raw</c> as a <c>ReplacingMergeTree</c> keyed on
///             <see cref="UsageEvent.IdempotencyKey" /></b> — docs/plan/22 § The pipeline names both
///             the engine and the key. That is a <i>second</i> dedup behind the rollup grain's, not a
///             replacement for it: the grain's is synchronous and authoritative for what reaches the
///             ledger, and ClickHouse's collapses anything that reached the table by another route
///             (a backfill, a replay, a second writer during a migration).
///         </item>
///         <item>
///             <b>Idempotent inserts.</b> <c>ReplacingMergeTree</c> collapses on merge, not on
///             insert, so a query that must not double-count reads <c>FINAL</c> or aggregates with
///             <c>argMax</c>. An implementation that inserts and assumes deduplication has happened
///             is wrong for an unbounded and unpredictable interval.
///         </item>
///         <item>
///             <b>All-or-nothing per batch, and a failure that is honestly a failure.</b>
///             <see cref="Result" /> here is load-bearing: <c>UsageRollupGrain</c> does not close an
///             hour whose write failed, so the raw records stay in durable grain state and the next
///             reminder retries. A sink that swallowed an error would strand the pipeline in a state
///             where the grain believes the data is safe and it is not.
///         </item>
///         <item>
///             <b>Per-tenant separation.</b> docs/plan/08 § The resource-graph projection puts the
///             projection in a per-tenant table; usage is at least as sensitive and gets the same
///             treatment. A single table keyed by tenant id is not equivalent — the isolation suite
///             tests reachability, not filtering.
///         </item>
///         <item>
///             <b>Retention and ordering.</b> <c>usage_raw</c> keeps enough to reconstruct
///             <c>usage_hourly</c> for the dispute window; the <c>ORDER BY</c> is
///             <c>(subscriptionId, meter, windowStart, resourceId)</c>, which is the shape every
///             cost query in docs/plan/22 § Cost visibility scans.
///         </item>
///         <item>
///             <b>Nothing derives a figure the ledger disagrees with.</b> The durable ledger is the
///             record of account (docs/plan/22 § The pipeline: append-only, durable-tier);
///             ClickHouse is the analytical copy. If they diverge, the ledger is right and the
///             divergence is an incident.
///         </item>
///     </list>
/// </remarks>
public interface IUsageSink {
    /// <summary>Writes raw records — <c>usage_raw</c>.</summary>
    /// <param name="usage">The batch. Already deduplicated by the rollup; may still redeliver.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or a failure that keeps the hour open and the records in grain state.</returns>
    Task<Result> WriteRawAsync(ImmutableArray<UsageEvent> usage, CancellationToken cancellationToken = default);

    /// <summary>Writes hourly aggregates — <c>usage_hourly</c>.</summary>
    /// <param name="aggregates">The batch, one row per (resource, meter, hour).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or a failure that keeps the hour open.</returns>
    Task<Result> WriteHourlyAsync(
        ImmutableArray<UsageAggregate> aggregates,
        CancellationToken cancellationToken = default
    );
}
