using CyberCloud.Identity.Host;
using Microsoft.AspNetCore.Builder;

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

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdentityHostAuthentication();
builder.Services.AddIdentityHostOpenIddict();

var app = builder.Build();

// ⚠ Order matters and this order is not the one that "reads" best. Authentication has to run before
// authorization, and both before any endpoint — a pipeline with UseAuthorization first produces
// endpoints that authorize an anonymous principal and 403 everything, which looks like a policy bug.
app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityEndpoints();

await app.RunAsync();
