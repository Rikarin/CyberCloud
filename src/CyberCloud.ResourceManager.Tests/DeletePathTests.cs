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

    // ── Committed quota comes back. docs/plan/06 § Quota ────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A delete returns exactly what the create committed — not an approximation, and not
    ///     twice.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect this pins.</b> <c>OperationGrain.ReturnCommittedQuotaAsync</c> delegated
    ///         to its own <c>ReleaseAsync</c>, which releases <i>leases</i>.
    ///         <see cref="OperationSpec.QuotaLeaseIds" /> is empty on every delete spec — the amounts
    ///         were <b>committed</b> by the create — so the call did nothing at all.
    ///         <c>IQuotaGrain.ReturnAsync</c> was the method that unwinds committed usage and nothing
    ///         called it, so a subscription's committed figure climbed by one resource's worth on
    ///         every delete and the allowance never came back. Quota is a limit rather than a bill, so
    ///         the damage is creates refused that should have been allowed — which is silent, and gets
    ///         worse the longer a subscription is used.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The size is deliberately not the fallback.</b> <c>TestingProvider</c> declares
    ///         <c>Vcpu</c> against the pointer <c>/properties/size</c>, so a create at <c>size: 5</c>
    ///         commits five. A "return" that credited the meter's fallback, or one unit, or the
    ///         current usage, would pass a test written against the default and fail here — which is
    ///         what "exactly" means.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the second meter matters.</b> The type also declares <c>Resources</c> with no
    ///         pointer, so a create draws on two meters and a delete has to unwind both. A fix that
    ///         returned only the first would leave the drift on the second.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ADeleteReturnsExactlyWhatTheCreateCommittedOnEveryMeter() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("returns-its-quota");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var vcpuBefore = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed;
        var countBefore = (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed;

        var created = await CreateSized(address, 5);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Committed.ShouldBe(vcpuBefore + 5, "the create commits what the declared pointer says");
        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow()
            .Committed.ShouldBe(countBefore + 1);

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId);
        var status = await operation.DriveAsync();
        status.GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Committed.ShouldBe(
                vcpuBefore,
                "a delete gives back exactly what the create committed — IQuotaGrain.ReturnAsync, "
                + "which nothing used to call"
            );

        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed.ShouldBe(countBefore);

        // ⚠ NOT TWICE. The operation is re-drivable from a reminder and it is already terminal, so a
        // second drive must not credit the meter again — a delete that over-returned would hand a
        // subscription free allowance for every stuck teardown that was later re-driven.
        await operation.DriveAsync();

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(vcpuBefore);
        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed.ShouldBe(countBefore);
    }

    /// <summary>
    ///     The amounts ride on the delete's own spec, because by the time they are needed there is
    ///     nothing left to derive them from.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>ReturnCommittedQuotaAsync</c> runs after <c>CompleteDeleteAsync</c> has cleared the
    ///     resource grain, and a re-drive after a silo loss runs it with no resource and no registry
    ///     lookup available. docs/plan/08 § Long-running operations: the state "includes everything
    ///     needed to re-drive". <see cref="OperationSpec.CommittedQuota" /> is the member that makes
    ///     that true of the quota return, and this asserts it is populated at accept time rather than
    ///     at convergence time.
    /// </remarks>
    [Fact]
    public async Task TheDeleteSpecCarriesTheCommittedAmountsBeforeTheTeardownRuns() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("spec-carries-quota");

        var created = await CreateSized(address, 4);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.FailTeardownWith[resourceId] = "the API server refused the delete";

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        // The teardown has not converged and never will, so nothing has been returned yet — but the
        // operation already knows what it owes.
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId);
        await operation.DriveAsync();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var stillCommitted = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed;

        stillCommitted.ShouldBeGreaterThanOrEqualTo(
            4,
            "a resource whose teardown is stuck still exists and still counts — docs/plan/06 "
            + "§ Two-phase create keeps it visible for exactly that reason"
        );

        // Now let it converge, from the same spec, and the credit lands.
        FakeWorld.FailTeardownWith.TryRemove(resourceId, out _);
        var finished = await operation.DriveAsync();

        finished.GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);
        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Committed.ShouldBe(stillCommitted - 4);
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

    Task<Result<WriteAccepted>> CreateSized(ResourceId address, int size) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(size),
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
