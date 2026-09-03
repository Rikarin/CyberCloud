using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Shouldly;
using System.Collections.Concurrent;
using System.Globalization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The group-delete choreography end to end: seal, reclaim one namespace per cluster the group
///     ever touched, then the record, then the name.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is about what happens when the reclaim says NO, because the
///         cost of a wrong yes is a tenant's live data and the cost of a wrong no is a namespace an
///         operator deletes by hand.</b> The one success case exists so that "refuses always" cannot
///         pass for correct.
///     </para>
///     <para>
///         ⚠ <b>What only <c>CyberCloud.AppHost.Tests</c> can show.</b>
///         <see cref="ListingConnection" /> answers whatever a test scripts, so the enumeration here
///         is a list. Against a real API server the namespace also holds
///         <c>ServiceAccount/default</c> and <c>ConfigMap/kube-root-ca.crt</c>, which is the whole
///         reason <c>NamespaceReclaim.IsAmbient</c> exists, and no fake would have produced them.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class GroupReclaimTests(ResourceManagerCluster cluster) {
    static Guid Cluster { get; } = new("7c1a0000-0000-4000-8000-000000000001");

    // ── The refusals ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGroupThatStillHoldsAResourceIsRefusedAndNothingIsDeleted() {
        var (scope, connection, reclaimer) = await BuildAsync("held");
        var member = Address(scope, "web-01");

        (await Group(scope).BeginCreateAsync(member)).IsSuccess.ShouldBeTrue();

        var refused = await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken);

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.Conflict);
        error.Message.ShouldContain(member.CanonicalPath);

        connection.Deleted.ShouldBeEmpty("the refusal happens above the cluster, not at it.");
        (await Group(scope).GetAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ANamespaceHoldingSomethingElseLeavesTheGroupSealedAndTheNamespaceAlone() {
        // ⚠ THE CASE WITH THE BLAST RADIUS. A tenant's own claim, in the platform's namespace, is
        // also the exact shape of the volume a soft-deleted resource is restored from.
        var (scope, connection, reclaimer) = await BuildAsync("occupied");

        (await Group(scope).RecordClusterAsync(Cluster)).IsSuccess.ShouldBeTrue();
        connection.Occupants.Add(("PersistentVolumeClaim", "data-harbor-database-0"));

        var refused = await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken);

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Message.ShouldContain("data-harbor-database-0");

        connection.Deleted.ShouldBeEmpty();

        var record = await Group(scope).GetAsync();

        record.IsSuccess.ShouldBeTrue("the group's record stays while its namespace does.");

        record.GetValueOrThrow().State.ShouldBe(
            ProvisioningState.Deleting,
            "a delete that began and did not finish stays visible in Deleting, exactly as a member's "
            + "does — and the choreography is re-drivable from there."
        );
    }

    [Fact]
    public async Task ARecordedClusterWithNoConnectionIsARefusalAndNotASkip() {
        // ⚠ "Could not connect" must never converge into "reclaimed". That is how a group is
        // reported deleted while its namespace and everything in it stays, with nothing left that
        // knows the namespace was ever anybody's.
        var (scope, connection, reclaimer) = await BuildAsync("unreachable");

        (await Group(scope).RecordClusterAsync(new("7c1a0000-0000-4000-8000-0000000000ff"))).IsSuccess
            .ShouldBeTrue();

        var refused = await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken);

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Message.ShouldContain("7c1a0000-0000-4000-8000-0000000000ff");

        connection.Deleted.ShouldBeEmpty();
        (await Group(scope).GetAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ANamespaceThatCannotBeEnumeratedIsARefusal() {
        var (scope, connection, reclaimer) = await BuildAsync("blind");

        (await Group(scope).RecordClusterAsync(Cluster)).IsSuccess.ShouldBeTrue();
        connection.RefuseListing = ErrorCode.AuthorizationFailed;

        var refused = await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken);

        refused.TryGetError(out var error).ShouldBeTrue();

        error.Code.ShouldBe(
            ErrorCode.AuthorizationFailed,
            "the cluster's own code is what says whether another attempt could differ."
        );

        connection.Deleted.ShouldBeEmpty();
    }

    // ── The success ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEmptyGroupWithAnEmptyNamespaceIsDeletedAndTheNameIsFreedLast() {
        var (scope, connection, reclaimer) = await BuildAsync("reclaimable");

        (await Group(scope).RecordClusterAsync(Cluster)).IsSuccess.ShouldBeTrue();

        // ⚠ Ambient occupants and nothing else — what a real namespace that has finished being used
        // actually holds. Under the original "nothing at all" rule this case was unreachable.
        connection.Occupants.Add(("ServiceAccount", "default"));
        connection.Occupants.Add(("ConfigMap", "kube-root-ca.crt"));

        var deleted = await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken);

        deleted.IsSuccess.ShouldBeTrue(deleted.TryGetError(out var why) ? why.Message : string.Empty);

        connection.Deleted.ShouldHaveSingleItem();

        connection.Deleted[0].ShouldBe(
            ReconcileDriver.NamespaceFor(scope.SubscriptionId, scope.ResourceGroup),
            "the namespace a group's objects live in is derived from the group's own coordinates, and "
            + "a delete that computed a different one would report a clean reclaim and leave the real "
            + "namespace behind."
        );

        (await Group(scope).GetAsync()).IsFailure.ShouldBeTrue();

        (await Subscription().ListResourceGroupsAsync()).GetValueOrThrow()
            .ShouldNotContain(
                scope.ResourceGroup,
                "the subscription's listing entry goes last — but it does go."
            );
    }

    [Fact]
    public async Task AGroupThatNeverPlacedAnythingIsDeletedWithoutTouchingAnyCluster() {
        // A group created and never used has no namespace anywhere, so there is nothing to reclaim.
        // ⚠ The same answer is given for a group whose resources were placed before clusters were
        // recorded at all — see IResourceGroupGrain.ListClustersAsync on why an empty list is not
        // proof that no namespace exists.
        var (scope, connection, reclaimer) = await BuildAsync("untouched");

        (await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        connection.Listed.ShouldBeEmpty();
        connection.Deleted.ShouldBeEmpty();
        (await Group(scope).GetAsync()).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ADeleteIsIdempotentOnAGroupThatIsAlreadyGone() {
        var (scope, _, reclaimer) = await BuildAsync("twice");

        (await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await reclaimer.DeleteAsync(scope, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    // ── The harness ──────────────────────────────────────────────────────────────────────────────

    ISubscriptionGrain Subscription() =>
        cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(ResourceManagerCluster.IsolatedSubscription));

    IResourceGroupGrain Group(ScopeId scope) =>
        cluster.For(scope.TenantId)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(scope.SubscriptionId, scope.ResourceGroup));

    static ResourceId Address(ScopeId scope, string name) =>
        new(
            scope.TenantId,
            scope.SubscriptionId,
            scope.ResourceGroup,
            ConformingReconciler.TypeName,
            name,
            Guid.NewGuid()
        );

    async Task<(ScopeId Scope, ListingConnection Connection, ResourceGroupReclaimer Reclaimer)> BuildAsync(
        string groupName
    ) {
        // ⚠ A group of its own per test, in IsolatedSubscription. The suite's shared `prod` group is
        // what every other class writes into, and this one deletes what it addresses.
        var scope = ScopeId.Group(
            ResourceManagerCluster.Tenant,
            ResourceManagerCluster.IsolatedSubscription,
            groupName
        );

        (await Subscription().CreateResourceGroupAsync(groupName, "eu-west-1")).IsSuccess.ShouldBeTrue();

        var connection = new ListingConnection(Cluster);

        var reclaimer = new ResourceGroupReclaimer(
            cluster.Grains,
            new OneConnectionFactory(connection),
            new ConnectionNamespaceInventory(new OneConnectionFactory(connection)),
            new NamespaceEnsurer(TestClock.Instance),
            NullLogger<ResourceGroupReclaimer>.Instance
        );

        return (scope, connection, reclaimer);
    }

    /// <summary>
    ///     A connection that answers a scripted namespace listing and records what was deleted.
    /// </summary>
    /// <remarks>
    ///     ⚠ Occupants carry no labels, so every one reads as <b>unmanaged</b> — the conservative
    ///     answer, and the one a reclaim has to refuse over. A test that wanted the managed case
    ///     would be asserting <c>NamespaceReclaim</c>'s arithmetic, which
    ///     <c>NamespaceEnsurerTests</c> already does without an Orleans cluster.
    /// </remarks>
    sealed class ListingConnection(Guid cluster) : IKubeClusterConnection {
        readonly ConcurrentQueue<string> deleted = new();
        readonly ConcurrentQueue<string> listed = new();

        public Guid ClusterId => cluster;

        /// <summary>What the namespace holds, as (kind, name).</summary>
        public List<(string Kind, string Name)> Occupants { get; } = [];

        /// <summary>When set, the listing fails with this code instead of answering.</summary>
        public ErrorCode? RefuseListing { get; set; }

        /// <summary>Every namespace this connection was asked to enumerate.</summary>
        public IReadOnlyList<string> Listed => [.. listed];

        /// <summary>Every namespace this connection was told to delete.</summary>
        public IReadOnlyList<string> Deleted => [.. deleted];

        public Task<Result<ApplyOutcome>> ApplyAsync(
            KubeCommand command,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException();

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(command);
            deleted.Enqueue(command.Target.Name);
            return Task.FromResult(Result.Success);
        }

        public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListNamespaceAsync(
            string ns,
            CancellationToken cancellationToken = default
        ) {
            listed.Enqueue(ns);

            if (RefuseListing is { } refusal) {
                return Task.FromResult(
                    Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                        refusal,
                        $"Cluster {cluster:D} refused to enumerate '{ns}'."
                    )
                );
            }

            return Task.FromResult(
                Result<IReadOnlyList<KubeObjectSummary>>.Success(
                    [
                        .. Occupants.Select(
                            x => new KubeObjectSummary {
                                Kind = new() { Group = "", Version = "v1", Kind = x.Kind, Plural = "" },
                                Namespace = ns,
                                Name = x.Name,
                                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                            }
                        )
                    ]
                )
            );
        }
    }

    /// <summary>Hands out one connection, exactly as <c>GrainClusterConnectionFactory</c> does.</summary>
    sealed class OneConnectionFactory(IKubeClusterConnection connection) : IClusterConnectionFactory {
        public IKubeClusterConnection? Connect(Guid clusterId) =>
            clusterId == connection.ClusterId ? connection : null;
    }
}
