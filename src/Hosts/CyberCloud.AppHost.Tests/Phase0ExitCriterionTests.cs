using System.Globalization;
using CyberCloud.Silo.Host.Hello;
using Npgsql;
using Orleans.Multitenant;
using Orleans.Runtime;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     docs/plan/24 § Phase 0's exit criterion, sentence by sentence.
/// </summary>
/// <remarks>
///     <para>
///         "<c>dotnet run</c> on the AppHost brings up a two-silo cluster with real Redis, Postgres,
///         NATS and k3s; a hello-world tenant-scoped grain round-trips through both storage tiers."
///     </para>
///     <para>
///         The fixture is the criterion's first clause: <see cref="LocalTopology" /> does not
///         return until the AppHost has started and both silos report healthy. Everything below is
///         the rest of it.
///     </para>
/// </remarks>
[Collection(LocalTopologySuite.Name)]
public sealed class Phase0ExitCriterionTests(LocalTopology topology)
{
    /// <summary>A tenant id that is a pure function of nothing, so runs are comparable.</summary>
    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0001");

    /// <summary>A second tenant, on (probably) the other shard.</summary>
    static readonly Guid OtherTenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0002");

    static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    [Fact]
    public void TheColdStartIsReported()
    {
        // ⚠ Reported, not asserted at a threshold. ADR-014's justification is that Aspire is "the
        // best local-orchestration experience .NET has", which is a claim about seconds — but the
        // number depends on the image cache, the machine and whether Docker Desktop is warm, so a
        // threshold here would be a flaky test masquerading as a budget. Printing it is what makes
        // a regression visible in the log of the run that caused it.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"AppHost cold start (StartAsync until both silos healthy): "
            + $"{topology.ColdStart.TotalSeconds:F1} s");

        TestContext.Current.TestOutputHelper?.WriteLine(topology.States());

        topology.ColdStart.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task AHelloWorldTenantScopedGrainRoundTripsThroughBothStorageTiers()
    {
        var grain = topology.Client
            .ForTenant(Id(Tenant))
            .GetGrain<IHelloGrain>("hello/phase-0");

        var written = await grain.SayHelloAsync("hello, cyber cloud");

        written.HotGreeting.ShouldBe("hello, cyber cloud");
        written.DurableGreeting.ShouldBe("hello, cyber cloud");
        written.TenantId.ShouldBe(Id(Tenant));

        // ⚠ THE ROUND TRIP. Without the deactivation this asserts on the activation's own fields and
        // would pass against a storage provider that discards every write. After it, the next call
        // is a fresh activation whose two states can only have come back out of Redis and out of
        // PostgreSQL.
        await grain.DeactivateAsync();

        var readBack = await grain.ReadBackAsync();

        readBack.HotGreeting.ShouldBe(
            "hello, cyber cloud",
            "the hot tier did not return what was written to it — Redis is not storing the grain.");

        readBack.DurableGreeting.ShouldBe(
            "hello, cyber cloud",
            "the durable tier did not return what was written to it — PostgreSQL is not storing the "
            + "grain.");

        readBack.HotWrites.ShouldBe(1);
        readBack.DurableWrites.ShouldBe(1);
    }

    [Fact]
    public async Task TheDurableRowLandsOnOneTenantShardAndNeverOnThePlatformShard()
    {
        var token = TestContext.Current.CancellationToken;

        var grain = topology.Client
            .ForTenant(Id(OtherTenant))
            .GetGrain<IHelloGrain>("hello/sharding");

        await grain.SayHelloAsync("sharded");

        var rows = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var shard in (string[])
                 [
                     CyberCloudResources.ShardA,
                     CyberCloudResources.ShardB,
                     CyberCloudResources.PlatformShard,
                 ])
        {
            rows[shard] = await GrainTypesAsync(shard, token);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Join(
                Environment.NewLine,
                rows.Select(x => $"{x.Key}: {string.Join(", ", x.Value)}")));

        var helloOn = rows.ToDictionary(
            x => x.Key,
            x => x.Value.Count(
                type => type.Contains("hello", StringComparison.OrdinalIgnoreCase)),
            StringComparer.Ordinal);

        // ⚠ The platform shard is the assertion that matters. GrainBackedShardMapCache excludes the
        // configured NullTenantShard from the placement list precisely so that a tenant's state
        // cannot land where the tenant directory and the shard map live; if this were ever non-zero,
        // a tenant's rows would be sharing a database with the platform's own, and the "N plain
        // Postgres servers" story of docs/plan/05 § Durable would be one server with everything on
        // it.
        helloOn[CyberCloudResources.PlatformShard].ShouldBe(
            0,
            "a tenant-scoped grain was stored on the null-tenant platform shard.");

