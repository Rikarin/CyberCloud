using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     The within-tenant grain keys this module addresses, built through
///     <see cref="GrainKeys" /> and never by string surgery.
/// </summary>
/// <remarks>
///     <para>
///         ADR-002 (docs/plan/02 § ADR-002) makes <see cref="GrainKeys" /> <i>"the only type allowed
///         to build the within-tenant part"</i>, and every method here ends in a call to it. This
///         type is in <c>.Contracts</c> rather than in the implementation assembly — unlike
///         <c>MeteringGrainKeys</c> — because the callers are outside the module: identity addresses
///         a message grain to send an OTP, and the gateway addresses one to answer a status read.
///     </para>
///     <para>
///         ⚠ <b>Every grain here is keyed <c>res/{guid:N}</c>, and four of the five guids are
///         derived rather than allocated.</b> <see cref="GrainKeys" /> accepts a closed set of key
///         shapes; adding <c>msg/{id}</c> would be a change to <see cref="GrainKeys" /> in
///         <c>CyberCloud.Core</c>, reviewed like a schema change, which is the same wall
///         <c>MeteringGrainKeys</c> stopped at and made the same call about. Two of the five are
///         genuine resources with genuine ids — the service and its templates and senders are
///         resources in the docs/plan/06 sense, with paths, ARM-shaped names and index entries. The
///         other two — the message and the provider-id index — are not resources, and they borrow
///         the shape.
///     </para>
///     <para>
///         ⚠ <b>What that borrowing costs, stated rather than hidden.</b> A repair tool that parsed
///         a key and routed it by <see cref="GrainKeyKind" /> would send a message key to
///         <c>IResourceGrain</c>. Nothing does that today, and the alternative — a new key shape —
///         is a change to the type every module in the platform parses keys through, made by a
///         module that is not yet wired into a host. The honest follow-up is a <c>msg/{id:N}</c>
///         shape in <see cref="GrainKeys" />, and it is a Core change with a Core review.
///     </para>
///     <para>
///         <b>Why derived rather than allocated.</b> <see cref="Message" /> is the whole idempotency
///         mechanism: a retry carrying the same key computes the same guid, addresses the same
///         activation, and finds the send already recorded. No index grain, no lookup, no window
///         where two callers both miss the index and both send. docs/plan/17 § The parts that are
///         actually the work asks for <i>"per-message status, retry with backoff, and idempotency in
///         one place"</i>, and one place is what a deterministic key buys.
///     </para>
/// </remarks>
public static class CommunicationGrainKeys {
    /// <summary>
    ///     The communication service resource — <c>ICommunicationServiceGrain</c>,
    ///     <c>ISuppressionListGrain</c> and <c>ISendLimitGrain</c>, which are three grain types on
    ///     one key.
    /// </summary>
    /// <param name="serviceId">
    ///     The <c>CyberCloud.Communication/services/{name}</c> resource's GUID, as
    ///     <c>ResourceId.Id</c> spells it.
    /// </param>
    /// <remarks>
    ///     Orleans addresses an activation by (grain type, key), so the three are distinct grains
    ///     that name one service — the same arrangement metering uses for the sampler, the rollup
    ///     and the ledger over <c>sub/{subscriptionId:N}</c>.
    /// </remarks>
    public static string Service(Guid serviceId) => GrainKeys.Resource(serviceId);

    /// <summary>A template resource — <c>IMessageTemplateGrain</c>.</summary>
    /// <param name="templateId">The <c>services/{service}/templates/{name}</c> child resource's GUID.</param>
    public static string Template(Guid templateId) => GrainKeys.Resource(templateId);

    /// <summary>A sender resource — <c>ISenderIdentityGrain</c>.</summary>
    /// <param name="senderId">The <c>services/{service}/senders/{name}</c> child resource's GUID.</param>
    public static string Sender(Guid senderId) => GrainKeys.Resource(senderId);

