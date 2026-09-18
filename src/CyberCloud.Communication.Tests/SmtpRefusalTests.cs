using CyberCloud.Communication.Providers.Smtp;
using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberCloud.Communication.Tests;

/// <summary>
///     What the email carrier does when the relay says no, or says nothing — every path a real relay
///     will not walk on demand, against <see cref="ScriptedSmtpServer" />.
/// </summary>
/// <remarks>
///     <para>
///         <c>SmtpChannelProviderTests</c> is the happy path against Mailpit. These are the refusals,
///         and the property each one pins is the same:
///         <b>
///             nothing was sent, and the failure says
///             why in the relay's own words.
///         </b> A carrier that swallowed a <c>550</c> into "the carrier
///         is down" would send an operator to the wrong console.
///     </para>
///     <para>
///         ⚠ <b>Two of the client's paths are not run to their happy end here or anywhere:</b> a
///         <c>STARTTLS</c> that completes and an <c>AUTH</c> that succeeds. Both need a certificate
///         the client trusts, and the client validates the chain and the host name with .NET's
///         defaults on purpose (<c>SmtpConnection.UpgradeToTlsAsync</c>). What IS run is the refusal
///         on each: a relay that does not offer <c>STARTTLS</c> when the section insists, a relay
///         whose certificate is not trusted, and a credential the client will not send in the clear.
///         The first relay with TLS is the first run of the other half.
///     </para>
/// </remarks>
public sealed class SmtpRefusalTests {
    static SmtpChannelProvider Carrier(ScriptedSmtpServer server, Action<SmtpRelayOptions>? configure = null) {
        var relay = new SmtpRelayOptions {
            Host = "127.0.0.1",
            Port = server.Port,
            Security = SmtpSecurity.None,
            From = "no-reply@cybercloud.example",
            UnsubscribeMailbox = "unsubscribe@cybercloud.example",
            Timeout = TimeSpan.FromSeconds(10)
        };

        configure?.Invoke(relay);

        return new(relay, new SystemClock(), NullLogger<SmtpChannelProvider>.Instance);
    }

    static OutboundMessage Message() =>
        new() {
            MessageId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Channel = ChannelKind.Email,
            Destination = "frank@example.com",
            Body = "424242 is your code.",
            Credentials = new() { Mode = CredentialMode.PlatformAccount }
        };

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheHappyPathSpeaksTheFiveCommandsInOrderAndTheBodyArrivesWhole() {
        await using var server = new ScriptedSmtpServer();
        server.Start();

        var accepted = await Carrier(server).SendAsync(Message(), Ct);

        var receipt = accepted.GetValueOrThrow();
        receipt.Status.ShouldBe(MessageStatus.Dispatched);
        receipt.ProviderMessageId.ShouldEndWith("@cybercloud.example");

        server.Commands.Select(static x => x.Split(' ', 2)[0].ToUpperInvariant())
            .ShouldBe(
                ["EHLO", "MAIL", "RCPT", "DATA", "QUIT"],
                "RFC 5321's minimal session, no STARTTLS and no AUTH on a relay that trusts the network"
            );

        server.Commands.ShouldContain("MAIL FROM:<no-reply@cybercloud.example>");
        server.Commands.ShouldContain("RCPT TO:<frank@example.com>");
        server.LastMessage.ShouldContain("Message-ID: <" + receipt.ProviderMessageId + ">");
        server.LastMessage.ShouldContain(
            "List-Unsubscribe: <mailto:unsubscribe@cybercloud.example?subject=unsubscribe>"
        );
        server.LastMessage.ShouldEndWith("424242 is your code.\r\n");
    }

    [Fact]
    public async Task AGreetingThatIsNot220IsRefusedBeforeAnythingIsSaid() {
        await using var server = new ScriptedSmtpServer();
        server.Greeting = "554 5.3.0 No SMTP service here";
        server.Start();

        var refused = await Carrier(server).SendAsync(Message(), Ct);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("554");
        refused.Error.Message.ShouldContain(
            "permanent",
            Case.Insensitive,
            "a 5yz reply is permanent, and the text says so"
        );
        server.Commands.ShouldBeEmpty("the client said nothing to a relay that refused it at the door");
    }

    [Fact]
    public async Task InsistingOnStartTlsAgainstARelayThatDoesNotOfferItSendsNothingInTheClear() {
        await using var server = new ScriptedSmtpServer();
        server.OfferStartTls = false;
        server.Start();

        var refused = await Carrier(server, static x => x.Security = SmtpSecurity.StartTls).SendAsync(Message(), Ct);

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("does not offer STARTTLS");

        // ⚠ THE ASSERTION. EHLO is fine in the clear; the envelope and the body are not.
        server.Commands.ShouldContain(x => x.StartsWith("EHLO", StringComparison.Ordinal));
        server.Commands.ShouldNotContain(
            x => x.StartsWith("MAIL", StringComparison.Ordinal),
            "nothing after EHLO went in the clear"
        );
    }

