using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     docs/plan/11 § Hosts: <i>"a session cookie must never be a credential the resource API
///     accepts. If it is, every CSRF becomes a control-plane write. Separate hosts on separate
///     origins makes that structural instead of a middleware configuration somebody will change."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the mechanical half of a boundary that is otherwise a convention.</b> The
///         identity host issues cookies and the gateway accepts bearer tokens; the failure mode is
///         not that somebody writes down the wrong rule, it is that somebody adds a second
///         authentication handler to one of the two hosts because a scenario needed it, and nothing
///         notices.
///     </para>
///     <para>
///         The assertions here run against the host's <b>real</b> registration —
///         <c>AddIdentityHostAuthentication</c> — over a fresh service collection, so a new handler
///         added to that method fails this suite immediately.
///     </para>
/// </remarks>
public sealed class IdentityHostAuthenticationTests {
    [Fact]
    public async Task TheIdentityHostHasExactlyOneSchemeAndItIsACookie() {
        var schemes = await SchemesAsync();

        schemes.Count.ShouldBe(
            1,
            "The identity host authenticates with a session cookie and nothing else. A second scheme "
            + "here — a JWT bearer handler, an API-key handler — makes this origin accept a credential "
            + "the two-host split exists to keep off it (docs/plan/11 § Hosts). Registered: "
            + string.Join(", ", schemes.Select(x => x.Name))
        );

        schemes[0].Name.ShouldBe(IdentityHostAuthentication.SchemeName);
        schemes[0].HandlerType.ShouldBe(typeof(CookieAuthenticationHandler));
    }

    [Fact]
    public async Task NoBearerHandlerIsRegisteredOnTheCookieOrigin() {
        var schemes = await SchemesAsync();

        // ⚠ A session cookie must not authenticate an API call, and the mirror rule is that a bearer
        // token must not authenticate anything here. Named by type rather than by scheme name,
        // because a handler can be registered under any name.
        foreach (var scheme in schemes) {
            scheme.HandlerType.Name.ShouldNotContain("JwtBearer", Case.Insensitive);
            scheme.HandlerType.Name.ShouldNotContain("Validation", Case.Insensitive);
            scheme.HandlerType.FullName!.ShouldNotContain("OpenIddict.Validation", Case.Insensitive);
        }
    }

    [Fact]
    public void TheHostAssemblyCannotEvenNameTheBearerValidationHandler() {
        // ⚠ THE STRUCTURAL HALF, AND THE STRONGER ONE. The registration assertions above catch
        // somebody wiring a bearer handler; this catches somebody adding the package that makes it
        // possible. docs/plan/11 § Hosts wants the split to be structural rather than a middleware
        // setting, and a missing assembly reference is as structural as it gets — the code that would
        // accept a bearer token here does not compile.
        var referenced = typeof(IdentityHostAuthentication).Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name ?? string.Empty)
            .ToList();

        referenced.ShouldNotContain(
            "OpenIddict.Validation.AspNetCore",
            "The identity host ISSUES tokens and must not VALIDATE them. Referencing the validation "
            + "handler here is the one edit that would let a bearer token authenticate on the cookie "
            + "origin — docs/plan/11 § Hosts."
        );

        referenced.ShouldNotContain("Microsoft.AspNetCore.Authentication.JwtBearer");

        // …and it does reference the server half, so this test is not passing because the host is
        // empty.
        referenced.ShouldContain(x => x.StartsWith("OpenIddict.Server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSessionCookieCarriesEveryFlagThatMakesItSurvivableToHaveOne() {
        var options = await CookieOptionsAsync();

        // HttpOnly: script cannot read it, so an XSS does not become a stolen session.
        options.Cookie.HttpOnly.ShouldBeTrue();

        // Secure, always — not "same as request". A cookie that will travel over plain HTTP in some
        // configuration is a cookie that travels over plain HTTP.
        options.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);

        // ⚠ Lax rather than Strict, and it is the one real trade here. The OIDC authorization-code
        // flow returns to this origin via a cross-site redirect, and Strict drops the cookie on that
        // navigation — so the user arrives signed in and is asked to sign in. Lax withholds it from
        // cross-site POSTs, which is the CSRF case that matters.
        options.Cookie.SameSite.ShouldBe(SameSiteMode.Lax);

        options.Cookie.IsEssential.ShouldBeTrue();
    }

    [Fact]
    public async Task TheCookieNameCarriesTheHostPrefixSoASubdomainCannotSetIt() {
        var options = await CookieOptionsAsync();

        // ⚠ `__Host-` is enforced by the BROWSER: a cookie with this prefix is rejected unless it is
        // Secure, has Path=/, and carries NO Domain attribute. The last one is the point — no Domain
        // means no subdomain can set it, so an attacker-controlled host under the same registrable
        // domain cannot inject a session cookie for the identity origin. That is a real attack
        // against a platform that hands tenants subdomains, which this one does.
        options.Cookie.Name.ShouldStartWith("__Host-");
        options.Cookie.Path.ShouldBe("/");
        options.Cookie.Domain.ShouldBeNullOrEmpty();
        options.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);
    }

    [Fact]
    public void ThePublishedTokenPolicyTellsTheGatewayNotToIntrospect() {
        // The gateway is built against this on another branch. It is asserted from the host side too,
        // because the number that matters is the one the running host actually publishes.
        Contracts.AccessTokenPolicy.SupportsIntrospection.ShouldBeFalse();
        Contracts.AccessTokenPolicy.AccessTokensAreRevocable.ShouldBeFalse();
        Contracts.AccessTokenPolicy.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void TheOpenIddictRegistrationDoesNotPublishAnIntrospectionEndpoint() {
        // ⚠ docs/plan/11 § Sessions and revocation: an introspection call per request "would put the
        // identity system on the hot path of every request, which is precisely what a short token is
        // for". An endpoint that exists gets used, so the correct implementation publishes none —
        // and IdentityHostOpenIddict names the four endpoint constants it does publish.
        var paths = typeof(IdentityHostOpenIddict)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!)
            .ToList();

        paths.ShouldNotContain(x => x.Contains("introspect", StringComparison.OrdinalIgnoreCase));
        paths.ShouldNotContain(x => x.Contains("revoke", StringComparison.OrdinalIgnoreCase));

        paths.ShouldContain(IdentityHostOpenIddict.TokenPath);
        paths.ShouldContain(IdentityHostOpenIddict.AuthorizationPath);
    }

    static ServiceProvider Build() {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddIdentityHostAuthentication();

        return services.BuildServiceProvider();
    }

    static async Task<List<AuthenticationScheme>> SchemesAsync() {
        await using var provider = Build();

        var registry = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        return [.. await registry.GetAllSchemesAsync()];
    }

    static async Task<CookieAuthenticationOptions> CookieOptionsAsync() {
        await using var provider = Build();

        return provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityHostAuthentication.SchemeName);
    }
}
