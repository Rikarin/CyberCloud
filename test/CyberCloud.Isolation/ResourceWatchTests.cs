using CyberCloud.Authorization.Contracts;
using CyberCloud.ResourceManager;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     The event half of the cross-resource seam, attacked through the real fan-out and the real
///     authorizer: a change reaches a watcher that may read the changed resource, and nobody else.
/// </summary>
/// <remarks>
///     <para>
///         <b>Three ways not to be told, one way to be.</b> A watcher in another subscription is not
///         in the changed resource's watch list — the key is the scope. A watcher smuggled into the
///         victim's list from another tenant is refused by the tenant gate on delivery. A watcher in
///         the same subscription that was never granted <c>read</c> is refused by the engine on
///         delivery. And a granted watcher is handed the event, with the changed resource's path, on
///         the same <c>PUT</c> that emitted it.
///     </para>
///     <para>
///         ⚠ <b>The delivery is synchronous with the write here, and that is a fixture fact.</b>
///         <c>ResourceManagerService.EmitAsync</c> awaits the fan-out, so when
///         <c>WriteAsync</c> returns the watcher's grain has the event. A projector-backed host
///         behaves the same; nothing here depends on a timing that a real silo would change.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class ResourceWatchTests(IsolationCluster cluster) {
    static IsolationTarget Widgets => IsolationCatalog.Targets[0];

    static IsolationTarget Probes => IsolationCatalog.Targets[1];

    [Fact]
    public async Task AGrantedWatcherInTheSameSubscriptionIsToldAndCanAcknowledge() {
        var watcher = await VictimResourceAsync(Widgets, "watch-granted");
        var watched = await VictimResourceAsync(Probes, "watch-granted-target");

        var (_, watch) = cluster.Views.For(watcher);
        (await watch.SubscribeAsync(Probes.Type, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        // Subscribing again is what every pass does, and it is a success that changes nothing.
        (await watch.SubscribeAsync(Probes.Type, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        await GrantReaderToResourceAsync(watcher.Id);

        await PatchAsync(watched, Probes, IsolationCluster.Victim, IsolationCluster.VictimUser);

        var input = (await cluster.For(IsolationCluster.Victim)
                .GetGrain<IResourceGrain>(GrainKeys.Resource(watcher.Id))
                .GetReconcileInputAsync()).GetValueOrThrow();

        input.PendingChanges.Length.ShouldBe(1, "the granted watcher was not told");
        input.PendingChanges[0].Change.ShouldBe(ResourceChangeKind.Updated);
        input.PendingChanges[0].ResourceId.ShouldBe(watched.Id);
        input.PendingChanges[0].Path.ShouldBe(watched.Path);
        input.PendingChanges[0].SubscriptionId.ShouldBe(IsolationCluster.VictimSubscription);
        input.ChangeSequence.ShouldBe(1);

        // Acknowledged — the way the driver does it after a converged pass — and gone.
        (await cluster.For(IsolationCluster.Victim)
                .GetGrain<IResourceGrain>(GrainKeys.Resource(watcher.Id))
                .AcknowledgeChangesAsync(input.ChangeSequence)).IsSuccess.ShouldBeTrue();

        (await Pending(IsolationCluster.Victim, watcher.Id)).ShouldBeEmpty();

        // Unsubscribed — and the next change is not delivered.
        (await watch.UnsubscribeAsync(Probes.Type, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await PatchAsync(watched, Probes, IsolationCluster.Victim, IsolationCluster.VictimUser, "eu-north");

        (await Pending(IsolationCluster.Victim, watcher.Id)).ShouldBeEmpty("an unsubscribed watcher was still told");
    }

    [Fact]
    public async Task AWatcherThatMayNotReadTheChangedResourceIsNotTold() {
        // ⚠ A subscription is not a grant. The event carries the path, the name and the tags, and a
        // watcher that could not GET the resource must not receive them by watching its type instead.
        var watcher = await VictimResourceAsync(Widgets, "watch-ungranted");
        var watched = await VictimResourceAsync(Probes, "watch-ungranted-target");

        (await cluster.Views.For(watcher).Watch.SubscribeAsync(Probes.Type, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        await PatchAsync(watched, Probes, IsolationCluster.Victim, IsolationCluster.VictimUser);

        (await Pending(IsolationCluster.Victim, watcher.Id)).ShouldBeEmpty(
            "an ungranted watcher learned a resource's path"
        );
    }

    [Fact]
    public async Task AWatcherInAnotherTenantHearsNothingThroughTheSeam() {
        var attacker = await AttackerResourceAsync(Widgets, "watch-attacker");
        var victim = await VictimResourceAsync(Probes, "watch-attacker-target");

        // The seam can only register the attacker in ITS subscription's list — that is the key.
        (await cluster.Views.For(attacker).Watch.SubscribeAsync(Probes.Type, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        await PatchAsync(victim, Probes, IsolationCluster.Victim, IsolationCluster.VictimUser);

        (await Pending(IsolationCluster.Attacker, attacker.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AWatcherSmuggledIntoAnotherTenantsListIsRefusedOnDelivery() {
        // ⚠ THE FAN-OUT'S OWN GATE, with the key bypassed. Nothing in production can write another
        // tenant's watch index, but the fan-out must not rely on that alone: an entry naming a
        // resource from another tenant is refused by the tenant gate before any tuple is read, and
        // the victim's write is not disturbed by it.
        var attacker = await AttackerResourceAsync(Widgets, "smuggled-attacker");
        var victim = await VictimResourceAsync(Probes, "smuggled-target");

        var smuggled = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceWatchGrain>(GrainKeys.WatchIndex(IsolationCluster.VictimSubscription, Probes.Type))
            .SubscribeAsync(new() { ResourceId = attacker.Id, Path = attacker.Path, Since = DateTimeOffset.UnixEpoch });

        smuggled.IsSuccess.ShouldBeTrue(smuggled.Error?.Message);

        // Even a tuple in the victim's tenant naming the attacker's GUID does not help — the gate is
        // before the engine.
        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.ResourceGroup,
                ReBacResourceAuthorizer.GroupObjectId(victim)
            ),
            Relations.Reader,
            SubjectRef.Of(ObjectTypes.Resource, attacker.Id)
        );

        var accepted = await PatchAsync(victim, Probes, IsolationCluster.Victim, IsolationCluster.VictimUser);
        accepted.IsSuccess.ShouldBeTrue(
            "a bystander's refusal disturbed the tenant's own write: " + accepted.Error?.Message
        );

        (await Pending(IsolationCluster.Attacker, attacker.Id)).ShouldBeEmpty(
            "the tenant gate on delivery did not hold"
        );

        // The victim tenant's grain for the attacker's GUID was never created by the delivery either.
        var ghost = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceGrain>(GrainKeys.Resource(attacker.Id))
            .GetReconcileInputAsync();

        ghost.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ADeletedWatcherIsDroppedFromTheIndexByTheFirstChangeItCannotBeHanded() {
        // ⚠ Deleting a resource does not unsubscribe it — the delete path knows nothing about what
        // the resource watched. The index is pruned lazily: the first fan-out that finds the
        // watcher's grain answering ResourceNotFound drops the entry, and the tenant's write that
        // triggered the fan-out is not disturbed by the failed delivery.
        var watcher = await VictimResourceAsync(Widgets, "watch-dead");
        var watched = await VictimResourceAsync(Probes, "watch-dead-target");

        (await cluster.Views.For(watcher).Watch.SubscribeAsync(Probes.Type, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        // Granted BEFORE the delete, because a grant to a resource is answered by the resource grain
        // and a grant to a gone one is refused — and because the fan-out checks the grant before it
        // tries the delivery. A dead watcher that was never granted is skipped by the check and
        // never pruned; this test is about the one that was.
        await GrantReaderToResourceAsync(watcher.Id);

        var index = cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceWatchGrain>(GrainKeys.WatchIndex(IsolationCluster.VictimSubscription, Probes.Type));

        (await index.ListAsync()).GetValueOrThrow()
            .ShouldContain(x => x.ResourceId == watcher.Id, "the subscription was not indexed");

        // ── The watcher goes: a widget has no soft-delete window, so its grain is cleared ────────
        var deleted = await cluster.Manager.DeleteAsync(
            new() {
                Path = watcher.Path,
                ApiVersion = Widgets.ApiVersion,
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser)
            },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        await DriveAsync(IsolationCluster.Victim, deleted.GetValueOrThrow().OperationId);

        var gone = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceGrain>(GrainKeys.Resource(watcher.Id))
            .GetReconcileInputAsync();
        gone.IsFailure.ShouldBeTrue("the fixture's delete left the watcher's grain in place, so nothing here is dead");

        // Still indexed: nothing on the delete path touched the watch list.
        (await index.ListAsync()).GetValueOrThrow()
            .ShouldContain(
                x => x.ResourceId == watcher.Id,
                "the delete path unsubscribed, which this test says it does not"
            );

        // ── The change: delivered to nobody, refused by the dead grain, and the write stands ──────
        var accepted = await PatchAsync(watched, Probes, IsolationCluster.Victim, IsolationCluster.VictimUser);
        accepted.IsSuccess.ShouldBeTrue(
            "a dead watcher's failed delivery disturbed the tenant's own write: " + accepted.Error?.Message
        );

        (await index.ListAsync()).GetValueOrThrow()
            .ShouldNotContain(x => x.ResourceId == watcher.Id, "the dead watcher was not pruned from the index");

        // And the next change costs the dead watcher nothing — the list is what the fan-out reads.
        var again = await PatchAsync(watched, Probes, IsolationCluster.Victim, IsolationCluster.VictimUser, "eu-north");
        again.IsSuccess.ShouldBeTrue(again.Error?.Message);
        (await index.ListAsync()).GetValueOrThrow().ShouldNotContain(x => x.ResourceId == watcher.Id);
    }

    [Fact]
    public async Task AWatcherIsNotToldAboutItsOwnWrites() {
        var watcher = await VictimResourceAsync(Widgets, "watch-self");

        (await cluster.Views.For(watcher).Watch.SubscribeAsync(Widgets.Type, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        await GrantReaderToResourceAsync(watcher.Id);
        await PatchAsync(watcher, Widgets, IsolationCluster.Victim, IsolationCluster.VictimUser);

        (await Pending(IsolationCluster.Victim, watcher.Id)).ShouldBeEmpty(
            "a resource was told about the pass it is already in"
        );
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    async Task<ImmutableArray<ResourceChangedEvent>> Pending(Guid tenant, Guid resourceId) =>
        (await cluster.For(tenant).GetGrain<IResourceGrain>(GrainKeys.Resource(resourceId)).GetReconcileInputAsync())
        .GetValueOrThrow()
        .PendingChanges;

    async Task<Result<WriteAccepted>> PatchAsync(
        ResourceId target,
        IsolationTarget type,
        Guid tenant,
        string user,
        string location = "eu-west"
    ) {
        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = target.Path,
                ApiVersion = type.ApiVersion,
                Verb = WriteVerb.Patch,
                Body = $$"""{"location":"{{location}}"}""",
                Caller = IsolationCluster.Caller(tenant, user)
            },
            TestContext.Current.CancellationToken
        );

        if (accepted.IsFailure) {
            return accepted;
        }

        // Driven to a terminal state so a second write in the same test is not refused as
        // concurrent. The fan-out has already run by the time WriteAsync returned.
        await DriveAsync(tenant, accepted.GetValueOrThrow().OperationId);

        return accepted;
    }

    async Task DriveAsync(Guid tenant, Guid operationId) {
        var operation = cluster.For(tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

        for (var i = 0; i < 6; i++) {
            if ((await operation.DriveAsync()).GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }

    async Task GrantReaderToResourceAsync(Guid resourceId) {
        var principal = resourceId.ToString("N", CultureInfo.InvariantCulture);

        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(IsolationCluster.Victim, IsolationCluster.VictimSubscription, IsolationCluster.Group),
            new(Relations.Reader, ObjectTypes.Resource, principal)
        );

        var granted = await cluster.Roles.AssignAsync(
            new() {
                Path = assignment.Path,
                Body =
                    $$$"""{"{{{RoleAssignmentBodyProperties.PrincipalId}}}":"{{{principal}}}","{{{RoleAssignmentBodyProperties.RoleDefinitionId}}}":"reader"}""",
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser)
            },
            TestContext.Current.CancellationToken
        );

        granted.IsSuccess.ShouldBeTrue("the fixture could not grant reader to a resource: " + granted.Error?.Message);
    }

    async Task<ResourceId> VictimResourceAsync(IsolationTarget target, string name) {
        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );
        return IsolationCluster.Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);
    }

    async Task<ResourceId> AttackerResourceAsync(IsolationTarget target, string name) {
        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Attacker,
            IsolationCluster.AttackerSubscription,
            IsolationCluster.AttackerUser
        );
        return IsolationCluster.Address(target, name, IsolationCluster.Attacker, IsolationCluster.AttackerSubscription)
            .WithId(id);
    }
}
