using System.Globalization;
using CyberCloud.Core.Contracts;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Configuration;
using Orleans.Multitenant;
using Orleans.Storage;
using Shouldly;
using StackExchange.Redis;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The load-bearing claim of docs/plan/05 § Storage provider wiring, attacked: "a grain
///     physically <i>cannot</i> be stored on the wrong tenant's shard, because the key is what
///     selects the connection."
/// </summary>
/// <remarks>
///     <para>
///         State is written as tenant A and then reached for as tenant B by every route that can be
///         constructed from inside the silo. Six of the seven routes find nothing. The seventh finds
///         it, and that is the finding: a <b>raw, un-scoped grain key</b> is not a storage problem at
///         all — the storage layer is doing exactly what it promises, which is to obey the key — and
///         it is closed by <c>AddMultitenantCommunicationSeparation</c>, which is tenancy's task and
///         is not wired here. Asserting the hole rather than omitting it is the point.
///     </para>
/// </remarks>
[Collection(StorageSuite.Name)]
public sealed class CrossTenantReachabilityTests(StorageFixture fixture)
{
    const string SharedKeyWithinTenant = "res/the-same-key-in-both-tenants";
    const string Secret = "tenant-A-only-payload";

    static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Route1_TheSameKeyUnderTenantBIsADifferentGrainWithNoState()
    {
        var (a, b) = fixture.SplitTenants;

        await fixture.Grains.ForTenant(Id(a)).GetGrain<IDurableStateGrain>(SharedKeyWithinTenant).WriteAsync(Secret);

        var asB = fixture.Grains.ForTenant(Id(b)).GetGrain<IDurableStateGrain>(SharedKeyWithinTenant);

        (await asB.ExistsAsync()).ShouldBeFalse();
        (await asB.ReadAsync()).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Route2_TenantBsShardDatabaseDoesNotContainTenantAsBytes()
    {
        var token = TestContext.Current.CancellationToken;
        var (a, b) = fixture.SplitTenants;

        await fixture.Grains.ForTenant(Id(a)).GetGrain<IDurableStateGrain>("res/route2").WriteAsync(Secret);

        var shardOfB = fixture.ShardMap.DurableShardFor(Id(b));
        await using var connection = await fixture.OpenShardAsync(shardOfB, token);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM orleansstorage WHERE position(convert_to(@needle, 'UTF8') in payloadbinary) > 0",
            connection);
        command.Parameters.AddWithValue("needle", Secret);

        (await command.ExecuteScalarAsync(token)).ShouldBe(0L);
    }

