using CyberCloud.ServiceDefaults.Storage;
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
///         ⚠ <b>Started once per test process, and <see cref="TryStartAsync" /> neither throws nor
///         skips.</b> A start that threw would fail the class, which reads as "the provider is
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
///         ⚠ <b>The images are pinned, and the k3s one is pinned to
///         <see cref="K3sImage" /> for the reason
///         <c>CyberCloud.Kubernetes.Tests.Infrastructure.K3sFixture</c> gives at length:</b>
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
/// </remarks>
public static class ClusterInfrastructure {
    /// <summary>
    ///     The k3s image, taken from <c>CyberCloud.Kubernetes.Tests</c>'s pin rather than re-chosen.
    /// </summary>
    public const string K3sImage = "rancher/k3s:v1.35.7-k3s1";

    /// <summary>The PostgreSQL image, matching <c>CyberCloud.ServiceDefaults.Tests</c>'s durable shards.</summary>
    public const string PostgresImage = "postgres:17-alpine";

    /// <summary>The Redis image, matching <c>CyberCloud.ServiceDefaults.Tests</c>'s hot tier.</summary>
    public const string RedisImage = "redis:8-alpine";

    static readonly SemaphoreSlim Gate = new(1, 1);

    static ClusterEndpoints? started;
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
    public static string SkipMessage(string provider, string wouldProve, Exception? reason = null) =>
        $"SKIPPED — {provider}: the cluster-backed conformance infrastructure did not come up, so "
        + "nothing was checked. "
        + $"NEEDS: a Docker daemon able to run {K3sImage}, {PostgresImage} and {RedisImage}. "
        + $"WOULD PROVE: {wouldProve} "
        + "This suite is present by name and skipped rather than absent, because \"conformance: "
        + "green\" must not be readable as \"criterion 3 is met\" on a machine that never ran the "
        + "check. docs/plan/03 § Providers, docs/plan/24 § Phase 1. "
        + "What went wrong: " + Describe(reason ?? failure);

    static string Describe(Exception? ex) =>
        ex is null ? "no exception was recorded." : ex.GetType().Name + ": " + ex.Message;

    static async Task<ClusterEndpoints> StartAsync(CancellationToken cancellationToken) {
        // ⚠ Taken BEFORE the containers, and released only when the process exits. See the remarks.
        ClusterSlot.Acquire();

        var k3s = new K3sBuilder(K3sImage).Build();

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

        // Testcontainers' resource reaper removes the containers when this process dies, including a
        // process that was killed. This is the tidy path, not the guarantee.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            try {
                k3s.DisposeAsync().AsTask().GetAwaiter().GetResult();
                postgres.DisposeAsync().AsTask().GetAwaiter().GetResult();
                redis.DisposeAsync().AsTask().GetAwaiter().GetResult();
            } catch (Exception) {
                // A container that will not stop at process exit is Ryuk's problem, not a test result.
            }
        };

        return endpoints;
    }
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
    /// <summary>The lock file's name. Shared by every assembly that runs a cluster-backed suite.</summary>
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
