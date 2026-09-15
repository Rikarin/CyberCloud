using CyberCloud.Core.Time;
using CyberCloud.Identity.Validation;
using CyberCloud.ObjectStorage;
using CyberCloud.Registry.Feeds.Host.Authentication;
using CyberCloud.Registry.Feeds.Host.Feeds;
using CyberCloud.Registry.Feeds.Host.Protocols;
using CyberCloud.ResourceManager;
using CyberCloud.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Registry.Feeds.Host;

/// <summary>
///     Builds the feeds host — the NuGet, npm and Maven service of docs/plan/13 § Artifact feeds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This exists so a test can compose and start the real host, for the reason
///             <c>GatewayComposition</c> and <c>IdentityComposition</c> give.
///         </b> Top-level statements cannot be called, so a host whose wiring lives in
///         <c>Program.cs</c> is a host nothing can assert against. <c>CyberCloud.Hosts.Tests</c>
///         composes this and reads the registry it produced;
///         <c>CyberCloud.Registry.Feeds.Host.Tests</c> starts it against an in-process silo and
///         drives <c>dotnet nuget push</c>, <c>npm publish</c> and <c>mvn deploy</c> over HTTP.
///     </para>
///     <para>
///         ⚠ <b>THREE REFUSING DEFAULTS AND ONE REFUSAL TO COMPOSE, in the order they are met.</b>
///         The object store is conditional like the gateway's vault: an unconfigured
///         <c>CyberCloud:ObjectStorage</c> leaves <c>UnavailableObjectStore</c> in place and the
///         pod starts, and every push meets a message naming the section. The bearer-token
///         validator is conditional like the gateway's identity seam, and for the same reason the
///         gateway learnt in #68 it is not allowed to be absent: a host with no
///         <c>IBearerTokenValidator</c> would build, start, pass its health checks and answer
///         <c>500</c> to every request, so the composition refuses, naming
///         <c>CyberCloud:Feeds:Identity:Issuer</c>.
///     </para>
///     <para>
///         <c>Program.cs</c> keeps what is not composition: ABP's initialization, the endpoint
///         mapping, and <c>RunAsync</c>.
///     </para>
/// </remarks>
public static class FeedsComposition {
    /// <summary>
    ///     Composes the feeds host and returns the built host, ready to initialize and run.
    /// </summary>
    /// <param name="args">The process arguments, passed through to configuration.</param>
    /// <param name="configure">
    ///     What this deployment supplies on top of the composition, or <see langword="null" />. Runs
    ///     last, so a registration made through it wins over a <c>TryAdd</c> above and loses to
    ///     nothing — a test registers the validator it can sign for and the object store it can
    ///     read back; a deployment registers nothing here that configuration does not already cover.
    /// </param>
    /// <returns>The built host. Nothing has started.</returns>
    /// <exception cref="InvalidOperationException">No bearer-token validator ended up registered.</exception>
    public static async Task<WebApplication> BuildAsync(string[] args, Action<IServiceCollection>? configure = null) {
        ArgumentNullException.ThrowIfNull(args);

        // ⚠ CreateClient, not CreateSilo, and not CreateBuilder — docs/plan/03 § Hosts. This host
        // reaches IFeedGrain and the resource manager's grains and activates none of them; a silo
        // here would make a feeds deploy move grains, and a bare web app would have no grain
        // factory to reach a catalogue with.
        var builder = OrleansApplication.CreateClient(args);

        var options = new FeedsOptions();
        builder.Configuration.GetSection(FeedsOptions.SectionName).Bind(options);

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton<IClock, SystemClock>();

        // ── The server's own body limits, raised to the cap — FeedsOptions.MaxRequestBodyBytes ──
        //
        // ⚠ WITHOUT THESE TWO LINES THE CAP IS A NUMBER NOBODY REACHES. Kestrel answers 413 to any
        // body over 30,000,000 bytes before a handler sees it, and ReadFormAsync stops a multipart
        // body at 128 MiB; the 256 MiB the shipped appsettings.json promises was refused at 28.6 MiB
        // with a message that named neither the cap nor the key. FeedsLimitsTests reads both limits
        // back off the running host.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);
        builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = options.MaxRequestBodyBytes);

