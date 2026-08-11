using CyberCloud.ServiceDefaults.HealthChecks;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The half-schema hole against a real PostgreSQL, plus the concurrency claim that goes with it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THESE SKIP UNLESS <c>CYBERCLOUD_TEST_SHARD</c> IS SET, AND THEY ARE UNRUN TODAY.</b>
///         Everything here needs a server that can answer <c>to_regclass</c> — a decision about
///         observed objects is <c>DurableSchemaPlanTests</c> and runs everywhere, but <i>observing</i>
///         them is not something a fake can do honestly. The rest of this project reaches for
///         Testcontainers (ADR-018, docs/plan/23 § Test layers); this file deliberately does not,
///         because a plain connection string runs against a container, a developer's local server, or
///         a real staging shard without an edit, and the assertions are about a database rather than
///         about a silo.
///     </para>
///     <code>
///     CYBERCLOUD_TEST_SHARD="Host=localhost;Port=5432;Database=cc_scratch;Username=cc;Password=cc" \
///         dotnet run --project src/CyberCloud.ServiceDefaults.Tests
///     </code>
///     <para>
///         ⚠ <b>Point it at a scratch database.</b> Every test starts by running the recovery SQL from
///         <c>deploy/README.md § Idempotence</c> — <c>DROP TABLE IF EXISTS orleansstorage,
///         orleansquery</c> — which is destructive of exactly the tables a real shard holds tenant
///         state in. Running the documented recovery is also the point: if the README's SQL stops
///         working, these stop passing.
///     </para>
/// </remarks>
public sealed class OrleansAdoNetSchemaTests {
    /// <summary>The environment variable that supplies a shard, and gates the whole file.</summary>
    public const string ShardVariable = "CYBERCLOUD_TEST_SHARD";

    /// <summary>The recovery SQL from <c>deploy/README.md § Idempotence</c>, verbatim.</summary>
    const string Recovery = """
        DROP TABLE IF EXISTS orleansstorage, orleansquery;
        DROP FUNCTION IF EXISTS writetostorage;
        """;

    static string? Shard => Environment.GetEnvironmentVariable(ShardVariable);

