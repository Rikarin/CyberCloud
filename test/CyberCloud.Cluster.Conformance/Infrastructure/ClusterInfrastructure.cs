using CyberCloud.ServiceDefaults.Storage;
using System.Text;
using Testcontainers.K3s;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace CyberCloud.Cluster.Conformance.Infrastructure;

/// <summary>
///     The three containers every test in this assembly shares: a real API server, a real durable
///     tier and a real reminder table.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Started once per test process, and <see cref="TryStartAsync" /> neither throws nor
///             skips.
///         </b> A start that threw would fail the class, which reads as "the provider is
///         broken"; a start that skipped would take the whole class out of the runner's output under
///         one message. Absence of Docker is a <i>reportable outcome</i> — it is the one outcome that
///         matters more than any assertion here, because a suite whose tests silently vanish without
///         a daemon recreates exactly the failure the loudly-skipped originals existed to prevent. So
///         the start is attempted once, the failure is remembered, and every test turns it into its
///         own <c>Assert.Skip</c> built from <see cref="SkipMessage" />, naming the provider, what is
///         missing, and what that particular test would have proved.
///     </para>
///     <para>
///         ⚠ <b>One test in this assembly never touches Docker, on purpose.</b>
///         <c>TheCaseOwnsClusterObjectsOrThisWholeSuiteWouldBeVacuous</c> reads the case and nothing
///         else, so it runs on a machine with no daemon. That is what keeps
///         <c>--minimum-expected-tests 1</c> satisfiable: Microsoft.Testing.Platform reports "Zero
///         tests ran" — and fails — for a run whose every test skipped, so a suite that skipped
///         wholesale would turn a missing daemon into a red build rather than into a visible skip.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The images are pinned, and the k3s one is pinned to
///             <see cref="K3sImage" /> for the reason
///             <c>CyberCloud.Kubernetes.Tests.Infrastructure.K3sFixture</c> gives at length:
///         </b>
///         <c>Testcontainers.K3s</c> 4.13.0 still defaults to Kubernetes 1.26, and the fabric is
///         written against <c>KubernetesClient</c> 19.0.2, whose models are generated from 1.35.
///         Testing an apply path against a nine-release-old server would leave the gap between what
///         the tests prove and what the client expects unmeasured. The constant is taken from that
///         fixture rather than re-derived, so the two suites cannot drift onto different servers.
///     </para>
///     <para>
///         ⚠ <b><see cref="ClusterSlot" /> is held for the life of the process.</b>
///         <c>./build.sh Test</c> runs suites with
///         <c>MaxDegreeOfParallelism = Environment.ProcessorCount</c>, and k3s-backed suites in this
///         repository have already produced failures under load that do not reproduce in isolation.
///         This assembly and every provider's <c>.Cluster.Conformance</c> assembly take one
///         cross-process lock, so however many of them a run contains, at most one is holding a k3s
///         container at a time. It does <b>not</b> serialise against
///         <c>CyberCloud.Kubernetes.Tests</c>, which would need one line in that project.
///     </para>
///     <para>
///         ⚠ <b>That last sentence stood unread for long enough to cost a run — #77.</b> Two suites
///         hold a k3s and take no <see cref="ClusterSlot" />: <c>CyberCloud.Kubernetes.Tests</c>,
///         named above, and <c>CyberCloud.AppHost.Tests</c>, which starts one through Aspire and
///         takes a machine-wide lock of its own under a different file name. Three unrelated permits
///         over seventeen cluster-backed suites, so three API servers could be live at once. The cap
///         that now covers all seventeen is <c>build/Build.Test.cs</c>
///         § <c>ClusterBackedSuiteDegree</c>, which is <b>1</b> precisely because that is the number
///         this permit already enforces — build/ was taught the invariant rather than the two suites
///         being taught the permit, because a lock taken <i>inside</i> a test process cannot stop the
///         build from starting the process, so the fifteen used to spend the container budget on
///         waiting rather than on working. ⚠ This lock stays regardless: it is what serialises a
///         `dotnet run` of one suite against a second checkout's, which no semaphore in one build
///         process can see.
///     </para>
/// </remarks>
public static class ClusterInfrastructure {
    /// <summary>
    ///     The k3s image, taken from <c>CyberCloud.Kubernetes.Tests</c>'s pin rather than re-chosen.
    /// </summary>
    public const string K3sImage = "rancher/k3s:v1.35.7-k3s1";

