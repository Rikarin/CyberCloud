using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Orleans.Multitenant;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     What <c>GET /authorize</c> does with a validated request, once the cookie has been read.
/// </summary>
public abstract record AuthorizeDecision {
    /// <summary>Mint a code from this principal and send it to the client.</summary>
    /// <param name="Principal">The code principal — <see cref="TokenApi.BuildCodePrincipal" />'s.</param>
    public sealed record IssueCode(ClaimsPrincipal Principal) : AuthorizeDecision;

    /// <summary>Send the client an OAuth error at its redirect URI — the request was valid, the answer is no.</summary>
    /// <param name="Error">The error, one of <c>OpenIddictConstants.Errors</c>.</param>
    /// <param name="Description">What to say about it.</param>
    public sealed record Refuse(string Error, string Description) : AuthorizeDecision;

    /// <summary>Send the person to the sign-in page, and bring them back here afterwards.</summary>
    /// <param name="Location">The absolute or same-origin URL of the page, with <c>returnUrl</c> set.</param>
    public sealed record SignIn(string Location) : AuthorizeDecision;
}

/// <summary>
///     The decisions behind the authorization endpoint's passthrough: whether the cookie session
///     may have a code, and where to send the person when it may not. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             By the time this runs, <c>DegradedModeHandlers.ValidateAuthorizationRequest</c> has
///             resolved the tenant and the client and validated the redirect URI.
///         </b> What is left is the one question OpenIddict cannot answer because the session is
///         ours: is the person behind this cookie signed into <i>this</i> tenant, completely, and
///         still? Four things have to be true, in this order, and each false one has a different
///         destination:
///     </para>
///     <list type="number">
///         <item>
///             <see cref="IdentitySessionPrincipal.IsFullyAuthenticated" /> — a cookie stamped
///             <c>cyc:2fa=pending</c> is a person who typed a password and not yet a code, and goes
///             back to the sign-in page, which resumes at the second-factor step.
///         </item>
///         <item>
///             The cookie's <c>tid</c> is the tenant the request resolved to — a session in one
///             tenant does not authorize a code for another, however the same person got there.
///         </item>
///         <item>
///             <c>prompt=login</c> was not asked for — a client that wants a fresh sign-in gets
///             one, and the parameter is dropped from the return URL so the second pass does not
///             loop.
///         </item>
///         <item>
///             <c>ISessionGrain.IsLive</c> — a cookie outlives a revoked session by up to eight
///             hours, and the grain is the only thing that knows about the revocation.
///         </item>
///     </list>
///     <para>
///         A false at any step with <c>prompt=none</c> is <c>login_required</c> back to the client,
///         because that is what the client asked to be told. Otherwise it is the sign-in page.
///     </para>
///     <para>
///         ⚠ <b>Consent is only free for a first-party client.</b> A tenant-registered client gets
///         <c>consent_required</c> until the consent page exists (owed —
///         <c>IdentityEndpoints</c>' remarks), because minting a code for a third party without
///         asking the person is worse than refusing to.
///     </para>
///     <para>
///         Returns values, for the reason <c>SignInApi</c> gives.
///     </para>
/// </remarks>
public sealed class AuthorizeApi(
    IGrainFactory grains,
    IOptions<IdentityHostOptions> options,
    ILogger<AuthorizeApi> logger
) {
    /// <summary>The page an unauthenticated <c>/authorize</c> lands on, under <see cref="IdentityHostOptions.SignInPageBaseUri" />.</summary>
    public const string SignInPagePath = "/signin";

    readonly IdentityHostOptions options = options.Value;

    /// <summary>
    ///     Decides.
    /// </summary>
    /// <param name="request">The validated authorization request.</param>
    /// <param name="tenantId">The tenant it resolved to.</param>
    /// <param name="client">The registration it resolved to.</param>
    /// <param name="user">The cookie principal, or an anonymous one.</param>
    /// <param name="pathAndQuery">
    ///     This request's own path and query, which becomes the return URL. ⚠ A path, never an
    ///     absolute URL — <see cref="ReturnUrl.Sanitize" /> would refuse the latter and the person
    ///     would land on <c>/</c> with no request to resume.
    /// </param>
    /// <param name="cancellationToken">Cancels the grain call.</param>
    public async Task<AuthorizeDecision> DecideAsync(
        OpenIddictRequest request,
        Guid tenantId,
        ApplicationRegistration client,
        ClaimsPrincipal? user,
        string pathAndQuery,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        var promptLogin = request.HasPromptValue(OpenIddictConstants.PromptValues.Login);
        var promptNone = request.HasPromptValue(OpenIddictConstants.PromptValues.None);

        if (!IdentitySessionPrincipal.IsFullyAuthenticated(user)
            || IdentitySessionPrincipal.TenantId(user) != tenantId
            || IdentitySessionPrincipal.UserId(user) is not { } userId
            || IdentitySessionPrincipal.SessionId(user) is not { } sessionId
            || promptLogin) {
            return NotSignedIn(promptNone, pathAndQuery, "no-usable-session");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tenant = grains.ForTenant(TenantHint.Qualifier(tenantId));

        var session = await tenant.GetGrain<ISessionGrain>(GrainKeys.Session(sessionId)).GetAsync();

        if (session.TryGetError(out _) || !session.GetValueOrThrow().IsLive) {
            return NotSignedIn(promptNone, pathAndQuery, "session-not-live");
        }

        // ⚠ First-party clients are the platform's own pages; everything else needs the consent page
        // that does not exist yet, and answers so rather than minting a code nobody agreed to.
        if (!FirstPartyClients.IsFirstParty(client.ClientId)) {
            GrantLog.AuthorizationRequestRefused(logger, tenantId, OpenIddictConstants.Errors.ConsentRequired, "consent-page-owed");

            return new AuthorizeDecision.Refuse(
                OpenIddictConstants.Errors.ConsentRequired,
                "This client needs the person's consent, and the consent page is not built yet. docs/plan/11 § Protocol."
            );
        }

        var profile = await tenant.GetGrain<IUserGrain>(GrainKeys.User(userId)).GetAsync();

        if (profile.TryGetError(out _)) {
            return NotSignedIn(promptNone, pathAndQuery, "user-not-found");
        }

        // The request's scopes, cut to what the client may have. The validator already refused a
        // scope outside the client's, so this is the same set — cut again so the code cannot carry
        // more than the registration allows if that order ever changes.
        var scopes = request.GetScopes()
            .Where(x => client.AllowedScopes.Contains(x, StringComparer.Ordinal))
            .ToList();

        GrantLog.AuthorizationCodeIssued(logger, tenantId, userId, sessionId);

        return new AuthorizeDecision.IssueCode(
            TokenApi.BuildCodePrincipal(WithCookieMethods(session.GetValueOrThrow(), user!), profile.GetValueOrThrow(), client, scopes)
        );
    }

    /// <summary>
    ///     The interactive session with the cookie's <c>amr</c> merged in.
    /// </summary>
    /// <remarks>
    ///     ⚠ The grain records the first factor and the cookie records both. <c>SignInService</c>
    ///     opens the session when the password verifies, with <c>[Password]</c>, and the second
    ///     factor is stamped onto the cookie by <see cref="IdentitySessionPrincipal.Promote" /> —
    ///     nothing writes it back to the grain. A code minted from the grain's list alone would say
    ///     <c>["pwd"]</c> for a sign-in that presented a code, and every access token and
    ///     step-up rule downstream would read one factor where there were two. So the token session
    ///     is opened with the union, in the grain's order first, and <c>amr</c> on the wire says
    ///     <c>["pwd", "otp"]</c> — <c>GrantsOverHttpTests</c> found the difference.
    /// </remarks>
    static SessionDescriptor WithCookieMethods(SessionDescriptor session, ClaimsPrincipal user) {
        var methods = new List<AuthenticationMethod>(session.Methods);

        foreach (var claim in user.FindAll(AccessTokenClaims.AuthenticationMethods)) {
            if (AuthenticationMethodNames.Parse(claim.Value) is { } method && !methods.Contains(method)) {
                methods.Add(method);
            }
        }

        return session with { Methods = methods };
    }

    /// <summary>
    ///     Where a person with no usable session goes: the sign-in page, with this request as the
    ///     return URL and <c>prompt=login</c> removed from it.
    /// </summary>
    /// <param name="pathAndQuery">This request's path and query.</param>
    /// <remarks>
    ///     ⚠ <c>prompt=login</c> is removed and only it — a client that asks for a fresh sign-in
    ///     gets one, and the resumed request must not ask again or the person loops between the
    ///     two pages forever. <c>prompt=none</c> never reaches here (it is answered to the client).
    ///     The rest of the query is kept byte for byte, because the PKCE challenge and the state in
    ///     it are the client's and any change to them fails the exchange.
    /// </remarks>
    public string SignInLocation(string pathAndQuery) {
        var returnUrl = ReturnUrl.Sanitize(WithoutPromptLogin(pathAndQuery));

        return options.SignInPageBaseUri.TrimEnd('/') + SignInPagePath + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
    }

    AuthorizeDecision NotSignedIn(bool promptNone, string pathAndQuery, string reason) {
        if (promptNone) {
            GrantLog.AuthorizationRequestRefused(logger, Guid.Empty, OpenIddictConstants.Errors.LoginRequired, reason);

            return new AuthorizeDecision.Refuse(OpenIddictConstants.Errors.LoginRequired, "The person is not signed in.");
        }

        return new AuthorizeDecision.SignIn(SignInLocation(pathAndQuery));
    }

    static string WithoutPromptLogin(string pathAndQuery) {
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);

        if (question < 0) {
            return pathAndQuery;
        }

        var kept = pathAndQuery[(question + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(WithoutLogin)
            .Where(pair => pair is not null)
            .ToList();

        return kept.Count == 0 ? pathAndQuery[..question] : pathAndQuery[..question] + "?" + string.Join('&', kept);
    }

    /// <summary>
    ///     One query pair without the <c>login</c> prompt value, or <see langword="null" /> when
    ///     that was all it said. Every other pair comes back untouched.
    /// </summary>
    static string? WithoutLogin(string pair) {
        var equals = pair.IndexOf('=', StringComparison.Ordinal);

        if (equals < 0 || !string.Equals(pair[..equals], "prompt", StringComparison.Ordinal)) {
            return pair;
        }

        var values = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.Equals(x, OpenIddictConstants.PromptValues.Login, StringComparison.Ordinal))
            .ToList();

        return values.Count == 0 ? null : "prompt=" + Uri.EscapeDataString(string.Join(' ', values));
    }
}
