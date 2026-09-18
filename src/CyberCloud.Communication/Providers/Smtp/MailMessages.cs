using System.Globalization;
using System.Text;

namespace CyberCloud.Communication.Providers.Smtp;

/// <summary>What an address has to look like before it goes on an SMTP envelope or in a header.</summary>
/// <remarks>
///     ⚠ <b>Narrower than RFC 5321 on purpose.</b> A quoted local part, an address literal and a
///     non-ASCII mailbox (SMTPUTF8, RFC 6531) are all legal and all refused here, because each is a
///     path this client does not implement and an address that merely <i>looked</i> accepted would
///     be refused by the relay with a sentence about syntax rather than about the address. What is
///     accepted is the shape every real recipient has: printable ASCII, exactly one <c>@</c>, no
///     white space, nothing that could close an angle bracket or start a new header line.
/// </remarks>
static class MailAddresses {
    /// <summary>Accepts an address or says what is wrong with it.</summary>
    /// <param name="address">The address, as <c>Destinations.Normalize</c> or configuration spells it.</param>
    public static Result Check(string address) {
        if (string.IsNullOrWhiteSpace(address)) {
            return Result.Failure(ErrorCode.InvalidRequestBody, "an email address is empty.");
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "an email address is local-part@domain, with exactly one '@' and something on both sides."
            );
        }

        foreach (var c in address) {
            if (c is <= ' ' or > '~' or '<' or '>' or '"' or ',' or ';' or ':' or '\\' or '(' or ')' or '[' or ']') {
                return Result.Failure(
                    ErrorCode.InvalidRequestBody,
                    "an email address here is printable ASCII with no white space, quotes, brackets or "
                    + $"separators; U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)} is not. "
                    + "Quoted local parts, address literals and non-ASCII mailboxes (SMTPUTF8) are not "
                    + "supported by this client."
                );
            }
        }

        return Result.Success;
    }
}

/// <summary>
///     Turns an <see cref="OutboundMessage" /> into the bytes an SMTP <c>DATA</c> command carries —
///     RFC 5322 headers, a MIME text body, and the deliverability headers docs/plan/17 asks for.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             Pure functions over strings, so the whole of what leaves the platform is asserted
///             without a relay.
///         </b> <c>MailMessagesTests</c> reads the output byte for byte; the
///         container suite then reads the same headers back from Mailpit, which proves a real
///         receiver parses them as this class meant them.
///     </para>
///     <para>
///         <b>What every message carries, and why each header is not optional:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <c>Message-ID</c>, minted per message under the sender's domain and returned as the
///             <see cref="DispatchReceipt.ProviderMessageId" />. It is the one handle a bounce or a
///             feedback-loop report quotes back, so it is the correlation key the receipt path will
///             need — and a relay that rewrites it (SES does, to its own id) is a relay whose
///             notifications name a different id, which the owed ingestion has to map.
///         </item>
///         <item>
///             <c>List-Unsubscribe</c>, when <see cref="SmtpRelayOptions.UnsubscribeMailbox" /> is
///             set. Gmail and Yahoo require it of bulk senders since 2024 and score its absence on
///             everything else; a recipient who uses it instead of the spam button is a recipient who
///             did not cost the domain a complaint. ⚠ A <c>mailto:</c>, for the reason that member
///             gives.
///         </item>
///         <item>
///             <c>Auto-Submitted: auto-generated</c> (RFC 3834). An OTP or an alert is not written by
///             a person, and saying so is what stops every vacation responder on the internet from
///             answering the platform's <c>From</c> address.
///         </item>
///         <item>
///             <c>Date</c>, <c>MIME-Version</c>, <c>Content-Type</c> with an explicit
///             <c>charset=utf-8</c>, and a quoted-printable body. A missing <c>Date</c> is a
///             classic spam-filter signal; an unlabelled charset renders a Czech template as
///             mojibake.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Nothing a template author writes can add a header.</b> A subject with a line break
///         in it is RFC 2047-encoded as one header value rather than written as two lines, and the
///         body is quoted-printable, so <c>\r\n.\r\n</c> in a template ends no message and
///         <c>\r\nBcc:</c> in a subject adds no recipient. The transport's dot-stuffing is the other
///         half of the same rule and lives in <c>SmtpConnection</c>.
///     </para>
/// </remarks>
static class MailMessages {
    /// <summary>The soft limit a body line is folded at. RFC 2045 says 76.</summary>
    public const int MaxLineLength = 76;

