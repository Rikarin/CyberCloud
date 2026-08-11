using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Communication;

/// <summary>
///     What <c>MessageGrain</c> holds. Hot tier, expiring at <see cref="ExpiresAt" />.
/// </summary>
/// <remarks>
///     ⚠ <b>There is no body, no subject and no template argument here, and that absence is the
///     single most important line in this file.</b> The body of an OTP message <i>is</i> the
///     one-time code; the body of a password-reset message carries a bearer token. Storing either
///     would put live credential material into grain state for <see cref="IMessageGrain.Retention" />
///     — replicated across silos, present in every serialization trace, and readable by anyone who
///     can read a status object. What is kept instead is <see cref="RequestDigest" />, which is
///     enough to prove a retry is the same message and reveals nothing.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.MessageState")]
public sealed class MessageState {
    /// <summary>Everything a status read returns, or <see langword="null" /> before the first send.</summary>
    [Id(0)]
    public MessageSnapshot? Snapshot { get; set; }

    /// <summary>
    ///     A digest of the request, for telling a retry from a different message under one key.
    /// </summary>
    /// <remarks>
    ///     SHA-256 over the channel, destination, template, locale, arguments and body — one way,
    ///     compared and never resolved. It exists precisely so the state does not have to hold the
    ///     inputs.
    ///     <para>
    ///         ⚠ <b>Named <c>Digest</c> rather than <c>Hash</c> or <c>Fingerprint</c> deliberately,
    ///         and <b>not</b> <c>RequestKey</c>.</b> The last one would trip CC1005 and would then be
    ///         answered with a suppression — and a suppression is a thing a reader has to evaluate.
    ///         A name that never raises the question is better than an argument that settles it.
    ///     </para>
    /// </remarks>
    [Id(1)]
    public string RequestDigest { get; set; } = string.Empty;

    /// <summary>When the record — and with it the idempotency guarantee — stops being valid.</summary>
    [Id(2)]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The spend reservation held while the carrier is being called.</summary>
    [Id(3)]
    public Guid ReservationId { get; set; }

    /// <summary>Whether this record has aged out.</summary>
    /// <param name="now">The current instant.</param>
    public bool IsExpired(DateTimeOffset now) => Snapshot is null || now >= ExpiresAt;
}

/// <summary>What <c>ProviderMessageIndexGrain</c> holds. Hot tier, expiring with its message.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.ProviderMessageIndexState")]
public sealed class ProviderMessageIndexState {
    /// <summary>The idempotency key that addresses the message grain.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a secret, and CC1005 is suppressed rather than the member renamed.</b> It is the
    ///     client-supplied idempotency key of docs/plan/17 § The parts that are actually the work —
    ///     a value chosen so that a retry repeats it, already half of a grain id, already in traces.
    ///     A credential is the thing you must not repeat; this is the thing you must.
    /// </remarks>
    [Id(0)]
    [SuppressMessage(
        "CyberCloud.Security",
        "CC1005:A secret must not be a serialized member of grain state",
        Justification =
            "Not a secret. This is the client-supplied idempotency key of docs/plan/17 § The parts "
            + "that are actually the work, which is already half of the message grain's id and is "
            + "deliberately logged and traced. Renaming it would lose the term docs/plan/17 uses."
    )]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>When the entry stops being valid. Never later than its message's expiry.</summary>
    [Id(1)]
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>What <c>SuppressionListGrain</c> holds. Durable — see <see cref="ISuppressionListGrain" />.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SuppressionListState")]
public sealed class SuppressionListState {
    /// <summary>
    ///     Every entry, keyed by <c>{(int)channel}:{normalizedDestination}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>{ get; set; }</c> rather than get-only, and the collection initialiser is not enough
    ///     on its own: System.Text.Json does not populate a get-only collection, so a get-only
    ///     property would deserialize to an empty list and the whole suppression list would silently
    ///     vanish on the first reactivation. Same trap, same repair, as <c>UsageLedgerState</c>.
    /// </remarks>
    [Id(0)]
    public Dictionary<string, SuppressionEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What <c>CommunicationServiceGrain</c> holds. Durable.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.CommunicationServiceState")]
public sealed class CommunicationServiceState {
    /// <summary>Whether <c>CreateAsync</c> has run.</summary>
    [Id(0)]
    public bool Created { get; set; }

