using CyberCloud.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Identity.Host;

/// <summary>
///     Builds the identity host — the OAuth 2.1 / OIDC authorization server. docs/plan/11 § Hosts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This exists so a test can compose and start the real host, for the reason
///             <c>GatewayComposition</c> and <c>SiloComposition</c> give.
///         </b> Top-level statements cannot be called, so a host whose wiring lives in
///         <c>Program.cs</c> is a host nothing can assert against — and this one mapped no token
///         endpoint for its whole life so far while every suite in its test project stayed green,
///         because each of them built a <c>ServiceCollection</c> shaped like the host rather than the
///         host. <c>CyberCloud.AppHost.Tests</c>' <c>TenantOverHttpTests</c> now starts this
///         composition beside the gateway's and takes a token from one to the other.
///     </para>
///     <para>
///         ⚠ <b>COOKIES ARE A CREDENTIAL HERE AND NOWHERE ELSE.</b> docs/plan/11 § Hosts makes the
///         split from the gateway a security boundary rather than a scaling one: "a session cookie
///         must never be a credential the resource API accepts. If it is, every CSRF becomes a
///         control-plane write." What makes that structural rather than a setting: this project
///         references <c>OpenIddict.Server.AspNetCore</c> and NOT <c>OpenIddict.Validation.*</c>, so
///         the bearer-validation handler is not a type this assembly can name;
///         <see cref="IdentityHostAuthentication" /> registers exactly one scheme and it is a cookie;
///         <c>IdentityHostAuthenticationTests</c> asserts both against the real registrations. The
///         gateway is the mirror image: it references <c>OpenIddict.Validation.SystemNetHttp</c> and
///         not <c>OpenIddict.Server.*</c>, and validates what this host mints against the key set
///         this host publishes.
///     </para>
///     <para>
///         <c>Program.cs</c> keeps what is not composition: ABP's initialization, the middleware
///         order, and <c>RunAsync</c>.
///     </para>
/// </remarks>
public static class IdentityComposition {
    /// <summary>
    ///     Composes the identity host and returns the built host, ready to initialize and run.
    /// </summary>
    /// <param name="args">The process arguments, passed through to configuration.</param>
    /// <param name="configure">
    ///     What this deployment supplies on top of the composition, or <see langword="null" />.
    /// </param>
    /// <returns>The built host. Nothing has started.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <paramref name="configure" /> is for the vault seams and nothing else, and it runs
    ///             last so a registration made through it wins.
    ///         </b> <see cref="IdentityHostServices.AddIdentityHostApi" /> registers refusing
    ///         defaults for <c>ITotpSecretSeam</c> and <c>IClientSecretSeam</c> with <c>TryAdd</c>,
    ///         because the vault they read is a deployment's (docs/plan/18) and this repository
    ///         provisions none. A deployment with a vault registers its adapters here; a test
    ///         registers what its deployment would. Nothing about this parameter bypasses a check —
    ///         a verifier that answered <c>true</c> to everything would be a real registration doing
    ///         a real thing badly, which is what the refusing defaults exist to make visible.
    ///     </para>
    /// </remarks>
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        Action<IServiceCollection>? configure = null
    ) {
        ArgumentNullException.ThrowIfNull(args);

        // ⚠ CreateClient, not CreateBuilder, and the difference is what makes the sign-in endpoints
        // possible at all. This host has to reach IUserGrain, IEmailIndexGrain, ISessionGrain and
        // IServicePrincipalGrain, so it needs an IGrainFactory in the container — without one,
        // SignInService cannot be resolved and /api/signin/password can only ever be a stub. The
        // gateway is the same call for the same reason (docs/plan/03 § Hosts, docs/plan/10 § Shape).
        //
        // ⚠ CreateClient and not CreateSilo. Co-hosting a silo here would mean an identity deploy
        // moves grains, and it would put grain memory on a process whose scaling driver is browser
        // traffic.
        var builder = OrleansApplication.CreateClient(args);

        builder.Services.AddIdentityHostAuthentication();
        builder.Services.AddIdentityHostOpenIddict();
        builder.Services.AddIdentityHostApi(builder.Configuration);

        // ⚠ REQUIRED BEFORE Build(), AND THE FAILURE IF IT IS MISSING NAMES NOTHING USEFUL.
        // OrleansApplication.CreateClient calls builder.Host.UseAutofac(), and ABP's service-provider
        // factory resolves IModuleContainer during Build() — with no module registered the host dies
        // with "Could not find singleton service: Volo.Abp.Modularity.IModuleContainer,
        // Volo.Abp.Core", which mentions neither UseAutofac nor this line. OrleansApplication's own
        // remarks carry the full account.
        await builder.Services.AddApplicationAsync<IdentityHostModule>();

        // ⚠ LAST, so a deployment's registration wins over a TryAdd above and loses to nothing.
        configure?.Invoke(builder.Services);

        return builder.Build();
    }

    /// <summary>
    ///     Puts the middleware and the endpoints on a built host.
    /// </summary>
    /// <param name="app">The host <see cref="BuildAsync" /> returned.</param>
    /// <returns>The same host, so a caller can chain.</returns>
    /// <remarks>
    ///     ⚠ Order matters and this order is not the one that "reads" best. Authentication has to
    ///     run before authorization, and both before any endpoint — a pipeline with
    ///     <c>UseAuthorization</c> first produces endpoints that authorize an anonymous principal and
    ///     <c>403</c> everything, which looks like a policy bug. OpenIddict's own endpoints —
    ///     <c>/token</c>, <c>/.well-known/openid-configuration</c>, <c>/.well-known/jwks</c> — are
    ///     served by its authentication handler, which is why they need no mapping to exist and why
    ///     <c>UseAuthentication</c> is what puts them on the wire.
    /// </remarks>
    public static WebApplication MapIdentityHost(this WebApplication app) {
        ArgumentNullException.ThrowIfNull(app);

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapDefaultEndpoints();
        app.MapIdentityEndpoints();

        return app;
    }
}
