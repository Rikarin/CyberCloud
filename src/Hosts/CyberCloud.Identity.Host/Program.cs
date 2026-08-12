using CyberCloud.Identity.Host;
using CyberCloud.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

// ── CyberCloud.Identity.Host — the OAuth 2.1 / OIDC authorization server ──────────────────────
//
// ⚠ COOKIES ARE A CREDENTIAL HERE AND NOWHERE ELSE. docs/plan/11 § Hosts makes the split from the
// gateway a security boundary rather than a scaling one: "a session cookie must never be a
// credential the resource API accepts. If it is, every CSRF becomes a control-plane write."
//
// What makes that structural rather than a setting:
//
//   * this project references OpenIddict.Server.AspNetCore and NOT OpenIddict.Validation.AspNetCore,
//     so the bearer-validation handler is not a type this assembly can name;
//   * AddIdentityHostAuthentication registers exactly one scheme and it is a cookie;
//   * IdentityHostAuthenticationTests asserts both, against the real registrations.
//
// The gateway is the mirror image and is built by somebody else. What it needs from this host is
// AccessTokenPolicy — the discovery path, the JWKS path, the algorithm, the ten-minute lifetime, and
// the fact that it must validate locally rather than introspect.

// ⚠ CreateClient, not CreateBuilder, and the difference is what makes the sign-in endpoints
// possible at all. This host has to reach IUserGrain, IEmailIndexGrain and ISessionGrain, so it
// needs an IGrainFactory in the container — without one, SignInService cannot be resolved and
// /api/signin/password can only ever be a stub. The gateway is the same call for the same reason
// (docs/plan/03 § Hosts, docs/plan/10 § Shape).
//
// ⚠ CreateClient and not CreateSilo. Co-hosting a silo here would mean an identity deploy moves
// grains, and it would put grain memory on a process whose scaling driver is browser traffic.
var builder = OrleansApplication.CreateClient(args);

builder.Services.AddIdentityHostAuthentication();
builder.Services.AddIdentityHostOpenIddict();
builder.Services.AddIdentityHostApi(builder.Configuration);

// ⚠ REQUIRED BEFORE Build(), AND THE FAILURE IF IT IS MISSING NAMES NOTHING USEFUL.
// OrleansApplication.CreateClient calls builder.Host.UseAutofac(), and ABP's service-provider
// factory resolves IModuleContainer during Build() — with no module registered the host dies with
// "Could not find singleton service: Volo.Abp.Modularity.IModuleContainer, Volo.Abp.Core", which
// mentions neither UseAutofac nor this line. OrleansApplication's own remarks carry the full
// account.
await builder.Services.AddApplicationAsync<IdentityHostModule>();

var app = builder.Build();

// ⚠ NOT app.InitializeApplicationAsync(), which is the line every ABP sample uses — that extension
// lives in Volo.Abp.AspNetCore, which docs/plan/02's dependency register does not list. This is the
// same initialisation without ABP's own middleware pipeline, which this host does not want: the
// pipeline below is deliberately only authentication and authorization.
await app.Services
    .GetRequiredService<IAbpApplicationWithExternalServiceProvider>()
    .InitializeAsync(app.Services);

// ⚠ Order matters and this order is not the one that "reads" best. Authentication has to run before
// authorization, and both before any endpoint — a pipeline with UseAuthorization first produces
// endpoints that authorize an anonymous principal and 403 everything, which looks like a policy bug.
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapIdentityEndpoints();

await app.RunAsync();

/// <summary>The entry point's generated class, so the test project can reference this assembly.</summary>
public partial class Program;
