using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The one CORS policy this host has: the first-party browser origins, with credentials, and
///     nobody else. docs/plan/10 § Authentication inputs, docs/plan/20 § SSR.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Derived from the redirect URIs, not listed beside them.</b> The portal's origin is
///         written once — as the place a code may be sent — and the origin that may present the
///         refresh cookie is computed from it, so a deployment that moves its portal changes one
///         value. A second list would be the one nobody updated.
///     </para>
///     <para>
///         The gateway's origin is the one worth naming: it is same-site with this host on the
///         development run (both <c>localhost</c>), so a page served from it could <c>POST</c> with
///         the cookie attached — and it gets no CORS header, so a script there cannot read the
///         answer, and no <c>Origin</c> match, so the extractor refuses it anyway.
///         <c>GrantsOverHttpTests</c> sends the request; this file reads the policy.
///     </para>
/// </remarks>
public sealed class CorsPolicyTests {
    [Fact]
    public async Task OnlyFirstPartyBrowserOriginsAreEchoedWithCredentials() {
        var services = Build(TestEnvironment.Development);
        var policy = await Policy(services);

        policy.Origins.ShouldBe(["http://localhost:4200"]);
        policy.SupportsCredentials.ShouldBeTrue("the refresh cookie rides on a credentialed fetch");
        policy.AllowAnyOrigin.ShouldBeFalse(
            "credentials and a wildcard origin are refused by every browser, and by this host"
        );
        policy.Methods.ShouldBe(["POST", "GET"], true);
        policy.Headers.ShouldBe(["Content-Type"]);

        // Evaluated as the middleware would: the portal's origin is echoed, with credentials.
        var result = Evaluate(services, policy, "http://localhost:4200");

        result.IsOriginAllowed.ShouldBeTrue();
        result.AllowedOrigin.ShouldBe("http://localhost:4200");
        result.SupportsCredentials.ShouldBeTrue();
    }

    [Fact]
    public async Task TheGatewayOriginIsNotAllowed() {
        var services = Build(TestEnvironment.Development);
        var policy = await Policy(services);

        foreach (var origin in new[] {
                     "http://localhost:5100", "http://localhost:4201", "https://portal.cybercloud.io", "null"
                 }) {
            var result = Evaluate(services, policy, origin);

            // ⚠ IsOriginAllowed, not AllowedOrigin: CorsService echoes the origin into the result either
            // way and the middleware writes the header only when this flag is set.
            result.IsOriginAllowed.ShouldBeFalse($"{origin} was allowed");
        }
    }

    [Fact]
    public async Task TheOriginsFollowTheConfiguredRedirectUris() {
        // A production deployment names its portal once, as a redirect URI, and the CORS origin
        // follows — without a localhost fallback, because the environment is not Development.
        var services = Build(
            TestEnvironment.Production,
            static options => options.Clients.Portal.RedirectUris.Add("https://portal.cybercloud.io/auth/callback")
        );

        var policy = await Policy(services);

        policy.Origins.ShouldBe(["https://portal.cybercloud.io"]);

        // And a production host with no redirect URI configured allows no origin at all.
        (await Policy(Build(TestEnvironment.Production))).Origins.ShouldBeEmpty();
    }

    static ServiceProvider Build(IHostEnvironment environment, Action<IdentityHostOptions>? configure = null) {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton(environment)
            .AddSingleton<IGrainFactory, RefusingGrainFactory>();

        services.Configure<IdentityHostOptions>(x => configure?.Invoke(x));
        services.AddSingleton<FirstPartyClients>();
        services.AddIdentityHostOpenIddict();

        return services.BuildServiceProvider();
    }

    static async Task<CorsPolicy> Policy(ServiceProvider services) {
        var policy = await services.GetRequiredService<ICorsPolicyProvider>()
            .GetPolicyAsync(new DefaultHttpContext(), FirstPartyClients.CorsPolicy);

        policy.ShouldNotBeNull("the FirstPartyBrowsers policy is not registered");

        return policy;
    }

    static CorsResult Evaluate(ServiceProvider services, CorsPolicy policy, string origin) {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers.Origin = origin;

        return new CorsService(
            Options.Create(services.GetRequiredService<IOptions<CorsOptions>>().Value),
            NullLoggerFactory.Instance
        )
            .EvaluatePolicy(context, policy);
    }
}
