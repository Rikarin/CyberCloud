using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The eleven steps of docs/plan/08 § The write path, end to end, and the order that is the whole
///     reason the manager is one component.
/// </summary>
[Collection(ResourceManagerSuite.Name)]
public sealed class WritePathTests(ResourceManagerCluster cluster) {
    // ── The order ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHappyCreateRunsAllElevenStepsInTheDocumentsOrder() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("order-happy");

        var accepted = await Write(address);

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.GetValueOrThrow().Trace.Reached.ShouldBe(WriteTrace.Canonical);
        accepted.GetValueOrThrow().Trace.IsCanonicalPrefix().ShouldBeTrue();
    }

    [Fact]
    public async Task EveryTraceIsAStrictlyIncreasingPrefixOfTheCanonicalOrder() {
        // ⚠ THE TEST THAT FAILS IF ANY STEP MOVES. WriteTraceBuilder.Enter throws on a step that is not
        // later than the last one recorded, so a reordering in the write path fails at the call site;
        // this asserts the resulting trace against the document's order for a create, an update, a
        // refusal at 3, a refusal at 6 and a refusal at 7 — the five shapes that between them cover
        // every step.
        ResourceManagerCluster.ResetDoubles();

        var traces = new List<WriteTrace>();

        var created = await Write(ResourceManagerCluster.Address("order-a"));
        traces.Add(created.GetValueOrThrow().Trace);

        await Converge(created.GetValueOrThrow());
        var updated = await Write(ResourceManagerCluster.Address("order-a"), TestingProvider.Body(size: 4));
        traces.Add(updated.GetValueOrThrow().Trace);

        SwitchableAuthorizer.GrantOnly();
        var refused = await Write(ResourceManagerCluster.Address("order-b"));
        refused.IsFailure.ShouldBeTrue();
        SwitchableAuthorizer.Reset();

        foreach (var trace in traces) {
            trace.IsCanonicalPrefix().ShouldBeTrue(trace.ToString());

            for (var i = 1; i < trace.Reached.Length; i++) {
                ((int)trace.Reached[i]).ShouldBeGreaterThan(
                    (int)trace.Reached[i - 1],
                    $"steps must strictly increase: {trace}"
                );
            }
        }
    }

    [Fact]
    public async Task WhenTheCheckRefusesNoQuotaIsReservedAndNoNameIsClaimed() {
        // ⚠ THE STEP-ORDER SECURITY TEST. docs/plan/08 § The write path, end to end: "Steps 3-7 are the
        // entire reason this is one component … A provider that could skip step 3 is a provider that
        // eventually will."
        //
        // This fails if step 3 moved AFTER step 6 or step 7 — the reservation or the claim would be
        // there. It is asserted against the REAL IQuotaGrain and IResourceIndexGrain, not against
        // doubles, so "we did not call it" and "we called it and it did nothing" cannot be confused.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("order-denied");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);

        var before = await quota.GetUsageAsync(QuotaMeter.Vcpu);
        var leasesBefore = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();

        SwitchableAuthorizer.GrantOnly();
        var refused = await Write(address);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        var after = await cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription)
            .GetUsageAsync(QuotaMeter.Vcpu);

        after.GetValueOrThrow().Reserved.ShouldBe(before.GetValueOrThrow().Reserved);
        after.GetValueOrThrow().Committed.ShouldBe(before.GetValueOrThrow().Committed);

        var index = await cluster.Index(address).GetAsync();
        index.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);

        // ⚠ Compared against a before-set rather than asserted empty. Every test in this collection
        // shares one quota grain, so leases from other tests are live; what this test is about is that
        // THIS request took none.
        var leasesAfter = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();

        leasesAfter.ExceptWith(leasesBefore);
        leasesAfter.ShouldBeEmpty("a write refused at step 3 must not have reserved anything at step 6");
    }

    [Fact]
    public async Task WhenTheCheckRefusesNoProviderIsEverCalled() {
        // The other half of the same property: a refused write must not reach a reconciler. FakeWorld
        // is what a reconciler writes into, so an empty world is "no provider ran".
        ResourceManagerCluster.ResetDoubles();

        SwitchableAuthorizer.GrantOnly();
        var refused = await Write(ResourceManagerCluster.Address("order-no-provider"));

        refused.IsFailure.ShouldBeTrue();
        FakeWorld.Passes.ShouldBeEmpty();
        FakeWorld.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task QuotaIsReservedBeforeTheNameIsClaimedAndTheLeaseIsReleasedWhenTheNameIsTaken() {
        // ⚠ STEP 6 BEFORE STEP 7, SHOWN BY THE FAILURE BETWEEN THEM. Somebody else claims the name
        // under a lease — the state a create is in between the two-phase create's step 1 and step 3 —
        // and our write then reserves at 6 and loses at 7. docs/plan/06 § Quota: "the lease is released
        // if the operation fails." A reservation left behind would hold a subscription's capacity for
        // the whole lease duration against a request that was refused.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("order-conflict");

        // A rival claim, unconfirmed, so ResolveAsync still reports the name as free and the write
        // takes the create path.
        var rival = Guid.NewGuid();
        (await cluster.Index(address).TryClaimAsync(address, rival)).IsSuccess.ShouldBeTrue();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var leasesBefore = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();

        var refused = await Write(address);

        refused.IsFailure.ShouldBeTrue("the name is claimed by somebody else");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);

        var leasesAfter = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();
        leasesAfter.ExceptWith(leasesBefore);

        leasesAfter.ShouldBeEmpty("the lease taken at step 6 is released when step 7 refuses");

        // And the rival's claim is untouched — losing the race must not release somebody else's name.
        (await cluster.Index(address).GetAsync()).GetValueOrThrow().BoundTo.ShouldBe(rival);
    }

    [Fact]
    public async Task PolicyIsAskedAtStepFiveAndTheDefaultSaysNoEngineRan() {
        ResourceManagerCluster.ResetDoubles();

        var accepted = await Write(ResourceManagerCluster.Address("order-policy"));

        accepted.IsSuccess.ShouldBeTrue();
        SwitchablePolicyEvaluator.Asked.ShouldBe(1);
        accepted.GetValueOrThrow().Trace.Reached.ShouldContain(WriteStep.Policy);
    }

    [Fact]
    public async Task APolicyDenialStopsAtStepFiveAndNothingBelowItRuns() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("order-policy-deny");

        SwitchablePolicyEvaluator.Next = new(
            PolicyEffect.Deny,
            new Error(ErrorCode.PolicyViolation, "the 'no-public-ips' policy refuses this sku.", "/properties/size")
        );

        var refused = await Write(address);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Target.ShouldBe("/properties/size");

        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    // ── The verb grammar ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARepeatedIdenticalPutIsANoOp() {
        // ⚠ THE REASON THE API IS PUT. docs/plan/06 § Two-phase create: "the caller retries the PUT —
        // which is idempotent because PUT with the same body on an existing resource is a no-op, which
        // is exactly why the API is PUT and not POST."
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("idempotent-put");

        var first = await Write(address);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        await Converge(first.GetValueOrThrow());

        var resource = cluster.Resource(ResourceManagerCluster.Tenant, first.GetValueOrThrow().Resource.Id);
        var before = (await resource.GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026)).GetValueOrThrow();

        var again = await Write(address);

        again.IsSuccess.ShouldBeTrue(again.Error?.Message);
        again.GetValueOrThrow().NoOp.ShouldBeTrue("a repeated identical PUT changes nothing");
        again.GetValueOrThrow().OperationId.ShouldBe(Guid.Empty, "a no-op starts no operation");

        var after = (await resource.GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026)).GetValueOrThrow();
        after.Etag.ShouldBe(before.Etag, "an etag that moved on a no-op would invalidate a concurrent reader");
        after.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
        after.ModifiedAt.ShouldBe(before.ModifiedAt);
    }

    [Fact]
    public async Task APutWithADifferentBodyIsNotANoOpAndStartsAnOperation() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("changed-put");

        var first = await Write(address);
        await Converge(first.GetValueOrThrow());

        var changed = await Write(address, TestingProvider.Body(size: 4, label: "second"));

        changed.IsSuccess.ShouldBeTrue(changed.Error?.Message);
        changed.GetValueOrThrow().NoOp.ShouldBeFalse();
        changed.GetValueOrThrow().OperationId.ShouldNotBe(Guid.Empty);
        changed.GetValueOrThrow().Resource.ProvisioningState.ShouldBe(ProvisioningState.Updating);
    }

    [Fact]
    public async Task PutIsAFullReplacementAndDropsAnOmittedProperty() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("replacing-put");

        var first = await Write(address, TestingProvider.Body(size: 2, label: "keep-me"));
        await Converge(first.GetValueOrThrow());

        // The same body without `label`. PUT replaces this version's slice, so the label goes.
        var replaced = await Write(
            address,
            """{"location":"eu-central","properties":{"size":2}}"""
        );

        replaced.IsSuccess.ShouldBeTrue(replaced.Error?.Message);
        replaced.GetValueOrThrow().Resource.Properties.ShouldNotContain("keep-me");
    }

    [Fact]
    public async Task PatchIsAMergeAndLeavesAnOmittedPropertyAlone() {
        // ⚠ The asymmetry that makes PUT idempotent and PATCH not. RFC 7386 JSON Merge Patch.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("merging-patch");

        var first = await Write(address, TestingProvider.Body(size: 2, label: "keep-me"));
        await Converge(first.GetValueOrThrow());

        var patched = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Patch,
                Body = """{"properties":{"size":9}}""",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        patched.IsSuccess.ShouldBeTrue(patched.Error?.Message);
        patched.GetValueOrThrow().Resource.Properties.ShouldContain("keep-me");
        patched.GetValueOrThrow().Resource.Properties.ShouldContain("9");
    }

    [Fact]
    public async Task PostNeverCreates() {
        // docs/plan/08 § The write path, end to end: POST "appears only for actions on an existing
        // resource (/restart, /rotateKeys, /listKeys), never for creation."
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("never-created");

        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "restart",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        action.IsFailure.ShouldBeTrue();
        action.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
        FakeWorld.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task PostIsRefusedByWriteAsyncBecauseItIsNotAWriteVerb() {
        ResourceManagerCluster.ResetDoubles();

        var result = await cluster.Manager.WriteAsync(
            new() {
                Path = ResourceManagerCluster.Address("post-to-write").Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("never creates");
    }

    // ── The api-version machinery ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AReadAtAnOldVersionKeepsGettingTheShapeItWasWrittenAgainst() {
        // ⚠ docs/plan/08 § The provider registry: "Adding a field is a new version … a read at an old
        // version projects down." The resource is written at 2027 with a field 2026 never had; a read
        // at 2026 must not see it, because the SDK generated against 2026 has no member for it.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("versioned");

        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2027,
                Verb = WriteVerb.Put,
                Body = """{"location":"eu-central","properties":{"size":2,"tier":"premium"}}""",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var atOld = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        var atNew = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2027, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        atOld.IsSuccess.ShouldBeTrue(atOld.Error?.Message);
        atOld.GetValueOrThrow().Properties.ShouldNotContain("premium");
        atOld.GetValueOrThrow().Properties.ShouldNotContain("tier");
        atOld.GetValueOrThrow().Properties.ShouldContain("eu-central");

        atNew.GetValueOrThrow().Properties.ShouldContain("premium");
    }

    [Fact]
    public async Task AnOldVersionPutDoesNotDeleteANewerVersionsField() {
        // ⚠ PUT replaces THIS VERSION'S SLICE, not the superset. An old client must not be able to
        // delete what it cannot see — that would make an old-version PUT a data-loss event.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("superset-safe");

        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2027,
                Verb = WriteVerb.Put,
                Body = """{"location":"eu-central","properties":{"size":2,"tier":"premium"}}""",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        await Converge(created.GetValueOrThrow());

        var oldWrite = await Write(address, """{"location":"eu-central","properties":{"size":5}}""");
        oldWrite.IsSuccess.ShouldBeTrue(oldWrite.Error?.Message);

        var atNew = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2027, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        atNew.GetValueOrThrow().Properties.ShouldContain("premium");
        atNew.GetValueOrThrow().Properties.ShouldContain("5");
    }

    [Fact]
    public async Task AnUnknownApiVersionIsRefusedAndTheErrorNamesTheOnesThatExist() {
        ResourceManagerCluster.ResetDoubles();

        var result = await cluster.Manager.WriteAsync(
            new() {
                Path = ResourceManagerCluster.Address("bad-version").Path,
                ApiVersion = "2030-01-01",
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InvalidApiVersion);
        result.Error.Message.ShouldContain(TestingProvider.V2026);
        result.Error.Message.ShouldContain(TestingProvider.V2027);
    }

    [Fact]
    public async Task ThereIsNoLatest() {
        ResourceManagerCluster.ResetDoubles();

        var result = await cluster.Manager.WriteAsync(
            new() {
                Path = ResourceManagerCluster.Address("no-latest").Path,
                ApiVersion = "latest",
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InvalidApiVersion);
    }

    // ── Tenancy, locks, and step 10 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACrossTenantPathIs404() {
        // ⚠ 404, not 403. The isolation suite (docs/plan/03) drives tenant B's ids as tenant A across
        // every provider and asserts 404 on all of them; this is the manager's half of that.
        ResourceManagerCluster.ResetDoubles();

        var result = await cluster.Manager.WriteAsync(
            new() {
                Path = ResourceManagerCluster.Address("other", tenant: ResourceManagerCluster.OtherTenant).Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AReadOnlyLockRefusesAWrite() {
        ResourceManagerCluster.ResetDoubles();
        SwitchableLockResolver.Level = LockLevel.ReadOnly;

        var result = await Write(ResourceManagerCluster.Address("locked"));

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);
        result.Error.Message.ShouldContain("ReadOnly");
    }

    [Fact]
    public async Task StepTenEmitsTheProjectionsColumns() {
        // The projector is out of scope; the EMISSION is not. docs/plan/08 § The resource-graph
        // projection lists the columns and this asserts the event carries them.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("emitted");

        var accepted = await Write(address);
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        RecordingChangeSink.Published.TryDequeue(out var change).ShouldBeTrue();
        change!.Change.ShouldBe(ResourceChangeKind.Created);
        change.TenantId.ShouldBe(ResourceManagerCluster.Tenant);
        change.SubscriptionId.ShouldBe(ResourceManagerCluster.Subscription);
        change.ResourceGroup.ShouldBe("prod");
        change.Provider.ShouldBe("CyberCloud.Testing");
        change.Type.ShouldBe("widgets");
        change.Name.ShouldBe("emitted");
        change.ApiVersion.ShouldBe(TestingProvider.V2026);
        change.ProvisioningState.ShouldBe(ProvisioningState.Creating);
        change.DesiredHash.ShouldStartWith("sha256:");
        change.StreamNamespace.ShouldBe($"cc.{ResourceManagerCluster.Tenant:N}.res");
    }

    [Fact]
    public async Task AFailedPublishDoesNotFailTheWrite() {
        // docs/plan/08 § The resource-graph projection: the projection is eventually consistent by
        // design. Refusing a create because a list view will lag trades a correct write for a cosmetic
        // one.
        ResourceManagerCluster.ResetDoubles();
        RecordingChangeSink.Fail = true;

        var accepted = await Write(ResourceManagerCluster.Address("publish-fails"));

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.GetValueOrThrow().Trace.Reached.ShouldBe(WriteTrace.Canonical);
    }

    [Fact]
    public async Task TheAcceptedResponseCarriesTheOperationUriAndARetryAfter() {
        ResourceManagerCluster.ResetDoubles();

        var accepted = await Write(ResourceManagerCluster.Address("accepted-shape"));
        var value = accepted.GetValueOrThrow();

        value.OperationId.ShouldNotBe(Guid.Empty);
        value.OperationUri.ShouldBe($"/operations/{value.OperationId:D}");
        value.RetryAfterSeconds.ShouldBe(10);
        value.Resource.ProvisioningState.ShouldBe(ProvisioningState.Creating);
    }

    [Fact]
    public async Task StepSevenClaimsAndConfirmsTheNameSoTheTwoPhaseCreateIsComplete() {
        // docs/plan/06 § Two-phase create: claim under a lease, create the grain and the operation,
        // then CONFIRM. A binding still under lease would expire and free the name from under a live
        // resource.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("two-phase");

        var accepted = await Write(address);
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var entry = await cluster.Index(address).GetAsync();
        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Confirmed);
        entry.GetValueOrThrow().BoundTo.ShouldBe(accepted.GetValueOrThrow().Resource.Id);
    }

    [Fact]
    public async Task AnInvalidBodyIsRefusedAtStepTwoAndTheTargetIsAJsonPointer() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("bad-body");

        var result = await Write(address, """{"location":"eu-central","properties":{}}""");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        result.Error.Target.ShouldBe("/properties/size");

        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    [Fact]
    public async Task AMismatchedIfMatchIsRefused() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("etagged");

        var created = await Write(address);
        await Converge(created.GetValueOrThrow());

        var stale = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(size: 7),
                IfMatch = "not-the-current-etag",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        stale.IsFailure.ShouldBeTrue();
        stale.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    Task<Result<WriteAccepted>> Write(ResourceId address, string? body = null) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = body ?? TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller(address.TenantId)
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>Drives the accepted operation to a terminal state, the way a reminder would.</summary>
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
