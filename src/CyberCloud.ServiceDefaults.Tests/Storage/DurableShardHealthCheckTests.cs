using CyberCloud.ServiceDefaults.HealthChecks;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The check that answers <c>BootstrapProviderLivenessTests</c>: a silo with a dead durable shard
///     starts healthy, and this is what says so out loud without evicting it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No Docker, and none needed.</b> "Unreachable" is the interesting half, and a port
///         nothing is listening on is unreachable in exactly the way a dead PostgreSQL is — Npgsql
///         fails the connect and the check reports the shard by name. The half that needs a real
///         server is "a reachable shard reports reachable", and that is
///         <c>OrleansAdoNetSchemaTests</c>'s gate rather than a container started here.
///     </para>
/// </remarks>
public sealed class DurableShardHealthCheckTests {
    [Fact]
    public async Task AnUnreachableShardIsReportedAndTheReportNamesWhichShard() {
        // Two dead shards out of three configured names, so "which one" is a real question. At 16
        // shards, "storage is unhealthy" means checking 16 servers.
        using var provider = Wire(
            ("durable-00", Dead()),
            ("durable-07", Dead()),
            ("durable-11", Dead())
        );

        var entry = await CheckAsync(provider);

        entry.Status.ShouldBe(
            HealthStatus.Unhealthy,
            "the check carries no tag, so Unhealthy cannot evict anything and there is no reason to "
            + "soften it to Degraded — see DurableShardHealthCheck's remarks."
        );

        Described(entry).ShouldContain("durable-00");
        Described(entry).ShouldContain("durable-07");
        Described(entry).ShouldContain("durable-11");

        // And the per-shard detail, which is what /api/health carries into an alert.
        foreach (var shard in new[] { "durable-00", "durable-07", "durable-11" }) {
            entry.Data.ShouldContainKey(shard);
            entry.Data[shard].ShouldNotBe(DurableShardHealthCheck.Reachable);
        }
    }

