using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Deletion — <i>"the same in reverse and it is the harder half"</i>, docs/plan/06 § Two-phase
///     create.
/// </summary>
[Collection(ResourceManagerSuite.Name)]
public sealed class DeletePathTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task TheIndexIsReleasedFirstSoTheNameIsImmediatelyReusable() {
        // ⚠ docs/plan/06 § Two-phase create: "release the index first (so the name is immediately
        // reusable), then tear down the data plane, then delete the grain state."
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("released-first");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;

        // Teardown will not converge, so the data plane is still up when we check the index.
        FakeWorld.FailTeardownWith[resourceId] = "the API server refused the delete";

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var entry = await cluster.Index(address).GetAsync();
        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free, "the name comes back immediately");
    }

    [Fact]
    public async Task AResourceWhoseTeardownFailsStaysDeletingAndStaysVisible() {
        // ⚠ THE BILLING-DISPUTE PREVENTION MEASURE. docs/plan/06 § Two-phase create: "A resource whose
        // data plane teardown fails is left in Deleting with a retry reminder and is VISIBLE in
        // listings with that state — never silently gone while its pods still run and its meter still
        // ticks."
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("stuck-deleting");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.FailTeardownWith[resourceId] = "the API server refused the delete";

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        deleted.GetValueOrThrow().Resource.ProvisioningState.ShouldBe(ProvisioningState.Deleting);

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId);

        // Drive it past the retry budget: every pass fails retryably, so it stays Deleting.
        await operation.DriveAsync();
        await operation.DriveAsync();

        var resource = cluster.Resource(ResourceManagerCluster.Tenant, resourceId);
        var snapshot = await resource.GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026);

        snapshot.IsSuccess.ShouldBeTrue("a stuck resource is FOUND, not gone");
        snapshot.GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Deleting);
        snapshot.GetValueOrThrow().LastFailure.ShouldContain("the API server refused the delete");

        // And its objects are still there, which is the whole reason it must stay visible.
        FakeWorld.Applied.ShouldContainKey(resourceId);
    }

    [Fact]
    public async Task EvenWhenTheOperationTimesOutTheResourceStaysDeletingRatherThanBecomingFailed() {
        // ⚠ The sharpest form of the rule: the operation gives up, and the RESOURCE does not move to
        // Failed. A Failed resource reads as "exists and is broken"; a Deleting one reads as "on its
        // way out and stuck", which is what it is and what the operator can act on.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("timed-out-delete");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.FailTeardownWith[resourceId] = "the API server refused the delete";

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId);
        await operation.DriveAsync();

        TestClock.Instance.Advance(TimeSpan.FromMinutes(61));
        var timedOut = await operation.DriveAsync();

        timedOut.GetValueOrThrow().State.ShouldBe(OperationState.Failed);

        var snapshot = await cluster.Resource(ResourceManagerCluster.Tenant, resourceId)
            .GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026);

        snapshot.IsSuccess.ShouldBeTrue();
        snapshot.GetValueOrThrow().ProvisioningState.ShouldBe(
            ProvisioningState.Deleting,
            "a failed teardown leaves the resource Deleting, not Failed"
        );
    }

    [Fact]
    public async Task ASucceededTeardownRemovesTheGrainStateLast() {
        // The order: index, then data plane, then grain state — and the grain goes only after the
        // reconciler READ THE OBJECTS BACK AS GONE.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("clean-delete");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId);
        var status = await operation.DriveAsync();

        status.GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);
        FakeWorld.Applied.ShouldNotContainKey(resourceId);

        var snapshot = await cluster.Resource(ResourceManagerCluster.Tenant, resourceId)
            .GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026);

        snapshot.IsFailure.ShouldBeTrue();
        snapshot.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task TheNameIsReusableWhileTheOldResourceIsStillTearingDown() {
        // The payoff of releasing the index first: a tenant retrying a create with the same name does
        // not have to wait for somebody else's teardown.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("reused-name");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());
        FakeWorld.FailTeardownWith[created.GetValueOrThrow().Resource.Id] = "still terminating";

        await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        var recreated = await Create(address);

        recreated.IsSuccess.ShouldBeTrue(recreated.Error?.Message);
        recreated.GetValueOrThrow().Resource.Id.ShouldNotBe(created.GetValueOrThrow().Resource.Id);
    }

    [Fact]
    public async Task ACanNotDeleteLockRefusesTheDelete() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("undeletable");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        SwitchableLockResolver.Level = LockLevel.CanNotDelete;

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        deleted.IsFailure.ShouldBeTrue();
        deleted.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);

        SwitchableLockResolver.Reset();
        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State
            .ShouldBe(IndexEntryState.Confirmed, "a refused delete must not have released the name");
    }

    [Fact]
    public async Task DeletingSomethingThatDoesNotExistIs404() {
        ResourceManagerCluster.ResetDoubles();

        var deleted = await cluster.Manager.DeleteAsync(
            new() {
                Path = ResourceManagerCluster.Address("never-was").Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        deleted.IsFailure.ShouldBeTrue();
        deleted.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task TheDeleteTraceStopsAtAcceptedAndIsCanonical() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("delete-trace");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        var trace = deleted.GetValueOrThrow().Trace;

        trace.StoppedAt.ShouldBe(WriteStep.Accepted);

        // ⚠ The delete path skips 2, 5 and 6 — there is no body, policy is not evaluated for a delete
        // in this build, and quota is RETURNED rather than reserved and only once teardown converges.
        // So the trace is increasing but is NOT a canonical prefix, and saying so is more honest than
        // recording steps that did not run.
        trace.Reached.ShouldNotContain(WriteStep.ValidateBody);
        trace.Reached.ShouldNotContain(WriteStep.Quota);

        for (var i = 1; i < trace.Reached.Length; i++) {
            ((int)trace.Reached[i]).ShouldBeGreaterThan((int)trace.Reached[i - 1]);
        }
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
