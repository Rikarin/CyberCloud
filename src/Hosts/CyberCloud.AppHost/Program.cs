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

// ── The hot tier ───────────────────────────────────────────────────────────────────────────────
//
// ⚠ DIVERGENCE FROM docs/plan/05 § Hot: this is a single Redis, not a Redis Cluster. The wiring
// under test is unaffected — HotTierConfigurator connects with StackExchange.Redis and the
// per-tenant decision is the `{cc:t:<id>}` hash tag, which a single node parses and ignores. What a
// single node cannot show is slot distribution across nodes; RedisClusterHashTagTests covers that
// arithmetic directly, and it does not need a cluster to be true.
var redis = builder.AddRedis(CyberCloudResources.Redis);

// ── The durable tier ───────────────────────────────────────────────────────────────────────────
//
// ⚠ DIVERGENCE FROM docs/plan/05 § Durable, which is "N plain Postgres servers that do not know
// about each other". These three shards are three databases on one server. What that preserves is
// everything the code can observe: three distinct connection strings, three distinct Npgsql pools,
// and a shard map that routes tenants across them — including the platform shard that carries every
// null-tenant grain. What it does not preserve is failure isolation, so "stop one shard and watch
// the blast radius" is not a thing you can do here. That is what TenancyCluster's three real
// containers are for (docs/plan/23's chaos-invariant 5); paying three container start-ups for it on
// every `dotnet run` would trade the thing ADR-014 is justified by — start-up time — for a property
// the test suite already has.
//
// ⚠ `AddDatabase` DOES NOT CREATE THE DATABASE. It declares a connection string with a `Database=`
// in it and a health check that opens it, and nothing else — so without `WithCreationScript` the
// three shard resources sit unhealthy forever and every dependent waits on them, with the only
// evidence being three lines in the PostgreSQL container's log:
//     FATAL: database "durable00" does not exist
// Observed exactly that. Aspire reports "waiting", not "misconfigured", because from its point of
// view a health check that fails is a resource that has not come up yet.
//
// The scripts are plain `CREATE DATABASE`, not guarded, because there is deliberately no data
// volume on this server: `dotnet run` starts from empty every time. Local state that survives a
// restart is a local state that drifts from what a colleague's run produces, and the durable tier is
// the one place a stale shard would look like working code.
var postgres = builder.AddPostgres(CyberCloudResources.Postgres);

var shardA = postgres.AddShard(CyberCloudResources.ShardA);
var shardB = postgres.AddShard(CyberCloudResources.ShardB);
var platformShard = postgres.AddShard(CyberCloudResources.PlatformShard);

// ── Streams ────────────────────────────────────────────────────────────────────────────────────
//
// ⚠ NOTHING CONSUMES THIS YET, and it is here anyway because docs/plan/24 § Phase 0 lists it. The
// stream provider is still a seam: OrleansApplication.CreateSilo's body names
// `.AddMultitenantStreams(StreamProviders.Events, …)` as not-yet-wired, and
// Microsoft.Orleans.Streaming.NATS is a prerelease that no project references
// (Directory.Packages.props § Orleans spells out why). The silos get the connection string, so the
// day that provider lands the AppHost does not change.
var nats = builder.AddNats(CyberCloudResources.Nats).WithJetStream();

// ── The Kubernetes data plane ──────────────────────────────────────────────────────────────────
//
// ADR-001 makes the Kubernetes API a data plane, and ADR-014 puts a k3s for it in this file.
//
// ⚠ THREE THINGS ARE LOAD-BEARING AND NONE OF THEM IS AN ASPIRE CONCEPT:
//
//  1. `--privileged`, `--tmpfs /run`, `--tmpfs /var/run` — k3s runs containerd, which needs a real
//     mount namespace and a writable non-overlay /run. Without the tmpfs mounts containerd starts
//     and then fails to create sandboxes. Testcontainers.K3s does exactly this; the arguments are
//     the same because the requirement is k3s', not the test library's.
//  2. `--write-kubeconfig` into a bind-mounted directory, mode 666. A kubeconfig that stays inside
//     the container is a cluster nothing can reach. This is the one thing Aspire has no shape for:
//     it has no "copy a file out when the container is ready".
//  3. A FIXED host port, and `--tls-san` naming the address that kubeconfig will contain. k3s bakes
//     `server: https://127.0.0.1:6443` into the file it writes, and the certificate has to have
//     that name in it.
var kubeconfigDirectory = Path.Combine(builder.AppHostDirectory, ".k3s");
Directory.CreateDirectory(kubeconfigDirectory);

var k3s = builder
    .AddContainer(CyberCloudResources.K3s, "rancher/k3s", "v1.32.5-k3s1")
    .WithContainerRuntimeArgs("--privileged", "--tmpfs", "/run", "--tmpfs", "/var/run")
    .WithArgs(
        "server",
        // Nothing in Cyber Cloud uses either, and both cost seconds and memory on every start.
        "--disable=traefik",
        "--disable=metrics-server",
        "--tls-san=127.0.0.1",
        "--tls-san=host.docker.internal",
        "--write-kubeconfig=/output/kubeconfig.yaml",
        "--write-kubeconfig-mode=666"
    )
    .WithBindMount(kubeconfigDirectory, "/output")
    .WithEndpoint(
        CyberCloudResources.K3sApiPort,
        CyberCloudResources.K3sApiPort,
        "https",
        "api",
        // ⚠ isProxied: false, and it is the difference between a kubeconfig that works and one that
        // lies. Aspire's default is to publish the container port on a random host port and put its
        // own proxy on the named one — which is invisible under `dotnet run` (the proxy does listen
        // on 6443) and absent under Aspire.Hosting.Testing, where the same test got
        // `Connection refused (127.0.0.1:6443)` while k3s was up and healthy. Unproxied, Docker
        // publishes 6443 → 6443 itself, so the address baked into the kubeconfig by
        // `--write-kubeconfig` is the address that works, in both.
        isProxied: false
    );

