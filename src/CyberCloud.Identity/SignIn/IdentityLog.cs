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
}
