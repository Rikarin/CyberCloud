using CyberCloud.Gateway.Host;
using CyberCloud.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

// ⚠ EVERY LINE OF COMPOSITION LIVES IN GatewayComposition AND NOT HERE. Top-level statements cannot
// be called from a test, so wiring in this file is wiring nothing can assert against — and this
// host's wiring was wrong in a way that produced no error anywhere: it composed the resource manager
// and registered no provider, so stage 6 resolved every path against an empty registry and answered
// the canonical 404. CyberCloud.Hosts.Tests composes GatewayComposition.BuildAsync and reads the
// registry it produced.
//
// ⚠ AND THAT ARGUMENT NOW COVERS THE REQUEST PATH TOO. The one app.Use that runs the nine stages,
// and the four hub mappings behind it, used to live in this file — as untestable as the wiring
// above and rather more load-bearing, which is why nothing in the repository could drive an HTTP
// request through to the real resource manager. They are GatewayComposition.MapGateway now, called
// below in the same place and the same order.
var app = await GatewayComposition.BuildAsync(args);

await app.Services
    .GetRequiredService<IAbpApplicationWithExternalServiceProvider>()
    .InitializeAsync(app.Services);

app.MapDefaultEndpoints();
app.MapGateway();

await app.RunAsync();

/// <summary>The entry point's generated class, so the test project can reference this assembly.</summary>
public partial class Program;
