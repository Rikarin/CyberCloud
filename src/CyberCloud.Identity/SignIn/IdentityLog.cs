using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.SignIn;

/// <summary>
///     Every log line the identity module emits, and the reason they are all in one file.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/11 § Auditing: "no email, name or IP in a log <i>message</i>. They go in
///         structured fields, which are subject to the retention and redaction policy; a message
///         string is not."</b> That rule is easy to state and very easy to break by accident — an
///         interpolated string in a <c>LogWarning</c> looks identical at the call site to a message
///         template, and the difference is whether the address ends up in a field a redaction policy
///         can reach or baked into a line nobody can un-bake.
///     </para>
///     <para>
///         <b>The mechanism, and why it is a file rather than a convention.</b> Every template here
///         is a compile-time constant, and the only values that can reach one are the arguments it
///         names. Because they are <see cref="LoggerMessageAttribute" />-generated, adding an
///         interpolated string is not even possible — the generator requires a constant — so a call
///         site cannot smuggle an address in past the template. What a call site <i>can</i> do is
///         pass an address as an argument to a placeholder, which is why the invariant below is
///         about the arguments and why <c>PiiNeverReachesALogMessageTests</c> drives the real
///         sign-in path with a distinctive address and asserts it appears in no formatted message.
///     </para>
///     <para>
///         ⚠ <b>Every placeholder in every template above is a GUID, an enum or a digest.</b> That is
///         the invariant, and it is what the test checks: a template may name as many fields as it
///         likes, so long as none of them can hold an address, a person's name or an IP. Adding a
///         <c>{Email}</c> placeholder would compile, would look like good structured logging, and
///         would put the address into the rendered message on every sink that renders one.
///     </para>
///     <para>
///         ⚠ <b>The user id is not PII by this rule and is in the messages.</b> A GUID is a handle
///         that means nothing without the directory, and an audit trail with no subject at all is
///         not an audit trail. docs/plan/11 § Auditing names email, name and IP, and stops there.
///     </para>
/// </remarks>
public static partial class IdentityLog {
    /// <summary>A sign-in attempt was refused, for whatever reason.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="reason">The internal reason, which never reaches the caller.</param>
    /// <param name="identifierDigest">
    ///     The lockout key's digest — enough to correlate attempts against one address without
    ///     writing the address.
    /// </param>
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Sign-in refused for tenant {TenantId}, identifier {IdentifierDigest}: {Reason}."
    )]
    public static partial void SignInRefused(
        ILogger logger,
        Guid tenantId,
        string reason,
        string identifierDigest
    );

    /// <summary>A sign-in succeeded.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Who.</param>
    /// <param name="sessionId">The session opened.</param>
    /// <param name="method">How they authenticated.</param>
    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Sign-in succeeded for user {UserId} in tenant {TenantId}: session {SessionId} by {Method}."
    )]
    public static partial void SignInSucceeded(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        AuthenticationMethod method
    );

    /// <summary>An attempt arrived while the identifier was locked out.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="identifierDigest">The lockout key's digest.</param>
    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Sign-in refused before any lookup: identifier {IdentifierDigest} in tenant {TenantId} is locked out."
    )]
    public static partial void LockedOut(ILogger logger, Guid tenantId, string identifierDigest);

    /// <summary>
    ///     A refresh token was presented twice. ⚠ The one line in this module that should page
    ///     somebody if it appears in volume.
    /// </summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="sessionId">The session, now revoked along with its whole chain.</param>
    /// <param name="userId">Whose session it was.</param>
    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Refresh reuse detected on session {SessionId} for user {UserId} in tenant {TenantId}. The session and its entire chain are revoked."
    )]
    public static partial void RefreshReuseDetected(
        ILogger logger,
        Guid tenantId,
        Guid sessionId,
        Guid userId
    );

    /// <summary>A credential was enrolled, changed or removed.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Whose credential.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="change">What happened to it — <c>enrolled</c>, <c>removed</c>, <c>replaced</c>.</param>
    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Information,
        Message = "Credential {Kind} {Change} for user {UserId} in tenant {TenantId}."
    )]
    public static partial void CredentialChanged(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        CredentialKind kind,
        string change
    );

    /// <summary>A sign-up or reset was requested for an address that has no account.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="identifierDigest">The digest of the address asked about.</param>
    /// <remarks>
    ///     ⚠ Recorded because the caller is told nothing — docs/plan/11 § Credentials makes the
    ///     response identical either way, so this line is the <i>only</i> place the distinction
    ///     exists. It is also the signal that somebody is enumerating, which is the thing that
    ///     matters operationally.
    /// </remarks>
    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Information,
        Message = "A request named an address with no account: identifier {IdentifierDigest} in tenant {TenantId}. The caller was told nothing."
    )]
    public static partial void UnknownAddressProbed(ILogger logger, Guid tenantId, string identifierDigest);

    /// <summary>A self-serve sign-up was requested. ⚠ No address, and no answer about one.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <remarks>
    ///     Deliberately carries neither the address nor whether it was free.
    ///     <see cref="UnknownAddressProbed" /> is the line that records the distinction, and it
    ///     records it as a digest — so the two together let an operator see enumeration pressure
    ///     without either line holding an address.
    /// </remarks>
    [LoggerMessage(
        EventId = 1106,
        Level = LogLevel.Information,
        Message = "A self-serve sign-up was requested in tenant {TenantId}. The caller was told nothing about the address."
    )]
    public static partial void SignUpRequested(ILogger logger, Guid tenantId);

    /// <summary>A passkey assertion challenge could not be built.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="reason">The library's reason, which never reaches the caller.</param>
    /// <remarks>
    ///     ⚠ Not the same thing as "that address has no passkey", which is not an error and must not
    ///     be logged as one — an address with no account gets a real discoverable-credential
    ///     challenge. Reaching this line means the WebAuthn library itself refused, which is a
    ///     configuration fault (a relying-party id that does not match the origin, most often).
    /// </remarks>
    [LoggerMessage(
        EventId = 1107,
        Level = LogLevel.Warning,
        Message = "A passkey assertion challenge could not be built in tenant {TenantId}: {Reason}."
    )]
    public static partial void PasskeyChallengeRefused(ILogger logger, Guid tenantId, string reason);

    /// <summary>A passkey assertion was refused.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="reason">
    ///     The internal reason, which never reaches the caller. ⚠ Includes the WebAuthn library's own
    ///     message, which distinguishes a wrong origin from a bad signature — useful here and an
    ///     oracle in a response body.
    /// </param>
    [LoggerMessage(
        EventId = 1108,
        Level = LogLevel.Information,
        Message = "A passkey assertion was refused in tenant {TenantId}: {Reason}."
    )]
    public static partial void PasskeyAssertionRefused(ILogger logger, Guid tenantId, string reason);

    /// <summary>A second factor was refused, so the session stays unusable.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Whose session. ⚠ Known here — the first factor already verified.</param>
    /// <param name="reason">The internal reason, which never reaches the caller.</param>
    /// <remarks>
    ///     ⚠ <c>totp-replayed</c> is the one value here worth an alert. It means a code was valid
    ///     <i>and</i> already spent, which is either a double-submitted form or somebody replaying a
    ///     code they observed — and the two are indistinguishable at this endpoint.
    /// </remarks>
    [LoggerMessage(
        EventId = 1109,
        Level = LogLevel.Information,
        Message = "A second factor was refused for user {UserId} in tenant {TenantId}: {Reason}."
    )]
    public static partial void SecondFactorRefused(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        string reason
    );

    /// <summary>A recovery code was redeemed and is now spent.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Whose code.</param>
    /// <remarks>
    ///     ⚠ Warning rather than Information, and it is the only second-factor success that is. A
    ///     recovery code is the break-glass credential — docs/plan/11 § Credentials, "the thing that
    ///     prevents 'I lost my phone' tickets" — so one being burnt is either a user who genuinely
    ///     lost their authenticator or an attacker who obtained the printed sheet. It is worth
    ///     surfacing either way, and it is rare enough that the volume costs nothing.
    /// </remarks>
    [LoggerMessage(
        EventId = 1110,
        Level = LogLevel.Warning,
        Message = "A recovery code was burnt for user {UserId} in tenant {TenantId}. The session is now fully authenticated."
    )]
    public static partial void RecoveryCodeBurnt(ILogger logger, Guid tenantId, Guid userId);

    /// <summary>A one-time code was issued and handed to <c>CyberCloud.Communication</c>.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Who it was sent to.</param>
    /// <param name="purpose">Why.</param>
    /// <param name="kind">Which channel.</param>
    /// <remarks>
    ///     ⚠ <b>No code, and no destination.</b> The code is the credential and the destination is an
    ///     address, which docs/plan/11 § Auditing bans from a log <i>message</i> outright. The four
    ///     placeholders here are two GUIDs and two enums, which is the invariant
    ///     <c>PiiNeverReachesALogMessageTests</c> holds this file to — and
    ///     <c>OtpIssuanceTests.TheCodeReachesNoLogMessage</c> holds the code to separately,
    ///     because a code is not PII and would slip past that suite.
    /// </remarks>
    [LoggerMessage(
        EventId = 1111,
        Level = LogLevel.Information,
        Message = "A one-time code was issued for user {UserId} in tenant {TenantId}: {Purpose} over {Kind}."
    )]
    public static partial void OtpIssued(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        OtpPurpose purpose,
        CredentialKind kind
    );

    /// <summary>A one-time code could not be issued or delivered.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Who it was for.</param>
    /// <param name="reason">
    ///     The internal reason, which never reaches the caller — the per-user issue cap, an
    ///     unenrolled channel, or <c>UnavailableOtpDelivery</c>'s sentence naming the missing
    ///     <c>AddCommunicationOtpDelivery</c> call. ⚠ <b>This is the one line an operator whose
    ///     silo is unwired will actually see</b>, so it carries the message verbatim.
    /// </param>
    [LoggerMessage(
        EventId = 1112,
        Level = LogLevel.Warning,
        Message = "A one-time code was not sent to user {UserId} in tenant {TenantId}: {Reason}"
    )]
    public static partial void OtpNotSent(ILogger logger, Guid tenantId, Guid userId, string reason);
}
