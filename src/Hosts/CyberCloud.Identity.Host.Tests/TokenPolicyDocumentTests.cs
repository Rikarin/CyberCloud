using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.RateLimiting;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The endpoint surface <see cref="IdentityEndpoints.MapIdentityEndpoints" /> maps, and the two
///     documents it serves that nothing else in the tree publishes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Still no <c>TestServer</c> and no <c>WebApplicationFactory</c></b> — see this
///         project's <c>.csproj</c> for why, and note that neither is needed. A route's
///         <see cref="Endpoint.RequestDelegate" /> is an ordinary delegate over an
///         <see cref="HttpContext" />, so the handlers can be invoked directly against a
///         <see cref="DefaultHttpContext" /> once the endpoints are built. What that does not cover
///         is middleware, which is the honest limit of this file.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <c>/.well-known/cybercloud-token-policy</c> is a contract with something outside this
///             repository.
///         </b> docs/plan/11 § Hosts publishes it so the gateway does not hard-code token
///         validation and so an operator can see a change to <see cref="AccessTokenPolicy" /> without
///         reading source. A document that drifted from the constants would be worse than no document
///         at all: it would be a wrong answer that looks authoritative.
///     </para>
/// </remarks>
public sealed class TokenPolicyDocumentTests {
    static IReadOnlyList<Endpoint> Endpoints() {
        var builder = WebApplication.CreateSlimBuilder();

        // ⚠ The host's real registration, because minimal-API endpoint building binds each handler's
        // parameters eagerly and fails with "Failure to infer one or more parameters" for anything
        // the container cannot supply. That makes this an assertion in its own right: the endpoints
        // below cannot be enumerated at all unless AddIdentityHostApi provides everything their
        // signatures ask for. IGrainFactory is the host builder's, exactly as in
        // IdentityHostServicesTests.
        builder.Services.AddSingleton<IGrainFactory, RefusingGrainFactory>();
        builder.Services.AddIdentityHostApi(builder.Configuration);

        var app = builder.Build();
        app.MapIdentityEndpoints();

        // ⚠ The builder's own data sources, not the container's `EndpointDataSource`. The latter is
        // populated by `UseEndpoints`, which only runs once the pipeline is built, so resolving it
        // here returns an EMPTY collection — and an empty collection makes every assertion below
        // vacuously true. That is why `EveryScriptCalledEndpointIsUnderTheApiPrefix` asserts the list
        // is non-empty before it iterates: a mapping that stopped happening must fail this file
        // rather than pass it.
        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(static x => x.Endpoints)];
    }

    static RouteEndpoint Route(string pattern) =>
        Endpoints()
            .OfType<RouteEndpoint>()
            .Single(x => string.Equals(x.RoutePattern.RawText, pattern, StringComparison.Ordinal));

    static async Task<JsonElement> GetAsync(string pattern) {
        var endpoint = Route(pattern);

        var context = new DefaultHttpContext {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        return (await JsonDocument.ParseAsync(context.Response.Body)).RootElement;
    }

    [Fact]
    public async Task TheTokenPolicyDocumentSaysWhatAccessTokenPolicySays() {
        var document = await GetAsync("/.well-known/cybercloud-token-policy");

        document.GetProperty("accessTokenLifetimeSeconds")
            .GetInt32()
            .ShouldBe((int)AccessTokenPolicy.AccessTokenLifetime.TotalSeconds);
        document.GetProperty("signingAlgorithm").GetString().ShouldBe(AccessTokenPolicy.SigningAlgorithm);
        document.GetProperty("jwksPath").GetString().ShouldBe(AccessTokenPolicy.JsonWebKeySetPath);
        document.GetProperty("discoveryPath").GetString().ShouldBe(AccessTokenPolicy.DiscoveryPath);
        document.GetProperty("supportsIntrospection")
            .GetBoolean()
            .ShouldBe(AccessTokenPolicy.SupportsIntrospection);
        document.GetProperty("accessTokensAreRevocable")
            .GetBoolean()
            .ShouldBe(AccessTokenPolicy.AccessTokensAreRevocable);
    }

    [Fact]
    public async Task TheForbiddenClaimNamesArePublishedWhileThereIsNoIntrospection() {
        var document = await GetAsync("/.well-known/cybercloud-token-policy");

        var published = document.GetProperty("forbiddenClaims")
            .EnumerateArray()
            .Select(static x => x.GetString())
            .ToList();

        // ⚠ The list is what the gateway needs in order to treat a token carrying one of these names
        // as suspect rather than as a token with extra claims. Publishing it is only useful if it is
        // the same list the platform enforces against, which is why this compares to the constant
        // rather than to a literal.
        if (AccessTokenPolicy.SupportsIntrospection) {
            published.ShouldBeEmpty(
                "with introspection available the gateway asks rather than infers, and the document "
                + "should stop advertising a heuristic"
            );
        } else {
            published.ShouldBe([.. AccessTokenClaims.ForbiddenClaims], true);
            published.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public async Task TheLivenessProbeSaysLiveAndNothingElse() {
        var document = await GetAsync("/health/live");

        document.GetProperty("status").GetString().ShouldBe("live");

        // ⚠ One property. docs/plan/11 § Hosts gives this host the OIDC surface and nothing else; a
        // probe that grew a tenant count or a dependency list would be a control-plane read
        // authenticated by nothing, on the origin that holds the session cookie.
        document.EnumerateObject().Count().ShouldBe(1);
    }

    [Fact]
    public void EveryScriptCalledEndpointIsUnderTheApiPrefix() {
        // ⚠ THE INVARIANT OnRedirectToLogin DEPENDS ON. A script-called endpoint mapped outside
        // `/api` receives a 302 to a login page instead of a 401, which every caller then fails to
        // parse — see UnauthenticatedApiCallsGet401Tests, which asserts the other half of the pair.
        // The exceptions are named here so adding another is a decision. The four OIDC passthroughs
        // are the last four: OpenIddict hands each a request it has already validated, /token and
        // /userinfo never consult the cookie (a bearer token is their credential, and OpenIddict's
        // own challenge answers 401 for a bad one), and /authorize and /logout are navigations by
        // definition — a person arrives at them by redirect, and a 302 to the sign-in page is
        // exactly what an unauthenticated /authorize answers.
        var navigable = new[] {
            "/health/live", "/.well-known/cybercloud-token-policy", IdentityHostOpenIddict.TokenPath,
            IdentityHostOpenIddict.UserInfoPath, IdentityHostOpenIddict.AuthorizationPath,
            IdentityHostOpenIddict.EndSessionPath
        };

        var mapped = Endpoints()
            .OfType<RouteEndpoint>()
            .Select(static x => "/" + x.RoutePattern.RawText!.TrimStart('/'))
            .ToList();

        mapped.ShouldNotBeEmpty("MapIdentityEndpoints mapped nothing at all");

        foreach (var route in mapped.Except(navigable, StringComparer.Ordinal)) {
            route.StartsWith("/api/", StringComparison.Ordinal)
                .ShouldBeTrue(
                    $"'{route}' is called by script, so it must sit under the prefix "
                    + "IdentityHostAuthentication.OnRedirectToLogin answers 401 for"
                );
        }
    }

    [Fact]
    public void TheSignInSurfaceIsTheOneThePagesCall() {
        var mapped = Endpoints()
            .OfType<RouteEndpoint>()
            .Select(static x => "/" + x.RoutePattern.RawText!.TrimStart('/'))
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ Named rather than counted. portal/apps/identity/src/app/identity-api.ts calls exactly
        // these, and a rename on one side of that pair produces a 404 at runtime rather than a build
        // error — the same hazard the [JsonPropertyName] attributes on the request records exist for.
        foreach (var route in new[] {
                     "/api/signin/begin", "/api/signin/password", "/api/signin/passkey/begin",
                     "/api/signin/passkey/complete", "/api/signup/begin", "/api/signup/verify",
                     "/api/signup/passkey/begin", "/api/signup/complete", "/api/consent"
                 }) {
            mapped.ShouldContain(route, $"the identity page calls {route}");
        }

        // ⚠ And the OIDC endpoints are NOT here, with one exception. They are OpenIddict's,
        // configured by IdentityHostOpenIddict; mapping one of them by hand would give this host two
        // handlers for the same path and the winner would depend on registration order. The
        // exception is the passthrough: /token IS mapped, because OpenIddict hands a validated
        // token request to whatever is mapped at its path, and until something was, the path
        // answered 404 — https://github.com/Rikarin/CyberCloud/issues/68.
        foreach (var openIddicts in new[] { "/connect/authorize", "/connect/token", "/connect/userinfo" }) {
            mapped.ShouldNotContain(openIddicts, $"{openIddicts} is OpenIddict's to map, not this file's");
        }

        mapped.ShouldContain(IdentityHostOpenIddict.TokenPath, "the token passthrough must have a handler behind it");
        mapped.ShouldContain(
            IdentityHostOpenIddict.AuthorizationPath,
            "the authorization passthrough must have a handler behind it"
        );
        mapped.ShouldContain(
            IdentityHostOpenIddict.UserInfoPath,
            "the userinfo passthrough must have a handler behind it"
        );
        mapped.ShouldContain(
            IdentityHostOpenIddict.EndSessionPath,
            "the end-session passthrough must have a handler behind it"
        );

        Route(IdentityHostOpenIddict.TokenPath)
            .Metadata.GetMetadata<HttpMethodMetadata>()!
            .HttpMethods.ShouldBe([HttpMethods.Post]);

        // A navigation on GET — a person arrives by redirect — and the consent page's answer on
        // POST, which is the form-post OF the request (every pair plus `consent`), not the form-post
        // response mode this server does not offer. IdentityEndpoints.MapAuthorize says what makes
        // the POST trustworthy and the GET's `consent=` parameter not.
        Route(IdentityHostOpenIddict.AuthorizationPath)
            .Metadata.GetMetadata<HttpMethodMetadata>()!
            .HttpMethods.ShouldBe([HttpMethods.Get, HttpMethods.Post]);

        // OIDC Core § 5.3.1: GET or POST, and OpenIddict extracts both.
        Route(IdentityHostOpenIddict.UserInfoPath)
            .Metadata.GetMetadata<HttpMethodMetadata>()!
            .HttpMethods.ShouldBe([HttpMethods.Get, HttpMethods.Post]);

        Route(IdentityHostOpenIddict.EndSessionPath)
            .Metadata.GetMetadata<HttpMethodMetadata>()!
            .HttpMethods.ShouldBe([HttpMethods.Get]);

        // ⚠ The three the portal calls cross-origin carry the first-party CORS policy, and nothing
        // else does — an /api endpoint with CORS headers would let a page on the portal's origin
        // drive the sign-in surface with the cookie attached. /userinfo is a bearer endpoint, so
        // the cookie is nothing to it; the policy lets the portal read the person's profile with
        // the token it already holds.
        foreach (var route in mapped) {
            var expected = route == IdentityHostOpenIddict.TokenPath
                || route == IdentityHostOpenIddict.EndSessionPath
                || route == IdentityHostOpenIddict.UserInfoPath;

            (Route(route).Metadata.GetMetadata<Microsoft.AspNetCore.Cors.Infrastructure.ICorsMetadata>() is not null)
                .ShouldBe(expected, $"{route} carries CORS metadata it should{(expected ? "" : " not")}");
        }
    }

    [Fact]
    public void TheRateLimitedRoutesAreExactlyTheOnesThatIssueOrTakeACode() {
        // ⚠ Named on both sides, so a route that starts issuing or taking a code without a bucket is
        // a failing test, and so is a bucket that lands on the password endpoint — which has the
        // lockout counter and the dummy hash and does not need one (IdentityRateLimits' remarks).
        var limited = Endpoints()
            .OfType<RouteEndpoint>()
            .Select(static x => (Route: x.RoutePattern.RawText!,
                    Bucket: x.Metadata.OfType<IdentityRateLimitBucket>().ToList())
            )
            .Where(static x => x.Bucket.Count > 0)
            .ToDictionary(static x => x.Route, static x => x.Bucket.Single().Name, StringComparer.Ordinal);

        limited.ShouldBe(
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["/api/signup/begin"] = IdentityRateLimits.SignUpBegin.Name,
                ["/api/signup/verify"] = IdentityRateLimits.CodeVerify.Name,
                ["/api/signin/otp"] = IdentityRateLimits.CodeVerify.Name,
                ["/api/signin/totp"] = IdentityRateLimits.CodeVerify.Name,
                ["/api/signin/recovery-code"] = IdentityRateLimits.CodeVerify.Name
            },
            true
        );

        IdentityRateLimits.All.Select(static x => x.Name).ShouldBe(["signup-begin", "code-verify"]);
    }

    [Fact]
    public void TheHealthProbeAndThePolicyDocumentAreGetOnly() {
        // A POST to either would be a route that exists for no caller, and the token-policy document
        // is a document rather than an action.
        foreach (var pattern in new[] { "/health/live", "/.well-known/cybercloud-token-policy" }) {
            var methods = Route(pattern).Metadata.GetMetadata<HttpMethodMetadata>();

            methods.ShouldNotBeNull(pattern);
            methods.HttpMethods.ShouldBe([HttpMethods.Get], pattern);
        }
    }

    [Fact]
    public void EverySignInEndpointIsPostOnly() {
        foreach (var endpoint in Endpoints().OfType<RouteEndpoint>()
                     .Where(static x => x.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))) {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();

            methods.ShouldNotBeNull(endpoint.RoutePattern.RawText);

            // ⚠ The one GET, named so a second one is a decision: /api/consent reads what the consent
            // page renders, and the only thing in its URL is the /authorize request the person was
            // already sent with — a URL the access log and the Referer have seen. It takes no
            // address and no credential, and it answers behind the cookie.
            if (string.Equals(endpoint.RoutePattern.RawText, "/api/consent", StringComparison.Ordinal)) {
                methods.HttpMethods.ShouldBe([HttpMethods.Get], endpoint.RoutePattern.RawText);

                continue;
            }

            // ⚠ POST, so the address and the credential are never in a URL. A GET would put them in
            // the access log, the browser history and the Referer header of whatever loads next.
            methods.HttpMethods.ShouldBe([HttpMethods.Post], endpoint.RoutePattern.RawText);
        }
    }
}
