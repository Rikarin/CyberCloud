using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     What <c>/authorize</c> does with a cookie — <see cref="AuthorizeApi" /> — and what its
///     validator refuses, against the real host's services and real session grains.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Four cookies that look signed in and are not, each with its own destination.</b>
///         A password without its second factor, a session in another tenant, a session the grain
///         has revoked, and a client that asked for a fresh sign-in are all cookies
///         <c>context.User</c> would present as authenticated; each goes back to the sign-in page
///         rather than to a code, and <c>prompt=none</c> turns the same answer into
///         <c>login_required</c> for the client. A handler that checked
///         <c>Identity.IsAuthenticated</c> and stopped would mint a code for all four.
///     </para>
///     <para>
///         The redirect-URI refusal is asserted on the validator's context here (rejected, with
///         which error) and on the wire in <c>GrantsOverHttpTests</c> (no <c>Location</c>) — the
///         two halves of "refused without a redirect".
///     </para>
/// </remarks>
[Collection(IdentityHostSuite.Name)]
public sealed class AuthorizeHandlerTests(IdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Path = "/authorize?response_type=code&client_id=cyc-portal&state=s&code_challenge=c&code_challenge_method=S256";

    [Fact]
    public async Task AFullyAuthenticatedCookieYieldsACodeForAFirstPartyClient() {
        var (cookie, session) = await SignedInAsync();

        var decision = await Api.DecideAsync(Request(), IdentityHostFixture.Tenant, Portal, cookie, Path, cancellationToken: Ct);

        var code = decision.ShouldBeOfType<AuthorizeDecision.IssueCode>();

        code.Principal.GetClaim(AccessTokenClaims.Subject).ShouldBe(fixture.UserId.ToString("N"));
        code.Principal.GetClaim(AccessTokenClaims.TenantId).ShouldBe(IdentityHostFixture.Tenant.ToString("N"));
        code.Principal.GetClaim(AccessTokenClaims.SessionId).ShouldBe(session.ToString("N"), "the code names the interactive session");
        code.Principal.GetClaim(AccessTokenClaims.SubjectType).ShouldBe(SubjectTypes.User);
        code.Principal.GetPresenters().ShouldBe([FirstPartyClients.Portal]);
        code.Principal.GetAudiences().ShouldBe([AccessTokenPolicy.Audience]);
        code.Principal.GetScopes().ShouldBe(["openid", "profile", "offline_access", "cyc.api"], ignoreOrder: true);

        // The profile, for the id_token only.
        code.Principal.FindFirst(OpenIddictConstants.Claims.Email)!.Value.ShouldBe(IdentityHostFixture.Email);
        code.Principal.FindFirst(OpenIddictConstants.Claims.Email)!.GetDestinations().ShouldBe([OpenIddictConstants.Destinations.IdentityToken]);
        code.Principal.FindFirst(OpenIddictConstants.Claims.Name)!.Value.ShouldBe(IdentityHostFixture.DisplayName);
    }

    [Fact]
    public async Task TheCodePrincipalCarriesTheInteractiveAuthTime() {
        var (cookie, session) = await SignedInAsync();

        var described = (await fixture.For(IdentityHostFixture.Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(session)).GetAsync()).GetValueOrThrow();

        var code = (await Api.DecideAsync(Request(), IdentityHostFixture.Tenant, Portal, cookie, Path, cancellationToken: Ct))
            .ShouldBeOfType<AuthorizeDecision.IssueCode>();

        // ⚠ The grain's AuthenticatedAt, typed as an integer — OpenIddict refuses the sign-in
        // otherwise — and never "now": every access token on every chain from this code carries it
        // forward, which is what a step-up rule compares against.
        var authTime = code.Principal.FindFirst(AccessTokenClaims.AuthenticationTime)!;

        authTime.Value.ShouldBe(described.AuthenticatedAt.ToUnixTimeSeconds().ToString());
        authTime.ValueType.ShouldBe(ClaimValueTypes.Integer64);

        // And both factors, not only the one the grain recorded — AuthorizeApi.WithCookieMethods.
        code.Principal.FindAll(AccessTokenClaims.AuthenticationMethods).Select(x => x.Value).ShouldBe(["pwd", "otp"]);
    }

    [Fact]
    public async Task APendingSecondFactorCookieIsSentToSignIn() {
        var (_, session) = await SignedInAsync();

        var pending = IdentitySessionPrincipal.Build(
            IdentityHostFixture.Tenant,
            SignInOutcome.Success(fixture.UserId, session, AuthenticationMethod.Password, secondFactorRequired: true)
        );

        var decision = await Api.DecideAsync(Request(), IdentityHostFixture.Tenant, Portal, pending, Path, cancellationToken: Ct);

        var signIn = decision.ShouldBeOfType<AuthorizeDecision.SignIn>();

        signIn.Location.ShouldStartWith(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.SignInPagePath + "?returnUrl=");
        Uri.UnescapeDataString(signIn.Location.Split("returnUrl=")[1]).ShouldBe(Path);
    }

    [Fact]
    public async Task ACookieForAnotherTenantIsSentToSignIn() {
        var (cookie, _) = await SignedInAsync();

        // The same person, the same live session — asked to authorize a request that resolved to a
        // tenant the cookie was not issued for.
        var decision = await Api.DecideAsync(Request(), IdentityHostFixture.OtherTenant, Portal, cookie, Path, cancellationToken: Ct);

        decision.ShouldBeOfType<AuthorizeDecision.SignIn>();
    }

    [Fact]
    public async Task ARevokedInteractiveSessionIsSentToSignIn() {
        var (cookie, session) = await SignedInAsync();

        // A cookie outlives its session by up to eight hours; only the grain knows.
        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(session)).RevokeAsync(RevocationReason.AdminAction))
            .IsSuccess.ShouldBeTrue();

        var decision = await Api.DecideAsync(Request(), IdentityHostFixture.Tenant, Portal, cookie, Path, cancellationToken: Ct);

        decision.ShouldBeOfType<AuthorizeDecision.SignIn>();
    }

    [Fact]
    public async Task PromptNoneWithoutASessionIsLoginRequired() {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var decision = await Api.DecideAsync(Request(prompt: "none"), IdentityHostFixture.Tenant, Portal, anonymous, Path + "&prompt=none", cancellationToken: Ct);

        var refused = decision.ShouldBeOfType<AuthorizeDecision.Refuse>();

        refused.Error.ShouldBe(OpenIddictConstants.Errors.LoginRequired);
    }

    [Fact]
    public async Task PromptLoginSendsEvenASignedInPersonToSignIn() {
        var (cookie, _) = await SignedInAsync();

        var decision = await Api.DecideAsync(Request(prompt: "login"), IdentityHostFixture.Tenant, Portal, cookie, Path + "&prompt=login", cancellationToken: Ct);

        var signIn = decision.ShouldBeOfType<AuthorizeDecision.SignIn>();

        // ⚠ And the return URL no longer says prompt=login, or the person loops forever.
        Uri.UnescapeDataString(signIn.Location.Split("returnUrl=")[1]).ShouldBe(Path);
    }

    [Fact]
    public void TheSignInRedirectKeepsTheQueryAndDropsPromptLogin() {
        var location = Api.SignInLocation("/authorize?client_id=cyc-portal&prompt=login&state=s&code_challenge=abc");

        location.ShouldBe(
            IdentityHostFixture.SignInPageBaseUri
            + "/signin?returnUrl="
            + Uri.EscapeDataString("/authorize?client_id=cyc-portal&state=s&code_challenge=abc")
        );

        // A prompt with more than one value loses only `login`; one with only `login` goes entirely.
        Uri.UnescapeDataString(Api.SignInLocation("/authorize?prompt=login%20consent&state=s").Split("returnUrl=")[1])
            .ShouldBe("/authorize?prompt=consent&state=s");

        Uri.UnescapeDataString(Api.SignInLocation("/authorize?prompt=login").Split("returnUrl=")[1])
            .ShouldBe("/authorize");

        // An empty base means the host's own origin: a same-origin path, which is what the
        // production layout serves.
        new AuthorizeApi(fixture.Grains, Options.Create(new IdentityHostOptions()), NullLogger<AuthorizeApi>.Instance)
            .SignInLocation("/authorize?state=s")
            .ShouldBe("/signin?returnUrl=" + Uri.EscapeDataString("/authorize?state=s"));
    }

    [Fact]
    public async Task AHintNamingNoTenantSendsAFirstPartyClientToSignInWithTheHintRemoved() {
        // The portal remembers the last tenant in a cookie and sends it on every /authorize. A
        // developer's second `dotnet run` starts with an empty durable tier, so the remembered
        // tenant is gone — and the first thing the portal showed was OpenIddict's error page with
        // nowhere to go. A retired tenant and a mistyped slug are the same shape.
        var request = Request();
        request[TenantHint.ParameterName] = Guid.NewGuid().ToString("D");

        var context = await ValidateAsync(request);

        context.IsRejected.ShouldBeFalse("a first-party client with an unknown tenant hint has a page to go to, and the validator must let the passthrough send it there");
        context.Transaction.Properties.ShouldContainKey(DegradedModeHandlers.UnknownTenantProperty);
        context.Transaction.Properties[DegradedModeHandlers.ClientProperty].ShouldBeOfType<ApplicationRegistration>()
            .ClientId.ShouldBe(FirstPartyClients.Portal, "the redirect_uri is still validated, against the static registration");

        var location = Api.SignInLocationWithoutTenant("/authorize?client_id=cyc-portal&tenant=gone-tenant&state=s&prompt=login", "gone-tenant", FirstPartyClients.Portal);

        Uri.UnescapeDataString(location.Split("returnUrl=")[1])
            .ShouldBe("/authorize?client_id=cyc-portal&state=s", "the hint comes off so the sign-in page asks for the organisation, and prompt=login comes off as it always does");
    }

    [Fact]
    public async Task AHintNamingNoTenantIsStillAnErrorForATenantClientAndForNoHintAtAll() {
        // A tenant's own client is registered IN a tenant: with no tenant there is no registration
        // to validate the redirect_uri against, so the error page is the only honest answer.
        var request = Request(clientId: "some-tenant-app");
        request[TenantHint.ParameterName] = Guid.NewGuid().ToString("D");

        var context = await ValidateAsync(request);

        context.IsRejected.ShouldBeTrue();
        context.Error.ShouldBe(OpenIddictConstants.Errors.InvalidRequest);
        context.Transaction.Properties.ShouldNotContainKey(DegradedModeHandlers.UnknownTenantProperty);
    }

    [Fact]
    public async Task ATenantRegisteredClientCannotConsentFreeItsWayToACode() {
        var (cookie, _) = await SignedInAsync();
        var clientId = "acme-dashboard-" + Guid.NewGuid().ToString("N")[..8];

        var thirdParty = new ApplicationRegistration {
            ApplicationId = Guid.NewGuid(),
            TenantId = IdentityHostFixture.Tenant,
            ClientId = clientId,
            RedirectUris = ["https://acme.example/cb"],
            AllowedGrants = [GrantType.AuthorizationCode],
            AllowedScopes = [.. FirstPartyClients.AllScopes],
            IsPublicClient = true
        };

        var request = Request(clientId: clientId);
        var path = Path.Replace("client_id=cyc-portal", "client_id=" + clientId, StringComparison.Ordinal);

        // ── 1. Nothing on record: the consent page, with this request as the return URL. ───────
        var asked = await Api.DecideAsync(request, IdentityHostFixture.Tenant, thirdParty, cookie, path, cancellationToken: Ct);

        var consent = asked.ShouldBeOfType<AuthorizeDecision.Consent>();

        consent.Location.ShouldStartWith(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.ConsentPagePath + "?returnUrl=");
        Uri.UnescapeDataString(consent.Location.Split("returnUrl=")[1]).ShouldBe(path);

        // ⚠ A consent=allow that arrived any way but the page's POST is no answer — the endpoint
        // passes null for a GET, and null is what this call passes. prompt=none with nothing on
        // record is consent_required, because that is what the client asked to be told.
        (await Api.DecideAsync(Request(clientId: clientId, prompt: "none"), IdentityHostFixture.Tenant, thirdParty, cookie, path, cancellationToken: Ct))
            .ShouldBeOfType<AuthorizeDecision.Refuse>()
            .Error.ShouldBe(OpenIddictConstants.Errors.ConsentRequired);

        // ── 2. Deny: access_denied to the client, nothing recorded. ────────────────────────────
        var denied = await Api.DecideAsync(request, IdentityHostFixture.Tenant, thirdParty, cookie, path, ConsentDecision.Deny, Ct);

        denied.ShouldBeOfType<AuthorizeDecision.Refuse>().Error.ShouldBe(OpenIddictConstants.Errors.AccessDenied);
        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<IConsentGrain>(GrainKeys.ConsentGrant(IdentityHostFixture.Tenant, fixture.UserId, clientId)).GetAsync())
            .IsFailure.ShouldBeTrue("a denial was recorded as a grant");

        // ── 3. Allow: the grant is recorded first and the code minted second. ──────────────────
        var allowed = await Api.DecideAsync(request, IdentityHostFixture.Tenant, thirdParty, cookie, path, ConsentDecision.Allow, Ct);

        allowed.ShouldBeOfType<AuthorizeDecision.IssueCode>().Principal.GetPresenters().ShouldBe([clientId]);

        var recorded = await fixture.For(IdentityHostFixture.Tenant).GetGrain<IConsentGrain>(GrainKeys.ConsentGrant(IdentityHostFixture.Tenant, fixture.UserId, clientId)).GetAsync();

        recorded.GetValueOrThrow().Scopes.ShouldBe(["openid", "profile", "offline_access", "cyc.api"]);

        // ── 4. On record: the next request is consent-free; a wider one, or prompt=consent, asks. ─
        (await Api.DecideAsync(request, IdentityHostFixture.Tenant, thirdParty, cookie, path, cancellationToken: Ct))
            .ShouldBeOfType<AuthorizeDecision.IssueCode>();

        (await Api.DecideAsync(Request(clientId: clientId, prompt: "consent"), IdentityHostFixture.Tenant, thirdParty, cookie, path, cancellationToken: Ct))
            .ShouldBeOfType<AuthorizeDecision.Consent>();

        (await fixture.For(IdentityHostFixture.Tenant).GetGrain<IConsentGrain>(GrainKeys.ConsentGrant(IdentityHostFixture.Tenant, fixture.UserId, clientId)).RevokeAsync())
            .IsSuccess.ShouldBeTrue();

        (await Api.DecideAsync(request, IdentityHostFixture.Tenant, thirdParty, cookie, path, cancellationToken: Ct))
            .ShouldBeOfType<AuthorizeDecision.Consent>("a revoked consent still minted a code");

        // And the portal never sees any of this: consent-free by registration.
        (await Api.DecideAsync(Request(), IdentityHostFixture.Tenant, Portal, cookie, Path, cancellationToken: Ct))
            .ShouldBeOfType<AuthorizeDecision.IssueCode>();
    }

    // ── The validator ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnregisteredRedirectUriIsRefusedWithoutARedirect() {
        var context = await ValidateAsync(new OpenIddictRequest {
            ClientId = FirstPartyClients.Portal,
            RedirectUri = "https://evil.example/callback",
            ResponseType = "code",
            CodeChallenge = "c",
            CodeChallengeMethod = "S256",
            Scope = "openid",
            [TenantHint.ParameterName] = IdentityHostFixture.Slug
        });

        context.IsRejected.ShouldBeTrue();
        context.Error.ShouldBe(OpenIddictConstants.Errors.InvalidRequest);
        context.ErrorDescription!.ShouldContain("redirect_uri");

        // ⚠ Rejected in the VALIDATION stage, which is what makes it an error page and not a
        // redirect: OpenIddict redirects an error to the client's URI only once validation passed.
        // GrantsOverHttpTests asserts the absence of the Location header on the wire.
        context.Transaction.Properties.ShouldNotContainKey(DegradedModeHandlers.ClientProperty);
    }

    [Fact]
    public async Task ATenantRegisteredClientCannotShadowAFirstPartyId() {
        // A tenant registers an application whose client_id is the portal's, pointing at its own
        // redirect URI. The resolver must answer with the platform's registration — every time.
        var applicationId = Guid.NewGuid();

        var registered = await fixture.For(IdentityHostFixture.Tenant)
            .GetGrain<IApplicationGrain>(GrainKeys.Application(applicationId))
            .CreateAsync(
                new() {
                    ClientId = FirstPartyClients.Portal,
                    DisplayName = "not the portal",
                    RedirectUris = ["https://evil.example/callback"],
                    AllowedGrants = [GrantType.AuthorizationCode],
                    AllowedScopes = [.. FirstPartyClients.AllScopes],
                    IsPublicClient = true
                }
            );

        registered.IsSuccess.ShouldBeTrue(registered.Error?.Message);

        var resolved = await fixture.Services.GetRequiredService<IClientResolver>().ResolveAsync(IdentityHostFixture.Tenant, FirstPartyClients.Portal, Ct);

        resolved.ShouldNotBeNull();
        resolved.TenantId.ShouldBe(Guid.Empty, "the first-party registration, not the tenant's");
        resolved.RedirectUris.ShouldBe([FirstPartyClients.DevelopmentPortalRedirectUri]);

        // And the validator agrees: the tenant's redirect URI is not registered for cyc-portal.
        var context = await ValidateAsync(new OpenIddictRequest {
            ClientId = FirstPartyClients.Portal,
            RedirectUri = "https://evil.example/callback",
            ResponseType = "code",
            CodeChallenge = "c",
            CodeChallengeMethod = "S256",
            [TenantHint.ParameterName] = IdentityHostFixture.Slug
        });

        context.IsRejected.ShouldBeTrue();
        context.Error.ShouldBe(OpenIddictConstants.Errors.InvalidRequest);
    }

    [Fact]
    public async Task ATenantsOwnClientResolvesThroughItsIndex() {
        var applicationId = Guid.NewGuid();

        var registered = await fixture.For(IdentityHostFixture.Tenant)
            .GetGrain<IApplicationGrain>(GrainKeys.Application(applicationId))
            .CreateAsync(
                new() {
                    ClientId = "contoso-app",
                    DisplayName = "Contoso",
                    RedirectUris = ["https://app.contoso.example/cb"],
                    AllowedGrants = [GrantType.AuthorizationCode],
                    AllowedScopes = ["openid"],
                    IsPublicClient = true
                }
            );

        registered.IsSuccess.ShouldBeTrue(registered.Error?.Message);

        var context = await ValidateAsync(new OpenIddictRequest {
            ClientId = "contoso-app",
            RedirectUri = "https://app.contoso.example/cb",
            ResponseType = "code",
            CodeChallenge = "c",
            CodeChallengeMethod = "S256",
            Scope = "openid",
            [TenantHint.ParameterName] = IdentityHostFixture.Slug
        });

        context.IsRejected.ShouldBeFalse(context.ErrorDescription);
        context.Transaction.Properties[DegradedModeHandlers.TenantProperty].ShouldBe(IdentityHostFixture.Tenant);
        ((ApplicationRegistration)context.Transaction.Properties[DegradedModeHandlers.ClientProperty]!).ApplicationId.ShouldBe(applicationId);

        // A scope outside the registration, and a missing PKCE challenge, are each refused.
        (await ValidateAsync(new OpenIddictRequest {
            ClientId = "contoso-app",
            RedirectUri = "https://app.contoso.example/cb",
            ResponseType = "code",
            CodeChallenge = "c",
            CodeChallengeMethod = "S256",
            Scope = "openid cyc.api",
            [TenantHint.ParameterName] = IdentityHostFixture.Slug
        })).Error.ShouldBe(OpenIddictConstants.Errors.InvalidScope);

        (await ValidateAsync(new OpenIddictRequest {
            ClientId = "contoso-app",
            RedirectUri = "https://app.contoso.example/cb",
            ResponseType = "code",
            Scope = "openid",
            [TenantHint.ParameterName] = IdentityHostFixture.Slug
        })).ErrorDescription!.ShouldContain("code_challenge");

        // The same id in the other tenant is nobody.
        (await ValidateAsync(new OpenIddictRequest {
            ClientId = "contoso-app",
            RedirectUri = "https://app.contoso.example/cb",
            ResponseType = "code",
            CodeChallenge = "c",
            CodeChallengeMethod = "S256",
            [TenantHint.ParameterName] = IdentityHostFixture.OtherSlug
        })).Error.ShouldBe(OpenIddictConstants.Errors.InvalidClient);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    AuthorizeApi Api => fixture.Services.GetRequiredService<AuthorizeApi>();

    ApplicationRegistration Portal => fixture.Services.GetRequiredService<FirstPartyClients>().Find(FirstPartyClients.Portal)!;

    static OpenIddictRequest Request(string clientId = FirstPartyClients.Portal, string? prompt = null) =>
        new() {
            ClientId = clientId,
            RedirectUri = IdentityHostFixture.PortalRedirectUri,
            ResponseType = "code",
            Scope = "openid profile offline_access cyc.api",
            State = "s",
            CodeChallenge = "c",
            CodeChallengeMethod = "S256",
            Prompt = prompt
        };

    async Task<OpenIddictServerEvents.ValidateAuthorizationRequestContext> ValidateAsync(OpenIddictRequest request) {
        var context = new OpenIddictServerEvents.ValidateAuthorizationRequestContext(
            new OpenIddictServerTransaction {
                Request = request,
                Options = fixture.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value,
                Logger = NullLogger.Instance,
                EndpointType = OpenIddictServerEndpointType.Authorization
            }
        );

        await fixture.Services.GetRequiredService<DegradedModeHandlers.ValidateAuthorizationRequest>().HandleAsync(context);

        return context;
    }

    /// <summary>A password sign-in followed by its second factor, as the cookie carries it.</summary>
    async Task<(ClaimsPrincipal Cookie, Guid SessionId)> SignedInAsync() {
        var opened = await fixture.Services.GetRequiredService<SignInService>()
            .OpenSessionAsync(IdentityHostFixture.Tenant, fixture.UserId, AuthenticationMethod.Password, new());

        var outcome = opened.GetValueOrThrow();

        outcome.SecondFactorRequired.ShouldBeTrue();

        var promoted = IdentitySessionPrincipal.Promote(
            IdentitySessionPrincipal.Build(IdentityHostFixture.Tenant, outcome),
            AuthenticationMethod.EmailOtp
        );

        return (promoted, outcome.SessionId);
    }
}
