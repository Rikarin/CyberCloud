using Projects;
using System.Globalization;

namespace CyberCloud.AppHost;

/// <summary>
///     The whole platform, as one method over an <see cref="IDistributedApplicationBuilder" />.
/// </summary>
/// <remarks>
///     <para>
///         <c>Program.cs</c> calls this and runs what it built; <c>AppHostTopologyTests</c> calls it
///         over a builder of its own and reads the model back <i>without starting anything</i>.
///         That second caller is why this is a method and not the body of <c>Program.cs</c>:
///         <c>Aspire.Hosting.Testing</c>'s builder runs the AppHost's entry point, and the entry
///         point ends in <c>RunAsync</c> — so "build the model to look at it" through that route
///         is "start k3s, two silos and three hosts to look at it", which was measured the day this
///         file was made and is the reason it exists.
///     </para>
///     <para>
///         The prose is the point of this file as much as the code. Every ⚠ below records a way the
///         topology was wrong once, or a way it deliberately differs from production (ADR-014), and
///         the divergences are the part a reader most needs when "works locally" stops meaning
///         anything.
///     </para>
/// </remarks>
public static class CyberCloudTopology {
    /// <summary>
    ///     <c>CyberCloud:Identity:SelfServeSignUp</c>, as an environment variable — the silo's
    ///     <c>PlatformBootstrapTask.SelfServeSignUpKey</c> and the identity host's
    ///     <c>IdentityHostOptions.SelfServeSignUp</c> both read it.
    /// </summary>
    public const string SelfServeSignUpVariable = "CyberCloud__Identity__SelfServeSignUp";

