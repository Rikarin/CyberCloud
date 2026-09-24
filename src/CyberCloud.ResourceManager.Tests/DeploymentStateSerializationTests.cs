using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     A deployment's parent-operation state through the serializers a real silo uses — the durable
///     tier's JSON and Orleans' own — rather than through in-memory storage.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The one thing <c>DeploymentTests</c> cannot show.</b> <c>ResourceManagerCluster</c> runs on
///         in-memory grain storage, which keeps the object graph, so
///         <c>DeploymentTests.AParentReDrivenAfterItsActivationIsLostResumesAtItsCursorWithoutRewritingAChild</c>
///         resumes from the very objects it wrote and would pass with a run record no serializer can
///         read back. A re-drive after a real silo loss resumes from these bytes instead: a step list that
///         came back empty would re-plan and write every child again, and a lost child operation id would
///         write the step in flight a second time. <c>ClusterConformanceTests.DesiredStateSurvivesARealSerializationRoundTrip</c>
///         is the durable round trip for an ordinary resource, and a deployment is not one.
///     </para>
///     <para>
///         ⚠ <b>The durable tier's contract is the property name</b> (see
///         <see cref="SystemTextJsonGrainStorageSerializer" />), and the grain-to-grain one is
///         <c>[Id]</c> and <c>[Alias]</c>. The same state is put through both, because it is written
///         to one and could be copied through the other.
///     </para>
/// </remarks>
public sealed class DeploymentStateSerializationTests : IDisposable {
    readonly ServiceProvider provider;
    readonly Serializer serializer;

    /// <summary>Builds an Orleans serializer over the assemblies the state's types come from.</summary>
    public DeploymentStateSerializationTests() {
        var services = new ServiceCollection();
        services.AddSerializer(static builder => builder
            .AddAssembly(typeof(OperationGrainState).Assembly)
            .AddAssembly(typeof(OperationSpec).Assembly)
            .AddAssembly(typeof(Error).Assembly)
        );

        provider = services.BuildServiceProvider();
        serializer = provider.GetRequiredService<Serializer>();
    }

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();

    static OperationGrainState Parent() {
        var parentId = Guid.Parse("0d1e2f3a-4b5c-4d6e-8f70-8192a3b4c5d6");
        var first = Guid.Parse("11111111-2222-4333-8444-555566667777");
        var second = Guid.Parse("88888888-9999-4aaa-8bbb-ccccddddeeee");

        return new() {
            Spec = new() {
                OperationId = parentId,
                Kind = OperationKind.Create,
                ResourcePath = "/tenants/…/resourceGroups/prod/providers/CyberCloud.Resources/deployments/shop",
                ResourceId = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                SubscriptionId = Guid.NewGuid(),
                ApiVersion = Deployments.V2026,
                Desired = """{"properties":{"template":"{\"resources\":[]}"}}""",
                Caller = new() { TenantId = Guid.NewGuid(), SubjectType = "user", SubjectId = "alice", CorrelationId = "c-1" },
                ParentOperationId = Guid.Empty
            },
            Status = OperationState.Running,
            Progress = [new() { At = DateTimeOffset.Parse("2026-09-24T10:00:00Z", null), Step = "planned", Detail = "2 resource(s)" }],
            StartedAt = DateTimeOffset.Parse("2026-09-24T10:00:00Z", null),
            Attempts = 2,
            Children = [first, second],
            IndexConfirmed = true,
            Deployment = new() {
                Cursor = 1,
                StepStartedAt = DateTimeOffset.Parse("2026-09-24T10:01:00Z", null),
                Steps = [
                    new() {
                        ResourcePath = "/tenants/…/widgets/front",
                        ApiVersion = "2026-08-01",
                        Body = """{"location":"eu-central","properties":{"size":1}}""",
                        Status = DeploymentStepStatus.Succeeded,
                        ChildOperationId = first,
                        Created = true,
                        Detail = "created"
                    },
                    new() {
                        ResourcePath = "/tenants/…/widgets/back",
                        ApiVersion = "2026-08-01",
                        Body = """{"location":"eu-central","properties":{"size":2}}""",
                        Status = DeploymentStepStatus.Running,
                        ChildOperationId = second,
                        Created = false,
                        Detail = ""
                    }
                ]
            }
        };
    }

    [Fact]
    public void AParentsRunRecordRoundTripsThroughTheDurableTiersJson() {
        var value = Parent();
        var durable = new SystemTextJsonGrainStorageSerializer();

        var round = durable.Deserialize<OperationGrainState>(durable.Serialize(value));

        AssertSame(value, round);

        // ⚠ And by name, because under JSON the name is the contract: a renamed property reads back as
        // its default and the parent re-plans from nothing on the next activation.
        var text = durable.Serialize(value).ToString();
        text.ShouldContain("\"Deployment\":{");
        text.ShouldContain("\"Steps\":[");
        text.ShouldContain("\"ChildOperationId\":");
        text.ShouldContain("\"ParentOperationId\":");
    }

    [Fact]
    public void AParentsRunRecordRoundTripsThroughOrleans() =>
        AssertSame(Parent(), serializer.Deserialize<OperationGrainState>(serializer.SerializeToArray(Parent())));

    [Fact]
    public void AChildsSpecKeepsItsParentThroughTheDurableTiersJson() {
        // The child's half of the nesting: the one field that lets it tell its parent it ended.
        var parent = Guid.NewGuid();
        var value = new OperationGrainState {
            Spec = new() { OperationId = Guid.NewGuid(), Kind = OperationKind.Create, ParentOperationId = parent }
        };

        var durable = new SystemTextJsonGrainStorageSerializer();
        var round = durable.Deserialize<OperationGrainState>(durable.Serialize(value));

        round.Spec!.ParentOperationId.ShouldBe(parent);
        round.Deployment.ShouldBeNull("an operation that is not a deployment's has no run, and must come back with none.");
    }

    static void AssertSame(OperationGrainState value, OperationGrainState round) {
        round.Spec.ShouldNotBeNull();
        round.Spec!.OperationId.ShouldBe(value.Spec!.OperationId);
        round.Spec.Caller.SubjectId.ShouldBe("alice");
        round.Spec.Caller.CorrelationId.ShouldBe("c-1");
        round.Spec.Desired.ShouldBe(value.Spec.Desired);
        round.Status.ShouldBe(OperationState.Running);
        round.Children.ShouldBe(value.Children);
        round.IndexConfirmed.ShouldBeTrue();
        round.Progress.Count.ShouldBe(1);

        var run = round.Deployment.ShouldNotBeNull("OperationGrainState.Deployment [Id(16)] did not come back.");
        run.Cursor.ShouldBe(1);
        run.StepStartedAt.ShouldBe(value.Deployment!.StepStartedAt);
        run.Steps.Count.ShouldBe(2, "the step list came back short, so a re-drive would re-plan.");

        for (var i = 0; i < run.Steps.Count; i++) {
            var expected = value.Deployment.Steps[i];
            var actual = run.Steps[i];

            actual.ResourcePath.ShouldBe(expected.ResourcePath);
            actual.ApiVersion.ShouldBe(expected.ApiVersion);
            actual.Body.ShouldBe(expected.Body);
            actual.Status.ShouldBe(expected.Status);
            actual.ChildOperationId.ShouldBe(expected.ChildOperationId);
            actual.Created.ShouldBe(expected.Created);
            actual.Detail.ShouldBe(expected.Detail);
        }
    }
}