    [Fact]
    public async Task OnlyTheUnreachableShardsAreNamed() {
        // The negative half of the same claim: a shard that is fine must not appear in the
        // description, or the report is noise at 16 shards. There is no reachable PostgreSQL here, so
        // this asserts the shape the other way round — a name that is not configured is never
        // reported.
        using var provider = Wire(("durable-00", Dead()));

        var entry = await CheckAsync(provider);

        Described(entry).ShouldContain("durable-00");
        Described(entry).ShouldNotContain("durable-01");
        entry.Data.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NoConfiguredShardsIsHealthyRatherThanEmptyUnhealthy() {
        // A silo with no durable tier has AddCyberCloudGrainStorage unwired entirely, so this check
        // is not registered at all. If it ever is, "nothing to reach" must not read as "everything
        // is down".
        using var provider = Wire();

        (await CheckAsync(provider)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task TheProbeIsCachedAndSingleFlightedSoScrapesDoNotMultiplyConnections() {
        // ⚠ The cost claim, measured rather than asserted. A SELECT 1 per shard per scrape is not
        // free at 16 shards and 30 silos (docs/plan/05 § Storage provider wiring budgets 5 pooled
        // connections per tenant per shard, and this probe is deliberately outside that pool). The
        // listener counts what actually arrives on the wire.
        using var shard = new CountingListener();
        var clock = new SteppableClock();

        using var provider = Wire(clock, ("durable-00", shard.ConnectionString));

        await CheckAsync(provider);
        var afterFirst = shard.Accepted;
        afterFirst.ShouldBeGreaterThan(0);

        // Four more scrapes inside the window: no new connections.
        for (var i = 0; i < 4; i++) {
            await CheckAsync(provider);
        }

        shard.Accepted.ShouldBe(afterFirst, "five scrapes in one window must cost one round of probes.");

        // Past the window, it probes again — a stale answer forever would be the other failure.
        clock.Advance(TimeSpan.FromMinutes(1));
        await CheckAsync(provider);

        shard.Accepted.ShouldBeGreaterThan(afterFirst);
    }

    [Fact]
    public async Task AShardThatAcceptsAndNeverAnswersIsUnreachableWithinTheTimeout() {
        // The failure a dead port does not cover: PgBouncer up, PostgreSQL behind it gone. The
        // connect succeeds and nothing else does, which is exactly why the probe is a SELECT 1 and
        // not an Open.
        using var shard = new CountingListener();
        using var provider = Wire(("durable-00", shard.ConnectionString));

        var elapsed = Stopwatch.StartNew();
        var entry = await CheckAsync(provider);
        elapsed.Stop();

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        Described(entry).ShouldContain("durable-00");

        // Bounded by HealthProbeTimeout, which has to stay under the five-second request timeout on
        // the health endpoints or a dark tier times out the scrape instead of reporting itself dark.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnUnreachableShardDoesNotFailTheReadinessPredicate() {
        // ⚠ The whole cascade argument in one assertion. This runs the readiness predicate — the
        // literal one from MapDefaultEndpoints, `Tags.Contains("ready")` — over a service collection
        // that has a dead shard in it, and requires that the shard contributes nothing to the answer.
        //
        // It is the readiness half of BootstrapProviderLivenessTests: the silo keeps taking traffic,
        // on purpose, and the reason is written down where a future edit has to read it.
        using var provider = Wire(
            services => services.AddHealthChecks().AddCheck(
                // Standing in for silo-ready, which needs a running silo. The point of the stand-in
                // is that the readiness answer must be its answer and nothing else's.
                "silo-ready",
                () => HealthCheckResult.Healthy("serving"),
                [HealthCheckTags.Ready]
            ),
            null,
            ("durable-00", Dead())
        );

        var health = provider.GetRequiredService<HealthCheckService>();

        var ready = await health.CheckHealthAsync(
            x => x.Tags.Contains(HealthCheckTags.Ready),
            TestContext.Current.CancellationToken
        );

        ready.Status.ShouldBe(
            HealthStatus.Healthy,
            "a silo with one unreachable shard is still serving every tenant on every other shard, "
            + "and evicting it concentrates that shard's load on its neighbours."
        );

        ready.Entries.Keys.ShouldBe(["silo-ready"]);
        ready.Entries.ShouldNotContainKey(DurableShardHealthCheck.Name);

        // And the same check, run with the everything predicate, does report it — so the absence
        // above is the tag doing its job rather than the check being broken.
        var everything = await health.CheckHealthAsync(TestContext.Current.CancellationToken);

        everything.Entries.ShouldContainKey(DurableShardHealthCheck.Name);
        everything.Entries[DurableShardHealthCheck.Name].Status.ShouldBe(HealthStatus.Unhealthy);
    }

    /// <summary>The entry's description, with the nullability the assertions do not care about gone.</summary>
    static string Described(HealthReportEntry entry) => entry.Description ?? string.Empty;

    static async Task<HealthReportEntry> CheckAsync(ServiceProvider provider) {
        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(x => x.Name == DurableShardHealthCheck.Name, TestContext.Current.CancellationToken);

        return report.Entries[DurableShardHealthCheck.Name];
    }

    static ServiceProvider Wire(params (string Shard, string ConnectionString)[] shards) =>
        Wire(null, null, shards);

    static ServiceProvider Wire(
        TimeProvider? time,
        params (string Shard, string ConnectionString)[] shards
    ) =>
        Wire(null, time, shards);

    static ServiceProvider Wire(
        Action<IServiceCollection>? extra,
        TimeProvider? time,
        params (string Shard, string ConnectionString)[] shards
    ) {
        var options = new CyberCloudStorageOptions();

        // Parsed, never connected — ConfiguredShardConnections builds the hot tier's
        // ConfigurationOptions eagerly and an empty string is not parseable.
        options.Hot.ConnectionString = "127.0.0.1:6379";
        options.Durable.HealthProbeTimeout = TimeSpan.FromSeconds(1);

        foreach (var (shard, connectionString) in shards) {
            options.Durable.Shards[shard] = connectionString;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton<IShardConnections>(new ConfiguredShardConnections(options));

        if (time is not null) {
            services.AddSingleton(time);
        }

        services.AddDurableShardHealthCheck();
        extra?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>A connection string for a port nothing is listening on.</summary>
    static string Dead() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;

        return $"Host=127.0.0.1;Port={port};Database=cc;Username=cc;Password=cc";
    }
}

/// <summary>
///     A socket that accepts PostgreSQL-shaped connections, says nothing, and counts them.
/// </summary>
/// <remarks>
///     It stands in for the failure a closed port cannot express — a live PgBouncer with no PostgreSQL
///     behind it — and its count is how the caching claim is measured rather than asserted.
/// </remarks>
sealed class CountingListener : IDisposable {
    readonly TcpListener listener;
    readonly CancellationTokenSource stopping = new();
    readonly List<TcpClient> held = [];

    int accepted;

    public CountingListener() {
        listener = new(IPAddress.Loopback, 0);
        listener.Start();

        _ = Task.Run(AcceptAsync);
    }

    /// <summary>How many connections have arrived.</summary>
    public int Accepted => Volatile.Read(ref accepted);

    /// <summary>A connection string pointed at this listener.</summary>
    public string ConnectionString =>
        $"Host=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};Database=cc;Username=cc;Password=cc";

    public void Dispose() {
        stopping.Cancel();
        listener.Dispose();

        lock (held) {
            foreach (var client in held) {
                client.Dispose();
            }

            held.Clear();
        }

        stopping.Dispose();
    }

    async Task AcceptAsync() {
        try {
            while (!stopping.IsCancellationRequested) {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);

                Interlocked.Increment(ref accepted);

                // Held rather than closed: closing would hand Npgsql a fast "connection reset", and
                // the case being modelled is a peer that answers nothing at all.
                lock (held) {
                    held.Add(client);
                }
            }
        } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) {
            // Disposed.
        }
    }
}

/// <summary>
///     A clock a test can move, so the staleness window is asserted rather than slept through.
/// </summary>
/// <remarks>
///     Hand-rolled because <c>Microsoft.Extensions.TimeProvider.Testing</c> is not in
///     <c>Directory.Packages.props</c> and one overridden method is cheaper than a package decision.
/// </remarks>
sealed class SteppableClock : TimeProvider {
    long timestamp = Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far forward — the staleness window is 30 seconds by default.</param>
    public void Advance(TimeSpan by) =>
        Interlocked.Add(ref timestamp, (long)(by.TotalSeconds * TimestampFrequency));
}
