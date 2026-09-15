using CyberCloud.Identity.Host;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

// ── CyberCloud.Identity.Host — the OAuth 2.1 / OIDC authorization server ──────────────────────
//
// ⚠ EVERY LINE OF COMPOSITION LIVES IN IdentityComposition AND NOT HERE, for the reason the gateway
// and the silo give in their own Program.cs: top-level statements cannot be called from a test, so
// wiring in this file is wiring nothing can assert against. This host mapped no token endpoint for
// its whole life so far, and its test project stayed green throughout, because every suite built a
// ServiceCollection shaped like the host rather than the host. CyberCloud.AppHost.Tests composes
// IdentityComposition.BuildAsync now and takes a token from it to the real gateway.
//
// The cookie-versus-bearer boundary docs/plan/11 § Hosts draws, and what makes it structural, is
// written on IdentityComposition.
var app = await IdentityComposition.BuildAsync(args);

// ⚠ NOT app.InitializeApplicationAsync(), which is the line every ABP sample uses — that extension
// lives in Volo.Abp.AspNetCore, which docs/plan/02's dependency register does not list. This is the
// same initialisation without ABP's own middleware pipeline, which this host does not want: the
// pipeline MapIdentityHost builds is deliberately only authentication and authorization.
await app.Services
    .GetRequiredService<IAbpApplicationWithExternalServiceProvider>()
    .InitializeAsync(app.Services);

app.MapIdentityHost();

await app.RunAsync();

/// <summary>The entry point's generated class, so the test project can reference this assembly.</summary>
public partial class Program;
