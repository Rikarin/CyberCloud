using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ServiceDefaults;
using CyberCloud.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Gateway.Host;

/// <summary>
///     Builds the gateway, module graph and all. docs/plan/10 § Shape.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists so a test can compose the real host, for the same reason
///         <c>SiloComposition</c> does.</b> Top-level statements cannot be called, so composition that
///         lives in <c>Program.cs</c> is composition nothing can assert against — and this host spent
///         its whole life so far composing the resource manager and registering no provider, which made
///         every resource and action path a <c>404</c> and left no trace anywhere.
///     </para>
///     <para>
///         <c>Program.cs</c> keeps what is not composition: ABP's initialization, the one middleware
///         that runs the pipeline, the four hub mappings and <c>RunAsync</c>.
///     </para>
/// </remarks>
public static class GatewayComposition {
    /// <summary>
    ///     Composes the gateway and returns the built host, ready to initialize and run.
    /// </summary>
    /// <param name="args">The process arguments, passed through to configuration.</param>
    /// <param name="configure">
    ///     What this deployment supplies on top of the composition, or <see langword="null" />.
    /// </param>
    /// <returns>The built host. Nothing has started.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><paramref name="configure" /> exists because the identity seam is the one part of
    ///         this host that no deployment shares.</b> Stage 2 resolves
    ///         <c>ICallerContextResolver</c>, and there is deliberately no default: a gateway that
    ///         authenticated nobody and served anyway is the failure
    ///         <see cref="GatewayServiceCollectionExtensions.AddIssuedTokenAuthentication" />'s
    ///         remarks refuse to build. What a deployment registers here is a real registration —
    ///         nothing about this parameter bypasses a stage, relaxes a check or is read anywhere
    ///         else — and a host that passes nothing gets exactly what it got before this parameter
    ///         existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not a test hook, and the distinction is the one worth keeping.</b> A
    ///         test-only registration living in the composition root would mean production code
    ///         carrying a seam whose only caller is a test; this is the composition root taking its
    ///         deployment-specific half as a parameter, which is what lets a test compose the
    ///         <i>same</i> host as production and differ only where a deployment legitimately
    ///         differs. Its one caller today is
    ///         <c>CyberCloud.AppHost.Tests</c>' <c>TenantOverHttpTests</c>; when
    ///         <c>CyberCloud.Identity.Host</c> issues real tokens, the gateway's own
    ///         <c>Program.cs</c> becomes the second.
    ///     </para>
    /// </remarks>
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        Action<IServiceCollection>? configure = null
    ) {
        ArgumentNullException.ThrowIfNull(args);

        // ⚠ CreateClient, not CreateSilo — docs/plan/03 § Hosts and docs/plan/10 § Shape. The gateway
        // is I/O bound and scales with request rate; the silo is memory bound and scales with resident
        // grains. Co-hosting means one is always the wrong size, and a gateway deploy would move
        // grains.
        //
        // ⚠ AND IT IS THE REASON THIS PROCESS CARRIES THE TENANCY BOUNDARY BY ITSELF.
        // Orleans.Multitenant's TenantSeparatingCallFilter never runs for a client
        // (docs/plan/00 § The tenant-separation row, corrected, with the decompiled proof). Stage 3 of
        // the pipeline plus ForTenant everywhere is the whole mechanism. There is no second one.
        //
        // ⚠ IT IS ALSO WHY THIS HOST CANNOT START A SECOND RECONCILE LOOP. A client activates no
        // grain and has no reminder service, so the IRemindable that drives a reconcile — OperationGrain
        // — can only ever fire on a silo. Composing the resource manager here registers ReconcileDriver
        // and DriftScanner and resolves neither.
        var builder = OrleansApplication.CreateClient(args);

        var options = new GatewayOptions {
            Region = builder.Configuration[$"{GatewayOptions.SectionName}:Region"] ?? "",
            PublicBaseUri = builder.Configuration[$"{GatewayOptions.SectionName}:PublicBaseUri"]
                ?? "https://api.cybercloud.io",
            CurrentApiVersion = ApiVersion.Parse(
                builder.Configuration[$"{GatewayOptions.SectionName}:CurrentApiVersion"] ?? "2026-08-01"
            )
        };

        builder.Services.AddCyberCloudGateway(options);

        // ── The vault — docs/plan/18, docs/plan/12 § The pattern, once, piece 5 ────────────────────
        //
        // ⚠ THE GATEWAY AND NOT THE SILO, AND THE REASON IS WHERE ACTIONS RUN.
        // AddCyberCloudGateway composes the resource manager, so THIS process holds IResourceManager —
        // and ResourceManagerService.ActionAsync serves a non-LongRunning action inline rather than
        // through an operation on a silo. A `listKeys` therefore resolves its credential from the
        // resolver registered here.
        //
        // ⚠ CONDITIONAL, AND AN UNCONFIGURED SECTION LEAVES THE REFUSING SEAMS IN PLACE.
        // UnavailableSecretResolver's message names the missing call and the two configuration keys,
        // and it is the sentence an operator meets on the first `listKeys` this host is asked to serve.
        // Making the OpenBao resolver the default instead would replace it with a connection error
        // naming an address nobody set.
        //
        // ⚠ MISCONFIGURED IS NOT THE SAME AS UNCONFIGURED. IsConfigured tests the two keys the resolver
        // validates; anything past that — a plaintext address without AllowInsecureTransport, a
        // relative URL — throws out of AddOpenBaoSecretResolver at composition, so the pod does not
        // start. A host that opted in and got the address wrong should fail now rather than at 03:00.
        var vault = new VaultOptions();
        builder.Configuration.GetSection(VaultOptions.SectionName).Bind(vault);

        if (vault.IsConfigured) {
            builder.Services.AddOpenBaoSecretResolver(vault);
        }

        // docs/plan/10 § SignalR — AddSignalR and nothing else. No AddStackExchangeRedis: "No SignalR
        // backplane product." The fan-out is a connection grain plus Orleans streams, which is
        // O(interested) rather than O(pods).
        builder.Services.AddSignalR();

        // ⚠ THE TWELVE PROVIDER MODULES ARRIVE HERE, and until they did this host's IProviderRegistry
        // was built from an empty set. GatewayHostModule's [DependsOn] list is what registers them;
        // AddCyberCloudGateway above is what registers the registry that reads them, and the order does
        // not matter because the registry is a factory resolved after all wiring.
        await builder.Services.AddApplicationAsync<GatewayHostModule>();

        // ⚠ LAST, so a deployment's registration wins over a TryAdd above and loses to nothing.
        configure?.Invoke(builder.Services);

        return builder.Build();
    }

    /// <summary>
    ///     Puts the request pipeline and the four hubs on a built host.
    /// </summary>
    /// <param name="app">The host <see cref="BuildAsync" /> returned.</param>
    /// <returns>The same host, so a caller can chain.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the second half of the argument the top of this file makes, applied to
    ///         the half that was left behind.</b> <c>Program.cs</c> holds no composition because top
    ///         -level statements cannot be called from a test — and it went on holding the one
    ///         <c>app.Use</c> that runs the nine stages, which is exactly as untestable and rather
    ///         more load-bearing. The consequence was structural: <c>CyberCloud.Gateway.Host.Tests</c>
    ///         could run the stages only by constructing them itself against a
    ///         <c>DefaultHttpContext</c> with a substituted <c>IResourceManager</c>, and
    ///         <c>CyberCloud.AppHost.Tests</c> could reach the real manager only by resolving it out
    ///         of the container and calling it directly. Nothing could drive HTTP through to the real
    ///         manager, and the two suites met at an interface neither covered.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A hub handshake goes through the pipeline too</b> — a SignalR negotiate is an
    ///         HTTP request, so it gets stages 1 to 5 and only then reaches the hub. A hub mapped
    ///         outside this method would be an endpoint with no tenant establishment at all, which
    ///         docs/plan/00 § The tenant-separation row, corrected makes a cross-tenant hole rather
    ///         than an oversight. That is why the hubs are mapped here and not by the caller.
    ///     </para>
    /// </remarks>
    public static WebApplication MapGateway(this WebApplication app) {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) => {
            if (context.Request.Path.StartsWithSegments("/health")
                || context.Request.Path.StartsWithSegments("/alive")) {
                await next(context);

                return;
            }

            var pipeline = context.RequestServices.GetRequiredService<GatewayPipeline>();
            var result = await pipeline.RunAsync(context);

            if (result.Route.Kind == RouteKind.Hub) {
                context.Items[GatewayCallerFeature.ItemKey] = result.Caller;
                await next(context);
            }
        });

        app.MapHub<ResourcesHub>(GatewayRouter.HubPrefix + HubNames.Resources);
        app.MapHub<OperationsHub>(GatewayRouter.HubPrefix + HubNames.Operations);
        app.MapHub<MetricsHub>(GatewayRouter.HubPrefix + HubNames.Metrics);
        app.MapHub<TerminalHub>(GatewayRouter.HubPrefix + HubNames.Terminal);

        return app;
    }
}
