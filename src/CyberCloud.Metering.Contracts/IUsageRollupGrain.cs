using System.Collections.Immutable;

namespace CyberCloud.Metering.Contracts;

/// <summary>
///     The rollup worker of docs/plan/22 § The pipeline: dedup by idempotency key, then the hourly
///     aggregate.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Coordinator · <b>Tier</b> Durable · <b>Key</b> <c>sub/{subscriptionId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>Durable, and this one is not a judgement call.</b> Its state is raw usage for hours
///         that have not closed yet, and the set of idempotency keys already seen. Neither can be
///         rebuilt: the sample window has passed, the resource may since have been deleted, and
///         "usage that was never recorded cannot be recovered" (docs/plan/22 § Effort) is the whole
///         reason M1 ships meters before it ships invoices. Losing the key set is worse than losing
///         the records, because it turns every redelivery in the retention window into double
///         billing. It is in <c>durable-grains.txt</c> with that reason.
///     </para>
///     <para>
///         <b>Why dedup is here and not only at the ClickHouse insert.</b> docs/plan/22 § The
///         pipeline puts it "at the ClickHouse insert, using a <c>ReplacingMergeTree</c> keyed on
///         it". That remains true and <see cref="IUsageSink" /> owes it. It cannot be the <i>only</i>
///         dedup, for two reasons the document does not reach: a <c>ReplacingMergeTree</c> collapses
///         on merge rather than on insert, so between the insert and the merge the table genuinely
///         holds both copies; and the durable ledger — the record of account — is upstream of
///         ClickHouse, so a duplicate that ClickHouse would eventually collapse would already have
///         been appended to an append-only ledger, where nothing can remove it. Deduplicating before
///         the ledger is what makes the ledger's immutability affordable.
///     </para>
///     <para>
///         ⚠ <b>The seen-key set is bounded by a retention horizon and that horizon is a real
///         limit.</b> Keys older than <c>UsageRollupGrain.DedupRetention</c> are pruned, so a
///         redelivery older than that would be accepted as new. The horizon is set to match
///         docs/plan/22 § The pipeline's own "JetStream, durable, 7-day retention": a message the
///         transport cannot still be holding cannot still be redelivered. Second defence: an hour
///         that has already been closed is refused outright (<see cref="CloseHourAsync" />), so a
///         late record for a settled hour becomes a ledger correction with a reason rather than a
///         silent second count.
///     </para>
/// </remarks>
[Alias("CyberCloud.Metering.IUsageRollupGrain")]
public interface IUsageRollupGrain : IGrainWithStringKey {
    /// <summary>Takes one record.</summary>
    /// <param name="usage">
    ///     The record. ⚠ Refused with <see cref="ErrorCode.InvalidRequestBody" /> if its
    ///     idempotency key is not the one its own contents produce — see
    ///     <see cref="UsageEvent.IsKeyConsistent" />.
    /// </param>
    /// <returns>
    ///     <see cref="UsageIngestOutcome.Accepted" /> or <see cref="UsageIngestOutcome.Duplicate" />,
    ///     both success, or a failure the caller must retry.
    /// </returns>
    Task<Result<UsageIngestReceipt>> IngestAsync(UsageEvent usage);

    /// <summary>Takes a batch — one sampler pass.</summary>
    /// <param name="usage">The records. Each is judged on its own key.</param>
    /// <returns>One receipt per record, in order.</returns>
    Task<Result<ImmutableArray<UsageIngestReceipt>>> IngestAsync(ImmutableArray<UsageEvent> usage);

    /// <summary>
    ///     Aggregates one hour, writes it to the sink and appends it to the ledger.
    /// </summary>
    /// <param name="hourStart">
    ///     The hour, snapped by <see cref="UsageWindow.HourAt" />. An unsnapped instant is refused
    ///     rather than snapped for you — an hour boundary a caller guessed at is a bug worth seeing.
    /// </param>
    /// <returns>
    ///     What was written, or a failure.
    ///     <para>
    ///         ⚠ <b>Order matters and it is: ledger, then sink, then drop.</b> The ledger is the
    ///         durable record of account, so it commits first. The sink is the analytical copy. Only
    ///         when both have succeeded are the hour's raw records dropped from grain state — a sink
    ///         failure leaves the hour open, the records in place and the next reminder retrying,
    ///         which is what "never lost" costs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An hour already closed is refused</b>, with <see cref="ErrorCode.Conflict" />.
    ///         Re-closing it would append the same aggregate to an append-only ledger a second time.
    ///     </para>
    /// </returns>
    Task<Result<ImmutableArray<UsageAggregate>>> CloseHourAsync(DateTimeOffset hourStart);

    /// <summary>
    ///     Closes every hour that has fully elapsed. What the hourly reminder calls
    ///     (docs/plan/04 § Reminders, "Rollups — metering aggregation windows").
    /// </summary>
    /// <returns>
    ///     Every aggregate written across every hour closed. ⚠ The <i>current</i> hour is never
    ///     closed — a five-minute sample for 13:55 arrives at 14:00, and an hour closed the instant
    ///     it ended would settle before its last sample landed.
    /// </returns>
    Task<Result<ImmutableArray<UsageAggregate>>> CloseElapsedHoursAsync();

    /// <summary>Starts the hourly close reminder.</summary>
    Task<Result> StartAsync();

    /// <summary>Stops it.</summary>
    Task<Result> StopAsync();

    /// <summary>The raw records still held — the hours that have not closed.</summary>
    /// <remarks>Diagnostic and for tests. Not a query anything bills from; that is the ledger.</remarks>
    Task<Result<ImmutableArray<UsageEvent>>> ListPendingAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
