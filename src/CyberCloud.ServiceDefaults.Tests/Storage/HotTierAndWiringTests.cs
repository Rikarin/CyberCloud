using CyberCloud.Core.Contracts;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Multitenant;
using Orleans.Persistence;
using Orleans.Storage;
using Shouldly;
using StackExchange.Redis;
using System.Globalization;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The hot tier against a real Redis, and the registration shape both tiers end up with.
/// </summary>
[Collection(StorageSuite.Name)]
public sealed class HotTierAndWiringTests(StorageFixture fixture) {
    [Fact]
    public async Task HotStateRoundTripsAndLandsUnderTheTenantsHashTag() {
        var tenant = StorageFixture.Tenant(101);
        var grain = fixture.Grains.ForTenant(Id(tenant)).GetGrain<IHotStateGrain>("session/abc");

        await grain.WriteAsync("a terminal session");
        (await grain.ReadAsync()).ShouldBe("a terminal session");

        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);

        var tag = fixture.ShardMap.HotHashTagFor(Id(tenant));
        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(pattern: "{" + tag + "}:*")) {
            keys.Add(key.ToString());
        }

        keys.ShouldContain(x => x.EndsWith(":session/abc", StringComparison.Ordinal));
        keys.ShouldAllBe(x => x.StartsWith("{" + tag + "}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStaleEtagLosesOnTheHotTierToo() {
        var tenant = StorageFixture.Tenant(102);
        var grainId = fixture.Grains.ForTenant(Id(tenant)).GetGrain<IHotStateGrain>("session/etag").GetGrainId();
        var storage = fixture.Services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Hot);

        var first = new GrainState<NoteState>(new() { Note = "first" });
        await storage.WriteStateAsync("CyberCloud.Tests.Hot", grainId, first);

        var stale = new GrainState<NoteState>(new() { Note = "stale" }) { ETag = null };

        (await Should.ThrowAsync<InconsistentStateException>(() =>
                storage.WriteStateAsync("CyberCloud.Tests.Hot", grainId, stale)
            ))
            .Message.ShouldContain("Version conflict");
    }

    [Fact]
    public async Task ConfigureTenantOptionsRunsOncePerTenantPerSiloEvenUnderConcurrentLoad() {
        // docs/plan/05 § Storage provider wiring: "Called once per tenant per silo, at first touch,
        // and cached by the multitenant storage provider." If that were false the shard lookup would
        // be on the hot path of every activation, so it is counted rather than believed.
        var tenants = Enumerable.Range(300, 12).Select(StorageFixture.Tenant).ToList();

        var hotBefore = fixture.Hot.Invocations;
        var durableBefore = fixture.Durable.Invocations;

        // 12 tenants × 25 grains × 2 tiers, all at once.
        await Task.WhenAll(
            tenants.SelectMany(tenant => Enumerable.Range(0, 25)
                .SelectMany(i => new[] {
                        fixture.Grains.ForTenant(Id(tenant)).GetGrain<IHotStateGrain>($"session/{i}").WriteAsync("x"),
                        fixture.Grains.ForTenant(Id(tenant)).GetGrain<IDurableStateGrain>($"res/{i}").WriteAsync("x")
                    }
                )
            )
        );

        (fixture.Hot.Invocations - hotBefore).ShouldBe(tenants.Count);
        (fixture.Durable.Invocations - durableBefore).ShouldBe(tenants.Count);

        foreach (var tenant in tenants) {
            fixture.Hot.InvocationsPerTenant[Id(tenant)].ShouldBe(1);
            fixture.Durable.InvocationsPerTenant[Id(tenant)].ShouldBe(1);
        }
    }

    [Fact]
    public async Task EveryTenantSharesOneRedisMultiplexerRatherThanOpeningItsOwn() {
        // The hot tier's version of the connection-pool problem. Orleans.Multitenant builds one
        // RedisGrainStorage per tenant and RedisStorageOptions.CreateMultiplexer defaults to
        // ConnectionMultiplexer.ConnectAsync, so the naive wiring opens one multiplexer per tenant
        // per silo. docs/plan/05 does the arithmetic for Postgres and not for Redis.
        var optionsForOne = new RedisStorageOptions();
        var optionsForAnother = new RedisStorageOptions();

        fixture.Hot.ConfigureForTenant(optionsForOne, Id(StorageFixture.Tenant(401)));
        fixture.Hot.ConfigureForTenant(optionsForAnother, Id(StorageFixture.Tenant(402)));

        var (first, firstShared) = await optionsForOne.CreateMultiplexer(optionsForOne);
        var (second, secondShared) = await optionsForAnother.CreateMultiplexer(optionsForAnother);

        first.ShouldBeSameAs(second);
        firstShared.ShouldBeTrue("a shared multiplexer must not be disposed by the provider that borrowed it");
        secondShared.ShouldBeTrue();
    }

    [Fact]
    public void BothTiersAreRegisteredUnderTheirPlanNamesAndAreDistinctProviders() {
        var hot = fixture.Services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Hot);
        var durable = fixture.Services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable);
        var @default = fixture.Services.GetRequiredKeyedService<IGrainStorage>("Default");

        // docs/plan/04 § Silo composition writes AddMultitenantGrainStorageAsDefault(StorageTiers.Hot, …),
        // an overload that does not exist — AsDefault registers under the literal "Default". Without
        // the alias, [PersistentState("state", StorageTiers.Hot)] resolves nothing.
        hot.ShouldBeSameAs(@default);
        hot.ShouldNotBeSameAs(durable);

        hot.GetType().Name.ShouldBe("MultitenantStorage");
        durable.GetType().Name.ShouldBe("MultitenantStorage");

        // And the unkeyed slot is the multitenant provider, not the tenant-unaware bootstrap one
        // that AddRedisGrainStorage TryAdds into it.
        fixture.Services.GetRequiredService<IGrainStorage>().ShouldBeSameAs(@default);
    }

    [Fact]
    public void TheHotTierValidatorRefusesAProviderWithNoTaggedKeyFunction() {
        // A provider that reaches production without GetStorageKey uses Orleans' default
        // {ServiceId}/state/{grainId}/{grainType} — no braces, no colocation, CROSSSLOT on any
        // multi-key op. It would pass every single-node test.
        var options = new RedisStorageOptions { ConfigurationOptions = ConfigurationOptions.Parse("localhost:6379") };

        Should.Throw<OrleansConfigurationException>(() =>
            new HotTierStorageOptionsValidator(options, "Hot").ValidateConfiguration()
        );
    }

    [Fact]
    public async Task TheDurableConnectionCountPerShardStaysWithinMaxPoolSizeUnderLoad() {
        // ⚠ The observable half of docs/plan/05 § Storage provider wiring's arithmetic. Setting
        // MaxPoolSize is easy; proving the setting reaches the connection string the PROVIDER uses,
        // and that it is per shard rather than per tenant, is the thing that goes wrong.
        var token = TestContext.Current.CancellationToken;

        var tenantsOnShardA = Enumerable.Range(500, 400)
            .Select(StorageFixture.Tenant)
            .Where(x => string.Equals(
                    fixture.ShardMap.DurableShardFor(Id(x)),
                    StorageFixture.ShardA,
                    StringComparison.Ordinal
                )
            )
            .Take(24)
            .ToList();

        tenantsOnShardA.Count.ShouldBeGreaterThan(8);

        var peak = 0;
        using var stop = new CancellationTokenSource();

        var sampler = Task.Run(
            async () => {
                while (!stop.IsCancellationRequested) {
                    peak = Math.Max(peak, await BackendsAgainstShardAAsync(token));
                    await Task.Delay(20, CancellationToken.None);
                }
            },
            CancellationToken.None
        );

        var load = tenantsOnShardA.SelectMany(tenant => Enumerable.Range(0, 30)
            .Select(i =>
                fixture.Grains.ForTenant(Id(tenant)).GetGrain<IDurableStateGrain>($"res/pool/{i}").WriteAsync("load")
            )
        );

        await Task.WhenAll(load);
        await stop.CancelAsync();
        await sampler;

        peak = Math.Max(peak, await BackendsAgainstShardAAsync(token));

        // 24 tenants, 720 concurrent writes, one shard. If each tenant had its own pool this would
        // be well above 5; if MaxPoolSize were dropped it would be up to Npgsql's default of 100.
        peak.ShouldBeGreaterThan(0, "the load did not actually open any connection, so this proves nothing");
        peak.ShouldBeLessThanOrEqualTo(5);
    }

    static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    async Task<int> BackendsAgainstShardAAsync(CancellationToken token) {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(fixture.Connections.Durable(StorageFixture.ShardA)) {
                ApplicationName = "cc-pool-observer", Pooling = false
            }.ConnectionString
        );

        await connection.OpenAsync(token);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name",
            connection
        );
        command.Parameters.AddWithValue("name", StorageFixture.ApplicationNamePrefix + StorageFixture.ShardA);

        return (int)(long)(await command.ExecuteScalarAsync(token))!;
    }
}