    [Fact]
    public async Task Route3_TenantBsConfiguredConnectionStringPointsAtTenantBsServer()
    {
        // The structural half of route 2: it is not that B's provider looks and finds nothing, it is
        // that B's provider is holding a connection string for a different PostgreSQL server. Run
        // through the production configurator, not a re-implementation of it.
        var (a, b) = fixture.SplitTenants;

        var optionsForA = new AdoNetGrainStorageOptions();
        var optionsForB = new AdoNetGrainStorageOptions();
        fixture.Durable.ConfigureForTenant(optionsForA, Id(a));
        fixture.Durable.ConfigureForTenant(optionsForB, Id(b));

        optionsForA.ConnectionString.ShouldNotBe(optionsForB.ConnectionString);
        optionsForA.ConnectionString.ShouldBe(fixture.Connections.Durable(fixture.ShardMap.DurableShardFor(Id(a))));
        optionsForB.ConnectionString.ShouldBe(fixture.Connections.Durable(fixture.ShardMap.DurableShardFor(Id(b))));

        new NpgsqlConnectionStringBuilder(optionsForA.ConnectionString).Port
            .ShouldNotBe(new NpgsqlConnectionStringBuilder(optionsForB.ConnectionString).Port);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Route4_ForgingTheTenantInsideTheKeyDoesNotChangeTheTenant()
    {
        // ADR-002: Orleans.Multitenant copies the key WITHIN the tenant verbatim, so an inner key
        // that itself looks like "A|res/x" produces "B|A|res/x" — still tenant B, because the
        // terminator is the first undoubled '|' and the tenant id cannot contain one.
        var (a, b) = fixture.SplitTenants;

        await fixture.Grains.ForTenant(Id(a)).GetGrain<IDurableStateGrain>("res/route4").WriteAsync(Secret);

        var forged = fixture.Grains.ForTenant(Id(b)).GetGrain<IDurableStateGrain>($"{Id(a)}|res/route4");

        forged.GetGrainId().GetTenantId().ShouldBe(Id(b));
        (await forged.ExistsAsync()).ShouldBeFalse();
        (await forged.ReadAsync()).ShouldBe(string.Empty);
    }

    [Fact]
    public void Route5_TenantBsHotKeyBuilderRefusesAGrainThatBelongsToTenantA()
    {
        // The strongest form of "physically cannot": tenant B's hot-tier provider has no expression
        // in it that produces a key inside tenant A's hash tag — the tag is captured when the
        // provider is built. Feed it A's grain id and it throws rather than composing one.
        var (a, b) = fixture.SplitTenants;

        var keysForB = TenantHotKeys.For(fixture.ShardMap, Id(b));
        var grainOfA = fixture.Grains.ForTenant(Id(a)).GetGrain<IHotStateGrain>("res/route5").GetGrainId();

        Should.Throw<InvalidOperationException>(() => keysForB.Key("CyberCloud.Tests.Hot", grainOfA))
            .Message.ShouldContain(Id(a));
    }

    [Fact]
    public async Task Route6_TenantBsHashTagNamespaceContainsNoneOfTenantAsKeys()
    {
        var (a, b) = fixture.SplitTenants;

        await fixture.Grains.ForTenant(Id(a)).GetGrain<IHotStateGrain>("res/route6").WriteAsync(Secret);
        await fixture.Grains.ForTenant(Id(b)).GetGrain<IHotStateGrain>("res/route6").WriteAsync("B's own");

        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);

        var tagOfA = fixture.ShardMap.HotHashTagFor(Id(a));
        var tagOfB = fixture.ShardMap.HotHashTagFor(Id(b));
        tagOfA.ShouldNotBe(tagOfB);

        var keysOfA = new List<string>();
        await foreach (var key in server.KeysAsync(pattern: "{" + tagOfA + "}:*"))
        {
            keysOfA.Add(key.ToString());
        }

        var keysOfB = new List<string>();
        await foreach (var key in server.KeysAsync(pattern: "{" + tagOfB + "}:*"))
        {
            keysOfB.Add(key.ToString());
        }

        keysOfA.ShouldNotBeEmpty();
        keysOfB.ShouldNotBeEmpty();
        keysOfA.Intersect(keysOfB, StringComparer.Ordinal).ShouldBeEmpty();

        // And the slots are different, which is what makes them different shards on a real cluster.
        RedisHashSlot.Of(keysOfA[0]).ShouldNotBe(RedisHashSlot.Of(keysOfB[0]));
    }

    [Fact]
    public async Task Route7_ARawGrainKeyDOESReachTheOtherTenantAndOnlyCommunicationSeparationStopsIt()
    {
        // ⚠ THE FINDING. This is not a storage defect — storage is obeying the key, which is the
        // contract. It is the residual hole docs/plan/04 § Silo composition calls out when it says
        // AddMultitenantCommunicationSeparation is "not optional": anything holding an IGrainFactory
        // can address any tenant's grain by its raw physical key, and nothing in the storage layer
        // is positioned to notice.
        //
        // ⚠ ANSWERED, AND THE ANSWER IS "HALF". The separation is now wired — in
        // CyberCloud.Tenancy's AddCyberCloudTenantSeparation — and this silo still does not have it,
        // because THIS fixture wires storage only (OrleansApplication.CreateSilo's default path).
        // So both assertions below are still exactly true here, and they are still worth making:
        // they are what "storage separation without call separation" looks like.
        //
        // The inverted half lives in CyberCloud.Tenancy.Tests.CrossTenantReachabilityTests:
        //
        //   Route7a  from INSIDE a grain, the raw key now throws UnauthorizedAccessException with
        //            both tenant ids — docs/plan/04 § Silo composition's promise, delivered.
        //   Route7b  from OUTSIDE a grain, the raw key STILL reaches the state, even with the
        //            separation wired. Orleans.Multitenant's TenantSeparatingCallFilter returns
        //            without consulting the authorizer when context.SourceId is absent or is a
        //            client, so a cluster client, a gateway or a hosted service is not covered. That
        //            boundary is the gateway's (docs/plan/10) and does not exist yet.
        var a = fixture.SplitTenants.OnShardA;

        var owner = fixture.Grains.ForTenant(Id(a)).GetGrain<IDurableStateGrain>("res/route7");
        await owner.WriteAsync(Secret);

        var rawKey = owner.GetGrainId().Key.ToString();
        rawKey.ShouldNotBeNull();
        rawKey.ShouldStartWith(Id(a));

        var byRawKey = fixture.Grains.GetGrain<IDurableStateGrain>(rawKey);

        (await byRawKey.ReadAsync()).ShouldBe(
            Secret,
            "storage routes by the tenant in the key, so a caller that can name the key reaches the "
            + "state. AddMultitenantCommunicationSeparation is what makes naming it insufficient.");

        fixture.Services.GetServices<ICrossTenantAuthorizer>().ShouldBeEmpty(
            "if a cross-tenant authorizer has been registered, communication separation is on and "
            + "this test's expectation is now inverted — update it, do not delete it.");
    }