    /// <summary>
    ///     The word a skip carries when the lane did not run because a daemon or a tool was
    ///     missing — the one word <c>build/Build.Test.cs</c> § <c>PrerequisiteSkips</c> reads.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One spelling on this side of the boundary, on purpose.</b> The first version of
    ///     the convention had this word typed into <see cref="SkipMessage" /> and typed again into
    ///     the bundle suite's two fixtures, and <c>SkipConventionTests</c> pinned only the first
    ///     against the build — the review of that commit found the second, the suite the guard was
    ///     written for, free to drift out of it. Every skip in the test tree that names a missing
    ///     daemon or tool now interpolates this constant, and the one test pins the constant.
    /// </remarks>
    public const string PrerequisiteMarker = "NEEDS:";

    /// <summary>
    ///     Where the kubelet reads drop-in configuration inside the k3s container, and the one drop-in
    ///     this harness puts there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A KUBELET AT 1.35 REFUSES TO START ON A CGROUP v1 HOST, AND A DOCKER DESKTOP ON
    ///             WINDOWS CAN BE ONE.
    ///         </b> KEP-4569 moved cgroup v1 into maintenance, and from 1.35 the
    ///         kubelet's <c>failCgroupV1</c> defaults to <c>true</c>: the container comes up, the
    ///         kubelet logs <i>"kubelet is configured to not run on a host using cgroup v1"</i>, and
    ///         k3s shuts down. Testcontainers then reports <c>ContainerNotRunningException</c>, every
    ///         test in every cluster-backed suite skips, and the skips read as if Docker were absent.
    ///         That is how the file-share lifecycle and silo-kill suites went unexecuted on the machine
    ///         that wrote them — #30's review found 21 skips and 3 passes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The flag is gone; only the config field is left.</b> <c>--fail-cgroup-v1=false</c>
    ///         as a <c>--kubelet-arg</c> is refused at 1.35 (<i>"unknown flag"</i>, measured). What
    ///         works is a <c>KubeletConfiguration</c> drop-in in the directory k3s already passes as
    ///         <c>--config-dir</c>, and the file must end in <c>.conf</c> — the kubelet ignores any
    ///         other suffix without a word, which cost one probe. On a cgroup v2 host the field is
    ///         simply true-by-default-and-irrelevant, so the drop-in is unconditional.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The machine that wrote the paragraph above is cgroup v2 now, and the drop-in
    ///             stays.
    ///         </b> WSL2's kernel was booting cgroup v1 (hybrid) and Docker Desktop inherited
    ///         it; <c>kernelCommandLine = cgroup_no_v1=all</c> in <c>.wslconfig</c>, a
    ///         <c>wsl --shutdown</c> and a Docker restart put <c>docker info</c> at
    ///         <c>Cgroup Version: 2</c> on 2026-09-15, and the kubelet starts with the field left at
    ///         its default — docs/plan/23 § The lane that needs a kubelet. The drop-in is kept for
    ///         the next machine in the state this one was in, where it is the difference between a
    ///         suite that runs and 21 skips that read as a missing daemon; it costs nothing here.
    ///     </para>
    /// </remarks>
    public const string KubeletDropInPath =
        "/var/lib/rancher/k3s/agent/etc/kubelet.conf.d/99-cybercloud-cgroup-v1.conf";

