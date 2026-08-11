using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     ⚠ <b>The revoke-then-stale-read bug class, end to end.</b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § Consistency names it exactly:
///         <i>
///             "an admin revokes a user's access, the UI
///             says done, and the user's next request is served from a cache and succeeds. Without a
///             token, the only fixes are 'never cache' or 'hope'."
///         </i>
///     </para>
///     <para>
///         So this file writes a tuple, revokes it, and then checks with
///         <b>
///             each of the three
///             modes
///         </b>
///         and asserts which of them see the revoke. The answer is not "all three", and
///         that is the point: <see cref="ConsistencyMode.MinimizeLatency" /> <b>still allows</b>,
///         because "any cached result" is what the document says it means. The fix is not to make
///         the fast mode safe — it is to make the unsafe mode's unsafety visible and to give the
///         enforcement path a mode that is not it.
///     </para>
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class ConsistencyTests(AuthorizationCluster cluster) {
    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    [Fact]
    public async Task TheThreeModesDisagreeAboutARevokeAndThatIsTheWholePoint() {
        var tenant = AuthorizationCluster.Tenant(100);
        var scope = Scope("revoke1");
        const string grant = "resourceGroup:revoke1#owner@user:alice";

        // 1 — the grant, and the token that covers it.
        var granted = await cluster.WriteAsync(tenant, grant);
        granted.Version.ShouldBeGreaterThan(0);

        // 2 — a normal request. This is what fills the cache.
        var beforeRevoke = await Allowed(tenant, scope, Consistency.MinimizeLatency);
        beforeRevoke.Allowed.ShouldBeTrue();

        // 3 — the admin revokes. The UI has a token that says "as of here, it is gone".
        var revoked = await cluster.RevokeAsync(tenant, grant);
        revoked.Version.ShouldBeGreaterThan(granted.Version);

        // 4 — ⚠ THE BUG, reproduced rather than fixed. MinimizeLatency serves the cached allow.
        var stale = await Allowed(tenant, scope, Consistency.MinimizeLatency);
        stale.Allowed.ShouldBeTrue(
            "docs/plan/07 § Consistency row 1 is 'any cached result'. If this ever starts failing, "
            + "MinimizeLatency has quietly become AtLeastAsFresh — which would be a latency "
            + "regression on every check in the platform, not a fix. Change it deliberately or not "
            + "at all."
        );
        stale.FromCache.ShouldBeTrue();
        stale.Token.Version.ShouldBe(granted.Version, "the cached answer is stamped as it was");

        // 5 — the portal passes the token the revoke returned. It sees the revoke.
        var fresh = await Allowed(tenant, scope, Consistency.AtLeastAsFresh(revoked));
        fresh.Allowed.ShouldBeFalse();
        fresh.FromCache.ShouldBeFalse();
        fresh.Token.Version.ShouldBe(revoked.Version);

        // 6 — and the enforcement path for anything destructive sees it regardless of any token.
        var enforced = await Allowed(tenant, scope, Consistency.FullyConsistent);
        enforced.Allowed.ShouldBeFalse();
        enforced.FromCache.ShouldBeFalse();
    }

    [Fact]
    public async Task OnceAnybodyAsksForFreshnessTheCacheStopsLyingToEverybody() {
        // The consolation prize, and worth asserting because it bounds the blast radius: the first
        // AtLeastAsFresh miss re-stamps the entry, so the NEXT MinimizeLatency request is correct.
        // The window is "until someone asks", not "forever".
        var tenant = AuthorizationCluster.Tenant(101);
        var scope = Scope("revoke2");
        const string grant = "resourceGroup:revoke2#owner@user:alice";

        await cluster.WriteAsync(tenant, grant);
        (await Allowed(tenant, scope, Consistency.MinimizeLatency)).Allowed.ShouldBeTrue();

        var revoked = await cluster.RevokeAsync(tenant, grant);

        (await Allowed(tenant, scope, Consistency.MinimizeLatency)).Allowed.ShouldBeTrue();
        (await Allowed(tenant, scope, Consistency.AtLeastAsFresh(revoked))).Allowed.ShouldBeFalse();

        var afterward = await Allowed(tenant, scope, Consistency.MinimizeLatency);
        afterward.Allowed.ShouldBeFalse();
        afterward.FromCache.ShouldBeTrue("the cache now holds the fresh answer");
    }

    [Fact]
    public async Task FullyConsistentNeverReadsACacheEvenWhenOneIsWarm() {
        var tenant = AuthorizationCluster.Tenant(102);
        var scope = Scope("fully1");

        await cluster.WriteAsync(tenant, "resourceGroup:fully1#owner@user:alice");

        (await Allowed(tenant, scope, Consistency.MinimizeLatency)).FromCache.ShouldBeFalse();
        (await Allowed(tenant, scope, Consistency.MinimizeLatency)).FromCache.ShouldBeTrue();

        // Warm cache, and it still does not read it. Three times, so this cannot pass by accident.
        for (var i = 0; i < 3; i++) {
            var result = await Allowed(tenant, scope, Consistency.FullyConsistent);
            result.FromCache.ShouldBeFalse();
            result.Allowed.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task FullyConsistentReadsTheDurableRowRatherThanTheActivation() {
        // ⚠ "Bypass all caches, read durable" (docs/plan/07 § Consistency, row 3) has a second half
        // that MinimizeLatency does not: the tuple grains are re-read from PostgreSQL rather than
        // answered from their in-memory state. This is observable because deactivating the object
        // grain and re-reading must produce the same answer AND because the durable read is what
        // makes a row changed by something other than this activation — a restore, a repair tool, a
        // migration — visible at all.
        var tenant = AuthorizationCluster.Tenant(103);
        var scope = Scope("fully2");

        await cluster.WriteAsync(tenant, "resourceGroup:fully2#owner@user:alice");
        await cluster.Objects(tenant, scope).DeactivateAsync();

        var result = await Allowed(tenant, scope, Consistency.FullyConsistent);

        result.Allowed.ShouldBeTrue();
        result.FromCache.ShouldBeFalse();
    }

    [Fact]
    public async Task AtLeastAsFreshWithNoTokenIsRefusedRatherThanDowngraded() {
        // Silently downgrading to MinimizeLatency would be the bug this whole section exists to
        // prevent, wearing the name of the fix.
        var tenant = AuthorizationCluster.Tenant(104);

        var result = await cluster.Check(tenant, Scope("token1"))
            .CheckAsync(
                Permissions.Read,
                Alice,
                new() { Mode = ConsistencyMode.AtLeastAsFresh, Token = null }
            );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        result.Error.Message.ShouldContain("revoke-then-stale-read");
    }

    [Fact]
    public async Task ATokenFromAnotherTenantIsRefused() {
        // A token is a PER-TENANT version. Comparing one tenant's against another's would make
        // freshness meaningless in whichever direction the numbers happened to fall.
        var (a, b) = (AuthorizationCluster.Tenant(105), AuthorizationCluster.Tenant(106));

        var tokenFromA = await cluster.WriteAsync(a, "resourceGroup:token2#owner@user:alice");

        var result = await cluster.Check(b, Scope("token2"))
            .CheckAsync(Permissions.Read, Alice, Consistency.AtLeastAsFresh(tokenFromA));

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
    }

    [Fact]
    public async Task TheTokenIsMonotonicAcrossEveryWriteAndRevoke() {
        var tenant = AuthorizationCluster.Tenant(107);
        long previous = -1;

        for (var i = 0; i < 5; i++) {
            var written = await cluster.WriteAsync(tenant, $"resourceGroup:mono{i}#owner@user:alice");

            written.Version.ShouldBeGreaterThan(previous);
            written.TenantId.ShouldBe(tenant);
            previous = written.Version;

            var revoked = await cluster.RevokeAsync(tenant, $"resourceGroup:mono{i}#owner@user:alice");

            revoked.Version.ShouldBeGreaterThan(previous);
            previous = revoked.Version;
        }
    }

    [Fact]
    public async Task AnAtLeastAsFreshCheckIsServedFromCacheOnceItsAnswerIsFreshEnough() {
        var tenant = AuthorizationCluster.Tenant(108);
        var scope = Scope("fresh1");

        var token = await cluster.WriteAsync(tenant, "resourceGroup:fresh1#owner@user:alice");

        var first = await Allowed(tenant, scope, Consistency.AtLeastAsFresh(token));
        first.FromCache.ShouldBeFalse();

        var second = await Allowed(tenant, scope, Consistency.AtLeastAsFresh(token));
        second.FromCache.ShouldBeTrue(
            "an entry stamped at or after the token is fresh enough; AtLeastAsFresh is not "
            + "FullyConsistent with extra steps"
        );
    }

    static ObjectRef Scope(string id) => ObjectRef.Of(ObjectTypes.ResourceGroup, id);

    async Task<CheckResult> Allowed(Guid tenant, ObjectRef scope, Consistency consistency) {
        var result = await cluster.Check(tenant, scope)
            .CheckAsync(Permissions.Read, Alice, consistency);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.GetValueOrThrow();
    }
}
