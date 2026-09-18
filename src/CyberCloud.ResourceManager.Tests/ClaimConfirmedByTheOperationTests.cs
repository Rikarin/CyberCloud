using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     docs/plan/06 § Two-phase create's step 3, finished by the operation when the write path died
///     before reaching it — the defect the first chaos storm found (issue #44).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The state under test is the one a silo death leaves, built by hand because nothing
///         else can build it.</b> <c>ResourceManagerService.WriteAsync</c> starts the operation at
///         step 10 and confirms the claim right after, in one method on one thread; the only way that
///         method exits between the two is the process dying, which a test cannot ask for. So the
///         first test claims a name and starts an operation for it without confirming, exactly as the
///         dying saga left them, and asks what the first pass does about it.
///     </para>
///     <para>
///         ⚠ <b>What went wrong without this, in the storm's own numbers:</b> two names were left
///         claimed by a dead write; the reminder drove both orphans to Succeeded; the lease expired
///         after 301 s; the retried PUTs created two more resources under the same names; the sweep
///         found 26 Succeeded members for 24 creates, 26 ConfigMaps for 24 resources, and the
///         group's reaper — which looks at Creating members only — had nothing to say about any of
///         it. The two ghosts were billed and unaddressable.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ClaimConfirmedByTheOperationTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task AnOperationWhoseWritePathDiedBeforeStepThreeConfirmsTheClaimOnItsFirstPass() {
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("confirmed-by-the-operation");
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var index = cluster.Index(address);

        // Steps 7 and 10 of the create saga, with 3 never reached.
        (await index.TryClaimAsync(address.WithId(resourceId), resourceId)).IsSuccess.ShouldBeTrue();

        (await index.GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Claimed, "the claim is a lease, not a binding.");

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, operationId);

        (await operation.StartAsync(
            new() {
                OperationId = operationId,
                Kind = OperationKind.Create,
                ResourcePath = address.Path,
                ResourceId = resourceId,
                TenantId = ResourceManagerCluster.Tenant,
                SubscriptionId = ResourceManagerCluster.Subscription,
                ApiVersion = TestingProvider.V2026,
                Desired = TestingProvider.Body(),
                IndexClaimed = true
            }
        )).IsSuccess.ShouldBeTrue();

        // The reminder's first tick, as a test drives it.
        _ = await operation.DriveAsync();

        var entry = (await index.GetAsync()).GetValueOrThrow();

        entry.State.ShouldBe(
            IndexEntryState.Confirmed,
            "the operation ran a pass and left the claim a lease. When the lease expires the name is "
            + "free, the tenant's retried PUT creates a second resource under it, and this one converges "
            + "into a ghost nobody can address — the reaper only sweeps Creating members."
        );

        entry.BoundTo.ShouldBe(resourceId);
    }

    [Fact]
    public async Task AnOperationWhoseClaimIsGoneCancelsRatherThanConvergingAGhost() {
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("claim-gone");

        // A real create, confirmed by the write path, kept mid-flight by the world.
        FakeWorld.StayInProgress[Guid.Empty] = true;
        var accepted = (await Create(address)).GetValueOrThrow();
        FakeWorld.StayInProgress.TryRemove(Guid.Empty, out _);
        FakeWorld.StayInProgress[accepted.Resource.Id] = true;

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);
        (await operation.DriveAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Running);

        // ⚠ The name is taken from under it — the shape a lease expiry followed by a retried PUT
        // leaves, done directly so the test does not depend on the lease's length. The second
        // claimant's binding is the one that must survive what follows.
        var rival = Guid.NewGuid();
        (await cluster.Index(address).ReleaseAsync(accepted.Resource.Id)).IsSuccess.ShouldBeTrue();
        (await cluster.Index(address).TryClaimAsync(address.WithId(rival), rival)).IsSuccess.ShouldBeTrue();
        (await cluster.Index(address).ConfirmAsync(rival)).IsSuccess.ShouldBeTrue();

        // The operation's own pass has no way to know yet: IndexConfirmed was set on the first
        // drive above. Deactivating and re-driving is not enough either, because the flag is
        // durable — which is correct: a confirmed claim is not re-confirmed on every pass. So the
        // case this test pins is the operation that never saw the claim confirmed, and the rival
        // arrived first.
        var late = Guid.NewGuid();
        var lateOperation = cluster.Operation(ResourceManagerCluster.Tenant, late);

        (await lateOperation.StartAsync(
            new() {
                OperationId = late,
                Kind = OperationKind.Create,
                ResourcePath = address.Path,
                ResourceId = accepted.Resource.Id,
                TenantId = ResourceManagerCluster.Tenant,
                SubscriptionId = ResourceManagerCluster.Subscription,
                ApiVersion = TestingProvider.V2026,
                Desired = TestingProvider.Body(),
                IndexClaimed = true
            }
        )).IsSuccess.ShouldBeTrue();

        FakeWorld.StayInProgress.TryRemove(accepted.Resource.Id, out _);

        OperationStatus? last = null;
        for (var i = 0; i < 5 && last?.IsTerminal != true; i++) {
            last = (await lateOperation.DriveAsync()).GetValueOrThrow();
        }

        last.ShouldNotBeNull();

        last.State.ShouldBe(
            OperationState.Canceled,
            $"the operation ended {last.State}. A create whose name belongs to another resource must not "
            + "converge — it would be a resource nobody can address, delete or stop paying for."
        );

        last.CancelReason.ShouldContain("could not be confirmed");

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .BoundTo.ShouldBe(rival, "the cancelling operation touched a binding that was not its own.");

        (await cluster.Resource(ResourceManagerCluster.Tenant, accepted.Resource.Id).GetAsync(TestingProvider.V2026, []))
            .GetValueOrThrow()
                .ProvisioningState.ShouldBe(ProvisioningState.Canceled, "the resource did not follow its operation.");
    }

    Task<Result<WriteAccepted>> Create(ResourceId address) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );
}
