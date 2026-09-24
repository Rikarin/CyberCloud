using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.DBforPostgreSQL.Conformance;
using CyberCloud.Providers.DBforPostgreSQL.Contracts;
using CyberCloud.Providers.Network.Conformance;
using CyberCloud.Providers.Network.Contracts;
using CyberCloud.ResourceManager.Contracts;
using Shouldly;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     A fresh k3s with a PostgreSQL shard and a Redis beside it, a kubeconfig file pointing at the
///     k3s, and — once a test has installed what it needs onto the cluster — the real resource
///     manager over all three.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The same three containers <c>ClusterInfrastructure</c> starts for every provider
///             suite, started through the same method, and NOT the process-wide trio it keeps.
///         </b> That trio lives until the test run ends, which is right for a suite whose every class
///         shares one cluster and wrong here twice over: the story's first assertion is that the
///         cluster holds none of the three components it is about to install, so it must be one
///         nothing else in this assembly has touched; and this assembly's other fixtures each dispose
///         their k3s at the end of the class, so a second, undisposed one would sit beside every class
///         that ran after this — which is the two-clusters-at-once pressure <c>EmptyClusterFixture</c>
///         measured a 1-in-8 flake against. <see cref="ClusterInfrastructure.StartContainersAsync" />
///         exists for this fixture and hands the three back to be disposed.
///     </para>
///     <para>
///         ⚠ <b>The resource manager is started by the test, not by this fixture</b>, because the
///         order is the subject. <c>ClusterConformanceHarness</c> installs a CustomResourceDefinition
///         for every custom kind a case renders that the API server does not already serve — the
///         committed one under <c>charts/bundle/*/crds/</c> since issue #91 — and <c>install.sh</c>'s
///         helm install of cloudnative-pg would then find <c>clusters.postgresql.cnpg.io</c> already
///         present and refuse to adopt it. So the installer runs first, the harness starts second
///         and finds the operator's own definitions served, and the ones it does install are for
///         the two Kube-OVN kinds no bundle component on this lane can serve — see the test.
///     </para>
///     <para>
///         ⚠ <b>It neither throws nor skips when Docker does not answer</b>, which is the contract
///         every cluster-backed assembly here keeps and <c>EmptyClusterFixture</c>'s remarks argue
///         in full. It does not escalate the way that fixture does when the process has already run
///         one cluster — that counter is private to it — and it does not need to any more: the skip
///         it produces carries <c>NEEDS:</c> and the exception, and <c>build/Build.Test.cs</c>
///         § <c>ReportClusterBackedCases</c> turns such a skip into a red <c>Test</c> run whenever a
///         Docker endpoint is present, which covers both fixtures from outside the process.
///     </para>
/// </remarks>
public sealed class M1StoryClusterFixture : IAsyncLifetime {
    /// <summary>The base silo port. Clear of the lifecycle fixture's 22300 and the silo-kill pair's 22500 and 22600.</summary>
    public const int BaseSiloPort = 22700;

    /// <summary>
    ///     What the story needs on <c>PATH</c>: the installer's shell, its one tool, and the client the read-back goes
    ///     through.
    /// </summary>
    static readonly string[] Tools = ["bash", "helm", "kubectl"];

    ClusterContainers? containers;
    Exception? failure;
    ClusterConformanceHarness<PostgresCase>? harness;

    /// <summary>The kubeconfig file <c>install.sh</c> and <c>kubectl</c> are pointed at, or <see langword="null" />.</summary>
    public string? KubeconfigPath { get; private set; }

    /// <summary>The raw client, or <see langword="null" /> when the cluster did not come up.</summary>
    public IKubernetes? Client { get; private set; }

