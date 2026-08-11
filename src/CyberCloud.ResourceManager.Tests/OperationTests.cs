using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Long-running operations: resumable, cancellable, backed off, and timed out.
///     docs/plan/08 § Long-running operations and § The reconcile loop.
/// </summary>
[Collection(ResourceManagerSuite.Name)]
public sealed class OperationTests(ResourceManagerCluster cluster) {
    // ── Resumability ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOperationReDrivesFromDurableStateOnActivation() {
        // ⚠ docs/plan/00's non-negotiable "every LRO is resumable", and docs/plan/08 § Long-running
        // operations: "On activation after a silo loss the grain re-registers its reminder and
        // continues."
        //
        // Deactivating the grain is the in-process stand-in for losing its silo. What it proves is the
        // re-drive path; what it does NOT prove is that the durable tier survived a real restart —
        // see this project's .csproj on what in-memory storage costs.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("resumable");

        FakeWorld.StayInProgress[Guid.Empty] = true; // placeholder; replaced once the id is known

        var accepted = await Create(address);
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var resourceId = accepted.GetValueOrThrow().Resource.Id;
        FakeWorld.StayInProgress.TryRemove(Guid.Empty, out _);
        FakeWorld.StayInProgress[resourceId] = true;

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        var first = await operation.DriveAsync();
        first.GetValueOrThrow().State.ShouldBe(OperationState.Running);
        first.GetValueOrThrow().Attempts.ShouldBe(1);
        var activationsBefore = first.GetValueOrThrow().Activations;

        // Lose the activation mid-operation.
        await operation.DeactivateAsync();

        // Touching it re-activates it, and OnActivateAsync re-drives: the counter goes up and the
        // recorded attempt survives, so the next pass continues rather than starting over.
        var afterRestart = await operation.GetAsync();

        afterRestart.GetValueOrThrow().Activations.ShouldBeGreaterThan(activationsBefore);
        afterRestart.GetValueOrThrow().Attempts.ShouldBe(1, "the attempt cursor survives the loss");
        afterRestart.GetValueOrThrow().State.ShouldBe(OperationState.Running);
        afterRestart.GetValueOrThrow().ResourcePath.ShouldBe(address.Path);

        // And it carries on from there rather than from zero.
        FakeWorld.StayInProgress.TryRemove(resourceId, out _);
        var continued = await operation.DriveAsync();

