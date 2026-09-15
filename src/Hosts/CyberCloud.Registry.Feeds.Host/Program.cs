using CyberCloud.Registry.Feeds.Host;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

// ── CyberCloud.Registry.Feeds.Host — NuGet, npm and Maven feeds, docs/plan/13 § Artifact feeds ──
//
// ⚠ EVERY LINE OF COMPOSITION LIVES IN FeedsComposition AND NOT HERE, for the reason the gateway,
// the identity host and the silo give in their own Program.cs: top-level statements cannot be
// called from a test, so wiring in this file is wiring nothing can assert against.
// CyberCloud.Hosts.Tests composes FeedsComposition.BuildAsync and CyberCloud.Registry.Feeds.Host.Tests
// starts it and drives the three protocols over HTTP.
var app = await FeedsComposition.BuildAsync(args);

// ⚠ NOT app.InitializeApplicationAsync() — that extension lives in Volo.Abp.AspNetCore, which
// docs/plan/02's dependency register does not list. The same initialisation without ABP's own
// middleware pipeline, as the gateway and the identity host do it.
await app.Services
    .GetRequiredService<IAbpApplicationWithExternalServiceProvider>()
    .InitializeAsync(app.Services);

app.MapFeeds();

await app.RunAsync();

/// <summary>The entry point's generated class, so the test project can reference this assembly.</summary>
public partial class Program;
