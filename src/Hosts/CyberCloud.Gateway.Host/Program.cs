using CyberCloud.Gateway.Host;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ServiceDefaults;
using CyberCloud.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

// ⚠ CreateClient, not CreateSilo — docs/plan/03 § Hosts and docs/plan/10 § Shape. The gateway is
// I/O bound and scales with request rate; the silo is memory bound and scales with resident grains.
// Co-hosting them means one is always the wrong size, and a gateway deploy would move grains.
//
// ⚠ AND IT IS THE REASON THIS PROCESS CARRIES THE TENANCY BOUNDARY BY ITSELF.
// Orleans.Multitenant's TenantSeparatingCallFilter never runs for a client
// (docs/plan/00 § The tenant-separation row, corrected, with the decompiled proof). Stage 3 of the
// pipeline plus ForTenant everywhere is the whole mechanism. There is no second one.
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

// ── The vault — docs/plan/18, docs/plan/12 § The pattern, once, piece 5 ────────────────────────
//
// ⚠ THE GATEWAY AND NOT THE SILO, WHICH IS THE OPPOSITE OF WHERE EVERY COMMENT IN THE TREE SAID
// THIS LINE WOULD GO, AND THE REASON IS WHERE ACTIONS RUN.
//
// AddCyberCloudGateway composes the resource manager (see its remarks), so THIS process holds
// IResourceManager — and ResourceManagerService.ActionAsync serves a non-LongRunning action inline
// rather than through an operation on a silo. A `listKeys` therefore resolves its credential from
// the resolver registered here. CyberCloud.Silo.Host does not call AddCyberCloudResourceManager at
// all and does not reference the assembly, so there is no ISecretResolver in a silo to replace;
// adding this line there would be a registration nothing resolves.
//
// ⚠ CONDITIONAL, AND AN UNCONFIGURED SECTION LEAVES THE REFUSING SEAMS IN PLACE. That is the shape
// SiloIdentityOptions.AddSiloIdentity already uses for AddCommunicationOtpDelivery, and the
// argument is the same: UnavailableSecretResolver's message names the missing call and the two
// configuration keys, and it is the sentence an operator meets on the first `listKeys` this host is
// asked to serve. Making the OpenBao resolver the default instead would replace that sentence with
// a connection error naming an address nobody set.
//
// ⚠ MISCONFIGURED IS NOT THE SAME AS UNCONFIGURED. IsConfigured tests the two keys the resolver
// validates; anything past that — a plaintext address without AllowInsecureTransport, a relative
// URL — throws out of AddOpenBaoSecretResolver at composition, so the pod does not start. A host
// that opted in and got the address wrong should fail now rather than at 03:00.
var vault = new VaultOptions();
builder.Configuration.GetSection(VaultOptions.SectionName).Bind(vault);

if (vault.IsConfigured) {
    builder.Services.AddOpenBaoSecretResolver(vault);
}

// docs/plan/10 § SignalR — AddSignalR and nothing else. No AddStackExchangeRedis: "No SignalR
// backplane product." The fan-out is a connection grain plus Orleans streams, which is
// O(interested) rather than O(pods).
builder.Services.AddSignalR();

await builder.Services.AddApplicationAsync<GatewayHostModule>();

var app = builder.Build();

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
