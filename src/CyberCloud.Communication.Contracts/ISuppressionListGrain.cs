using System.Collections.Immutable;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     The addresses a service may not send to — docs/plan/17 § The parts that are actually the
///     work: <i>"Bounces, complaints, opt-outs — per tenant, honoured before dispatch. Ignoring a
///     complaint is how a sending domain gets blocked."</i>
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Coordinator · <b>Tier</b> <b>Durable</b> · <b>Key</b> <c>res/{serviceId:N}</c>,
///         tenant-qualified — the same key as <see cref="ICommunicationServiceGrain" /> and
///         <see cref="ISendLimitGrain" />, on a different grain type.
///     </para>
///     <para>
///         ⚠ <b>Durable, and it is the one grain in this module where that is not arguable.</b>
///         docs/plan/17 § The parts that are actually the work puts message grains in the hot tier;
///         a suppression list is the opposite kind of state. docs/plan/05 § Choosing a tier's test is
///         <i>"can this be rebuilt"</i>, and the honest answer here is no in the worst possible way:
///         the upstream is a person who already told us to stop. Losing the list does not degrade
///         sending, it silently reverses a consent decision — and the platform then messages
///         everyone who opted out, which is a regulatory finding in most jurisdictions and, in the
///         US, a statutory penalty per message. Rebuilding it would mean asking every recipient
///         again, which is itself the thing they said not to do.
///     </para>
///     <para>
///         ⚠ <b>One grain per service holding every channel's entries, rather than one grain per
///         address.</b> A per-address grain (the <c>IEmailIndexGrain</c> shape) would bound the state
///         and cost the same single grain call on the send path. It was not chosen because
///         enumeration is a first-class operation here — a tenant has to be able to see and export
///         their suppression list, a support case starts with "is this address on it", and a
///         per-address design answers those only by scanning a tenant, which is the operation
///         docs/plan/05 § Hot describes as impossible when you need it. The cost is that state grows
///         with the list; <see cref="Ceiling" /> is where that stops being free and says so.
///     </para>
/// </remarks>
[Alias("CyberCloud.Communication.ISuppressionListGrain")]
public interface ISuppressionListGrain : IGrainWithStringKey {
    /// <summary>Adds an address, or updates the reason on one already there.</summary>
    /// <param name="channel">Which channel it applies to. Suppression is per channel — an email bounce says nothing about a phone number.</param>
    /// <param name="destination">The address, in any spelling. Normalized by <see cref="Destinations.Normalize" /> before it is stored.</param>
    /// <param name="reason">Why. See <see cref="SuppressionReason" /> — it decides who may lift it.</param>
    /// <param name="note">The carrier's or the operator's words, verbatim, for the support case.</param>
    /// <returns>
    ///     The entry as stored.
    ///     <para>
    ///         ⚠ Suppressing an address that is already suppressed succeeds and is not a conflict.
    ///         The operation is naturally idempotent, which is what lets a duplicate carrier webhook
    ///         and a repeated <c>STOP</c> both be handled by doing the obvious thing.
    ///     </para>
    /// </returns>
    Task<Result<SuppressionEntry>> SuppressAsync(
        ChannelKind channel,
        string destination,
        SuppressionReason reason,
        string note
    );

    /// <summary>Whether an address is suppressed. The check every send makes before dispatch.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="destination">The address, in any spelling.</param>
    /// <returns>
    ///     <see cref="SuppressionCheck.Clear" /> when it is not on the list. ⚠ An unparseable
    ///     destination reports clear rather than failing: the caller has already validated it, and a
    ///     check that could fail would tempt a caller into treating failure as "not suppressed".
    /// </returns>
    Task<Result<SuppressionCheck>> CheckAsync(ChannelKind channel, string destination);

    /// <summary>Takes an address off the list.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="destination">The address.</param>
    /// <param name="reason">Who is asking and why — recorded, and required.</param>
    /// <returns>
    ///     <para>
    ///         ⚠ <b><see cref="ErrorCode.PolicyViolation" /> for
    ///         <see cref="SuppressionReason.Complaint" /> and
    ///         <see cref="SuppressionReason.OptOut" />, and that refusal is the point of the
    ///         method.</b> Those two are statements by the recipient, not facts about an address. A
    ///         tenant who could clear them could un-unsubscribe their own recipients, which is both
    ///         the thing regulators write rules about and the thing that gets a sending domain
    ///         blocked. Re-consent arrives as a new inbound message, through
    ///         <see cref="IWebhookRouter.HandleInboundAsync" />, from the person who withdrew it.
    ///     </para>
    ///     <para>
    ///         <see cref="SuppressionReason.HardBounce" /> and
    ///         <see cref="SuppressionReason.ManualBlock" /> are the tenant's to clear — a typo'd
    ///         address and an operator's block are both facts a tenant can correct.
    ///     </para>
    /// </returns>
    Task<Result> ReleaseAsync(ChannelKind channel, string destination, string reason);

    /// <summary>Every entry on one channel, in the order they were added.</summary>
    /// <param name="channel">The channel, or <see cref="ChannelKind.Unknown" /> for all of them.</param>
    Task<Result<ImmutableArray<SuppressionEntry>>> ListAsync(ChannelKind channel);

    /// <summary>How many entries there are, over every channel.</summary>
    Task<Result<long>> CountAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();

    /// <summary>
    ///     Where one service's list stops growing quietly.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling rather than an eviction, because evicting a suppression entry re-enables
    ///     sending to somebody who opted out.</b> At the ceiling
    ///     <see cref="SuppressAsync" /> keeps accepting — refusing would be worse — and the grain is
    ///     over a size that a single durable read on the send path should not carry. The successor
    ///     is a per-address grain with a separate enumerable index, which is a design change and a
    ///     migration; the number is here so that the day it matters arrives as a measurement rather
    ///     than as a latency graph.
    /// </remarks>
    public const int Ceiling = 50_000;
}
