using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     ⚠ <b>Cache invalidation is deliberately crude — and it is per tenant.</b>
/// </summary>
/// <remarks>
///     docs/plan/07 § Caching across requests: <i>"The tenant relation version is bumped on every
///     tuple write, so a write invalidates the tenant's whole check cache. That is crude and it is
///     right: tuple writes are rare (role assignments), checks are constant, and a fine-grained
///     invalidation graph is a second consistency problem to get wrong."</i> Two things follow and
///     both are tested here: a write really does invalidate, and one tenant's write must not
///     invalidate another's.
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class CheckCacheInvalidationTests(AuthorizationCluster cluster)
{
    static ObjectRef Scope(string id) => ObjectRef.Of(ObjectTypes.ResourceGroup, id);

    [Fact]
    public async Task AWriteInvalidatesEveryCachedAnswerOnTheObject()
    {
        var tenant = AuthorizationCluster.Tenant(300);
        var scope = Scope("cache1");

        await cluster.WriteAsync(tenant, "resourceGroup:cache1#owner@user:alice");
        var second = await cluster.WriteAsync(tenant, "resourceGroup:cache1#reader@user:carol");

        await Ask(tenant, scope, "alice", Consistency.AtLeastAsFresh(second));
        await Ask(tenant, scope, "carol", Consistency.AtLeastAsFresh(second));

        (await cluster.Check(tenant, scope).CachedEntryCountAsync()).GetValueOrThrow().ShouldBe(2);

        // The write that invalidates.
        var third = await cluster.WriteAsync(tenant, "resourceGroup:cache1#reader@user:dave");

        var dave = await Ask(tenant, scope, "dave", Consistency.AtLeastAsFresh(third));
        dave.FromCache.ShouldBeFalse();

        // ⚠ Crude: alice's and carol's answers went too, even though neither was about dave.
        (await cluster.Check(tenant, scope).CachedEntryCountAsync()).GetValueOrThrow().ShouldBe(
            1,
            "a tuple write invalidates the whole cache, not the entries a graph says are affected");

        var alice = await Ask(tenant, scope, "alice", Consistency.MinimizeLatency);
        alice.FromCache.ShouldBeFalse("alice's cached answer was dropped by the unrelated write");
        alice.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task OneTenantsWriteDoesNotInvalidateAnothersCache()
    {
        // ⚠ The version is PER TENANT. If it were global, every role assignment anywhere in the
        // platform would cold-start every other tenant's check cache — which is the failure mode
        // that turns a crude-but-right invalidation into an outage.
        var (a, b) = (AuthorizationCluster.Tenant(310), AuthorizationCluster.Tenant(311));
        var scope = Scope("cache2");

        var tokenA = await cluster.WriteAsync(a, "resourceGroup:cache2#owner@user:alice");
        var tokenB = await cluster.WriteAsync(b, "resourceGroup:cache2#owner@user:bob");

        (await Ask(a, scope, "alice", Consistency.AtLeastAsFresh(tokenA))).FromCache.ShouldBeFalse();
        (await Ask(b, scope, "bob", Consistency.AtLeastAsFresh(tokenB))).FromCache.ShouldBeFalse();

        // Tenant A writes several more times.
        ConsistencyToken latestA = tokenA;
        for (var i = 0; i < 3; i++)
        {
            latestA = await cluster.WriteAsync(a, $"resourceGroup:cache2#reader@user:noise{i}");
        }

        // ⚠ Tenant B's cached answer is untouched, and it is still fresh enough for B's OWN token —
        // which is the assertion that matters, because B's token has not moved and A's has moved
        // three times. If the version were global, B's entry would now be stale.
        var bob = await Ask(b, scope, "bob", Consistency.AtLeastAsFresh(tokenB));
        bob.FromCache.ShouldBeTrue("tenant A's writes must not move tenant B's relation version");
        bob.Token.Version.ShouldBe(tokenB.Version);

        (await cluster.Check(b, scope).CachedEntryCountAsync()).GetValueOrThrow().ShouldBe(1);

        // …while tenant A's own entry is no longer fresh enough for A's latest token, which is what
        // says the two counters really are independent rather than both simply never moving.
        var stillStale = await Ask(a, scope, "alice", Consistency.MinimizeLatency);
        stillStale.FromCache.ShouldBeTrue(
            "MinimizeLatency takes any cached result, so the stale entry is still served — see "
            + "ConsistencyTests for what that costs");
        stillStale.Token.Version.ShouldBe(tokenA.Version);

        var fresh = await Ask(a, scope, "alice", Consistency.AtLeastAsFresh(latestA));
        fresh.FromCache.ShouldBeFalse("A's entry is stamped before A's latest write");
        fresh.Token.Version.ShouldBe(latestA.Version);
    }

    [Fact]
    public async Task TheTwoTenantsAreOnDifferentDurableShardsSoThisIsNotOneStorePretendingToBeTwo()
    {
        var (a, b) = cluster.SplitPair(320);

        cluster.DurableShardOf(a).ShouldNotBe(cluster.DurableShardOf(b));

        var tokenA = await cluster.WriteAsync(a, "resourceGroup:cache3#owner@user:alice");
        var tokenB = await cluster.WriteAsync(b, "resourceGroup:cache3#owner@user:alice");

        // Two independent counters, not one shared one.
        tokenA.TenantId.ShouldBe(a);
        tokenB.TenantId.ShouldBe(b);
        tokenA.Version.ShouldBe(1);
        tokenB.Version.ShouldBe(1);
    }

    [Fact]
    public async Task ATruncatedAnswerIsNeverWrittenToTheCache()
    {
        // A cap did not compute an answer. Caching "I gave up" would make one unlucky walk
        // permanent for as long as the tenant's version stands still — which for a read-mostly
        // tenant is a very long time.
        var tenant = AuthorizationCluster.Tenant(330);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "deep0");

        // Thirteen parent hops: one past the documented depth cap of 12.
        for (var i = 0; i < 13; i++)
        {
            await cluster.WriteAsync(tenant, $"resourceGroup:deep{i}#parent@resourceGroup:deep{i + 1}");
        }

        var token = await cluster.WriteAsync(tenant, "resourceGroup:deep13#owner@user:alice");

        var result = await cluster.Check(tenant, scope).CheckAsync(
            Permissions.Read,
            SubjectRef.Of(ObjectTypes.User, "alice"),
            Consistency.AtLeastAsFresh(token));

        var check = result.GetValueOrThrow();
        check.Allowed.ShouldBeFalse();
        check.Outcome.ShouldBe(CheckOutcome.DepthCapExceeded);
        check.CapDetail.ShouldNotBeNullOrEmpty();

        (await cluster.Check(tenant, scope).CachedEntryCountAsync()).GetValueOrThrow().ShouldBe(0);
    }

    async Task<CheckResult> Ask(Guid tenant, ObjectRef scope, string user, Consistency consistency)
    {
        var result = await cluster.Check(tenant, scope).CheckAsync(
            Permissions.Read, SubjectRef.Of(ObjectTypes.User, user), consistency);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.GetValueOrThrow();
    }
}
