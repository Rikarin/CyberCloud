using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using System.Net;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The person's path, over HTTP, against the real host: a password and a delivered code on the
///     sign-in API, <c>/authorize</c> with the cookie, <c>/token</c> with PKCE, the refresh cookie
///     and its <c>Origin</c> rule, the replay, a restart on the same keys, and <c>/logout</c>.
///     docs/plan/11 § Protocol, § Sessions and revocation; docs/plan/10 § Authentication inputs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One story rather than a sweep, because the state carries.</b> A refresh needs the
///         cookie the exchange set; the replay needs the cookie the refresh retired; the sign-out
///         needs a session to end. Splitting the steps into independent facts would mean repeating
///         the sign-in and the exchange in each, and asserting the same cookie four times. The
///         independent facts — an unregistered redirect URI, a foreign origin — are their own tests
///         below.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is one a browser would have made and nothing else could.</b>
///         The handler order, the <c>Set-Cookie</c> attributes, the CORS headers, the shape of the
///         JSON body and the absence of the refresh token from it are properties of the wire, and
///         <c>AuthorizeHandlerTests</c> and <c>TokenApiTests</c> — which drive the decisions
///         directly — cannot see any of them.
///     </para>
/// </remarks>
[Collection(IdentityHostSuite.Name)]
public sealed class GrantsOverHttpTests(IdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Scope = "openid profile offline_access cyc.api";

    [Fact]
    public async Task APersonSignsInHoldsATokenRefreshesSurvivesARestartAndSignsOut() {
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);

        var (verifier, challenge) = BrowserClient.Pkce();
        var state = BrowserClient.Base64Url(Guid.NewGuid().ToByteArray());
        var authorize = AuthorizePath(challenge, state, tenant: IdentityHostFixture.Slug);

        // ── 1. No cookie: /authorize sends the person to the sign-in page, with itself as the return URL.
        using var unauthenticated = await browser.GetAsync(authorize, Ct);

        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var signInPage = BrowserClient.Location(unauthenticated);

        signInPage.GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.SignInPagePath);

        // ⚠ The return URL is the /authorize request itself — path and query, byte for byte — and
        // a same-origin path rather than an absolute URL, because that is what the page's
        // sanitizer accepts and what its dev-server proxy forwards back here.
        BrowserClient.Query(signInPage)["returnUrl"].ShouldBe(authorize);

        // ── 2. The first factor: a password, naming the tenant by slug.
        using var password = await browser.PostJsonAsync(
            "/api/signin/password",
            new { email = IdentityHostFixture.Email, password = IdentityHostFixture.Password, returnUrl = authorize, tenant = IdentityHostFixture.Slug },
            Ct
        );

        password.StatusCode.ShouldBe(HttpStatusCode.OK);

        var first = await BrowserClient.JsonAsync(password, Ct);

        first.GetProperty("succeeded").GetBoolean().ShouldBeTrue(first.GetRawText());
        first.GetProperty("secondFactorRequired").GetBoolean().ShouldBeTrue("a password owes a second factor");
        first.GetProperty("returnUrl").GetString().ShouldBe(authorize);
        browser.Cookies.ShouldContainKey(IdentityHostAuthentication.CookieName);

        // ── 3. A pending second factor is not a session /authorize will mint from.
        using var pending = await browser.GetAsync(authorize, Ct);

        pending.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        BrowserClient.Location(pending).GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.SignInPagePath);

        // ── 4. The second factor: the code the silo delivered through the seam.
        using var send = await browser.PostJsonAsync("/api/signin/otp/send", new { returnUrl = authorize }, Ct);

        (await BrowserClient.JsonAsync(send, Ct)).GetProperty("message").GetString().ShouldBe(UniformFailures.OtpSent);

        var code = fixture.Otp.LastCode;
        code.ShouldNotBeNull("the silo delivered no code through IOtpDeliverySeam");

        using var otp = await browser.PostJsonAsync("/api/signin/otp", new { code, returnUrl = authorize }, Ct);

        var second = await BrowserClient.JsonAsync(otp, Ct);

        second.GetProperty("succeeded").GetBoolean().ShouldBeTrue(second.GetRawText());
        second.GetProperty("secondFactorRequired").GetBoolean().ShouldBeFalse();

        // ── 5. /authorize now mints a code and sends it to the portal's callback.
        using var authorized = await browser.GetAsync(authorize, Ct);

        authorized.StatusCode.ShouldBe(HttpStatusCode.Redirect, await authorized.Content.ReadAsStringAsync(Ct));

        var callback = BrowserClient.Location(authorized);

        callback.GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.PortalRedirectUri);

        var callbackQuery = BrowserClient.Query(callback);

        callbackQuery["state"].ShouldBe(state);
        callbackQuery.ShouldContainKey("code");
        callbackQuery["iss"].TrimEnd('/').ShouldBe(fixture.BaseAddress.GetLeftPart(UriPartial.Authority), "OpenIddict stamps the issuer on the redirect");

        // ── 6. The exchange: a simple cross-origin POST with credentials, answered with CORS headers,
        //       an access token in the body and the refresh token in a cookie.
        using var exchanged = await browser.PostFormAsync(
            IdentityHostOpenIddict.TokenPath,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "authorization_code",
                ["client_id"] = FirstPartyClients.Portal,
                ["redirect_uri"] = IdentityHostFixture.PortalRedirectUri,
                ["code"] = callbackQuery["code"],
                ["code_verifier"] = verifier
            },
            Ct
        );

        var tokens = await BrowserClient.JsonAsync(exchanged, Ct);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());
        BrowserClient.Header(exchanged, "Access-Control-Allow-Origin").ShouldBe(IdentityHostFixture.PortalOrigin);
        BrowserClient.Header(exchanged, "Access-Control-Allow-Credentials").ShouldBe("true");

        tokens.GetProperty("token_type").GetString().ShouldBe("Bearer");
        // ⚠ 599 or 600: OpenIddict computes it from the token's exp minus now, and a second can
        // tick between minting and answering.
        tokens.GetProperty("expires_in").GetInt32().ShouldBeInRange((int)AccessTokenPolicy.AccessTokenLifetime.TotalSeconds - 2, (int)AccessTokenPolicy.AccessTokenLifetime.TotalSeconds);
        // RFC 6749 § 5.1: `scope` is optional when it equals what was asked for, and OpenIddict
        // omits it then. Present or not, it may not differ.
        if (tokens.TryGetProperty("scope", out var granted)) {
            granted.GetString().ShouldBe(Scope);
        }
        tokens.TryGetProperty("refresh_token", out _).ShouldBeFalse("the browser client's refresh token belongs in the cookie, not the body");

        var refreshCookie = BrowserClient.SetCookieHeader(exchanged, RefreshCookie.Name);

        refreshCookie.ShouldNotBeNull("no __Host-cyc-refresh cookie was set at the exchange");
        refreshCookie.ShouldContain("httponly", Case.Insensitive);
        refreshCookie.ShouldContain("secure", Case.Insensitive);
        refreshCookie.ShouldContain("samesite=lax", Case.Insensitive);
        refreshCookie.ShouldContain("path=/", Case.Insensitive);
        refreshCookie.ShouldContain("max-age=1209600", Case.Insensitive);

        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var access = BrowserClient.Payload(accessToken);

        TestContext.Current.TestOutputHelper?.WriteLine("access token payload: " + access.GetRawText());

        // ⚠ The serialized token, not the principal: exactly the closed set, and none of the
        // claims that ride beside it in the refresh token or the id_token.
        var accessClaims = access.EnumerateObject().Select(x => x.Name).ToList();

        accessClaims.ShouldAllBe(x => AccessTokenClaims.Permitted.Contains(x), "a claim outside AccessTokenClaims.Permitted reached the wire: " + string.Join(", ", accessClaims));
        accessClaims.ShouldNotContain(AccessTokenPrincipalFactory.RefreshHandleClaim);
        accessClaims.ShouldNotContain(AccessTokenPrincipalFactory.InteractiveSessionClaim);
        accessClaims.ShouldNotContain("email");
        accessClaims.ShouldNotContain("name");

        access.GetProperty(AccessTokenClaims.TenantId).GetString().ShouldBe(IdentityHostFixture.Tenant.ToString("N"));
        access.GetProperty(AccessTokenClaims.Subject).GetString().ShouldBe(fixture.UserId.ToString("N"));
        access.GetProperty(AccessTokenClaims.SubjectType).GetString().ShouldBe(SubjectTypes.User);
        access.GetProperty(AccessTokenClaims.Audience).GetString().ShouldBe(AccessTokenPolicy.Audience);
        access.GetProperty(AccessTokenClaims.AuthorizedParty).GetString().ShouldBe(FirstPartyClients.Portal);
        access.GetProperty(AccessTokenClaims.Scope).GetString().ShouldBe(Scope);

        var amr = access.GetProperty(AccessTokenClaims.AuthenticationMethods).EnumerateArray().Select(x => x.GetString()).ToList();

        amr.ShouldBe(["pwd", "otp"], "the token session carries the interactive session's methods");

        var tokenSessionId = Guid.ParseExact(access.GetProperty(AccessTokenClaims.SessionId).GetString()!, "N");
        var authTime = access.GetProperty(AccessTokenClaims.AuthenticationTime).GetInt64();

        // The token session is a real grain, open, tracked on the user, and not the cookie session.
        var tokenSession = await fixture.For(IdentityHostFixture.Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(tokenSessionId)).GetAsync();

        tokenSession.IsSuccess.ShouldBeTrue();
        tokenSession.GetValueOrThrow().ClientId.ShouldBe(FirstPartyClients.Portal);
        tokenSession.GetValueOrThrow().IsLive.ShouldBeTrue();

        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<IUserGrain>(GrainKeys.User(fixture.UserId)).ListSessionsAsync())
            .GetValueOrThrow()
            .ShouldContain(tokenSessionId);

        // The id_token carries what the portal shows and the access token does not.
        var id = BrowserClient.Payload(tokens.GetProperty("id_token").GetString()!);

        id.GetProperty("email").GetString().ShouldBe(IdentityHostFixture.Email);
        id.GetProperty("name").GetString().ShouldBe(IdentityHostFixture.DisplayName);
        id.GetProperty(AccessTokenClaims.TenantId).GetString().ShouldBe(IdentityHostFixture.Tenant.ToString("N"));
        id.GetProperty("aud").GetString().ShouldBe(FirstPartyClients.Portal);
        id.GetProperty("nonce").GetString().ShouldBe("n-" + state);

        // ── 7. A refresh from the cookie: no refresh_token in the body, the Origin is the portal's.
        var firstRefreshCookie = browser.Cookies[RefreshCookie.Name];

        using var refreshed = await Refresh(browser);

        var refreshedTokens = await BrowserClient.JsonAsync(refreshed, Ct);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, refreshedTokens.GetRawText());
        refreshedTokens.TryGetProperty("refresh_token", out _).ShouldBeFalse();
        browser.Cookies[RefreshCookie.Name].ShouldNotBe(firstRefreshCookie, "the refresh cookie was not rotated");

        var refreshedAccess = BrowserClient.Payload(refreshedTokens.GetProperty("access_token").GetString()!);

        refreshedAccess.GetProperty(AccessTokenClaims.SessionId).GetString().ShouldBe(tokenSessionId.ToString("N"), "a refresh keeps the token session");
        refreshedAccess.GetProperty(AccessTokenClaims.AuthenticationTime).GetInt64().ShouldBe(authTime, "auth_time is carried, not recomputed");
        refreshedAccess.EnumerateObject().Select(x => x.Name).ShouldAllBe(x => AccessTokenClaims.Permitted.Contains(x));

        // ── 8. A restart on the same key directory: the earlier token still verifies against the same
        //       key set, and the refresh cookie — encrypted with the persisted encryption key — still works.
        using var jwksBefore = await browser.GetAsync(AccessTokenPolicy.JsonWebKeySetPath, Ct);
        var keysBefore = await jwksBefore.Content.ReadAsStringAsync(Ct);

        await fixture.RestartHostAsync();

        using var restarted = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);
        restarted.SetCookie(RefreshCookie.Name, browser.Cookies[RefreshCookie.Name]);
        restarted.SetCookie(IdentityHostAuthentication.CookieName, browser.Cookies[IdentityHostAuthentication.CookieName]);

        using var jwksAfter = await restarted.GetAsync(AccessTokenPolicy.JsonWebKeySetPath, Ct);

        (await jwksAfter.Content.ReadAsStringAsync(Ct)).ShouldBe(keysBefore, "the restarted host published a different key set");

        using var refreshedAfterRestart = await Refresh(restarted);

        refreshedAfterRestart.StatusCode.ShouldBe(HttpStatusCode.OK, await refreshedAfterRestart.Content.ReadAsStringAsync(Ct));

        // ── 9. The replay: the cookie the refresh above retired is refused, and the whole chain with it.
        var retired = browser.Cookies[RefreshCookie.Name];
        var live = restarted.Cookies[RefreshCookie.Name];

        restarted.SetCookie(RefreshCookie.Name, retired);

        using var replayed = await Refresh(restarted);

        replayed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BrowserClient.JsonAsync(replayed, Ct)).GetProperty("error").GetString().ShouldBe("invalid_grant");
        BrowserClient.SetCookieHeader(replayed, RefreshCookie.Name)!.ShouldContain("max-age=0", Case.Insensitive, "a refused refresh clears the cookie");

        restarted.SetCookie(RefreshCookie.Name, live);

        using var afterReplay = await Refresh(restarted);

        afterReplay.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the legitimate generation survived a replay, so the chain was not revoked");

        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(tokenSessionId)).IsLiveAsync())
            .GetValueOrThrow()
            .ShouldBeFalse();

        // ── 10. A fresh code exchange on the same cookie session works — the cookie session is live,
        //        and it is the token session, not the sign-in, that the replay ended.
        var (verifier2, challenge2) = BrowserClient.Pkce();

        using var authorizedAgain = await restarted.GetAsync(AuthorizePath(challenge2, "s2", tenant: IdentityHostFixture.Tenant.ToString("D")), Ct);

        authorizedAgain.StatusCode.ShouldBe(HttpStatusCode.Redirect, await authorizedAgain.Content.ReadAsStringAsync(Ct));

        using var exchangedAgain = await restarted.PostFormAsync(
            IdentityHostOpenIddict.TokenPath,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "authorization_code",
                ["client_id"] = FirstPartyClients.Portal,
                ["redirect_uri"] = IdentityHostFixture.PortalRedirectUri,
                ["code"] = BrowserClient.Query(BrowserClient.Location(authorizedAgain))["code"],
                ["code_verifier"] = verifier2
            },
            Ct
        );

        exchangedAgain.StatusCode.ShouldBe(HttpStatusCode.OK, await exchangedAgain.Content.ReadAsStringAsync(Ct));
        restarted.Cookies.ShouldContainKey(RefreshCookie.Name);

        // ── 11. Sign out: the session cookie and the refresh cookie are cleared, the interactive
        //        session is revoked, and the browser lands on the portal with the state echoed.
        using var loggedOut = await restarted.GetAsync(
            IdentityHostOpenIddict.EndSessionPath
            + "?client_id=" + FirstPartyClients.Portal
            + "&post_logout_redirect_uri=" + Uri.EscapeDataString(FirstPartyClients.DevelopmentPortalPostLogoutRedirectUri)
            + "&state=bye",
            Ct
        );

        loggedOut.StatusCode.ShouldBe(HttpStatusCode.Redirect, await loggedOut.Content.ReadAsStringAsync(Ct));

        var landed = BrowserClient.Location(loggedOut);

        landed.GetLeftPart(UriPartial.Path).ShouldBe(FirstPartyClients.DevelopmentPortalPostLogoutRedirectUri);
        BrowserClient.Query(landed)["state"].ShouldBe("bye");
        restarted.Cookies.ShouldNotContainKey(IdentityHostAuthentication.CookieName, "the session cookie survived /logout");
        restarted.Cookies.ShouldNotContainKey(RefreshCookie.Name, "the refresh cookie survived /logout");

        // And the token session opened at step 10 dies at its next refresh, because its sign-in is gone.
        var orphaned = BrowserClient.SetCookieHeader(exchangedAgain, RefreshCookie.Name)!;
        restarted.SetCookie(RefreshCookie.Name, orphaned[(RefreshCookie.Name.Length + 1)..orphaned.IndexOf(';', StringComparison.Ordinal)]);

        using var afterLogout = await Refresh(restarted);

        afterLogout.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a token session outlived the sign-in it was bound to");
        (await BrowserClient.JsonAsync(afterLogout, Ct)).GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task AnUnregisteredRedirectUriIsRefusedWithoutARedirect() {
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);
        var (_, challenge) = BrowserClient.Pkce();

        // ⚠ THE OPEN REDIRECT THAT IS NOT. A redirect_uri nobody registered must be answered on this
        // origin — an error page — and never by a redirect carrying anything to the URI the caller
        // chose. OpenIddict does that only for a request whose VALIDATION failed, which is why the
        // redirect-URI check lives in the validator and not in the passthrough.
        using var refused = await browser.GetAsync(
            AuthorizePath(challenge, "s", tenant: IdentityHostFixture.Slug, redirectUri: "https://evil.example/callback"),
            Ct
        );

        refused.StatusCode.ShouldNotBe(HttpStatusCode.Redirect);
        refused.Headers.Location.ShouldBeNull();

        var body = await refused.Content.ReadAsStringAsync(Ct);

        body.ShouldContain("invalid_request");
        body.ShouldContain("redirect_uri");
    }

    [Fact]
    public async Task AnUnknownTenantOnAuthorizeIsInvalidRequest() {
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);
        var (_, challenge) = BrowserClient.Pkce();

        using var refused = await browser.GetAsync(AuthorizePath(challenge, "s", tenant: "no-such-tenant"), Ct);

        refused.StatusCode.ShouldNotBe(HttpStatusCode.Redirect);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("tenant");
    }

    [Fact]
    public async Task ACookieBorneRefreshFromAForeignOriginIsRefusedBeforeAnything() {
        // A page on the gateway's origin — or a tenant's subdomain, which is same-site with this
        // host and whose POST a Lax cookie rides along with — presents the person's cookie. The
        // Origin header is the one thing such a page cannot set, and it is checked first.
        using var foreign = new BrowserClient(fixture.BaseAddress, "http://localhost:5100");
        foreign.SetCookie(RefreshCookie.Name, "not-even-read");

        using var refused = await Refresh(foreign);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await BrowserClient.JsonAsync(refused, Ct);

        body.GetProperty("error").GetString().ShouldBe("invalid_request");
        body.GetProperty("error_description").GetString().ShouldBe(DegradedModeHandlers.ExtractRefreshTokenFromCookie.OriginNotAllowed);

        // No CORS headers for an origin outside the first-party list, and the cookie is left alone:
        // a foreign page must not be able to sign the person out of the portal either.
        BrowserClient.Header(refused, "Access-Control-Allow-Origin").ShouldBeNull();
        BrowserClient.SetCookieHeader(refused, RefreshCookie.Name).ShouldBeNull();
    }

    [Fact]
    public async Task ARefreshWithNoCookieIsTheMissingParameterAnswerThePortalsFirstLoadExpects() {
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);

        using var refused = await Refresh(browser);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BrowserClient.JsonAsync(refused, Ct)).GetProperty("error").GetString().ShouldBe("invalid_request");
        BrowserClient.Header(refused, "Access-Control-Allow-Origin").ShouldBe(IdentityHostFixture.PortalOrigin, "the portal reads this error cross-origin");
    }

    [Fact]
    public async Task AnUnknownTenantOnThePasswordEndpointIsTheUniformFailure() {
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);

        using var refused = await browser.PostJsonAsync(
            "/api/signin/password",
            new { email = IdentityHostFixture.Email, password = IdentityHostFixture.Password, returnUrl = "/", tenant = "no-such-tenant" },
            Ct
        );

        refused.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await BrowserClient.JsonAsync(refused, Ct);

        body.GetProperty("succeeded").GetBoolean().ShouldBeFalse();
        body.GetProperty("message").GetString().ShouldBe(UniformFailures.SignIn);
        browser.Cookies.ShouldNotContainKey(IdentityHostAuthentication.CookieName);
    }

    [Fact]
    public async Task TheDiscoveryDocumentAdvertisesTheFlowsThePortalNeeds() {
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);

        using var discovery = await browser.GetAsync(AccessTokenPolicy.DiscoveryPath, Ct);
        var document = await BrowserClient.JsonAsync(discovery, Ct);

        var origin = fixture.BaseAddress.GetLeftPart(UriPartial.Authority);

        document.GetProperty("authorization_endpoint").GetString().ShouldBe(origin + IdentityHostOpenIddict.AuthorizationPath);
        document.GetProperty("token_endpoint").GetString().ShouldBe(origin + IdentityHostOpenIddict.TokenPath);
        document.GetProperty("end_session_endpoint").GetString().ShouldBe(origin + IdentityHostOpenIddict.EndSessionPath);
        document.GetProperty("jwks_uri").GetString().ShouldBe(origin + AccessTokenPolicy.JsonWebKeySetPath);
        document.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()).ShouldBe(["S256"]);

        var grants = document.GetProperty("grant_types_supported").EnumerateArray().Select(x => x.GetString()).ToList();

        grants.ShouldContain("authorization_code");
        grants.ShouldContain("refresh_token");
        grants.ShouldContain("client_credentials");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    static string AuthorizePath(string challenge, string state, string tenant, string redirectUri = IdentityHostFixture.PortalRedirectUri) =>
        IdentityHostOpenIddict.AuthorizationPath
        + "?response_type=code"
        + "&client_id=" + FirstPartyClients.Portal
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&scope=" + Uri.EscapeDataString(Scope)
        + "&state=" + state
        + "&code_challenge=" + challenge
        + "&code_challenge_method=S256"
        + "&nonce=n-" + state
        + "&tenant=" + Uri.EscapeDataString(tenant);

    static Task<HttpResponseMessage> Refresh(BrowserClient browser) =>
        browser.PostFormAsync(
            IdentityHostOpenIddict.TokenPath,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "refresh_token",
                ["client_id"] = FirstPartyClients.Portal
            },
            Ct
        );
}