    [Fact]
    public async Task AHalfAppliedSchemaIsDetectedAndCompletedRatherThanSkipped() {
        var shard = await ResetAsync();

        // The state a pod evicted between the two scripts leaves: PostgreSQL-Main.sql and nothing
        // else. Constructed rather than simulated, because the bug was that the probe could not tell
        // this apart from a complete schema.
        await ExecuteAsync(shard, MainScript());

        (await ProbeAsync(shard)).Plan.ShouldBe(DurableSchemaPlan.ApplyPersistenceScript);

        (await OrleansAdoNetSchema.ApplyAsync(shard, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the shard was half applied, so this call had work to do.");

        (await ProbeAsync(shard)).IsComplete.ShouldBeTrue();

        // And the four rows AdoNetGrainStorage.Init reads are actually there, which is the thing the
        // silo fails on rather than the tables.
        (await ScalarAsync(shard, "SELECT count(*) FROM orleansquery;")).ShouldBe(4L);
    }

    [Fact]
    public async Task AFullyAppliedSchemaIsANoOp() {
        var shard = await ResetAsync();

        (await OrleansAdoNetSchema.ApplyAsync(shard, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await OrleansAdoNetSchema.ApplyAsync(shard, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await OrleansAdoNetSchema.ApplyAsync(shard, TestContext.Current.CancellationToken)).ShouldBeFalse();

        // The cheap path stays cheap: no advisory lock is taken on it, so nothing is waiting on
        // anything. pg_locks is the observable form of that claim.
        (await ScalarAsync(
            shard,
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory';"
        )).ShouldBe(0L);
    }

    [Fact]
    public async Task TwoConcurrentAppliersDoNotCorrupt() {
        var shard = await ResetAsync();

        // deploy/README.md § N silos racing calls the racing case out as "probe-then-CREATE is not
        // atomic" and accepts a 42P07 for the loser. With the advisory lock the loser gets a clean
        // no-op instead, which is a strictly better answer and the one asserted here.
        var appliers = Enumerable.Range(0, 4)
            .Select(_ => OrleansAdoNetSchema.ApplyAsync(shard, TestContext.Current.CancellationToken))
            .ToArray();

        var applied = await Task.WhenAll(appliers);

        applied.Count(x => x).ShouldBe(1, "exactly one applier does the work.");
        applied.Count(x => !x).ShouldBe(3, "the other three find a complete schema and return.");

        (await ProbeAsync(shard)).IsComplete.ShouldBeTrue();
        (await ScalarAsync(shard, "SELECT count(*) FROM orleansquery;"))
            .ShouldBe(4L, "a racing INSERT would have doubled these, or half of them would be missing.");
    }

    [Fact]
    public async Task ATornSchemaIsRefusedWithAnInventoryRatherThanGuessedAt() {
        var shard = await ResetAsync();

        await OrleansAdoNetSchema.ApplyAsync(shard, TestContext.Current.CancellationToken);

        // A state no interruption produces: an operator dropped the index. Re-running the persistence
        // script here would fail on 42P07 for orleansstorage, and dropping what is in the way would
        // drop every tenant's durable state on this shard.
        await ExecuteAsync(shard, "DROP INDEX ix_orleansstorage;");

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => OrleansAdoNetSchema.ApplyAsync(shard, TestContext.Current.CancellationToken)
        );

        failure.Message.ShouldContain("ix_orleansstorage=MISSING");
        failure.Message.ShouldContain("orleansstorage=present");

        // Nothing was dropped on the way to that answer.
        (await ScalarAsync(shard, "SELECT count(*) FROM orleansquery;")).ShouldBe(4L);
    }

    [Fact]
    public async Task AReachableShardIsReportedReachable() {
        // The half of DurableShardHealthCheckTests that a closed port cannot prove. Everything there
        // is about an unreachable shard; a check that answered Unhealthy unconditionally would pass
        // every one of those tests.
        var shard = await ResetAsync();

        var options = new CyberCloudStorageOptions();
        options.Hot.ConnectionString = "127.0.0.1:6379";
        options.Durable.Shards["durable-00"] = shard;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton<IShardConnections>(new ConfiguredShardConnections(options));
        services.AddDurableShardHealthCheck();

        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(
                x => x.Name == DurableShardHealthCheck.Name,
                TestContext.Current.CancellationToken
            );

        var entry = report.Entries[DurableShardHealthCheck.Name];

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data["durable-00"].ShouldBe(DurableShardHealthCheck.Reachable);
    }

    /// <summary>Skips the test when no shard is configured, and otherwise returns an empty one.</summary>
    static async Task<string> ResetAsync() {
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(Shard),
            $"{ShardVariable} is not set. These assertions need a real PostgreSQL — see the remarks "
            + "on OrleansAdoNetSchemaTests for the one-line invocation."
        );

        await ExecuteAsync(Shard!, Recovery);
        return Shard!;
    }

    static async Task<DurableSchemaState> ProbeAsync(string shard) {
        // The probe, asked the way the applier asks it, so the test and the code cannot disagree
        // about what "complete" means. Reading the objects directly here would be a second opinion,
        // and the four assertions that follow it are the independent check.
        var query = await ScalarAsync(shard, "SELECT to_regclass('orleansquery')::text IS NOT NULL;");
        var storage = await ScalarAsync(shard, "SELECT to_regclass('orleansstorage')::text IS NOT NULL;");
        var index = await ScalarAsync(shard, "SELECT to_regclass('ix_orleansstorage')::text IS NOT NULL;");
        var function = await ScalarAsync(
            shard,
            "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'writetostorage');"
        );

        var rows = (bool)query!
            ? (long)(await ScalarAsync(shard, "SELECT count(*) FROM orleansquery;"))!
            : 0L;

        return new((bool)query!, (bool)storage!, (bool)index!, (bool)function!, (int)rows);
    }

    static string MainScript() {
        // The applier's own embedded copy, read out of its assembly. The half-schema this constructs
        // is therefore byte-for-byte the one an interrupted apply leaves, rather than a hand-written
        // approximation of it that could drift.
        using var stream = typeof(OrleansAdoNetSchema).Assembly
            .GetManifestResourceStream("CyberCloud.ServiceDefaults.Storage.PostgreSQL-Main.sql")!;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static async Task ExecuteAsync(string shard, string sql) {
        await using var connection = await OpenAsync(shard);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    static async Task<object?> ScalarAsync(string shard, string sql) {
        await using var connection = await OpenAsync(shard);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    static async Task<NpgsqlConnection> OpenAsync(string shard) {
        // Pooling off, for the reason the applier turns it off: an idle backend left behind here
        // would count against the per-shard budget these tests are run beside.
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(shard) { Pooling = false }.ConnectionString
        );

        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
