namespace CyberCloud.Metering.Contracts;

/// <summary>
///     The per-subscription, reminder-driven sampler of docs/plan/22 § Two kinds of meter.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Worker · <b>Tier</b> Hot · <b>Key</b> <c>sub/{subscriptionId:N}</c>,
///         tenant-qualified — the same key string as <c>ISubscriptionGrain</c>, <c>IQuotaGrain</c>,
///         <c>IUsageRollupGrain</c> and <c>IUsageLedgerGrain</c>, on a different grain type. Orleans
///         addresses an activation by (grain type, key), so these are distinct grains that name the
///         same subscription; a second key shape would be a spelling without an identity.
///     </para>
///     <para>
///         ⚠ <b>Hot, and it is genuinely rebuildable — which is not obvious and is worth the
///         paragraph.</b> This grain's state is a cursor: which window it last sampled. Losing it
///         makes the sampler re-sample the current window, which produces records with the <i>same</i>
///         idempotency keys, which the rollup collapses. So the loss costs one redundant pass and
///         nothing else, and docs/plan/05 § Choosing a tier's test — "if the honest answer is 'this
///         can be rebuilt', the tier is Hot" — answers cleanly. The durable half of this pipeline is
///         <c>IUsageRollupGrain</c> and <c>IUsageLedgerGrain</c>, which hold records that cannot be
///         reconstructed once the window has passed.
///     </para>
///     <para>
///         <b>One event per (resource, meter, window), and the window is snapped</b>
///         (<see cref="UsageWindow.SampleAt" />). Those two facts together are docs/plan/22's "a
///         sampler that runs twice produces one record": the second run computes the same window,
///         therefore the same keys, therefore no new records. It is not a lock, not a cursor check
///         and not a lease — it is arithmetic, which is why it survives a silo restart, a duplicate
///         reminder and two silos racing.
///     </para>
/// </remarks>
[Alias("CyberCloud.Metering.IUsageSamplerGrain")]
public interface IUsageSamplerGrain : IGrainWithStringKey {
    /// <summary>
    ///     Starts sampling this subscription, and keeps the reminder registered.
    /// </summary>
    /// <returns>Success. Safe to call repeatedly — the reminder is registered or updated, never doubled.</returns>
    /// <remarks>
    ///     Something has to call this once per subscription. In M1 that is the host or an operator;
    ///     the intended caller is subscription creation (docs/plan/06 § The hierarchy), which is not
    ///     this assembly's to change. ⚠ A subscription nobody started is a subscription with no
    ///     state-based usage at all, and it is silent — which is why
    ///     <see cref="IsRunningAsync" /> exists for a health check to fail on.
    /// </remarks>
    Task<Result> StartAsync();

    /// <summary>Stops sampling. The subscription is closed, or metering is being drained.</summary>
    /// <returns>Success, whether or not it was running.</returns>
    Task<Result> StopAsync();

    /// <summary>Whether the reminder is registered.</summary>
    Task<Result<bool>> IsRunningAsync();

    /// <summary>
    ///     Samples the window containing now, once.
    /// </summary>
    /// <returns>
    ///     What the pass did, or a failure. ⚠ A failure from <see cref="IMeteredResourceSource" />
    ///     propagates rather than being swallowed into an empty pass: recording zero usage for a
    ///     window we could not read is a loss that looks exactly like an idle subscription
    ///     afterwards, and docs/plan/22's opening property forbids it.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Public because the reminder is not the only caller, and that is deliberate.</b> The
    ///     reminder is a five-minute wakeup; a test drives this directly, and an operator draining a
    ///     subscription before closing it calls it once more. Running it twice in one window is
    ///     safe by construction — see the remarks on this interface.
    /// </remarks>
    Task<Result<UsageSampleReport>> SampleAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
