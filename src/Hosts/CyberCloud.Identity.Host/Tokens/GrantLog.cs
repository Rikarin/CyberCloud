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
    public static partial void AuthorizationCodeIssued(ILogger logger, Guid tenantId, Guid userId, Guid interactiveSessionId);

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
    public static partial void GrantRefused(ILogger logger, Guid tenantId, string grantType, Guid sessionId, string reason);

    /// <summary>A token session was opened for a client at a code exchange.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">Who.</param>
    /// <param name="sessionId">The new token session.</param>
    /// <param name="interactiveSessionId">The cookie session it is bound to.</param>
    [LoggerMessage(
        EventId = 1133,
        Level = LogLevel.Information,
        Message = "Token session {SessionId} opened for user {UserId} in tenant {TenantId}, bound to session {InteractiveSessionId}."
    )]
    public static partial void TokenSessionOpened(
        ILogger logger,
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        Guid interactiveSessionId
    );

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
}