// ── The durable schema ─────────────────────────────────────────────────────────────────────────
//
// ⚠ WITHOUT THIS RESOURCE THE DURABLE TIER IS AN EMPTY DATABASE AND SILO 1 FAILS TO START.
//
// Microsoft.Orleans.Persistence.AdoNet ships zero SQL and does not migrate — the whole account is on
// CyberCloud.ServiceDefaults.Storage.OrleansAdoNetSchema. The scripts are bare `CREATE TABLE`, so
// they must run once, before the silos, which is exactly what `WaitForCompletion` expresses. The
// same program with the same argument is what a Helm pre-install hook Job would run; nothing about
// this step is Aspire-shaped, which is the point.
var durableSchema = builder
    .AddProject<CyberCloud_Silo_Host>(CyberCloudResources.DurableSchema)
    .WithArgs("--apply-durable-schema")
    .WithDurableShards(shardA, shardB, platformShard)
    .WaitFor(postgres);

// ── The two silos ──────────────────────────────────────────────────────────────────────────────
//
// ⚠ TWO `AddProject` CALLS, NOT `WithReplicas(2)`, AND THE REASON IS ORLEANS' PORTS.
//
// Aspire replicas are copies of one resource definition: same image or assembly, same environment,
// endpoint ports individually allocated. Orleans' silo-to-silo and gateway sockets are NOT Aspire
// endpoints — they are opened by the Orleans runtime from configuration, long after the process
// starts, and Aspire has no way to vary a configuration value per replica. Two replicas would
// therefore be handed the same CyberCloud:Cluster:LocalhostSiloPort and the second would die on
// AddressInUseException out of SocketConnectionListener. Two named resources with two ports is the
// honest spelling of what is actually two different configurations.
//
// ⚠ AND THEY MUST BE TOLD ABOUT EACH OTHER. `UseLocalhostClustering(silo, gateway)` defaults the
// primary-silo endpoint to the caller's own port, so two silos with distinct ports silently become
// TWO ONE-SILO CLUSTERS — see CyberCloudClusterOptions.LocalhostPrimarySiloPort. Silo 2 names silo
// 1 as the primary; that is the line that makes this a two-silo cluster rather than two clusters.
// ⚠ WHAT LETS A SILO ACTUALLY REACH THE k3s ABOVE, AND WITHOUT IT THE CLUSTER IS DECORATION.
//
// KubeApiClientFactory needs a `ResolveKubeconfig` delegate to turn a cluster connection's
// CredentialRef into kubeconfig bytes; with none registered it refuses every connect, so a reconcile
// of any type declaring RequiresCluster failed at its first apply — see LocalKubeconfigFiles. The
// silo registers the file-backed resolver when it is given a directory, and this is the directory:
// the same bind mount `--write-kubeconfig` writes into. Production sets no such key and keeps the
// refusal, because a production kubeconfig belongs in Vault (docs/plan/09 § Cluster connections).
var kubeconfigRoot = Path.GetFullPath(kubeconfigDirectory);

var siloOne = builder
    .AddProject<CyberCloud_Silo_Host>(CyberCloudResources.SiloOne)
    .WithCyberCloudStorage(redis, shardA, shardB, platformShard)
    .WithReference(nats)
    .WithEnvironment("CyberCloud__Silo__KubeconfigRoot", kubeconfigRoot)
    .WithOrleansPorts(CyberCloudResources.SiloOnePort, CyberCloudResources.SiloOneGatewayPort)
    // ⚠ The endpoint is declared, not inherited. Aspire reads a project's endpoints from its
    // launchSettings.json, and this host deliberately has none: it is launched by Aspire, by
    // `dotnet run` with plain environment variables, and in production by a container image, and a
    // launchSettings.json is a fourth answer that only one of those three reads. Without this line
    // the AppHost fails at start with "no endpoint was found matching one of the specified names:
    // https, http" — from WithHttpHealthCheck, which is where it is least expected.
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(durableSchema)
    .WaitFor(redis);

builder
    .AddProject<CyberCloud_Silo_Host>(CyberCloudResources.SiloTwo)
    .WithCyberCloudStorage(redis, shardA, shardB, platformShard)
    .WithReference(nats)
    .WithEnvironment("CyberCloud__Silo__KubeconfigRoot", kubeconfigRoot)
    .WithOrleansPorts(CyberCloudResources.SiloTwoPort, CyberCloudResources.SiloTwoGatewayPort)
    .WithEnvironment(
        "CyberCloud__Cluster__LocalhostPrimarySiloPort",
        CyberCloudResources.SiloOnePort.ToString(CultureInfo.InvariantCulture)
    )
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health")
    // ⚠ The development membership table lives in silo 1's process, so silo 2 cannot join before
    // silo 1 is serving. This is the ordering constraint that ADR-004's Kubernetes membership does
    // not have, and it is the price of not running a membership store on a laptop.
    .WaitFor(siloOne);

// ⚠ NOTHING WAITS ON k3s, ON PURPOSE. ADR-001 makes the Kubernetes API a data plane that is written
// to and reconciled against, not a dependency of the control plane booting — so a silo that refused
// to start without a cluster would be modelling the opposite of ADR-001. It also costs: k3s takes
// about 20 s to serve `/readyz` and the two silos are up in a third of that.
_ = k3s;

await builder.Build().RunAsync();