    /// <summary>The drop-in's content. See <see cref="KubeletDropInPath" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             And the disk thresholds, because the node's disk is the Docker Desktop VM's and every
    ///             image and volume on the machine is on it.
    ///         </b> Measured on 2026-09-24 by #30's CloudNativePG lane: the VM's
    ///         251 GB disk at 96% (11 GB free) put the k3s node at <c>DiskPressure</c> within seconds of
    ///         starting — the kubelet's default <c>nodefs.available&lt;10%</c> and
    ///         <c>imagefs.available&lt;15%</c> are 25 and 38 GB of headroom on that disk — so it evicted
    ///         openebs's provisioner eleven times and tainted the node against every pod after. A test
    ///         cluster that lives for twenty minutes needs a few gigabytes, not a tenth of the disk, so
    ///         the thresholds are 1% here and the image collector's are raised so it does not delete
    ///         the PostgreSQL image between the server that pulled it and the restore that needs it.
    ///         ⚠ <c>evictionHard</c> replaces the kubelet's whole default map, so the memory and inode
    ///         signals are restated at their defaults rather than dropped.
    ///     </para>
    /// </remarks>
    public const string KubeletDropIn =
        "apiVersion: kubelet.config.k8s.io/v1beta1\nkind: KubeletConfiguration\nfailCgroupV1: false\n"
        + "evictionHard:\n  memory.available: \"100Mi\"\n  nodefs.available: \"1%\"\n  nodefs.inodesFree: \"5%\"\n"
        + "  imagefs.available: \"1%\"\nimageGCHighThresholdPercent: 99\nimageGCLowThresholdPercent: 98\n";

    /// <summary>
    ///     The entrypoint every k3s-in-Docker here starts through: make <c>/var/run</c> a shared
    ///     mount, then <c>exec</c> k3s with whatever command the container was given.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Without this, KubeVirt is the one bundle component the local topology can never
    ///             run.
    ///         </b> <c>Testcontainers.K3s</c> and the AppHost both start k3s with
    ///         <c>--tmpfs /var/run</c>, which Docker mounts with private propagation, and
    ///         <c>virt-handler</c> refuses to start on such a node:
    ///         <i>
    ///             path "/var/run/kubevirt" is
    ///             mounted on "/var/run" but it is not a shared mount
    ///         </i> (issue #2, 2026-09-15). A real
    ///         node has no such problem. <c>mount --make-rshared /var/run</c> inside the container
    ///         before k3s starts is the whole fix; measured the same day on a throwaway container:
    ///         <c>/proc/self/mountinfo</c> shows <c>/var/run … shared:259</c>, <c>/run</c> stays
    ///         private, and k3s logs <i>k3s is up and running</i> five seconds in.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             An entrypoint wrapper and not an exec after start, so that the three recipes are
    ///             one recipe.
    ///         </b> Testcontainers could run the mount through <c>ExecAsync</c> in a
    ///         startup callback; Aspire has no post-start exec at all — <c>WithContainerRuntimeArgs</c>
    ///         reaches <c>docker run</c> and nothing reaches <c>docker exec</c>. A wrapper is the
    ///         same four strings in both, and in a bare <c>docker run</c>: <c>/bin/sh -c '…' k3s</c>
    ///         followed by the command. The image ships <c>/bin/sh</c> and <c>/bin/aux/mount</c>;
    ///         <c>"$@"</c> is expanded by that shell from the arguments after <c>k3s</c> (its
    ///         <c>$0</c>), so the module's own <c>server --disable=traefik …</c> command is
    ///         untouched, and no shell on the host ever sees the string.
    ///     </para>
    /// </remarks>
    public const string SharedVarRunScript = "mount --make-rshared /var/run && exec /bin/k3s \"$@\"";

    /// <summary>
    ///     The k3s packaged component this recipe switches off, beside the <c>--disable=traefik</c>
    ///     <c>Testcontainers.K3s</c> already passes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             metrics-server is the one aggregated API a stock k3s registers, and it made the
    ///             namespace listing a race (#96).
    ///         </b> k3s ships it as an <c>APIService</c> for
    ///         <c>metrics.k8s.io/v1beta1</c> whose backend is a pod, and a pod needs an image pull
    ///         and a kubelet. Until it answers, discovery of that group returns 503 and
    ///         <c>NamespaceContents</c> refuses the whole enumeration — correctly, see
    ///         <c>KubeApiClient.DiscoverNamespacedKindsAsync</c>. So which arm
    ///         <c>ClusterConformanceTests.ARealNamespaceHoldsWhatKubernetesPutsThereAndTheReclaimSeesIt</c>
    ///         took depended on how old the cluster was when the test reached it: the refusing arm
    ///         on a young k3s, the listing arm four minutes into a full <c>./build.sh Test</c>. The
    ///         Network family's cluster suite went red and green on the same tree for exactly that
    ///         reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off, and not waited for, because nothing here reads a metric.</b> The AppHost's
    ///         k3s has passed <c>--disable=metrics-server</c> since it was written ("nothing in Cyber
    ///         Cloud uses either"); this is the test recipe agreeing with it. Waiting instead would
    ///         cost every suite an image pull inside a fresh container. The refusal it used to
    ///         provoke by accident is now provoked on purpose, with an <c>APIService</c> that names a
    ///         service nobody runs, in <c>CyberCloud.Kubernetes.Tests</c>
    ///         § <c>NamespaceDiscoveryRefusalTests</c>.
    ///     </para>
    /// </remarks>
    public const string DisableMetricsServer = "--disable=metrics-server";

