using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Communication.Grains;

/// <summary>
///     <see cref="ISuppressionListGrain" /> — Coordinator, Durable, key <c>res/{serviceId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="ISuppressionListGrain" /> first: the refusal in
///     <see cref="ReleaseAsync" /> is the design, not a missing feature.</b> A tenant clearing a
///     complaint or an opt-out is the operation this list exists to prevent.
/// </remarks>
public sealed class SuppressionListGrain(
    [PersistentState("suppression-list", StorageTiers.Durable)] IPersistentState<SuppressionListState> state,
    IClock clock
)
    : Grain, ISuppressionListGrain {
    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Both decodes run and neither result is kept. They are the assertion that this grain was
    ///     reached with a tenant-qualified <c>res/</c> key — a suppression list read through the
    ///     wrong key shape would report an address as clear because it was looking at another
    ///     service's list.
    /// </remarks>
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = CommunicationGrainDecoder.TenantOf(this);
        _ = CommunicationGrainDecoder.ResourceOf(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<SuppressionEntry>> SuppressAsync(
        ChannelKind channel,
        string destination,
        SuppressionReason reason,
        string note
    ) {
        if (reason == SuppressionReason.Unknown) {
            return Result<SuppressionEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A suppression needs a reason. SuppressionReason.Unknown is the zero value a "
                + "default-constructed wire type carries, and the reason decides who may lift the "
                + "entry — see SuppressionReason."
            );
        }

        var normalized = Destinations.Normalize(channel, destination);
        if (normalized.TryGetError(out var error)) {
            return Result<SuppressionEntry>.Failure(error);
        }

        var address = normalized.GetValueOrThrow();
        var key = KeyFor(channel, address);

        // ⚠ Adding an address that is already there succeeds and updates the reason rather than
        // conflicting. A carrier redelivers its webhooks and a handset that sent STOP once will send
        // it again when the next message arrives, so "already suppressed" is the common path.
        var entry = new SuppressionEntry {
            Channel = channel,
            Destination = address,
            Reason = reason,
            SuppressedAt = state.State.Entries.TryGetValue(key, out var existing)
                ? existing.SuppressedAt
                : clock.UtcNow,
            Note = note
        };

        state.State.Entries[key] = entry;
        await state.WriteStateAsync();

        return Result<SuppressionEntry>.Success(entry);
    }

    /// <inheritdoc />
    public Task<Result<SuppressionCheck>> CheckAsync(ChannelKind channel, string destination) {
        var normalized = Destinations.Normalize(channel, destination);

        // ⚠ An unparseable destination reports clear rather than failing. The send path has already
        // normalized and validated it, so reaching here with a bad one means a caller went round the
        // front door — and a check that could fail would tempt that caller into reading failure as
        // "not suppressed", which is the one reading that must never be available.
        if (normalized.TryGetError(out _)) {
            return Task.FromResult(Result<SuppressionCheck>.Success(SuppressionCheck.Clear));
        }

        return Task.FromResult(
            Result<SuppressionCheck>.Success(
                state.State.Entries.TryGetValue(KeyFor(channel, normalized.GetValueOrThrow()), out var entry)
                    ? new() { IsSuppressed = true, Entry = entry }
                    : SuppressionCheck.Clear
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(ChannelKind channel, string destination, string reason) {
        if (string.IsNullOrWhiteSpace(reason)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "Taking an address off the suppression list needs a reason. It is the record of who "
                + "decided that a recipient can be contacted again, and it is what an abuse "
                + "investigation reads."
            );
        }

        var normalized = Destinations.Normalize(channel, destination);
        if (normalized.TryGetError(out var error)) {
            return Result.Failure(error);
        }

        var key = KeyFor(channel, normalized.GetValueOrThrow());
        if (!state.State.Entries.TryGetValue(key, out var entry)) {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"{normalized.GetValueOrThrow()} is not suppressed on {channel}, so there is nothing "
                + "to release."
            );
        }

        // ⚠ THE LINE THIS METHOD EXISTS FOR. A complaint and an opt-out are statements by the
        // recipient; a tenant clearing one is a tenant deciding on someone else's behalf that they
        // want to be messaged again. docs/plan/17 § The parts that are actually the work: "Ignoring a
        // complaint is how a sending domain gets blocked." Re-consent arrives as an inbound message
        // from the person who withdrew it, through IWebhookRouter.HandleInboundAsync.
        if (entry.Reason is SuppressionReason.Complaint or SuppressionReason.OptOut) {
            return Result.Failure(
                ErrorCode.PolicyViolation,
                $"{entry.Destination} is suppressed on {channel} because of a "
                + $"{entry.Reason.ToString().ToUpperInvariant()}, which is the recipient's decision "
                + "and not the tenant's to reverse. Only a fresh opt-in from that recipient lifts it. "
                + "A HardBounce or a ManualBlock can be released here; those are facts about an "
                + "address rather than statements by a person."
            );
        }

        _ = state.State.Entries.Remove(key);
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<SuppressionEntry>>> ListAsync(ChannelKind channel) =>
        Task.FromResult(
            Result<ImmutableArray<SuppressionEntry>>.Success(
                [
                    .. channel == ChannelKind.Unknown
                        ? state.State.Entries.Values
                        : state.State.Entries.Values.Where(x => x.Channel == channel)
                ]
            )
        );

    /// <inheritdoc />
    public Task<Result<long>> CountAsync() => Task.FromResult(Result<long>.Success(state.State.Entries.Count));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     ⚠ The channel goes in as its numeric value and the separator is <c>':'</c>, so no
    ///     destination can be re-cut into a different (channel, address) pair. A <c>'|'</c> would
    ///     have been the obvious separator and is exactly the character
    ///     <c>Orleans.Multitenant</c> reserves — this is state rather than a grain key, so it would
    ///     not have been wrong, but choosing a different one keeps the two ideas visibly apart.
    /// </summary>
    static string KeyFor(ChannelKind channel, string destination) =>
        ((int)channel).ToString(CultureInfo.InvariantCulture) + ":" + destination;
}