    [Fact]
    public async Task ARelayWhoseCertificateIsNotTrustedIsRefusedAtTheHandshake() {
        await using var server = new ScriptedSmtpServer();
        server.OfferStartTls = true;
        server.Start();

        var refused = await Carrier(server, static x => x.Security = SmtpSecurity.StartTls).SendAsync(Message(), Ct);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("could not be spoken to");
        refused.Error.Message.ShouldContain(
            "AuthenticationException",
            Case.Sensitive,
            "the self-signed certificate failed .NET's default validation, which is the validation this client keeps"
        );

        server.Commands.ShouldContain("STARTTLS");
        server.Commands.ShouldNotContain(x => x.StartsWith("MAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACredentialIsNeverSentOverAConnectionWithNoTls() {
        // ⚠ SmtpRelayOptions.Validate refuses this section at composition; this is the second gate,
        // at the moment it matters, for a carrier constructed around the first — or a relay whose
        // STARTTLS went away between two sends.
        await using var server = new ScriptedSmtpServer();
        server.AuthMechanisms = "PLAIN LOGIN";
        server.Start();

        var refused = await Carrier(
            server,
            static x => {
                x.Username = "smtp-user";
                x.Password = "smtp-pass";
            }
        ).SendAsync(Message(), Ct);

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("no TLS");
        server.Commands.ShouldNotContain(
            x => x.StartsWith("AUTH", StringComparison.Ordinal),
            "the password never reached the wire"
        );
        server.Commands.ShouldNotContain(x => x.StartsWith("MAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedRecipientIsAPermanentFailureInTheRelaysOwnWords() {
        await using var server = new ScriptedSmtpServer();
        server.RecipientReply = "550 5.1.1 <frank@example.com>: Recipient address rejected: User unknown";
        server.Start();

        var refused = await Carrier(server).SendAsync(Message(), Ct);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("refused RCPT TO with 550");
        refused.Error.Message.ShouldContain(
            "User unknown",
            Case.Sensitive,
            "verbatim, because it is the sentence the relay's operator will search for"
        );
        refused.Error.Message.ShouldContain("permanent");

        server.Commands.ShouldNotContain("DATA", "no body follows a refused recipient");
        server.LastMessage.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedEhloIsReportedAsTheStepItWas() {
        await using var server = new ScriptedSmtpServer();
        server.RefuseEhloWith = "502 5.5.2 Error: command not recognized";
        server.Start();

        var refused = await Carrier(server).SendAsync(Message(), Ct);

        refused.Error!.Message.ShouldContain("refused EHLO with 502");
    }

    [Fact]
    public async Task ARelayThatNeverAnswersIsATimeoutAndNotAHang() {
        await using var server = new ScriptedSmtpServer();
        server.Silent = true;
        server.Start();

        var refused = await Carrier(server, static x => x.Timeout = TimeSpan.FromSeconds(2)).SendAsync(Message(), Ct);

        refused.Error!.Code.ShouldBe(ErrorCode.OperationTimeout);
        refused.Error.Message.ShouldContain("did not finish the exchange within 2 s");
        // ⚠ The sentence tells an operator what the grain does with the message, so it has to be
        // what the grain does: MessageGrain keeps an OperationTimeout Queued and settles every other
        // carrier failure as Failed —
        // IdempotencyTests.ACarrierThatNeverAnsweredLeavesTheMessageQueuedForADeliberateRetry.
        refused.Error.Message.ShouldContain("keeps it Queued — not Failed", Case.Sensitive);
        refused.Error.Message.ShouldContain(
            "IMessageGrain.RetryAsync",
            Case.Sensitive,
            "and names the one call that moves it on"
        );
    }

    [Fact]
    public async Task ARegisteredSenderTheRelayCannotSendAsIsRefusedNotReplaced() {
        await using var server = new ScriptedSmtpServer();
        server.Start();

        // A channel with a registered sender hands the carrier the From the tenant proved. This relay
        // sends as its own From only, so the honest answer is a refusal — not mail from the platform
        // pretending nothing was asked.
        var refused = await Carrier(server).SendAsync(Message() with { Sender = "billing@tenant.example" }, Ct);

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("billing@tenant.example");
        refused.Error.Message.ShouldContain("CyberCloud.Mail");
        server.Commands.ShouldBeEmpty("refused before any connection");

        // The relay's own From, spelled with a different case, is the one sender it can honour.
        var accepted = await Carrier(server).SendAsync(Message() with { Sender = "No-Reply@CyberCloud.example" }, Ct);
        accepted.GetValueOrThrow().Status.ShouldBe(MessageStatus.Dispatched);
        server.Commands.ShouldContain("MAIL FROM:<no-reply@cybercloud.example>");
    }

    [Fact]
    public async Task TheCallersOwnCancellationIsNotReportedAsTheRelaysFault() {
        await using var server = new ScriptedSmtpServer();
        server.Silent = true;
        server.Start();

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => Carrier(server).SendAsync(Message(), caller.Token));
    }

    [Fact]
    public async Task ANonEmailMessageIsRefusedWithoutAConnection() {
        await using var server = new ScriptedSmtpServer();
        server.Start();

        var refused = await Carrier(server).SendAsync(Message() with { Channel = ChannelKind.Sms }, Ct);

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        server.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task StatusIsHonestlyUnknownAndTheWebhookRecognisesNothing() {
        await using var server = new ScriptedSmtpServer();
        var carrier = Carrier(server);

        var status = await carrier.GetStatusAsync("abc@cybercloud.example", Ct);
        status.IsFailure.ShouldBeTrue(
            "SMTP has no status query, and Unknown as a success would read as \"not delivered yet\""
        );
        status.Error!.Message.ShouldContain("communication-receipts-have-no-ingress");

        var webhook = await carrier.HandleWebhookAsync(new() { Body = "{}" }, Ct);
        webhook.GetValueOrThrow()
            .ShouldBe(WebhookOutcome.Empty, "a relay's bounce is mail, not a callback this class can verify");
    }
}