    /// <summary>
    ///     A k3s builder on <see cref="K3sImage" /> that comes up on a cgroup v1 host as well as a
    ///     v2 one, with <c>/var/run</c> shared so KubeVirt's handler can run on it and no
    ///     metrics-server (<see cref="DisableMetricsServer" />). Every k3s under <c>test/</c> goes
    ///     through here; <c>CyberCloud.Kubernetes.Tests.Infrastructure.K3sFixture</c> cannot
    ///     reference this assembly and carries the same lines beside its own copy of the pin.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>WithCommand</c> <b>appends</b> to the module's own <c>server --disable=traefik</c>
    ///     rather than replacing it, which is why the flag is passed alone.
    /// </remarks>
    public static K3sBuilder K3s() =>
        new K3sBuilder(K3sImage)
            .WithEntrypoint("/bin/sh", "-c", SharedVarRunScript, "k3s")
            .WithCommand(DisableMetricsServer)
            .WithResourceMapping(Encoding.UTF8.GetBytes(KubeletDropIn), KubeletDropInPath);

    /// <summary>The PostgreSQL image, matching <c>CyberCloud.ServiceDefaults.Tests</c>'s durable shards.</summary>
    public const string PostgresImage = "postgres:17-alpine";

    /// <summary>The Redis image, matching <c>CyberCloud.ServiceDefaults.Tests</c>'s hot tier.</summary>
    public const string RedisImage = "redis:8-alpine";

    static readonly SemaphoreSlim Gate = new(1, 1);

    static ClusterEndpoints? started;
    static ClusterContainers? running;
    static Exception? failure;
    static bool attempted;

    /// <summary>
    ///     The containers, starting them on first use, or <see langword="null" /> when Docker did
    ///     not answer.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ Does not throw and does not skip. Absence of Docker is a <i>reportable outcome</i>, and
    ///     the report is <see cref="SkipMessage" /> — attached to each test individually so the
    ///     runner's output names the provider and the assertion that was not made.
    /// </remarks>
    public static async Task<ClusterEndpoints?> TryStartAsync(CancellationToken cancellationToken) {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            if (!attempted) {
                attempted = true;

                try {
                    started = await StartAsync(cancellationToken).ConfigureAwait(false);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    failure = ex;
                }
            }
        } finally {
            Gate.Release();
        }

