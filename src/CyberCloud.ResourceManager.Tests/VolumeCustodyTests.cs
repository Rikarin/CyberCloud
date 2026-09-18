using CyberCloud.Kubernetes.Contracts;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <c>VolumeCustody</c> — the two moves that let an operator-owned claim outlive its owner and
///     find a new one, and the guard that runs before either.
/// </summary>
/// <remarks>
///     <para>
///         The same discipline as <see cref="RetainedVolumeTests" />: the guard is made to fire, and
///         every refusal is asserted together with the claim being exactly as it was found. A detach
///         of somebody else's claim orphans their volume from its controller; an adopt of somebody
///         else's claim hands their volume to a database that will mount it. Neither destroys data by
///         itself and both are the first step of something that does.
///     </para>
///     <para>
///         ⚠ <b>The double models ownership as the API server does — by uid — and nothing else.</b>
///         It does not garbage-collect; that half lives in <c>FakeKubeCluster</c> and is what the
///         conformance case over <c>CyberCloud.DBforPostgreSQL/servers</c> runs against. What this
///         file proves is the arithmetic on this side of the API server.
///     </para>
/// </remarks>
public sealed class VolumeCustodyTests {
    const string Namespace = "cc-sub-rg";

    static readonly ImmutableDictionary<string, string> Ownership =
        ImmutableDictionary<string, string>.Empty.Add("cnpg.io/cluster", "main");

    static readonly OwnerRef OldCluster =
        new() { ApiVersion = "postgresql.cnpg.io/v1", Kind = "Cluster", Name = "main", Uid = "uid-old" };

    static readonly OwnerRef NewCluster = OldCluster with { Uid = "uid-new" };

