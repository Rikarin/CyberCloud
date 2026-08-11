namespace CyberCloud.Communication.Contracts;

/// <summary>
///     One message, from accepted to settled — docs/plan/17 § The parts that are actually the work's
///     <i>"queued → dispatched → delivered/failed, with the provider id and receipts"</i>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Hot, TTL'd · <b>Key</b>
///         <c>res/{derived(serviceId, idempotencyKey):N}</c>, tenant-qualified. See
///         <see cref="CommunicationGrainKeys.Message" /> for the derivation and for what borrowing
///         the <c>res/</c> shape costs.
///     </para>
///     <para>
///         <b>Why the key <i>is</i> the idempotency mechanism.</b> docs/plan/17 § The parts that are
///         actually the work wants <i>"per-message status, retry with backoff, and idempotency in
///         one place"</i>. Deriving the key from the caller's key puts all three in this activation:
///         a retry addresses the same grain, finds a recorded send, and gets the original snapshot
///         back without a carrier call. There is no index to consult, so there is no window in which
///         two concurrent retries both miss it — Orleans serializes calls to one activation, which
///         is the property that makes this correct rather than merely likely.
///     </para>
///     <para>
///         ⚠ <b>Hot tier, and docs/plan/17 § The parts that are actually the work says so directly:
///         "Message grains (hot tier, TTL'd)".</b> The argument holds up against docs/plan/05
///         § Choosing a tier's test. What is lost if the hot tier is flushed is the ability to answer
///         "did this arrive" for messages sent in the retention window, and the ability to collapse a
///         retry that arrives after the flush. Neither is a record of account: the money is in
///         metering's ledger, which is durable, and the audit trail is the audit log. A durable tier
///         would put a synchronously-replicated write on the send path for state that is worthless
///         in a week — and the send path is an OTP, where latency is a user waiting at a login form.
///     </para>
///     <para>
///         ⚠ <b>What that costs, stated rather than hidden.</b> A hot-tier loss inside the retention
///         window turns a retry into a second send. That is the exact harm the idempotency
///         requirement exists to prevent, so it is not a shrug — it is a bounded one: a
///         <c>FLUSHALL</c> is a declared incident (docs/plan/05 § Hot), the window is
///         <see cref="Retention" />, and the alternative costs a durable write on every OTP forever.
///     </para>
///     <para>
///         <b>The TTL is state, not storage.</b> <see cref="MessageStatus" /> and the timestamps are
///         written with an expiry the grain checks on activation; the Redis-side <c>EXPIRE</c> that
///         actually reclaims the bytes belongs to the hot storage provider the silo host registers,
///         and is not decided here. A grain past its expiry reports
///         <see cref="MessageStatus.Unknown" /> and behaves as though it had never been sent — which
///         is exactly what an idempotency horizon means.
///     </para>
/// </remarks>
[Alias("CyberCloud.Communication.IMessageGrain")]
public interface IMessageGrain : IGrainWithStringKey {
    /// <summary>
    ///     Sends, or returns the send that already happened under this idempotency key.
    /// </summary>
    /// <param name="request">
    ///     What to send. ⚠ <see cref="SendRequest.IdempotencyKey" /> must be the one this grain's key
    ///     was derived from — reaching a message grain with a mismatched key is a bug in the caller
    ///     and is refused with <see cref="ErrorCode.InvalidGrainKey" /> rather than silently sending
    ///     something the key does not describe.
    /// </param>
    /// <returns>
    ///     <para>The message's state after the attempt.</para>
    ///     <para>
    ///         <b>On a repeat</b> the recorded snapshot comes back and no carrier is called, which is
    ///         the whole point. ⚠ A repeat that differs from the original in destination, channel or
    ///         content is <see cref="ErrorCode.Conflict" />, not a silent replay: two different
    ///         messages under one key means the caller is generating keys wrongly, and answering
    ///         with the first message's status would hide it until somebody asked why the second
    ///         never arrived.
    ///     </para>
    ///     <para>
    ///         <b>The order of checks is the contract</b>, because each one is a thing that must not
    ///         reach a carrier: the request is validated, the service's channel configuration is
    ///         read, the sender's registration is checked, the <b>suppression list is honoured</b>,
    ///         the template is rendered, the <b>spend limit is reserved</b>, and only then is
    ///         <see cref="IChannelProvider.SendAsync" /> called. A refusal at any step leaves
    ///         <see cref="MessageStatus.Refused" /> and the provider uncalled.
    ///     </para>
    /// </returns>
    Task<Result<MessageSnapshot>> SendAsync(SendRequest request);

