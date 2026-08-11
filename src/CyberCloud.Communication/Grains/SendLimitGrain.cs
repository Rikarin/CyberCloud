using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Globalization;

namespace CyberCloud.Communication.Grains;

/// <summary>
///     <see cref="ISendLimitGrain" /> — Worker, Hot, key <c>res/{serviceId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="ISendLimitGrain" /> first: the caps arrive as an argument and are never
///     stored, which is what bounds a hot-tier loss to one window.</b>
/// </remarks>
public sealed class SendLimitGrain(
    [PersistentState("send-limit", StorageTiers.Hot)] IPersistentState<SendLimitState> state,
    IClock clock
)
    : Grain, ISendLimitGrain {
    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = CommunicationGrainDecoder.TenantOf(this);
        _ = CommunicationGrainDecoder.ResourceOf(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<SpendReservation>> ReserveAsync(
        ChannelKind channel,
        ChannelLimits limits,
        decimal estimatedCost
    ) {
        ArgumentNullException.ThrowIfNull(limits);

        if (channel == ChannelKind.Unknown) {
            return Result<SpendReservation>.Failure(
                ErrorCode.InvalidRequestBody,
                "A reservation names a channel. ChannelKind.Unknown is the zero value a "
                + "default-constructed wire type carries, not a channel."
            );
        }

        if (estimatedCost < 0) {
            return Result<SpendReservation>.Failure(
                ErrorCode.InvalidRequestBody,
                $"An estimated cost cannot be negative; {estimatedCost.ToString(CultureInfo.InvariantCulture)} is."
            );
        }

        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var window = WindowFor(channel, today);

        Expire(window, now);

        var messagesAfter = window.Messages + 1;
        var committed = window.Settled + window.Pending.Sum(x => x.Amount);
        var spendAfter = committed + estimatedCost;

        // ⚠ THE LINE BETWEEN A BUG AND A FIVE-FIGURE INVOICE — docs/plan/17 § The parts that are
        // actually the work. Both caps are checked before the carrier is called, and the message
        // names the limit, the window, the current figure and the request, which is docs/plan/22
        // § Quota's rule: "429 naming the meter, the request, the current usage and the limit. Never
        // a bare 'quota exceeded'." At 03:00 the difference between those two messages is whether
        // on-call has to open a dashboard.
        if (messagesAfter > limits.MaxMessagesPerWindow) {
            return Result<SpendReservation>.Failure(
                ErrorCode.QuotaExceeded,
                $"The {channel} message limit for the UTC day {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
                + $"is {limits.MaxMessagesPerWindow.ToString(CultureInfo.InvariantCulture)} and "
                + $"{window.Messages.ToString(CultureInfo.InvariantCulture)} have been sent, so this "
                + "message would be number "
                + messagesAfter.ToString(CultureInfo.InvariantCulture)
                + ". The window resets at the next UTC midnight. Raise "
                + "ChannelLimits.MaxMessagesPerWindow on the service resource if this is real "
                + "traffic — but check for a loop first, because that is what this limit is for."
            );
        }

        if (spendAfter > limits.MaxSpendPerWindow) {
            return Result<SpendReservation>.Failure(
                ErrorCode.QuotaExceeded,
                $"The {channel} spend limit for the UTC day {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
                + $"is {limits.MaxSpendPerWindow.ToString(CultureInfo.InvariantCulture)} {limits.Currency} "
                + $"and {committed.ToString(CultureInfo.InvariantCulture)} {limits.Currency} is "
                + $"committed, so this message's estimated {estimatedCost.ToString(CultureInfo.InvariantCulture)} "
                + $"{limits.Currency} would reach {spendAfter.ToString(CultureInfo.InvariantCulture)} "
                + $"{limits.Currency}. The window resets at the next UTC midnight."
            );
        }

        var reservation = new PendingReservation {
            ReservationId = Guid.NewGuid(),
            Amount = estimatedCost,
            ExpiresAt = now + ISendLimitGrain.ReservationLease
        };

        window.Messages = messagesAfter;
        window.Pending.Add(reservation);
        await state.WriteStateAsync();

        return Result<SpendReservation>.Success(
            new() {
                ReservationId = reservation.ReservationId,
                Channel = channel,
                Amount = reservation.Amount,
                Window = today,
                ExpiresAt = reservation.ExpiresAt
            }
        );
    }

    /// <inheritdoc />
    public async Task<Result> SettleAsync(Guid reservationId, decimal actualCost) {
        foreach (var window in state.State.Windows.Values) {
            var held = window.Pending.FirstOrDefault(x => x.ReservationId == reservationId);
            if (held is null) {
                continue;
            }

            _ = window.Pending.Remove(held);

            // ⚠ Settling above the estimate is allowed. An international SMS routinely costs several
            // times a domestic one, and the alternative is unsending a message that has already gone.
            // The cap's job is to stop the NEXT one, and it now does — from a higher figure.
            window.Settled += actualCost < 0 ? held.Amount : actualCost;
            await state.WriteStateAsync();

            return Result.Success;
        }

        // ⚠ An unknown reservation settles successfully and changes nothing. A carrier receipt
        // arriving twice must not double-count spend, and a receipt arriving after the lease expired
        // must not resurrect a claim that was already given back.
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(Guid reservationId) {
        foreach (var window in state.State.Windows.Values) {
            var held = window.Pending.FirstOrDefault(x => x.ReservationId == reservationId);
            if (held is null) {
                continue;
            }

            _ = window.Pending.Remove(held);

            // The message never left, so it does not count against the message cap either. Without
            // this, a channel whose carrier is down burns the day's allowance on nothing.
            window.Messages = Math.Max(0, window.Messages - 1);
            await state.WriteStateAsync();

            return Result.Success;
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ChannelSpend>> ReadAsync(ChannelKind channel) {
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var window = WindowFor(channel, today);

        Expire(window, now);

        return Task.FromResult(
            Result<ChannelSpend>.Success(
                new() {
                    Channel = channel,
                    Window = today,
                    Messages = window.Messages,
                    Settled = window.Settled,
                    Reserved = window.Pending.Sum(x => x.Amount)
                }
            )
        );
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The channel's counters for today, resetting them when the UTC day has turned.
    /// </summary>
    /// <remarks>
    ///     ⚠ The reset is lazy rather than scheduled. A reminder that fired at midnight per service
    ///     would be a timer per tenant to keep a number at zero that nothing has read since
    ///     yesterday; reading it on the next send costs one date comparison and cannot drift.
    /// </remarks>
    ChannelWindowState WindowFor(ChannelKind channel, DateOnly today) {
        var slot = (int)channel;

        if (!state.State.Windows.TryGetValue(slot, out var window)) {
            window = new() { Window = today };
            state.State.Windows[slot] = window;
        }

        if (window.Window != today) {
            window.Window = today;
            window.Messages = 0;
            window.Settled = 0m;
            window.Pending.Clear();
        }

        return window;
    }

    /// <summary>
    ///     Gives back claims nothing settled inside the lease.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without this, a silo that died between reserving and dispatching holds a slice of the
    ///     day's budget until midnight. The message count comes back too — the message never left.
    /// </remarks>
    static void Expire(ChannelWindowState window, DateTimeOffset now) {
        for (var i = window.Pending.Count - 1; i >= 0; i--) {
            if (now < window.Pending[i].ExpiresAt) {
                continue;
            }

            window.Pending.RemoveAt(i);
            window.Messages = Math.Max(0, window.Messages - 1);
        }
    }
}
