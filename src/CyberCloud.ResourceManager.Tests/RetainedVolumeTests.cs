using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <c>VolumeReclaimer</c> — the guard on the one path where the platform is supposed to destroy a
///     tenant's data.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Most of this file makes the guard FIRE, and that is deliberate rather than
///         thorough.</b> A guard nobody ever made refuse has not been verified — the same discipline
///         that found two defects in the namespace-reclaim guard, one of which meant every refusal
///         threw instead of refusing. So each case below hands the reclaimer a claim it must not
///         touch and asserts two things together: that it refused, and that the claim is <b>still
///         there</b> afterwards. The second half is what separates "refused" from "deleted it and
///         then reported a problem".
///     </para>
///     <para>
///         ⚠ <b>The refusals are non-retryable, and that is a property rather than a detail.</b> A
///         claim that is not this resource's will not become this resource's on the next attempt, so
///         a retryable refusal would be thirty-nine further opportunities to destroy it before
///         <c>ReconcileSchedule</c>'s ceiling. <c>OperationGrain</c> reads exactly this flag to
///         decide between failing the purge and re-driving it.
///     </para>
/// </remarks>
public sealed class RetainedVolumeTests {
    const string Namespace = "cc-sub-rg";

    static readonly ImmutableDictionary<string, string> Ownership =
        ImmutableDictionary<string, string>.Empty
            .Add("app.kubernetes.io/instance", "vault")
            .Add("app.kubernetes.io/component", "database");

