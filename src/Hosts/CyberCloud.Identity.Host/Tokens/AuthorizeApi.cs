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

    /// <summary>
    ///     Send the person to the consent page, which posts their answer back to <c>/authorize</c>.
    /// </summary>
    /// <param name="Location">The absolute or same-origin URL of the page, with <c>returnUrl</c> set.</param>
    public sealed record Consent(string Location) : AuthorizeDecision;
}

/// <summary>
///     What the consent page posted back to <c>/authorize</c>, when it did.
/// </summary>
/// <remarks>
///     ⚠ Read only off a <c>POST</c> from the page's own origin — <c>IdentityEndpoints.MapAuthorize</c>
///     decides whether a request carries one at all. A <c>consent=allow</c> in the query string of
///     a <c>GET</c> is a link anybody could have sent the person, and it is ignored: consent is what
///     the person clicked on a page this host served, not a parameter a client can pre-fill.
/// </remarks>
public enum ConsentDecision {
    /// <summary>The person allowed the client the scopes it asked for.</summary>
    Allow,

    /// <summary>The person declined. The client hears <c>access_denied</c>.</summary>
    Deny
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
///             ⚠ The one exception is a member who joined this tenant <i>with</i> the cookie's
///             account (<see cref="UserProfile.HomeAccount" />): they have no credential here by
///             design, and a complete, live sign-in of that account opens their session here —
///             <see cref="HomeAccounts" />' remarks. Any other session in another tenant is still
///             the sign-in page.
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
///         ⚠ <b>Consent is only free for a first-party client.</b> The portal and the CLI are the
///         platform's own pages; every other client is a tenant's, and a code for it is minted only
///         once the person has said yes — on this request, through the consent page's <c>POST</c>
///         back to <c>/authorize</c> (<see cref="ConsentDecision.Allow" />), or earlier, as a grant
///         <c>IConsentGrain</c> holds per (person, client) that covers every scope this request asks
///         for. A grant that covers fewer scopes is a fresh question, not a partial yes;
///         <c>prompt=consent</c> asks even when a grant covers everything, as OIDC Core § 3.1.2.1
///         says it should; <see cref="ConsentDecision.Deny" /> is <c>access_denied</c> to the
///         client, RFC 6749 § 4.1.2.1; and <c>prompt=none</c> with nothing on record is
///         <c>consent_required</c>, because that is what the client asked to be told. The grant is
///         written before the code is minted, so a crash between the two costs a second prompt and
///         never a code nobody agreed to.
///     </para>
///     <para>
///         Returns values, for the reason <c>SignInApi</c> gives.
///     </para>
/// </remarks>
public sealed class AuthorizeApi(
    IGrainFactory grains,
    IOptions<IdentityHostOptions> options,
    HomeAccounts homes,
    ILogger<AuthorizeApi> logger
) {
    /// <summary>
    ///     The page an unauthenticated <c>/authorize</c> lands on, under
    ///     <see cref="IdentityHostOptions.SignInPageBaseUri" />.
    /// </summary>
    public const string SignInPagePath = "/signin";

    /// <summary>The page a tenant-registered client's <c>/authorize</c> lands on, under the same base.</summary>
    public const string ConsentPagePath = "/consent";

    /// <summary>The form field the consent page posts its answer in: <c>allow</c> or <c>deny</c>.</summary>
    public const string ConsentParameter = "consent";

    /// <summary>What the client hears when the person declined.</summary>
    public const string ConsentDeniedDescription = "The person declined to authorize this client.";

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
    /// <param name="consent">
    ///     What the consent page posted back, or <see langword="null" /> when this request carries
    ///     no answer — every <c>GET</c>, and a <c>POST</c> from anywhere but the page's origin.
    /// </param>
    /// <param name="signIn">
    ///     The request, for the device record of a session this opens — only a member signing in
    ///     through their home account gets one. Defaults to the client id and nothing else.
    /// </param>
    /// <param name="cancellationToken">Cancels the grain call.</param>
    public async Task<AuthorizeDecision> DecideAsync(
        OpenIddictRequest request,
        Guid tenantId,
        ApplicationRegistration client,
        ClaimsPrincipal? user,
        string pathAndQuery,
        ConsentDecision? consent = null,
        SignInContext? signIn = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        var promptLogin = request.HasPromptValue(OpenIddictConstants.PromptValues.Login);
        var promptNone = request.HasPromptValue(OpenIddictConstants.PromptValues.None);

        if (!IdentitySessionPrincipal.IsFullyAuthenticated(user)
            || IdentitySessionPrincipal.TenantId(user) is not { } cookieTenantId
            || IdentitySessionPrincipal.UserId(user) is not { } cookieUserId
            || IdentitySessionPrincipal.SessionId(user) is not { } sessionId
            || promptLogin) {
            return NotSignedIn(promptNone, pathAndQuery, "no-usable-session");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (cookieTenantId != tenantId) {
            return await ThroughHomeAccountAsync(
                request,
                tenantId,
                client,
                user!,
                pathAndQuery,
                consent,
                signIn ?? new() { ClientId = client.ClientId },
                cancellationToken
            );
        }

        var tenant = grains.ForTenant(TenantHint.Qualifier(tenantId));

        var session = await tenant.GetGrain<ISessionGrain>(GrainKeys.Session(sessionId)).GetAsync();

        if (session.TryGetError(out _) || !session.GetValueOrThrow().IsLive) {
            return NotSignedIn(promptNone, pathAndQuery, "session-not-live");
        }

        var profile = await tenant.GetGrain<IUserGrain>(GrainKeys.User(cookieUserId)).GetAsync();

        if (profile.TryGetError(out _)) {
            return NotSignedIn(promptNone, pathAndQuery, "user-not-found");
        }

        return await IssueAsync(
            request,
            tenantId,
            client,
            WithCookieMethods(session.GetValueOrThrow(), user!),
            profile.GetValueOrThrow(),
            pathAndQuery,
            consent
        );
    }

    /// <summary>
    ///     The decision for a cookie signed into another tenant: the member who joined this one with
    ///     that account, signed in from it, or the sign-in page.
    /// </summary>
    /// <remarks>
    ///     ⚠ The member is found and consent is settled before any session opens, so a request that
    ///     ends on the consent page, or refused, leaves no session behind. The session is opened only
    ///     when a code is about to be minted from it.
    /// </remarks>
    async Task<AuthorizeDecision> ThroughHomeAccountAsync(
        OpenIddictRequest request,
        Guid tenantId,
        ApplicationRegistration client,
        ClaimsPrincipal user,
        string pathAndQuery,
        ConsentDecision? consent,
        SignInContext signIn,
        CancellationToken cancellationToken
    ) {
        if (await homes.SignedInAsync(user) is not { } home
            || await homes.MemberAsync(home, tenantId, cancellationToken) is not { } member) {
            return NotSignedIn(
                request.HasPromptValue(OpenIddictConstants.PromptValues.None),
                pathAndQuery,
                "no-usable-session"
            );
        }

        return await IssueAsync(
            request,
            tenantId,
            client,
            null,
            member,
            pathAndQuery,
            consent,
            () => homes.OpenAsync(home, member, signIn with { ClientId = client.ClientId })
        );
    }

    /// <summary>Consent, then the code — the half of the decision both kinds of session share.</summary>
    /// <remarks>
    ///     Either <c>session</c> is the cookie's, or it is <see langword="null" /> and <c>open</c>
    ///     makes the member's once nothing stands between the request and a code.
    /// </remarks>
    async Task<AuthorizeDecision> IssueAsync(
        OpenIddictRequest request,
        Guid tenantId,
        ApplicationRegistration client,
        SessionDescriptor? session,
        UserProfile profile,
        string pathAndQuery,
        ConsentDecision? consent,
        Func<Task<SessionDescriptor?>>? open = null
    ) {
        var promptNone = request.HasPromptValue(OpenIddictConstants.PromptValues.None);
        var userId = profile.UserId;

        // The request's scopes, cut to what the client may have. The validator already refused a
        // scope outside the client's, so this is the same set — cut again so the code cannot carry
        // more than the registration allows if that order ever changes.
        var scopes = request.GetScopes()
            .Where(x => client.AllowedScopes.Contains(x, StringComparer.Ordinal))
            .ToList();

        // ⚠ First-party clients are the platform's own pages and are consent-free by registration;
        // everything else is a tenant's, and needs the person's yes — see the type's remarks.
        if (!FirstPartyClients.IsFirstParty(client.ClientId)) {
            var consented = await ConsentAsync(
                request,
                tenantId,
                userId,
                client,
                scopes,
                promptNone,
                pathAndQuery,
                consent
            );

            if (consented is not null) {
                return consented;
            }
        }

        if (session is null) {
            session = open is null ? null : await open();

            if (session is null) {
                return NotSignedIn(promptNone, pathAndQuery, "member-session-not-opened");
            }
        }

        GrantLog.AuthorizationCodeIssued(logger, tenantId, userId, session.SessionId);

        return new AuthorizeDecision.IssueCode(TokenApi.BuildCodePrincipal(session, profile, client, scopes));
    }

    /// <summary>
    ///     The consent half of the decision, for a tenant-registered client: the answer the page
    ///     posted, the grant on record, or where to send the person. <see langword="null" /> means
    ///     the person has consented and the code may be minted.
    /// </summary>
    async Task<AuthorizeDecision?> ConsentAsync(
        OpenIddictRequest request,
        Guid tenantId,
        Guid userId,
        ApplicationRegistration client,
        List<string> scopes,
        bool promptNone,
        string pathAndQuery,
        ConsentDecision? consent
    ) {
        var grant = grains.ForTenant(TenantHint.Qualifier(tenantId))
            .GetGrain<IConsentGrain>(GrainKeys.ConsentGrant(tenantId, userId, client.ClientId));

        switch (consent) {
            case ConsentDecision.Deny:
                GrantLog.AuthorizationRequestRefused(
                    logger,
                    tenantId,
                    OpenIddictConstants.Errors.AccessDenied,
                    "consent-denied"
                );

                return new AuthorizeDecision.Refuse(OpenIddictConstants.Errors.AccessDenied, ConsentDeniedDescription);

            case ConsentDecision.Allow: {
                // ⚠ Written before the code is minted, never after: a crash between the two costs a
                // second prompt, and the other order could mint a code from a grant that was never
                // recorded.
                var granted = await grant.GrantAsync(userId, client.ClientId, scopes);

                if (granted.TryGetError(out var failed)) {
                    GrantLog.AuthorizationRequestRefused(
                        logger,
                        tenantId,
                        OpenIddictConstants.Errors.ServerError,
                        "consent-not-recorded"
                    );

                    return new AuthorizeDecision.Refuse(OpenIddictConstants.Errors.ServerError, failed.Message);
                }

                GrantLog.ConsentGranted(logger, tenantId, userId, client.ApplicationId);

                return null;
            }
        }

        var recorded = await grant.GetAsync();

        if (recorded.IsSuccess
            && recorded.GetValueOrThrow().Covers(scopes)
            && !request.HasPromptValue(OpenIddictConstants.PromptValues.Consent)) {
            return null;
        }

        if (promptNone) {
            GrantLog.AuthorizationRequestRefused(
                logger,
                tenantId,
                OpenIddictConstants.Errors.ConsentRequired,
                "consent-not-on-record"
            );

            return new AuthorizeDecision.Refuse(
                OpenIddictConstants.Errors.ConsentRequired,
                "The person has not consented to this client."
            );
        }

        return new AuthorizeDecision.Consent(ConsentLocation(pathAndQuery));
    }

    /// <summary>
    ///     Where a person whose consent a tenant-registered client needs goes: the consent page,
    ///     with this request as the return URL.
    /// </summary>
    /// <param name="pathAndQuery">This request's path and query.</param>
    /// <remarks>
    ///     The query is kept byte for byte, <c>prompt=login</c> included — the page posts every pair
    ///     back with its answer, and the answer is what stops <c>prompt=consent</c> from asking
    ///     twice; <c>prompt=login</c> cannot reach here, because a person who has not signed in
    ///     never sees the consent page.
    /// </remarks>
    public string ConsentLocation(string pathAndQuery) {
        var returnUrl = ReturnUrl.Sanitize(pathAndQuery);

        return options.SignInPageBaseUri.TrimEnd('/')
            + ConsentPagePath
            + "?returnUrl="
            + Uri.EscapeDataString(returnUrl);
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

        return options.SignInPageBaseUri.TrimEnd('/')
            + SignInPagePath
            + "?returnUrl="
            + Uri.EscapeDataString(returnUrl);
    }

    /// <summary>
    ///     Where a person whose <c>tenant</c> hint named no tenant goes: the sign-in page, with this
    ///     request as the return URL and the hint removed from it, so the page asks for the
    ///     organisation and the resumed request names the one they type.
    /// </summary>
    /// <param name="pathAndQuery">This request's path and query.</param>
    /// <param name="hint">The hint that resolved to nothing, for the log.</param>
    /// <param name="clientId">The client, for the log.</param>
    /// <remarks>
    ///     ⚠ Removed rather than left in place: the sign-in page reads <c>tenant</c> off the return
    ///     URL and, when it finds one, hides the organisation field and sends that value with the
    ///     credential — so a stale hint left in the URL would be a sign-in against the missing tenant,
    ///     refused uniformly, with the person never asked which organisation they meant. The rest
    ///     of the query is kept byte for byte, as <see cref="SignInLocation" /> keeps it.
    /// </remarks>
    public string SignInLocationWithoutTenant(string pathAndQuery, string hint, string? clientId) {
        GrantLog.AuthorizationRequestRedirectedForTenant(logger, hint, clientId ?? string.Empty);

        var returnUrl = ReturnUrl.Sanitize(WithoutPair(WithoutPromptLogin(pathAndQuery), TenantHint.ParameterName));

        return options.SignInPageBaseUri.TrimEnd('/')
            + SignInPagePath
            + "?returnUrl="
            + Uri.EscapeDataString(returnUrl);
    }

    /// <summary>The path and query without every pair named <paramref name="name" />.</summary>
    static string WithoutPair(string pathAndQuery, string name) {
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);

        if (question < 0) {
            return pathAndQuery;
        }

        var kept = pathAndQuery[(question + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !string.Equals(pair.Split('=', 2)[0], name, StringComparison.Ordinal))
            .ToList();

        return kept.Count == 0 ? pathAndQuery[..question] : pathAndQuery[..question] + "?" + string.Join('&', kept);
    }

    AuthorizeDecision NotSignedIn(bool promptNone, string pathAndQuery, string reason) {
        if (promptNone) {
            GrantLog.AuthorizationRequestRefused(logger, Guid.Empty, OpenIddictConstants.Errors.LoginRequired, reason);

            return new AuthorizeDecision.Refuse(
                OpenIddictConstants.Errors.LoginRequired,
                "The person is not signed in."
            );
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
            .Where(static pair => pair is not null)
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
            .Where(static x => !string.Equals(x, OpenIddictConstants.PromptValues.Login, StringComparison.Ordinal))
            .ToList();

        return values.Count == 0 ? null : "prompt=" + Uri.EscapeDataString(string.Join(' ', values));
    }
}
