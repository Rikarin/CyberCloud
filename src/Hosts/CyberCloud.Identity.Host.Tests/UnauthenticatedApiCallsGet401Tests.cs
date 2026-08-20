using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The cookie handler's <c>OnRedirectToLogin</c> is the one piece of this host's authentication
///     registration that <em>decides</em> something rather than declares it, and
///     <see cref="IdentityHostAuthenticationTests" /> asserts the declarations.
/// </summary>
/// <remarks>
///     ⚠ <b>A 302 to a login page on an XHR is a 200 with a login page in it.</b> Every client then
///     fails to parse the answer, and the failure surfaces as "the API returned HTML" somewhere far
///     away from the missing session that caused it. This host serves JSON under <c>/api</c> and
///     browser navigations everywhere else, so the branch is not optional and it is not observable
///     from the registration — only from running the delegate.
/// </remarks>
public sealed class UnauthenticatedApiCallsGet401Tests {
    static CookieAuthenticationOptions Registered() =>
        new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddIdentityHostAuthentication()
            .BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityHostAuthentication.SchemeName);

    static RedirectContext<CookieAuthenticationOptions> Redirect(string path) {
        var options = Registered();
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        return new(
            context,
            new(IdentityHostAuthentication.SchemeName, displayName: null, typeof(CookieAuthenticationHandler)),
            options,
            new AuthenticationProperties(),
            "https://identity.example/signin?returnUrl=" + Uri.EscapeDataString(path)
        );
    }

    [Theory]
    [InlineData("/api/signin/password")]
    [InlineData("/api")]
    [InlineData("/api/anything/at/all")]
    public async Task AnApiRequestWithNoSessionIs401AndIsNotRedirected(string path) {
        var context = Redirect(path);

        await context.Options.Events.RedirectToLogin(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        context.Response.Headers.Location.ToString().ShouldBeEmpty(
            "a redirect here turns an unauthenticated XHR into a 200 carrying a login page"
        );
    }

    [Theory]
    [InlineData("/apiary")]
    [InlineData("/apifoo/bar")]
    public async Task APathThatMerelyStartsWithTheLettersApiIsNotAnApiPath(string path) {
        // ⚠ StartsWithSegments, not StartsWith. `/apiary` is a page and must be redirected like one;
        // a string prefix test would 401 it, and the user would get an empty response instead of a
        // login screen with no clue why.
        var context = Redirect(path);

        await context.Options.Events.RedirectToLogin(context);

        context.Response.StatusCode.ShouldNotBe(StatusCodes.Status401Unauthorized);
        context.Response.Headers.Location.ToString().ShouldBe(context.RedirectUri);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/connect/authorize")]
    [InlineData("/signin")]
    public async Task ABrowserNavigationWithNoSessionIsSentToTheLoginPage(string path) {
        var context = Redirect(path);

        await context.Options.Events.RedirectToLogin(context);

        context.Response.Headers.Location.ToString().ShouldBe(context.RedirectUri);
    }

    [Fact]
    public async Task TheApiPrefixIsMatchedOrdinallyAndCaseSensitively() {
        // ⚠ The comparison is StringComparison.Ordinal on purpose. A case-insensitive match would
        // make `/API/...` a JSON path, and route matching elsewhere in the host is ordinal too — two
        // components disagreeing about what "/api" means is how a path ends up authenticated by one
        // set of rules and dispatched by another.
        var context = Redirect("/API/signin/password");

        await context.Options.Events.RedirectToLogin(context);

        context.Response.StatusCode.ShouldNotBe(StatusCodes.Status401Unauthorized);
    }
}