    /// <summary>The line ending SMTP requires everywhere.</summary>
    public const string CrLf = "\r\n";

    // RFC 2047: an encoded word is at most 75 characters. `=?utf-8?B?` is 10, `?=` is 2, so the
    // base64 payload is at most 63 characters, which is 45 raw bytes (63 / 4 * 3 = 47, floored to a
    // multiple of three).
    const int MaxEncodedWordBytes = 45;

    /// <summary>The <c>Message-ID</c> for a message, angle brackets included.</summary>
    /// <param name="messageId">Our id for the message — <see cref="OutboundMessage.MessageId" />.</param>
    /// <param name="domain">The sender's domain, which is what makes the id globally unique.</param>
    public static string MessageIdFor(Guid messageId, string domain) =>
        string.Create(CultureInfo.InvariantCulture, $"<{messageId:N}@{domain}>");

    /// <summary>
    ///     The subject a message goes out under: its own, or its first line when a free-text send
    ///     has none.
    /// </summary>
    /// <remarks>
    ///     An email with no <c>Subject</c> is a spam signal and a blank line in an inbox. A free-text
    ///     OTP send (<c>CommunicationOtpDelivery</c> with no template) has no subject, and its body's
    ///     first line — "424242 is your code." — is what a mail client would have shown anyway.
    /// </remarks>
    public static string SubjectFor(OutboundMessage message) {
        if (!string.IsNullOrWhiteSpace(message.Subject)) {
            return message.Subject.Trim();
        }

        var body = message.Body.AsSpan().TrimStart();
        var end = body.IndexOfAny('\r', '\n');
        var line = (end < 0 ? body : body[..end]).TrimEnd();

        if (line.Length > 78) {
            line = line[..77].TrimEnd();
            return string.Concat(line, "…");
        }

        return line.Length == 0 ? "(no subject)" : line.ToString();
    }

    /// <summary>Composes the whole message — headers, blank line, body — CRLF throughout.</summary>
    /// <param name="message">What to send. The destination is already normalized.</param>
    /// <param name="relay">Where <c>From</c> and the unsubscribe mailbox come from.</param>
    /// <param name="messageId">The <c>Message-ID</c>, from <see cref="MessageIdFor" />.</param>
    /// <param name="date">When it was composed — the <c>Date</c> header.</param>
    public static string Compose(
        OutboundMessage message,
        SmtpRelayOptions relay,
        string messageId,
        DateTimeOffset date
    ) {
        var headers = new StringBuilder(1024);

        Header(
            headers,
            "Date",
            date.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)
        );
        Header(headers, "From", Mailbox(relay.FromName, relay.From));
        Header(headers, "To", string.Concat("<", message.Destination, ">"));
        Header(headers, "Subject", EncodeHeaderValue(SubjectFor(message)));
        Header(headers, "Message-ID", messageId);
        Header(headers, "MIME-Version", "1.0");
        Header(headers, "Content-Type", "text/plain; charset=utf-8");
        Header(headers, "Content-Transfer-Encoding", "quoted-printable");
        Header(headers, "Auto-Submitted", "auto-generated");

        if (relay.UnsubscribeMailbox.Length > 0) {
            Header(
                headers,
                "List-Unsubscribe",
                string.Concat("<mailto:", relay.UnsubscribeMailbox, "?subject=unsubscribe>")
            );
        }

