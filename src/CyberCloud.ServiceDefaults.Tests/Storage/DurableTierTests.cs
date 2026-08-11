using System.Globalization;
using CyberCloud.Core.Contracts;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Multitenant;
using Orleans.Storage;
using Shouldly;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The durable tier against two real PostgreSQL servers: the row lands on the tenant's shard and
///     nowhere else, it is readable JSON, and a stale etag loses.
/// </summary>
[Collection(StorageSuite.Name)]
public sealed class DurableTierTests(StorageFixture fixture)
{
    static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AGrainsRowExistsOnItsOwnShardAndOnNoOther()
    {
        var token = TestContext.Current.CancellationToken;
        var (a, b) = fixture.SplitTenants;

        await fixture.Grains.ForTenant(Id(a)).GetGrain<IDurableStateGrain>("res/split").WriteAsync("A's row");
        await fixture.Grains.ForTenant(Id(b)).GetGrain<IDurableStateGrain>("res/split").WriteAsync("B's row");

        var shardOfA = fixture.ShardMap.DurableShardFor(Id(a));
        var shardOfB = fixture.ShardMap.DurableShardFor(Id(b));
        shardOfA.ShouldNotBe(shardOfB);

        // The grain key within the tenant is identical for both. If placement were not by tenant,
        // one would have overwritten the other.
        (await RowsOn(shardOfA, token)).ShouldContain(x => x.Contains("A's row", StringComparison.Ordinal));
        (await RowsOn(shardOfA, token)).ShouldNotContain(x => x.Contains("B's row", StringComparison.Ordinal));
        (await RowsOn(shardOfB, token)).ShouldContain(x => x.Contains("B's row", StringComparison.Ordinal));
        (await RowsOn(shardOfB, token)).ShouldNotContain(x => x.Contains("A's row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStoredPayloadIsHumanReadableJsonWhenReadWithPlainSql()
    {
        // docs/plan/05 § Serialization: the entire stated reason for choosing JSON over MemoryPack
        // for this tier is that in year two someone answers "what did this look like before the bad
        // deploy" with psql. That is a testable claim, so it is a test.
        var token = TestContext.Current.CancellationToken;
        var tenant = StorageFixture.Tenant(41);

        // ⚠ The apostrophe and the umlaut are the test, not decoration. System.Text.Json's DEFAULT
        // encoder writes them as ' and ü, which is valid JSON that nobody can read in a
        // psql window — and readability in a psql window is the entire stated reason this tier is
        // JSON rather than MemoryPack (docs/plan/05 § Serialization).
        await fixture.Grains.ForTenant(Id(tenant))
            .GetGrain<IDurableStateGrain>("res/readable")
            .WriteAsync("Müller's database, a resource nobody has to decompile");

        var shard = fixture.ShardMap.DurableShardFor(Id(tenant));
        await using var connection = await fixture.OpenShardAsync(shard, token);

        // ⚠ The column is PayloadBinary. docs/plan/05 § Durable lists the schema as "(GrainIdHash,
        // …, PayloadBinary, PayloadJson, ModifiedOn, Version)" — there is NO PayloadJson column in
        // Orleans 10.2.2's PostgreSQL schema, and the provider reads and writes only PayloadBinary.
        // JSON-ness comes from the serializer, not from the column.
        await using var command = new NpgsqlCommand(
            "SELECT convert_from(payloadbinary, 'UTF8') FROM orleansstorage WHERE grainidextensionstring LIKE '%res/readable'",
            connection);

        var payload = (string?)await command.ExecuteScalarAsync(token);

        payload.ShouldNotBeNull();
        payload.ShouldStartWith("{");
        payload.ShouldContain("\"Note\":\"Müller's database, a resource nobody has to decompile\"");
        payload.ShouldContain("\"Revision\":1");
        payload.ShouldNotContain("\\u", Case.Sensitive);
    }

    [Fact]
    public async Task TheSchemaHasNoPayloadJsonColumnWhateverThePlanSays()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await fixture.OpenShardAsync(StorageFixture.ShardA, token);
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'orleansstorage' ORDER BY column_name",
            connection);

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                columns.Add(reader.GetString(0));
            }
        }

        columns.ShouldContain("payloadbinary");
        columns.ShouldContain("version");
        columns.ShouldContain("serviceid");
        columns.ShouldNotContain("payloadjson");
    }

    [Fact]
    public async Task AStaleEtagLosesWithInconsistentStateExceptionRatherThanWinningQuietly()
    {
        // Two writers, one grain, one stale view of the version. Driven through the storage provider
        // rather than the grain because a grain will not let you hold a stale IGrainState — and the
        // etag is the mechanism that makes that safe, so it is the mechanism under test.
        var tenant = StorageFixture.Tenant(52);
        var grainId = fixture.Grains.ForTenant(Id(tenant))
            .GetGrain<IDurableStateGrain>("res/etag")
            .GetGrainId();

        var storage = fixture.Services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable);

        var first = new GrainState<NoteState>(new NoteState { Note = "first" });
        await storage.WriteStateAsync(StorageFixture.ProviderTestGrainType, grainId, first);
        first.ETag.ShouldNotBeNull();

        var stale = new GrainState<NoteState>(new NoteState { Note = "stale" }) { ETag = null };

        var thrown = await Should.ThrowAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(StorageFixture.ProviderTestGrainType, grainId, stale));

        thrown.Message.ShouldContain("Version conflict");

        var readBack = new GrainState<NoteState>(new NoteState());
        await storage.ReadStateAsync(StorageFixture.ProviderTestGrainType, grainId, readBack);
        readBack.State.Note.ShouldBe("first");
    }

    [Fact]
    public async Task ANullTenantGrainReachesTheConfiguredPlatformShardInsteadOfThrowing()
    {
        // docs/plan/04 § Grain taxonomy makes the tenant directory, the shard map and the provider
        // registry null-tenant AND durable. docs/plan/05 § Storage provider wiring's body would
        // throw FormatException on the first activation of any of them.
        var token = TestContext.Current.CancellationToken;
        var grain = fixture.Grains.GetGrain<IDurableStateGrain>("platform/shard-map");

        await grain.WriteAsync("the platform's own state");
        (await grain.ReadAsync()).ShouldBe("the platform's own state");

        fixture.Durable.ShardPerTenant.ShouldContainKey("Null");
        fixture.Durable.ShardPerTenant["Null"].ShouldBe(StorageFixture.ShardA);

        (await RowsOn(StorageFixture.ShardA, token))
            .ShouldContain(x => x.Contains("the platform's own state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDurableProviderIsTheMultitenantWrapperAndNotABarePostgresProvider()
    {
        var storage = fixture.Services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable);

        // If this ever becomes AdoNetGrainStorage, every tenant shares one connection string and the
        // whole sharding story is gone — with no other symptom.
        storage.GetType().Name.ShouldBe("MultitenantStorage");

        await Task.CompletedTask;
    }

    async Task<List<string>> RowsOn(string shard, CancellationToken token)
    {
        await using var connection = await fixture.OpenShardAsync(shard, token);
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(convert_from(payloadbinary, 'UTF8'), '') FROM orleansstorage",
            connection);

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