        // ── The resource manager, for ReadAsync and IResourceAuthorizer — docs/plan/07 ─────────
        //
        // ⚠ ONE CALL, INTO THE ASSEMBLY THAT OWNS THE SEAM, for the reason the gateway gives: naming
        // ReBacResourceAuthorizer here would be naming the engine's registration in a data-plane
        // host. The registry it builds comes from the one provider module FeedsHostModule loads.
        builder.Services.AddCyberCloudResourceManager();

        // ── The object store — docs/plan/13, docs/plan/15 § Object storage ─────────────────────
        //
        // Conditional like the gateway's vault, and an unconfigured section leaves the refusing
        // default in place: UnavailableObjectStore's message names AddS3ObjectStore and the four
        // keys. Misconfigured is not unconfigured — a plain-http endpoint without the flag throws
        // out of AddS3ObjectStore at composition, so the pod does not start.
        var storage = new ObjectStorageOptions();
        builder.Configuration.GetSection(ObjectStorageOptions.SectionName).Bind(storage);

        if (storage.IsConfigured) {
            builder.Services.AddS3ObjectStore(storage);
        }

        // ── The three protocols and the path every one of them goes through ────────────────────
        builder.Services.AddSingleton<FeedCredentialResolver>();
        builder.Services.AddSingleton<FeedAccess>();
        builder.Services.AddSingleton<NuGetProtocol>();
        builder.Services.AddSingleton<NpmProtocol>();
        builder.Services.AddSingleton<MavenProtocol>();

        // ⚠ THE SAME LINE THE SILO CARRIES, FOR THE SAME REASON, AND IT WAS FOUND THE SAME WAY.
        // ContainerRegistryApplicationModule depends on AbpDddApplicationModule, whose graph brings
        // Volo.Abp.Authorization, which registers enough of ASP.NET Core's authorization surface for
        // WebApplication to insert UseAuthorization by itself — and that middleware throws "Unable to
        // find the required services … AddAuthorization" from ConfigureApplication, which runs on
        // StartAsync and not on Build. The gateway does not need it because it registers no
        // authorization services of its own; this host loads a provider module the way the silo
        // does, and CyberCloud.Registry.Feeds.Host.Tests' fixture is where the host first started
        // and died on this. No endpoint here carries [Authorize]: FeedAccess is the authorization.
        builder.Services.AddAuthorization();

        // ⚠ REQUIRED BEFORE Build(), and the failure if it is missing names nothing useful —
        // OrleansApplication's remarks carry the full account. The module brings the provider.
        await builder.Services.AddApplicationAsync<FeedsHostModule>();

        // ── The bearer-token validator — docs/plan/11 § Protocol ───────────────────────────────
        if (options.Identity.IsConfigured) {
            builder.Services.AddJwksBearerTokenValidation(options.Identity.ToBearerTokenOptions(), FeedsIdentityOptions.SectionName);
        }

        // ⚠ LAST, so a deployment's registration wins over a TryAdd above and loses to nothing.
        configure?.Invoke(builder.Services);

        // ⚠ THE CHECK ValidateOnBuild WOULD HAVE BEEN, IF IT RAN — it does not, because UseAutofac
        // replaces the provider factory it belongs to. FeedAccess is resolved per request by the
        // endpoints, so a container missing the validator builds, starts, reports healthy and
        // throws on the first real request; #68 is the gateway's record of that shape.
        if (builder.Services.All(x => x.ServiceType != typeof(IBearerTokenValidator))) {
            throw new InvalidOperationException(
                "The feeds host has no IBearerTokenValidator, so no request can be authenticated and "
                + "every push and pull would fail — this refusal is instead of a host that starts, "
                + $"passes its health checks and answers 500 to everything else. Set {FeedsIdentityOptions.SectionName}"
                + ":Issuer to the identity host's origin (the shipped appsettings.json carries it), or "
                + "register a validator through BuildAsync's configure parameter."
            );
        }