    /// <summary>
    ///     The message a service and an idempotency key name together — <c>IMessageGrain</c>.
    /// </summary>
    /// <param name="serviceId">The service the send goes through.</param>
    /// <param name="idempotencyKey">
    ///     The caller's key. ⚠ Trimmed and compared ordinally, and <b>not</b> case-folded: a caller
    ///     that sends <c>"Otp-42"</c> and retries with <c>"otp-42"</c> meant two things or made a
    ///     mistake, and folding them would silently pick one reading. Empty is rejected — a send
    ///     with no key is a send that cannot be retried safely, and <c>IMessageGrain.SendAsync</c>
    ///     refuses it too.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="idempotencyKey" /> is blank.</exception>
    public static string Message(Guid serviceId, string idempotencyKey) {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) {
            throw new ArgumentException(
                "A send needs an idempotency key — docs/plan/17 § The parts that are actually the "
                + "work. Without one there is no grain to address twice, so a retry after a timeout "
                + "sends a second message.",
                nameof(idempotencyKey)
            );
        }

        return GrainKeys.Resource(Derive("message", serviceId, idempotencyKey.Trim()));
    }

    /// <summary>
    ///     The index from a carrier's message id back to ours — <c>IProviderMessageIndexGrain</c>.
    /// </summary>
    /// <param name="serviceId">
    ///     The service whose webhook the receipt arrived on. ⚠ In the derivation so that two tenants
    ///     whose carriers happen to mint the same id never share an index entry — carrier ids are
    ///     unique per account, not per planet.
    /// </param>
    /// <param name="providerMessageId">The carrier's id, verbatim.</param>
    /// <exception cref="ArgumentException"><paramref name="providerMessageId" /> is blank.</exception>
    public static string ProviderMessage(Guid serviceId, string providerMessageId) {
        if (string.IsNullOrWhiteSpace(providerMessageId)) {
            throw new ArgumentException(
                "A receipt with no provider message id correlates to nothing. Drop it before "
                + "reaching for a grain key.",
                nameof(providerMessageId)
            );
        }

        return GrainKeys.Resource(Derive("provider", serviceId, providerMessageId.Trim()));
    }

    /// <summary>
    ///     A name-based GUID over a domain, a service and a name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The construction is RFC 9562 § 5.5's, with SHA-256 in place of SHA-1: hash the
    ///         namespace and the name, take the first sixteen bytes, and stamp the version and
    ///         variant bits. Version <c>8</c> is the RFC's <i>custom</i> version, which is the honest
    ///         label — this is not v5, because v5 is defined to use SHA-1.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The domain string is what keeps two derivations apart.</b> Without it, a service
    ///         whose idempotency key happened to equal a carrier's message id would produce one guid
    ///         for two different grains. It is prefixed with its own length so that
    ///         <c>("message", "a|b")</c> and <c>("messagea", "|b")</c> cannot collide by re-cutting —
    ///         the same prefix-free argument <c>GrainKeys.EmailIndex</c> makes about writing the
    ///         tenant id in fixed-width form.
    ///     </para>
    /// </remarks>
    static Guid Derive(string domain, Guid serviceId, string name) {
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"cybercloud.communication\n{domain.Length}\n{domain}\n{serviceId:N}\n{name}"
        );

        Span<byte> digest = stackalloc byte[32];
        _ = SHA256.HashData(Encoding.UTF8.GetBytes(material), digest);

        Span<byte> id = stackalloc byte[16];
        digest[..16].CopyTo(id);

        // RFC 9562 § 4.1-4.2: version in the high nibble of octet 6, variant in the top bits of
        // octet 8. Version 8 is "custom", which is what a SHA-256 name-based id honestly is.
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        // ⚠ bigEndian: true. `new Guid(ReadOnlySpan<byte>)` defaults to the little-endian .NET
        // layout, which byte-swaps the first three fields — so the version and variant bits stamped
        // above would land on different octets and the guid would not be the one just constructed.
        return new(id, bigEndian: true);
    }
}
