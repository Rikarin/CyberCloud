using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Tests.Infrastructure;
using System.Globalization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The driver's half of the cross-resource seam: a pass is handed the real
///     <see cref="IResourceView" /> and <see cref="IResourceWatch" />, bound to its own resource, and
///     the changes delivered to it — and acknowledges them only when it converges.
/// </summary>
/// <remarks>
///     <para>
///         The <i>rule</i> — who may see what — is driven through the real ReBAC engine in
///         <c>CyberCloud.Isolation</c> (<c>CrossResourceViewTests</c>, <c>ResourceWatchTests</c>),
///         because that suite is the one that composes it. What this class pins is the plumbing
///         between the driver and the grain that the isolation suite reaches around: that
///         <c>ReconcileDriver</c> replaces the refusing defaults, that a delivered change reaches the
///         next pass, and that acknowledgement follows convergence and not the read.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-verified.</b> With the <c>View = seams.View</c> line removed from the driver,
///         <see cref="APassIsHandedTheRealSeamsRatherThanTheRefusingDefaults" /> reports
///         <c>RefusingResourceView</c>; with the acknowledgement moved above the
///         <c>outcome.IsConverged</c> guard,
///         <see cref="AChangeReadByAPassThatDidNotConvergeIsSeenAgain" /> fails on the second pass's
///         count.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class CrossResourceSeamTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task APassIsHandedTheRealSeamsRatherThanTheRefusingDefaults() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("sees-the-seam");

        var accepted = (await Create(address)).GetValueOrThrow();
        await Converge(accepted);

        var seen = FakeWorld.Seams[accepted.Resource.Id];

        // ⚠ THE RUNTIME TYPE NAMES, because the refusing defaults implement the same interfaces and a
        // reconciler cannot tell them apart until its first call fails. A hand-built context carries
        // RefusingResourceView; a driver-built one must not.
        seen.View.ShouldBe(nameof(OwnedResourceView), "the driver handed the pass the refusing default view");
        seen.Watch.ShouldBe(nameof(OwnedResourceWatch), "the driver handed the pass the refusing default watch");
        seen.Changes.ShouldBe(0, "a resource that watches nothing was handed changes");
    }

    [Fact]
    public async Task ADeliveredChangeReachesTheNextPassAndIsAcknowledgedWhenItConverges() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("hears-a-change");

        var accepted = (await Create(address)).GetValueOrThrow();
        await Converge(accepted);

        var resource = cluster.Resource(ResourceManagerCluster.Tenant, accepted.Resource.Id);

        // Delivered the way ResourceWatchFanout delivers: straight into the watcher's grain.
        var handed = await resource.NotifyChangedAsync(ChangeAbout("a-share", 1));
        handed.IsSuccess.ShouldBeTrue(handed.Error?.Message);

        var input = (await resource.GetReconcileInputAsync()).GetValueOrThrow();
        input.PendingChanges.Length.ShouldBe(1);
        input.PendingChanges[0].Name.ShouldBe("a-share");
        input.ChangeSequence.ShouldBe(1);
        input.ChangesDropped.ShouldBe(0);

        // The next pass — a PATCH that changes the body — reads the change and converges.
        var updated = (await Patch(address)).GetValueOrThrow();
        await Converge(updated);

        FakeWorld.Seams[accepted.Resource.Id].Changes.ShouldBe(1, "the pass was not handed the delivered change");

        var after = (await resource.GetReconcileInputAsync()).GetValueOrThrow();
        after.PendingChanges.ShouldBeEmpty("a converged pass did not acknowledge what it read");
        after.ChangeSequence.ShouldBe(0);
    }

    [Fact]
    public async Task AChangeReadByAPassThatDidNotConvergeIsSeenAgain() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("keeps-a-change");

        var accepted = (await Create(address)).GetValueOrThrow();
        await Converge(accepted);

        var resource = cluster.Resource(ResourceManagerCluster.Tenant, accepted.Resource.Id);
        (await resource.NotifyChangedAsync(ChangeAbout("a-share", 1))).IsSuccess.ShouldBeTrue();

        // ⚠ InProgress: the pass read the change and has not finished acting on it, so it must see
        // it again. Acknowledging on the read would lose it to a pass that then failed.
        FakeWorld.StayInProgress[accepted.Resource.Id] = true;

        var updated = (await Patch(address)).GetValueOrThrow();
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, updated.OperationId);
        _ = await operation.DriveAsync();

        FakeWorld.Seams[accepted.Resource.Id].Changes.ShouldBe(1);

        var stillPending = (await resource.GetReconcileInputAsync()).GetValueOrThrow();
        stillPending.PendingChanges.Length.ShouldBe(1, "an InProgress pass acknowledged what it had not finished with");

        // Now it converges, and the change goes with it.
        FakeWorld.StayInProgress.TryRemove(accepted.Resource.Id, out _);
        await Converge(updated);

        (await resource.GetReconcileInputAsync()).GetValueOrThrow().PendingChanges.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheListIsBoundedAndTheDropIsCounted() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("bounded-changes");

        var accepted = (await Create(address)).GetValueOrThrow();
        await Converge(accepted);

        var resource = cluster.Resource(ResourceManagerCluster.Tenant, accepted.Resource.Id);

        for (var i = 1; i <= ReconcileInput.MaxPendingChanges + 3; i++) {
            (await resource.NotifyChangedAsync(ChangeAbout("share-" + i.ToString(CultureInfo.InvariantCulture), i)))
                .IsSuccess.ShouldBeTrue();
        }

        var input = (await resource.GetReconcileInputAsync()).GetValueOrThrow();
        input.PendingChanges.Length.ShouldBe(ReconcileInput.MaxPendingChanges);
        input.ChangesDropped.ShouldBe(3, "the reconciler is not told it missed something");

        // The oldest went, the newest stayed, and the sequence counted the dropped ones too.
        input.PendingChanges[0].Name.ShouldBe("share-4");
        input.PendingChanges[^1].Name.ShouldBe("share-" + (ReconcileInput.MaxPendingChanges + 3).ToString(CultureInfo.InvariantCulture));
        input.ChangeSequence.ShouldBe(ReconcileInput.MaxPendingChanges + 3);

        // Acknowledging everything read resets the count; a partial acknowledgement does not.
        (await resource.AcknowledgeChangesAsync(input.ChangeSequence - 1)).IsSuccess.ShouldBeTrue();
        var partial = (await resource.GetReconcileInputAsync()).GetValueOrThrow();
        partial.PendingChanges.Length.ShouldBe(1);
        partial.ChangesDropped.ShouldBe(3, "a partial acknowledgement forgot the drops the next pass still has to rescan for");

        (await resource.AcknowledgeChangesAsync(input.ChangeSequence)).IsSuccess.ShouldBeTrue();
        var caughtUp = (await resource.GetReconcileInputAsync()).GetValueOrThrow();
        caughtUp.PendingChanges.ShouldBeEmpty();
        caughtUp.ChangesDropped.ShouldBe(0);
    }

    [Fact]
    public async Task AResourceThatIsGoneRefusesADeliveryByName() {
        // How the fan-out learns to drop a dead watcher: not a silent success over nothing.
        var handed = await cluster.Resource(ResourceManagerCluster.Tenant, Guid.NewGuid())
            .NotifyChangedAsync(ChangeAbout("a-share", 1));

        handed.IsFailure.ShouldBeTrue();
        handed.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    static ResourceChangedEvent ChangeAbout(string name, long version) {
        var target = ResourceManagerCluster.Address(name);

        return new() {
            Change = ResourceChangeKind.Updated,
            ResourceId = Guid.NewGuid(),
            TenantId = target.TenantId,
            SubscriptionId = target.SubscriptionId,
            ResourceGroup = target.ResourceGroup,
            Provider = target.Type.Namespace,
            Type = target.Type.Type,
            Name = target.Name,
            Path = target.Path,
            Version = version
        };
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

    Task<Result<WriteAccepted>> Patch(ResourceId address) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Patch,
                Body = """{"properties":{"size":11}}""",
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