    /// <summary>Declares every resource of the local platform on <paramref name="builder" />.</summary>
    /// <param name="builder">A fresh builder; nothing is expected to be on it yet.</param>
    public static void Compose(IDistributedApplicationBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

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
        // The resource-changed stream (docs/plan/04 § Streams) — JetStream, because the projector below
        // pulls it through a durable consumer and a plain NATS keeps nothing to pull. Until #54 nothing
        // consumed this; the silos and the gateway now read the connection string it renders
        // (ConnectionStrings__nats) as ResourceGraphOptions' NATS URL. ⚠ The GATEWAY gets it too, and
        // that line is what makes the stream carry anything: ResourceManagerService emits step 11 from
        // the gateway's process, so a gateway without NATS keeps the logging sink and the projection
        // learns only the silo-side transitions.
        //
        // ⚠ Not the Orleans stream provider. OrleansApplication.CreateSilo's body still names
        // `.AddMultitenantStreams(StreamProviders.Events, …)` as a seam, and it stays one:
        // Microsoft.Orleans.Streaming.NATS has never shipped without an -alpha suffix
        // (Directory.Packages.props § Orleans), so CyberCloud.ResourceGraph speaks JetStream directly.
        var nats = builder.AddNats(CyberCloudResources.Nats).WithJetStream();

        // ── The region's ClickHouse — docs/plan/05 § Every store, docs/plan/08 § The resource-graph projection ──
        //
        // Where the silos' ResourceGraphProjector writes each tenant's resource_graph table. One
        // container, one user, no volume: the same shape ProjectionRoundTripTests stands up. /ping is
        // the readiness answer ClickHouse gives once it listens, and it reads CLICKHOUSE_USER before it
        // listens, so a 200 here is a server whose user exists.
        var clickHouse = builder
            .AddContainer(CyberCloudResources.ClickHouse, "clickhouse/clickhouse-server", "25.3-alpine")
            .WithEnvironment("CLICKHOUSE_USER", CyberCloudResources.ClickHouseUser)
            .WithEnvironment("CLICKHOUSE_PASSWORD", CyberCloudResources.ClickHousePassword)
            .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
            .WithEndpoint(CyberCloudResources.ClickHouseHttpPort, CyberCloudResources.ClickHouseHttpPort, "http", "http", isProxied: false)
            .WithHttpHealthCheck("/ping", 200, "http");

        // ── The Kubernetes data plane ──────────────────────────────────────────────────────────────────
        //
        // ADR-001 makes the Kubernetes API a data plane, and ADR-014 puts a k3s for it in this file.
        //
        // ⚠ FOUR THINGS ARE LOAD-BEARING AND NONE OF THEM IS AN ASPIRE CONCEPT:
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
        //  4. THE ENTRYPOINT IS A SHELL THAT MAKES /var/run SHARED AND THEN EXECS k3s. A `--tmpfs` is a
        //     private mount, and KubeVirt's `virt-handler` refuses to run on such a node — `path
        //     "/var/run/kubevirt" is mounted on "/var/run" but it is not a shared mount` — so until
        //     2026-09-15 charts/bundle/kubevirt was the one component this topology could never host
        //     (issue #2). Aspire can run nothing after a container starts: `WithContainerRuntimeArgs`
        //     reaches `docker run` and nothing reaches `docker exec`, and Docker has no propagation flag
        //     for a tmpfs (`bind-propagation` is for binds). So the fix is the container's own first
        //     command — `/bin/sh -c 'mount --make-rshared /var/run && exec /bin/k3s "$@"' k3s server …`
        //     — the same four strings test/CyberCloud.Cluster.Conformance's ClusterInfrastructure and
        //     CyberCloud.Kubernetes.Tests' K3sFixture hand Testcontainers, so the three k3s recipes in
        //     this repository are one recipe. `"$@"` is expanded by the shell INSIDE the container from
        //     the arguments after `k3s` (its $0); Aspire passes the array to Docker unparsed. Measured
        //     the same day on a throwaway container: /proc/self/mountinfo shows `/var/run … shared`,
        //     /run stays private, and "k3s is up and running" is logged five seconds in.
        var kubeconfigDirectory = Path.Combine(builder.AppHostDirectory, ".k3s");
        Directory.CreateDirectory(kubeconfigDirectory);

        var k3s = builder
            .AddContainer(CyberCloudResources.K3s, "rancher/k3s", "v1.32.5-k3s1")
            .WithContainerRuntimeArgs("--privileged", "--tmpfs", "/run", "--tmpfs", "/var/run")
            .WithEntrypoint("/bin/sh")
            .WithArgs(
                "-c",
                "mount --make-rshared /var/run && exec /bin/k3s \"$@\"",
                "k3s",
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

        // ── The object store ───────────────────────────────────────────────────────────────────────────
        //
        // SeaweedFS with its S3 gateway — what CyberCloud.ContainerRegistry/feeds writes artefacts to (#29),
        // through the hand-written SigV4 client in CyberCloud.ObjectStorage. The same image
        // SeaweedFsRoundTripTests stands up, with the same one-identity s3.json, so the store the AppHost
        // runs is the store the client is tested against.
        //
        // ⚠ THE BUCKET HAS TO BE MADE, AND THE CLIENT WILL NOT MAKE IT. IObjectStore writes into a bucket
        // an operator created — PUT /{bucket} is the one S3 call it deliberately does not know — and
        // SeaweedFS answers NoSuchBucket rather than creating one on the first PutObject. A bucket, to
        // SeaweedFS, is a directory under the filer's /buckets, and the filer's own HTTP API creates a
        // directory on the first upload into it; so the init container below is one POST of an empty file
        // to the filer, which is the whole of "create the bucket". The feeds host and the silo wait on it.
        //
        // ⚠ Both ports are published unproxied, like k3s' above and for the same reason: the endpoint has
        // to BE the address, because the SigV4 signature covers the Host header the client sends.
        var objectStoreIdentities =
            $$"""
              {"identities":[{"name":"cybercloud","credentials":[{"accessKey":"{{CyberCloudResources.ObjectStoreAccessKeyId}}","secretKey":"{{CyberCloudResources.ObjectStoreSecretAccessKey}}"}],"actions":["Admin","Read","Write","List","Tagging"]}]}
              """;
        var objectStoreConfigDirectory = Path.Combine(builder.AppHostDirectory, ".seaweedfs");
        var objectStoreConfigFile = Path.Combine(objectStoreConfigDirectory, "s3.json");
        Directory.CreateDirectory(objectStoreConfigDirectory);

        // ⚠ WRITTEN ONLY WHEN IT DIFFERS, AND THE REASON IS DOCKER DESKTOP ON WINDOWS. A bind-mounted
        // file is held open by the container that mounts it, and a SeaweedFS from the previous run —
        // a `dotnet run` killed a moment ago, a test topology still tearing down — holds this one.
        // An unconditional write then dies with "being used by another process" out of a line that
        // reads as a formatting step, and the whole AppHost with it. The content is a constant, so
        // after the first run there is nothing to write.
        //
        // ⚠ AND THE READ ITSELF DIES THE SAME WAY, WHICH THE GUARD ABOVE DID NOT ALLOW FOR. Measured on
        // 2026-09-17 by CyberCloud.AppHost.Tests run whole: AppHostTopologyTests builds this model in
        // the same process and at the same moment as the LocalTopology collection fixture starts the
        // real one, so the SeaweedFS the fixture started holds the bind-mounted file exactly while
        // `SelfServeSignUpIsOneDecisionOnAllThreeSides` reaches this line — `File.ReadAllText` threw
        // "being used by another process" out of a step that reads as a comparison, and the model
        // test failed with a message about a SeaweedFS it never started. This method is the only
        // writer of the file and the content is a constant, so a file that exists and cannot be read
        // is one this method wrote with this content and a container is mounting; comparing it would
        // answer "equal", and the answer is taken without the read.
        //
        // ⚠ AND THE WRITE, ON A CHECKOUT THAT HAS NO FILE YET, RACES WITH ITSELF. The guard above reads
        // nothing when the file is absent, so two Compose calls in one process — the LocalTopology
        // fixture's and AppHostTopologyTests', which xUnit starts at the same moment — both see
        // "absent" and both write; `File.WriteAllText` opens with FileShare.Read, so the second dies
        // with the same "being used by another process" 53 ms into a test that started no container,
        // and only on a checkout that has never run the AppHost — which is every CI run, and which
        // the first fix never measured (the review of it did: 1/27 red with the file absent, 2 of 2
        // times). So the check and the write are one locked section within a process, and a write
        // refused from outside the process is taken as another writer of the same constant.
        EnsureObjectStoreIdentityFile(objectStoreConfigFile, objectStoreIdentities);

        var objectStore = builder
            .AddContainer(CyberCloudResources.ObjectStore, "chrislusf/seaweedfs", "3.80")
            .WithArgs("server", "-s3", "-s3.config=/etc/seaweedfs/s3.json", "-dir=/data", "-ip.bind=0.0.0.0")
            .WithBindMount(objectStoreConfigDirectory, "/etc/seaweedfs", isReadOnly: true)
            .WithEndpoint(CyberCloudResources.ObjectStoreS3Port, CyberCloudResources.ObjectStoreS3Port, "http", "s3", isProxied: false)
            .WithEndpoint(CyberCloudResources.ObjectStoreFilerPort, CyberCloudResources.ObjectStoreFilerPort, "http", "filer", isProxied: false)
            // ⚠ 403, not 200, and on the S3 port rather than the filer's — SeaweedFsRoundTripTests' finding:
            // the gateway prints its banner before the filer it depends on is ready, and a 403 to an
            // unsigned GET / is the earliest answer that means "the S3 gateway is up and checking
            // credentials". A 200 from the filer's own port comes sooner and means less.
            .WithHttpHealthCheck("/", 403, "s3");

        var objectStoreBucket = builder
            .AddContainer(CyberCloudResources.ObjectStoreBucketInit, "curlimages/curl", "8.12.1")
            .WithArgs(
                "--fail", "--silent", "--show-error", "--retry", "10", "--retry-connrefused", "--retry-delay", "2",
                "-F", "file=@/dev/null;filename=.bucket",
                $"http://{CyberCloudResources.ObjectStore}:{CyberCloudResources.ObjectStoreFilerPort.ToString(CultureInfo.InvariantCulture)}/buckets/{CyberCloudResources.ObjectStoreBucket}/.bucket"
            )
            .WaitFor(objectStore);

        // ── The mail relay — docs/plan/17, #93 ─────────────────────────────────────────────────────────
        //
        // Mailpit: an SMTP server on 1025 that accepts every message and delivers none, and a web inbox
        // on 8025 where they all land. It is the carrier behind CyberCloud.Communication's email channel
        // on this run — the smtp carrier the silos register when CyberCloud:Communication:Smtp names a
        // relay (WithDevelopmentMailRelay) — so a sign-up code and an alert arrive somewhere a person can
        // open, http://localhost:8025, instead of only on a silo's console. The console line stays.
        //
        // ⚠ THE SAME IMAGE TAG SmtpChannelProviderTests RUNS THE CARRIER AGAINST, and AppHostTopologyTests
        // holds the two together: the dialect the client is proven against is the dialect this run speaks.
        //
        // ⚠ BOTH PORTS UNPROXIED, like k3s' and SeaweedFS', because the endpoint has to BE the address:
        // the silos are handed the literal localhost:1025, and a person is told http://localhost:8025.
        //
        // ⚠ NOTHING WAITS ON IT, for the reason nothing waits on k3s below: a carrier is a data plane the
        // control plane refuses honestly without. A code minted before Mailpit is up is logged on the
        // console and its mail is a Warning beside it (DevelopmentOtpDelivery); Mailpit is up in about a
        // second and the silos take longer than that to start, so in practice the first code lands.
        builder
            .AddContainer(CyberCloudResources.Mailpit, CyberCloudResources.MailpitImage, CyberCloudResources.MailpitTag)
            .WithEndpoint(CyberCloudResources.MailpitSmtpPort, CyberCloudResources.MailpitSmtpPort, "tcp", "smtp", isProxied: false)
            .WithEndpoint(CyberCloudResources.MailpitHttpPort, CyberCloudResources.MailpitHttpPort, "http", "http", isProxied: false)
            .WithHttpHealthCheck("/readyz", 200, "http");

        var siloOne = builder
            .AddProject<CyberCloud_Silo_Host>(CyberCloudResources.SiloOne)
            .WithCyberCloudStorage(redis, shardA, shardB, platformShard)
            .WithReference(nats)
            .WithResourceGraph()
            .WithObjectStore()
            .WithDevelopmentMailRelay()
            .WithEnvironment("CyberCloud__Silo__KubeconfigRoot", kubeconfigRoot)
            // ⚠ Self-serve sign-up is a decision three processes have to agree on, and this is the first
            // of the three. On a silo it makes PlatformBootstrapTask write the platform:root#operator
            // grant sign-up creates tenants under; on the identity host it opens /api/signup/*. Both
            // silos carry it because either may be the one that starts first, and the task is
            // idempotent. The enrolment code goes to Mailpit's inbox through the relay above AND to
            // the silo's console — DevelopmentOtpDelivery, read in the dashboard (#93).
            .WithEnvironment(SelfServeSignUpVariable, "true")
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
            .WaitForCompletion(objectStoreBucket)
            .WaitFor(redis)
            // The projector reconnects on its own, so this is a quieter start rather than a
            // correctness need: without it the first seconds of the silo's log are NATS and
            // ClickHouse refusals that read like a misconfiguration.
            .WaitFor(nats)
            .WaitFor(clickHouse);

        builder
            .AddProject<CyberCloud_Silo_Host>(CyberCloudResources.SiloTwo)
            .WithCyberCloudStorage(redis, shardA, shardB, platformShard)
            .WithReference(nats)
            .WithResourceGraph()
            .WithObjectStore()
            .WithDevelopmentMailRelay()
            .WithEnvironment("CyberCloud__Silo__KubeconfigRoot", kubeconfigRoot)
            .WithEnvironment(SelfServeSignUpVariable, "true")
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

        // ── The hosts a user reaches — docs/plan/03 § Hosts ────────────────────────────────────────────
        //
        // Until 2026-09-15 this file stopped at the silos: docs/plan/24 § Phase 0's criterion is about the
        // cluster, and the gateway and identity host were started in-process by TenantOverHttpTests through
        // the same GatewayComposition and IdentityComposition their Program.cs files call. That made "the
        // whole platform" a thing a test could drive and nobody could open. These three are the same
        // composition roots, launched as the processes they are.
        //
        // ⚠ THREE FIXED PORTS, AND THE ISSUER IS WHY. The gateway and the feeds host validate a bearer
        // token against exactly the issuer they were configured with — JwksCallerContextResolver refuses a
        // discovery document whose `issuer` differs — so `http://localhost:5101` has to be one string on
        // three sides; CyberCloudResources.IdentityIssuer is that string, and the port it names is pinned
        // rather than allocated so that it can be. The gateway's port is pinned for the portal's proxy
        // file (below), and the feeds host's for `dotnet nuget push`, which wants a URL a person can type.
        //
        // ⚠ THE IDENTITY HOST IS HANDED THE ISSUER TOO, BECAUSE ITS REQUESTS ARRIVE ON TWO ORIGINS. Left
        // to infer it, OpenIddict stamps every token with the origin of the request that minted it — and
        // a person's /authorize is resumed through the identity app's dev server (4201), which proxies it
        // here with its own Host header. The first run of the dev-run story minted an authorization code
        // under `http://localhost:4201/` and /token on 5101 refused it with OpenIddict's ID2088, "the
        // issuer associated to the specified token is not valid", with every host healthy. One explicit
        // issuer is one key set, one discovery document and one `iss`, whichever origin asked.
        //
        // ⚠ ALL THREE ARE ORLEANS CLIENTS AND WAIT ON SILO 1 — see AsOrleansClient. None waits on k3s, for
        // the reason the silos do not.
        var identity = builder
            .AddProject<CyberCloud_Identity_Host>(CyberCloudResources.Identity)
            .AsOrleansClient()
            // ⚠ WebAuthn checks the ORIGIN of the page that made the credential against this list, and the
            // page is the identity app's dev server, not this host. IdentityHostOptions.Origins defaults to
            // https://localhost:5001, which nothing here listens on; a passkey ceremony from the dev server
            // would be refused with "origin not allowed" and nothing in that message names this line.
            // `localhost` is a secure context to every browser, so plain http is fine for the ceremony.
            .WithEnvironment("CyberCloud__Identity__Origins__0", $"http://localhost:{CyberCloudResources.IdentityAppPort.ToString(CultureInfo.InvariantCulture)}")
            // The same string the gateway and the feeds host validate against — see the issuer note above.
            .WithEnvironment("CyberCloud__Identity__Issuer", CyberCloudResources.IdentityIssuer)
            // The third of the three (see silo-one), plus the region a signed-up tenant is homed to and
            // its default resource group is placed in. `local` is the region this laptop is.
            .WithEnvironment(SelfServeSignUpVariable, "true")
            .WithEnvironment("CyberCloud__Identity__DefaultRegion", CyberCloudResources.DefaultRegion)
            // ⚠ THE KEYS PERSIST UNDER THIS DIRECTORY, AND ONLY BECAUSE THIS IS DEVELOPMENT. Signing and
            // encryption keys and the data-protection ring go to .identity/ beside .k3s/ and .seaweedfs/
            // (all three gitignored), so a restart of the identity host does not sign every portal tab
            // out — an ephemeral encryption key would refuse every refresh cookie, with an invalid_grant
            // that reads as a session bug. DevelopmentKeyFile refuses this setting in any other
            // environment and names the vault seam that replaces it.
            .WithEnvironment("CyberCloud__Identity__DevelopmentKeyDirectory", Path.Combine(builder.AppHostDirectory, ".identity"))
            // An unauthenticated /authorize sends the person to the identity app's dev server, whose proxy
            // forwards the resumed /authorize back here with the cookie. In production the pages are
            // built into this host and this stays empty.
            .WithEnvironment("CyberCloud__Identity__SignInPageBaseUri", $"http://localhost:{CyberCloudResources.IdentityAppPort.ToString(CultureInfo.InvariantCulture)}")
            // The portal's registration: where a code may be sent and where the browser lands after
            // sign-out. The CORS origin for /token is derived from the first — FirstPartyClients.
            .WithEnvironment("CyberCloud__Identity__Clients__Portal__RedirectUris__0", $"http://localhost:{CyberCloudResources.PortalPort.ToString(CultureInfo.InvariantCulture)}/auth/callback")
            .WithEnvironment("CyberCloud__Identity__Clients__Portal__PostLogoutRedirectUris__0", $"http://localhost:{CyberCloudResources.PortalPort.ToString(CultureInfo.InvariantCulture)}/")
            .WithHttpEndpoint(CyberCloudResources.IdentityPort, isProxied: false)
            .WithHttpHealthCheck("/health")
            .WaitFor(siloOne);

        var gateway = builder
            .AddProject<CyberCloud_Gateway_Host>(CyberCloudResources.Gateway)
            .AsOrleansClient()
            // The publisher's half of the resource-changed stream — see § Streams above for why the
            // gateway, an Orleans client, is the process that has to hold it.
            .WithReference(nats)
            // The resource graph's QUERY half (#54): the gateway answers
            // POST /tenants/{t}/providers/CyberCloud.ResourceGraph/resources from the same ClickHouse
            // the silos project into. Same section as the silos' — a gateway with the endpoint queries
            // and still runs no projector (GatewayComposition's remarks).
            .WithResourceGraph()
            .WithEnvironment("CyberCloud__Gateway__Identity__Issuer", CyberCloudResources.IdentityIssuer)
            // ⚠ PublicBaseUri is what the gateway tells OTHERS about itself — the agent tunnel address a
            // connected cluster's install command carries (#36), among other things. The shipped default
            // is https://api.cybercloud.io, which is a host nothing on this laptop can reach; a tenant's
            // agent installed from a local run would dial production. It is this gateway's own address.
            .WithEnvironment("CyberCloud__Gateway__PublicBaseUri", $"http://localhost:{CyberCloudResources.GatewayPort.ToString(CultureInfo.InvariantCulture)}")
            .WithHttpEndpoint(CyberCloudResources.GatewayPort, isProxied: false)
            .WithHttpHealthCheck("/health")
            .WaitFor(siloOne)
            .WaitFor(identity)
            // A quieter start, as for the silos: the first query against a ClickHouse that is not
            // listening yet is a 500 that reads like a misconfiguration.
            .WaitFor(clickHouse);

        builder
            .AddProject<CyberCloud_Registry_Feeds_Host>(CyberCloudResources.Feeds)
            .AsOrleansClient()
            .WithObjectStore()
            .WithEnvironment("CyberCloud__Feeds__Identity__Issuer", CyberCloudResources.IdentityIssuer)
            .WithEnvironment("CyberCloud__Feeds__PublicBaseUri", $"http://localhost:{CyberCloudResources.FeedsPort.ToString(CultureInfo.InvariantCulture)}")
            .WithHttpEndpoint(CyberCloudResources.FeedsPort, isProxied: false)
            .WithHttpHealthCheck("/health")
            .WaitFor(siloOne)
            .WaitFor(identity)
            .WaitForCompletion(objectStoreBucket);

        // ── The two Angular apps ───────────────────────────────────────────────────────────────────────
        //
        // `ng serve` for the portal and for the identity app, through pnpm, each with the proxy file that
        // stands in for the API shim docs/plan/03 § `portal/` gives CyberCloud.Portal.Host: the portal calls
        // the platform on its own origin at /api (API_BASE_PATH), and the identity app calls the sign-in
        // endpoints at /api/signin/…. The ports and the targets are pinned in CyberCloudResources and the
        // files are pinned to them by AppHostTopologyTests, because a proxy file that names a stale port
        // is a portal that loads and cannot call anything.
        //
        // ⚠ NODE 24. portal/.nvmrc pins the major and the Angular CLI refuses older ones outright; Aspire
        // runs whatever `node` is on PATH. On a machine whose PATH has an older Node the two resources fail
        // at start with the CLI's own message, and everything above them is unaffected — which is why they
        // come last, wait on the hosts they proxy to, and are the one thing this file can be asked to leave
        // out: `--CyberCloud:AppHost:Frontends=false`, which CyberCloud.AppHost.Tests passes.
        //
        // ⚠ install: false. The portal is a pnpm workspace with a frozen lockfile and a single-version
        // policy (portal/pnpm-workspace.yaml); an install Aspire ran on every start would be one that could
        // write the lockfile, and `pnpm install --frozen-lockfile` is a step a person runs once.
        if (!string.Equals(builder.Configuration[CyberCloudResources.FrontendsKey], "false", StringComparison.OrdinalIgnoreCase)) {
            var portalDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "portal"));

            builder
                .AddJavaScriptApp(CyberCloudResources.Portal, portalDirectory, "serve")
                .WithPnpm(install: false)
                .WithHttpEndpoint(CyberCloudResources.PortalPort, isProxied: false)
                .WaitFor(gateway);

            builder
                .AddJavaScriptApp(CyberCloudResources.IdentityApp, portalDirectory, "serve:identity")
                .WithPnpm(install: false)
                .WithHttpEndpoint(CyberCloudResources.IdentityAppPort, isProxied: false)
                .WaitFor(identity);
        }