        return headers
            .Append(CrLf)
            .Append(QuotedPrintable(message.Body))
            .ToString();
    }

    /// <summary>A <c>name-addr</c> when there is a name, an <c>angle-addr</c> when there is not.</summary>
    static string Mailbox(string name, string address) {
        if (string.IsNullOrWhiteSpace(name)) {
            return string.Concat("<", address, ">");
        }

        var trimmed = name.Trim();

        // A display name that is plain letters, digits and a few safe marks goes as an atom
        // sequence; anything else is quoted, and anything outside ASCII or with a control character
        // is an encoded word — which also covers the CR/LF injection case.
        var phrase = trimmed.All(static c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            ? trimmed
            : trimmed.All(static c => c is >= ' ' and <= '~')
                ? string.Concat(
                    "\"",
                    trimmed.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal),
                    "\""
                )
                : EncodeHeaderValue(trimmed);

        return string.Concat(phrase, " <", address, ">");
    }

    static void Header(StringBuilder into, string name, string value) =>
        into.Append(name).Append(": ").Append(Fold(name.Length + 2, value)).Append(CrLf);

    /// <summary>
    ///     A header value as RFC 5322 allows it: itself when it is printable ASCII, and an RFC 2047
    ///     <c>B</c>-encoded word sequence otherwise.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every character that is not printable ASCII — a line break included — takes the whole
    ///     value down the encoding path. That is what makes a template's subject unable to add a
    ///     header line, and it is checked by <c>MailMessagesTests</c> with a subject that tries.
    /// </remarks>
    public static string EncodeHeaderValue(string value) {
        if (value.All(static c => c is >= ' ' and <= '~')) {
            return value;
        }

        var words = new StringBuilder(value.Length * 2);
        var bytes = Encoding.UTF8.GetBytes(value);
        var offset = 0;

        while (offset < bytes.Length) {
            // Cut at most MaxEncodedWordBytes bytes, and never inside a UTF-8 sequence: back off to
            // the last byte that starts a character.
            var take = Math.Min(MaxEncodedWordBytes, bytes.Length - offset);

            while (take > 1 && offset + take < bytes.Length && (bytes[offset + take] & 0xC0) == 0x80) {
                take--;
            }

            if (words.Length > 0) {
                words.Append(' ');
            }

            words.Append("=?utf-8?B?").Append(Convert.ToBase64String(bytes, offset, take)).Append("?=");
            offset += take;
        }

        return words.ToString();
    }

    /// <summary>Folds a header value at white space so no line exceeds 78 characters.</summary>
    /// <param name="used">How many characters the header name and colon already took on the first line.</param>
    /// <param name="value">The value, already encoded.</param>
    static string Fold(int used, string value) {
        const int limit = 78;

        if (used + value.Length <= limit) {
            return value;
        }

        var folded = new StringBuilder(value.Length + 16);
        var column = used;
        var first = true;

        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (first) {
                first = false;
            } else if (column + 1 + word.Length > limit) {
                // The fold is CRLF and one space: the space is the separator the words had, moved
                // to the start of the next line, which is what an unfolding reader puts back.
                folded.Append(CrLf).Append(' ');
                column = 1;
            } else {
                folded.Append(' ');
                column++;
            }

            folded.Append(word);
            column += word.Length;
        }

        return folded.ToString();
    }

    /// <summary>
    ///     The body as quoted-printable (RFC 2045 § 6.7): CRLF line endings, soft breaks at
    ///     <see cref="MaxLineLength" />, trailing white space and <c>=</c> encoded.
    /// </summary>
    /// <remarks>
    ///     ⚠ A body line consisting of a single <c>.</c> stays a single <c>.</c> here — dot-stuffing
    ///     is the transport's job and <c>SmtpConnection.SendDataAsync</c> does it, because it is a
    ///     property of the <c>DATA</c> command rather than of the message.
    /// </remarks>
    public static string QuotedPrintable(string body) {
        var normalized = body.ReplaceLineEndings(CrLf);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var output = new StringBuilder(bytes.Length + bytes.Length / 8);
        var column = 0;

        for (var i = 0; i < bytes.Length; i++) {
            var b = bytes[i];

            if (b == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') {
                output.Append(CrLf);
                column = 0;
                i++;
                continue;
            }

            var atLineEnd = i + 1 == bytes.Length
                || (bytes[i + 1] == (byte)'\r' && i + 2 < bytes.Length && bytes[i + 2] == (byte)'\n');
            var literal = (b is >= 33 and <= 126 && b != (byte)'=') || (b is (byte)' ' or (byte)'\t' && !atLineEnd);
            var width = literal ? 1 : 3;

            // A soft line break: `=` then CRLF, invisible to the reader, keeps every line under the
            // limit. The `=` itself takes a column, hence the minus one.
            if (column + width > MaxLineLength - 1) {
                output.Append('=').Append(CrLf);
                column = 0;
            }

            if (literal) {
                output.Append((char)b);
            } else {
                output.Append('=').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }

            column += width;
        }

        return output.ToString();
    }
}
