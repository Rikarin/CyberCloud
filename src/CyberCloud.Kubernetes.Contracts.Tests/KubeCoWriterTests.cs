using CyberCloud.Core.Resources;
using Shouldly;
using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     <see cref="KubeCoWriter" /> — the read-apply-read-again loop a child reconciler co-writes
///     through — over a scripted connection.
/// </summary>
public sealed class KubeCoWriterTests {
    static readonly GroupVersionKind Vpcs =
        new() { Group = "kubeovn.io", Version = "v1", Kind = "Vpc", Plural = "vpcs" };

    static readonly ObjectRef Target = new() { Kind = Vpcs, Name = "hub-vpc" };

    static readonly ResourceId Owner = new(
        Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
        Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
        "prod",
        new("CyberCloud.Network", "virtualNetworks"),
        "hub",
        Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
    );

    static readonly ResourceId Peering = new(
        Owner.TenantId,
        Owner.SubscriptionId,
        "prod",
        new("CyberCloud.Network", "virtualNetworks/peerings"),
        "to-spoke",
        Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a"),
        "hub"
    );

    const string Fragment = """{ "spec": { "vpcPeerings": [ { "remoteVpc": "spoke-vpc", "localConnectIP": "10.0.0.1/30" } ] } }""";

    [Fact]
    public async Task AnAbsentOwnerObjectIsAFailureAndNeverACreate() {
        var cluster = new ScriptedConnection { Live = null };
        var writer = new KubeCoWriter(cluster);

        var result = await writer.ApplyFragmentAsync(Peering, Target, Fragment, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        result.Error.Message.ShouldContain("never creates");
        cluster.Applied.ShouldBeEmpty("nothing may be applied against an object that is not there");
    }

    [Fact]
    public async Task AppliesTheFragmentBuiltFromTheReadItJustMade() {
        var cluster = new ScriptedConnection { Live = Live("41") };
        var writer = new KubeCoWriter(cluster);

        var result = await writer.ApplyFragmentAsync(Peering, Target, Fragment, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        cluster.Applied.Count.ShouldBe(1);
        cluster.Applied[0].IsCoOwned.ShouldBeTrue();
        cluster.Applied[0].OwnerResourceId.ShouldBe(Owner.Id);
        cluster.Applied[0].ResourceId.ShouldBe(Peering.Id);
        JsonNode.Parse(cluster.Applied[0].Body)!["metadata"]!["resourceVersion"]!.GetValue<string>().ShouldBe("41");
    }

    [Fact]
    public async Task AStaleApplyIsReadAgainAndAppliedAgain() {
        // ⚠ The race the co-owned mode exists to lose loudly, and the loop that wins it on the
        // second try: the first apply carries version 41, the object has moved to 42, the read
        // happens again and the second apply carries 42.
        var cluster = new ScriptedConnection { Live = Live("41"), StaleTimes = 1 };
        var writer = new KubeCoWriter(cluster);

        var result = await writer.ApplyFragmentAsync(Peering, Target, Fragment, TestContext.Current.CancellationToken);

        result.GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        cluster.Reads.ShouldBe(2, "a stale answer is followed by a fresh read, not a blind retry");
        cluster.Applied.Count.ShouldBe(2);
        JsonNode.Parse(cluster.Applied[0].Body)!["metadata"]!["resourceVersion"]!.GetValue<string>().ShouldBe("41");
        JsonNode.Parse(cluster.Applied[1].Body)!["metadata"]!["resourceVersion"]!.GetValue<string>().ShouldBe("42");
    }

    [Fact]
    public async Task TheRetryIsBoundedAndTheStaleOutcomeIsHandedBack() {
        var cluster = new ScriptedConnection { Live = Live("41"), StaleTimes = int.MaxValue };
        var writer = new KubeCoWriter(cluster);

        var result = await writer.ApplyFragmentAsync(Peering, Target, Fragment, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue("stale is an outcome the reconciler reports as InProgress, not a failure");
        result.GetValueOrThrow().Result.ShouldBe(ApplyResult.Stale);
        result.GetValueOrThrow().Message.ShouldContain(KubeCoWriter.MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cluster.Applied.Count.ShouldBe(KubeCoWriter.MaxAttempts);
    }

    [Fact]
    public async Task WithdrawingFromAnObjectThatIsGoneIsConvergedBecauseTheOwnersDeleteWins() {
        var cluster = new ScriptedConnection { Live = null };
        var writer = new KubeCoWriter(cluster);

        var result = await writer.WithdrawFragmentAsync(Peering, Target, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.GetValueOrThrow().Result.ShouldBe(ApplyResult.Unchanged);
        result.GetValueOrThrow().Message.ShouldContain("owner's delete wins");
        cluster.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithdrawingAppliesTheOthersUnionAndNeverDeletes() {
        var cluster = new ScriptedConnection { Live = Live("41", withOwnFragment: true) };
        var writer = new KubeCoWriter(cluster);

        var result = await writer.WithdrawFragmentAsync(Peering, Target, TestContext.Current.CancellationToken);

        result.GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);
        cluster.Deleted.ShouldBe(0);
        cluster.Applied.Count.ShouldBe(1);
        cluster.Applied[0].Annotations.Keys.ShouldNotContain(KubeLabels.FragmentAnnotation(Peering.Id));
    }

    [Fact]
    public async Task AStaleWithdrawalIsReadAgainToo() {
        var cluster = new ScriptedConnection { Live = Live("41", withOwnFragment: true), StaleTimes = 1 };
        var writer = new KubeCoWriter(cluster);

        var result = await writer.WithdrawFragmentAsync(Peering, Target, TestContext.Current.CancellationToken);

        result.GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);
        cluster.Reads.ShouldBe(2);
        cluster.Applied.Count.ShouldBe(2);
    }

    [Fact]
    public async Task APassWithNoClusterFailsByName() {
        var writer = new NoClusterCoWriter();

        var result = await writer.ApplyFragmentAsync(Peering, Target, Fragment, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("RequiresCluster");
        result.Error.Message.ShouldContain(Peering.Path);
    }

    static KubeObject Live(string resourceVersion, bool withOwnFragment = false) {
        var annotations = new JsonObject {
            [KubeLabels.ResourcePathAnnotation] = Owner.Path, [KubeLabels.ReconcileHashAnnotation] = "sha256:owner"
        };

        if (withOwnFragment) {
            annotations[KubeLabels.FragmentAnnotation(Peering.Id)] = JsonNode.Parse(Fragment)!.ToJsonString();
        }

        var document = new JsonObject {
            ["apiVersion"] = "kubeovn.io/v1",
            ["kind"] = "Vpc",
            ["metadata"] = new JsonObject {
                ["name"] = "hub-vpc",
                ["resourceVersion"] = resourceVersion,
                ["labels"] = new JsonObject {
                    [KubeLabels.TenantId] = KubeLabels.GuidValue(Owner.TenantId),
                    [KubeLabels.SubscriptionId] = KubeLabels.GuidValue(Owner.SubscriptionId),
                    [KubeLabels.ResourceGroup] = "prod",
                    [KubeLabels.ResourceId] = KubeLabels.GuidValue(Owner.Id),
                    [KubeLabels.ResourceType] = KubeLabels.ResourceTypeValue(Owner.Type),
                    [KubeLabels.ApiVersion] = "2026-08-01",
                    [KubeLabels.ManagedBy] = KubeLabels.ManagedByValue
                },
                ["annotations"] = annotations
            },
            ["spec"] = new JsonObject()
        };

        return new() { Ref = Target, Json = document.ToJsonString(), ResourceVersion = resourceVersion };
    }

    /// <summary>
    ///     A connection whose object moves once per stale answer, so the loop's second read sees a
    ///     newer version than its first.
    /// </summary>
    sealed class ScriptedConnection : IKubeClusterConnection {
        public KubeObject? Live { get; set; }

        public int StaleTimes { get; init; }

        public int Reads { get; private set; }

        public int Deleted { get; private set; }

        public List<KubeCommand> Applied { get; } = [];

        public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
            Reads++;

            return Task.FromResult(
                Live is null
                    ? Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here")
                    : Result<KubeObject>.Success(Live)
            );
        }

        public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
            Applied.Add(command);

            if (Applied.Count <= StaleTimes) {
                // The object moved: bump the version the next read hands back.
                var bumped = (int.Parse(Live!.ResourceVersion, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);

                var node = JsonNode.Parse(Live.Json)!.AsObject();
                node["metadata"]!["resourceVersion"] = bumped;
                Live = Live with { Json = node.ToJsonString(), ResourceVersion = bumped };

                return Task.FromResult(
                    Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Stale, Target = command.Target })
                );
            }

            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() { Result = ApplyResult.Updated, Target = command.Target, ResourceVersion = Live!.ResourceVersion }
                )
            );
        }

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) {
            Deleted++;
            return Task.FromResult(Result.Success);
        }
    }
}
