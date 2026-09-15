using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     <c>__Host-cyc-refresh</c>: every attribute, verbatim, and which clients get it at all.
///     docs/plan/10 § Authentication inputs, docs/plan/11 § Sessions and revocation.
/// </summary>
/// <remarks>
///     ⚠ Asserted on the <c>Set-Cookie</c> header string and not on a <see cref="CookieOptions" />,
///     because the attributes are what the browser enforces and the string is what the browser
///     reads. <c>__Host-</c> in particular is a browser rule about the string: <c>Secure</c>,
///     <c>Path=/</c> and no <c>Domain</c>, or the cookie is dropped without a word.
/// </remarks>
[Collection(IdentityHostSuite.Name)]
public sealed class RefreshCookieTests(IdentityHostFixture fixture) {
    [Fact]
    public void TheAttributesAreExactlyTheContractsVerbatim() {
        var context = new DefaultHttpContext();

        RefreshCookie.Issue(context.Response, "the-token");

        var header = context.Response.Headers.SetCookie.ToString();

        header.ShouldStartWith("__Host-cyc-refresh=the-token;");
        header.ShouldContain("path=/", Case.Insensitive);
        header.ShouldContain("secure", Case.Insensitive);
        header.ShouldContain("samesite=lax", Case.Insensitive);
        header.ShouldContain("httponly", Case.Insensitive);
        header.ShouldContain("max-age=1209600", Case.Insensitive);
        header.ShouldNotContain("domain=", Case.Insensitive, "a Domain attribute makes a __Host- cookie invalid — and would let a subdomain set it");

        RefreshCookie.MaxAge.ShouldBe(AccessTokenPolicy.RefreshTokenLifetime);
        RefreshCookie.MaxAge.ShouldBe(TimeSpan.FromSeconds(1209600));
    }

    [Fact]
    public void ClearWritesMaxAgeZero() {
        var context = new DefaultHttpContext();

        RefreshCookie.Clear(context.Response);

        var header = context.Response.Headers.SetCookie.ToString();

        // ⚠ The same name, path and Secure flag as the cookie it removes, or the browser keeps the
        // original beside the deletion; and max-age=0 rather than a past Expires, which is the
        // spelling every browser honours.
        header.ShouldStartWith("__Host-cyc-refresh=;");
        header.ShouldContain("max-age=0", Case.Insensitive);
        header.ShouldContain("path=/", Case.Insensitive);
        header.ShouldContain("secure", Case.Insensitive);
        header.ShouldContain("httponly", Case.Insensitive);
    }

    [Fact]
    public void ReadAnswersTheCookieOrNothing() {
        var context = new DefaultHttpContext();

        RefreshCookie.Read(context.Request).ShouldBeNull();

        context.Request.Headers.Cookie = "__Host-cyc-refresh=abc; other=1";
        RefreshCookie.Read(context.Request).ShouldBe("abc");

        context.Request.Headers.Cookie = "__Host-cyc-refresh=";
        RefreshCookie.Read(context.Request).ShouldBeNull("an empty value is no cookie");
    }

    [Fact]
    public async Task OnlyBrowserClientsGetTheCookie() {
        var handler = new DegradedModeHandlers.MoveRefreshTokenToCookie();

        // The portal: the token moves out of the body and into the cookie.
        var (context, http) = Apply(FirstPartyClients.Portal, OpenIddictConstants.GrantTypes.AuthorizationCode, refreshToken: "rt-portal");

        await handler.HandleAsync(context);

        context.Response.RefreshToken.ShouldBeNull("the body kept the token the cookie now carries");
        http.Response.Headers.SetCookie.ToString().ShouldStartWith("__Host-cyc-refresh=rt-portal;");

        // The CLI: a native app with its own keychain and no cookie jar. The body keeps it.
        (context, http) = Apply(FirstPartyClients.Cli, OpenIddictConstants.GrantTypes.AuthorizationCode, refreshToken: "rt-cli");

        await handler.HandleAsync(context);

        context.Response.RefreshToken.ShouldBe("rt-cli");
        http.Response.Headers.SetCookie.ToString().ShouldBeEmpty();

        // A tenant's own client, likewise.
        (context, http) = Apply("contoso-app", OpenIddictConstants.GrantTypes.AuthorizationCode, refreshToken: "rt-tenant");

        await handler.HandleAsync(context);

        context.Response.RefreshToken.ShouldBe("rt-tenant");
        http.Response.Headers.SetCookie.ToString().ShouldBeEmpty();

        FirstPartyClients.IsBrowserClient(FirstPartyClients.Portal).ShouldBeTrue();
        FirstPartyClients.IsBrowserClient(FirstPartyClients.Cli).ShouldBeFalse();
        FirstPartyClients.IsBrowserClient("contoso-app").ShouldBeFalse();
    }

    [Fact]
    public async Task ARefusedRefreshClearsTheCookieAndARefusedRequestDoesNot() {
        var handler = new DegradedModeHandlers.MoveRefreshTokenToCookie();

        // invalid_grant on a refresh: the chain refused the cookie, so the browser should stop
        // presenting it.
        var (context, http) = Apply(FirstPartyClients.Portal, OpenIddictConstants.GrantTypes.RefreshToken, error: OpenIddictConstants.Errors.InvalidGrant);

        await handler.HandleAsync(context);

        http.Response.Headers.SetCookie.ToString().ShouldContain("max-age=0", Case.Insensitive);

        // ⚠ invalid_request on a refresh — a foreign Origin, a missing parameter — says nothing about
        // the cookie, and clearing it would let a page on another origin sign the person out of the
        // portal with one POST.
        (context, http) = Apply(FirstPartyClients.Portal, OpenIddictConstants.GrantTypes.RefreshToken, error: OpenIddictConstants.Errors.InvalidRequest);

        await handler.HandleAsync(context);

        http.Response.Headers.SetCookie.ToString().ShouldBeEmpty();

        // And a refused code exchange leaves whatever cookie the browser holds alone.
        (context, http) = Apply(FirstPartyClients.Portal, OpenIddictConstants.GrantTypes.AuthorizationCode, error: OpenIddictConstants.Errors.InvalidGrant);

        await handler.HandleAsync(context);

        http.Response.Headers.SetCookie.ToString().ShouldBeEmpty();
    }

    (OpenIddictServerEvents.ApplyTokenResponseContext Context, HttpContext Http) Apply(
        string clientId,
        string grantType,
        string? refreshToken = null,
        string? error = null
    ) {
        var http = new DefaultHttpContext();

        var transaction = new OpenIddictServerTransaction {
            Request = new OpenIddictRequest { ClientId = clientId, GrantType = grantType },
            Response = new OpenIddictResponse { RefreshToken = refreshToken, Error = error },
            Options = fixture.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value,
            Logger = NullLogger.Instance,
            EndpointType = OpenIddictServerEndpointType.Token
        };

        transaction.Properties[typeof(HttpRequest).FullName!] = new WeakReference<HttpRequest>(http.Request);

        return (new(transaction), http);
    }
}
