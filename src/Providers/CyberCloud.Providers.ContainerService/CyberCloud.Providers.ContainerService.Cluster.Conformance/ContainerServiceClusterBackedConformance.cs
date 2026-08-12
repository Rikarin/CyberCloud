using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.ContainerService.Conformance;

namespace CyberCloud.Providers.ContainerService.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed-Kubernetes provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHAT A GREEN RUN OF THIS SUITE PROVES, AND WHAT IT DOES NOT — SAID FIRST, BECAUSE ON
///         THIS PROVIDER THE GAP IS THE WIDEST IN THE TREE.</b> The k3s container this starts has
///         <b>no Cluster API, no Kamaji and no KubeVirt</b>.
///         <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c> derives a minimal CRD
///         stub per custom kind from the case's own <c>Objects</c> — six of them here, in four API
///         groups — and waits for <c>Established</c>. That makes the REST paths exist and nothing else.
///     </para>
///     <list type="bullet">
///         <item>
///             <b>What it proves:</b> that the apply path reaches a real API server; that ADR-013's
///             seven labels survive real admission; that a real <c>kubectl delete</c> is corrected on
///             the next pass; that a second field manager's <c>409</c> becomes a named drift event;
///             that desired state survives a real serialization round trip; and that a missing CRD
///             produces a refusal with the API server's own words in it.
///         </item>
///         <item>
///             ⚠ <b>What it does NOT prove:</b> that any rendered document satisfies the real CRD's
///             schema. A derived stub has an <b>open</b> schema — no required fields, no enums, no
///             <c>+kubebuilder:default</c> — so a <c>KamajiControlPlane</c> missing
///             <c>spec.version</c>, a <c>MachineDeployment</c> with its rollout policy at the
///             <c>v1beta1</c> path, or a <c>KubevirtMachineTemplate</c> whose seven-level spec is
///             nested wrongly would all apply, read back and go green here. It also does not prove
///             that any controller would accept the objects, because none is watching them: no
///             <c>Cluster</c> in this suite has ever had a control plane provisioned, no
///             <c>KubevirtMachineTemplate</c> has produced a VM, and no kubeconfig <c>Secret</c> has
///             been written.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>So the schema half is owed to a real management cluster and is written down as owed
///         rather than implied by a green tick</b> —
///         <c>charts/managed/kubernetes/conformance.yaml § owed</c>,
///         <c>a-green-cluster-suite-proves-the-apply-path-only</c>. docs/plan/09 § Testing the fabric
///         already asks for the thing that would close it: <i>"a kind cluster with CAPI + Kamaji +
///         KubeVirt in the nightly e2e … the single most valuable test in the suite, because it is the
///         one that catches operator version drift."</i>
///     </para>
///     <para>
///         ⚠ <b>Two class declarations per type over the SAME
///         <c>ProviderConformanceCase</c> the Docker-free suite uses.</b> One type, one case: a second
///         copy here would be a second description of the same type, and the two would disagree the
///         first time either changed.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ManagedClusterLifecycleConformance(ClusterConformanceFixture<ManagedClusterCase> fixture)
    : ClusterConformanceTests<ManagedClusterCase>(fixture),
        IClassFixture<ClusterConformanceFixture<ManagedClusterCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-Kubernetes provider.</summary>
public sealed class ManagedClusterSiloKillConformance : SiloKillConformanceTests<ManagedClusterCase>;

/// <summary>
///     The same suite against the node-pool child type.
/// </summary>
/// <remarks>
///     ⚠ <b>Its ancestor is created against the SAME real API server</b> —
///     <c>ClusterConformanceHarness.CreateAncestorsAsync</c> drives a real cluster resource to a
///     terminal state before the first pool assertion runs — so a parent that fails to converge fails
///     loudly with the cluster's own message rather than as a child that will not create.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class AgentPoolLifecycleConformance(ClusterConformanceFixture<AgentPoolCase> fixture)
    : ClusterConformanceTests<AgentPoolCase>(fixture), IClassFixture<ClusterConformanceFixture<AgentPoolCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the node-pool child type.</summary>
public sealed class AgentPoolSiloKillConformance : SiloKillConformanceTests<AgentPoolCase>;
