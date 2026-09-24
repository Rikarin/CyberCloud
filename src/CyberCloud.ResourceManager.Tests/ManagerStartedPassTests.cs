using CyberCloud.ResourceManager.Reconcile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     docs/plan/08 § The manager-started pass: a type that declares <c>PassEvery</c> gets a reconcile
///     pass with nobody writing to it — a reminder, jittered, per resource, cancelled on delete.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The reminder row is read out of the silo's own <see cref="IReminderTable" /></b>, the
///         way <c>OrphanReaperArmingTests</c> reads the reaper's: a grain method that answered "I
///         armed" would be a proxy, and the row is the fact.
///     </para>
///     <para>
///         ⚠ <b>The tick is driven through <see cref="IResourceGrain.RunPeriodicPassAsync" /></b>, the
///         body the reminder calls, for <c>OperationGrain.DriveAsync</c>'s reason: the reminder's
///         floor is a minute. What a real tick adds — that Orleans calls <c>ReceiveReminder</c> — is
///         Orleans'.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ManagerStartedPassTests(ResourceManagerCluster cluster) {
    IReminderTable Reminders => cluster.SiloServices.GetRequiredService<IReminderTable>();

    [Fact]
    public async Task AConvergedResourceOfAPeriodicTypeIsArmedAndATickReconcilesItWithoutChangingIt() {
        ResourceManagerCluster.ResetDoubles();
        var (id, grain) = await CreatedAsync("pass-armed");

        var row = await Reminders.ReadRow(grain.GetGrainId(), PeriodicPass.ReminderName);
        row.ShouldNotBeNull("a converged gauge holds no reminder, so nothing would ever pass over it");
        row.Period.ShouldBe(PeriodicPass.MinimumPeriod);

        var before = await Snapshot(grain);
        var passes = FakeWorld.Passes.GetValueOrDefault(id);
        var published = RecordingChangeSink.Published.Count(x => x.ResourceId == id);

        var started = await grain.RunPeriodicPassAsync();
        started.IsSuccess.ShouldBeTrue(started.Error?.Message);
        started.GetValueOrThrow().ShouldNotBe(Guid.Empty, "a resource at rest with a period is due a pass");

        var status = await DriveAsync(started.GetValueOrThrow());
        status.State.ShouldBe(OperationState.Succeeded, status.Error?.Message);

        FakeWorld.Passes[id].ShouldBe(passes + 1, "the tick started an operation that never reached the reconciler");

        // ⚠ NOTHING ABOUT THE RESOURCE MOVED. A pass nobody asked for is not a write: no new etag, no
        // Updating, no operation id left on the resource for a tenant's PUT to collide with.
        var after = await Snapshot(grain);
        after.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
        after.Etag.ShouldBe(before.Etag);
        after.OperationId.ShouldBe(Guid.Empty);
        after.Body.ShouldBe(before.Body);

        // ⚠ The two a write's ending would leave and a pass must not: a new version — the transition
        // count the projection orders by — and a resource-changed event waking every blade watching it.
        after.Version.ShouldBe(before.Version, "the pass ended like a write and stamped a transition on the resource");
        RecordingChangeSink.Published.Count(x => x.ResourceId == id)
            .ShouldBe(published, "the pass announced a state change nobody made");
    }

    [Fact]
    public async Task ATickWhileThePreviousPassIsStillRunningStartsNothing() {
        ResourceManagerCluster.ResetDoubles();
        var (id, grain) = await CreatedAsync("pass-once");

        FakeWorld.StayInProgress[id] = true;

        var first = (await grain.RunPeriodicPassAsync()).GetValueOrThrow();
        (await cluster.Operation(ResourceManagerCluster.Tenant, first).DriveAsync()).GetValueOrThrow()
            .IsTerminal.ShouldBeFalse();

        (await grain.RunPeriodicPassAsync()).GetValueOrThrow()
            .ShouldBe(Guid.Empty, "two drivers of one resource is the race the single-writer guard exists to prevent");

        FakeWorld.StayInProgress.TryRemove(id, out _);
        (await DriveAsync(first)).State.ShouldBe(OperationState.Succeeded);

        (await grain.RunPeriodicPassAsync()).GetValueOrThrow().ShouldNotBe(Guid.Empty, "the previous pass finished");
    }

    [Fact]
    public async Task AFailedPassIsTheOperationsFailureAndLeavesTheResourceSucceeded() {
        ResourceManagerCluster.ResetDoubles();
        var (id, grain) = await CreatedAsync("pass-fails");

        FakeWorld.FailWith[id] = "the retention prune could not list the recovery points";
        FakeWorld.FailCode[id] = ErrorCode.InvalidRequestBody;

        var status = await DriveAsync((await grain.RunPeriodicPassAsync()).GetValueOrThrow());

        status.State.ShouldBe(OperationState.Failed);
        status.Error!.Message.ShouldContain("could not list");

        var after = await Snapshot(grain);
        after.ProvisioningState.ShouldBe(
            ProvisioningState.Succeeded,
            "a converged resource reported Failed because a pass nobody asked for could not finish"
        );
        after.LastFailure.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADeleteRemovesTheReminderAndATickOverAnythingButARestingResourceStartsNothing() {
        ResourceManagerCluster.ResetDoubles();
        var (id, grain) = await CreatedAsync("pass-deleted");
        var address = ResourceManagerCluster.Address("pass-deleted") with { Type = TestingProvider.PeriodicTypeName };

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        (await Reminders.ReadRow(grain.GetGrainId(), PeriodicPass.ReminderName))
            .ShouldBeNull("the delete began and the reminder is still there, waking a resource on its way out");

        // Deleting, not at rest: a tick that fired before the unregister landed does nothing.
        (await grain.RunPeriodicPassAsync()).GetValueOrThrow().ShouldBe(Guid.Empty);

        (await DriveAsync(deleted.GetValueOrThrow().OperationId)).State.ShouldBe(OperationState.Succeeded);
        (await grain.RunPeriodicPassAsync()).IsFailure.ShouldBeTrue("a pass over a resource that is gone");
        FakeWorld.Passes.GetValueOrDefault(id).ShouldBe(1, "only the create reconciled it");
    }

    [Fact]
    public async Task AWriteThatBeginsWhileAPassIsRunningStopsThePassBeforeApplyingAnything() {
        ResourceManagerCluster.ResetDoubles();
        var (id, grain) = await CreatedAsync("pass-overtaken");

        // A pass that has read the old body and has not finished — a vault backing off.
        FakeWorld.StayInProgress[id] = true;
        var refresh = (await grain.RunPeriodicPassAsync()).GetValueOrThrow();
        (await cluster.Operation(ResourceManagerCluster.Tenant, refresh).DriveAsync()).GetValueOrThrow()
            .IsTerminal.ShouldBeFalse();
        FakeWorld.StayInProgress.TryRemove(id, out _);

        var write = (await cluster.Manager.WriteAsync(
            new() {
                Path = Address("pass-overtaken").Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(3, "second"),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        (await DriveAsync(write.OperationId)).State.ShouldBe(OperationState.Succeeded);

        // ⚠ #30'S REVIEW: nothing consulted the running pass, so a refresh that had read the old body
        // could apply it after this write converged. The write's pass stops it first.
        var stopped = (await cluster.Operation(ResourceManagerCluster.Tenant, refresh).GetAsync()).GetValueOrThrow();
        stopped.State.ShouldBe(OperationState.Canceled, "the periodic pass outlived a write that began after it");

        var passes = FakeWorld.Passes[id];
        (await DriveAsync(refresh)).State.ShouldBe(OperationState.Canceled);
        FakeWorld.Passes[id].ShouldBe(passes, "a stopped pass reached the reconciler");
        FakeWorld.Applied[id].ShouldContain("second", Case.Sensitive, "the older body was applied over the write's");

        (await grain.RunPeriodicPassAsync()).GetValueOrThrow()
            .ShouldNotBe(Guid.Empty, "the stopped pass is still recorded as running, so no pass ever starts again");
    }

    [Fact]
    public async Task ADeleteThatBeginsWhileAPassIsRunningStopsThePassBeforeTheTeardown() {
        ResourceManagerCluster.ResetDoubles();
        var (id, grain) = await CreatedAsync("pass-deleted-under");

        FakeWorld.StayInProgress[id] = true;
        var refresh = (await grain.RunPeriodicPassAsync()).GetValueOrThrow();
        (await cluster.Operation(ResourceManagerCluster.Tenant, refresh).DriveAsync()).GetValueOrThrow()
            .IsTerminal.ShouldBeFalse();
        FakeWorld.StayInProgress.TryRemove(id, out _);

        var deleted = (await cluster.Manager.DeleteAsync(
            new() {
                Path = Address("pass-deleted-under").Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        (await DriveAsync(deleted.OperationId)).State.ShouldBe(OperationState.Succeeded);

        var passes = FakeWorld.Passes[id];
        (await DriveAsync(refresh)).State.ShouldBe(OperationState.Canceled, "the pass outlived the delete");
        FakeWorld.Passes[id].ShouldBe(passes, "a pass re-applied what the delete tore down");
    }

    [Fact]
    public async Task AResourceThatConvergedWithNoReminderIsArmedByTheSiloStartBackfill() {
        ResourceManagerCluster.ResetDoubles();
        var address = new ResourceId(
            ResourceManagerCluster.Tenant,
            ResourceManagerCluster.IsolatedSubscription,
            "pass-backfill",
            TestingProvider.PeriodicTypeName,
            "pass-backfilled",
            Guid.Empty
        );

        var tenant = cluster.For(ResourceManagerCluster.Tenant);
        _ = await tenant.GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(ResourceManagerCluster.IsolatedSubscription))
            .CreateResourceGroupAsync(address.ResourceGroup, "eu-west-1");

        var (_, grain) = await CreatedAsync(address);

        // ⚠ The state #30's review found: a vault that converged before manager-started passes
        // shipped, or whose row went with a restored reminder table, has no reminder, and only a write
        // would give it one.
        var row = await Reminders.ReadRow(grain.GetGrainId(), PeriodicPass.ReminderName);
        await Reminders.RemoveRow(grain.GetGrainId(), PeriodicPass.ReminderName, row!.ETag);
        (await Reminders.ReadRow(grain.GetGrainId(), PeriodicPass.ReminderName)).ShouldBeNull();

        await RegisterInDirectoryAsync();

        var backfill = new PeriodicPassBackfill(
            cluster.Grains,
            cluster.Registry,
            Options.Create(new PeriodicPassBackfillOptions()),
            NullLogger<PeriodicPassBackfill>.Instance
        );

        var covered = await backfill.RunAsync(TestContext.Current.CancellationToken);

        covered.Unreadable.ShouldBe(0);
        covered.Armed.ShouldBeGreaterThanOrEqualTo(1);
        (await Reminders.ReadRow(grain.GetGrainId(), PeriodicPass.ReminderName))
            .ShouldNotBeNull("the backfill walked past a converged gauge with no reminder");

        (await grain.ArmPeriodicPassAsync()).GetValueOrThrow().ShouldBeFalse("a second arm replaced a reminder that was there");
    }

    [Fact]
    public async Task ATypeWithNoPeriodIsNeverArmed() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("pass-none");

        var accepted = (await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        (await DriveAsync(accepted.OperationId)).State.ShouldBe(OperationState.Succeeded);

        var grain = cluster.Resource(ResourceManagerCluster.Tenant, accepted.Resource.Id);
        (await Reminders.ReadRow(grain.GetGrainId(), PeriodicPass.ReminderName)).ShouldBeNull();
        (await grain.RunPeriodicPassAsync()).GetValueOrThrow().ShouldBe(Guid.Empty);
    }

    [Fact]
    public void TheFirstDueTimeIsInTheSecondHalfOfThePeriodAndStableForOneResource() {
        var period = TimeSpan.FromHours(1);

        for (var i = 0; i < 200; i++) {
            var resource = Guid.NewGuid();
            var due = PeriodicPass.FirstDue(resource, period);

            due.ShouldBeGreaterThanOrEqualTo(period / 2);
            due.ShouldBeLessThanOrEqualTo(period);
            PeriodicPass.FirstDue(resource, period).ShouldBe(due, "re-arming would move the resource's slot");
        }
    }

    static ResourceId Address(string name) => ResourceManagerCluster.Address(name) with { Type = TestingProvider.PeriodicTypeName };

    Task<(Guid Id, IResourceGrain Grain)> CreatedAsync(string name) => CreatedAsync(Address(name));

    /// <summary>
    ///     Puts the suite's tenant and <see cref="ResourceManagerCluster.IsolatedSubscription" /> where a
    ///     backfill's walk finds them — <c>ExpirySweeperTests</c> does the same, and every call is idempotent.
    /// </summary>
    async Task RegisterInDirectoryAsync() {
        var tenant = cluster.For(ResourceManagerCluster.Tenant);
        var root = tenant.GetGrain<ITenantGrain>(GrainKeys.Tenant(ResourceManagerCluster.Tenant));

        _ = await root.CreateAsync("resource-manager-tests", "Resource manager tests", "eu-west-1");
        (await root.AddSubscriptionAsync(ResourceManagerCluster.IsolatedSubscription)).IsSuccess.ShouldBeTrue();

        (await cluster.Grains.GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
            .RegisterAsync(
                new() {
                    TenantId = ResourceManagerCluster.Tenant,
                    Slug = "resource-manager-tests",
                    HomeRegion = "eu-west-1",
                    Status = TenantStatus.Active
                }
            )).IsSuccess.ShouldBeTrue();
    }

    async Task<(Guid Id, IResourceGrain Grain)> CreatedAsync(ResourceId address) {
        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        (await DriveAsync(accepted.GetValueOrThrow().OperationId)).State.ShouldBe(OperationState.Succeeded);

        var id = accepted.GetValueOrThrow().Resource.Id;
        return (id, cluster.Resource(ResourceManagerCluster.Tenant, id));
    }

    static async Task<ResourceSnapshot> Snapshot(IResourceGrain grain) =>
        (await grain.GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026)).GetValueOrThrow();

    async Task<OperationStatus> DriveAsync(Guid operationId) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < 20; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }
        }

        return last!;
    }
}
