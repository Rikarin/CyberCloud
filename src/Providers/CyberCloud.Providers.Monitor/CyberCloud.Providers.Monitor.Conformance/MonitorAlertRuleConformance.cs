using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Monitor.Contracts;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Conformance;

/// <summary>
///     <c>CyberCloud.Monitor/workspaces/alertRules</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE FIRST CLUSTERLESS CHILD OF A CLUSTER-BACKED PARENT, AND THE SUITE NEEDED NO CHANGE
///             TO MEET IT.
///         </b> The sending module's four types taught <c>test/CyberCloud.Conformance</c> the
///         clusterless half — an <see cref="IConvergedModule" /> in place of the fake cluster — and
///         the ancestor machinery already created a parent through the manager. The two compose: the
///         harness converges the workspace ancestor onto its fake cluster, and every assertion about
///         this type reads <see cref="MonitorAlertingModule" />. What was not certain until it ran is
///         that a child's <c>ReconcileContext.Cluster</c> is <see langword="null" /> when the child
///         declares no cluster even though its parent does — <c>ProviderRegistry</c> answers per type,
///         and the driver believes it.
///     </para>
///     <para>
///         ⚠ <b>WHAT A GREEN RUN HERE PROVES AND WHAT IT DOES NOT.</b> It proves the twelve-step
///         write path, the verb grammar, the four reconciler clauses, the cross-tenant <c>404</c> and
///         the delete-read-back, over grain state read around the reconciler. It proves
///         <b>nothing</b> about a rule ever being evaluated — the suite never ticks the evaluator, and
///         the seam it hosts refuses by name — and nothing about a notification reaching anybody.
///         Both are <c>AlertEvaluatorTests</c>' in <c>CyberCloud.Providers.Monitor.Tests</c>, over a
///         scripted store and an in-memory carrier, and that suite was sabotage-tested.
///     </para>
///     <para>
///         ⚠ <b>The body names a sending service that does not exist in the harness, on purpose.</b>
///         The reconciler checks the path's shape and tenant and not the service's existence — its
///         remarks say why — so a rule converges over a service nobody created, and the first
///         notification would record the sending module's refusal on the instance. That is the
///         shipping behaviour, and the harness needing no <c>CyberCloud.Communication/services</c>
///         resource to run this suite is a consequence of it rather than a shortcut.
///     </para>
/// </remarks>
public sealed class MonitorAlertRuleCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Monitor/workspaces/alertRules",
            CreateProvider = () => new MonitorProvider(),
            ReconcilerType = typeof(MonitorAlertRuleReconciler),
            // ⚠ The module's seam, read when the factory RUNS — after the harness attached a cluster
            // — and not when this case was constructed. MonitorAlertingModule.Plane refuses by name
            // if that order is ever wrong.
            CreateReconciler = clock => new MonitorAlertRuleReconciler(clock, MonitorAlertingModule.Instance.Plane),
            Type = MonitorAlertRules.Type,
            ApiVersion = MonitorWorkspaces.V2026,
            Body = _ => MonitorAlertRules.Body(HarnessService, ["oncall@example.com"], threshold: 5),
            // Changes the threshold, which the evaluator holds and every evaluation compares against.
            ChangedBody = _ => MonitorAlertRules.Body(HarnessService, ["oncall@example.com"], threshold: 7),
            // A service that is not a resource id path — refused by the schema's format, at the pointer.
            InvalidBody = _ => WithService(MonitorAlertRules.Body(HarnessService, ["oncall@example.com"]), "not-a-path"),
            InvalidBodyTarget = "/properties/actionGroup/service",
            ActionName = MonitorAlertRules.ListInstancesAction,
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            // Never asked — a clusterless case has no object to match — and false rather than true
            // so that a harness bug that DID ask would read as a failure.
            ObjectMatchesDesired = static _ => false
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [MonitorCase.ProviderCase];

    /// <inheritdoc />
    public static IConvergedModule? ConvergedModule => MonitorAlertingModule.Instance;

    /// <summary>
    ///     The sending service the harness's rules name: a <c>CyberCloud.Communication/services</c>
    ///     path in the harness's own tenant, subscription and resource group.
    /// </summary>
    /// <remarks>
    ///     ⚠ In the harness's primary tenant, and the suite also writes this body into the OTHER
    ///     tenant for the cross-tenant assertion. That write is refused with the parent-not-found
    ///     <c>404</c> before any pass runs, which is what the assertion is about — the tenant check in
    ///     <c>MonitorAlertRules.ToSpec</c> never sees it, and <c>AlertEvaluatorTests</c> is where that
    ///     check is exercised.
    /// </remarks>
    static string HarnessService =>
        new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            new(MonitorAlertRules.ServiceNamespace, MonitorAlertRules.ServiceType),
            "alerts",
            Guid.Empty
        ).Path;

    static string WithService(string body, string service) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!["actionGroup"]!["service"] = service;
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the alert-rule type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MonitorAlertRuleConformance(ProviderTestCluster<MonitorAlertRuleCase> cluster)
    : ProviderConformanceTests<MonitorAlertRuleCase>(cluster), IClassFixture<ProviderTestCluster<MonitorAlertRuleCase>>;

/// <summary>
///     The cluster-backed half, which this type does not have — said here, by name, rather than
///     left to be noticed.
/// </summary>
/// <remarks>
///     ⚠ Not derived from <c>ClusterBackedConformanceTests</c>, because that class's skip promises a
///     <c>*.Cluster.Conformance</c> case that cannot exist for a clusterless type:
///     <c>test/CyberCloud.Cluster.Conformance</c> refuses a case with no objects
///     (<c>ClusterConformanceTests.TheCaseOwnsClusterObjectsOrThisWholeSuiteWouldBeVacuous</c>). The
///     one criterion in that half that would mean something here — killing the silo mid-create and
///     finding the rule converged from a real durable tier — is the same debt the sending module
///     recorded, now owed by a second type: <c>charts/bundle/bundle.yaml § owed</c>,
///     <c>communication-silo-kill-has-no-clusterless-harness</c>.
/// </remarks>
public sealed class MonitorAlertRuleBackedConformance {
    /// <summary>The skip that says what the cluster-backed half would have checked.</summary>
    [Fact]
    [Trait("Requires", "cluster")]
    public void TheSiloKillCriterionHasNoClusterlessHarnessYet() =>
        Assert.Skip(
            "SKIPPED, AND SAYING SO — CyberCloud.Monitor/workspaces/alertRules is clusterless, and "
            + "test/CyberCloud.Cluster.Conformance refuses a case with no objects by name. Four of its "
            + "five criteria are about a real API server and do not apply; the fifth — killing the silo "
            + "mid-create and finding the rule converged from a real durable tier — does apply, and has "
            + "no clusterless harness yet. charts/bundle/bundle.yaml § owed carries it as "
            + "communication-silo-kill-has-no-clusterless-harness, and this is its second creditor."
        );
}
