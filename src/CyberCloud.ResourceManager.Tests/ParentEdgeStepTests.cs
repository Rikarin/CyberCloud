using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Step 8 — the ReBAC <c>parent</c> edge — and the window its position closes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What is under test here is the <i>ordering</i> and the <i>rollback</i>, not the
///         tuple.</b> Whether the tuple <c>ReBacResourceRelationWriter</c> produces is one
///         <c>CyberCloudSchema</c> can actually walk is a question about the schema, and
///         <c>CyberCloud.Isolation</c>'s <c>ParentEdgeTests</c> answers it against the real engine.
///         What this suite owns is the write path: that the edge is written after the name is claimed
///         and before durable state exists, and that a failure at that point leaves nothing behind.
///     </para>
///     <para>
///         docs/plan/08 § The write path, end to end, step 8.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ParentEdgeStepTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task TheEdgeIsWrittenAfterTheNameIsClaimedAndBeforeTheResourceIsDurable() {
        // ⚠ BOTH BOUNDS MATTER AND THEY BOUND DIFFERENT FAILURES.
        //
        //   • After the claim: a create that lost the name race would otherwise have written a tuple
        //     for a resource that never existed.
        //   • Before the durable write: a resource that is durable and unlinked is invisible to its
        //     own creator, and stays invisible if the silo dies before the link.
        //
        // WriteTraceBuilder throws on a step recorded out of order, so this assertion and the write
        // path cannot silently drift apart.
        ResourceManagerCluster.ResetDoubles();

        var accepted = await Create(ResourceManagerCluster.Address("edge-ordering"));
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var reached = accepted.GetValueOrThrow().Trace.Reached;

        reached.ShouldContain(WriteStep.LinkParent);
        reached.IndexOf(WriteStep.IndexClaim).ShouldBeLessThan(reached.IndexOf(WriteStep.LinkParent));
        reached.IndexOf(WriteStep.LinkParent).ShouldBeLessThan(reached.IndexOf(WriteStep.SubmitDesired));

        RecordingRelationWriter.Edges.ShouldContainKey(accepted.GetValueOrThrow().Resource.Id);

        // And the edge points at the group within the subscription, not at a bare group name — a
        // bare name would merge the `prod` group of every subscription into one object.
        RecordingRelationWriter.Edges[accepted.GetValueOrThrow().Resource.Id]
            .ShouldContain(ResourceManagerCluster.Subscription.ToString("N"));
    }

    [Fact]
    public async Task AFailedEdgeWriteRefusesTheCreateAndLeavesNoResourceBehind() {
        // ⚠ THIS IS THE ANSWER TO "WHAT ON FAILURE?", AND IT IS ONLY AVAILABLE BECAUSE OF WHERE THE
        // STEP SITS. Nothing durable has been written yet, so the operation can simply fail: the
        // quota lease goes back, the index claim is left to expire, no resource grain exists and no
        // operation was started. There is nothing to roll back because nothing was committed.
        //
        // Had the edge been written after SubmitDesiredAsync, this same failure would have left a
        // durable resource that its creator could not see, and the only honest choices would have
        // been to delete a resource the caller was told about or to return 202 for something
        // invisible.
        ResourceManagerCluster.ResetDoubles();
        RecordingRelationWriter.FailLink = true;

        var address = ResourceManagerCluster.Address("edge-fails");

        // ⚠ A delta rather than "empty": the quota grain is shared with the rest of the collection
        // and holds every lease those tests took. What matters is that THIS create left none.
        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var before = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();

        try {
            var refused = await Create(address);

            refused.IsFailure.ShouldBeTrue("the create succeeded despite the parent edge not being written");

            var index = await cluster.Index(address).GetAsync();
            index.GetValueOrThrow().State.ShouldNotBe(
                IndexEntryState.Confirmed,
                "the name was permanently bound to a resource that was never created"
            );

            // No resource grain took durable state. The GUID is not knowable from outside, so this
            // asserts the observable equivalent: the path resolves to nothing.
            var read = await cluster.Manager.ReadAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = TestingProvider.V2026,
                    Caller = ResourceManagerCluster.Caller()
                },
                TestContext.Current.CancellationToken
            );

            read.IsFailure.ShouldBeTrue("a resource exists at a path whose create was refused");
            read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

            // ⚠ And the quota came back. A refused create that kept its lease would let a broken
            // tuple store eat a subscription's allowance one retry at a time.
            var after = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();
            after.ExceptWith(before);

            after.ShouldBeEmpty("the refused create kept its quota lease");
        }
        finally {
            RecordingRelationWriter.FailLink = false;
        }
    }

    [Fact]
    public async Task AConvergedDeleteRemovesTheEdgeAndAnAcceptedOneDoesNot() {
        // ⚠ THE TIMING OF THE UNLINK IS THE WHOLE OF IT. A resource in Deleting is still visible —
        // docs/plan/06 § Two-phase create calls that a billing-dispute prevention measure — so the
        // edge has to outlive the ACCEPTED delete and die with the CONVERGED one. Unlinking at the
        // request would have blinded the owner to their own teardown, indefinitely if it kept
        // failing.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("edge-unlink");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow().OperationId);

        var resourceId = created.GetValueOrThrow().Resource.Id;
        RecordingRelationWriter.Edges.ShouldContainKey(resourceId);

        var deleted = await cluster.Manager.DeleteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        RecordingRelationWriter.Edges.ShouldContainKey(
            resourceId,
            "the edge was removed when the delete was accepted, so the owner cannot read a resource "
            + "that is still up"
        );

        await Converge(deleted.GetValueOrThrow().OperationId);

        RecordingRelationWriter.Edges.ShouldNotContainKey(
            resourceId,
            "the resource is gone and its parent tuple is not — a row per resource ever deleted"
        );
    }

    [Fact]
    public async Task AnUpdateThatFailsDoesNotTakeTheEdgeAwayFromAResourceThatStillExists() {
        // ⚠ THE ROLLBACK IS GATED ON "WAS THIS A CREATE", AND THIS IS WHY. An update writes the same
        // edge idempotently; if the desired write then fails, removing the edge would take a LIVE
        // resource away from its owner because an unrelated PUT was malformed at the grain.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("edge-update-fails");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow().OperationId);

        var resourceId = created.GetValueOrThrow().Resource.Id;

        // An If-Match that cannot match: the grain refuses at SubmitDesiredAsync, which is the step
        // after the edge was rewritten.
        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(size: 7),
                IfMatch = "\"not-the-etag\"",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("a stale If-Match was accepted");

        RecordingRelationWriter.Edges.ShouldContainKey(
            resourceId,
            "a failed UPDATE unlinked a resource that still exists, so a mistyped If-Match made "
            + "somebody's resource invisible"
        );

        var read = await cluster.Manager.ReadAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
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

    async Task Converge(Guid operationId) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, operationId);

        for (var i = 0; i < 5; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
