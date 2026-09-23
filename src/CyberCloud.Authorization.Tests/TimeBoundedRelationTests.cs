using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     Just-in-time roles, through the real grains — docs/plan/07 § Time-bounded relations, issue #49.
/// </summary>
/// <remarks>
///     <para>
///         Everything here runs against one real silo, real PostgreSQL shards, and the real Redis the
///         hot tier and the reminders share, with <see cref="MovableClock" /> as the silo's
///         <c>IClock</c>. Nothing waits: a grant is written to end an hour from the clock's start,
///         and the test moves the clock. What that exercises is the production filter in
///         <c>ObjectRelationsGrain</c>, the production cache comparison in <c>CheckGrain</c>, the
///         production closure rule in the maintainer, and the production sweep — not a shortened
///         copy of any of them.
///     </para>
///     <para>
///         ⚠ <b>Every test puts the clock back.</b> The collection shares one silo, and a clock left
///         an hour ahead would expire the next test's grant before it was checked.
///     </para>
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class TimeBoundedRelationTests(AuthorizationCluster cluster) {
    static readonly DateTimeOffset Start = MovableClock.Start;
    static readonly DateTimeOffset InAnHour = Start.AddHours(1);

    static readonly string[] BothResources = ["jit-f-r1", "jit-f-r2"];
    static readonly string[] TheGroup = ["jit-g"];

    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    // ── Allowed, then denied, and nothing wrote in between ─────────────────────────────────────

    [Fact]
    public async Task AGrantWithAnExpiryAllowsUntilTheClockReachesItAndThenDeniesWithNoWrite() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4900);
                var scope = Group("jit-a");

                var granted = await WriteAsync(tenant, "resourceGroup:jit-a#reader@user:alice", InAnHour);

                var allowed = await CheckAsync(tenant, scope, Consistency.FullyConsistent);
                allowed.Allowed.ShouldBeTrue();
                allowed.ValidUntil.ShouldBe(InAnHour, "the allow rests on one tuple, and that tuple ends then");

                cluster.Clock.UtcNow = InAnHour - TimeSpan.FromTicks(1);
                (await CheckAsync(tenant, scope, Consistency.FullyConsistent)).Allowed.ShouldBeTrue(
                    "a tick before its expiry the grant still holds"
                );

                cluster.Clock.UtcNow = InAnHour;
                var denied = await CheckAsync(tenant, scope, Consistency.FullyConsistent);
                denied.Allowed.ShouldBeFalse(
                    "the expiry instant is the first instant the grant no longer holds — TupleExpiry.IsLive"
                );

                // ⚠ AND NOTHING WROTE. The deny above came from the clock alone: the tenant's relation
                // version is exactly the one the grant's write returned, so no revoke, no sweep and no
                // cache invalidation had any part in it.
                var token = (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow();
                token.Version.ShouldBe(granted.Version, "a tuple expiring is not a write, and moved no version");
                denied.Token.Version.ShouldBe(granted.Version);
            }
        );
    }

    [Fact]
    public async Task ATokenMintedBeforeTheExpiryIsSatisfiedAfterItAndReadsTheGrantAsGone() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4901);
                var scope = Group("jit-b");

                // ⚠ The zookie a portal holds right after the grant — and still holds an hour later.
                var token = await WriteAsync(tenant, "resourceGroup:jit-b#reader@user:alice", InAnHour);
                (await CheckAsync(tenant, scope, Consistency.AtLeastAsFresh(token))).Allowed.ShouldBeTrue();

                cluster.Clock.UtcNow = InAnHour;

                // A token is a lower bound on the writes an answer reflects, never a point in time to
                // read at; there is no revision at which the tuple is still live to go back to. So the
                // same token, presented after the expiry, is satisfied — and the grant is gone.
                var after = await CheckAsync(tenant, scope, Consistency.AtLeastAsFresh(token));
                after.Allowed.ShouldBeFalse(
                    "a consistency token minted while the grant was live kept it alive past its expiry — "
                    + "docs/plan/07 § Time-bounded relations says a token cannot"
                );
                after.Token.Version.ShouldBe(token.Version);
            }
        );
    }

    // ── The cache rule ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACachedAllowIsServedUntilTheExpiryInstantAndNotATickLongerInAnyMode() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4902);
                var scope = Group("jit-c");

                await WriteAsync(tenant, "resourceGroup:jit-c#reader@user:alice", InAnHour);

                var walked = await CheckAsync(tenant, scope, Consistency.MinimizeLatency);
                walked.FromCache.ShouldBeFalse();
                walked.ValidUntil.ShouldBe(InAnHour);

                var cached = await CheckAsync(tenant, scope, Consistency.MinimizeLatency);
                cached.FromCache.ShouldBeTrue("the second identical check is the cache's");
                cached.Allowed.ShouldBeTrue();
                cached.ValidUntil.ShouldBe(InAnHour, "the entry carries the instant it was proved until");

                cluster.Clock.UtcNow = InAnHour - TimeSpan.FromTicks(1);
                var lastTick = await CheckAsync(tenant, scope, Consistency.MinimizeLatency);
                lastTick.FromCache.ShouldBeTrue("a tick before the expiry the cached allow is still true");
                lastTick.Allowed.ShouldBeTrue();

                // ⚠ THE RULE. MinimizeLatency is "any cached result" and no write has moved the
                // version, so nothing but the entry's own instant can stop it being served here — and
                // a memoised allow that outlived its grant is the whole failure JIT roles can have.
                cluster.Clock.UtcNow = InAnHour;
                var atExpiry = await CheckAsync(tenant, scope, Consistency.MinimizeLatency);
                atExpiry.FromCache.ShouldBeFalse(
                    "the check cache served an allow at the instant the tuple that proved it expired"
                );
                atExpiry.Allowed.ShouldBeFalse();
                atExpiry.ValidUntil.ShouldBeNull("a deny with no negation under it can't change by expiring");
            }
        );
    }

    [Fact]
    public async Task AnAllowWithALongerLivedSecondDerivationIsReWalkedAtTheExpiryAndStaysAllowed() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4903);
                var resource = ObjectRef.Of(ObjectTypes.Resource, "jit-d-r1");

                // A just-in-time Reader directly on the resource — the first arm the walk tries — and
                // a permanent Reader on its group, which it reaches only through `parent`.
                await cluster.WriteAsync(tenant, "resource:jit-d-r1#parent@resourceGroup:jit-d");
                await cluster.WriteAsync(tenant, "resourceGroup:jit-d#reader@user:alice");
                await WriteAsync(tenant, "resource:jit-d-r1#reader@user:alice", InAnHour);

                var first = await CheckAsync(tenant, resource, Consistency.MinimizeLatency);
                first.Allowed.ShouldBeTrue();
                first.ValidUntil.ShouldBe(
                    InAnHour,
                    "the walk short-circuits on the direct tuple, so the instant is that tuple's — early, "
                    + "which is allowed, and this test is what shows early is harmless"
                );

                cluster.Clock.UtcNow = InAnHour;

                var rewalked = await CheckAsync(tenant, resource, Consistency.MinimizeLatency);
                rewalked.FromCache.ShouldBeFalse("the cached entry ended with the tuple that proved it");
                rewalked.Allowed.ShouldBeTrue("Reader on the group still reaches the resource");
                rewalked.ValidUntil.ShouldBeNull("the derivation left is permanent");
            }
        );
    }

    [Fact]
    public async Task AnExpiringSuspensionDeniesAssignRoleUntilItEndsAndTheCachedDenyEndsWithIt() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4904);
                var scope = Group("jit-e");

                // `assignRole` is Rel(owner) & !Rel(suspended): the one shape where time can turn a
                // deny into an allow, so the one deny the cache must not keep.
                await cluster.WriteAsync(tenant, "resourceGroup:jit-e#owner@user:alice");
                await WriteAsync(tenant, "resourceGroup:jit-e#suspended@user:alice", InAnHour);

                var denied = await CheckAsync(tenant, scope, Consistency.MinimizeLatency, Permissions.AssignRole);
                denied.Allowed.ShouldBeFalse();
                denied.ValidUntil.ShouldBe(InAnHour, "the deny rests on a suspension that ends then");

                (await CheckAsync(tenant, scope, Consistency.MinimizeLatency, Permissions.AssignRole))
                    .FromCache.ShouldBeTrue();

                cluster.Clock.UtcNow = InAnHour;

                var allowed = await CheckAsync(tenant, scope, Consistency.MinimizeLatency, Permissions.AssignRole);
                allowed.FromCache.ShouldBeFalse("a cached deny outlived the suspension that proved it");
                allowed.Allowed.ShouldBeTrue("the owner is no longer suspended");
            }
        );
    }

    // ── ListObjects ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListObjectsDropsAnExpiredGrantAtTheSameInstantACheckDoes() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4905);

                await cluster.WriteAsync(tenant, "resourceGroup:jit-f#parent@subscription:jit-f-sub");
                await cluster.WriteAsync(tenant, "resource:jit-f-r1#parent@resourceGroup:jit-f");
                await cluster.WriteAsync(tenant, "resource:jit-f-r2#parent@resourceGroup:jit-f");

                await WriteAsync(tenant, "resourceGroup:jit-f#reader@user:alice", InAnHour);

                (await ListAsync(tenant, Alice)).ShouldBe(BothResources);

                cluster.Clock.UtcNow = InAnHour;

                (await ListAsync(tenant, Alice)).ShouldBeEmpty(
                    "the listing still reached resources through a grant every check has stopped honouring"
                );
            }
        );
    }

    // ── The membership index ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExpiringGroupMembershipIsWalkedRatherThanClosedAndStopsGrantingOnTime() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4906);
                var scope = Group("jit-g");
                var eng = ObjectRef.Of(ObjectTypes.Group, "jit-g-eng");

                await cluster.WriteAsync(tenant, "resourceGroup:jit-g#reader@group:jit-g-eng#member");
                await WriteAsync(tenant, "group:jit-g-eng#member@user:alice", InAnHour);

                // ⚠ The closure has no clock, so the edge is not in it — and the userset says so.
                var slice = (await cluster.Index(tenant, eng).ReadAsync()).GetValueOrThrow();
                slice.MembersOf(Relations.Member).ShouldNotContain(Alice, "an expiring edge was closed over");
                slice.IsUnclosed(Relations.Member).ShouldBeTrue("the userset's closure left an edge out and doesn't say so");

                var allowed = await CheckAsync(tenant, scope, Consistency.MinimizeLatency);
                allowed.Allowed.ShouldBeTrue("the walk sees the live membership the closure left out");
                allowed.ValidUntil.ShouldBe(InAnHour, "the membership the walk crossed ends then");

                (await ListGroupsAsync(tenant, Alice)).ShouldBe(TheGroup);

                cluster.Clock.UtcNow = InAnHour;

                (await CheckAsync(tenant, scope, Consistency.MinimizeLatency)).Allowed.ShouldBeFalse();
                (await ListGroupsAsync(tenant, Alice)).ShouldBeEmpty();
            }
        );
    }

    [Fact]
    public async Task ShorteningAPermanentMembershipTakesItOutOfTheClosure() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4907);
                var scope = Group("jit-h");
                var eng = ObjectRef.Of(ObjectTypes.Group, "jit-h-eng");

                await cluster.WriteAsync(tenant, "resourceGroup:jit-h#reader@group:jit-h-eng#member");
                await cluster.WriteAsync(tenant, "group:jit-h-eng#member@user:alice");

                (await cluster.Index(tenant, eng).ReadAsync()).GetValueOrThrow()
                    .MembersOf(Relations.Member).ShouldContain(Alice, "the permanent membership is closed over");

                // ⚠ THE REWRITE THAT NEEDS A RECOMPUTE. The same tuple, now ending in an hour. A union
                // can't take a member out of a closure; without TupleStoreGrain's step 2 on a rewrite,
                // the index would keep answering "yes" for Alice after the hour — from the fast path,
                // with no walk to notice.
                await WriteAsync(tenant, "group:jit-h-eng#member@user:alice", InAnHour);

                var slice = (await cluster.Index(tenant, eng).ReadAsync()).GetValueOrThrow();
                slice.MembersOf(Relations.Member).ShouldNotContain(Alice, "the shortened membership is still closed over");
                slice.IsUnclosed(Relations.Member).ShouldBeTrue();

                (await CheckAsync(tenant, scope, Consistency.MinimizeLatency)).Allowed.ShouldBeTrue();

                cluster.Clock.UtcNow = InAnHour;

                (await CheckAsync(tenant, scope, Consistency.MinimizeLatency)).Allowed.ShouldBeFalse(
                    "a membership shortened to an hour still granted after the hour"
                );

                // And made permanent again, the closure takes it back and the mark goes.
                cluster.Clock.UtcNow = Start;
                await cluster.WriteAsync(tenant, "group:jit-h-eng#member@user:alice");

                var restored = (await cluster.Index(tenant, eng).ReadAsync()).GetValueOrThrow();
                restored.MembersOf(Relations.Member).ShouldContain(Alice);
                restored.IsUnclosed(Relations.Member).ShouldBeFalse("nothing expiring is left beneath the userset");
            }
        );
    }

    // ── The sweep ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSweepDeletesAnExpiredTupleAuditsItAndDisarmsOnceNothingIsLeft() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4908);
                var scope = Group("jit-i");
                var later = Start.AddHours(3);

                await WriteAsync(tenant, "resourceGroup:jit-i#reader@user:alice", InAnHour);
                await WriteAsync(tenant, "resourceGroup:jit-i#contributor@user:bob", later);

                // Before anything has expired: nothing to remove, both registered, the reminder armed.
                var idle = await SweepAsync(tenant);
                idle.Removed.ShouldBe(0);
                idle.Remaining.ShouldBe(2);
                idle.Armed.ShouldBeTrue("a tenant with expiring grants has no reminder to sweep them");

                cluster.Clock.UtcNow = InAnHour;
                var before = (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow();

                var swept = await SweepAsync(tenant);
                swept.Removed.ShouldBe(1);
                swept.Remaining.ShouldBe(1, "Bob's grant has two hours left");
                swept.Armed.ShouldBeTrue();
                swept.Failed.ShouldBe(0);

                (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow().Version.ShouldBeGreaterThan(
                    before.Version,
                    "the sweep's delete is a delete — journalled, applied, and covered by a new version"
                );

                // ⚠ THE AUDIT EVENT: when the grant ended and when storage caught up, as fields.
                var removed = cluster.Audit.Events.Where(static e => e.Id == 1701).ToList();
                removed.ShouldContain(
                    e => (string?)e.Fields["Tuple"] == "resourceGroup:jit-i#reader@user:alice"
                        && (DateTimeOffset?)e.Fields["ExpiresOn"] == InAnHour
                        && (DateTimeOffset?)e.Fields["SweptAt"] == InAnHour
                        && (Guid?)e.Fields["TenantId"] == tenant,
                    "the sweep removed an expired grant and wrote no audit event for it"
                );

                // ⚠ DELETED, NOT HIDDEN. Put the clock back before the expiry: a tuple still in
                // storage would be live again and would allow. Neither index may still hold it either.
                cluster.Clock.UtcNow = Start;

                (await CheckAsync(tenant, scope, Consistency.FullyConsistent)).Allowed.ShouldBeFalse(
                    "the swept tuple is still in the forward index — the sweep hid it rather than deleting it"
                );

                (await cluster.SubjectIndex(tenant, Alice).ListAsync()).GetValueOrThrow()
                    .ShouldNotContain(e => e.Object == scope, "the swept tuple is still in the reverse index");

                (await CheckAsync(tenant, scope, Consistency.FullyConsistent, Permissions.Write, Bob)).Allowed
                    .ShouldBeTrue("the sweep took a grant that had not expired");

                cluster.Clock.UtcNow = later;

                var last = await SweepAsync(tenant);
                last.Removed.ShouldBe(1);
                last.Remaining.ShouldBe(0);
                last.Armed.ShouldBeFalse("nothing is left to sweep and the reminder is still registered");
            }
        );
    }

    [Fact]
    public async Task ARewriteReplacesTheExpiryAndAPermanentOneLeavesNothingToSweep() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4909);
                var scope = Group("jit-j");
                const string grant = "resourceGroup:jit-j#reader@user:alice";

                await WriteAsync(tenant, grant, InAnHour);

                // Extended: the new instant is the one that holds, and the register follows it.
                var extended = Start.AddHours(2);
                await WriteAsync(tenant, grant, extended);

                cluster.Clock.UtcNow = InAnHour;
                (await CheckAsync(tenant, scope, Consistency.FullyConsistent)).Allowed.ShouldBeTrue(
                    "the grant ended at its first expiry although a rewrite had extended it"
                );
                (await SweepAsync(tenant)).Removed.ShouldBe(0, "the sweep removed a grant a rewrite had extended");

                // Made permanent: nothing to expire, nothing registered, nothing armed.
                await cluster.WriteAsync(tenant, grant);

                cluster.Clock.UtcNow = Start.AddDays(30);
                (await CheckAsync(tenant, scope, Consistency.FullyConsistent)).Allowed.ShouldBeTrue();

                var sweep = await SweepAsync(tenant);
                sweep.Removed.ShouldBe(0);
                sweep.Remaining.ShouldBe(0, "a permanent grant is still registered for the sweep");
                sweep.Armed.ShouldBeFalse();
            }
        );
    }

    [Fact]
    public async Task AnExpiryThatIsNotLaterThanNowIsRefusedAndNothingLands() {
        await AtStartAsync(async () => {
                var tenant = AuthorizationCluster.Tenant(4910);
                var before = (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow();

                var refused = await cluster.Store(tenant)
                    .WriteAsync(Tuple("resourceGroup:jit-k#reader@user:alice") with { ExpiresOn = Start });

                refused.IsFailure.ShouldBeTrue("a grant that ends now grants nothing and must not be stored");
                refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

                (await cluster.Store(tenant).PendingCountAsync()).GetValueOrThrow().ShouldBe(0);
                (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow().Version.ShouldBe(before.Version);
                (await cluster.Objects(tenant, Group("jit-k")).ReadDurableAsync()).GetValueOrThrow().Count.ShouldBe(0);
            }
        );
    }

    // ── The tenant boundary ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneTenantsExpiryAndSweepLeaveAnotherTenantsSameTupleAlone() {
        await AtStartAsync(async () => {
                var (a, b) = cluster.SplitPair(4911);
                var scope = Group("jit-l");
                const string grant = "resourceGroup:jit-l#reader@user:alice";

                // The same tuple, spelled the same way, in two tenants on two shards: one ends in an
                // hour, one is permanent. Nothing about one may touch the other.
                await WriteAsync(a, grant, InAnHour);
                await cluster.WriteAsync(b, grant);

                cluster.Clock.UtcNow = InAnHour;

                (await CheckAsync(a, scope, Consistency.FullyConsistent)).Allowed.ShouldBeFalse();
                (await CheckAsync(b, scope, Consistency.FullyConsistent)).Allowed.ShouldBeTrue(
                    "tenant A's expiry reached tenant B's tuple"
                );

                (await SweepAsync(a)).Removed.ShouldBe(1);

                (await CheckAsync(b, scope, Consistency.FullyConsistent)).Allowed.ShouldBeTrue(
                    "tenant A's sweep deleted tenant B's tuple"
                );

                var bSweep = await SweepAsync(b);
                bSweep.Removed.ShouldBe(0);
                bSweep.Remaining.ShouldBe(0, "tenant A's expiring tuple is registered in tenant B's store");
                bSweep.Armed.ShouldBeFalse();
            }
        );
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static SubjectRef Bob => SubjectRef.Of(ObjectTypes.User, "bob");

    static ObjectRef Group(string id) => ObjectRef.Of(ObjectTypes.ResourceGroup, id);

    static RelationTuple Tuple(string text) => RelationTuple.Parse(text).GetValueOrThrow();

    /// <summary>Runs a test from the clock's start and puts the clock back however it ends.</summary>
    async Task AtStartAsync(Func<Task> test) {
        cluster.Clock.UtcNow = Start;

        try {
            await test();
        } finally {
            cluster.Clock.UtcNow = Start;
        }
    }

    async Task<ConsistencyToken> WriteAsync(Guid tenant, string tuple, DateTimeOffset expiresOn) {
        var written = await cluster.Store(tenant).WriteAsync(Tuple(tuple) with { ExpiresOn = expiresOn });
        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        return written.GetValueOrThrow();
    }

    async Task<CheckResult> CheckAsync(
        Guid tenant,
        ObjectRef target,
        Consistency consistency,
        string permission = Permissions.Read,
        SubjectRef? subject = null
    ) {
        var result = await cluster.Check(tenant, target).CheckAsync(permission, subject ?? Alice, consistency);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.GetValueOrThrow();
    }

    async Task<ExpirySweepReport> SweepAsync(Guid tenant) {
        var swept = await cluster.Store(tenant).SweepExpiredAsync();
        swept.IsSuccess.ShouldBeTrue(swept.Error?.Message);
        return swept.GetValueOrThrow();
    }

    async Task<string[]> ListAsync(Guid tenant, SubjectRef subject) =>
        await ListOfAsync(tenant, subject, ObjectTypes.Resource);

    async Task<string[]> ListGroupsAsync(Guid tenant, SubjectRef subject) =>
        await ListOfAsync(tenant, subject, ObjectTypes.ResourceGroup);

    async Task<string[]> ListOfAsync(Guid tenant, SubjectRef subject, string type) {
        var listed = await cluster.For(tenant)
            .GetGrain<IListObjectsGrain>(GrainKeys.ListObjects(subject.Type, subject.Id))
            .ListObjectsAsync(new() { ObjectType = type, Permission = Permissions.Read });

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        return [.. listed.GetValueOrThrow().Objects.Select(static x => x.Id)];
    }
}
