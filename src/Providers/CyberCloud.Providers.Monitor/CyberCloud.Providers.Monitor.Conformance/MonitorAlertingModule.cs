using CyberCloud.Communication;
using CyberCloud.Conformance;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Monitor.Alerting;
using CyberCloud.Providers.Monitor.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Conformance;

/// <summary>
///     The alert evaluator, as the world the shared suite reads and breaks for
///     <c>CyberCloud.Monitor/workspaces/alertRules</c>.
/// </summary>
/// <remarks>
///     <para>
///         The second <see cref="IConvergedModule" /> in the tree, and the first under a cluster-backed
///         parent: the harness creates the workspace ancestor against its fake cluster, then reads
///         this type through here. Every reading goes through <see cref="GrainAlertControlPlane" />
///         over the harness's own client — the same seam the reconciler and the handler hold, built
///         the way <c>MonitorApplicationModule</c> builds it — and never through the reconciler's
///         <c>ObserveAsync</c>. <see cref="IConvergedModule" />'s remarks say why that matters.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The silo hosts the sending module too, with no carrier, and the query seam is the
///             refusing default.
///         </b> The evaluator grain takes an <c>IMessageSender</c>, so the silo
///         needs <c>AddCyberCloudCommunication</c> the way the silo host has it; nothing in the suite
///         sends, and the in-memory reminder service's ticks — a minute apart, if the run lasts that
///         long — find a seam that refuses by name, record the sentence on the rule, and move
///         nothing. That is the evaluator behaving as designed on a store it cannot reach, and it is
///         why a green run here proves convergence and nothing about evaluation:
///         <c>AlertEvaluatorTests</c> in <c>CyberCloud.Providers.Monitor.Tests</c> is what evaluates.
///     </para>
/// </remarks>
public sealed class MonitorAlertingModule : IConvergedModule {
    IGrainFactory? grains;

    /// <summary>The one instance, for the one case source that reads it.</summary>
    public static MonitorAlertingModule Instance { get; } = new();

    /// <summary>The seam a directly-driven reconciler holds, bound once the harness has a cluster.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="Attach" /> ran.</exception>
    public IAlertControlPlane Plane =>
        new GrainAlertControlPlane(
            grains
            ?? throw new InvalidOperationException(
                "The harness has not attached a cluster yet. MonitorAlertingModule.Plane is read by the "
                + "case's CreateReconciler, which the suite calls only after ProviderTestCluster.InitializeAsync "
                + "— reaching it earlier is a harness bug."
            )
        );

    /// <inheritdoc />
    public void ConfigureSilo(ISiloBuilder silo) {
        silo.ConfigureServices(static services => services.AddCyberCloudMonitorAlerting());
        silo.AddCyberCloudCommunication();
    }

    /// <inheritdoc />
    public void ConfigureHandlers(IServiceCollection services, IGrainFactory grains) {
        // What the gateway registers for this family's one grain-reaching handler.
        services.AddSingleton(grains);
        services.AddCyberCloudMonitorAlerting();
    }

    /// <inheritdoc />
    public void Attach(IGrainFactory grains) => this.grains = grains;

    /// <inheritdoc />
    /// <remarks>
    ///     Removes every rule on the shared ancestor workspace's evaluator. Every test here creates a
    ///     fresh rule under the same ancestor and never deletes it, and an evaluator carries at most
    ///     <c>IAlertEvaluatorGrain.MaxRules</c> — so without this the fifty-first test would be refused
    ///     by the fifty before it, correctly and uselessly. ⚠ Blocks on the grain calls, because every
    ///     reset in the suite is synchronous; the test runner has no synchronization context to
    ///     deadlock against.
    /// </remarks>
    public void Reset() {
        if (grains is null) {
            return;
        }

        var ancestor = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            MonitorWorkspaces.Type,
            ConformanceIds.AncestorName(0),
            Guid.Empty
        );

        var plane = Plane;
        var evaluator = MonitorAlertRules.EvaluatorIdFor(ancestor);

        Task.Run(async () => {
                var listed = Throwing(await plane.ListRulesAsync(ancestor.TenantId, evaluator, CancellationToken.None));

                foreach (var rule in listed) {
                    Throwing(
                        await plane.RemoveRuleAsync(
                            ancestor.TenantId,
                            evaluator,
                            rule.Spec.RuleId,
                            CancellationToken.None
                        )
                    );
                }
            }
        )
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public async Task<bool> HoldsAsync(ResourceId id, CancellationToken cancellationToken) =>
        (await Plane.GetRuleAsync(
                id.TenantId,
                MonitorAlertRules.EvaluatorIdFor(id),
                id.Id,
                cancellationToken
            )).IsSuccess;

    /// <inheritdoc />
    public async Task<bool> MatchesAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        var held = await Plane.GetRuleAsync(
            id.TenantId,
            MonitorAlertRules.EvaluatorIdFor(id),
            id.Id,
            cancellationToken
        );
        if (!held.TryGetValue(out var snapshot)) {
            return false;
        }

        using var desired = JsonDocument.Parse(desiredJson);
        return MonitorAlertRules.Matches(snapshot, id, desired.RootElement);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) =>
        Throwing(
            await Plane.RemoveRuleAsync(id.TenantId, MonitorAlertRules.EvaluatorIdFor(id), id.Id, cancellationToken)
        );

    /// <inheritdoc />
    public async Task CorruptAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        // A threshold no body in the suite asks for, on the spec as held — the hand edit.
        var held = Throwing(
            await Plane.GetRuleAsync(id.TenantId, MonitorAlertRules.EvaluatorIdFor(id), id.Id, cancellationToken)
        );

        Throwing(
            await Plane.UpsertRuleAsync(
                id.TenantId,
                MonitorAlertRules.EvaluatorIdFor(id),
                held.Spec with {
                    Condition = held.Spec.Condition with { Threshold = held.Spec.Condition.Threshold + 7919 }
                },
                cancellationToken
            )
        );
    }

    static void Throwing(Result result) {
        if (result.TryGetError(out var error)) {
            throw new InvalidOperationException(
                $"The evaluator refused a write the suite made behind the reconciler's back: {error.Message}"
            );
        }
    }

    static T Throwing<T>(Result<T> result)
        where T : notnull {
        if (result.TryGetError(out var error)) {
            throw new InvalidOperationException(
                $"The evaluator refused a call the suite made around the reconciler: {error.Message}"
            );
        }

        return result.GetValueOrThrow();
    }
}