        return started;
    }

    /// <summary>Why a test did not run, in the form the loudly-skipped originals used.</summary>
    /// <param name="provider">The provider under test.</param>
    /// <param name="wouldProve">What the calling test would have proved.</param>
    /// <param name="reason">What went wrong, when it was not Docker's absence.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="PrerequisiteMarker" /> is read by the build.</b> <c>build/Build.Test.cs</c>
    ///     § <c>PrerequisiteSkips</c> reads every cluster-backed suite's report after a <c>Test</c>
    ///     run and treats a skip carrying that word as "the lane did not run because something was
    ///     missing", which beside a Docker endpoint fails the run. Every skip in this tree that names
    ///     a missing daemon or tool carries it — <c>EmptyClusterFixture.Skip</c>,
    ///     <c>M1StoryClusterFixture.Skip</c> and <c>BundleInstaller.SkipWithoutBash</c> in the bundle
    ///     suite included, and every one of them through the constant rather than by spelling — and
    ///     the one skip a working lane makes honestly ("created no PersistentVolumeClaim on a real
    ///     cluster") does not, because nothing is missing. Renaming the constant without renaming
    ///     <c>Build.Test.cs § PrerequisiteMarker</c> would not break anything and would quietly make
    ///     the guard blind to every one of these messages, which is what <c>SkipConventionTests</c>
    ///     is for.
    /// </remarks>
    public static string SkipMessage(string provider, string wouldProve, Exception? reason = null) =>
        $"SKIPPED — {provider}: the cluster-backed conformance infrastructure did not come up, so "
        + "nothing was checked. "
        + $"{PrerequisiteMarker} a Docker daemon able to run {K3sImage}, {PostgresImage} and {RedisImage}. "
        + $"WOULD PROVE: {wouldProve} "
        + """This suite is present by name and skipped rather than absent, because "conformance: """
        + """green" must not be readable as "criterion 3 is met" on a machine that never ran the """
        + "check. docs/plan/03 § Providers, docs/plan/24 § Phase 1. "
        + "What went wrong: "
        + Describe(reason ?? failure);

    static string Describe(Exception? ex) =>
        ex is null ? "no exception was recorded." : ex.GetType().Name + ": " + ex.Message;

    static async Task<ClusterEndpoints> StartAsync(CancellationToken cancellationToken) {
        running = await StartContainersAsync(cancellationToken).ConfigureAwait(false);

        return running.Endpoints;
    }

    /// <summary>
    ///     Stops the containers <see cref="TryStartAsync" /> started, if this process started any.
    ///     <see cref="ClusterInfrastructureTeardown" /> calls it when the test run ends.
    /// </summary>
    /// <remarks>
    ///     ⚠ Swallows a container that won't stop. Testcontainers' resource reaper removes it when
    ///     the process dies, a killed process included, so a failed stop isn't a test result. The
    ///     reaper is the guarantee and this is the tidy path.
    /// </remarks>
    public static async ValueTask StopAsync() {
        ClusterContainers? containers;

        await Gate.WaitAsync().ConfigureAwait(false);

        try {
            containers = running;
            running = null;
        } finally {
            Gate.Release();
        }

        if (containers is null) {
            return;
        }

        try {
            await containers.DisposeAsync().ConfigureAwait(false);
        } catch (Exception) {
            // The reaper's, as the remarks say.
        }
    }

    /// <summary>
    ///     Starts a fresh k3s, PostgreSQL and Redis, applies the Orleans schema to the shard, and
    ///     hands the three back with their endpoints — to a caller that will dispose them.
    /// </summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Public, and the one caller besides <see cref="TryStartAsync" /> is the reason.</b>
    ///         The process-wide trio above lives until the test run ends, which is right for the
    ///         provider suites — one cluster for every class in the assembly — and wrong for a test
    ///         whose subject is <i>what a fresh cluster becomes</i>: <c>CyberCloud.Bundle.Cluster.Conformance</c>
    ///         installs <c>charts/bundle/</c> components onto an API server that must hold none of
    ///         them beforehand, and then needs the Orleans harness over the same k3s. Before this
    ///         method it would have had to copy the three builders and the schema apply, and the pin
    ///         and the reminder-table configuration would have had a second home to drift in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ClusterSlot" /> is taken here, before the first container.</b> The permit
    ///         is per process and idempotent, so a bundle fixture that already holds it for its own
    ///         k3s pays nothing; a process that does not yet hold it waits here, which is the whole
    ///         point of the permit.
    ///     </para>
    /// </remarks>
    public static async Task<ClusterContainers> StartContainersAsync(CancellationToken cancellationToken) {
        // ⚠ Taken BEFORE the containers, and released only when the process exits. See the remarks.
        ClusterSlot.Acquire();

        var k3s = K3s().Build();

        var postgres = new PostgreSqlBuilder(PostgresImage)
            .WithDatabase("cybercloud")
            .WithUsername("cybercloud")
            .WithPassword("cybercloud")
            .Build();

        var redis = new RedisBuilder(RedisImage)
            // docs/plan/05 § Hot: noeviction. Set explicitly rather than inherited from the image,
            // for the reason CyberCloud.ServiceDefaults.Tests' StorageFixture gives — a default is
            // how one environment ends up with allkeys-lru and another with noeviction.
                .WithCommand("--maxmemory-policy", "noeviction", "--appendonly", "yes")
                .Build();

        await Task.WhenAll(
            k3s.StartAsync(cancellationToken),
            postgres.StartAsync(cancellationToken),
            redis.StartAsync(cancellationToken)
        )
            .ConfigureAwait(false);

        var durable = postgres.GetConnectionString();

        // ⚠ The SHIPPED applier, not a test-local copy of it. `--apply-durable-schema` runs this exact
        // call, so a defect in it fails here rather than in a cluster.
        await OrleansAdoNetSchema.ApplyAsync(durable, cancellationToken).ConfigureAwait(false);

        var endpoints = new ClusterEndpoints(
            await k3s.GetKubeconfigAsync().ConfigureAwait(false),
            durable,
            redis.GetConnectionString()
        );

        return new ClusterContainers(k3s, postgres, redis, endpoints);
    }
}

