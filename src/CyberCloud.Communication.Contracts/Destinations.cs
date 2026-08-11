using System.Collections.Frozen;
using System.Collections.Immutable;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     The canonical form of a destination, for the suppression list and for the wire.
/// </summary>
/// <remarks>
///     ⚠ <b>A suppression list that does not normalize is a suppression list that can be walked
///     around.</b> docs/plan/17 § The parts that are actually the work makes suppression
///     <i>"honoured before dispatch"</i>, and a recipient who sent <c>STOP</c> from
///     <c>+420 777 123 456</c> has not consented to <c>+420777123456</c>. The same argument
///     <see cref="GrainKeys.NormalizeEmail" /> makes about two spellings of one account, with the
///     stake moved from "a duplicate sign-up" to "a message somebody said not to send".
/// </remarks>
public static class Destinations {
    /// <summary>
    ///     The canonical form of <paramref name="destination" /> for
    ///     <paramref name="channel" />, or a failure saying why it is not addressable.
    /// </summary>
    /// <param name="channel">Which channel decides the rule — email addresses and numbers differ.</param>
    /// <param name="destination">The address or number, as the caller typed it.</param>
    /// <returns>
    ///     The form every comparison, key and stored entry uses. ⚠ Store this, never the input:
    ///     writing an unnormalized address beside a normalized suppression entry is how an
    ///     opt-out stops matching.
    /// </returns>
    public static Result<string> Normalize(ChannelKind channel, string? destination) {
        if (string.IsNullOrWhiteSpace(destination)) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                "A send needs a destination. This is the delivery path — there is no default."
            );
        }

        return channel switch {
            ChannelKind.Email => NormalizeEmail(destination),
            ChannelKind.Sms or ChannelKind.WhatsApp or ChannelKind.Voice => NormalizePhone(destination),

            // ⚠ A push token is opaque to us and case-sensitive to APNs and FCM. Trimming is the
            // only safe transformation: anything else would break a live device registration, and
            // there is no second spelling of a token for a recipient to have consented from.
            ChannelKind.Push => Result<string>.Success(destination.Trim()),
            _ => Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{channel} is not a channel a destination can be normalized for. "
                + "ChannelKind.Unknown is the zero value a default-constructed wire type carries."
            )
        };
    }

    /// <summary>
    ///     ⚠ Defers to <see cref="GrainKeys.NormalizeEmail" /> rather than repeating it. Two
    ///     canonicalisers for one address shape is two answers, and the one that differs is the one
    ///     an opt-out was recorded under.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The error code is re-stated and the message is not.</b>
    ///     <see cref="GrainKeys.NormalizeEmail" /> fails with
    ///     <see cref="ErrorCode.InvalidGrainKey" />, whose own remarks say it "names the mechanism,
    ///     not the field" and that a user-facing validator should say so in its own words. A tenant
    ///     sending to a malformed address has not touched a grain key and should not be told about
    ///     one — but the explanation of <i>what</i> is wrong with the address is that function's and
    ///     is kept verbatim.
    /// </remarks>
    static Result<string> NormalizeEmail(string destination) {
        var normalized = GrainKeys.NormalizeEmail(destination);

        return normalized.TryGetError(out var error)
            ? Result<string>.Failure(ErrorCode.InvalidRequestBody, error.Message)
            : normalized;
    }

    /// <summary>
    ///     E.164: a leading <c>+</c> and up to fifteen digits, with every separator a human types
    ///     removed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A shape check, not a routing check.</b> Whether <c>+420777123456</c> is a live
    ///         handset is the carrier's answer and arrives as a delivery receipt. What this has to
    ///         guarantee is the property the suppression list rests on: <b>two spellings of one
    ///         number produce one string</b>. Spaces, hyphens, dots, and round brackets are the
    ///         separators every keypad and every pasted contact card produces, and stripping exactly
    ///         those merges the spellings and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A missing <c>+</c> is refused rather than guessed.</b> <c>777123456</c> is a
    ///         national number and which nation is not in the string. Inferring one from the tenant's
    ///         address would send an SMS to a stranger in a country the tenant does not operate in,
    ///         and it would do it to the same wrong number every time, consistently, which is the
    ///         failure mode that survives testing.
    ///     </para>
    /// </remarks>
    static Result<string> NormalizePhone(string destination) {
        var trimmed = destination.AsSpan().Trim();
        Span<char> buffer = stackalloc char[trimmed.Length + 1];
        var length = 0;
        var digits = 0;

        for (var i = 0; i < trimmed.Length; i++) {
            var c = trimmed[i];

            // Space, hyphen-minus, full stop, brackets, and the three look-alikes a pasted
            // contact card carries: no-break space, non-breaking hyphen, en dash.
            if (c is ' ' or '-' or '.' or '(' or ')' or '\u00A0' or '\u2011' or '\u2013') {
                continue;
            }

            if (c == '+') {
                if (length != 0) {
                    return Invalid(destination, "'+' appears somewhere other than the front");
                }

                buffer[length++] = c;
                continue;
            }

            if (c is < '0' or > '9') {
                return Invalid(
                    destination,
                    $"it contains '{c}', which is neither a digit nor a separator E.164 allows"
                );
            }

            buffer[length++] = c;
            digits++;
        }

        if (length == 0 || buffer[0] != '+') {
            return Invalid(
                destination,
                "it does not start with '+'. E.164 needs a country code, and guessing one sends the "
                + "message to a real handset in the wrong country"
            );
        }

        if (digits is < 4 or > 15) {
            return Invalid(
                destination,
                $"it has {digits.ToString(System.Globalization.CultureInfo.InvariantCulture)} digits; "
                + "E.164 allows at most 15, and fewer than 4 is not a number"
            );
        }

        return Result<string>.Success(new(buffer[..length]));
    }

    static Result<string> Invalid(string destination, string why) =>
        Result<string>.Failure(
            ErrorCode.InvalidRequestBody,
            $"'{destination}' is not a destination this channel can reach: {why}."
        );
}

