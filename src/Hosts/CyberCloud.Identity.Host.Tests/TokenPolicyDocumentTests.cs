using CyberCloud.Identity.Contracts;
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
///         ⚠ <b><c>/.well-known/cybercloud-token-policy</c> is a contract with something outside this
///         repository.</b> docs/plan/11 § Hosts publishes it so the gateway does not hard-code token
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
        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints)];
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
            .Select(x => x.GetString())
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
            published.ShouldBe([.. AccessTokenClaims.ForbiddenClaims], ignoreOrder: true);
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
        // The two navigable exceptions are named here so adding a third is a decision.
        var navigable = new[] { "/health/live", "/.well-known/cybercloud-token-policy" };

        var mapped = Endpoints()
            .OfType<RouteEndpoint>()
            .Select(x => "/" + x.RoutePattern.RawText!.TrimStart('/'))
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
            .Select(x => "/" + x.RoutePattern.RawText!.TrimStart('/'))
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ Named rather than counted. portal/apps/identity/src/app/identity-api.ts calls exactly
        // these, and a rename on one side of that pair produces a 404 at runtime rather than a build
        // error — the same hazard the [JsonPropertyName] attributes on the request records exist for.
        foreach (var route in new[] {
            "/api/signin/begin",
            "/api/signin/password",
            "/api/signup",
            "/api/signin/passkey/begin",
            "/api/signin/passkey/complete"
        }) {
            mapped.ShouldContain(route, $"the identity page calls {route}");
        }

        // ⚠ And the OIDC endpoints are NOT here. They are OpenIddict's, configured by
        // IdentityHostOpenIddict; mapping one of them by hand would give this host two handlers for
        // the same path and the winner would depend on registration order.
        foreach (var openIddicts in new[] { "/connect/authorize", "/connect/token", "/connect/userinfo" }) {
            mapped.ShouldNotContain(openIddicts, $"{openIddicts} is OpenIddict's to map, not this file's");
        }
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
                     .Where(x => x.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))) {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();

            // ⚠ POST, so the address and the credential are never in a URL. A GET would put them in
            // the access log, the browser history and the Referer header of whatever loads next.
            methods.ShouldNotBeNull(endpoint.RoutePattern.RawText);
            methods.HttpMethods.ShouldBe([HttpMethods.Post], endpoint.RoutePattern.RawText);
        }
    }
}
