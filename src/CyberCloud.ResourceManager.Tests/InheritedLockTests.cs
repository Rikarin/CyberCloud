using CyberCloud.ResourceManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Locks, inherited down the hierarchy — docs/plan/06 § Tags, locks, "<c>CanNotDelete</c> /
///     <c>ReadOnly</c>, inherited down the hierarchy".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This class uses the <i>shipped</i> <c>ResourceScopeLockResolver</c> over real
///         resource-group and subscription grains, and that is the entire point.</b> Every other test
///         of the lock step in this repository goes through <c>SwitchableLockResolver</c> — a double
///         that returns whatever level the test set, at every scope, which is exactly right for
///         asking "does step 4 honour a lock" and exactly useless for asking "is a lock at the
///         subscription ever found". The answer to the second question was <b>no</b>: the resolver
///         read the resource's own lock and nothing else, because neither ancestor grain had a lock
///         member to read. So a subscription-wide <c>CanNotDelete</c> stopped nothing.
///     </para>
///     <para>
///         ⚠ <b>Every test puts the lock back.</b> The scopes are shared with the rest of the
///         collection and xUnit runs a collection's tests one at a time, so a lock left behind would
///         fail an unrelated test somewhere later with a message about a lock nobody set.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class InheritedLockTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task ACanNotDeleteAtTheSubscriptionBlocksADeleteOfAResourceThreeLevelsDown() {
        // ⚠ THE DEFECT, EXACTLY AS docs/plan/06 § Tags, locks PROMISES IT WILL NOT HAPPEN. The lock
        // is set on the subscription; the resource is a resource, inside a group, inside that
        // subscription. Nobody touches the resource's own lock. Before the walk existed this delete
        // succeeded, and the error message the write path already printed — "Locks are inherited from
        // the resource group, the subscription and the management group" — was a description of
        // something the platform did not do.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("sub-lock-delete");
        var manager = ManagerWithRealLocks();

        var created = await Create(manager, address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        await SetSubscriptionLockAsync(LockLevel.CanNotDelete);

        try {
            var deleted = await manager.DeleteAsync(Request(address), TestContext.Current.CancellationToken);

            deleted.IsFailure.ShouldBeTrue(
                "a CanNotDelete on the SUBSCRIPTION did not stop the delete of a resource inside it"
            );

            deleted.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);

            // ⚠ And the resource is untouched — not merely "the call returned an error". A refusal
            // that had already released the index would have handed the name away.
            var entry = await cluster.Index(address).GetAsync();
            entry.GetValueOrThrow().State.ShouldBe(
                IndexEntryState.Confirmed,
                "the refused delete released the name anyway"
            );
        }
        finally {
            await SetSubscriptionLockAsync(LockLevel.None);
        }
    }

    [Fact]
    public async Task AReadOnlyAtTheSubscriptionBlocksAWrite() {
        // The other half of the inheritance: ReadOnly refuses writes as well as deletes, and it does
        // so from a scope two levels above the resource.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("sub-lock-write");
        var manager = ManagerWithRealLocks();

        var created = await Create(manager, address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        await SetSubscriptionLockAsync(LockLevel.ReadOnly);

        try {
            var written = await manager.WriteAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = TestingProvider.V2026,
                    Verb = WriteVerb.Put,
                    Body = TestingProvider.Body(size: 4),
                    Caller = ResourceManagerCluster.Caller()
                },
                TestContext.Current.CancellationToken
            );

            written.IsFailure.ShouldBeTrue("a ReadOnly on the SUBSCRIPTION did not stop a write below it");
            written.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);

            // ⚠ Refused at step 4, so quota was never drawn and the body never reached a provider.
            written.Error.Code.ShouldNotBe(ErrorCode.QuotaExceeded);

            // A read is still a read. ReadOnly means read-only, not invisible.
            var read = await manager.ReadAsync(Request(address), TestContext.Current.CancellationToken);
            read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        }
        finally {
            await SetSubscriptionLockAsync(LockLevel.None);
        }
    }

    [Fact]
    public async Task AReadOnlyAtTheSubscriptionAlsoStopsACreateThatDoesNotExistYet() {
        // ⚠ A create used to be unreachable by any lock at all — the resolver returned None the moment
        // it saw Guid.Empty. A subscription that is read-only but into which anyone can still add new
        // resources is not read-only, and "the resource has no lock of its own yet" is not a reason,
        // because the lock was never on the resource.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("sub-lock-create");
        var manager = ManagerWithRealLocks();

        await SetSubscriptionLockAsync(LockLevel.ReadOnly);

        try {
            var created = await Create(manager, address);

            created.IsFailure.ShouldBeTrue("a ReadOnly subscription accepted a brand-new resource");
            created.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);

            var entry = await cluster.Index(address).GetAsync();
            entry.GetValueOrThrow().State.ShouldBe(
                IndexEntryState.Free,
                "the refused create claimed the name anyway — step 4 runs before step 7"
            );
        }
        finally {
            await SetSubscriptionLockAsync(LockLevel.None);
        }
    }

    [Fact]
    public async Task AGroupLockIsFoundToo() {
        // The middle link. Set on the resource group rather than the subscription, so a resolver that
        // had only learned to read the subscription would fail here.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("group-lock");
        var manager = ManagerWithRealLocks();

        var created = await Create(manager, address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        await SetGroupLockAsync(LockLevel.CanNotDelete);

        try {
            var deleted = await manager.DeleteAsync(Request(address), TestContext.Current.CancellationToken);

            deleted.IsFailure.ShouldBeTrue("a CanNotDelete on the resource GROUP did not stop the delete");
            deleted.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);
        }
        finally {
            await SetGroupLockAsync(LockLevel.None);
        }
    }

    [Fact]
    public async Task TheStrongerOfTwoScopesWinsEvenWhenTheWeakerOneIsLowerAndHasTheLargerEnumValue() {
        // ⚠ THE ORDERING TRAP, END TO END. CanNotDelete is 2 and ReadOnly is 1, so a `Math.Max` walk
        // would resolve "subscription: ReadOnly, group: CanNotDelete" to CanNotDelete — and let the
        // WRITE through, which is the one thing ReadOnly exists to refuse. The operator who set the
        // stronger lock at the higher scope did the sensible thing and would have been quietly
        // overruled by the scope below.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("mixed-locks");
        var manager = ManagerWithRealLocks();

        var created = await Create(manager, address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        await SetSubscriptionLockAsync(LockLevel.ReadOnly);
        await SetGroupLockAsync(LockLevel.CanNotDelete);

        try {
            var resolved = await new ResourceScopeLockResolver(cluster.Grains).ResolveAsync(
                address.WithId(created.GetValueOrThrow().Resource.Id),
                TestContext.Current.CancellationToken
            );

            resolved.GetValueOrThrow().ShouldBe(
                LockLevel.ReadOnly,
                "the numerically larger CanNotDelete on the lower scope overruled the stronger "
                + "ReadOnly above it"
            );

            var written = await manager.WriteAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = TestingProvider.V2026,
                    Verb = WriteVerb.Put,
                    Body = TestingProvider.Body(size: 5),
                    Caller = ResourceManagerCluster.Caller()
                },
                TestContext.Current.CancellationToken
            );

            written.IsFailure.ShouldBeTrue("the write went through under a ReadOnly subscription");
            written.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);
        }
        finally {
            await SetSubscriptionLockAsync(LockLevel.None);
            await SetGroupLockAsync(LockLevel.None);
        }
    }

    [Fact]
    public async Task AScopeWithNoRecordContributesNoLockRatherThanFailingTheWalk() {
        // ⚠ FAIL-OPEN, DELIBERATELY, AND ONLY HERE. The group and subscription grains are created by
        // an admin path the resource manager does not drive, so a walk that refused every write
        // against an unrecorded scope would be a platform in which nothing can be created. Absence of
        // a lock record is absence of a lock — which is safe precisely because the EXISTENCE of the
        // subscription is a separate check, at step 1, that answers 404 rather than a lock level.
        ResourceManagerCluster.ResetDoubles();

        var unknown = new ResourceId(
            ResourceManagerCluster.Tenant,
            Guid.Parse("77777777-7777-4777-8777-777777777777"),
            "never-created",
            ConformingReconciler.TypeName,
            "nobody",
            Guid.NewGuid()
        );

        var resolved = await new ResourceScopeLockResolver(cluster.Grains).ResolveAsync(
            unknown,
            TestContext.Current.CancellationToken
        );

        resolved.IsSuccess.ShouldBeTrue(resolved.Error?.Message);
        resolved.GetValueOrThrow().ShouldBe(LockLevel.None);
    }

    // ── The pieces ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The write path with the <b>shipped</b> lock resolver, and doubles for everything else.
    /// </summary>
    /// <remarks>
    ///     ⚠ The authorizer stays a double: this class is about which SCOPES a lock is found at, and
    ///     a ReBAC refusal would stop the request one step earlier and make a lock defect read as an
    ///     authorization defect.
    /// </remarks>
    ResourceManagerService ManagerWithRealLocks() =>
        new ResourceManagerService(
            cluster.Registry,
            new SwitchableAuthorizer(),
            new RecordingRelationWriter(),
            new ResourceScopeLockResolver(cluster.Grains),
            new SwitchablePolicyEvaluator(),
            new RecordingChangeSink(),
            cluster.Grains,
            NullLogger<ResourceManagerService>.Instance
        );

    Task<Result> SetSubscriptionLockAsync(LockLevel level) =>
        cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(ResourceManagerCluster.Subscription))
            .SetLockAsync(level);

    Task<Result> SetGroupLockAsync(LockLevel level) =>
        cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<IResourceGroupGrain>(
                GrainKeys.ResourceGroup(ResourceManagerCluster.Subscription, "prod")
            )
            .SetLockAsync(level);

    static WriteRequest Request(ResourceId address) =>
        new() {
            Path = address.Path,
            ApiVersion = TestingProvider.V2026,
            Caller = ResourceManagerCluster.Caller()
        };

    static Task<Result<WriteAccepted>> Create(ResourceManagerService manager, ResourceId address) =>
        manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    async Task Converge(WriteAccepted accepted) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