    /// <summary>Why the story did not run, in the form every cluster-backed suite here uses.</summary>
    /// <param name="wouldProve">What the story would have proved.</param>
    public string Skip(string wouldProve) =>
        "SKIPPED — docs/plan/24 § Phase 2's exit story: no fresh cluster to run it on, so nothing was "
        + "checked. "
        + $"{ClusterInfrastructure.PrerequisiteMarker} a Docker daemon able to run {ClusterInfrastructure.K3sImage}, "
        + $"{ClusterInfrastructure.PostgresImage} and {ClusterInfrastructure.RedisImage}, and `bash`, "
        + "`helm` and `kubectl` on PATH. "
        + $"WOULD PROVE: {wouldProve} "
        + "This suite is present by name and skipped rather than absent, because the story's steps "
        + "4–5 must not be readable as met on a machine that never ran them. "
        + "What went wrong: "
        + (failure is null ? "no exception was recorded." : failure.GetType().Name + ": " + failure.Message);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var missing = Tools.Where(static tool => !BundleInstaller.OnPath(tool)).ToList();

        if (missing.Count > 0) {
            failure = new InvalidOperationException(
                "install.sh is a bash script whose three selected rows are each one `helm upgrade "
                + "--install`, and the story reads the database back through `kubectl exec`; not on "
                + "PATH: "
                + string.Join(", ", missing)
                + "."
            );

            return;
        }

        var token = TestContext.Current.CancellationToken;