        // ⚠ NOTHING WAITS ON k3s, ON PURPOSE. ADR-001 makes the Kubernetes API a data plane that is written
        // to and reconciled against, not a dependency of the control plane booting — so a silo that refused
        // to start without a cluster would be modelling the opposite of ADR-001. It also costs: k3s takes
        // about 20 s to serve `/readyz` and the two silos are up in a third of that.
        _ = k3s;
    }

    /// <summary>The one lock the identity file is checked and written under; see <see cref="EnsureObjectStoreIdentityFile" />.</summary>
    static readonly Lock ObjectStoreIdentityFileLock = new();

    /// <summary>
    ///     Leaves <paramref name="path" /> holding <paramref name="content" />, writing it only when
    ///     it is absent or holds something else — and treating a file that cannot be read, or cannot
    ///     be written over, as one that already holds it, for the reasons at the one call site.
    /// </summary>
    /// <param name="path">The identity file SeaweedFS mounts.</param>
    /// <param name="content">The constant the file always holds.</param>
    /// <remarks>
    ///     ⚠ The lock serialises the Compose calls of one process, which is the race
    ///     <c>CyberCloud.AppHost.Tests</c> run whole produces on a fresh checkout; it reaches no
    ///     second process and no container, so the write is caught as well. A refused write means
    ///     somebody holds the file open, and nobody holds a file that is not there — so after the
    ///     refusal the file exists, and the only writer of it anywhere writes this constant. A refusal
    ///     on a file that is <i>still</i> absent is a different story, and is rethrown.
    /// </remarks>
    static void EnsureObjectStoreIdentityFile(string path, string content) {
        lock (ObjectStoreIdentityFileLock) {
            if (File.Exists(path) && !HoldsOtherContent(path, content)) {
                return;
            }

            try {
                File.WriteAllText(path, content);
            } catch (IOException) when (File.Exists(path)) {
                // Written, or being written, by a Compose in another process — or mounted by a
                // SeaweedFS that started between the check and this line. It holds this content.
            }
        }
    }

    /// <summary>
    ///     Whether an existing file holds something other than <paramref name="expected" /> — and
    ///     therefore has to be rewritten. A file that cannot be opened is reported as holding the
    ///     expected content, for the reason at the one call site.
    /// </summary>
    /// <param name="path">The file, which exists.</param>
    /// <param name="expected">The constant this method's caller would write.</param>
    static bool HoldsOtherContent(string path, string expected) {
        try {
            return !string.Equals(File.ReadAllText(path), expected, StringComparison.Ordinal);
        } catch (IOException) {
            // Held by a SeaweedFS that is mounting it, on Docker Desktop for Windows. The only writer
            // of this file is the line that calls this method, and it writes a constant.
            return false;
        }
    }
}
