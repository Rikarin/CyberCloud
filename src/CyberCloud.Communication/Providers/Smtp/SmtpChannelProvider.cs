using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace CyberCloud.Communication.Providers.Smtp;

/// <summary>
///     The email carrier — <see cref="IChannelProvider" /> for <see cref="ChannelKind.Email" /> over
///     SMTP to a configured relay. The first real carrier in the module (#93).
/// </summary>
/// <remarks>
///     <para>
///         <b>What it is, and what it is not.</b> It is the client side of docs/plan/17 § The
///         channel abstraction's "Amazon SES/our own Postfix (email)": one authenticated,
///         TLS-protected SMTP submission per message to whatever relay <see cref="SmtpRelayOptions" />
///         names, with the deliverability headers docs/plan/17 § Deliverability asks of the message
///         itself — <see cref="MailMessages" /> lists them. It is <b>not</b> the relay: the warmed
///         outbound pool with its PTR records, feedback loops and RBL monitoring is platform
///         infrastructure this repository does not deploy, and <c>charts/bundle/bundle.yaml § owed</c>
///         (<c>the-platform-has-no-mta</c>) says what remains.
///     </para>
///     <para>
///         <b>Against <see cref="IChannelProvider" />'s list of what a real implementation owes:</b>
///     </para>
///     <list type="number">
///         <item>
///             <i>Authenticate from a handle.</i> The relay credential is the platform's own and is
///             configuration — <see cref="SmtpRelayOptions" />' remarks own the deviation. A tenant's
///             own credential (<see cref="CredentialMode.TenantAccount" />) is <b>refused</b> rather
///             than sent through the platform's relay under the platform's name: a BYO SMTP account
///             needs a host per tenant, which <see cref="ChannelConfiguration" /> cannot carry, and
///             sending it through ours would bill the platform for traffic the tenant asked to pay
///             for.
///         </item>
///         <item>
///             <i>Send as the registered sender.</i> ⚠ Not yet. <see cref="OutboundMessage.Sender" />
///             is a <c>From</c> address the tenant registered, and the relay sends as
///             <see cref="SmtpRelayOptions.From" /> only — a tenant's <c>From</c> needs SPF and DKIM
///             alignment for the tenant's domain, which docs/plan/17 § Deliverability puts on
///             <c>CyberCloud.Mail</c>. A message naming a sender that is not the relay's own
///             <c>From</c> is <b>refused</b> before any connection, rather than sent under the
///             platform's name with nothing saying so.
///             <c>SmtpRefusalTests.ARegisteredSenderTheRelayCannotSendAsIsRefusedNotReplaced</c>.
///         </item>
///         <item>
///             <i>A provider message id, always.</i> The <c>Message-ID</c> this class minted,
///             without its angle brackets. It is under the sender's domain, so a bounce quoting it
///             can be attributed to a message.
///         </item>
///         <item>
///             <i>Verify webhook signatures.</i> ⚠ There is no webhook. A relay reports a bounce as
///             <b>mail</b> — an RFC 3464 delivery status notification to the envelope sender — and
///             SES reports it as an SNS notification. Neither has an ingress on this platform, so
///             <see cref="HandleWebhookAsync" /> recognises nothing; the design for both is in the
///             owed row (<c>communication-receipts-have-no-ingress</c>) so the first deployed relay
///             lands with it.
///         </item>
///         <item>
///             <i>Classify failures.</i> A <c>5yz</c> from the relay is reported as permanent and a
///             <c>4yz</c> as transient in the failure's text, and the message grain records
///             <see cref="MessageStatus.Failed" /> either way. Whether an address is dead is a thing
///             the <i>receiving</i> server says in the bounce, so
///             <see cref="DeliveryReceipt.Suppresses" /> is the ingestion's to set, not this class's.
///         </item>
///         <item>
///             <i>Report cost.</i> SMTP has no price on the wire. The receipt carries none, so the
///             spend limit stays at the channel's estimate — which for a relay the platform runs is
///             the honest figure.
///         </item>
///         <item>
///             <i>Be idempotent where the carrier lets you.</i> SMTP does not. Our idempotency is the
///             message grain's. A relay that never answers is reported as
///             <see cref="ErrorCode.OperationTimeout" />, which is the one carrier failure the grain
///             keeps <see cref="MessageStatus.Queued" /> rather than settling as
///             <see cref="MessageStatus.Failed" /> — whether the relay queued the message is
///             unknown — and a deliberate retry of it (<c>IMessageGrain.RetryAsync</c>) mints the
///             same <c>Message-ID</c>, so a receiver that deduplicates on it drops the second copy.
///         </item>
///         <item>
///             <i>Enforce nothing about compliance.</i> Nothing here checks SPF, DKIM or DMARC on
///             the sender's domain — the relay's operator does that once, and docs/plan/17
///             § Deliverability puts the verification seam on <c>CyberCloud.Mail</c>.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The suppression check is not here, and must not be.</b> <see cref="IChannelProvider.SendAsync" />
///         says a provider "must not re-check" the list; <c>MessageGrain.DispatchAsync</c> runs it
///         before a provider is resolved, and <c>SmtpChannelProviderTests</c> asserts against a real
///         relay that a suppressed address produces no connection at all.
///     </para>
///     <para>
///         ⚠ <b>No address in any log line.</b> docs/plan/11 § Auditing bans an email from a log
///         message. What is logged is the message id, the relay's reply code and its text — a
///         relay's text can quote the address back, which is why the text goes at Debug and the
///         code at Warning.
///     </para>
/// </remarks>
public sealed class SmtpChannelProvider(
    SmtpRelayOptions relay,
    IClock clock,
    ILogger<SmtpChannelProvider> logger
) : IChannelProvider {
    /// <summary>The name a channel configuration selects this carrier by — <c>provider: smtp</c>.</summary>
    public const string ProviderName = "smtp";

    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Email;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public async Task<Result<DispatchReceipt>> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Channel != ChannelKind.Email) {
            return Result<DispatchReceipt>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The smtp carrier sends email and was handed a {message.Channel} message."
            );
        }

        if (message.Credentials.Mode == CredentialMode.TenantAccount) {
            return Result<DispatchReceipt>.Failure(
                ErrorCode.PolicyViolation,
                "This channel is on the tenant's own account (account: tenant) and the smtp carrier sends "
                + "through the platform's relay only. A tenant's SMTP credentials name a relay host of their "
                + "own, which a channel configuration cannot carry yet; until it can, set account: platform "
                + "to send through the platform's relay. charts/bundle/bundle.yaml § owed, "
                + "the-platform-has-no-mta, records the BYO half."
            );
        }

        if (MailAddresses.Check(message.Destination).TryGetError(out var badAddress)) {
            return Result<DispatchReceipt>.Failure(badAddress.Code, $"The destination was refused before any connection: {badAddress.Message}");
        }

        // ⚠ A registered sender is a From address the tenant proved to a carrier, and this relay
        // sends as the platform's From alone — see the class remarks. Refused, not replaced: mail
        // that says "from the platform" when the channel said "from billing@tenant" is a
        // misattribution nobody asked for, and the refusal names the gap.
        if (message.Sender.Length > 0 && !string.Equals(message.Sender, relay.From, StringComparison.OrdinalIgnoreCase)) {
            return Result<DispatchReceipt>.Failure(
                ErrorCode.PolicyViolation,
                $"This channel names a registered sender ({message.Sender}) and the smtp carrier sends as the "
                + $"relay's own From ({relay.From}) only: a tenant's From needs SPF and DKIM alignment for the "
                + "tenant's domain, which is CyberCloud.Mail's (docs/plan/17 § Deliverability) and does not ship "
                + "yet. Nothing was sent. Until it does, an email channel on this carrier leaves its sender "
                + "empty and goes out under the platform's name."
            );
        }

        var messageId = MailMessages.MessageIdFor(message.MessageId, relay.FromDomain);
        var text = MailMessages.Compose(message, relay, messageId, clock.UtcNow);

        var submitted = await SubmitAsync(message.Destination, text, cancellationToken);

        if (submitted.TryGetError(out var refused)) {
            // ⚠ The relay's own text goes to Debug and the code to Warning, because a relay's text
            // can quote the address back — see the class remarks.
            logger.LogWarning(
                "The relay {Relay} did not accept message {MessageId} ({Code}).",
                relay.Host,
                message.MessageId,
                refused.Code
            );
            logger.LogDebug("Relay refusal for {MessageId}: {Reason}", message.MessageId, refused.Message);

            return Result<DispatchReceipt>.Failure(refused);
        }

        logger.LogInformation(
            "The relay {Relay} accepted message {MessageId} as {SmtpMessageId}.",
            relay.Host,
            message.MessageId,
            messageId
        );

        // The queue id the relay printed — "queued as 4XyZ" — is worth having beside ours when a
        // Postfix operator is asked where a message went, and Debug is where a relay's free text
        // belongs (see the class remarks about addresses in text).
        logger.LogDebug("Relay reply for {MessageId}: {Reply}.", message.MessageId, submitted.GetValueOrThrow());

        return Result<DispatchReceipt>.Success(
            new() {
                ProviderMessageId = messageId.Trim('<', '>'),
                Status = MessageStatus.Dispatched,
                AcceptedAt = clock.UtcNow
            }
        );
    }

    /// <inheritdoc />
    public Task<Result<DeliveryStatus>> GetStatusAsync(string providerMessageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Result<DeliveryStatus>.Failure(
                ErrorCode.InternalError,
                "SMTP has no status query: a relay says \"accepted\" once and reports what happened later as a "
                + "delivery status notification to the envelope sender, or, for Amazon SES, as an SNS "
                + "notification. Neither has an ingress on this platform yet — charts/bundle/bundle.yaml § owed, "
                + "communication-receipts-have-no-ingress — so where message "
                + providerMessageId
                + " got to after the relay is unknown. ⚠ Unknown is reported as a failure and not as "
                + "MessageStatus.Unknown, for the reason the refusing seams give: a caller would read that as "
                + "\"not delivered yet\" and keep waiting."
            )
        );

    /// <inheritdoc />
    /// <remarks>
    ///     Recognises nothing, which is a success — a bounce reaches a relay as mail and SES's
    ///     notification reaches an SNS topic, and neither is a callback this class can verify
    ///     today. What parsing each will take is written where the ingress is owed.
    /// </remarks>
    public ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(WebhookEnvelope request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result<WebhookOutcome>.Success(WebhookOutcome.Empty));

    /// <summary>
    ///     One SMTP session: greeting, <c>EHLO</c>, TLS as configured, <c>AUTH</c> if configured, the
    ///     envelope, the message, <c>QUIT</c>.
    /// </summary>
    /// <returns>The relay's reply to the message, or a failure whose target is the reply code that refused.</returns>
    async Task<Result<string>> SubmitAsync(string destination, string text, CancellationToken cancellationToken) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(relay.Timeout);
        var ct = timeout.Token;

        try {
            await using var connection = await SmtpConnection.ConnectAsync(
                relay.Host,
                relay.Port,
                relay.Security == SmtpSecurity.ImplicitTls,
                ct
            );

            var greeting = await connection.ReadReplyAsync(ct);
            if (greeting.Code != 220) {
                return Refused("greeting", greeting);
            }

            var extensions = await HelloAsync(connection, ct);
            if (extensions.TryGetError(out var helloFailed)) {
                return Result<string>.Failure(helloFailed);
            }

            if (relay.Security == SmtpSecurity.StartTls) {
                if (!extensions.GetValueOrThrow().Contains("STARTTLS")) {
                    return Result<string>.Failure(
                        ErrorCode.PolicyViolation,
                        $"The relay {relay.Host}:{relay.Port.ToString(CultureInfo.InvariantCulture)} does not offer "
                        + "STARTTLS and CyberCloud:Communication:Smtp:Security is StartTls. Nothing was sent: a "
                        + "message in the clear is a message anyone on the path reads. Point the silo at the "
                        + "relay's submission port, use ImplicitTls on 465, or — for a relay in the same pod — "
                        + "set Security to None deliberately."
                    );
                }

                var starting = await connection.SendCommandAsync("STARTTLS", ct);
                if (starting.Code != 220) {
                    return Refused("STARTTLS", starting);
                }

                await connection.UpgradeToTlsAsync(ct);

                // RFC 3207 § 4.2: the client MUST discard what it learned before TLS and say EHLO again.
                extensions = await HelloAsync(connection, ct);
                if (extensions.TryGetError(out var helloAgainFailed)) {
                    return Result<string>.Failure(helloAgainFailed);
                }
            }

            if (!string.IsNullOrWhiteSpace(relay.Username)) {
                var authenticated = await AuthenticateAsync(connection, extensions.GetValueOrThrow(), ct);
                if (authenticated.TryGetError(out var authFailed)) {
                    return Result<string>.Failure(authFailed);
                }
            }

            var from = await connection.SendCommandAsync(string.Concat("MAIL FROM:<", relay.From, ">"), ct);
            if (!from.IsCompleted) {
                return Refused("MAIL FROM", from);
            }

            var to = await connection.SendCommandAsync(string.Concat("RCPT TO:<", destination, ">"), ct);
            if (!to.IsCompleted) {
                return Refused("RCPT TO", to);
            }

            var data = await connection.SendCommandAsync("DATA", ct);
            if (data.Code != 354) {
                return Refused("DATA", data);
            }

            var accepted = await connection.SendDataAsync(text, ct);
            if (!accepted.IsCompleted) {
                return Refused("the message", accepted);
            }

            // A relay that answers QUIT badly has still queued the message; the 250 above is the
            // fact that matters, so QUIT's answer is read and not judged.
            _ = await connection.SendCommandAsync("QUIT", ct);

            return Result<string>.Success(accepted.ToString());
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException) {
            return Result<string>.Failure(
                ErrorCode.OperationTimeout,
                $"The relay {relay.Host}:{relay.Port.ToString(CultureInfo.InvariantCulture)} did not finish the "
                + $"exchange within {relay.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s. Whether "
                + "it queued the message is unknown, so the message grain keeps it Queued — not Failed — and "
                + "only IMessageGrain.RetryAsync, by a caller who knows a second copy is acceptable, sends it again."
            );
        } catch (Exception error) when (error is SocketException or IOException or AuthenticationException or InvalidOperationException) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"The relay {relay.Host}:{relay.Port.ToString(CultureInfo.InvariantCulture)} could not be spoken to: "
                + $"{error.GetType().Name}: {error.Message}"
            );
        }
    }

    /// <summary>Says <c>EHLO</c> and reads the extension keywords the relay offers, upper-cased.</summary>
    async Task<Result<HashSet<string>>> HelloAsync(SmtpConnection connection, CancellationToken ct) {
        var hello = await connection.SendCommandAsync(string.Concat("EHLO ", ClientName()), ct);

        if (!hello.IsCompleted) {
            var failed = Refused("EHLO", hello);
            return Result<HashSet<string>>.Failure(failed.Error!);
        }

        // The first line is the relay's own name; every line after is `KEYWORD [params]`.
        var extensions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in hello.Lines.Skip(1)) {
            var keyword = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (keyword is not null) {
                extensions.Add(keyword.ToUpperInvariant());
            }

            // `AUTH PLAIN LOGIN` — the mechanisms ride on the AUTH line and are wanted separately.
            if (string.Equals(keyword, "AUTH", StringComparison.OrdinalIgnoreCase)) {
                foreach (var mechanism in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)) {
                    extensions.Add(string.Concat("AUTH=", mechanism.ToUpperInvariant()));
                }
            }
        }

        return Result<HashSet<string>>.Success(extensions);
    }

    /// <summary>
    ///     <c>AUTH PLAIN</c> when the relay offers it, <c>AUTH LOGIN</c> when it offers only that.
    /// </summary>
    /// <remarks>
    ///     ⚠ Never over a connection without TLS — <see cref="SmtpRelayOptions.Validate" /> refuses
    ///     the configuration, and this checks again at the moment it matters, because the two are
    ///     different mistakes: one is a setting, the other is a relay that dropped its STARTTLS.
    /// </remarks>
    async Task<Result> AuthenticateAsync(SmtpConnection connection, HashSet<string> extensions, CancellationToken ct) {
        if (!connection.IsEncrypted) {
            return Result.Failure(
                ErrorCode.PolicyViolation,
                "Refusing to send the relay password over a connection with no TLS."
            );
        }

        SmtpReply reply;

        if (extensions.Contains("AUTH=PLAIN")) {
            // RFC 4616: [authzid] NUL authcid NUL passwd, base64, as the initial response.
            var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Concat("\0", relay.Username, "\0", relay.Password)));
            reply = await connection.SendCommandAsync(string.Concat("AUTH PLAIN ", plain), ct);
        } else if (extensions.Contains("AUTH=LOGIN")) {
            reply = await connection.SendCommandAsync("AUTH LOGIN", ct);

            if (reply.Code == 334) {
                reply = await connection.SendCommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(relay.Username)), ct);
            }

            if (reply.Code == 334) {
                reply = await connection.SendCommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(relay.Password)), ct);
            }
        } else {
            return Result.Failure(
                ErrorCode.InternalError,
                $"The relay {relay.Host} offers neither AUTH PLAIN nor AUTH LOGIN (it advertised: "
                + string.Join(", ", extensions.Order(StringComparer.Ordinal))
                + ") and a credential is configured. Those two are what Amazon SES and every SASL-enabled "
                + "Postfix offer; nothing else is implemented."
            );
        }

        return reply.Code == 235
            ? Result.Success
            : Result.Failure(
                ErrorCode.AuthorizationFailed,
                $"The relay {relay.Host} refused the credential: {reply}. Nothing was sent."
            );
    }

    /// <summary>A refusal that carries the step, the verdict, and whether trying again could help.</summary>
    /// <remarks>
    ///     The reply code is in the text and not on <c>Error.Target</c>, which is an RFC 6901 pointer
    ///     into a request body (docs/plan/08 § Errors) and refuses anything else.
    /// </remarks>
    static Result<string> Refused(string step, SmtpReply reply) =>
        Result<string>.Failure(
            ErrorCode.InternalError,
            $"The relay refused {step} with {reply}. "
            + (reply.IsPermanentFailure
                ? "A 5yz reply is permanent: the same message to the same relay gets the same answer, so fix what it names."
                : "A non-5yz refusal is transient: the relay may accept the same message shortly.")
        );

    /// <summary>The name this client introduces itself by in <c>EHLO</c>.</summary>
    /// <remarks>
    ///     The machine's host name when it is one a relay accepts — letters, digits, hyphens and
    ///     dots — and a fixed label when it is not. Postfix with <c>reject_invalid_helo_hostname</c>
    ///     refuses an EHLO argument with an underscore or a space in it, and a Windows machine name
    ///     can have either.
    /// </remarks>
    static string ClientName() {
        string name;

        try {
            name = System.Net.Dns.GetHostName();
        } catch (SocketException) {
            name = string.Empty;
        }

        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            ? name
            : "cybercloud-silo";
    }
}