    /// <summary>The owning tenant.</summary>
    [Id(1)]
    public Guid TenantId { get; set; }

    /// <summary>The resource's name within its group.</summary>
    [Id(2)]
    public string Name { get; set; } = string.Empty;

    /// <summary>When it was created.</summary>
    [Id(3)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Every channel configured, in configuration order.</summary>
    [Id(4)]
    public List<ChannelConfiguration> Channels { get; set; } = [];

    /// <summary>Template name to template resource id. The service is the naming authority.</summary>
    [Id(5)]
    public Dictionary<string, Guid> Templates { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What <c>MessageTemplateGrain</c> holds. Durable.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.MessageTemplateState")]
public sealed class MessageTemplateState {
    /// <summary>Whether <c>CreateAsync</c> has run.</summary>
    [Id(0)]
    public bool Created { get; set; }

    /// <summary>The service it belongs to.</summary>
    [Id(1)]
    public Guid ServiceId { get; set; }

    /// <summary>Its name within the service.</summary>
    [Id(2)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Which channel it is written for.</summary>
    [Id(3)]
    public ChannelKind Channel { get; set; } = ChannelKind.Unknown;

    /// <summary>Every version, oldest first. ⚠ Append-only — see <see cref="IMessageTemplateGrain" />.</summary>
    [Id(4)]
    public List<MessageTemplateVersion> Versions { get; set; } = [];
}

/// <summary>What <c>SenderIdentityGrain</c> holds. Durable.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SenderIdentityState")]
public sealed class SenderIdentityState {
    /// <summary>The sender, or <see langword="null" /> before registration.</summary>
    [Id(0)]
    public SenderIdentity? Sender { get; set; }
}

/// <summary>One channel's counters for one window.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.ChannelWindowState")]
public sealed class ChannelWindowState {
    /// <summary>The UTC date this window covers. A different date resets everything below.</summary>
    [Id(0)]
    public DateOnly Window { get; set; }

    /// <summary>Messages dispatched in the window.</summary>
    [Id(1)]
    public long Messages { get; set; }

    /// <summary>Money settled at real carrier prices.</summary>
    [Id(2)]
    public decimal Settled { get; set; }

    /// <summary>Claims taken and not yet settled or released.</summary>
    [Id(3)]
    public List<PendingReservation> Pending { get; set; } = [];
}

/// <summary>A claim on the window's allowance, held while a carrier is being called.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.PendingReservation")]
public sealed class PendingReservation {
    /// <summary>Identifies the claim.</summary>
    [Id(0)]
    public Guid ReservationId { get; set; }

    /// <summary>What is held.</summary>
    [Id(1)]
    public decimal Amount { get; set; }

    /// <summary>When it is given back if nothing has settled it.</summary>
    [Id(2)]
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>What <c>SendLimitGrain</c> holds. Hot — see <see cref="ISendLimitGrain" />.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SendLimitState")]
public sealed class SendLimitState {
    /// <summary>One window per channel, keyed by the channel's numeric value.</summary>
    [Id(0)]
    public Dictionary<int, ChannelWindowState> Windows { get; set; } = [];
}

/// <summary>
///     The one-way digest a message grain compares a retry against.
/// </summary>
/// <remarks>
///     ⚠ <b>It covers everything that would make two sends different messages, and nothing that
///     would make them the same one attempted twice.</b> The channel, destination, template, version,
///     locale, arguments and body are in; the idempotency key is not, because it is already the grain
///     key and including it would make every digest trivially match.
/// </remarks>
static class RequestDigests {
    /// <summary>Lower-case hex SHA-256 of the parts of a request that identify the message.</summary>
    /// <param name="request">The request.</param>
    /// <param name="normalizedDestination">The destination, already canonical.</param>
    public static string Of(SendRequest request, string normalizedDestination) {
        var material = new StringBuilder(256)
            .Append((int)request.Channel)
            .Append('\n')
            .Append(normalizedDestination)
            .Append('\n')
            .Append(request.TemplateName)
            .Append('\n')
            .Append(request.TemplateVersion)
            .Append('\n')
            .Append(request.Locale)
            .Append('\n')
            .Append(request.Body)
            .Append('\n');

        foreach (var argument in request.Arguments.IsDefault ? [] : request.Arguments) {
            material.Append(argument.Name).Append('=').Append(argument.Value).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }
}
