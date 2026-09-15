using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The code exchange and the refresh — the half of <see cref="TokenApi" /> that opens, rotates
///     and revokes session grains — plus the tenant hint and the <c>Origin</c> rule on both sides
///     of the refresh cookie: the extractor that reads it and the validator that guards its
///     writing.
/// </summary>
/// <remarks>
///     ⚠ Real grains, on purpose. "The chain is revoked" and "the token session is tracked on the
///     user" are facts about what <c>ISessionGrain</c> and <c>IUserGrain</c> did, and a double that
///     recorded the calls would prove the calls were made and nothing about their effect —
///     <c>RefreshReuseTests</c> in <c>CyberCloud.Identity.Tests</c> pins the grain's rule, and this
///     file pins that the host drives it.
/// </remarks>
public sealed partial class TokenApiTests {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly IReadOnlyList<string> AllScopes = ["openid", "profile", "offline_access", "cyc.api"];

    [Fact]
    public async Task ACodeExchangeOpensATokenSessionAndTracksIt() {
        var (code, interactive) = await CodeAsync();

        var minted = await Api.MintForCodeAsync(code, Portal, new() { DeviceLabel = "a test", ClientAddress = "127.0.0.1" }, Ct);

        minted.IsSuccess.ShouldBeTrue(minted.Error?.Message);

        var principal = minted.GetValueOrThrow();
        var tokenSession = Guid.ParseExact(principal.GetClaim(AccessTokenClaims.SessionId)!, "N");

        tokenSession.ShouldNotBe(interactive, "the token session is its own grain, not the cookie session");
        principal.GetClaim(AccessTokenPrincipalFactory.InteractiveSessionClaim).ShouldBe(interactive.ToString("N"));
        principal.GetClaim(AccessTokenPrincipalFactory.RefreshHandleClaim).ShouldNotBeNullOrEmpty();
        principal.GetClaim(AccessTokenClaims.AuthorizedParty).ShouldBe(FirstPartyClients.Portal);

        var described = (await Session(tokenSession).GetAsync()).GetValueOrThrow();

        described.IsLive.ShouldBeTrue();
        described.ClientId.ShouldBe(FirstPartyClients.Portal);
        described.UserId.ShouldBe(fixture.UserId);
        described.DeviceLabel.ShouldBe("a test");

        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<IUserGrain>(GrainKeys.User(fixture.UserId)).ListSessionsAsync())
            .GetValueOrThrow()
            .ShouldContain(tokenSession, "sign-out-everywhere has to reach the token session");
    }

    [Fact]
    public async Task TheTokenSessionCarriesTheInteractiveMethods() {
        var (code, _) = await CodeAsync();

        var principal = (await Api.MintForCodeAsync(code, Portal, new(), Ct)).GetValueOrThrow();
        var tokenSession = Guid.ParseExact(principal.GetClaim(AccessTokenClaims.SessionId)!, "N");

        // The password the grain recorded and the code the cookie stamped, both — on the grain the
        // token session was opened from, and on the token's amr.
        (await Session(tokenSession).GetAsync()).GetValueOrThrow().Methods
            .ShouldBe([AuthenticationMethod.Password, AuthenticationMethod.EmailOtp]);

        principal.FindAll(AccessTokenClaims.AuthenticationMethods).Select(x => x.Value).ShouldBe(["pwd", "otp"]);
    }

    [Fact]
    public async Task ARevokedInteractiveSessionRefusesTheCode() {
        var (code, interactive) = await CodeAsync();

        (await Session(interactive).RevokeAsync(RevocationReason.SignOut)).IsSuccess.ShouldBeTrue();

        var refused = await Api.MintForCodeAsync(code, Portal, new(), Ct);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldBe(TokenApi.SessionRevokedDescription);
    }

    [Fact]
    public async Task ARefreshRotatesThroughTheSessionGrain() {
        var (code, _) = await CodeAsync();

        var first = (await Api.MintForCodeAsync(code, Portal, new(), Ct)).GetValueOrThrow();
        var tokenSession = Guid.ParseExact(first.GetClaim(AccessTokenClaims.SessionId)!, "N");

        var refreshed = await Api.MintForRefreshAsync(first, Portal, Ct);

        refreshed.IsSuccess.ShouldBeTrue(refreshed.Error?.Message);

        var second = refreshed.GetValueOrThrow();

        second.GetClaim(AccessTokenClaims.SessionId).ShouldBe(tokenSession.ToString("N"), "a refresh keeps the token session");
        second.GetClaim(AccessTokenPrincipalFactory.RefreshHandleClaim).ShouldNotBe(first.GetClaim(AccessTokenPrincipalFactory.RefreshHandleClaim));
        second.GetClaim(AccessTokenClaims.AuthenticationTime).ShouldBe(first.GetClaim(AccessTokenClaims.AuthenticationTime), "auth_time is carried");
        second.FindAll(AccessTokenClaims.AuthenticationMethods).Select(x => x.Value).ShouldBe(["pwd", "otp"]);
        second.GetClaim(OpenIddictConstants.Claims.Email).ShouldBe(IdentityHostFixture.Email, "the id_token's claims ride the refresh token too");

        (await Session(tokenSession).ChainLengthAsync()).GetValueOrThrow().ShouldBe(2);
    }

    [Fact]
    public async Task AReplayedRefreshIsInvalidGrantAndTheChainIsRevoked() {
        var (code, _) = await CodeAsync();

        var first = (await Api.MintForCodeAsync(code, Portal, new(), Ct)).GetValueOrThrow();
        var second = (await Api.MintForRefreshAsync(first, Portal, Ct)).GetValueOrThrow();
        var tokenSession = Guid.ParseExact(first.GetClaim(AccessTokenClaims.SessionId)!, "N");

        // The retired generation, presented again.
        var replayed = await Api.MintForRefreshAsync(first, Portal, Ct);

        replayed.IsFailure.ShouldBeTrue("a retired handle was accepted");
        replayed.Error!.Message.ShouldBe(TokenApi.RefreshRejectedDescription);

        // ⚠ And the live generation dies with it — docs/plan/11 § Protocol's "revoke the whole
        // chain". The legitimate client holding `second` is signed out too, because the replay is
        // the one signal that a token was stolen and nobody can tell which holder is the thief.
        (await Api.MintForRefreshAsync(second, Portal, Ct)).IsFailure.ShouldBeTrue("the chain survived a replay");
        (await Session(tokenSession).IsLiveAsync()).GetValueOrThrow().ShouldBeFalse();
    }

    [Fact]
    public async Task ARefreshAfterLogoutRevokesTheTokenSession() {
        var (code, interactive) = await CodeAsync();

        var first = (await Api.MintForCodeAsync(code, Portal, new(), Ct)).GetValueOrThrow();
        var tokenSession = Guid.ParseExact(first.GetClaim(AccessTokenClaims.SessionId)!, "N");

        // /logout revokes the interactive session and nothing else; the token sessions bound to it
        // are found at their next refresh.
        (await Session(interactive).RevokeAsync(RevocationReason.SignOut)).IsSuccess.ShouldBeTrue();
        (await Session(tokenSession).IsLiveAsync()).GetValueOrThrow().ShouldBeTrue("nothing has touched the token session yet");

        var refused = await Api.MintForRefreshAsync(first, Portal, Ct);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldBe(TokenApi.SessionRevokedDescription);
        (await Session(tokenSession).IsLiveAsync()).GetValueOrThrow().ShouldBeFalse("the token session was left alive after its sign-in ended");
    }

    [Fact]
    public async Task ACookieBorneRefreshFromAForeignOriginNeverReachesAGrain() {
        // The extractor has no grain factory at all — its only dependency is the static client list —
        // so "never reaches a grain" is a property of its signature. What is asserted is the rule:
        // a foreign Origin is refused before the cookie is even read, and the request is left
        // without a refresh token so nothing downstream could use one.
        var handler = new DegradedModeHandlers.ExtractRefreshTokenFromCookie(fixture.Services.GetRequiredService<FirstPartyClients>());

        var http = new DefaultHttpContext();
        http.Request.Headers.Origin = "http://localhost:5100";
        http.Request.Headers.Cookie = RefreshCookie.Name + "=stolen";

        var context = Extract(http, new OpenIddictRequest { GrantType = OpenIddictConstants.GrantTypes.RefreshToken, ClientId = FirstPartyClients.Portal });

        await handler.HandleAsync(context);

        context.IsRejected.ShouldBeTrue();
        context.Error.ShouldBe(OpenIddictConstants.Errors.InvalidRequest);
        context.ErrorDescription.ShouldBe(DegradedModeHandlers.ExtractRefreshTokenFromCookie.OriginNotAllowed);
        context.Request!.RefreshToken.ShouldBeNull();

        // The portal's origin, the same cookie: copied into the request for OpenIddict to decrypt.
        http = new DefaultHttpContext();
        http.Request.Headers.Origin = IdentityHostFixture.PortalOrigin;
        http.Request.Headers.Cookie = RefreshCookie.Name + "=mine";

        context = Extract(http, new OpenIddictRequest { GrantType = OpenIddictConstants.GrantTypes.RefreshToken, ClientId = FirstPartyClients.Portal });

        await handler.HandleAsync(context);

        context.IsRejected.ShouldBeFalse();
        context.Request!.RefreshToken.ShouldBe("mine");

        // The CLI, from a process with no Origin and the token in its body: not this handler's.
        http = new DefaultHttpContext();
        http.Request.Headers.Cookie = RefreshCookie.Name + "=someone-elses";

        context = Extract(http, new OpenIddictRequest { GrantType = OpenIddictConstants.GrantTypes.RefreshToken, ClientId = FirstPartyClients.Cli, RefreshToken = "from-the-keychain" });

        await handler.HandleAsync(context);

        context.IsRejected.ShouldBeFalse();
        context.Request!.RefreshToken.ShouldBe("from-the-keychain");
    }

    [Fact]
    public async Task ABrowserClientsTokenRequestFromAForeignOriginIsRefused() {
        // ⚠ The write side of the rule the extractor above enforces on the read side. A code
        // exchange or a body-borne refresh for the portal from any origin but the portal's would
        // end in MoveRefreshTokenToCookie planting the resulting cookie in whichever browser sent
        // the request — the login-CSRF DegradedModeHandlers.ValidateTokenRequest's remarks describe
        // — so the validator refuses it before a token session is opened. The same handler that
        // answers the code grant answers the body-borne refresh, so one principal covers both.
        var handler = new DegradedModeHandlers.ValidateTokenRequest(
            Api,
            fixture.Services.GetRequiredService<IClientResolver>(),
            fixture.Services.GetRequiredService<FirstPartyClients>()
        );
        var (code, _) = await CodeAsync();

        foreach (var origin in new[] { "http://evil.example", "http://localhost:5100", "null", "" }) {
            var context = Validate(origin, new OpenIddictRequest { GrantType = OpenIddictConstants.GrantTypes.AuthorizationCode, ClientId = FirstPartyClients.Portal }, code: code);

            await handler.HandleAsync(context);

            context.IsRejected.ShouldBeTrue($"a code exchange for the portal from Origin '{origin}' was validated");
            context.Error.ShouldBe(OpenIddictConstants.Errors.InvalidRequest);
            context.ErrorDescription.ShouldBe(DegradedModeHandlers.ValidateTokenRequest.OriginNotAllowed);
            context.Transaction.Properties.ShouldNotContainKey(DegradedModeHandlers.ClientProperty);

            context = Validate(origin, new OpenIddictRequest { GrantType = OpenIddictConstants.GrantTypes.RefreshToken, ClientId = FirstPartyClients.Portal, RefreshToken = "from-a-form" }, refresh: code);

            await handler.HandleAsync(context);

            context.IsRejected.ShouldBeTrue($"a body-borne refresh for the portal from Origin '{origin}' was validated");
            context.ErrorDescription.ShouldBe(DegradedModeHandlers.ValidateTokenRequest.OriginNotAllowed);
        }

        // The portal's own origin: validated, client resolved, and the passthrough gets to mint.
        var own = Validate(IdentityHostFixture.PortalOrigin, new OpenIddictRequest { GrantType = OpenIddictConstants.GrantTypes.AuthorizationCode, ClientId = FirstPartyClients.Portal }, code: code);

        await handler.HandleAsync(own);

        own.IsRejected.ShouldBeFalse(own.ErrorDescription);
        own.Transaction.Properties[DegradedModeHandlers.ClientProperty].ShouldBeOfType<ApplicationRegistration>().ClientId.ShouldBe(FirstPartyClients.Portal);

        // The CLI, from a process with no Origin: not a browser client, and not this rule's. That
        // the code was minted for the portal is OpenIddict's ValidateAuthorizedParty to refuse,
        // later in the same pipeline — GrantsOverHttpTests.TheVerifierIsWhatBindsACodeToTheTabThatAskedForIt.
        var cli = Validate("", new OpenIddictRequest { GrantType = OpenIddictConstants.GrantTypes.AuthorizationCode, ClientId = FirstPartyClients.Cli }, code: code);

        await handler.HandleAsync(cli);

        cli.IsRejected.ShouldBeFalse(cli.ErrorDescription);
    }

    // ── The tenant hint ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownTenantHintAnswersUniformly() {
        var hint = fixture.Services.GetRequiredService<TenantHint>();

        // A slug nobody registered, a GUID nobody registered, and a string that is neither.
        (await hint.ResolveAsync("no-such-tenant", Ct)).ShouldBeNull();
        (await hint.ResolveAsync(Guid.NewGuid().ToString("D"), Ct)).ShouldBeNull();
        (await hint.ResolveAsync("Not A Slug!", Ct)).ShouldBeNull();

        // And on the sign-in API, the uniform failure with no cookie — the same body a wrong password
        // gets, so the tenant's existence is not readable off this endpoint.
        var refused = await fixture.Services.GetRequiredService<SignInApi>()
            .SignInWithPasswordAsync(new(IdentityHostFixture.Email, IdentityHostFixture.Password, "/", "no-such-tenant"), new(), Ct);

        refused.Principal.ShouldBeNull();
        refused.Response.Succeeded.ShouldBeFalse();
        refused.Response.Message.ShouldBe(UniformFailures.SignIn);
    }

    [Fact]
    public async Task ASignInThatNamedItsOrganisationResumesTheAuthorizeRequestWithThatTenant() {
        // The person arrived from an /authorize that named no tenant (or a remembered one that is
        // gone), typed the organisation, and signed in. The page navigates to the return URL as the
        // response carries it — and the resumed /authorize resolves its tenant hint before it reads
        // the cookie, so a return URL still naming nothing gets the fallback tenant, the cookie is
        // for another, and the person is back on the sign-in page. Forever. Sign-up already stamps
        // the tenant into the return URL; sign-in did not, and the first browser run of the second
        // visit found the loop.
        var api = fixture.Services.GetRequiredService<SignInApi>();
        var resumed = "/authorize?client_id=cyc-portal&state=s&code_challenge=c&code_challenge_method=S256";

        var signedIn = await api.SignInWithPasswordAsync(new(IdentityHostFixture.Email, IdentityHostFixture.Password, resumed, IdentityHostFixture.Slug), new(), Ct);

        signedIn.Response.Succeeded.ShouldBeTrue(signedIn.Response.Message);
        signedIn.Response.ReturnUrl.ShouldBe(resumed + "&tenant=" + IdentityHostFixture.Tenant.ToString("D"), "the resumed request has to name the tenant the cookie is for");

        // A stale remembered tenant in the return URL is replaced, not joined by a second value.
        var stale = "/authorize?tenant=" + Guid.NewGuid().ToString("D") + "&state=s";
        var replaced = await api.SignInWithPasswordAsync(new(IdentityHostFixture.Email, IdentityHostFixture.Password, stale, IdentityHostFixture.Slug), new(), Ct);

        replaced.Response.Succeeded.ShouldBeTrue(replaced.Response.Message);
        replaced.Response.ReturnUrl.ShouldBe("/authorize?tenant=" + IdentityHostFixture.Tenant.ToString("D") + "&state=s");

        // Any other destination survives unchanged — a plain `/` does not grow a query it never reads.
        var elsewhere = await api.SignInWithPasswordAsync(new(IdentityHostFixture.Email, IdentityHostFixture.Password, "/account", IdentityHostFixture.Slug), new(), Ct);

        elsewhere.Response.Succeeded.ShouldBeTrue(elsewhere.Response.Message);
        elsewhere.Response.ReturnUrl.ShouldBe("/account");
    }

    [Fact]
    public async Task ASlugHintResolvesThroughTheDirectory() {
        var hint = fixture.Services.GetRequiredService<TenantHint>();

        (await hint.ResolveAsync(IdentityHostFixture.Slug, Ct)).ShouldBe(IdentityHostFixture.Tenant);
        (await hint.ResolveAsync(IdentityHostFixture.Tenant.ToString("D"), Ct)).ShouldBe(IdentityHostFixture.Tenant);
        (await hint.ResolveAsync(IdentityHostFixture.Tenant.ToString("N"), Ct)).ShouldBe(IdentityHostFixture.Tenant);
        (await hint.ResolveAsync("  " + IdentityHostFixture.Slug + " ", Ct)).ShouldBe(IdentityHostFixture.Tenant, "a typed value is trimmed");

        // A retired tenant is unknown over HTTP, whatever the directory still holds for it.
        var retired = Guid.Parse("7a11e0aa-0000-4000-8000-00000000a0ff");

        (await fixture.Grains.GetGrain<Tenancy.Contracts.ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
            .RegisterAsync(new() { TenantId = retired, Slug = "grants-tests-retired", HomeRegion = "local", Status = Tenancy.Contracts.TenantStatus.PendingDeletion }))
            .IsSuccess.ShouldBeTrue();

        (await hint.ResolveAsync("grants-tests-retired", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task TheDevelopmentDefaultTenantAppliesOnlyInDevelopment() {
        // ⚠ Over a factory that refuses: the fallback is a decision, not a lookup, and a hint that
        // names nothing must not wake the directory to find that out.
        var grains = new RefusingGrainFactory();

        var development = new TenantHint(grains, Options.Create(new IdentityHostOptions()), TestEnvironment.Development);
        var production = new TenantHint(grains, Options.Create(new IdentityHostOptions()), TestEnvironment.Production);
        var configured = new TenantHint(grains, Options.Create(new IdentityHostOptions { TenantId = SignInApiHarness.Tenant }), TestEnvironment.Production);

        development.Default.ShouldBe(Guid.Empty, "Development falls back to the platform tenant so a fresh run can sign in");
        production.Default.ShouldBeNull("outside Development a request that names no tenant names none");
        configured.Default.ShouldBe(SignInApiHarness.Tenant, "an explicitly configured tenant wins in any environment");

        (await development.ResolveAsync(null, Ct)).ShouldBe(Guid.Empty);
        (await development.ResolveAsync("", Ct)).ShouldBe(Guid.Empty);
        (await production.ResolveAsync(null, Ct)).ShouldBeNull();
        (await configured.ResolveAsync(null, Ct)).ShouldBe(SignInApiHarness.Tenant);

        // The fallback named explicitly is the fallback, without a lookup — the platform tenant is
        // never in the directory and the portal may remember it.
        (await development.ResolveAsync(Guid.Empty.ToString("D"), Ct)).ShouldBe(Guid.Empty);
        (await configured.ResolveAsync(SignInApiHarness.Tenant.ToString("N"), Ct)).ShouldBe(SignInApiHarness.Tenant);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    TokenApi Api => fixture.Services.GetRequiredService<TokenApi>();

    ApplicationRegistration Portal => fixture.Services.GetRequiredService<FirstPartyClients>().Find(FirstPartyClients.Portal)!;

    ISessionGrain Session(Guid id) => fixture.For(IdentityHostFixture.Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(id));

    /// <summary>A code principal for a fresh password-and-code sign-in — what /authorize would mint.</summary>
    async Task<(ClaimsPrincipal Code, Guid InteractiveSessionId)> CodeAsync() {
        var opened = await fixture.Services.GetRequiredService<SignInService>()
            .OpenSessionAsync(IdentityHostFixture.Tenant, fixture.UserId, AuthenticationMethod.Password, new());

        var outcome = opened.GetValueOrThrow();

        var cookie = IdentitySessionPrincipal.Promote(
            IdentitySessionPrincipal.Build(IdentityHostFixture.Tenant, outcome),
            AuthenticationMethod.EmailOtp
        );

        var decision = await fixture.Services.GetRequiredService<AuthorizeApi>().DecideAsync(
            new OpenIddictRequest {
                ClientId = FirstPartyClients.Portal,
                RedirectUri = IdentityHostFixture.PortalRedirectUri,
                ResponseType = "code",
                Scope = string.Join(' ', AllScopes),
                CodeChallenge = "c",
                CodeChallengeMethod = "S256"
            },
            IdentityHostFixture.Tenant,
            Portal,
            cookie,
            "/authorize",
            Ct
        );

        return (decision.ShouldBeOfType<AuthorizeDecision.IssueCode>().Principal, outcome.SessionId);
    }

    /// <summary>
    ///     A token request at the validation stage — after OpenIddict decrypted the code or the
    ///     refresh token and put its principal on the context — from a page on <paramref name="origin" />.
    /// </summary>
    OpenIddictServerEvents.ValidateTokenRequestContext Validate(string origin, OpenIddictRequest request, ClaimsPrincipal? code = null, ClaimsPrincipal? refresh = null) {
        var http = new DefaultHttpContext();

        if (origin.Length > 0) {
            http.Request.Headers.Origin = origin;
        }

        var transaction = new OpenIddictServerTransaction {
            Request = request,
            Options = fixture.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value,
            Logger = NullLogger.Instance,
            EndpointType = OpenIddictServerEndpointType.Token
        };

        transaction.Properties[typeof(HttpRequest).FullName!] = new WeakReference<HttpRequest>(http.Request);

        return new(transaction) { AuthorizationCodePrincipal = code, RefreshTokenPrincipal = refresh };
    }

    OpenIddictServerEvents.ExtractTokenRequestContext Extract(HttpContext http, OpenIddictRequest request) {
        var transaction = new OpenIddictServerTransaction {
            Request = request,
            Options = fixture.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value,
            Logger = NullLogger.Instance,
            EndpointType = OpenIddictServerEndpointType.Token
        };

        // ⚠ How OpenIddict's ASP.NET Core host hands a handler the request: a weak reference under
        // the request type's full name, which is what GetHttpRequest() reads.
        transaction.Properties[typeof(HttpRequest).FullName!] = new WeakReference<HttpRequest>(http.Request);

        return new(transaction);
    }
}
