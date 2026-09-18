using CyberCloud.Communication.Providers.Smtp;
using System.Text;

namespace CyberCloud.Communication.Tests;

/// <summary>
///     What leaves the platform as an email, byte for byte — the deliverability headers docs/plan/17
///     asks for and the two injection paths a template author could otherwise reach.
/// </summary>
/// <remarks>
///     No relay: <c>MailMessages</c> is pure functions over strings, and this is where the exact
///     bytes are pinned. <c>SmtpChannelProviderTests</c> then sends the same shape to a real Mailpit
///     and reads the headers back, which proves a receiver agrees with what is asserted here.
/// </remarks>
public sealed class MailMessagesTests {
    static readonly Guid MessageId = Guid.Parse("0f1e2d3c-4b5a-4978-8f6e-5d4c3b2a1908");
    static readonly DateTimeOffset Date = new(2026, 9, 17, 14, 22, 9, TimeSpan.FromHours(2));

    static SmtpRelayOptions Relay(string unsubscribe = "unsubscribe@cybercloud.example") =>
        new() {
            Host = "relay.example",
            Port = 1025,
            Security = SmtpSecurity.None,
            From = "no-reply@cybercloud.example",
            FromName = "Cyber Cloud",
            UnsubscribeMailbox = unsubscribe
        };

    static OutboundMessage Message(string subject = "Your code", string body = "424242 is your code.") =>
        new() {
            MessageId = MessageId,
            TenantId = Guid.NewGuid(),
            Channel = ChannelKind.Email,
            Destination = "alice@example.com",
            Subject = subject,
            Body = body,
            Locale = "en-US"
        };

    static Dictionary<string, string> HeadersOf(string message) {
        var end = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        end.ShouldBeGreaterThan(0, "a message is headers, a blank line, then the body");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var line in message[..end].Split("\r\n")) {
            if (line.StartsWith(' ') || line.StartsWith('\t')) {
                current.ShouldNotBeNull("a folded continuation needs a header to continue");
                headers[current] += line;
                continue;
            }

            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            colon.ShouldBeGreaterThan(0, $"'{line}' is not a header");
            current = line[..colon];
            headers[current] = line[(colon + 2)..];
        }

