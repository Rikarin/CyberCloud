using CyberCloud.ResourceManager.Orchestration;
using CyberCloud.ResourceManager.Reconcile;
using System.Reflection;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     A parent's pass yields between steps once it has used its budget — docs/plan/08 § The reconcile
///     loop's thirty seconds, for a pass that is a deployment's.
/// </summary>
/// <remarks>
///     ⚠ <b>No cluster, on purpose.</b> The case the budget exists for is a rerun whose every child is a
///     no-op, which never reaches a grain: every step is one <see cref="IResourceManager.WriteChildAsync" />
///     that answers <see cref="WriteAccepted.NoOp" />. So the manager is a stand-in that answers exactly
///     that and counts, and the budget is zero, so the only thing between two writes is the check.
/// </remarks>
public sealed class DeploymentDriverBudgetTests {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    static OperationSpec Spec(int resources) {
        var template = new JsonObject {
            ["resources"] = new JsonArray(
                [
                    .. Enumerable.Range(0, resources)
                        .Select(static x => (JsonNode)new JsonObject {
                                ["type"] = "CyberCloud.Testing/widgets",
                                ["apiVersion"] = "2026-08-01",
                                ["name"] = $"w{x}",
                                ["properties"] = new JsonObject { ["size"] = 1 }
                            }
                        )
                ]
            )
        };

        return new() {
            OperationId = Guid.NewGuid(),
            Kind = OperationKind.Update,
            TenantId = Tenant,
            SubscriptionId = Subscription,
            ResourcePath = new ResourceId(Tenant, Subscription, "prod", Deployments.Type, "rerun", Guid.Empty).Path,
            ApiVersion = Deployments.V2026,
            Desired = new JsonObject { ["properties"] = new JsonObject { ["template"] = template.ToJsonString() } }.ToJsonString(),
            Caller = new() { TenantId = Tenant, SubjectType = "user", SubjectId = "alice" }
        };
    }

    [Fact]
    public async Task APassThatHasUsedItsBudgetYieldsBetweenStepsAndTheNextResumesAtTheCursor() {
        var manager = NoOpManager.Create();
        var driver = new DeploymentDriver(manager, null!, TestClock.Instance) { Budget = TimeSpan.Zero };
        var spec = Spec(3);
        var run = new DeploymentRunState();

        var first = await driver.RunAsync(spec, run, null, TestContext.Current.CancellationToken);

        first.Outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress, "a zero budget must still write one step, and then yield.");
        run.Cursor.ShouldBe(1);
        ((NoOpManager)(object)manager).Writes.ShouldBe(1);
        first.Progress[^1].Step.ShouldBe("yielding");

        (await driver.RunAsync(spec, run, null, TestContext.Current.CancellationToken)).Outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        run.Cursor.ShouldBe(2);

        var last = await driver.RunAsync(spec, run, null, TestContext.Current.CancellationToken);

        last.Outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        run.Cursor.ShouldBe(3);
        ((NoOpManager)(object)manager).Writes.ShouldBe(3, "a step was written twice across the yields, or skipped.");
        run.Steps.ShouldAllBe(static x => x.Status == DeploymentStepStatus.Succeeded && x.Detail == "no change");
    }

    [Fact]
    public async Task AtTheDefaultBudgetOnePassWritesEveryNoOpStep() {
        var manager = NoOpManager.Create();
        var driver = new DeploymentDriver(manager, null!, TestClock.Instance);
        var run = new DeploymentRunState();

        driver.Budget.ShouldBe(ReconcileDriver.PassBudget);
        (await driver.RunAsync(Spec(5), run, null, TestContext.Current.CancellationToken)).Outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        ((NoOpManager)(object)manager).Writes.ShouldBe(5);
    }

    /// <summary>
    ///     An <see cref="IResourceManager" /> that answers every child write with a no-op and refuses
    ///     everything else — a <see cref="DispatchProxy" />, because the interface has twenty-odd members
    ///     and the driver calls one.
    /// </summary>
    public class NoOpManager : DispatchProxy {
        public int Writes { get; private set; }

        public static IResourceManager Create() => Create<IResourceManager, NoOpManager>();

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (targetMethod?.Name != nameof(IResourceManager.WriteChildAsync)) {
                throw new InvalidOperationException($"The driver called {targetMethod?.Name}, which a no-op rerun never reaches.");
            }

            Writes++;
            return Task.FromResult(Result<WriteAccepted>.Success(new() { NoOp = true }));
        }
    }
}