    /// <summary>
    ///     ⚠ <b>The claim a teardown kept is removed, read back as gone, and reported converged.</b>
    /// </summary>
    [Fact]
    public async Task AClaimTheResourceOwnsIsRemovedAndTheReclaimConverges() {
        var cluster = new FakeClaimCluster();
        var claim = Claim("data-vault-0");
        cluster.Plant(claim, Ownership);

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(claim)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Error?.Message);
        cluster.Holds(claim).ShouldBeFalse("the claim the purge named is still in the cluster");
        cluster.Deleted.ShouldHaveSingleItem();
    }

    /// <summary>
    ///     ⚠ <b>A claim that is already gone converges, because a reclaim is re-driven from a
    ///     reminder and its second pass must be able to finish.</b>
    /// </summary>
    [Fact]
    public async Task AClaimThatIsAlreadyGoneConvergesAndDeletesNothing() {
        var cluster = new FakeClaimCluster();

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(Claim("data-vault-0"))]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Error?.Message);
        cluster.Deleted.ShouldBeEmpty("there was nothing there and a delete was issued anyway");
    }

    // ── The sabotage cases ─────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A claim carrying somebody else's labels is refused, and survives.</b> This is the
    ///     failure the whole guard exists for: a name this resource predicted, on an object that is
    ///     not its.
    /// </summary>
    [Fact]
    public async Task AClaimWhoseLabelsNameSomebodyElseIsRefusedAndSurvives() {
        var cluster = new FakeClaimCluster();
        var claim = Claim("data-vault-0");

        cluster.Plant(
            claim,
            ImmutableDictionary<string, string>.Empty
                .Add("app.kubernetes.io/instance", "somebody-elses-vault")
                .Add("app.kubernetes.io/component", "database")
        );

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(claim)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse("a claim that is not ours will not become ours on a retry");
        outcome.Error!.Message.ShouldContain("somebody-elses-vault");
        cluster.Holds(claim).ShouldBeTrue("the purge deleted a volume it did not own");
        cluster.Deleted.ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ <b>A claim carrying no labels at all is refused</b> — the shape a tenant's own hand-made
    ///     claim has, and the shape <c>NamespaceEnsurerTests</c> models as the unmanaged occupant.
    /// </summary>
    [Fact]
    public async Task AClaimWithNoLabelsAtAllIsRefusedAndSurvives() {
        var cluster = new FakeClaimCluster();
        var claim = Claim("data-vault-0");
        cluster.Plant(claim, ImmutableDictionary<string, string>.Empty, labelled: false);

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(claim)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse();
        cluster.Holds(claim).ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>A claim missing one of the labels is refused, not accepted on the strength of the
    ///     others.</b> Ownership is every pair or none — a subset match would accept any claim of the
    ///     same component belonging to a different instance.
    /// </summary>
    [Fact]
    public async Task AClaimMatchingAllButOneLabelIsRefused() {
        var cluster = new FakeClaimCluster();
        var claim = Claim("data-vault-0");

        cluster.Plant(
            claim,
            ImmutableDictionary<string, string>.Empty.Add("app.kubernetes.io/component", "database")
        );

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(claim)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse();
        outcome.Error!.Message.ShouldContain("app.kubernetes.io/instance");
        cluster.Holds(claim).ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>A claim in another namespace is refused before it is even read.</b> A namespace is
    ///     one resource group on one cluster, so this is the refusal that stands between a bug in one
    ///     provider and another tenant's disks.
    /// </summary>
    [Fact]
    public async Task AClaimInAnotherNamespaceIsRefusedWithoutBeingRead() {
        var cluster = new FakeClaimCluster();
        var elsewhere = new ObjectRef {
            Kind = RetainedVolume.ClaimKind, Namespace = "cc-sub-other-rg", Name = "data-vault-0"
        };

        cluster.Plant(elsewhere, Ownership);

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(elsewhere)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse();
        outcome.Error!.Message.ShouldContain("cc-sub-other-rg");
        cluster.Holds(elsewhere).ShouldBeTrue();
        cluster.Read.ShouldBeEmpty("the claim was read before its namespace was checked");
    }

    /// <summary>
    ///     ⚠ <b>A retained volume that is not a claim is refused.</b> A provider handing over its own
    ///     <c>StatefulSet</c> here would be asking the purge to do the teardown's job, one step after
    ///     the teardown has already converged.
    /// </summary>
    [Fact]
    public async Task AVolumeDeclaredAtTheWrongKindIsRefused() {
        var cluster = new FakeClaimCluster();
        var set = new ObjectRef {
            Kind = new() { Group = "apps", Version = "v1", Kind = "StatefulSet", Plural = "statefulsets" },
            Namespace = Namespace,
            Name = "vault"
        };

        cluster.Plant(set, Ownership);

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(set)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse();
        outcome.Error!.Message.ShouldContain("StatefulSet");
        cluster.Holds(set).ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>A claim declared with no ownership labels is refused.</b> A name with no evidence
    ///     behind it is the exact shape the guard exists to reject, and a provider is as capable of
    ///     producing it as a caller is.
    /// </summary>
    [Fact]
    public async Task AClaimDeclaredWithNoOwnershipLabelsIsRefused() {
        var cluster = new FakeClaimCluster();
        var claim = Claim("data-vault-0");
        cluster.Plant(claim, Ownership);

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler(
                [new RetainedVolume(claim, ImmutableDictionary<string, string>.Empty, "no evidence")]
            ),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse();
        cluster.Holds(claim).ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>One bad entry stops the whole reclaim, and the good ones beside it are untouched.</b>
    ///     A list whose third entry is not ours is a provider that got the list wrong, and a loop that
    ///     had already destroyed the first two would have acted on that wrong list before finding out.
    /// </summary>
    [Fact]
    public async Task OneUnownedClaimStopsTheWholeReclaimBeforeAnythingIsDeleted() {
        var cluster = new FakeClaimCluster();
        var mine = Claim("data-vault-0");
        var theirs = Claim("storage-vault-0");

        cluster.Plant(mine, Ownership);
        cluster.Plant(theirs, ImmutableDictionary<string, string>.Empty.Add("app.kubernetes.io/instance", "other"));

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(mine), Volume(theirs)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        cluster.Deleted.ShouldBeEmpty("the first claim was destroyed before the second was checked");
        cluster.Holds(mine).ShouldBeTrue();
        cluster.Holds(theirs).ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>A reclaim with claims to remove and no cluster to remove them from does not
    ///     converge.</b> A reconciler's own <c>DeleteAsync</c> converges in that state, because a
    ///     teardown with nothing to reach has nothing left to remove; here the provider has just said
    ///     there is something left, so converging would report disks destroyed that were never
    ///     reached.
    /// </summary>
    [Fact]
    public async Task VolumesWithNoClusterConnectionDoNotConverge() {
        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(Claim("data-vault-0"))]),
            Context(cluster: null),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeTrue("a connection that comes back should finish the job");
        outcome.Error!.Message.ShouldContain("data-vault-0");
    }

    /// <summary>
    ///     ⚠ <b>A type that keeps nothing converges with no cluster and no reads</b>, which is the
    ///     default on <c>IResourceReconciler</c> and the answer for most of the catalogue.
    /// </summary>
    [Fact]
    public async Task AReconcilerThatKeepsNothingConvergesWithoutTouchingAnything() {
        var cluster = new FakeClaimCluster();

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new KeepsNothingReconciler(),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        cluster.Read.ShouldBeEmpty();
        cluster.Deleted.ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ <b>A read that failed for any reason other than absence does not converge.</b> "The
    ///     cluster did not answer" and "the claim is gone" are the two answers this whole path must
    ///     keep apart: the first must retry and the second must finish.
    /// </summary>
    [Fact]
    public async Task AReadThatFailedForAnyOtherReasonRetriesRatherThanConverging() {
        var cluster = new FakeClaimCluster { ReadFailsWith = ErrorCode.AuthorizationFailed };

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(Claim("data-vault-0"))]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeTrue();
        cluster.Deleted.ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ <b>A claim still readable after its delete is <c>InProgress</c> rather than converged.</b>
    ///     The <c>pvc-protection</c> finalizer holds a claim until the pods that mounted it are gone,
    ///     so this is the ordinary case rather than an exceptional one — and reporting it converged
    ///     would be the "observes, never assumes" violation one level down.
    /// </summary>
    [Fact]
    public async Task AClaimStillReadableAfterItsDeleteIsInProgress() {
        var cluster = new FakeClaimCluster { DeletesAreInert = true };
        var claim = Claim("data-vault-0");
        cluster.Plant(claim, Ownership);

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new DeclaringReconciler([Volume(claim)]),
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        cluster.Deleted.ShouldHaveSingleItem();
    }

    // ── The naming rule ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The claim name is Kubernetes' own <c>{volume}-{set}-{ordinal}</c>, one per replica.</b>
    /// </summary>
    [Fact]
    public void OfSetNamesOneClaimPerReplicaInKubernetesOwnShape() {
        var volumes = RetainedVolume.OfSet(Namespace, "store", "broker", 3, Ownership, "the file store");

        volumes.Select(x => x.Claim.Name)
            .ShouldBe(["store-broker-0", "store-broker-1", "store-broker-2"]);

        volumes.ShouldAllBe(x => x.Claim.Namespace == Namespace);
        volumes.ShouldAllBe(x => x.Claim.Kind == RetainedVolume.ClaimKind);
    }

    /// <summary>
    ///     ⚠ <b>A set with no ownership labels cannot be declared at all.</b> The refusal is at the
    ///     declaration rather than only at the guard, so a provider cannot ship a claim list with no
    ///     evidence and find out at a tenant's purge.
    /// </summary>
    [Fact]
    public void OfSetRefusesToDeclareClaimsWithNoOwnershipLabels() =>
        Should.Throw<ArgumentException>(
            () => RetainedVolume.OfSet(
                Namespace,
                "store",
                "broker",
                1,
                ImmutableDictionary<string, string>.Empty,
                "the file store"
            )
        );

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────

    static ObjectRef Claim(string name) =>
        new() { Kind = RetainedVolume.ClaimKind, Namespace = Namespace, Name = name };

    static RetainedVolume Volume(ObjectRef claim) => new(claim, Ownership, "the vault's stored secrets");

    static ReconcileContext Context(IKubeClusterConnection? cluster) =>
        new(
            ResourceId.ParsePath(
                    "/tenants/11111111-1111-1111-1111-111111111111"
                    + "/subscriptions/22222222-2222-2222-2222-222222222222"
                    + "/resourceGroups/rg/providers/CyberCloud.Testing/vaults/vault"
                )
                .GetValueOrThrow()
                .WithId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            TestingProvider.V2026,
            JsonDocument.Parse("{}").RootElement,
            null,
            Namespace,
            cluster,
            new UnavailableSecretResolver(),
            new RecordingReconcileLog()
        );

    /// <summary>A reconciler that declares whatever the case hands it and does nothing else.</summary>
    sealed class DeclaringReconciler(ImmutableArray<RetainedVolume> volumes) : IResourceReconciler {
        public ResourceTypeName Type => TestingProvider.VaultTypeName;

        public Task<ReconcileOutcome> ReconcileAsync(ReconcileContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReconcileOutcome.Converged);

        public Task<ReconcileOutcome> DeleteAsync(ReconcileContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReconcileOutcome.Converged);

        public Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ObservedState.Absent);

        public Task<Result<ImmutableArray<RetainedVolume>>> RetainedVolumesAsync(
            ReconcileContext context,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result<ImmutableArray<RetainedVolume>>.Success(volumes));
    }

    /// <summary>A reconciler that takes the interface's default — the answer for most of the catalogue.</summary>
    sealed class KeepsNothingReconciler : IResourceReconciler {
        public ResourceTypeName Type => TestingProvider.VaultTypeName;

        public Task<ReconcileOutcome> ReconcileAsync(ReconcileContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReconcileOutcome.Converged);

        public Task<ReconcileOutcome> DeleteAsync(ReconcileContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReconcileOutcome.Converged);

        public Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ObservedState.Absent);
    }

    /// <summary>
    ///     An API server that holds claims — enough of one to answer a read, a delete and a read back.
    /// </summary>
    /// <remarks>
    ///     ⚠ It is not <c>FakeKubeCluster</c>, which lives in <c>test/CyberCloud.Conformance</c> and
    ///     which this project does not reference. What is under test is the guard's arithmetic over
    ///     what a read returns, so a dictionary that can be planted with an object nobody applied is
    ///     the fixture — an object <i>we</i> never wrote is the whole subject.
    /// </remarks>
    sealed class FakeClaimCluster : IKubeClusterConnection {
        readonly Dictionary<string, string> objects = new(StringComparer.Ordinal);

        public Guid ClusterId => Guid.Parse("44444444-4444-4444-4444-444444444444");

        public List<ObjectRef> Read { get; } = [];

        public List<ObjectRef> Deleted { get; } = [];

        /// <summary>When set, every read comes back as a failure carrying this code.</summary>
        public ErrorCode? ReadFailsWith { get; init; }

        /// <summary>When set, a delete is accepted and removes nothing — a claim under a finalizer.</summary>
        public bool DeletesAreInert { get; init; }

        public void Plant(ObjectRef target, ImmutableDictionary<string, string> labels, bool labelled = true) {
            var metadata = new JsonObject {
                ["name"] = target.Name, ["namespace"] = target.Namespace
            };

            if (labelled) {
                var written = new JsonObject();
                foreach (var (key, value) in labels) {
                    written[key] = value;
                }

                metadata["labels"] = written;
            }

            objects[Key(target)] = new JsonObject {
                ["apiVersion"] = target.Kind.ApiVersion,
                ["kind"] = target.Kind.Kind,
                ["metadata"] = metadata
            }.ToJsonString();
        }

        public bool Holds(ObjectRef target) => objects.ContainsKey(Key(target));

        public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A reclaim applies nothing.");

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
            ArgumentNullException.ThrowIfNull(target);
            Read.Add(target);

            if (ReadFailsWith is { } code) {
                return Task.FromResult(Result<KubeObject>.Failure(code, "the API server refused the read"));
            }

            return Task.FromResult(
                objects.TryGetValue(Key(target), out var json)
                    ? Result<KubeObject>.Success(new() { Ref = target, Json = json })
                    : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not there")
            );
        }

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(command);
            Deleted.Add(command.Target);

            if (!DeletesAreInert) {
                objects.Remove(Key(command.Target));
            }

            return Task.FromResult(Result.Success);
        }

        static string Key(ObjectRef target) =>
            $"{target.Kind.Group}/{target.Kind.Version}/{target.Kind.Kind}/{target.Namespace}/{target.Name}";
    }
}