        (helloOn[CyberCloudResources.ShardA] + helloOn[CyberCloudResources.ShardB])
            .ShouldBeGreaterThan(
                0,
                "the durable state of a tenant-scoped grain is on neither tenant shard, so nothing "
                + "was written to PostgreSQL at all.");
    }

    [Fact]
    public async Task TheK3sApiServerAnswersKubernetes()
    {
        // The k3s API server is reachable and speaks Kubernetes. Anonymous authentication is off in
        // k3s, so the honest evidence is a Kubernetes `Status` object with reason Unauthorized —
        // which no port-forward, no proxy and no half-started container produces. Anything that is
        // not the API server answers with a connection failure or with something that is not JSON.
        using var handler = new HttpClientHandler
        {
            // Local, self-signed, and generated fresh at container start. Verifying it would mean
            // parsing the kubeconfig's CA out for a test whose subject is reachability.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var response = await http.GetAsync(
            new Uri($"https://127.0.0.1:{CyberCloudResources.K3sApiPort}/version"),
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldContain(
            "\"apiVersion\"",
            customMessage: "https://127.0.0.1:6443/version did not answer with a Kubernetes object, "
            + "so nothing can reach the k3s API server.");
    }

    [Fact]
    public async Task TheKubeconfigIsWrittenWhereSomethingOutsideTheContainerCanReadIt()
    {
        // ⚠ Aspire has no "copy a file out of a container when it is ready", so this is a bind mount
        // plus `--write-kubeconfig`, and the thing that can silently fail is the file mode: k3s
        // writes 0600 by default, which is root-owned inside the container and unreadable outside
        // it. `--write-kubeconfig-mode=666` is the fix and this is what checks it is still there.
        var path = Path.Combine(
            TestPaths.AppHostDirectory,
            ".k3s",
            "kubeconfig.yaml");

        File.Exists(path).ShouldBeTrue(
            $"k3s did not write a kubeconfig to {path}, so CyberCloud.Kubernetes has no way to "
            + "reach the local cluster.");

        var kubeconfig = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        kubeconfig.ShouldContain(
            $"127.0.0.1:{CyberCloudResources.K3sApiPort}",
            customMessage: "the kubeconfig names an address that is not the one the API server is "
            + "published on.");
    }

    /// <summary>
    ///     Every <c>graintypestring</c> stored on one shard.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read back as a list rather than counted with a <c>LIKE '%HelloGrain%'</c>, because
    ///     the stored string is not the CLR type name.</b> Orleans 7 and later store the <i>grain
    ///     type name</i> — the class name with a <c>Grain</c> suffix stripped and lower-cased — so
    ///     <c>HelloGrain</c> is stored as <c>hello</c> and that <c>LIKE</c> matches nothing while the
    ///     row is sitting right there. Observed: the round-trip test passed and this one reported
    ///     zero rows on every shard, which reads as "PostgreSQL is not storing anything". Returning
    ///     the strings means a future mismatch shows what is actually stored.
    /// </remarks>
    async Task<IReadOnlyList<string>> GrainTypesAsync(string shard, CancellationToken cancellationToken)
    {
        var connectionString = await topology.ShardConnectionStringAsync(shard, cancellationToken);

        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT graintypestring FROM orleansstorage;",
            connection);

        var types = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            types.Add(reader.GetString(0));
        }

        return types;
    }
}

/// <summary>Where the AppHost project is on disk, from the test assembly's point of view.</summary>
/// <remarks>
///     The bind-mounted kubeconfig directory is <c>&lt;AppHost project&gt;/.k3s</c>, chosen in the
///     AppHost as <c>builder.AppHostDirectory</c>. A test cannot ask the running application for it,
///     so it is found from the repository layout instead — docs/plan/03 § Hosts.
///     <para>
///         ⚠ Walked upward to <c>CyberCloud.slnx</c> rather than counted in <c>..</c> segments.
///         <c>Directory.Build.props</c> redirects every output into <c>artifacts/bin/…</c>, so the
///         depth from an assembly to the repository root is a property of the build layout rather
///         than of the project, and a fixed count is wrong the day somebody changes
///         <c>ArtifactsPath</c>.
///     </para>
/// </remarks>
static class TestPaths
{
    public static string AppHostDirectory { get; } =
        Path.Combine(RepositoryRoot(), "src", "Hosts", "CyberCloud.AppHost");

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(typeof(TestPaths).Assembly.Location)!);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No CyberCloud.slnx above the test assembly, so the repository root cannot be found.");
    }
}