/// <summary>
///     The keywords that mean "stop sending to me" — docs/plan/17 § The parts that are actually the
///     work: <i><c>STOP</c> handling is legally required in most jurisdictions</i>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Where the list comes from.</b> The English set is the one the US carriers and the
///         CTIA short-code handbook require every campaign to honour, and it is the set every
///         aggregator implements. The rest are the same intent in the languages the platform ships
///         a portal in; a recipient who writes <c>ODHLÁSIT</c> has opted out whether or not a
///         regulator has written it down.
///     </para>
///     <para>
///         ⚠ <b>Matching is deliberately generous, and generous is the safe direction here.</b> A
///         false positive suppresses somebody who wrote "stop" meaning something else, and they
///         re-subscribe. A false negative keeps messaging somebody who asked us not to, which is a
///         regulatory finding and, in the US, a per-message statutory penalty. When the two errors
///         are that asymmetric, the threshold does not belong in the middle.
///     </para>
///     <para>
///         ⚠ <b>This is a floor, not a compliance program.</b> We are a broker, not a carrier
///         (docs/plan/17 § The channel abstraction): per-country consent rules, quiet hours and
///         mandatory help text remain the tenant's obligation. What the platform guarantees is that
///         a recognised stop word suppresses <i>before</i> anything else happens to the message.
///     </para>
/// </remarks>
public static class StopKeywords {
    /// <summary>
    ///     Every recognised keyword, upper case. ⚠ Append-only in practice: removing one un-opts-out
    ///     everybody who used it.
    /// </summary>
    public static ImmutableArray<string> All { get; } = [
        // The CTIA set.
        "STOP",
        "STOPALL",
        "UNSUBSCRIBE",
        "CANCEL",
        "END",
        "QUIT",
        "OPTOUT",
        "OPT-OUT",
        "REVOKE",
        // Czech, German, French, Spanish — the portal's shipping locales.
        "ODHLASIT",
        "ODHLÁSIT",
        "STOPP",
        "ABMELDEN",
        "ARRET",
        "ARRÊT",
        "DESABONNER",
        "BAJA",
        "PARAR"
    ];

    static readonly FrozenSet<string> Lookup = All.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a handset puts after a keyword. Stripped before the whole-body match.</summary>
    static readonly char[] TrailingPunctuation = ['.', '!', '?', ',', ';', ':'];

    /// <summary>
    ///     Whether an inbound body asks us to stop, and which keyword said so.
    /// </summary>
    /// <param name="body">The inbound message, as the recipient typed it.</param>
    /// <param name="keyword">The keyword matched, upper case, or empty.</param>
    /// <returns><c>true</c> when the message is an opt-out and the address must be suppressed.</returns>
    /// <remarks>
    ///     ⚠ <b>The whole body must be the keyword, once punctuation and spacing are removed.</b>
    ///     Scanning for the word anywhere would opt out the recipient who replied
    ///     <c>"please don't stop sending these"</c>, and the carriers' own rule is the whole-message
    ///     one. The trailing punctuation strip is what catches <c>"STOP."</c> and <c>"Stop!"</c>,
    ///     which is most of what real handsets send.
    /// </remarks>
    public static bool IsStop(string? body, out string keyword) {
        keyword = string.Empty;

        if (string.IsNullOrWhiteSpace(body)) {
            return false;
        }

        var candidate = body.AsSpan().Trim().TrimEnd(TrailingPunctuation).Trim();
        if (candidate.IsEmpty) {
            return false;
        }

        // One allocation, and only for a body short enough to be a keyword. An inbound reply that is
        // a paragraph is not an opt-out and does not need to be examined further.
        if (candidate.Length > 32) {
            return false;
        }

        var text = new string(candidate);
        if (!Lookup.TryGetValue(text, out var matched)) {
            return false;
        }

        keyword = matched;
        return true;
    }
}