        return builder.Build();
    }

    /// <summary>
    ///     Puts the three protocols on a built host.
    /// </summary>
    /// <param name="app">The host <see cref="BuildAsync" /> returned.</param>
    /// <returns>The same host, so a caller can chain.</returns>
    /// <remarks>
    ///     <para>
    ///         Every route is <c>/{kind}/{subscription}/{group}/{feed}/…</c> — <see cref="FeedUrls" />
    ///         — and every handler's first line is <c>FeedAccess.ResolveAsync</c>, so there is no
    ///         endpoint on this host that serves a byte before a credential has validated and the
    ///         feed has been read through the resource manager. The health endpoints are the one
    ///         exception, and they serve no tenant's byte.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>npm is one catch-all route</b>, dispatched inside <see cref="NpmProtocol" />,
    ///         because a scoped package name carries a slash — see that class for the six shapes.
    ///         Maven is two catch-all routes because its whole protocol is "a path".
    ///     </para>
    /// </remarks>
    public static WebApplication MapFeeds(this WebApplication app) {
        ArgumentNullException.ThrowIfNull(app);

        app.MapDefaultEndpoints();

        var nuget = app.MapGroup(FeedUrls.RoutePrefix(FeedKind.NuGet));

        nuget.MapGet(
            "/v3/index.json",
            (HttpContext http, Guid subscription, string group, string feed, NuGetProtocol protocol) =>
                protocol.ServiceIndexAsync(http, subscription, group, feed)
        );

        nuget.MapPut(
            "/v2/package",
            (HttpContext http, Guid subscription, string group, string feed, NuGetProtocol protocol) =>
                protocol.PushAsync(http, subscription, group, feed)
        );

        nuget.MapDelete(
            "/v2/package/{id}/{version}",
            (HttpContext http, Guid subscription, string group, string feed, string id, string version, NuGetProtocol protocol) =>
                protocol.UnlistAsync(http, subscription, group, feed, id, version)
        );

        nuget.MapPost(
            "/v2/package/{id}/{version}",
            (HttpContext http, Guid subscription, string group, string feed, string id, string version, NuGetProtocol protocol) =>
                protocol.RelistAsync(http, subscription, group, feed, id, version)
        );

        nuget.MapGet(
            "/v3/flatcontainer/{id}/index.json",
            (HttpContext http, Guid subscription, string group, string feed, string id, NuGetProtocol protocol) =>
                protocol.VersionsAsync(http, subscription, group, feed, id)
        );

        nuget.MapGet(
            "/v3/flatcontainer/{id}/{version}/{file}",
            (HttpContext http, Guid subscription, string group, string feed, string id, string version, string file, NuGetProtocol protocol) =>
                protocol.DownloadAsync(http, subscription, group, feed, id, version, file)
        );

        nuget.MapGet(
            "/v3/registration/{id}/index.json",
            (HttpContext http, Guid subscription, string group, string feed, string id, NuGetProtocol protocol) =>
                protocol.RegistrationIndexAsync(http, subscription, group, feed, id)
        );

        nuget.MapGet(
            "/v3/registration/{id}/{version}.json",
            (HttpContext http, Guid subscription, string group, string feed, string id, string version, NuGetProtocol protocol) =>
                protocol.RegistrationLeafAsync(http, subscription, group, feed, id, version)
        );

        nuget.MapGet(
            "/v3/query",
            (HttpContext http, Guid subscription, string group, string feed, NuGetProtocol protocol) =>
                protocol.SearchAsync(http, subscription, group, feed)
        );

        app.Map(
            FeedUrls.RoutePrefix(FeedKind.Npm) + "/{**rest}",
            (HttpContext http, Guid subscription, string group, string feed, string rest, NpmProtocol protocol) =>
                protocol.DispatchAsync(http, subscription, group, feed, rest)
        );

        var maven = app.MapGroup(FeedUrls.RoutePrefix(FeedKind.Maven));

        maven.MapPut(
            "/{**path}",
            (HttpContext http, Guid subscription, string group, string feed, string path, MavenProtocol protocol) =>
                protocol.PutAsync(http, subscription, group, feed, path)
        );

        maven.MapMethods(
            "/{**path}",
            [HttpMethods.Get, HttpMethods.Head],
            (HttpContext http, Guid subscription, string group, string feed, string path, MavenProtocol protocol) =>
                protocol.GetAsync(http, subscription, group, feed, path)
        );

        return app;
    }
}
