using CyberCloud.AppHost;
using Projects;
using System.Globalization;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  CyberCloud.AppHost — docs/plan/24 § Phase 0:
//
//    "`dotnet run` on the AppHost brings up a two-silo cluster with real Redis, Postgres, NATS and
//     k3s; a hello-world tenant-scoped grain round-trips through both storage tiers"
//
//  ⚠ ADR-014: LOCAL DEVELOPMENT ONLY. Nothing here is a deployment description. Production is Helm
//  charts, and the deliberate consequence is that this file is allowed to be *unlike* production
//  where that makes a laptop faster — one PostgreSQL server with three databases instead of three
//  servers, development clustering instead of Kubernetes membership. Each of those is called out
//  below, because an undocumented divergence is how "works locally" starts meaning nothing.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

var builder = DistributedApplication.CreateBuilder(args);

CyberCloudTopology.Compose(builder);

await builder.Build().RunAsync();