        return headers;
    }

    static string BodyOf(string message) => message[(message.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];

    // ── The headers docs/plan/17 § Deliverability asks of the message itself ────────────────────

    [Fact]
    public void EveryMessageCarriesTheDeliverabilityHeaders() {
        var messageId = MailMessages.MessageIdFor(MessageId, Relay().FromDomain);
        var composed = MailMessages.Compose(Message(), Relay(), messageId, Date);
        var headers = HeadersOf(composed);

        // A per-message Message-ID, under the sender's domain, is the handle a bounce quotes back.
        headers["Message-ID"].ShouldBe("<0f1e2d3c4b5a49788f6e5d4c3b2a1908@cybercloud.example>");
        messageId.ShouldBe(headers["Message-ID"]);

        headers["List-Unsubscribe"].ShouldBe("<mailto:unsubscribe@cybercloud.example?subject=unsubscribe>");
        headers["Auto-Submitted"].ShouldBe(
            "auto-generated",
            "RFC 3834 — so no vacation responder answers the platform"
        );
        headers["Date"].ShouldBe("Thu, 17 Sep 2026 12:22:09 +0000", "UTC, whatever offset the clock carried");
        headers["From"].ShouldBe("Cyber Cloud <no-reply@cybercloud.example>");
        headers["To"].ShouldBe("<alice@example.com>");
        headers["Subject"].ShouldBe("Your code");
        headers["MIME-Version"].ShouldBe("1.0");
        headers["Content-Type"].ShouldBe("text/plain; charset=utf-8");
        headers["Content-Transfer-Encoding"].ShouldBe("quoted-printable");

        BodyOf(composed).ShouldBe("424242 is your code.");
    }

    [Fact]
    public void TwoMessagesNeverShareAMessageId() {
        var domain = Relay().FromDomain;

        MailMessages.MessageIdFor(Guid.NewGuid(), domain)
            .ShouldNotBe(MailMessages.MessageIdFor(Guid.NewGuid(), domain));
    }

    [Fact]
    public void TheUnsubscribeHeaderIsAbsentWhenNoMailboxIsConfigured() {
        // ⚠ Absent rather than pointing at a mailbox nobody reads. A List-Unsubscribe that goes
        // nowhere is a recipient who tried to leave and could not — which is the complaint the
        // header exists to prevent.
        var composed = MailMessages.Compose(Message(), Relay(""), "<x@cybercloud.example>", Date);

        HeadersOf(composed).ShouldNotContainKey("List-Unsubscribe");
    }

    [Fact]
    public void AFreeTextSendWithNoSubjectUsesItsFirstLine() {
        // CommunicationOtpDelivery with no template sends "424242 is your code." and no subject; an
        // email with no Subject header is a spam signal and a blank inbox line.
        var composed = MailMessages.Compose(
            Message("", "424242 is your code.\r\nIt expires in ten minutes."),
            Relay(),
            "<x@cybercloud.example>",
            Date
        );

        HeadersOf(composed)["Subject"].ShouldBe("424242 is your code.");
    }

    // ── The two injection paths ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ASubjectWithALineBreakCannotAddAHeader() {
        var composed = MailMessages.Compose(
            Message("Your code\r\nBcc: mallory@example.net"),
            Relay(),
            "<x@cybercloud.example>",
            Date
        );
        var headers = HeadersOf(composed);

        headers.ShouldNotContainKey("Bcc");
        headers["Subject"].ShouldStartWith(
            "=?utf-8?B?",
            Case.Sensitive,
            "a control character takes the whole value down the RFC 2047 path"
        );

        // And it decodes back to what was written, line break included, as one value.
        Decode(headers["Subject"]).ShouldBe("Your code\r\nBcc: mallory@example.net");
    }

    [Fact]
    public void ANonAsciiSubjectIsEncodedInWordsNoLongerThanRfc2047Allows() {
        var subject = "Váš kód: Přihlášení do Cyber Cloud — Ověření účtu — Ještě jednou pro jistotu";
        var composed = MailMessages.Compose(Message(subject), Relay(), "<x@cybercloud.example>", Date);
        var value = HeadersOf(composed)["Subject"];

        foreach (var word in value.Split(' ')) {
            word.Length.ShouldBeLessThanOrEqualTo(75, "RFC 2047 § 2: an encoded-word is at most 75 characters");
            word.ShouldStartWith("=?utf-8?B?");
            word.ShouldEndWith("?=");
        }

        Decode(value).ShouldBe(subject, "and it comes back whole — no word was cut inside a UTF-8 sequence");
    }

    [Fact]
    public void ALongHeaderIsFoldedSoNoLineExceeds78Characters() {
        var subject = string.Join(' ', Enumerable.Repeat("word", 40));
        var composed = MailMessages.Compose(Message(subject), Relay(), "<x@cybercloud.example>", Date);

        foreach (var line in composed[..composed.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n")) {
            line.Length.ShouldBeLessThanOrEqualTo(78, $"RFC 5322 § 2.1.1 — '{line}'");
        }

        HeadersOf(composed)["Subject"].Replace("  ", " ", StringComparison.Ordinal).ShouldBe(subject);
    }

    // ── The body ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBodyIsQuotedPrintableWithSoftBreaks() {
        var body = "Dobrý den,\n\n" + new string('x', 100) + "\r\nend. \n=\n";
        var encoded = MailMessages.QuotedPrintable(body);

        foreach (var line in encoded.Split("\r\n")) {
            line.Length.ShouldBeLessThanOrEqualTo(MailMessages.MaxLineLength, $"RFC 2045 § 6.7 — '{line}'");
        }

        encoded.ShouldContain("Dobr=C3=BD den,", Case.Sensitive, "UTF-8 bytes outside printable ASCII are =XX");
        encoded.ShouldContain(
            "end.=20\r\n",
            Case.Sensitive,
            "trailing white space is encoded so a relay cannot strip it"
        );
        encoded.ShouldContain("\r\n=3D\r\n", Case.Sensitive, "the escape character is itself escaped");
        var withoutLineBreaks = encoded.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        withoutLineBreaks.ShouldNotContain(
            "\n",
            Case.Sensitive,
            "every line break in the output is CRLF, never a bare LF"
        );
        withoutLineBreaks.ShouldNotContain("\r", Case.Sensitive, "and never a bare CR");

        DecodeQuotedPrintable(encoded).ShouldBe(body.ReplaceLineEndings("\r\n"));
    }

    [Fact]
    public void ALoneDotLineIsLeftForTheTransportToStuff() {
        // The message composer does not dot-stuff — that is a property of the DATA command, and
        // SmtpConnection.SendDataAsync does it. Doing it here as well would send ".." to the reader.
        MailMessages.QuotedPrintable("first\r\n.\r\nlast").ShouldBe("first\r\n.\r\nlast");
    }

    // ── The address rule ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("alice@example.com", true)]
    [InlineData("first.last+tag@sub.example.co.uk", true)]
    [InlineData("alice", false)]
    [InlineData("@example.com", false)]
    [InlineData("alice@", false)]
    [InlineData("alice@ex@ample.com", false)]
    [InlineData("alice <alice@example.com>", false)]
    [InlineData("alice@example.com\r\nRCPT TO:<mallory@example.net>", false)]
    [InlineData("\"quoted local\"@example.com", false)]
    [InlineData("jiří@example.com", false)]
    public void AnAddressIsPlainAsciiWithOneAtAndNothingThatCouldEscapeAnEnvelope(string address, bool accepted) {
        MailAddresses.Check(address).IsSuccess.ShouldBe(accepted);
    }

    // ── The relay section ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void APasswordOverNoTlsIsRefusedAtComposition() {
        var relay = Relay();
        relay.Username = "smtp-user";
        relay.Password = "smtp-pass";

        var refused = relay.Validate();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("in the clear");
    }

    [Fact]
    public void HalfACredentialIsRefused() {
        var relay = Relay();
        relay.Security = SmtpSecurity.StartTls;
        relay.Username = "smtp-user";

        relay.Validate().Error!.Message.ShouldContain("go together");
    }

    [Fact]
    public void AFromThatIsNotAnAddressIsRefusedNamingTheKey() {
        var relay = Relay();
        relay.From = "Cyber Cloud";

        relay.Validate().Error!.Message.ShouldStartWith("CyberCloud:Communication:Smtp:From:");
    }

    [Fact]
    public void TheUsableSectionValidates() {
        Relay().Validate().IsSuccess.ShouldBeTrue();
        Relay().FromDomain.ShouldBe("cybercloud.example");
    }

    static string Decode(string encodedWords) {
        var decoded = new StringBuilder();

        foreach (var word in encodedWords.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            var payload = word["=?utf-8?B?".Length..^2];
            decoded.Append(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        }

        return decoded.ToString();
    }

    static string DecodeQuotedPrintable(string encoded) {
        var bytes = new List<byte>(encoded.Length);

        for (var i = 0; i < encoded.Length; i++) {
            var c = encoded[i];

            if (c == '=' && i + 2 < encoded.Length && encoded[i + 1] == '\r' && encoded[i + 2] == '\n') {
                i += 2;
                continue;
            }

            if (c == '=') {
                bytes.Add(Convert.ToByte(encoded.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }

            bytes.Add((byte)c);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
