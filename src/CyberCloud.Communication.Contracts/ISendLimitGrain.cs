namespace CyberCloud.Communication.Contracts;

/// <summary>
///     The per-window counters that stop a runaway loop — docs/plan/17 § The parts that are actually
///     the work: <i>"An SMS loop is a five-figure incident within an hour, and the limit is the only
///     thing between a bug and that invoice."</i>
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Worker · <b>Tier</b> Hot, TTL'd · <b>Key</b> <c>res/{serviceId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>The counters are here and the limits are not, and that split is the design.</b>
///         <see cref="ChannelLimits" /> lives on <see cref="ICommunicationServiceGrain" />, which is
///         durable; this grain is handed them on every call. The reason is what a hot-tier loss
///         costs: if the policy lived here, a <c>FLUSHALL</c> would reset every tenant's cap to
///         nothing-configured, which under <see cref="ChannelLimits.None" /> means <i>zero</i> — the
///         platform would stop sending rather than start overspending, but the failure would be
///         total. With the split, a flush costs at most one window's counters: the caps come back
///         from PostgreSQL and the day starts again at zero, so the worst case is a tenant getting
///         two days of allowance on the day of an incident.
///     </para>
///     <para>
///         ⚠ <b>Hot rather than durable, and docs/plan/05 § Hot names the category directly</b> —
///         <i>"rate-limit counters"</i> is on its list of what the hot tier holds. Making these
///         durable would put a synchronously-replicated write in front of every send, on the path an
///         OTP travels, to protect a figure that is reconstructible: metering's usage ledger is the
///         durable record of what was actually sent, and a reconciliation against it is the backstop
///         if a window's counters are ever in doubt.
///     </para>
///     <para>
///         ⚠ <b>One activation per service serializes every send through it, and that is a feature
///         here.</b> Orleans runs one call at a time per activation, so two concurrent sends cannot
///         both read a counter below the cap and both pass. A design keyed per (service, channel,
///         day) would buy parallelism and cost a key shape <see cref="GrainKeys" /> does not have —
///         the same trade metering considered and declined. If a single service ever needs more send
///         throughput than one activation gives, the answer is a second service resource, which the
///         resource model already supports.
///     </para>
/// </remarks>
[Alias("CyberCloud.Communication.ISendLimitGrain")]
public interface ISendLimitGrain : IGrainWithStringKey {
    /// <summary>
    ///     Claims one message's worth of the window's allowance, or refuses and says why.
    /// </summary>
    /// <param name="channel">Which channel's allowance.</param>
    /// <param name="limits">
    ///     The caps, read from the durable service resource by the caller. ⚠ Passed in rather than
    ///     held, for the reason on this type — and the only caller is <see cref="IMessageGrain" />,
    ///     which reads them from <see cref="ICommunicationServiceGrain" /> in the same operation.
    /// </param>
    /// <param name="estimatedCost">
    ///     What the message is expected to cost, from
    ///     <see cref="ChannelConfiguration.EstimatedUnitCost" />. Settled to the real figure when the
    ///     carrier reports one.
    /// </param>
    /// <returns>
    ///     <para>The claim, to settle or release.</para>
    ///     <para>
    ///         ⚠ <b><see cref="ErrorCode.QuotaExceeded" /> when either cap would be crossed, and the
    ///         message names the limit, the window, the current figure and the request.</b> That
    ///         shape is docs/plan/22 § Quota's rule — <i>"429 naming the meter, the request, the
    ///         current usage and the limit. Never a bare 'quota exceeded'"</i> — and it is what turns
    ///         a refusal into something an on-call engineer can act on at 03:00 without opening a
    ///         dashboard.
    ///     </para>
    /// </returns>
    Task<Result<SpendReservation>> ReserveAsync(ChannelKind channel, ChannelLimits limits, decimal estimatedCost);

    /// <summary>Converts a claim into a settled figure at the price the carrier charged.</summary>
    /// <param name="reservationId">The claim, from <see cref="ReserveAsync" />.</param>
    /// <param name="actualCost">
    ///     What it really cost. ⚠ May be more than the estimate — an international SMS routinely is —
    ///     and settling above the cap is allowed. The alternative is unsending a message that has
    ///     already gone, and the cap's job is to stop the <i>next</i> one, which it then does.
    /// </param>
    /// <returns>
    ///     ⚠ Settling a claim that has already been settled, released, or expired succeeds and
    ///     changes nothing. A carrier receipt arriving twice must not double-count spend.
    /// </returns>
    Task<Result> SettleAsync(Guid reservationId, decimal actualCost);

    /// <summary>Gives a claim back, for a send the carrier refused.</summary>
    /// <param name="reservationId">The claim.</param>
    /// <remarks>
    ///     ⚠ Without this, a channel whose carrier is down burns the day's budget on messages that
    ///     never left — and the tenant's first working send of the day is refused with a limit
    ///     message that is true and useless.
    /// </remarks>
    Task<Result> ReleaseAsync(Guid reservationId);

    /// <summary>What one channel has spent and sent in the current window.</summary>
    /// <param name="channel">The channel.</param>
    Task<Result<ChannelSpend>> ReadAsync(ChannelKind channel);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();

    /// <summary>
    ///     How long an unsettled claim holds its slice before it is given back.
    /// </summary>
    /// <remarks>
    ///     ⚠ Long enough that a slow carrier is not treated as a dead one, short enough that a silo
    ///     that died mid-send does not hold budget for the rest of the day. Five minutes is above
    ///     every carrier's own request timeout and far below the window.
    /// </remarks>
    public static TimeSpan ReservationLease => TimeSpan.FromMinutes(5);
}
