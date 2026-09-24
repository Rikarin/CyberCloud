using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The log lines the interactive grants emit, under the same rule as <c>IdentityLog</c>:
///     every placeholder is a GUID, an enum, a grant name or an internal reason, and nothing that
///     can hold an address, a name or an IP. docs/plan/11 § Auditing.
/// </summary>
/// <remarks>
///     Here rather than in <c>IdentityLog</c> because these are the host's decisions — which
///     client, which grant, why a code or a refresh was refused — and the module's file is the
///     module's. The event ids continue its <c>11xx</c> range from <c>1130</c> so a dashboard filter
///     on the range catches both.
/// </remarks>
static partial class GrantLog {
    /// <summary>An authorization request was refused before a code was minted.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant the request resolved to, or the empty GUID when it resolved to none.</param>
    /// <param name="error">The OAuth error sent back.</param>
    /// <param name="reason">The internal reason.</param>
    [LoggerMessage(
        EventId = 1130,
        Level = LogLevel.Information,
        Message = "Authorization request refused in tenant {TenantId} with {Error}: {Reason}"
    )]
    public static partial void AuthorizationRequestRefused(ILogger logger, Guid tenantId, string error, string reason);

    /// <summary>
    ///     An authorization request named a tenant the directory does not know, and the person was
    ///     sent to the sign-in page to name one — <c>AuthorizeApi.SignInLocationWithoutTenant</c>.
    /// </summary>
    /// <param name="logger">The sink.</param>
    /// <param name="hint">The <c>tenant</c> value as it arrived.</param>
    /// <param name="clientId">The first-party client that sent it.</param>
    [LoggerMessage(
        EventId = 1136,
        Level = LogLevel.Information,
        Message = "Authorization request from {ClientId} named unknown tenant '{Hint}'; sent to sign-in to name one"
    )]
    public static partial void AuthorizationRequestRedirectedForTenant(ILogger logger, string hint, string clientId);

    /// <summary>An authorization code was minted for a signed-in user.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">Who.</param>
    /// <param name="interactiveSessionId">The cookie session the code was minted from.</param>
    [LoggerMessage(
        EventId = 1131,
        Level = LogLevel.Information,
        Message = "Authorization code issued to user {UserId} in tenant {TenantId} from session {InteractiveSessionId}."
    )]
    public static partial void AuthorizationCodeIssued(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        Guid interactiveSessionId
    );

    /// <summary>A code exchange or a refresh was refused after the token itself validated.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="grantType">Which grant.</param>
    /// <param name="sessionId">The session the token named.</param>
    /// <param name="reason">The internal reason — the caller sees <c>invalid_grant</c> and a sentence.</param>
    [LoggerMessage(
        EventId = 1132,
        Level = LogLevel.Information,
        Message = "Grant {GrantType} refused for session {SessionId} in tenant {TenantId}: {Reason}"
    )]
    public static partial void GrantRefused(
        ILogger logger,
        Guid tenantId,
        string grantType,
        Guid sessionId,
        string reason
    );

    /// <summary>A token session was opened for a client at a code exchange.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">Who.</param>
    /// <param name="sessionId">The new token session.</param>
    /// <param name="interactiveSessionId">The cookie session it is bound to.</param>
    [LoggerMessage(
        EventId = 1133,
        Level = LogLevel.Information,
        Message =
            "Token session {SessionId} opened for user {UserId} in tenant {TenantId}, bound to session {InteractiveSessionId}."
    )]
    public static partial void TokenSessionOpened(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        Guid interactiveSessionId
    );

    /// <summary>
    ///     An authorization code was presented a second time; the token session its first exchange
    ///     opened was revoked. RFC 6749 § 4.1.2.
    /// </summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">Whose code.</param>
    /// <param name="codeId">The code's <c>jti</c> — an id, never the code.</param>
    /// <param name="revokedSessionId">The token session the first exchange opened, now revoked.</param>
    /// <remarks>
    ///     ⚠ Warning, not information: a replayed code is either a client bug or a code that leaked,
    ///     and both are worth a person's attention where a refused refresh is routine.
    /// </remarks>
    [LoggerMessage(
        EventId = 1137,
        Level = LogLevel.Warning,
        Message =
            "Authorization code {CodeId} for user {UserId} in tenant {TenantId} was presented again; token session {RevokedSessionId} revoked."
    )]
    public static partial void AuthorizationCodeReplayed(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        Guid codeId,
        Guid revokedSessionId
    );

    /// <summary>A person allowed a tenant-registered client — the consent page's yes, recorded.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">Who.</param>
    /// <param name="applicationId">The client's registration — its GUID, not its client id.</param>
    [LoggerMessage(
        EventId = 1138,
        Level = LogLevel.Information,
        Message = "User {UserId} in tenant {TenantId} consented to application {ApplicationId}."
    )]
    public static partial void ConsentGranted(ILogger logger, Guid tenantId, Guid userId, Guid applicationId);

    /// <summary>A person signed out: the interactive session was revoked.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="sessionId">The interactive session.</param>
    [LoggerMessage(
        EventId = 1134,
        Level = LogLevel.Information,
        Message = "Session {SessionId} in tenant {TenantId} signed out."
    )]
    public static partial void SignedOut(ILogger logger, Guid tenantId, Guid sessionId);

    /// <summary>A device authorization could not be recorded — every drawn user code collided, or the grain refused.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="clientId">The client that asked.</param>
    /// <param name="reason">The grain's refusal.</param>
    [LoggerMessage(
        EventId = 1140,
        Level = LogLevel.Warning,
        Message = "Device authorization for {ClientId} not begun: {Reason}"
    )]
    public static partial void DeviceAuthorizationNotBegun(ILogger logger, string clientId, string reason);

    /// <summary>A person answered a device authorization on the verification page.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant the person is signed into.</param>
    /// <param name="userId">Who answered.</param>
    /// <param name="approved">Whether they allowed it.</param>
    [LoggerMessage(
        EventId = 1141,
        Level = LogLevel.Information,
        Message = "User {UserId} in tenant {TenantId} answered a device authorization: approved {Approved}."
    )]
    public static partial void DeviceAuthorizationDecided(ILogger logger, Guid tenantId, Guid userId, bool approved);

    /// <summary>
    ///     A device code was redeemed a second time; the token session the first redemption opened
    ///     was revoked — <c>AuthorizationCodeReplayed</c>'s rule, applied to RFC 8628.
    /// </summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">Whose approval.</param>
    /// <param name="revokedSessionId">The token session the first redemption opened, now revoked.</param>
    [LoggerMessage(
        EventId = 1142,
        Level = LogLevel.Warning,
        Message = "A device code for user {UserId} in tenant {TenantId} was redeemed again; token session {RevokedSessionId} revoked."
    )]
    public static partial void DeviceCodeReplayed(ILogger logger, Guid tenantId, Guid userId, Guid revokedSessionId);

    /// <summary>A client revoked its own refresh token at <c>/revoke</c>, which ends the token session.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="sessionId">The token session revoked.</param>
    [LoggerMessage(
        EventId = 1143,
        Level = LogLevel.Information,
        Message = "Token session {SessionId} in tenant {TenantId} revoked by its client."
    )]
    public static partial void RevokedByClient(ILogger logger, Guid tenantId, Guid sessionId);
}
