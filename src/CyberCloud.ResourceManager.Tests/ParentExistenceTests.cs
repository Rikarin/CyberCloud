using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Step 1's third existence check: a child's <b>parent resource</b> is one that exists.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The write path validated the tenant, the subscription, the type and the api-version,
///         and never the parent.</b> docs/plan/12 § Child resources gave a child an address that
///         names its parent — <c>…/widgets/{widgetName}/gadgets/{gadgetName}</c> — and
///         <see cref="ResourceId.Parent" /> made that a pure function of the id, but nothing on the
///         write path called it. So a gadget could be created under a widget that was never created,
///         or one that had since been deleted, and every layer below step 1 agreed: the name was
///         free, the quota was there, the claim succeeded, the reconciler ran.
///     </para>
///     <para>
///         ⚠ <b>What is left behind is worse than a bad reference, because of what step 8 does with
///         it.</b> <c>IResourceRelationWriter.LinkToParentAsync</c> writes the ReBAC <c>parent</c>
///         edge from the address, so an orphan's edge points at a resource that does not exist —
///         permission inherits from nothing, and the interleaved grammar was chosen precisely so that
///         it would inherit from something.
///     </para>
///     <para>
///         ⚠ <b>Every refusal here is the same <c>404</c> a missing resource gets, byte for byte.</b>
///         A distinct "parent not found" would be an enumeration oracle for exactly the reason
///         docs/plan/07 § The enforcement seam gives for the subscription check: this runs
///         <i>before</i> the ReBAC seam, so a caller who may write in a resource group but may not
///         read a particular widget would otherwise learn, one probe at a time, which widget names
///         are live.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ParentExistenceTests(ResourceManagerCluster cluster) {
    /// <summary>The address of a gadget under a widget — the interleaved shape, in one place.</summary>
    static ResourceId Child(string parentName, string name) =>
        new(
            ResourceManagerCluster.Tenant,
            ResourceManagerCluster.Subscription,
            "prod",
            TestingProvider.ChildTypeName,
            name,
            Guid.Empty,
            parentName
        );

    /// <summary>
    ///     Drives an accepted operation to a terminal state, the way <c>MeteredAmountTests</c> does.
    /// </summary>
    /// <remarks>
    ///     A resource still <c>Creating</c> refuses the next write with <c>OperationInProgress</c>,
    ///     which would fail these tests for a reason that has nothing to do with parents.
    /// </remarks>
    async Task ConvergeAsync(WriteAccepted accepted) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            if ((await operation.DriveAsync()).GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }

    /// <summary>Creates a widget and drives it to terminal, so it can be deleted afterwards.</summary>
    async Task<ResourceId> CreateParentAsync(string name) {
        var address = ResourceManagerCluster.Address(name);

        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message ?? "the parent this suite is about could not be created");
        await ConvergeAsync(created.GetValueOrThrow());
        return address;
    }

    Task<Result<WriteAccepted>> CreateChildAsync(ResourceId child) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = child.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.ChildBody(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    [Fact]
    public async Task ACreateUnderAParentThatDoesNotExistIs404AndClaimsNothing() {
        ResourceManagerCluster.ResetDoubles();

        var child = Child("no-such-widget", "orphan");

        var refused = await CreateChildAsync(child);

        refused.IsFailure.ShouldBeTrue(
            "a child resource was created under a parent that does not exist — the parent was never "
            + "looked up, and an index entry and a ReBAC edge now exist for a resource whose owner is "
            + "an address that resolves to nothing"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // ⚠ Refused at step 1, so nothing below it ran — the same property the subscription check
        // holds. A check placed after the index claim would leave the child's own name taken by a
        // create that failed.
        var entry = await cluster.Index(child).GetAsync();
        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    [Fact]
    public async Task ACreateUnderAParentThatExistsIsAccepted() {
        // The other half, and it is the half that stops the check from being "refuse every child".
        ResourceManagerCluster.ResetDoubles();

        await CreateParentAsync("host-widget");

        var accepted = await CreateChildAsync(Child("host-widget", "attached"));

        accepted.IsSuccess.ShouldBeTrue(
            accepted.Error?.Message ?? "a child under a parent that exists was refused"
        );
    }

    [Fact]
    public async Task AParentUnderAnUnexpiredClaimReadsAsAbsent() {
        // ⚠ ONLY A CONFIRMED BINDING IS A PARENT. docs/plan/06 § Two-phase create makes a claim a
        // lease and not yet a resource, and IResourceIndexGrain.ResolveAsync resolves only the
        // confirmed state for exactly this reason: a claim may never become anything. A child hung
        // off one would outlive its parent's failure to exist.
        ResourceManagerCluster.ResetDoubles();

        var claimed = ResourceManagerCluster.Address("claimed-widget");

        // The claim, without the confirm — step 1 of two-phase create and no step 3.
        var claim = await cluster.Index(claimed).TryClaimAsync(claimed, Guid.NewGuid());
        claim.IsSuccess.ShouldBeTrue("the fixture could not put the parent's name under a claim");

        (await cluster.Index(claimed).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Claimed, "the fixture did not leave a claim behind");

        var refused = await CreateChildAsync(Child("claimed-widget", "premature"));

        refused.IsFailure.ShouldBeTrue(
            "a child was attached to a parent that is only CLAIMED — a name under an unexpired lease "
            + "is not a resource, and the create it belongs to may never finish"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AParentThatWasDeletedStopsAcceptingNewChildren() {
        // The second half of "or one that was deleted". Delete releases the index FIRST
        // (docs/plan/06 § Two-phase create, "so the name is immediately reusable"), so the parent
        // stops resolving the moment the delete is accepted — which is exactly when it must stop
        // being attachable, not when teardown converges.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("doomed-widget");

        var deleted = await cluster.Manager.DeleteAsync(
            new() {
                Path = parent.Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message ?? "the fixture could not delete the parent");

        var refused = await CreateChildAsync(Child("doomed-widget", "late-arrival"));

        refused.IsFailure.ShouldBeTrue("a child was attached to a parent that has been deleted");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AMissingParentAnswersByteIdenticallyToAMissingChild() {
        // ⚠ THE ORACLE THE STATUS CODE ALONE DOES NOT CLOSE, and the reason the check reuses
        // NotFound<T> rather than growing a code of its own. Two 404s whose messages differ are a way
        // to ask WHICH of the two things was absent — and "that widget does not exist" is the more
        // valuable answer, because it is a probe for the names of resources the caller may not be
        // allowed to read. The parent lookup runs before the ReBAC seam, so nothing else holds this
        // line.
        ResourceManagerCluster.ResetDoubles();

        await CreateParentAsync("oracle-widget");

        var orphan = Child("no-such-widget", "oracle-orphan");
        var absent = Child("oracle-widget", "oracle-absent");

        var onParent = await CreateChildAsync(orphan);

        var onChild = await cluster.Manager.ReadAsync(
            new() { Path = absent.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        onParent.IsFailure.ShouldBeTrue();
        onChild.IsFailure.ShouldBeTrue();

        onParent.Error!.Code.ShouldBe(onChild.Error!.Code);
        onParent.Error.Target.ShouldBe(onChild.Error.Target);
        onParent.Error.Details.ShouldBeEmpty();

        // The one legitimate difference is the path each caller supplied. Blank it out and the two
        // messages are the same bytes.
        //
        // ⚠ There is deliberately no "and it must not mention the parent's path" assertion here,
        // because under the interleaved grammar that is unsatisfiable rather than merely unmet: the
        // parent's path is a PREFIX of the child's, so quoting the address the caller supplied always
        // quotes the parent's too. That is why the byte-identity above is the whole guard — the
        // refusal may repeat what the caller said and must add nothing to it.
        onParent.Error.Message.Replace(orphan.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(
                onChild.Error.Message.Replace(absent.Path, "PATH", StringComparison.Ordinal),
                "the refusal says which layer refused it"
            );
    }

    [Fact]
    public async Task AnUpdateOfAnExistingChildDoesNotRecheckTheParent() {
        // ⚠ THE CHECK IS ON THE CREATE AND ONLY ON THE CREATE, and this test is what pins that.
        // Re-checking on every write would mean that deleting a parent silently froze every child —
        // a PATCH that had nothing to do with the parent would start answering 404 for a resource
        // that plainly exists, which is a worse failure than the one being closed. What SHOULD happen
        // to children when a parent is deleted is a lifecycle decision, recorded at
        // docs/plan/08 § Deleting a parent resource that has children; it is not this line's job.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("transient-widget");

        var child = Child("transient-widget", "survivor");
        var created = await CreateChildAsync(child);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await ConvergeAsync(created.GetValueOrThrow());

        // The parent goes away underneath the child, which is the case a check written on every
        // write rather than on the create would break.
        var removed = await cluster.Manager.DeleteAsync(
            new() {
                Path = parent.Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        removed.IsSuccess.ShouldBeTrue(removed.Error?.Message ?? "the fixture could not delete the parent");

        var updated = await cluster.Manager.WriteAsync(
            new() {
                Path = child.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Patch,
                Body = """{"location":"eu-west"}""",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        updated.IsSuccess.ShouldBeTrue(
            updated.Error?.Message
            ?? "an update to an existing child was refused, so the parent is being re-checked on "
            + "every write rather than at create"
        );
    }

    [Fact]
    public async Task ReadingAndDeletingAnExistingChildStillWork() {
        // The verbs share ResolveAsync, so a check that did not test `existing` would break all four.
        // A GET is the one an attacker probes with and the one a portal blade issues constantly.
        ResourceManagerCluster.ResetDoubles();

        await CreateParentAsync("readable-widget");

        var child = Child("readable-widget", "readable");
        var created = await CreateChildAsync(child);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await ConvergeAsync(created.GetValueOrThrow());

        var request = new WriteRequest {
            Path = child.Path,
            ApiVersion = TestingProvider.V2026,
            Caller = ResourceManagerCluster.Caller()
        };

        (await cluster.Manager.ReadAsync(request, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue("an existing child could not be read");

        (await cluster.Manager.DeleteAsync(request, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue("an existing child could not be deleted");
    }

    [Fact]
    public async Task ATopLevelTypeIsUnaffected() {
        // The regression guard. `widgets` has no parent — ResourceId.Parent is null for it — and the
        // check must be invisible. Every other suite in this project writes `widgets` and would fail
        // loudly, but failing loudly somewhere else is not the same as saying so here.
        ResourceManagerCluster.ResetDoubles();

        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = ResourceManagerCluster.Address("unparented").Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message ?? "a top-level type was caught by the parent check");
    }
}
