using CyberCloud.Gateway.Host;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

// ⚠ EVERY LINE OF COMPOSITION LIVES IN GatewayComposition AND NOT HERE. Top-level statements cannot
// be called from a test, so wiring in this file is wiring nothing can assert against — and this
// host's wiring was wrong in a way that produced no error anywhere: it composed the resource manager
// and registered no provider, so stage 6 resolved every path against an empty registry and answered
// the canonical 404. CyberCloud.Hosts.Tests composes GatewayComposition.BuildAsync and reads the
// registry it produced.
var app = await GatewayComposition.BuildAsync(args);

await app.Services
    .GetRequiredService<IAbpApplicationWithExternalServiceProvider>()
    .InitializeAsync(app.Services);

app.MapDefaultEndpoints();

// ── The one pipeline, in front of everything ──────────────────────────────────────────────────
//
// ⚠ A hub handshake goes through it too. A SignalR negotiate is an HTTP request, so it gets stages
// 1 to 5 — correlation, authentication, the tenant check and the connection concurrency limit —
// and only then reaches the hub. A hub mapped outside the pipeline would be an endpoint with no
// tenant establishment at all, which docs/plan/00 § The tenant-separation row, corrected makes a
// cross-tenant hole rather than an oversight.
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

await app.RunAsync();

/// <summary>The entry point's generated class, so the test project can reference this assembly.</summary>
public partial class Program;
