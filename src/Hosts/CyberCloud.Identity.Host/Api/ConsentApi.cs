using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.Api;

/// <summary>What <c>GET /api/consent</c> answers — what the consent page renders, and nothing more.</summary>
/// <param name="Ready">Whether there is a request to consent to. When false, <paramref name="Message" /> says why.</param>
/// <param name="ClientName">
///     The client's display name, from its registration. ⚠ Never from the authorization request's
///     query string, which is attacker-controlled — <c>IdentityEndpoints</c>' remarks on the page.
/// </param>
/// <param name="Scopes">The scopes the request asks for, cut to what the client may have.</param>
/// <param name="ReturnUrl">
///     The <c>/authorize</c> request the page posts its answer to — a same-origin path, already
///     sanitized by the server, and sanitized again by the page before it becomes a form action.
/// </param>
/// <param name="Message">What to render when <paramref name="Ready" /> is false, verbatim.</param>
public sealed record ConsentPageResponse(
    [property: JsonPropertyName("ready")]
    bool Ready,
    [property: JsonPropertyName("clientName")]
    string ClientName,
    [property: JsonPropertyName("scopes")]
    string[] Scopes,
    [property: JsonPropertyName("returnUrl")]
    string ReturnUrl,
    [property: JsonPropertyName("message")]
    string Message
);

/// <summary>
///     What the consent page needs to render, resolved from the <c>/authorize</c> request it was
///     sent with. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The page never sees the client's name from the query string.</b> An authorization
///         request is attacker-composed text — a link anybody can send — and a consent page that
///         rendered a name from it would let a phisher name their client "Cyber Cloud portal". The
///         name comes from <see cref="ApplicationRegistration.DisplayName" />, resolved the way
///         <c>/authorize</c> itself resolves the client: the tenant hint through the directory, the
///         client through <see cref="IClientResolver" />, and the <c>redirect_uri</c> checked against
///         the registration — so a page is rendered only for a request <c>/authorize</c> would
///         have accepted.
///     </para>
///     <para>
///         ⚠ <b>The answer is not taken here.</b> The page posts <c>allow</c> or <c>deny</c> to
///         <c>/authorize</c> itself, with the request's own parameters — <c>AuthorizeApi</c>'s
///         remarks say why — so OpenIddict answers the client (a code, or <c>access_denied</c> at
///         the registered redirect URI in the requested response mode) and this API only describes.
///         A person who is not fully signed into the request's tenant gets <c>ready: false</c>: the
///         page has nothing to ask them, and <c>/authorize</c> would send them to sign in first.
///     </para>
///     <para>
///         Every failure is <c>200</c> with <c>ready: false</c> and a sentence, for the reason
///         <c>SignInApi</c> gives every failure a <c>200</c>: the page renders one shape. The caller
///         is authenticated and asking about a request they were sent, so the sentences may be
///         specific.
///     </para>
/// </remarks>
public sealed class ConsentApi(TenantHint tenants, IClientResolver clients) {
    /// <summary>The return URL is not an <c>/authorize</c> request.</summary>
    public const string NotAnAuthorizationRequest = "This page needs an authorization request to describe.";

    /// <summary>The cookie is missing, pending a second factor, or for another tenant.</summary>
    public const string SignInFirst = "Sign in first.";

    /// <summary>The request names no tenant the directory knows, or no client the tenant registered.</summary>
    public const string UnknownClient = "The client is not registered.";

    /// <summary>The request's <c>redirect_uri</c> is not the client's.</summary>
    public const string UnregisteredRedirectUri = "The request's redirect URI is not registered for this client.";

    /// <summary>The client is the portal or the CLI, which are consent-free.</summary>
    public const string FirstPartyNeedsNoConsent = "This client is the platform's own and needs no consent.";

    /// <summary>
    ///     Describes the request behind <paramref name="returnUrl" /> for the person behind
    ///     <paramref name="user" />.
    /// </summary>
    /// <param name="returnUrl">The <c>/authorize</c> path and query the page was sent with.</param>
    /// <param name="user">The cookie principal.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    public async Task<ConsentPageResponse> DescribeAsync(string? returnUrl, ClaimsPrincipal? user, CancellationToken cancellationToken = default) {
        var sanitized = ReturnUrl.Sanitize(returnUrl);
        var question = sanitized.IndexOf('?', StringComparison.Ordinal);

        if (question < 0 || !string.Equals(sanitized[..question], IdentityHostOpenIddict.AuthorizationPath, StringComparison.Ordinal)) {
            return NotReady(NotAnAuthorizationRequest);
        }

        var query = QueryHelpers.ParseQuery(sanitized[question..]);

        if (!IdentitySessionPrincipal.IsFullyAuthenticated(user) || IdentitySessionPrincipal.TenantId(user) is not { } cookieTenant) {
            return NotReady(SignInFirst);
        }

        var tenantId = await tenants.ResolveAsync(query.TryGetValue(TenantHint.ParameterName, out var hint) ? hint.ToString() : null, cancellationToken);

        if (tenantId is null) {
            return NotReady(UnknownClient);
        }

        if (tenantId != cookieTenant) {
            return NotReady(SignInFirst);
        }

        var clientId = query.TryGetValue(OpenIddictConstants.Parameters.ClientId, out var id) ? id.ToString() : null;
        var client = await clients.ResolveAsync(tenantId.Value, clientId, cancellationToken);

        if (client is null) {
            return NotReady(UnknownClient);
        }

        if (FirstPartyClients.IsFirstParty(client.ClientId)) {
            return NotReady(FirstPartyNeedsNoConsent);
        }

        var redirectUri = query.TryGetValue(OpenIddictConstants.Parameters.RedirectUri, out var uri) ? uri.ToString() : null;

        if (!FirstPartyClients.IsRegisteredRedirectUri(client, redirectUri)) {
            return NotReady(UnregisteredRedirectUri);
        }

        var scopes = (query.TryGetValue(OpenIddictConstants.Parameters.Scope, out var scope) ? scope.ToString() : string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => client.AllowedScopes.Contains(x, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new(true, client.DisplayName, scopes, sanitized, string.Empty);
    }

    static ConsentPageResponse NotReady(string message) => new(false, string.Empty, [], ReturnUrl.Default, message);
}
