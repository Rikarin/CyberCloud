using CyberCloud.Core.Resources;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Multitenant;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     The <c>"Null"</c> tenant trap, on the live path it actually sits on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The trap.</b> docs/plan/05 § Storage provider wiring's five-line
///         <c>configureTenantOptions</c> body opens with <c>Guid.Parse(tenantId)</c>.
///         <c>Orleans.Multitenant</c> passes
///         <c>MultitenantStorageOptions.TenantIdForNullTenant</c> — the literal string
///         <c>"Null"</c> — for a grain with no tenant qualification, and <c>Guid.Parse("Null")</c>
///         throws. Every grain in the Platform row of docs/plan/04 § Grain taxonomy is null-tenant
///         <b>and</b> durable, so this is not a theoretical edge: it is the first activation of the
///         tenant directory.
///     </para>
///     <para>
///         These tests do the thing the trap breaks — round-trip a null-tenant durable grain through
///         the real storage tier — and then check the row is in the shard the configuration named.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class NullTenantGrainTests(TenancyCluster cluster)
{
    [Fact]
    public void TheTrapIsRealAndTheSentinelIsTheDefault()
    {
        // Asserted against the library rather than against a comment, so that a change in
        // Orleans.Multitenant's default is a failing test rather than a silent behaviour change.
        new MultitenantStorageOptions().TenantIdForNullTenant.ShouldBe("Null");

        Should.Throw<FormatException>(
            () => Guid.Parse(new MultitenantStorageOptions().TenantIdForNullTenant, null));
    }

    [Fact]
    public void TheShardMapRoutesTheSentinelToTheConfiguredPlatformShard()
    {
        // The repair: IShardMapCache takes a string, and DurableTierOptions.NullTenantShard names
        // the shard. No parse, no throw.
        cluster.ShardMap.DurableShardFor("Null").ShouldBe(TenancyCluster.PlatformShard);

        // …and it is the shard the storage layer will actually use, through the production
        // configurator rather than a re-implementation of it.
        var options = new Orleans.Configuration.AdoNetGrainStorageOptions();
        cluster.Services.GetRequiredService<DurableTierConfigurator>()
            .ConfigureForTenant(options, "Null");

        options.ConnectionString.ShouldBe(
            cluster.Connections.Durable(TenancyCluster.PlatformShard));
    }

    [Fact]
    public async Task ANullTenantDurableGrainRoundTripsThroughTheStorageTier()
    {
        // ⚠ THE TEST THE TRAP WOULD FAIL. Two writes and two reads through the shard map grain and
        // the tenant directory grain, each of them null-tenant and durable, with a deactivation in
        // between so the read really comes from PostgreSQL.
        var tenant = TenancyCluster.Tenant(31_001);

        var assigned = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow();

        (await cluster.DirectoryGrain().RegisterAsync(new TenantDirectoryEntry
        {
            TenantId = tenant,
            Slug = "null-tenant-probe",
            HomeRegion = "eu-central",
            HotShard = assigned.HotHashTag,
            DurableShard = assigned.DurableShard,
            Status = TenantStatus.Active,
        })).IsSuccess.ShouldBeTrue();

        await cluster.ShardMapGrain().DeactivateAsync();
        await cluster.DirectoryGrain().DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        (await cluster.ShardMapGrain().GetAssignmentAsync(tenant)).GetValueOrThrow()
            .ShouldBe(assigned);
        (await cluster.DirectoryGrain().LookupAsync(tenant)).GetValueOrThrow().Slug
            .ShouldBe("null-tenant-probe");
    }

    [Fact]
    public async Task TheRowsForANullTenantGrainAreInThePlatformShardAndNowhereElse()
    {
        var token = TestContext.Current.CancellationToken;
        var tenant = TenancyCluster.Tenant(31_002);

        (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central")).IsSuccess.ShouldBeTrue();
        (await cluster.DirectoryGrain().RegisterAsync(new TenantDirectoryEntry
        {
            TenantId = tenant, Slug = "null-tenant-rows", HomeRegion = "eu-central",
            Status = TenantStatus.Active,
        })).IsSuccess.ShouldBeTrue();

        (await CountKeys(TenancyCluster.PlatformShard, GrainKeys.ShardMap(), token))
            .ShouldBe(1L, "the shard map's row is in the platform shard.");
        (await CountKeys(TenancyCluster.PlatformShard, GrainKeys.TenantDirectory(), token))
            .ShouldBe(1L, "the directory's row is in the platform shard.");

        foreach (var shard in TenancyCluster.TenantShards)
        {
            (await CountKeys(shard, GrainKeys.ShardMap(), token)).ShouldBe(
                0L, $"no platform grain may be stored in tenant shard '{shard}'.");
            (await CountKeys(shard, GrainKeys.TenantDirectory(), token)).ShouldBe(0L);
        }
    }

    [Fact]
    public async Task ANullTenantGrainsPhysicalKeyCarriesNoTenantPrefix()
    {
        // The other half of "null tenant": the key is the key, with no qualification in front of it.
        // A tenanted spelling would be a second activation of a thing there is exactly one of.
        var map = cluster.ShardMapGrain();

        map.GetGrainId().Key.ToString().ShouldBe(GrainKeys.ShardMap());
        map.GetGrainId().GetTenantId().ShouldBeNull();

        cluster.DirectoryGrain().GetGrainId().Key.ToString().ShouldBe(GrainKeys.TenantDirectory());

        await Task.CompletedTask;
    }

    [Fact]
    public async Task TenantQualifyingAPlatformGrainIsRefusedRatherThanSilentlyForked()
    {
        // The mistake that would give the platform one shard map per tenant. It has to fail loudly:
        // a second shard map is a second answer to "which database is this tenant in".
        var forked = cluster.For(TenancyCluster.Tenant(31_003))
            .GetGrain<IShardMapGrain>(GrainKeys.ShardMap());

        var thrown = await Should.ThrowAsync<Exception>(() => forked.GetSnapshotAsync(0));

        thrown.ToString().ShouldContain("null-tenant");
    }

    [Fact]
    public async Task APlatformGrainReachedByASecondKeyStringIsRefused()
    {
        // There is exactly one of each of these, and "exactly one" is enforced by the key. A second
        // accepted key string would be a second activation.
        var wrongKey = cluster.Grains.GetGrain<IShardMapGrain>("platform/tenant-directory");

        var thrown = await Should.ThrowAsync<Exception>(() => wrongKey.GetSnapshotAsync(0));

        thrown.ToString().ShouldContain("platform/shard-map");
    }

    [Fact]
    public void TheNullTenantShardIsNotInTheTenantPlacementRotation()
    {
        // ⚠ Not tidiness. Between process start and the first refresh, the cache places unassigned
        // tenants over its configured list; the shard map grain places over ITS list. If the
        // platform shard were in one and not the other, a tenant touched in that window would land
        // on one shard and be recorded on another.
        cluster.ShardMap.Shards.ShouldNotContain(TenancyCluster.PlatformShard);
        cluster.ShardMap.Shards.ShouldBe(TenancyCluster.TenantShards, ignoreOrder: true);
    }

    async Task<long> CountKeys(string shard, string keyWithinTenant, CancellationToken token)
    {
        await using var connection = await cluster.OpenShardAsync(shard, token);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM orleansstorage WHERE grainidextensionstring = @key",
            connection);

        command.Parameters.AddWithValue("key", keyWithinTenant);

        return (long)(await command.ExecuteScalarAsync(token))!;
    }
}
