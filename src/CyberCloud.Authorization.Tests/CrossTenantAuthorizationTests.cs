using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using CyberCloud.Core.Resources;
using Npgsql;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     The cross-tenant suite, extended to the authorization store:
///     <b>
///         tenant B must not be able to
///         check, read or write tenant A's tuples.
///     </b>
/// </summary>
/// <remarks>
///     <para>
///         The route numbering continues <c>CyberCloud.Tenancy.Tests.CrossTenantReachabilityTests</c>.
///         Routes 1–6 there are storage claims and routes 7–20 are the tenancy grains; these are the
///         four authorization grains, plus the two questions that are specific to this subsystem: can
///         another tenant's <i>token</i> be used, and can another tenant's <i>check</i> be made to
///         answer about the wrong object.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The residue from route 7b applies here unchanged and is worth restating for this
///             subsystem in particular.
///         </b>
///         <c>Orleans.Multitenant</c>'s filter never consults the
///         authorizer when the caller is not a grain, so a raw physical key from a cluster client
///         still reaches any tenant's grain. For an authorization store that residue is "everyone's
///         permissions", so the gateway boundary (docs/plan/10) matters more here than anywhere
///         else. <see cref="Route26_FromOutsideAGrainTheRawKeyIsStillOpen" /> asserts the limit
///         rather than leaving it to be assumed.
///     </para>
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class CrossTenantAuthorizationTests(AuthorizationCluster cluster) {
    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    [Fact]
    public async Task Route21_TenantBCannotReadTenantAsObjectRelationsFromInsideAGrain() {
        var (a, b) = (A(21), B(21));
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "secret21");

        await cluster.WriteAsync(a, "resourceGroup:secret21#owner@user:alice");

        var rawKey = cluster.Objects(a, scope).GetGrainId().Key.ToString()!;
        rawKey.ShouldStartWith(AuthorizationCluster.Id(a));

        var attacker = cluster.For(b).GetGrain<IAuthorizationReacherGrain>("rel/obj/resource/r21");
        (await attacker.MyTenantAsync()).ShouldBe(AuthorizationCluster.Id(b));

        var thrown =
            await Should.ThrowAsync<UnauthorizedAccessException>(() =>
                attacker.ReachObjectRelationsByRawKeyAsync(rawKey)
            );

        thrown.Message.ShouldContain(AuthorizationCluster.Id(a));
        thrown.Message.ShouldContain(AuthorizationCluster.Id(b));

        // And the tuple really was there to be read, or this test proves nothing.
        (await cluster.Objects(a, scope).ReadAsync()).GetValueOrThrow().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Route22_TenantBCannotReadTenantAsReverseIndexFromInsideAGrain() {
        var (a, b) = (A(22), B(22));

        await cluster.WriteAsync(a, "resourceGroup:secret22#owner@user:alice");

        var rawKey = cluster.SubjectIndex(a, Alice).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IAuthorizationReacherGrain>("rel/obj/resource/r22");

        var thrown =
            await Should.ThrowAsync<UnauthorizedAccessException>(() =>
                attacker.ReachSubjectRelationsByRawKeyAsync(rawKey)
            );

        thrown.Message.ShouldContain(AuthorizationCluster.Id(a));
    }

    [Fact]
    public async Task Route23_TenantBCannotRunACheckInTenantAsCheckGrain() {
        var (a, b) = (A(23), B(23));
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "secret23");

        await cluster.WriteAsync(a, "resourceGroup:secret23#owner@user:alice");

        var rawKey = cluster.Check(a, scope).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IAuthorizationReacherGrain>("rel/obj/resource/r23");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            attacker.ReachCheckByRawKeyAsync(rawKey, Permissions.Read, "user:alice")
        );
    }

    [Fact]
    public async Task Route24_TenantBCannotReadTenantAsRelationVersion() {
        // The version is not secret in itself, but it is the input to every freshness decision in
        // the tenant — and reading it across the boundary would be a rate of change signal about
        // another customer's administration.
        var (a, b) = (A(24), B(24));

        await cluster.WriteAsync(a, "resourceGroup:secret24#owner@user:alice");

        var rawKey = cluster.Store(a).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IAuthorizationReacherGrain>("rel/obj/resource/r24");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachTupleStoreByRawKeyAsync(rawKey));
    }

    [Fact]
    public async Task Route25_TenantBCannotWriteATupleIntoTenantAsStore() {
        // ⚠ The highest-severity direction. A read across the boundary discloses; a write across it
        // GRANTS — tenant B would be able to make itself an owner in tenant A.
        var (a, b) = (A(25), B(25));

        var rawKey = cluster.Store(a).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IAuthorizationReacherGrain>("rel/obj/resource/r25");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            attacker.WriteThroughTupleStoreByRawKeyAsync(rawKey, "resourceGroup:secret25#owner@user:mallory")
        );

        // Nothing landed.
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "secret25");
        (await cluster.Objects(a, scope).ReadAsync()).GetValueOrThrow().Count.ShouldBe(0);
        (await cluster.Store(a).GetTokenAsync()).GetValueOrThrow().Version.ShouldBe(0);
    }

    [Fact]
    public async Task Route26_FromOutsideAGrainTheRawKeyIsStillOpen() {
        // ⚠ THE LIMIT, asserted rather than assumed — the same one
        // CyberCloud.Tenancy.Tests § Route7b records. Orleans.Multitenant's filter returns without
        // consulting the authorizer when the caller is a client, and a test is a client. The
        // boundary that closes this is the gateway (docs/plan/10). Until it exists, an IGrainFactory
        // outside a grain is inside the trust boundary — and for THIS subsystem that means it can
        // read every tenant's grants.
        var a = A(26);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "secret26");

        await cluster.WriteAsync(a, "resourceGroup:secret26#owner@user:alice");

        var rawKey = cluster.Objects(a, scope).GetGrainId().Key.ToString()!;
        var reached = await cluster.Grains.GetGrain<IObjectRelationsGrain>(rawKey).ReadAsync();

        reached.IsSuccess.ShouldBeTrue(
            "a raw physical key from outside a grain context still reaches the state. If this ever "
            + "starts failing, communication separation has grown a client-side half — rewrite this "
            + "test to assert the new behaviour; do not delete it."
        );
        reached.GetValueOrThrow().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Route27_TheSameObjectIdInTwoTenantsIsTwoUnrelatedObjects() {
        // The ordinary shape of the attack, and the one that is not an exception: tenant B builds a
        // well-formed key from tenant A's object id under its own qualification. Allowed — and it
        // finds a different, empty object, because the tenant is in the physical key.
        var (a, b) = (A(27), B(27));
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "shared27");

        await cluster.WriteAsync(a, "resourceGroup:shared27#owner@user:alice");

        var asB = await cluster.Check(b, scope)
            .CheckAsync(Permissions.Read, Alice, Consistency.FullyConsistent);

        asB.GetValueOrThrow()
            .Allowed.ShouldBeFalse("tenant A's grant must not be visible under tenant B's qualification");

        (await cluster.Objects(b, scope).ReadAsync()).GetValueOrThrow().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Route28_TenantAsTuplesAreOnlyInTenantAsShardDatabase() {
        // "Tuples live in the durable tier, sharded by tenant" (docs/plan/07 § Storage), shown with
        // plain SQL against two different PostgreSQL servers rather than asserted.
        var (a, b) = cluster.SplitPair(5280);
        var token = TestContext.Current.CancellationToken;

        await cluster.WriteAsync(a, "resourceGroup:shard28#owner@user:alice");

        var key = cluster.Objects(a, ObjectRef.Of(ObjectTypes.ResourceGroup, "shard28"))
            .GetGrainId()
            .Key.ToString()!;

        var onA = await CountRows(cluster.DurableShardOf(a), key, token);
        var onB = await CountRows(cluster.DurableShardOf(b), key, token);
        var onPlatform = await CountRows(AuthorizationCluster.PlatformShard, key, token);

        onA.ShouldBe(1);
        onB.ShouldBe(0, "the other tenant's PostgreSQL server does not contain these bytes");
        onPlatform.ShouldBe(0, "no authorization grain is null-tenant");
    }

    [Fact]
    public void Route29_ThereIsNoNullTenantAuthorizationGrainKey() {
        // Every ReBAC key shape is tenant-qualified. If one were not, a single activation would hold
        // two customers' relations and the whole sharding argument would collapse to a comment.
        GrainKeys.ObjectRelations("resourceGroup", "x").ShouldStartWith("rel/obj/");
        GrainKeys.SubjectRelations("user", "x").ShouldStartWith("rel/sub/");
        GrainKeys.CheckCache("resourceGroup", "x").ShouldStartWith("rel/check/");
        GrainKeys.TupleStore(Guid.NewGuid()).ShouldStartWith("rel/store/");

        GrainKeys.PlatformSingletons.ShouldNotContain(x =>
            x.Contains("rel", StringComparison.Ordinal)
        );
    }

    static Guid A(int n) => AuthorizationCluster.Tenant(5000 + n);

    static Guid B(int n) => AuthorizationCluster.Tenant(6000 + n);

    async Task<long> CountRows(string shard, string physicalKey, CancellationToken cancellationToken) {
        await using var connection = await cluster.OpenShardAsync(shard, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM orleansstorage WHERE grainidextensionstring = @key",
            connection
        );

        command.Parameters.AddWithValue("key", physicalKey);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