        continued.GetValueOrThrow().Attempts.ShouldBe(2);
        continued.GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);
    }

    [Fact]
    public async Task TheSpecCarriesEverythingAReDriveNeeds() {
        // ⚠ docs/plan/08 § Long-running operations: the state "includes everything needed to re-drive:
        // the resource id, the desired body, the quota lease, the index claim, the step cursor." The
        // quota lease and the index claim are the two a resume CANNOT recompute — a new reservation
        // would double-count, and a claim that looks free might be one this operation lost.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("spec-complete");

        var accepted = await Create(address);
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        await operation.DeactivateAsync();
        var status = await operation.GetAsync();

        status.IsSuccess.ShouldBeTrue();
        status.GetValueOrThrow().ResourcePath.ShouldBe(address.Path);

        // The lease is live and belongs to this operation, which is only knowable from the spec.
        var leases = await cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription)
            .ListLeasesAsync();

        leases.GetValueOrThrow()
            .ShouldContain(x => x.OperationId == accepted.GetValueOrThrow().OperationId);
    }

    [Fact]
    public async Task StartingTheSameSpecTwiceSucceedsAndChangesNothing() {
        ResourceManagerCluster.ResetDoubles();
        var operationId = Guid.NewGuid();
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, operationId);

        var spec = new OperationSpec {
            OperationId = operationId,
            Kind = OperationKind.Create,
            ResourcePath = ResourceManagerCluster.Address("restartable").Path,
            ResourceId = Guid.NewGuid(),
            TenantId = ResourceManagerCluster.Tenant,
            SubscriptionId = ResourceManagerCluster.Subscription,
            ApiVersion = TestingProvider.V2026,
            Desired = TestingProvider.Body()
        };

        (await operation.StartAsync(spec)).IsSuccess.ShouldBeTrue();
        (await operation.StartAsync(spec)).IsSuccess.ShouldBeTrue("the retried PUT must be a no-op all the way down");

        var different = spec with { Desired = TestingProvider.Body(size: 99) };
        var conflict = await operation.StartAsync(different);

        conflict.IsFailure.ShouldBeTrue();
        conflict.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    // ── Cancellation completes ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACancelledCreateRunsTheDeletePathForAnythingAlreadyApplied() {
        // ⚠ THE BILLING-DISPUTE TEST. docs/plan/08 § Long-running operations: "for anything already
        // applied it runs the delete path. A 'cancelled' create that leaves resources running is a
        // billing dispute waiting to happen, so cancellation COMPLETES rather than abandoning."
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("cancelled-create");

        var accepted = await Create(address);
        var resourceId = accepted.GetValueOrThrow().Resource.Id;
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        // Make it stay in progress so there is something to interrupt, and drive one pass so the
        // reconciler applies.
        FakeWorld.StayInProgress[resourceId] = true;
        await operation.DriveAsync();

        FakeWorld.Applied.ShouldContainKey(resourceId, "the create must have applied something to interrupt");

        var cancelled = await operation.CancelAsync("the user changed their mind");
        cancelled.IsSuccess.ShouldBeTrue();

        // ⚠ CancelAsync returns as soon as the flag is set. The operation is NOT yet Canceled.
        var flagged = await operation.GetAsync();
        flagged.GetValueOrThrow().CancelRequested.ShouldBeTrue();
        flagged.GetValueOrThrow().State.ShouldNotBe(OperationState.Canceled);

        // The next drive runs the teardown, and only then does it report Canceled.
        FakeWorld.StayInProgress.TryRemove(resourceId, out _);
        var finished = await operation.DriveAsync();

        finished.GetValueOrThrow().State.ShouldBe(OperationState.Canceled);
        FakeWorld.Deletes.ShouldContainKey(resourceId, "the delete path must have run");
        FakeWorld.Applied.ShouldNotContainKey(resourceId, "nothing may be left running");
    }

    [Fact]
    public async Task ACancelledOperationReleasesItsQuotaLeases() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("cancelled-quota");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var before = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();

        var accepted = await Create(address);
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Reserved
            .ShouldBeGreaterThan(before.Reserved, "step 6 reserved");

        await operation.CancelAsync("changed my mind");
        await operation.DriveAsync();

        var after = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();
        after.Reserved.ShouldBe(before.Reserved, "a cancelled operation releases its lease");
        after.Committed.ShouldBe(before.Committed, "and never commits it");
    }

    [Fact]
    public async Task ATerminalOperationCannotBeCancelled() {
        ResourceManagerCluster.ResetDoubles();
        var accepted = await Create(ResourceManagerCluster.Address("already-done"));
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        await operation.DriveAsync();
        (await operation.GetAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        var cancelled = await operation.CancelAsync("too late");

        cancelled.IsFailure.ShouldBeTrue();
        cancelled.Error!.Code.ShouldBe(ErrorCode.Conflict);
        cancelled.Error.Message.ShouldContain("promise a teardown that will never run");
    }

    // ── Quota, failure and the ledger ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailedOperationReleasesItsLease() {
        // docs/plan/06 § Quota: "the lease is released if the operation fails".
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("failing");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var before = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();

        var accepted = await Create(address);
        FakeWorld.FailWith[accepted.GetValueOrThrow().Resource.Id] = "the manifest was rejected";

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);
        var status = await operation.DriveAsync();

        status.GetValueOrThrow().State.ShouldBe(OperationState.Failed);
        status.GetValueOrThrow().Error!.Code.ShouldBe(ErrorCode.ProvisioningFailed);

        var after = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();
        after.Reserved.ShouldBe(before.Reserved);
        after.Committed.ShouldBe(before.Committed);

        // And the resource says why.
        var resource = await cluster.Resource(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().Resource.Id)
            .GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026);

        resource.GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Failed);
        resource.GetValueOrThrow().LastFailure.ShouldContain("the manifest was rejected");
    }

    [Fact]
    public async Task AnAbandonedLeaseExpiresOnItsOwn() {
        // ⚠ Uses IQuotaGrain's own expiry rather than reimplementing one. docs/plan/06 § Quota: the
        // lease "expires on its own if the operation grain dies", and IQuotaGrain evaluates expiry on
        // READ rather than on a timer — "a timer that has to fire for correctness is a timer whose
        // silo can die."
        ResourceManagerCluster.ResetDoubles();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var before = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();

        var accepted = await Create(ResourceManagerCluster.Address("abandoned"));
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Reserved
            .ShouldBeGreaterThan(before.Reserved);

        var operationId = accepted.GetValueOrThrow().OperationId;

        (await quota.ListLeasesAsync()).GetValueOrThrow()
            .ShouldContain(x => x.OperationId == operationId, "step 6 took a lease for this operation");

        // Nobody drives the operation. Time passes past the lease duration.
        TestClock.Instance.Advance(TimeSpan.FromMinutes(61));

        // ⚠ Asserted on THIS operation's lease rather than on the meter total. Every test in this
        // collection shares one quota grain, so the total also loses every other test's abandoned
        // lease at the same instant — which is the same rule working, just not this test's subject.
        (await quota.ListLeasesAsync()).GetValueOrThrow()
            .ShouldNotContain(
                x => x.OperationId == operationId,
                "an abandoned lease frees its quota the moment somebody asks"
            );
    }

    [Fact]
    public async Task ASucceededOperationCommitsItsLease() {
        ResourceManagerCluster.ResetDoubles();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var before = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();

        var accepted = await Create(ResourceManagerCluster.Address("committing"), size: 3);
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        await operation.DriveAsync();

        var after = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();
        after.Committed.ShouldBe(before.Committed + 3, "the meter draws from the declared body pointer");
        after.Reserved.ShouldBe(before.Reserved);
    }

    [Fact]
    public async Task ExceedingAMeterIsRefusedAtStepSixAndNamesTheMeter() {
        // docs/plan/08 § Errors' worked example is a QuotaExceeded, and the rule is that the message
        // "names the actual numbers".
        ResourceManagerCluster.ResetDoubles();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var usage = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();

        var address = ResourceManagerCluster.Address("too-big");
        var oversized = (int)(usage.Limit + 1);

        var refused = await Create(address, size: oversized);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
        refused.Error.Message.ShouldContain("Vcpu");
        refused.Error.Message.ShouldContain(oversized.ToString(System.Globalization.CultureInfo.InvariantCulture));

        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State
            .ShouldBe(IndexEntryState.Free, "a quota refusal must not have claimed the name");
    }

    // ── Backoff and the ceiling ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInProgressPassSchedulesAndSaysHowLong() {
        ResourceManagerCluster.ResetDoubles();
        var accepted = await Create(ResourceManagerCluster.Address("in-progress"));
        var resourceId = accepted.GetValueOrThrow().Resource.Id;

        FakeWorld.StayInProgress[resourceId] = true;

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);
        var status = await operation.DriveAsync();

        status.GetValueOrThrow().State.ShouldBe(OperationState.Running);
        status.GetValueOrThrow().Progress.ShouldContain(
            x => x.Step == "waiting" && x.Detail.Contains("replicas", StringComparison.Ordinal)
        );

        status.GetValueOrThrow().Progress.ShouldContain(x => x.Detail.Contains("Next attempt in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AfterSixtyMinutesInProgressTheOperationFailsWithATimeoutNamingTheLastProgressEntry() {
        // ⚠ THE STUCK-FOREVER TEST. docs/plan/08 § The reconcile loop: "After 60 minutes in InProgress
        // the operation fails with a timeout error naming the last progress entry — a resource stuck
        // forever is worse than a resource that failed, because a failure is actionable."
        ResourceManagerCluster.ResetDoubles();
        var accepted = await Create(ResourceManagerCluster.Address("stuck"));
        var resourceId = accepted.GetValueOrThrow().Resource.Id;

        FakeWorld.StayInProgress[resourceId] = true;

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        var running = await operation.DriveAsync();
        running.GetValueOrThrow().State.ShouldBe(OperationState.Running);
        var lastBefore = running.GetValueOrThrow().LastProgress;
        lastBefore.ShouldNotBeNull();

        TestClock.Instance.Advance(TimeSpan.FromMinutes(61));

        var timedOut = await operation.DriveAsync();

        timedOut.GetValueOrThrow().State.ShouldBe(OperationState.Failed);
        timedOut.GetValueOrThrow().Error!.Code.ShouldBe(ErrorCode.OperationTimeout);
        timedOut.GetValueOrThrow().Error!.Message.ShouldContain(lastBefore.Step);
        timedOut.GetValueOrThrow().Error!.Message.ShouldContain("60 minutes");
    }

    [Fact]
    public async Task AProgressArrayIsCappedAndSaysHowManyItDropped() {
        // An unbounded array on a durable grain is a row that grows until the write fails.
        ResourceManagerCluster.ResetDoubles();
        var accepted = await Create(ResourceManagerCluster.Address("chatty"));
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);

        for (var i = 0; i < OperationGrain.MaxProgressEntries + 20; i++) {
            await operation.ReportAsync(new() { Step = "noise", Detail = $"entry {i}" });
        }

        var status = await operation.GetAsync();
        status.GetValueOrThrow().Progress.Length.ShouldBeLessThanOrEqualTo(OperationGrain.MaxProgressEntries);
    }

    [Fact]
    public async Task AnOperationThatWasNeverStartedIs404() {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, Guid.NewGuid());
        var status = await operation.GetAsync();

        status.IsFailure.ShouldBeTrue();
        status.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── The poll. docs/plan/08 § Long-running operations, GET /operations/{opId} ─────────────────

    /// <summary>
    ///     ⚠ <b>The check happens, and it is the resource's read permission.</b>
    /// </summary>
    /// <remarks>
    ///     <c>IResourceManager</c> had no method for docs/plan/10 § Long-running operations' endpoint,
    ///     so the gateway asked its own question — <c>ReadAsync</c> on the operation's resource, which
    ///     is a full read per poll. The method exists now and the check moved <i>into</i> it, so this
    ///     is the test that the move did not lose the check: the operation is real and in the caller's
    ///     own tenant, and a caller without the type's read permission still gets the canonical
    ///     <c>404</c>.
    /// </remarks>
    [Fact]
    public async Task PollingAnOperationRunsTheReadCheckAndAnswers404WhenItRefuses() {
        ResourceManagerCluster.ResetDoubles();

        var accepted = await Create(ResourceManagerCluster.Address("polled"));
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var operationId = accepted.GetValueOrThrow().OperationId;

        // Readable: the status comes back, and it names the resource the operation drives.
        var allowed = await cluster.Manager.GetOperationAsync(
            operationId,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        allowed.IsSuccess.ShouldBeTrue(allowed.Error?.Message);
        allowed.GetValueOrThrow().OperationId.ShouldBe(operationId);
        allowed.GetValueOrThrow().ResourceId.ShouldBe(accepted.GetValueOrThrow().Resource.Id);

        // ⚠ And the seam was asked with the READ permission, not the write one the create used.
        SwitchableAuthorizer.Asked.ShouldContain("read");

        // Revoked: the same operation, the same tenant, and a 404 that says nothing else.
        SwitchableAuthorizer.GrantOnly("write");

        var refused = await cluster.Manager.GetOperationAsync(
            operationId,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "docs/plan/07 § The enforcement seam: 404, never 403 — there is no third case for a GET"
        );
    }

    /// <summary>
    ///     Another tenant's operation reads as absent, and <c>ForTenant</c> is what makes it so.
    /// </summary>
    /// <remarks>
    ///     ⚠ The tenant in the key comes from the <see cref="CallerContext" />, which the gateway built
    ///     from the token — docs/plan/00 § The tenant-separation row, corrected. Nothing else closes
    ///     this: <c>IResourceManager</c> is held by an Orleans client, so
    ///     <c>Orleans.Multitenant</c>'s call filter never runs.
    /// </remarks>
    [Fact]
    public async Task AnotherTenantsOperationIsTheCanonical404() {
        ResourceManagerCluster.ResetDoubles();

        var accepted = await Create(ResourceManagerCluster.Address("not-yours"));
        var operationId = accepted.GetValueOrThrow().OperationId;

        var stolen = await cluster.Manager.GetOperationAsync(
            operationId,
            ResourceManagerCluster.Caller(ResourceManagerCluster.OtherTenant, "mallory"),
            TestContext.Current.CancellationToken
        );

        stolen.IsFailure.ShouldBeTrue();
        stolen.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        stolen.Error.Message.ShouldNotContain("not-yours", Case.Insensitive);
    }

    [Fact]
    public async Task AnOperationNobodyStartedIsTheSame404AsOneYouMayNotSee() {
        ResourceManagerCluster.ResetDoubles();

        var absent = await cluster.Manager.GetOperationAsync(
            Guid.NewGuid(),
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        absent.IsFailure.ShouldBeTrue();
        absent.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    Task<Result<WriteAccepted>> Create(ResourceId address, int size = 2) =>
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
}
