using System.Globalization;

namespace CyberCloud.Communication.Providers.Smtp;

/// <summary>How the connection to the relay is protected.</summary>
public enum SmtpSecurity {
    /// <summary>
    ///     Connect in the clear and upgrade with <c>STARTTLS</c> before anything else is said. The
    ///     submission port, 587, and the default. ⚠ A relay that does not offer <c>STARTTLS</c> is
    ///     refused rather than spoken to in the clear.
    /// </summary>
    StartTls = 0,

    /// <summary>TLS from the first byte — the SMTPS port, 465. What Amazon SES calls "TLS Wrapper".</summary>
    ImplicitTls,

    /// <summary>
    ///     No TLS at all. ⚠ For a relay on the same machine or the same pod and for nothing else:
    ///     <see cref="SmtpRelayOptions.Validate" /> refuses a password over this mode, because a
    ///     password in the clear is a password on the network.
    /// </summary>
    None
}

/// <summary>
///     The outbound relay the platform's email goes through — <c>CyberCloud:Communication:Smtp</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             One relay, configured on the silo, and the shape is deliberately the smallest one
///             two very different relays share.
///         </b> Amazon SES's SMTP endpoint
///         (<c>email-smtp.{region}.amazonaws.com:587</c>, <c>STARTTLS</c>, an SMTP credential pair)
///         and a Postfix relay in the same cluster (<c>postfix.mail.svc:25</c>, no auth on a private
///         network) both speak exactly this: a host, a port, one of three TLS arrangements, and an
///         optional <c>AUTH</c>. docs/plan/17 § The channel abstraction names "Amazon SES/our own
///         Postfix" as the email carrier, and this is the client side of both. On a development run
///         it is Mailpit at <c>localhost:1025</c> with <see cref="SmtpSecurity.None" /> —
///         <c>CyberCloudTopology</c>.
///     </para>
///     <para>
///         ⚠ <b>The platform's account, not a tenant's.</b> This is the relay the platform's own
///         OTPs, alerts and invoices leave through, and a tenant channel on
///         <c>CredentialMode.PlatformAccount</c> shares it. A tenant's own SMTP credentials
///         (<c>CredentialMode.TenantAccount</c>) are a different relay per tenant, whose host is
///         not a thing <c>ChannelConfiguration</c> can carry yet — <c>SmtpChannelProvider</c> refuses
///         that mode and says so.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The credential is configuration and not a vault handle, and that is the one place
///             this deviates from <c>IChannelProvider</c>'s first rule.
///         </b> The rule exists for a
///         <i>tenant's</i> credential, which is theirs and reaches this module as a
///         <c>CarrierSecretRef</c>. The relay credential is the platform's own, lives in the silo's
///         environment beside its database connection strings, and is read once at composition.
///         Moving it behind a handle is a change to this one type and to the line in
///         <c>SiloComposition</c> that binds it.
///     </para>
/// </remarks>
public sealed class SmtpRelayOptions {
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "CyberCloud:Communication:Smtp";

    /// <summary>The relay's host name. Empty means no relay, and no email carrier is registered.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The relay's port. 587 for submission, 465 for implicit TLS, 25 or 1025 for a local relay.</summary>
    public int Port { get; set; } = 587;

    /// <summary>How the connection is protected.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>The <c>AUTH</c> user name, or empty for a relay that trusts the network.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>The <c>AUTH</c> password. Set with <see cref="Username" /> or not at all.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     The address every message is sent from — <c>From</c> and the envelope sender. ⚠ Its
    ///     domain is the one SPF, DKIM and DMARC have to be right for (docs/plan/17
    ///     § Deliverability), and the domain <c>Message-ID</c>s are minted under.
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The display name beside <see cref="From" />, or empty for the bare address.</summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>
    ///     The mailbox a <c>List-Unsubscribe</c> header points at, or empty to send no such header.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A <c>mailto:</c> and not an <c>https:</c> one-click URL, and the reason is honesty
    ///         about what exists.
    ///     </b> RFC 8058's one-click needs an endpoint that accepts the
    ///     <c>POST</c>, and the platform has no ingress a carrier or a mailbox provider can reach
    ///     (<c>charts/bundle/bundle.yaml § owed</c>, <c>communication-receipts-have-no-ingress</c>).
    ///     A <c>mailto:</c> is valid to every major receiver and is what a Postfix relay can route
    ///     into a mailbox somebody reads; the one-click form lands with that ingress.
    /// </remarks>
    public string UnsubscribeMailbox { get; set; } = string.Empty;

    /// <summary>How long one SMTP exchange may take, connect to <c>QUIT</c>.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a relay is named at all. The switch <c>SiloComposition</c> reads.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    /// <summary>The domain of <see cref="From" /> — what <c>Message-ID</c>s are minted under.</summary>
    public string FromDomain {
        get {
            var at = From.LastIndexOf('@');
            return at < 0 ? string.Empty : From[(at + 1)..];
        }
    }

    /// <summary>
    ///     Checks the section is usable, naming the first thing that is not.
    /// </summary>
    /// <returns>Success, or a failure naming the key and what a valid value looks like.</returns>
    /// <remarks>
    ///     ⚠ Called at composition so a misconfigured relay fails the silo's start rather than its
    ///     first send — the same trade <c>ObjectStorageOptions</c> makes with an insecure endpoint. A
    ///     silo that starts and refuses every email at 03:00 is worse than one that does not start
    ///     and says why.
    /// </remarks>
    public Result Validate() {
        if (!IsConfigured) {
            return Result.Failure(ErrorCode.InvalidRequestBody, $"{SectionName}:Host is empty, so there is no relay to send through.");
        }

        if (Port is < 1 or > 65535) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"{SectionName}:Port is {Port.ToString(CultureInfo.InvariantCulture)}; a TCP port is 1 to 65535."
            );
        }

        if (!Enum.IsDefined(Security)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"{SectionName}:Security is not one of StartTls, ImplicitTls or None."
            );
        }

        if (string.IsNullOrWhiteSpace(Username) != string.IsNullOrWhiteSpace(Password)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"{SectionName}:Username and {SectionName}:Password go together: set both for a relay that "
                + "authenticates, or neither for one that trusts the network."
            );
        }

        if (!string.IsNullOrWhiteSpace(Username) && Security == SmtpSecurity.None) {
            return Result.Failure(
                ErrorCode.PolicyViolation,
                $"{SectionName}:Security is None and a password is set. AUTH over a connection with no TLS "
                + "puts the relay password on the network in the clear; use StartTls or ImplicitTls, or drop "
                + "the credential for a relay that trusts the network."
            );
        }

        if (MailAddresses.Check(From).TryGetError(out var badFrom)) {
            return Result.Failure(badFrom.Code, $"{SectionName}:From: {badFrom.Message}");
        }

        if (UnsubscribeMailbox.Length > 0 && MailAddresses.Check(UnsubscribeMailbox).TryGetError(out var badUnsubscribe)) {
            return Result.Failure(badUnsubscribe.Code, $"{SectionName}:UnsubscribeMailbox: {badUnsubscribe.Message}");
        }

        if (Timeout <= TimeSpan.Zero) {
            return Result.Failure(ErrorCode.InvalidRequestBody, $"{SectionName}:Timeout must be positive.");
        }

        return Result.Success;
    }
}