    [Fact]
    public async Task CotenantsOnOneShardAreSeparatedByTheKeyAndNothingElse()
    {
        // The case a single-shard test would miss and a two-shard test could accidentally skip: two
        // tenants whose rows are in the SAME PostgreSQL server. Separation here is entirely the
        // grain key, which is exactly what ADR-002 claims and is worth pinning.
        var token = TestContext.Current.CancellationToken;
        var (first, second) = fixture.CotenantsOnOneShard;

        var shard = fixture.ShardMap.DurableShardFor(Id(first));
        fixture.ShardMap.DurableShardFor(Id(second)).ShouldBe(shard);

        await fixture.Grains.ForTenant(Id(first)).GetGrain<IDurableStateGrain>("res/cotenant").WriteAsync("first's");
        await fixture.Grains.ForTenant(Id(second)).GetGrain<IDurableStateGrain>("res/cotenant").WriteAsync("second's");

        (await fixture.Grains.ForTenant(Id(first)).GetGrain<IDurableStateGrain>("res/cotenant").ReadAsync())
            .ShouldBe("first's");
        (await fixture.Grains.ForTenant(Id(second)).GetGrain<IDurableStateGrain>("res/cotenant").ReadAsync())
            .ShouldBe("second's");

        await using var connection = await fixture.OpenShardAsync(shard, token);
        await using var command = new NpgsqlCommand(
            "SELECT grainidextensionstring FROM orleansstorage WHERE grainidextensionstring LIKE '%res/cotenant' ORDER BY 1",
            connection);

        var keys = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                keys.Add(reader.GetString(0));
            }
        }

        keys.Count.ShouldBe(2);
        keys.ShouldContain(x => x.StartsWith(Id(first), StringComparison.Ordinal));
        keys.ShouldContain(x => x.StartsWith(Id(second), StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothTiersRouteByTheTenantInTheGrainKeyRatherThanByAmbientState()
    {
        // Belt and braces on the mechanism itself: MultitenantStorage picks the per-tenant provider
        // from grainId.GetTenantId(). Drive the provider directly with two grain ids and confirm the
        // two writes land in two shards.
        var token = TestContext.Current.CancellationToken;
        var (a, b) = fixture.SplitTenants;

        var storage = fixture.Services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable);

        var idA = fixture.Grains.ForTenant(Id(a)).GetGrain<IDurableStateGrain>("res/direct").GetGrainId();
        var idB = fixture.Grains.ForTenant(Id(b)).GetGrain<IDurableStateGrain>("res/direct").GetGrainId();

        await storage.WriteStateAsync(
            StorageFixture.ProviderTestGrainType,
            idA,
            new GrainState<NoteState>(new NoteState { Note = "direct-A" }));

        await storage.WriteStateAsync(
            StorageFixture.ProviderTestGrainType,
            idB,
            new GrainState<NoteState>(new NoteState { Note = "direct-B" }));

        (await CountOf("direct-A", fixture.ShardMap.DurableShardFor(Id(a)), token)).ShouldBe(1L);
        (await CountOf("direct-A", fixture.ShardMap.DurableShardFor(Id(b)), token)).ShouldBe(0L);
        (await CountOf("direct-B", fixture.ShardMap.DurableShardFor(Id(b)), token)).ShouldBe(1L);
        (await CountOf("direct-B", fixture.ShardMap.DurableShardFor(Id(a)), token)).ShouldBe(0L);
    }

    async Task<long> CountOf(string needle, string shard, CancellationToken token)
    {
        await using var connection = await fixture.OpenShardAsync(shard, token);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM orleansstorage WHERE position(convert_to(@needle, 'UTF8') in payloadbinary) > 0",
            connection);
        command.Parameters.AddWithValue("needle", needle);

        return (long)(await command.ExecuteScalarAsync(token))!;
    }
}