    [Fact]
    public async Task ADetachClearsTheOwnerOfEveryClaimAndReportsHowMany() {
        var cluster = new CustodyCluster();
        var data = Claim("main-1");
        var wal = Claim("main-1-wal");
        cluster.Plant(data, Ownership, OldCluster);
        cluster.Plant(wal, Ownership, OldCluster);

        var detached = await VolumeCustody.DetachAsync(
            cluster,
            [Volume(data), Volume(wal)],
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        detached.IsSuccess.ShouldBeTrue(detached.Error?.Message);
        detached.GetValueOrThrow().ShouldBe(2);
        cluster.ControllerOf(data).ShouldBeNull("the data claim still names the Cluster about to be deleted");
        cluster.ControllerOf(wal).ShouldBeNull("the WAL claim still names the Cluster about to be deleted");
    }

    [Fact]
    public async Task ADetachIsIdempotentSoARetriedTeardownWritesNothing() {
        var cluster = new CustodyCluster();
        var data = Claim("main-1");
        cluster.Plant(data, Ownership, OldCluster);

        (await VolumeCustody.DetachAsync(
                cluster,
                [Volume(data)],
                Context(cluster),
                TestContext.Current.CancellationToken
            ))
            .GetValueOrThrow()
            .ShouldBe(1);

        var again = await VolumeCustody.DetachAsync(
            cluster,
            [Volume(data)],
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        again.GetValueOrThrow().ShouldBe(0, "a claim with no owner was detached a second time");
        cluster.OwnerChanges.Count.ShouldBe(1, "the second pass wrote to the API server");
    }

    [Fact]
    public async Task AClaimWhoseLabelsNameAnotherClusterStopsTheWholeMoveBeforeAnyWrite() {
        // ⚠ The disagreeing claim is SECOND, so a loop that checked-then-moved one at a time would
        // already have detached the first before finding out the list was wrong.
        var cluster = new CustodyCluster();
        var ours = Claim("main-1");
        var theirs = Claim("main-3");
        cluster.Plant(ours, Ownership, OldCluster);
        cluster.Plant(theirs, Ownership.SetItem("cnpg.io/cluster", "somebody-elses-main"), OldCluster);

        var detached = await VolumeCustody.DetachAsync(
            cluster,
            [Volume(ours), Volume(theirs)],
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        detached.IsFailure.ShouldBeTrue();
        detached.Error!.Message.ShouldContain("somebody-elses-main");
        cluster.OwnerChanges.ShouldBeEmpty("a claim was moved before the whole list was checked");
        cluster.ControllerOf(ours)!.Uid.ShouldBe("uid-old", "the claim that was ours lost its owner anyway");
    }

    [Fact]
    public async Task AnAdoptWritesTheControllerItWasGivenAndLeavesAClaimAlreadyHisAlone() {
        var cluster = new CustodyCluster();
        var detachedClaim = Claim("main-1");
        var alreadyOwned = Claim("main-1-wal");
        cluster.Plant(detachedClaim, Ownership, null);
        cluster.Plant(alreadyOwned, Ownership, NewCluster);

        var adopted = await VolumeCustody.AdoptAsync(
            cluster,
            [Volume(detachedClaim), Volume(alreadyOwned)],
            NewCluster,
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        adopted.IsSuccess.ShouldBeTrue(adopted.Error?.Message);
        adopted.GetValueOrThrow().ShouldBe(1);
        cluster.ControllerOf(detachedClaim).ShouldBe(NewCluster);
        cluster.OwnerChanges.ShouldHaveSingleItem().Target.Name.ShouldBe("main-1");
    }

    [Fact]
    public async Task AnAdoptRefusesAnOwnerWithNoUidWithoutReadingAnything() {
        // ⚠ An owner reference is compared by uid. Writing one whose uid was never issued hands the
        // claims to nothing, and the garbage collector removes a dependent whose owner it cannot find.
        var cluster = new CustodyCluster();
        var claim = Claim("main-1");
        cluster.Plant(claim, Ownership, null);

        var adopted = await VolumeCustody.AdoptAsync(
            cluster,
            [Volume(claim)],
            NewCluster with { Uid = string.Empty },
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        adopted.IsFailure.ShouldBeTrue();
        adopted.Error!.Message.ShouldContain("uid");
        cluster.Reads.ShouldBeEmpty();
        cluster.OwnerChanges.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMoveTheApiServerAcceptedAndDidNotHoldIsReportedRatherThanBelieved() {
        var cluster = new CustodyCluster { OwnerChangesAreInert = true };
        var claim = Claim("main-1");
        cluster.Plant(claim, Ownership, OldCluster);

        var detached = await VolumeCustody.DetachAsync(
            cluster,
            [Volume(claim)],
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        detached.IsFailure.ShouldBeTrue("a detach that changed nothing was reported as done");
        detached.Error!.Message.ShouldContain("still reads back");
    }

    [Fact]
    public async Task AnAbsentClaimIsSkippedRatherThanFailed() {
        var cluster = new CustodyCluster();
        var present = Claim("main-1");
        cluster.Plant(present, Ownership, OldCluster);

        var detached = await VolumeCustody.DetachAsync(
            cluster,
            [Volume(Claim("main-2")), Volume(present)],
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        detached.IsSuccess.ShouldBeTrue(detached.Error?.Message);
        detached.GetValueOrThrow().ShouldBe(1);
        cluster.ControllerOf(present).ShouldBeNull();
    }

    [Fact]
    public async Task AClaimOutsideTheResourcesNamespaceIsRefusedByAddressBeforeItIsRead() {
        var cluster = new CustodyCluster();
        var elsewhere = new ObjectRef {
            Kind = RetainedVolume.ClaimKind, Namespace = "cc-sub-other-rg", Name = "main-1"
        };

        var detached = await VolumeCustody.DetachAsync(
            cluster,
            [Volume(elsewhere)],
            Context(cluster),
            TestContext.Current.CancellationToken
        );

        detached.IsFailure.ShouldBeTrue();
        detached.Error!.Message.ShouldContain("cc-sub-other-rg");
        cluster.Reads.ShouldBeEmpty("a claim outside the namespace was read");
    }

    static ObjectRef Claim(string name) =>
        new() { Kind = RetainedVolume.ClaimKind, Namespace = Namespace, Name = name };

    static RetainedVolume Volume(ObjectRef claim) => new(claim, Ownership, "the instance's data volume");

    static ReconcileContext Context(IKubeClusterConnection cluster) =>
        new(
            ResourceId.ParsePath(
                "/tenants/11111111-1111-1111-1111-111111111111"
                + "/subscriptions/22222222-2222-2222-2222-222222222222"
                + "/resourceGroups/rg/providers/CyberCloud.Testing/vaults/main"
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

    /// <summary>A cluster that holds claims and their owner references, and nothing else.</summary>
    sealed class CustodyCluster : IKubeClusterConnection {
        readonly Dictionary<string, string> objects = new(StringComparer.Ordinal);

        public Guid ClusterId => Guid.Parse("44444444-4444-4444-4444-444444444444");

        public List<ObjectRef> Reads { get; } = [];

        public List<(ObjectRef Target, OwnerRef? Owner)> OwnerChanges { get; } = [];

        /// <summary>When set, an ownership change is accepted and changes nothing.</summary>
        public bool OwnerChangesAreInert { get; init; }

        public void Plant(ObjectRef target, ImmutableDictionary<string, string> labels, OwnerRef? owner) {
            var written = new JsonObject();
            foreach (var (key, value) in labels) {
                written[key] = value;
            }

            var metadata = new JsonObject {
                ["name"] = target.Name, ["namespace"] = target.Namespace, ["labels"] = written
            };

            if (owner is not null) {
                metadata["ownerReferences"] = new JsonArray(KubeJson.OwnerReference(owner));
            }

            objects[Key(target)] = new JsonObject {
                ["apiVersion"] = target.Kind.ApiVersion, ["kind"] = target.Kind.Kind, ["metadata"] = metadata
            }.ToJsonString();
        }

        public OwnerRef? ControllerOf(ObjectRef target) =>
            objects.TryGetValue(Key(target), out var json) ? KubeJson.ControllerOf(JsonNode.Parse(json)) : null;

        public Task<Result<ApplyOutcome>> ApplyAsync(
            KubeCommand command,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException("A custody move applies nothing.");

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
            ArgumentNullException.ThrowIfNull(target);
            Reads.Add(target);

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
        ) =>
            throw new NotSupportedException("A custody move deletes nothing.");

        public Task<Result> SetOwnerAsync(
            ObjectRef target,
            OwnerRef? owner,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(target);
            OwnerChanges.Add((target, owner));

            if (!objects.TryGetValue(Key(target), out var json)) {
                return Task.FromResult(Result.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not there"));
            }

            if (OwnerChangesAreInert) {
                return Task.FromResult(Result.Success);
            }

            var root = JsonNode.Parse(json)!.AsObject();
            var metadata = root["metadata"]!.AsObject();

            if (owner is null) {
                metadata.Remove("ownerReferences");
            } else {
                metadata["ownerReferences"] = new JsonArray(KubeJson.OwnerReference(owner));
            }

            objects[Key(target)] = root.ToJsonString();
            return Task.FromResult(Result.Success);
        }

        static string Key(ObjectRef target) =>
            $"{target.Kind.Group}/{target.Kind.Version}/{target.Kind.Kind}/{target.Namespace}/{target.Name}";
    }
}