    /// <summary>Where this message got to. The answer to "did it arrive".</summary>
    /// <returns>
    ///     ⚠ <see cref="ErrorCode.ResourceNotFound" /> when no send was recorded under this key, or
    ///     when the record has aged past <see cref="Retention" />. The two are indistinguishable on
    ///     purpose — a caller cannot tell "never sent" from "sent and forgotten", because a hot tier
    ///     that could tell them apart would be a durable tier.
    /// </returns>
    Task<Result<MessageSnapshot>> GetAsync();

    /// <summary>
    ///     Records what a carrier said about this message. Called by <see cref="IWebhookRouter" />,
    ///     never by a tenant.
    /// </summary>
    /// <param name="receipt">The carrier's statement.</param>
    /// <returns>
    ///     The updated snapshot.
    ///     <para>
    ///         ⚠ <b>Late, duplicate and out-of-order receipts are all normal and none of them is an
    ///         error.</b> A receipt for a status the message has already passed is recorded and
    ///         ignored; a second copy of one already seen is recorded once. Carriers retry their
    ///         webhooks, and a delivery pipeline that treats a retry as a fault alerts on the
    ///         carrier working correctly.
    ///     </para>
    /// </returns>
    Task<Result<MessageSnapshot>> RecordReceiptAsync(DeliveryReceipt receipt);

    /// <summary>
    ///     Re-drives a message stuck at <see cref="MessageStatus.Queued" />.
    /// </summary>
    /// <param name="request">
    ///     The original request, again. ⚠ Passed rather than replayed from state, and that is a
    ///     privacy decision with teeth: the grain never stores the body or the template arguments,
    ///     because the body of an OTP message <i>is</i> the one-time code and the body of a
    ///     password-reset message is a bearer token. Keeping a digest is enough to prove the retry is
    ///     the same message; keeping the text would put a live credential in the hot tier for
    ///     <see cref="Retention" />. A mismatched digest is <see cref="ErrorCode.Conflict" />.
    /// </param>
    /// <returns>
    ///     The snapshot after the attempt.
    ///     <para>
    ///         ⚠ <b>Explicit rather than automatic, and that is the safe direction.</b> A message is
    ///         <see cref="MessageStatus.Queued" /> with no provider id when the silo died between
    ///         writing the record and hearing back from the carrier — so nobody knows whether it
    ///         went. Retrying automatically would resolve that ambiguity towards a duplicate, and
    ///         docs/plan/17 § The parts that are actually the work is explicit about which way to
    ///         resolve it: <i>"an OTP sent twice is confusing; an invoice notice sent twice is a
    ///         support call"</i>. So the platform holds, reports, and lets a human or a caller who
    ///         knows the message is safe to repeat say so.
    ///     </para>
    ///     <para>
    ///         <see cref="ErrorCode.Conflict" /> for anything not <see cref="MessageStatus.Queued" />.
    ///     </para>
    /// </returns>
    Task<Result<MessageSnapshot>> RetryAsync(SendRequest request);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();

    /// <summary>
    ///     How long a message record — and therefore the idempotency guarantee — lasts.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Thirty days, chosen against the two things it bounds.</b> Below it sits the longest
    ///     plausible client retry (minutes) and the longest plausible late delivery receipt, which
    ///     for SMS to a handset that is switched off is measured in days. Above it sits the cost: one
    ///     hot-tier key per message sent, so a tenant sending a million messages a month holds a
    ///     million keys. Thirty days covers a monthly billing cycle's worth of "did this invoice
    ///     notice go out", which is the question support actually asks.
    /// </remarks>
    public static TimeSpan Retention => TimeSpan.FromDays(30);
}
