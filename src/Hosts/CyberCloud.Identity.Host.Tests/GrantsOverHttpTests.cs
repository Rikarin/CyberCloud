using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.RateLimiting;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using System.Net;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The person's path, over HTTP, against the real host: a password and a delivered code on the
///     sign-in API, <c>/authorize</c> with the cookie, <c>/token</c> with PKCE, the refresh cookie
///     and its <c>Origin</c> rule on both sides, the verifier and the one-time-use record binding a
///     code to its tab, the replay of both, a restart on the same keys, <c>/logout</c>,
///     <c>/userinfo</c>, the consent page's round trip for a tenant's client, its secret, and the
///     per-IP buckets. docs/plan/11 § Protocol, § Sessions and revocation, § Credentials;
///     docs/plan/10 § Authentication inputs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One story rather than a sweep, because the state carries.</b> A refresh needs the
///         cookie the exchange set; the replay needs the cookie the refresh retired; the sign-out
///         needs a session to end. Splitting the steps into independent facts would mean repeating
///         the sign-in and the exchange in each, and asserting the same cookie four times. The
///         independent facts — an unregistered redirect URI, a foreign origin on either side of
///         the cookie, the PKCE checks — are their own tests below, over <see cref="SignInAsync" />.
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
        // The return URL comes back with the tenant the cookie is for stamped in — the slug the
        // person typed becomes the tenant's id, so the resumed /authorize names exactly the tenant
        // the session was opened in. SignInApi.WithTenantWhenResuming says why the alternative loops.
        var resumed = first.GetProperty("returnUrl").GetString()!;

        resumed.ShouldBe(AuthorizePath(challenge, state, tenant: IdentityHostFixture.Tenant.ToString("D")));
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

        // ── 5. /authorize — the request as the sign-in response returned it, which is what the
        //       page navigates to — now mints a code and sends it to the portal's callback.
        using var authorized = await browser.GetAsync(resumed, Ct);

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
    public async Task AnUnknownTenantOnAuthorizeSendsThePortalToSignInWithoutTheHint() {
        // The portal remembers the last tenant in a cookie; a developer's second `dotnet run` has an
        // empty durable tier, so that tenant is gone. The first version of this test asserted the
        // error page, and the error page is what a person saw with nowhere to go. Now: the sign-in
        // page, with the hint removed so it asks for the organisation, and the rest of the request
        // — state, challenge, redirect_uri — byte for byte, so the exchange still works afterwards.
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);
        var (_, challenge) = BrowserClient.Pkce();
        var request = AuthorizePath(challenge, "s", tenant: "no-such-tenant");

        using var redirected = await browser.GetAsync(request, Ct);

        redirected.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var signInPage = redirected.Headers.Location!.ToString();

        signInPage.ShouldStartWith(IdentityHostFixture.SignInPageBaseUri + "/signin?returnUrl=");
        BrowserClient.Query(new Uri(signInPage))["returnUrl"].ShouldBe(request.Replace("&tenant=no-such-tenant", "", StringComparison.Ordinal));

        // A tenant's own client has no registration outside its tenant, so for it the error page is
        // still the only honest answer — there is no redirect_uri to validate.
        using var refused = await browser.GetAsync(
            AuthorizePath(challenge, "s", tenant: "no-such-tenant").Replace("client_id=" + FirstPartyClients.Portal, "client_id=some-tenant-app", StringComparison.Ordinal),
            Ct
        );

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
    public async Task ACodeExchangeFromAForeignOriginPlantsNoCookie() {
        // ⚠ THE LOGIN-CSRF. An attacker who signed in as THEMSELVES holds a code and its verifier.
        // A form on their page, POSTed top-level from the victim's browser, would exchange it here
        // — and Set-Cookie on a top-level cross-site POST is honoured whatever SameSite says, so
        // the attacker's refresh cookie would land in the victim's browser and the victim's next
        // silent refresh would sign them into the attacker's tenant. The portal's state check on
        // /auth/callback never sees this path; the Origin header, which a page cannot forge, is
        // what refuses it — before a token session is opened.
        using var browser = await SignInAsync();
        var (verifier, challenge) = BrowserClient.Pkce();
        var code = await CodeAsync(browser, challenge, "s-foreign");

        browser.Origin = "http://evil.example";

        using var refused = await Exchange(browser, code, verifier);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));

        var body = await BrowserClient.JsonAsync(refused, Ct);

        body.GetProperty("error").GetString().ShouldBe("invalid_request");
        body.GetProperty("error_description").GetString().ShouldBe(DegradedModeHandlers.ValidateTokenRequest.OriginNotAllowed);
        BrowserClient.SetCookieHeader(refused, RefreshCookie.Name).ShouldBeNull("a refresh cookie was planted from a foreign origin");
        BrowserClient.Header(refused, "Access-Control-Allow-Origin").ShouldBeNull();
        body.TryGetProperty("access_token", out _).ShouldBeFalse();

        // The same code from the portal's own origin: exchanged, cookie set. Nothing about the code
        // was consumed by the refusal, because nothing was minted.
        browser.Origin = IdentityHostFixture.PortalOrigin;

        using var exchanged = await Exchange(browser, code, verifier);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, await exchanged.Content.ReadAsStringAsync(Ct));
        browser.Cookies.ShouldContainKey(RefreshCookie.Name);

        // And the body-borne refresh is refused from a foreign origin too — an attacker's own
        // token in an attacker's own form is the same shape as the CLI's — with no rotation, so
        // the honest chain is untouched.
        var refreshToken = browser.Cookies[RefreshCookie.Name];

        browser.Origin = "http://evil.example";

        using var refusedRefresh = await browser.PostFormAsync(
            IdentityHostOpenIddict.TokenPath,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "refresh_token",
                ["client_id"] = FirstPartyClients.Portal,
                ["refresh_token"] = refreshToken
            },
            Ct
        );

        refusedRefresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BrowserClient.JsonAsync(refusedRefresh, Ct)).GetProperty("error_description").GetString().ShouldBe(DegradedModeHandlers.ValidateTokenRequest.OriginNotAllowed);
        BrowserClient.SetCookieHeader(refusedRefresh, RefreshCookie.Name).ShouldBeNull("a refresh cookie was written, or cleared, from a foreign origin");

        browser.Origin = IdentityHostFixture.PortalOrigin;

        using var stillLive = await Refresh(browser);

        stillLive.StatusCode.ShouldBe(HttpStatusCode.OK, "the refused request rotated the chain, so the honest refresh read as a replay");
    }

    [Fact]
    public async Task TheVerifierIsWhatBindsACodeToTheTabThatAskedForIt() {
        // ⚠ The verifier check is OpenIddict's ValidateCodeVerifier, which DegradedModeHandlers'
        // remarks say survives degraded mode without a filter. This test is what makes that claim
        // cost something: an OpenIddict upgrade or a handler-order change that dropped it would fail
        // here, not in a browser. It was the ONLY binding until #94's code store; it is still the
        // one that refuses a stolen code BEFORE it is burnt, so a thief's attempt costs the
        // legitimate tab nothing.
        using var browser = await SignInAsync();
        var (verifier, challenge) = BrowserClient.Pkce();
        var code = await CodeAsync(browser, challenge, "s-pkce");

        var (wrongVerifier, _) = BrowserClient.Pkce();

        await ShouldRefuse(Exchange(browser, code, wrongVerifier), "invalid_grant", "a wrong code_verifier was accepted");
        await ShouldRefuse(Exchange(browser, code, verifier: null), "invalid_request", "a missing code_verifier was accepted");
        await ShouldRefuse(Exchange(browser, code, verifier, redirectUri: "http://localhost:4200/elsewhere"), "invalid_grant", "a redirect_uri other than the one the code was issued for was accepted");
        await ShouldRefuse(Exchange(browser, code, verifier, clientId: FirstPartyClients.Cli), "invalid_grant", "a client other than the code's presenter was accepted");

        // ⚠ Four refusals, and the code is still unburnt: every one of them is answered before
        // TokenApi.MintForCodeAsync runs, so a thief guessing verifiers cannot spend the code the
        // legitimate tab is about to present. The right verifier, from the right client, to the
        // right URI: the token.
        using var exchanged = await Exchange(browser, code, verifier);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, await exchanged.Content.ReadAsStringAsync(Ct));

        // And once: AReplayedCodeIsRefusedAndRevokesTheSessionTheFirstExchangeOpened is the replay.
        await ShouldRefuse(Exchange(browser, code, verifier), "invalid_grant", "a code was exchanged twice");
    }

    [Fact]
    public async Task AReplayedCodeIsRefusedAndRevokesTheSessionTheFirstExchangeOpened() {
        // ⚠ RFC 6749 § 4.1.2, both halves: "MUST deny the request" and "SHOULD revoke all tokens
        // previously issued based on that authorization code". The second half is the one worth a
        // test — a store that only refused the replay would leave whoever exchanged the code FIRST
        // holding a live session, which under the hostile reading is the thief.
        using var browser = await SignInAsync();
        var (verifier, challenge) = BrowserClient.Pkce();
        var code = await CodeAsync(browser, challenge, "s-replay");

        using var exchanged = await Exchange(browser, code, verifier);

        var tokens = await BrowserClient.JsonAsync(exchanged, Ct);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());

        var tokenSessionId = Guid.ParseExact(BrowserClient.Payload(tokens.GetProperty("access_token").GetString()!).GetProperty(AccessTokenClaims.SessionId).GetString()!, "N");
        var tokenSession = fixture.For(IdentityHostFixture.Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(tokenSessionId));

        (await tokenSession.IsLiveAsync()).GetValueOrThrow().ShouldBeTrue();
        browser.Cookies.ShouldContainKey(RefreshCookie.Name);

        // ── The replay: the same code, the same verifier, the same tab. ─────────────────────────
        using var replayed = await Exchange(browser, code, verifier);

        replayed.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await replayed.Content.ReadAsStringAsync(Ct));

        var body = await BrowserClient.JsonAsync(replayed, Ct);

        body.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.GetProperty("error_description").GetString().ShouldBe(TokenApi.CodeReplayedDescription);
        body.TryGetProperty("access_token", out _).ShouldBeFalse();

        // ⚠ THE ASSERTION THAT MATTERS: the session the FIRST exchange opened is dead, and so is the
        // refresh chain the portal is holding for it.
        (await tokenSession.IsLiveAsync()).GetValueOrThrow().ShouldBeFalse("the replay was refused but the first exchange's session survived");
        (await tokenSession.GetAsync()).GetValueOrThrow().RevokedBecause.ShouldBe(RevocationReason.AuthorizationCodeReuseDetected);

        using var refreshed = await Refresh(browser);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the refresh chain of a session revoked for code reuse still rotated");
        (await BrowserClient.JsonAsync(refreshed, Ct)).GetProperty("error").GetString().ShouldBe("invalid_grant");

        // The interactive session is untouched — it is the token session that died — so the person
        // is one /authorize away from a fresh code, which mints and exchanges once more.
        var (verifier2, challenge2) = BrowserClient.Pkce();
        var code2 = await CodeAsync(browser, challenge2, "s-replay-2");

        using var exchangedAgain = await Exchange(browser, code2, verifier2);

        exchangedAgain.StatusCode.ShouldBe(HttpStatusCode.OK, await exchangedAgain.Content.ReadAsStringAsync(Ct));

        // ⚠ The record itself is not asserted on by key: the code is an encrypted envelope and its
        // id (oi_tkn_id) is inside it, readable by nobody but this server — which is the point of
        // keying the store by the id rather than by the code (GrainKeys.AuthorizationCode). The
        // refusal above and the dead session are what the record exists for;
        // AuthorizationCodeReuseTests drives the grain directly.
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
        document.GetProperty("userinfo_endpoint").GetString().ShouldBe(origin + IdentityHostOpenIddict.UserInfoPath, "OIDC Core § 5.3 wants /userinfo advertised, and #94 mapped it");
        document.GetProperty("jwks_uri").GetString().ShouldBe(origin + AccessTokenPolicy.JsonWebKeySetPath);
        document.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()).ShouldBe(["S256"]);

        var grants = document.GetProperty("grant_types_supported").EnumerateArray().Select(x => x.GetString()).ToList();

        grants.ShouldContain("authorization_code");
        grants.ShouldContain("refresh_token");
        grants.ShouldContain("client_credentials");
    }

    [Fact]
    public async Task UserInfoAnswersTheSessionsClaimsAndDiesWithTheSession() {
        using var browser = await SignInAsync();
        var (verifier, challenge) = BrowserClient.Pkce();
        var code = await CodeAsync(browser, challenge, "s-userinfo");

        using var exchanged = await Exchange(browser, code, verifier);

        var tokens = await BrowserClient.JsonAsync(exchanged, Ct);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());

        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var access = BrowserClient.Payload(accessToken);

        // ── No token: RFC 6750's challenge, and nothing a client could read as claims. ─────────
        using var anonymous = await browser.GetAsync(IdentityHostOpenIddict.UserInfoPath, Ct);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");

        // ── The token: sub MUST match the id_token's; name and email come from the user grain, as
        //    the id_token was minted from it; tid and sub_typ so a relying party can build the
        //    subject reference the gateway builds. Cross-origin from the portal, with CORS headers.
        browser.Bearer = accessToken;

        using var userinfo = await browser.GetAsync(IdentityHostOpenIddict.UserInfoPath, Ct);

        var claims = await BrowserClient.JsonAsync(userinfo, Ct);

        userinfo.StatusCode.ShouldBe(HttpStatusCode.OK, claims.GetRawText());
        BrowserClient.Header(userinfo, "Access-Control-Allow-Origin").ShouldBe(IdentityHostFixture.PortalOrigin);
        claims.GetProperty("sub").GetString().ShouldBe(access.GetProperty(AccessTokenClaims.Subject).GetString());
        claims.GetProperty("sub").GetString().ShouldBe(BrowserClient.Payload(tokens.GetProperty("id_token").GetString()!).GetProperty("sub").GetString(), "OIDC Core § 5.3.2: the sub MUST match the id_token's");
        claims.GetProperty(AccessTokenClaims.TenantId).GetString().ShouldBe(IdentityHostFixture.Tenant.ToString("N"));
        claims.GetProperty(AccessTokenClaims.SubjectType).GetString().ShouldBe(SubjectTypes.User);
        claims.GetProperty("name").GetString().ShouldBe(IdentityHostFixture.DisplayName);
        claims.GetProperty("email").GetString().ShouldBe(BrowserClient.Payload(tokens.GetProperty("id_token").GetString()!).GetProperty("email").GetString(), "the id_token and /userinfo were minted from the same grain");
        claims.GetProperty("email").GetString().ShouldEndWith("@grants.example");
        claims.EnumerateObject().Select(x => x.Name).ShouldBe(["sub", AccessTokenClaims.TenantId, AccessTokenClaims.SubjectType, "name", "email"], "the userinfo shape changed");

        // POST works too — OIDC Core § 5.3.1.
        using var posted = await browser.PostFormAsync(IdentityHostOpenIddict.UserInfoPath, new Dictionary<string, string>(StringComparer.Ordinal), Ct);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync(Ct));

        // ── The session dies: the JWT has minutes left, and /userinfo says invalid_token anyway.
        //    That is the one thing this endpoint knows that the token does not.
        var tokenSessionId = Guid.ParseExact(access.GetProperty(AccessTokenClaims.SessionId).GetString()!, "N");

        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(tokenSessionId)).RevokeAsync(RevocationReason.AdminAction))
            .IsSuccess.ShouldBeTrue();

        using var afterRevoke = await browser.GetAsync(IdentityHostOpenIddict.UserInfoPath, Ct);

        afterRevoke.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a revoked session still answered /userinfo");
        afterRevoke.Headers.WwwAuthenticate.ToString().ShouldContain("invalid_token");

        // A token this server did not sign is OpenIddict's to refuse, before the passthrough.
        browser.Bearer = accessToken[..^4] + "AAAA";

        using var forged = await browser.GetAsync(IdentityHostOpenIddict.UserInfoPath, Ct);

        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATenantClientNeedsConsentAndGetsACodeOnceItIsGiven() {
        // A client registered IN the tenant — resolved through its index, not the static list — is
        // not consent-free. The person is sent to the consent page; the page reads what to render
        // from /api/consent and posts the answer back to /authorize; deny is access_denied to the
        // client, allow is a code, and the allowance is on record for next time.
        using var browser = await SignInAsync();
        var (verifier, challenge) = BrowserClient.Pkce();
        var authorize = AuthorizePath(challenge, "s-consent", tenant: IdentityHostFixture.Slug, redirectUri: IdentityHostFixture.TenantPublicClientRedirectUri, clientId: IdentityHostFixture.TenantPublicClient);

        // ── 1. Nothing on record: to the consent page, with the request as the return URL. ─────
        using var asked = await browser.GetAsync(authorize, Ct);

        asked.StatusCode.ShouldBe(HttpStatusCode.Redirect, await asked.Content.ReadAsStringAsync(Ct));

        var consentPage = BrowserClient.Location(asked);

        consentPage.GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.ConsentPagePath);
        BrowserClient.Query(consentPage)["returnUrl"].ShouldBe(authorize);

        // ── 2. What the page renders: the REGISTERED name, and the scopes. ─────────────────────
        using var described = await browser.GetAsync("/api/consent?returnUrl=" + Uri.EscapeDataString(authorize), Ct);

        var page = await BrowserClient.JsonAsync(described, Ct);

        described.StatusCode.ShouldBe(HttpStatusCode.OK, page.GetRawText());
        page.GetProperty("ready").GetBoolean().ShouldBeTrue(page.GetRawText());
        page.GetProperty("clientName").GetString().ShouldBe(IdentityHostFixture.TenantPublicClientName);
        page.GetProperty("scopes").EnumerateArray().Select(x => x.GetString()).ShouldBe(Scope.Split(' '));
        page.GetProperty("returnUrl").GetString().ShouldBe(authorize);

        // A request /authorize would refuse is not described either — the page renders nothing for
        // a redirect URI the registration does not carry, so a phisher's link has no page.
        using var refusedPage = await browser.GetAsync("/api/consent?returnUrl=" + Uri.EscapeDataString(authorize.Replace(Uri.EscapeDataString(IdentityHostFixture.TenantPublicClientRedirectUri), Uri.EscapeDataString("https://evil.example/cb"), StringComparison.Ordinal)), Ct);

        (await BrowserClient.JsonAsync(refusedPage, Ct)).GetProperty("ready").GetBoolean().ShouldBeFalse();

        // ── 3. consent=allow anywhere but the page's POST is no answer. ────────────────────────
        using var linked = await browser.GetAsync(authorize + "&consent=allow", Ct);

        BrowserClient.Location(linked).GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.ConsentPagePath, "a GET link pre-filled consent");

        var form = BrowserClient.Query(new Uri("http://x" + authorize));

        browser.Origin = "http://evil.example";

        using var foreignPost = await browser.PostFormAsync(IdentityHostOpenIddict.AuthorizationPath, WithConsent(form, "allow"), Ct);

        BrowserClient.Location(foreignPost).GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.ConsentPagePath, "a form on another origin granted consent with the person's cookie");

        // ── 4. Deny, from the page: access_denied at the registered redirect URI, state echoed. ─
        browser.Origin = IdentityHostFixture.SignInPageBaseUri;

        using var denied = await browser.PostFormAsync(IdentityHostOpenIddict.AuthorizationPath, WithConsent(form, "deny"), Ct);

        denied.StatusCode.ShouldBe(HttpStatusCode.Redirect, await denied.Content.ReadAsStringAsync(Ct));

        var deniedAt = BrowserClient.Location(denied);

        deniedAt.GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.TenantPublicClientRedirectUri);
        BrowserClient.Query(deniedAt)["error"].ShouldBe("access_denied");
        BrowserClient.Query(deniedAt)["state"].ShouldBe("s-consent");
        BrowserClient.Query(deniedAt).ShouldNotContainKey("code");

        // ── 5. Allow, from the page: the code, exchanged by the tenant client. ─────────────────
        using var allowed = await browser.PostFormAsync(IdentityHostOpenIddict.AuthorizationPath, WithConsent(form, "allow"), Ct);

        allowed.StatusCode.ShouldBe(HttpStatusCode.Redirect, await allowed.Content.ReadAsStringAsync(Ct));

        var callback = BrowserClient.Location(allowed);

        callback.GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.TenantPublicClientRedirectUri);
        BrowserClient.Query(callback)["state"].ShouldBe("s-consent");

        using var client = new BrowserClient(fixture.BaseAddress, "https://acme.example");
        using var exchanged = await Exchange(client, BrowserClient.Query(callback)["code"], verifier, redirectUri: IdentityHostFixture.TenantPublicClientRedirectUri, clientId: IdentityHostFixture.TenantPublicClient);

        var tokens = await BrowserClient.JsonAsync(exchanged, Ct);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());
        BrowserClient.Payload(tokens.GetProperty("access_token").GetString()!).GetProperty(AccessTokenClaims.AuthorizedParty).GetString().ShouldBe(IdentityHostFixture.TenantPublicClient);
        tokens.TryGetProperty("refresh_token", out _).ShouldBeTrue("a tenant client is not the browser client, so its refresh token stays in the body");
        BrowserClient.SetCookieHeader(exchanged, RefreshCookie.Name).ShouldBeNull();

        // ── 6. On record: the next request mints without asking; prompt=consent asks again. ───
        var (_, challenge2) = BrowserClient.Pkce();

        using var again = await browser.GetAsync(AuthorizePath(challenge2, "s-consent-2", tenant: IdentityHostFixture.Slug, redirectUri: IdentityHostFixture.TenantPublicClientRedirectUri, clientId: IdentityHostFixture.TenantPublicClient), Ct);

        BrowserClient.Location(again).GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.TenantPublicClientRedirectUri, "a consent on record still asked");
        BrowserClient.Query(BrowserClient.Location(again)).ShouldContainKey("code");

        using var reprompted = await browser.GetAsync(AuthorizePath(challenge2, "s-consent-3", tenant: IdentityHostFixture.Slug, redirectUri: IdentityHostFixture.TenantPublicClientRedirectUri, clientId: IdentityHostFixture.TenantPublicClient) + "&prompt=consent", Ct);

        BrowserClient.Location(reprompted).GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.ConsentPagePath, "prompt=consent did not ask");

        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<IConsentGrain>(GrainKeys.ConsentGrant(IdentityHostFixture.Tenant, signedInUserId, IdentityHostFixture.TenantPublicClient)).GetAsync())
            .GetValueOrThrow().Scopes.ShouldBe(Scope.Split(' '));
    }

    [Fact]
    public async Task AConfidentialClientMustPresentItsSecretOnTheCodeAndRefreshGrants() {
        // ⚠ The review's low finding, closed with the consent page because that is what made a
        // tenant client's code mintable at all: a confidential client's redirect URI may be a
        // server nobody else can read, so a code lifted from a log must still be useless without
        // the secret — RFC 6749 § 4.1.3 and § 6. One sentence for missing, wrong and unreadable.
        using var browser = await SignInAsync();
        var (verifier, challenge) = BrowserClient.Pkce();
        var authorize = AuthorizePath(challenge, "s-secret", tenant: IdentityHostFixture.Slug, redirectUri: IdentityHostFixture.TenantConfidentialClientRedirectUri, clientId: IdentityHostFixture.TenantConfidentialClient);

        using var asked = await browser.GetAsync(authorize, Ct);

        BrowserClient.Location(asked).GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.ConsentPagePath);

        browser.Origin = IdentityHostFixture.SignInPageBaseUri;

        using var allowed = await browser.PostFormAsync(IdentityHostOpenIddict.AuthorizationPath, WithConsent(BrowserClient.Query(new Uri("http://x" + authorize)), "allow"), Ct);

        var code = BrowserClient.Query(BrowserClient.Location(allowed))["code"];

        using var server = new BrowserClient(fixture.BaseAddress, "https://acme.example");

        // No secret, a wrong secret: invalid_client, the same sentence, and the code is NOT burnt —
        // the check runs at validation, before TokenApi consumes anything, so a thief's guesses
        // cost the client nothing.
        await ShouldRefuseClient(Exchange(server, code, verifier, redirectUri: IdentityHostFixture.TenantConfidentialClientRedirectUri, clientId: IdentityHostFixture.TenantConfidentialClient), "a confidential client exchanged a code with no secret");
        await ShouldRefuseClient(Exchange(server, code, verifier, redirectUri: IdentityHostFixture.TenantConfidentialClientRedirectUri, clientId: IdentityHostFixture.TenantConfidentialClient, clientSecret: "not-it"), "a confidential client exchanged a code with a wrong secret");

        using var exchanged = await Exchange(server, code, verifier, redirectUri: IdentityHostFixture.TenantConfidentialClientRedirectUri, clientId: IdentityHostFixture.TenantConfidentialClient, clientSecret: IdentityHostFixture.TenantConfidentialClientSecret);

        var tokens = await BrowserClient.JsonAsync(exchanged, Ct);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());

        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // The refresh grant too, and a refused refresh rotates nothing — the honest one afterwards
        // still works.
        await ShouldRefuseClient(RefreshInBody(server, refreshToken, IdentityHostFixture.TenantConfidentialClient), "a confidential client refreshed with no secret");
        await ShouldRefuseClient(RefreshInBody(server, refreshToken, IdentityHostFixture.TenantConfidentialClient, clientSecret: "not-it"), "a confidential client refreshed with a wrong secret");

        using var refreshed = await RefreshInBody(server, refreshToken, IdentityHostFixture.TenantConfidentialClient, clientSecret: IdentityHostFixture.TenantConfidentialClientSecret);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync(Ct));

        // And a PUBLIC client that sends a secret is misconfigured in a way worth refusing.
        var (verifier2, challenge2) = BrowserClient.Pkce();
        var publicCode = await CodeAsync(browser, challenge2, "s-public-secret");

        await ShouldRefuseClient(Exchange(browser, publicCode, verifier2, clientSecret: "a-spa-with-a-secret"), "a public client presenting a secret was accepted", "A public client must not send a client_secret.");
    }

    [Fact]
    public async Task ThePerIpLimitOnSignUpBeginTripsAndRecovers() {
        // ⚠ Per IP, so the answer depends on nothing in the body: a made-up address and a real one
        // are counted alike and refused alike, which is the uniform-failure property kept under
        // the limit — IdentityRateLimits' remarks. Every request here comes from 127.0.0.1.
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        var bucket = IdentityRateLimits.SignUpBegin;

        string? firstBody = null;

        for (var i = 0; i < bucket.Limit; i++) {
            using var admitted = await browser.PostJsonAsync("/api/signup/begin", new { email = i % 2 == 0 ? IdentityHostFixture.Email : $"nobody-{i}@grants.example", returnUrl = "/" }, Ct);

            admitted.StatusCode.ShouldBe(HttpStatusCode.OK, $"request {i + 1} of {bucket.Limit} was refused inside the window");

            var body = await admitted.Content.ReadAsStringAsync(Ct);

            (firstBody ??= body).ShouldBe(body, "the answer inside the limit differed between a real address and a made-up one");
        }

        // The (limit + 1)th: 429, Retry-After, one sentence — for a real address and for garbage.
        foreach (var email in new[] { IdentityHostFixture.Email, "not-an-address" }) {
            using var refused = await browser.PostJsonAsync("/api/signup/begin", new { email, returnUrl = "/" }, Ct);

            refused.StatusCode.ShouldBe((HttpStatusCode)429);
            refused.Headers.RetryAfter.ShouldNotBeNull();
            refused.Headers.RetryAfter!.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
            refused.Headers.RetryAfter.Delta.Value.ShouldBeLessThanOrEqualTo(bucket.Window);

            var body = await BrowserClient.JsonAsync(refused, Ct);

            body.GetProperty("message").GetString().ShouldBe(IdentityRateLimits.RefusedMessage);
            body.GetProperty("retryAfterSeconds").GetInt32().ShouldBe((int)Math.Ceiling(refused.Headers.RetryAfter.Delta.Value.TotalSeconds));
            BrowserClient.SetCookieHeader(refused, "__Host-cyc-signup").ShouldBeNull("a refused begin issued a sign-up ticket");
        }

        // ── Recovers: the window slides, and the oldest request leaves it. ─────────────────────
        fixture.Clock.Advance(bucket.Window + TimeSpan.FromSeconds(1));

        using var recovered = await browser.PostJsonAsync("/api/signup/begin", new { email = IdentityHostFixture.Email, returnUrl = "/" }, Ct);

        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, "the limit did not recover once the window passed");
        (await recovered.Content.ReadAsStringAsync(Ct)).ShouldBe(firstBody);
    }

    [Fact]
    public async Task ThePerIpLimitOnCodeVerifyTripsAcrossSignUpsAndRecovers() {
        // The grain caps guesses per code; this bucket caps them per caller across codes, so a
        // caller cannot buy more guesses by opening more sign-ups. A call with no ticket is the
        // cheapest guess there is — a body-less 401 — and it is counted like any other.
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        var bucket = IdentityRateLimits.CodeVerify;

        // Sign-up is closed on this fixture, so each guess is the closed sentence in a 200 — counted
        // all the same, because the filter runs before the handler reads anything.
        for (var i = 0; i < bucket.Limit; i++) {
            using var counted = await browser.PostJsonAsync("/api/signup/verify", new { code = "000000" }, Ct);

            counted.StatusCode.ShouldBe(HttpStatusCode.OK, $"guess {i + 1} of {bucket.Limit} was refused inside the window");
        }

        using var refused = await browser.PostJsonAsync("/api/signup/verify", new { code = "000000" }, Ct);

        refused.StatusCode.ShouldBe((HttpStatusCode)429);
        (await BrowserClient.JsonAsync(refused, Ct)).GetProperty("message").GetString().ShouldBe(IdentityRateLimits.RefusedMessage);

        // ⚠ One bucket for every code-verify endpoint: the sign-in OTP endpoint is full too, for
        // this address, though it was never called — that is what "across codes" means.
        using var otp = await browser.PostJsonAsync("/api/signin/otp", new { code = "000000", returnUrl = "/" }, Ct);

        otp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "an anonymous caller is a 401 before it is counted — filters run after authorization");

        fixture.Clock.Advance(bucket.Window + TimeSpan.FromSeconds(1));

        using var recovered = await browser.PostJsonAsync("/api/signup/verify", new { code = "000000" }, Ct);

        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, "the limit did not recover once the window passed");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The request's pairs as the consent page posts them back, plus its answer.</summary>
    static Dictionary<string, string> WithConsent(Dictionary<string, string> request, string answer) =>
        new(request, StringComparer.Ordinal) { [AuthorizeApi.ConsentParameter] = answer };

    /// <summary>A body-borne refresh — the CLI's and a tenant client's shape.</summary>
    static Task<HttpResponseMessage> RefreshInBody(BrowserClient client, string refreshToken, string clientId, string? clientSecret = null) {
        var form = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken
        };

        if (clientSecret is not null) {
            form["client_secret"] = clientSecret;
        }

        return client.PostFormAsync(IdentityHostOpenIddict.TokenPath, form, Ct);
    }

    static async Task ShouldRefuseClient(Task<HttpResponseMessage> pending, string because, string description = DegradedModeHandlers.ValidateTokenRequest.ClientNotAuthenticated) {
        using var response = await pending;
        var body = await response.Content.ReadAsStringAsync(Ct);

        // RFC 6749 § 5.2: invalid_client MAY be a 401, and OpenIddict makes it one.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, because + ": " + body);

        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("error").GetString().ShouldBe("invalid_client", because);
        json.GetProperty("error_description").GetString().ShouldBe(description, because);
    }

    /// <summary>The person the last <see cref="SignInAsync" /> signed in, and their address.</summary>
    Guid signedInUserId;

    /// <summary>
    ///     A tab on the portal's origin, signed in with the password and the delivered code — as a
    ///     fresh person each time, for the reason <c>IdentityHostFixture.CreatePersonAsync</c> gives.
    ///     <see cref="signedInUserId" /> says who.
    /// </summary>
    async Task<BrowserClient> SignInAsync() {
        var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.PortalOrigin);
        var email = $"person-{Guid.NewGuid():N}@grants.example";

        signedInUserId = await fixture.CreatePersonAsync(email);

        using var password = await browser.PostJsonAsync(
            "/api/signin/password",
            new { email, password = IdentityHostFixture.Password, returnUrl = "/", tenant = IdentityHostFixture.Slug },
            Ct
        );

        (await BrowserClient.JsonAsync(password, Ct)).GetProperty("succeeded").GetBoolean().ShouldBeTrue();

        using var send = await browser.PostJsonAsync("/api/signin/otp/send", new { returnUrl = "/" }, Ct);

        var code = fixture.Otp.LastCode;
        code.ShouldNotBeNull("the silo delivered no code through IOtpDeliverySeam");

        using var otp = await browser.PostJsonAsync("/api/signin/otp", new { code, returnUrl = "/" }, Ct);

        var second = await BrowserClient.JsonAsync(otp, Ct);

        second.GetProperty("succeeded").GetBoolean().ShouldBeTrue(second.GetRawText());
        second.GetProperty("secondFactorRequired").GetBoolean().ShouldBeFalse();

        return browser;
    }

    /// <summary>The authorization code <c>/authorize</c> mints for a signed-in tab.</summary>
    static async Task<string> CodeAsync(BrowserClient browser, string challenge, string state) {
        using var authorized = await browser.GetAsync(AuthorizePath(challenge, state, tenant: IdentityHostFixture.Slug), Ct);

        authorized.StatusCode.ShouldBe(HttpStatusCode.Redirect, await authorized.Content.ReadAsStringAsync(Ct));

        return BrowserClient.Query(BrowserClient.Location(authorized))["code"];
    }

    /// <summary>The code exchange, with every parameter the portal sends unless a test says otherwise.</summary>
    static Task<HttpResponseMessage> Exchange(
        BrowserClient browser,
        string code,
        string? verifier,
        string redirectUri = IdentityHostFixture.PortalRedirectUri,
        string clientId = FirstPartyClients.Portal,
        string? clientSecret = null
    ) {
        var form = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code
        };

        if (verifier is not null) {
            form["code_verifier"] = verifier;
        }

        if (clientSecret is not null) {
            form["client_secret"] = clientSecret;
        }

        return browser.PostFormAsync(IdentityHostOpenIddict.TokenPath, form, Ct);
    }

    static async Task ShouldRefuse(Task<HttpResponseMessage> pending, string error, string because) {
        using var response = await pending;
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, because + ": " + body);
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString().ShouldBe(error, because);
    }

    static string AuthorizePath(string challenge, string state, string tenant, string redirectUri = IdentityHostFixture.PortalRedirectUri, string clientId = FirstPartyClients.Portal) =>
        IdentityHostOpenIddict.AuthorizationPath
        + "?response_type=code"
        + "&client_id=" + clientId
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
