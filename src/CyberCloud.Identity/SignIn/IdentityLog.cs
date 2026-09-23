using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.SignIn;

/// <summary>
///     Every log line the identity module emits, and the reason they are all in one file.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             docs/plan/11 § Auditing: "no email, name or IP in a log <i>message</i>. They go in
///             structured fields, which are subject to the retention and redaction policy; a message
///             string is not."
///         </b> That rule is easy to state and very easy to break by accident — an
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
        Message =
            "Refresh reuse detected on session {SessionId} for user {UserId} in tenant {TenantId}. The session and its entire chain are revoked."
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
        Message =
            "A request named an address with no account: identifier {IdentifierDigest} in tenant {TenantId}. The caller was told nothing."
    )]
    public static partial void UnknownAddressProbed(ILogger logger, Guid tenantId, string identifierDigest);

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
        Message =
            "A recovery code was burnt for user {UserId} in tenant {TenantId}. The session is now fully authenticated."
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
    ///     <c>AddCommunicationOtpDelivery</c> call. ⚠
    ///     <b>
    ///         This is the one line an operator whose
    ///         silo is unwired will actually see
    ///     </b>, so it carries the message verbatim.
    /// </param>
    [LoggerMessage(
        EventId = 1112,
        Level = LogLevel.Warning,
        Message = "A one-time code was not sent to user {UserId} in tenant {TenantId}: {Reason}"
    )]
    public static partial void OtpNotSent(ILogger logger, Guid tenantId, Guid userId, string reason);

    /// <summary>
    ///     A one-time code was written to the log instead of being sent — the Development-only
    ///     delivery seam. ⚠ The code is in the message on purpose; the address is not.
    /// </summary>
    /// <param name="logger">The sink.</param>
    /// <param name="code">The six digits. ⚠ A credential, in a log line, and only Development can produce it.</param>
    /// <param name="userId">Who it is for.</param>
    /// <param name="purpose">Why.</param>
    /// <param name="kind">Which channel it would have gone out on.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The one template in this file that renders a credential, and the constructor of
    ///             the only caller refuses to run outside Development
    ///         </b> — <c>DevelopmentOtpDelivery</c>. On a development run the code also goes to
    ///         Mailpit's inbox when the AppHost's relay is configured (#93), and this line stays
    ///         beside it because the person reading it off the Aspire dashboard is the person who
    ///         typed the address ten seconds earlier. Warning rather than Information so it stands
    ///         out in a console, and the message starts with a marker a dashboard filter can find.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The address is not a parameter, and it must not become one.</b> It reaches the
    ///         sink as a <c>Destination</c> property in a log scope the caller opens around this
    ///         call — a structured field the retention and redaction policy can reach, and one no
    ///         rendered message carries. Adding a <c>{Destination}</c> placeholder here would put
    ///         it in the line, which is the thing docs/plan/11 § Auditing forbids;
    ///         <c>DevelopmentOtpDeliveryTests.TheAddressIsAStructuredPropertyAndNeverInTheMessage</c>
    ///         pins both halves.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1113,
        Level = LogLevel.Warning,
        Message =
            "⚠ DEVELOPMENT OTP DELIVERY (#93): code {Code} for user {UserId}, {Purpose} via {Kind}. This seam refuses to load outside Development."
    )]
    public static partial void DevelopmentOtpDelivered(
        ILogger logger,
        string code,
        Guid userId,
        OtpPurpose purpose,
        CredentialKind kind
    );

    /// <summary>
    ///     The Development seam also mailed the code through the platform's communication service —
    ///     Mailpit on the AppHost — and the mail was refused. The line above still has the code.
    /// </summary>
    /// <param name="logger">The sink.</param>
    /// <param name="userId">Who it was for.</param>
    /// <param name="code">The refusal's <c>ErrorCode</c> value — <c>PolicyViolation</c>, <c>InternalError</c>.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ Warning and not Error, and the delivery still reports success: in Development the
    ///         log line <i>is</i> a delivery, and a Mailpit that is not up yet must not turn a
    ///         sign-up into "something went wrong" when the code is on the console the person is
    ///         looking at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sending module's sentence is not in the template</b>, and the first cut had
    ///         it there. A suppression refusal opens with the address it refused and a relay's
    ///         <c>550</c> quotes it back, so the sentence is the address by another route.
    ///         <c>DevelopmentOtpDelivery</c> puts it in the scope under
    ///         <c>DevelopmentOtpDelivery.ReasonProperty</c>, beside the destination; what the line
    ///         carries is the code, which says what kind of refusal it was and names nobody.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1122,
        Level = LogLevel.Warning,
        Message =
            "The development code for user {UserId} was logged above and was NOT mailed ({Code}); the sending module's reason is the Reason property."
    )]
    public static partial void DevelopmentOtpNotMailed(ILogger logger, Guid userId, string code);

    /// <summary>A self-serve sign-up began and allocated its ids. ⚠ No address.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="signupId">The sign-up.</param>
    /// <param name="tenantId">The tenant it will create.</param>
    /// <param name="userId">The user that will own it.</param>
    [LoggerMessage(
        EventId = 1114,
        Level = LogLevel.Information,
        Message = "Sign-up {SignupId} began: it will create tenant {TenantId} owned by user {UserId}."
    )]
    public static partial void SignUpBegun(ILogger logger, Guid signupId, Guid tenantId, Guid userId);

    /// <summary>A sign-up's create step completed and was recorded.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="signupId">The sign-up.</param>
    /// <param name="tenantId">The tenant being created.</param>
    /// <param name="step">Which step.</param>
    [LoggerMessage(
        EventId = 1115,
        Level = LogLevel.Information,
        Message = "Sign-up {SignupId} completed step {Step} for tenant {TenantId}."
    )]
    public static partial void SignUpStepCompleted(ILogger logger, Guid signupId, Guid tenantId, SignUpStep step);

    /// <summary>A sign-up's create step failed. The sign-up stays re-drivable.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="signupId">The sign-up.</param>
    /// <param name="tenantId">The tenant being created.</param>
    /// <param name="step">Which step failed.</param>
    /// <param name="reason">
    ///     The internal reason, which reaches the caller only as one of the fixed sentences
    ///     <c>SignUpOrchestrator</c> maps it to. ⚠ Never an address: the scope manager's and the
    ///     grains' messages name ids and slugs, and a slug is a name the person chose for their
    ///     organisation rather than a fact about them.
    /// </param>
    [LoggerMessage(
        EventId = 1116,
        Level = LogLevel.Warning,
        Message = "Sign-up {SignupId} failed at step {Step} for tenant {TenantId}: {Reason}"
    )]
    public static partial void SignUpFailed(
        ILogger logger,
        Guid signupId,
        Guid tenantId,
        SignUpStep step,
        string reason
    );

    /// <summary>A token request's client could not be authenticated.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant the host serves.</param>
    /// <param name="grantType">The grant the request named.</param>
    /// <param name="reason">
    ///     The internal reason, which never reaches the caller — the endpoint answers
    ///     <c>invalid_client</c> whatever happened, so an unknown client, a disabled one, a wrong
    ///     secret and an unwired vault all look the same from outside. ⚠ The last of those carries
    ///     <c>UnavailableClientSecrets</c>' sentence verbatim, because this is the one line an
    ///     operator whose host has no verifier will see.
    /// </param>
    [LoggerMessage(
        EventId = 1120,
        Level = LogLevel.Information,
        Message = "Token request refused in tenant {TenantId} for grant {GrantType}: {Reason}"
    )]
    public static partial void TokenRequestRefused(ILogger logger, Guid tenantId, string grantType, string reason);

    /// <summary>An access token was minted.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="subjectType">What kind of subject — <c>user</c>, <c>servicePrincipal</c>, <c>managedIdentity</c>.</param>
    /// <param name="subjectId">Who.</param>
    /// <param name="grantType">Which grant produced it.</param>
    [LoggerMessage(
        EventId = 1121,
        Level = LogLevel.Information,
        Message = "Access token issued to {SubjectType} {SubjectId} in tenant {TenantId} by {GrantType}."
    )]
    public static partial void TokenIssued(
        ILogger logger,
        Guid tenantId,
        string subjectType,
        Guid subjectId,
        string grantType
    );

    /// <summary>An invitation was created and its link mailed (#43).</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant invited into.</param>
    /// <param name="invitationId">The invitation — its id, never its link.</param>
    /// <param name="invitedBy">Who sent it.</param>
    [LoggerMessage(
        EventId = 1125,
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} into tenant {TenantId} sent by {InvitedBy}."
    )]
    public static partial void InvitationSent(ILogger logger, Guid tenantId, Guid invitationId, Guid invitedBy);

    /// <summary>An invitation was recorded and its mail refused — the sender's retry re-sends.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="invitationId">The invitation.</param>
    /// <param name="reason">The seam's refusal, which names what is missing.</param>
    [LoggerMessage(
        EventId = 1126,
        Level = LogLevel.Warning,
        Message = "Invitation {InvitationId} into tenant {TenantId} was not delivered: {Reason}"
    )]
    public static partial void InvitationNotDelivered(ILogger logger, Guid tenantId, Guid invitationId, string reason);

    /// <summary>An invitation was accepted: the invited user is an active member.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="invitationId">The invitation.</param>
    /// <param name="userId">The member it made.</param>
    [LoggerMessage(
        EventId = 1127,
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} into tenant {TenantId} accepted by user {UserId}."
    )]
    public static partial void InvitationAccepted(ILogger logger, Guid tenantId, Guid invitationId, Guid userId);
}