/// <summary>
///     The three containers <see cref="ClusterInfrastructure.StartContainersAsync" /> started, with
///     where they are. Disposing it stops all three.
/// </summary>
/// <param name="K3s">The API server.</param>
/// <param name="Postgres">The durable shard, schema applied.</param>
/// <param name="Redis">The reminder table.</param>
/// <param name="Endpoints">Where the three are.</param>
public sealed record ClusterContainers(
    K3sContainer K3s,
    PostgreSqlContainer Postgres,
    RedisContainer Redis,
    ClusterEndpoints Endpoints
) : IAsyncDisposable {
    /// <inheritdoc />
    /// <remarks>
    ///     All three at once, because nothing depends on the order and the stop is time the test
    ///     runner is waiting on (see <see cref="ClusterInfrastructureTeardown" />).
    /// </remarks>
    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(
                K3s.DisposeAsync().AsTask(),
                Postgres.DisposeAsync().AsTask(),
                Redis.DisposeAsync().AsTask()
            )
            .ConfigureAwait(false);
}

/// <summary>Where the three containers are.</summary>
/// <param name="Kubeconfig">The k3s kubeconfig, as YAML.</param>
/// <param name="DurableConnectionString">The PostgreSQL shard, with the Orleans schema applied.</param>
/// <param name="RedisConnectionString">The reminder table's Redis.</param>
public sealed record ClusterEndpoints(
    string Kubeconfig,
    string DurableConnectionString,
    string RedisConnectionString
);

/// <summary>
///     One cross-process permit to hold a k3s container, taken for the life of the test process.
/// </summary>
/// <remarks>
///     ⚠ <b>A lock file rather than a named mutex.</b> A named <c>Mutex</c> has thread affinity and is
///     released by whichever thread took it, which is not a property a fixture started from the
///     thread pool can promise. An exclusively-opened file is released by the operating system when
///     the process ends, however it ends — which is the only release guarantee worth having for a
///     lock that outlives every test.
/// </remarks>
public static class ClusterSlot {
    /// <summary>
    ///     The lock file's name. Shared by the fifteen assemblies built on
    ///     <see cref="ClusterInfrastructure" />.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         This read "shared by every assembly that runs a cluster-backed suite" until the #77
    ///         review, and #77 is precisely what made it false.
    ///     </b>
    ///     <c>build/Build.Test.cs</c> § <c>StartsCluster</c> gives "cluster-backed suite" a
    ///     build-enforced meaning — seventeen suites, decided by what their output ships — and two of
    ///     the seventeen take no lock here at all. <see cref="ClusterInfrastructure" />'s remarks
    ///     name them. Every assembly that takes <i>this</i> permit shares this name; not every
    ///     cluster-backed suite takes it, which is the whole reason build/ has a cap of its own.
    ///     ⚠ <c>CyberCloud.Bundle.Cluster.Conformance.csproj</c> quotes this summary as the
    ///     documented contract for depending on the project, so the two move together.
    /// </remarks>
    public const string FileName = "cybercloud-cluster-conformance.slot";

    static FileStream? held;

    /// <summary>Blocks until this process owns the slot.</summary>
    public static void Acquire() {
        if (held is not null) {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), FileName);

        while (true) {
            try {
                held = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return;
            } catch (IOException) {
                // Somebody else's cluster-backed suite is running. Waiting is the entire point.
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }
}