        try {
            containers = await ClusterInfrastructure.StartContainersAsync(token).ConfigureAwait(false);

            // ⚠ A FILE, for the reason EmptyClusterFixture gives: install.sh, helm and kubectl are
            // separate processes and read $KUBECONFIG. Deleted in DisposeAsync — it holds a working
            // client certificate for the container.
            var path = Path.Combine(
                Path.GetTempPath(),
                "cybercloud-m1-story-" + Guid.NewGuid().ToString("N") + ".kubeconfig"
            );
            await File.WriteAllTextAsync(path, containers.Endpoints.Kubeconfig, token).ConfigureAwait(false);
            KubeconfigPath = path;

            using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(containers.Endpoints.Kubeconfig));
            Client = new k8s.Kubernetes(
                await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(yaml).ConfigureAwait(false)
            );
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            failure = ex;
        }
    }

    /// <summary>
    ///     Starts the resource manager over the three containers — one silo, the PostgreSQL case
    ///     under test and the two network cases beside it.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ Called by the test AFTER <c>install.sh</c>, for the reason the class remarks give. The
    ///     harness is held here so that it is disposed with the containers whatever the test did.
    /// </remarks>
    public async Task<ClusterConformanceHarness<PostgresCase>> StartResourceManagerAsync(
        CancellationToken cancellationToken
    ) {
        if (containers is null) {
            throw new InvalidOperationException(
                "the containers did not come up, so there is nothing to start a silo over."
            );
        }

        harness = await ClusterConformanceHarness<PostgresCase>.StartAsync(
            1,
            "m1-story",
            BaseSiloPort,
            containers.Endpoints,
            cancellationToken,
            [VirtualNetworkCase.ProviderCase, NetworkSubnetCase.ProviderCase]
        )
            .ConfigureAwait(false);

        return harness;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (harness is not null) {
            await harness.DisposeAsync().ConfigureAwait(false);
        }

        Client?.Dispose();

        if (KubeconfigPath is not null) {
            File.Delete(KubeconfigPath);
        }

        if (containers is not null) {
            await containers.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
///     docs/plan/24 § Phase 2's exit story, steps 4 and 5 —
///     <i>
///         "create a VPC and a Postgres server in
///         it → get the connection string from Vault"
///     </i> — run end to end on a cluster this test
///     installed itself, through the real resource manager.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE FIRST TIME A MANAGED DATABASE HAS STARTED UNDER TEST IN THIS REPOSITORY, AND THE
///             REASON IT IS ONE TEST RATHER THAN FIVE.
///         </b> Every earlier cluster-backed suite starts a bare k3s with no operator on it —
///         <c>charts/managed/postgres/conformance.yaml</c> § owed says so in as many words, twice —
///         so <c>Converged</c> for a <c>CyberCloud.DBforPostgreSQL/servers</c> has only ever meant
///         "the Cluster object read back", never "PostgreSQL answers". Here the operator is
///         installed by <c>charts/bundle/install.sh</c> a minute before the resource manager writes
///         the server, so what converges is watched all the way to a running primary pod and a
///         <c>SELECT 1</c>. Cutting that into five tests would hand each of them a cluster in a
///         state a previous test produced, which is the input-set-differs-between-runs failure this
///         repository has shipped and <c>EmptyClusterFixture</c>'s remarks name; the story is one
///         sequence and is asserted as one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What "in it" means here, exactly, because the story says more than the platform
///             does.
///         </b> <c>PostgresServers.Schema2026</c> has no subnet or network property: nothing in
///         the catalogue places a database into a VPC, so the server is created in the same resource
///         group and lands in the same namespace, and that is the whole of the join. The VPC and its
///         subnet are Kube-OVN <c>Vpc</c> and <c>Subnet</c> objects, and
///         <b>
///             this cluster has no
///             Kube-OVN
///         </b>: docs/plan/23 § The lane that needs a kubelet keeps kube-ovn on the VM lane
///         (a CNI-less cluster, OVS kernel modules, a node label), so <c>ClusterConformanceHarness</c>
///         installs kube-ovn's committed definition for each kind (<c>charts/bundle/kube-ovn/crds/</c>)
///         and the two objects are admitted, labelled and readable — and route nothing. What the network half of this test
///         proves is
///         that the resource manager writes a parent and a child of another family in the same silo
///         as the database, through the same write path, against the same API server. What it cannot
///         prove is any packet.
///     </para>
///     <para>
///         ⚠ <b>"From Vault" is read as <c>listKeys</c>, and the reason is this type's own.</b>
///         <c>PostgresServers.ClusterJson</c>'s remarks record that this row declines the vault seam
///         on purpose: CloudNativePG generates the owner's password itself into
///         <c>{cluster}-app</c>, and the platform's route to it is <c>PostgresServerListKeysHandler</c>
///         reading that Secret through the cluster connection. So the credential's path out is asserted
///         as the tenant would take it — a <c>POST …/listKeys</c> through the resource manager — and
///         the answer is then used, from inside the cluster, to connect. ⚠ That needed a harness fix:
///         the cluster-backed harness's action dispatcher held a <c>NoClusterConnectionFactory</c>
///         until this test, so no <c>RequiresCluster</c> action had ever been dispatched against a
///         real API server.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Costs, measured on this 24-CPU host on 2026-09-17, warm image cache: 3 m 25 s for
///             the whole story.
///         </b> The three containers come up in about 20 s; <c>install.sh</c>
///         installs cert-manager, openebs-localpv and cloudnative-pg in one run; the silo starts and
///         the two network objects converge in seconds; the database converges as soon as the Cluster
///         reads back, and the primary pod is running about a minute later, of which the
///         <c>ghcr.io/cloudnative-pg/postgresql:17</c> pull is 28 s (254 MB). The first run took
///         eight minutes longer and proved nothing, because the primary never started — the finding
///         the class remarks open with. <see cref="BundleInstaller.BudgetFor" /> bounds the installer
///         run at 32 m — three helm timeouts and the margin — and <see cref="PrimaryBudget" /> bounds
///         the database.
///     </para>
/// </remarks>
/// <param name="cluster">The fresh cluster.</param>
public sealed class M1StoryOnAFreshCluster(M1StoryClusterFixture cluster) : IClassFixture<M1StoryClusterFixture> {
    /// <summary>The three components the story installs, in the roster's order.</summary>
    static readonly string[] Components = [
        BundleInstaller.CertManagerComponent,
        BundleInstaller.OpenEbsLocalPvComponent,
        BundleInstaller.CloudNativePgComponent
    ];

    /// <summary>The class <c>charts/bundle/openebs-localpv</c> installs, and the one the server is asked to use.</summary>
    const string StorageClass = "openebs-hostpath";

    const string NetworkName = "story-vpc";
    const string SubnetName = "app";
    const string ServerName = "story-db";

    /// <summary>How long one resource gets to converge through the manager.</summary>
    /// <remarks>
    ///     ⚠ Generous for the database on purpose. <c>PostgresServerReconciler</c> reports
    ///     <c>InProgress</c> until the Cluster reads back matching its spec, and against a real
    ///     operator the read-back carries whatever the webhook defaulted — the object is rewritten
    ///     between the apply and the read. Two minutes is many multiples of the measured seconds and
    ///     well under the primary budget, so a reconciler that never converges fails here with the
    ///     operation's own last progress line rather than being blamed on the operator.
    /// </remarks>
    static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(2);

    /// <summary>How long the primary pod gets to be running and ready after the server converged.</summary>
    /// <remarks>
    ///     ⚠ The same eight minutes <c>CloudNativePgOnAnEmptyCluster.ReadyBudget</c> gives the same
    ///     image pull, for the same reason: measured at about a minute warm, and a cold cache on a
    ///     slow link is the case the budget is for.
    /// </remarks>
    static readonly TimeSpan PrimaryBudget = TimeSpan.FromMinutes(8);

    /// <summary>
    ///     A VPC and a subnet are created, a PostgreSQL server is created beside them on a cluster
    ///     whose operator this test installed, its primary pod runs, <c>listKeys</c> answers with the
    ///     operator's credential, and that credential opens a connection.
    /// </summary>
    [Fact]
    public async Task AVpcAndAPostgresServerComeUpOnAFreshClusterAndListKeysOpensAConnection() {
        Assert.SkipWhen(
            cluster.Client is null || cluster.KubeconfigPath is null,
            cluster.Skip(
                "that one charts/bundle/install.sh run installs cert-manager, openebs-localpv and "
                + "cloudnative-pg onto a fresh k3s; that the real resource manager then creates a "
                + "CyberCloud.Network/virtualNetworks, a subnet under it and a "
                + "CyberCloud.DBforPostgreSQL/servers against that cluster; that CloudNativePG brings "
                + "the server's primary pod to Running; and that listKeys hands out a credential "
                + "psql accepts."
            )
        );

        var raw = cluster.Client!;
        var token = TestContext.Current.CancellationToken;

        // ── 1. The cluster is fresh, and each clause is load-bearing ────────────────────────────
        //
        // Without them every assertion below would hold over a cluster this test did not install.
        (await IsServedAsync(raw, PostgresServers.ClusterKind, token)).ShouldBeFalse(
            "postgresql.cnpg.io/v1 was already served before install.sh ran, so the server created "
            + "below would be admitted by a definition this test did not install."
        );

        (await IsServedAsync(
                raw,
                new() { Group = "cert-manager.io", Version = "v1", Kind = "Certificate", Plural = "certificates" },
                token
            ))
            .ShouldBeFalse("cert-manager.io/v1 was already served before install.sh ran.");

        (await raw.StorageV1.ListStorageClassAsync(cancellationToken: token)).Items
            .ShouldNotContain(
                x => x.Metadata.Name == StorageClass,
                $"{StorageClass} already existed before install.sh ran."
            );

        // ── 2. One installer run, three components, three phases ───────────────────────────────
        var install = await BundleInstaller.RunAsync(
            string.Join(' ', Components.Select(static x => "--component " + x)),
            cluster.KubeconfigPath,
            token,
            BundleInstaller.BudgetFor(Components)
        );

        install.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh installing cert-manager, openebs-localpv and cloudnative-pg onto "
            + "one fresh k3s failed. Its output was:\n"
            + install.Output
        );

        // ⚠ The roster's order, not the command line's — the same assertion CloudNativePgOnAnEmptyCluster
        // makes for two rows, made for three.
        var positions = Components.Select(x => install.Output.IndexOf("\n  " + x + "\n", StringComparison.Ordinal))
            .ToList();

        positions.ShouldAllBe(
            x => x >= 0,
            "install.sh did not print every selected component. Its output was:\n" + install.Output
        );
        positions.ShouldBe(
            [.. positions.OrderBy(static x => x)],
            "install.sh installed the three components in an order other than bundle.yaml's phases 15, 25, 50. Its output was:\n"
            + install.Output
        );

        (await IsServedAsync(raw, PostgresServers.ClusterKind, token)).ShouldBeTrue(
            "postgresql.cnpg.io/v1 is not served after install.sh succeeded. Installer output:\n" + install.Output
        );

        (await raw.StorageV1.ReadStorageClassAsync(StorageClass, cancellationToken: token)).Provisioner
            .ShouldBe(
                "openebs.io/local",
                "the storage class install.sh installed is not on openebs-localpv's provisioner."
            );

        // ── 3. The resource manager, over the cluster the installer just filled ─────────────────
        var rm = await cluster.StartResourceManagerAsync(token);
        var ns = ClusterConformanceHarness<PostgresCase>.Namespace;
        var clusterId = ClusterConformanceHarness<PostgresCase>.ClusterId;

        // ── 4. A VPC, then a subnet in it ───────────────────────────────────────────────────────
        var network = Address(VirtualNetworks.Type, NetworkName);
        await CreateAsync(rm, network, VirtualNetworks.V2026, VirtualNetworks.Body(clusterId), token);

        (await ReadClusterScopedAsync(raw, VirtualNetworks.VpcRef(ns, NetworkName), token)).ShouldNotBeNull(
            $"the virtual network converged and no Kube-OVN Vpc named '{VirtualNetworks.VpcRef(ns, NetworkName).Name}' "
            + "is on the API server. ⚠ It is admitted against kube-ovn's committed definition and no "
            + "operator, because this lane has no Kube-OVN — docs/plan/23 § The lane that needs a kubelet."
        );

        var subnet = Address(NetworkSubnets.Type, SubnetName, NetworkName);
        await CreateAsync(rm, subnet, NetworkSubnets.V2026, NetworkSubnets.Body(clusterId), token);

        (await ReadClusterScopedAsync(raw, NetworkSubnets.SubnetRef(ns, subnet), token)).ShouldNotBeNull(
            $"the subnet converged and no Kube-OVN Subnet named '{NetworkSubnets.SubnetRef(ns, subnet).Name}' is on the API server."
        );

        // ── 5. A PostgreSQL server beside them, on the operator and the storage the run installed ─
        var server = Address(PostgresServers.Type, ServerName);
        var body = ServerBody(clusterId);
        await CreateAsync(rm, server, PostgresServers.V2026, body, token);

        // ⚠ The operator's half, watched rather than assumed: Converged means the Cluster read back,
        // and this is the primary pod it produces. Both labels are CloudNativePG's own.
        var primary = await PollAsync(
            PrimaryBudget,
            async () => {
                var pods = await raw.CoreV1.ListNamespacedPodAsync(
                    ns,
                    labelSelector: $"cnpg.io/cluster={ServerName},cnpg.io/instanceRole=primary",
                    cancellationToken: token
                );

                return pods.Items.FirstOrDefault(static pod =>
                    pod.Status?.Phase == "Running"
                    && pod.Status.ContainerStatuses?.All(static container => container.Ready) == true
                );
            },
            token
        );

        primary.ShouldNotBeNull(
            $"no Running, Ready primary pod for '{ServerName}' appeared in `{ns}` within "
            + $"{PrimaryBudget.TotalMinutes:F0} minutes of the server converging. The Cluster object is on "
            + "the API server, so this is CloudNativePG's bootstrap. What the namespace held when the "
            + "budget ran out:\n"
            + await DescribeAsync(raw, ns, token)
        );

        // ── 6. The credential, the way a tenant fetches it ──────────────────────────────────────
        var keys = await rm.Manager.ActionAsync(
            new() {
                Path = server.Path,
                ApiVersion = PostgresServers.V2026,
                Verb = WriteVerb.Post,
                Action = PostgresServers.ListKeysAction,
                Caller = ClusterConformanceHarness<PostgresCase>.Caller()
            },
            token
        );

        keys.IsSuccess.ShouldBeTrue(
            $"listKeys on '{server.Path}' failed after the primary pod was running: {keys.Error?.Code} "
            + $"{keys.Error?.Message}. The handler reads the Secret '{PostgresServers.CredentialSecretName(ServerName)}' "
            + "CloudNativePG writes while bringing the cluster up; a ResourceNotFound here with a running "
            + "primary means the operator did not create it — read the rendered Cluster's "
            + "bootstrap.initdb.secret against CloudNativePG's ShouldInitDBCreateApplicationSecret."
        );

        using var response = JsonDocument.Parse(keys.GetValueOrThrow().ActionResponse);
        var credential = response.RootElement;

        var host = credential.GetProperty("host").GetString();
        var database = credential.GetProperty("database").GetString();
        var username = credential.GetProperty("username").GetString();
        var password = credential.GetProperty("password").GetString();

        host.ShouldBe(
            ServerName + "-rw",
            "pooling is off in the story's body, so the advertised host is the read-write Service."
        );
        database.ShouldBe("app");
        username.ShouldBe(
            "app",
            "the username is read from the operator's Secret, and the body asked for the owner `app`."
        );
        password.ShouldNotBeNullOrWhiteSpace("listKeys answered with an empty password, which authenticates nothing.");

        // ── 7. And the credential opens a connection, from inside the cluster ───────────────────
        //
        // charts/managed/postgres/conformance.yaml § assertions, `connect-in-cluster`: "A client
        // connects to the read-write Service with the credentials from listKeys and runs SELECT 1".
        // The client is psql in the primary's own image, reached through kubectl exec, connecting to
        // the -rw Service by name rather than to localhost — so the Service, the DNS name and the
        // password are all on the path. ⚠ The password travels as a process argument inside the pod,
        // which is fine for a throwaway k3s in a test and would not be for a tenant's; the platform's
        // own path out is the listKeys reply and nothing else.
        var (exitCode, output) = await BundleInstaller.CaptureAsync(
            "kubectl",
            [
                "--namespace", ns, "exec", primary!.Metadata.Name, "--container", "postgres", "--", "psql",
                $"host={host} port={PostgresServers.Port} dbname={database} user={username} password={password} sslmode=require",
                "--tuples-only", "--no-align", "--command", "SELECT 1"
            ],
            null,
            cluster.KubeconfigPath,
            token
        );

        exitCode.ShouldBe(
            0,
            $"psql inside '{primary.Metadata.Name}' could not connect to {host}:{PostgresServers.Port} as "
            + $"{username} with the password listKeys handed out. psql said:\n{output}"
        );

        // ⚠ A line equal to "1", not the whole output — kubectl writes remarks of its own to stderr
        // ("Defaulted container …" cost the first green run of this story), and this harness
        // interleaves the two streams on purpose: BundleInstaller.CaptureAsync.
        output.Split('\n')
            .Select(static x => x.Trim())
            .ShouldContain("1", "SELECT 1 through the listKeys credential did not answer 1. psql said:\n" + output);
    }

    /// <summary>The story's server: one instance, no pooler, no backup, no monitoring, on the bundle's storage class.</summary>
    /// <remarks>
    ///     ⚠ Each override narrows what a green run says and is named for the reason
    ///     <c>CloudNativePgOnAnEmptyCluster.ChartValues</c> gives for the same seven values on the
    ///     chart: a second instance is a second pull proving nothing this one does not; a Pooler is a
    ///     PgBouncer pull and turns <c>host</c> into the pooler's name, which
    ///     <c>pooler-is-the-default-endpoint</c> owns rather than this story; barman needs an object
    ///     store this cluster has none of; <c>enablePodMonitor</c> wants a definition from phase 20,
    ///     which is not installed here; the storage class is THE POINT — a claim naming neither class
    ///     would bind through k3s's own provisioner on a cluster with two defaults; and
    ///     <c>s1.nano</c> is schedulable on a single node inside a container.
    /// </remarks>
    static string ServerBody(Guid clusterId) {
        var body = JsonNode.Parse(PostgresServers.Body(clusterId, 1, "256Mi", false))!
            .AsObject();
        var properties = body["properties"]!.AsObject();

        properties["sizing"] = new JsonObject { ["preset"] = "s1.nano" };
        properties["storage"]!.AsObject()["class"] = StorageClass;
        properties["backup"] = new JsonObject { ["enabled"] = false };
        properties["monitoring"] = new JsonObject { ["enabled"] = false };

        return body.ToJsonString();
    }

    /// <summary>An address in the story's tenant, subscription and resource group.</summary>
    static ResourceId Address(ResourceTypeName type, string name, string parentNames = "") =>
        new(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            type,
            name,
            Guid.Empty,
            parentNames
        );

    /// <summary>Writes a resource through the manager and drives its operation to <c>Succeeded</c>.</summary>
    static async Task CreateAsync(
        ClusterConformanceHarness<PostgresCase> rm,
        ResourceId address,
        string apiVersion,
        string body,
        CancellationToken token
    ) {
        var accepted = await rm.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = apiVersion,
                Verb = WriteVerb.Put,
                Body = body,
                Caller = ClusterConformanceHarness<PostgresCase>.Caller()
            },
            token
        );

        accepted.IsSuccess.ShouldBeTrue(
            $"PUT {address.Path} was refused: {accepted.Error?.Code} {accepted.Error?.Message}"
        );

        var operation = rm.Operation(ConformanceIds.Tenant, accepted.GetValueOrThrow().OperationId);
        var deadline = DateTimeOffset.UtcNow + ConvergeBudget;
        OperationStatus? last = null;

        // ⚠ Driven the way its reminder would drive it, one second apart — ClusterConformanceTests
        // § BetweenDrives has the reason the delay exists at all.
        while (DateTimeOffset.UtcNow < deadline) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        last.ShouldNotBeNull();
        last.State.ShouldBe(
            OperationState.Succeeded,
            $"the create of '{address.Path}' ended {last.State} after {ConvergeBudget.TotalMinutes:F0} minutes: "
            + $"{last.Error?.Code} {last.Error?.Message}. Last progress: "
            + string.Join(" | ", last.Progress.TakeLast(3).Select(static x => x.Step + ": " + x.Detail))
        );
    }

    /// <summary>Whether the API server answers a list for this kind.</summary>
    static async Task<bool> IsServedAsync(IKubernetes raw, GroupVersionKind kind, CancellationToken token) {
        try {
            await raw.CustomObjects.ListClusterCustomObjectAsync(
                kind.Group,
                kind.Version,
                kind.Plural,
                limit: 1,
                cancellationToken: token
            );
            return true;
        } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
            return false;
        }
    }

    /// <summary>Reads a cluster-scoped custom object, or <see langword="null" /> when absent.</summary>
    static async Task<string?> ReadClusterScopedAsync(IKubernetes raw, ObjectRef target, CancellationToken token) {
        try {
            using var response = await raw.CustomObjects.GetClusterCustomObjectWithHttpMessagesAsync(
                target.Kind.Group,
                target.Kind.Version,
                target.Kind.Plural,
                target.Name,
                cancellationToken: token
            );

            return ((JsonElement)response.Body!).GetRawText();
        } catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound) {
            return null;
        }
    }

    /// <summary>
    ///     What a namespace holds — the CloudNativePG Cluster's status, every pod with its container
    ///     states, every claim, and the newest events — for a failure message a reader can act on
    ///     after the cluster is gone.
    /// </summary>
    /// <remarks>
    ///     ⚠ The fixture disposes the k3s when the class ends, so "describe the Cluster and read the
    ///     pod's logs" is advice nobody can follow after a failure. The first run of this test failed
    ///     on the primary budget with exactly that advice in its message and nothing else, which cost
    ///     a second ten-minute run to learn what the operator had said. Everything below is read with
    ///     the raw client and is best-effort: a read that fails is reported as the exception and the
    ///     rest is still collected.
    /// </remarks>
    static async Task<string> DescribeAsync(IKubernetes raw, string ns, CancellationToken token) {
        var report = new StringBuilder();

        try {
            var clusters = await raw.CustomObjects.ListNamespacedCustomObjectAsync(
                PostgresServers.ClusterKind.Group,
                PostgresServers.ClusterKind.Version,
                ns,
                PostgresServers.ClusterKind.Plural,
                cancellationToken: token
            );

            foreach (var item in JsonSerializer.SerializeToElement(clusters).GetProperty("items").EnumerateArray()) {
                report.Append("Cluster ")
                    .Append(item.GetProperty("metadata").GetProperty("name").GetString())
                    .Append(": status=");
                report.Append(item.TryGetProperty("status", out var status) ? status.GetRawText() : "(none)")
                    .Append('\n');
                report.Append("  spec=").Append(item.GetProperty("spec").GetRawText()).Append('\n');
            }
        } catch (Exception ex) when (ex is HttpOperationException
                                         or HttpRequestException
                                         or JsonException
                                         or KeyNotFoundException) {
            report.Append("Clusters could not be read: ").Append(ex.Message).Append('\n');
        }

        try {
            foreach (var pod in (await raw.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: token)).Items) {
                report.Append("Pod ").Append(pod.Metadata.Name).Append(": phase=").Append(pod.Status?.Phase);

                foreach (var container in pod.Status?.ContainerStatuses ?? []) {
                    report.Append("; ").Append(container.Name).Append(" ready=").Append(container.Ready);
                    report.Append(" state=")
                        .Append(
                            container.State?.Waiting is { } waiting ? "waiting("
                            + waiting.Reason
                            + ": "
                            + waiting.Message
                            + ")"
                            : container.State?.Terminated is { } terminated ? "terminated("
                            + terminated.Reason
                            + " exit "
                            + terminated.ExitCode
                            + ")"
                            : container.State?.Running is not null ? "running" : "unknown"
                        );
                }

                foreach (var container in pod.Status?.InitContainerStatuses ?? []) {
                    report.Append("; init ")
                        .Append(container.Name)
                        .Append(" state=")
                        .Append(
                            container.State?.Waiting is { } waiting ? "waiting("
                            + waiting.Reason
                            + ": "
                            + waiting.Message
                            + ")"
                            : container.State?.Terminated is { } terminated ? "terminated("
                            + terminated.Reason
                            + " exit "
                            + terminated.ExitCode
                            + ")"
                            : container.State?.Running is not null ? "running" : "unknown"
                        );
                }

                report.Append('\n');
            }
        } catch (Exception ex) when (ex is HttpOperationException or HttpRequestException) {
            report.Append("Pods could not be read: ").Append(ex.Message).Append('\n');
        }

        try {
            foreach (var claim in (await raw.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
                             ns,
                             cancellationToken: token
                         )).Items) {
                report.Append("PVC ")
                    .Append(claim.Metadata.Name)
                    .Append(": phase=")
                    .Append(claim.Status?.Phase)
                    .Append(" class=")
                    .Append(claim.Spec?.StorageClassName)
                    .Append('\n');
            }
        } catch (Exception ex) when (ex is HttpOperationException or HttpRequestException) {
            report.Append("Claims could not be read: ").Append(ex.Message).Append('\n');
        }

        try {
            var events = (await raw.CoreV1.ListNamespacedEventAsync(ns, cancellationToken: token)).Items
                .OrderByDescending(static x => x.LastTimestamp ?? x.EventTime ?? x.Metadata.CreationTimestamp)
                .Take(25);

            foreach (var e in events) {
                report.Append("Event ")
                    .Append(e.Type)
                    .Append(' ')
                    .Append(e.Reason)
                    .Append(" on ")
                    .Append(e.InvolvedObject?.Kind)
                    .Append('/')
                    .Append(e.InvolvedObject?.Name)
                    .Append(": ")
                    .Append(e.Message)
                    .Append('\n');
            }
        } catch (Exception ex) when (ex is HttpOperationException or HttpRequestException) {
            report.Append("Events could not be read: ").Append(ex.Message).Append('\n');
        }

        return report.ToString();
    }

    /// <summary>Polls until <paramref name="read" /> returns non-null, or the budget runs out.</summary>
    static async Task<T?> PollAsync<T>(TimeSpan budget, Func<Task<T?>> read, CancellationToken token)
        where T : class {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline) {
            var value = await read().ConfigureAwait(false);

            if (value is not null) {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }

        return null;
    }
}
