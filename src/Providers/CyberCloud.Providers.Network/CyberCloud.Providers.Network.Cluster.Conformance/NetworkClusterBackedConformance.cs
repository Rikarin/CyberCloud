using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Network.Conformance;

namespace CyberCloud.Providers.Network.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the tenant networking provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Four class declarations over the two cases
///         <c>CyberCloud.Providers.Network.Conformance</c> already declares.</b> One provider, one
///         <c>ProviderConformanceCase</c> per type.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST CLUSTER-BACKED SUITE IN THE TREE FOR A CLUSTER-SCOPED OBJECT, AND IT IS THE
///         REASON <c>ClusterConformanceHarness</c> CHANGED.</b> That harness derived a CRD stub per
///         custom kind and hard-coded <c>Scope = "Namespaced"</c>. A Kube-OVN <c>Vpc</c> is
///         <c>+kubebuilder:resource:scope="Cluster"</c>, and <c>KubeApiClient</c> sends a
///         cluster-scoped apply to <c>/apis/{group}/{version}/{plural}/{name}</c> — which a
///         <c>Namespaced</c> definition does not serve. The scope is now derived from the case's own
///         <c>ObjectRef.IsClusterScoped</c>, alongside the group, version, kind and plural it already
///         derived, and nothing changed for the nine families whose objects carry a namespace.
///     </para>
///     <para>
///         ⚠ <b>WHAT THIS SUITE PROVES FOR THIS FAMILY, STATED EXPLICITLY BECAUSE THE GAP IS WIDER
///         HERE THAN ANYWHERE ELSE IN THE TREE.</b> The k3s it starts has <b>no Kube-OVN and no CRDs
///         for it</b>. So what is established is: the apply path reaches a cluster-scoped REST path,
///         ADR-013's seven labels and two annotations survive real admission on an object with no
///         namespace, server-side apply works under this platform's field manager, and a conflict
///         parses. What is <b>not</b> established: that these manifests satisfy Kube-OVN's schema —
///         the derived stub's schema is <c>x-kubernetes-preserve-unknown-fields</c>, so a field
///         Kube-OVN would refuse is accepted here and a <c>+kubebuilder:default</c> the real CRD
///         carries is absent. And nothing at all about the behaviour this family's <c>Matches</c> is
///         built around, because <b>the thing that rewrites the spec is the Kube-OVN controller</b>
///         and there is no controller in this cluster. <c>NetworkMatchesTests</c> hand-writes the
///         controller-shaped read-back for exactly that reason.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class VirtualNetworkLifecycleConformance(
    ClusterConformanceFixture<VirtualNetworkCase> fixture
) : ClusterConformanceTests<VirtualNetworkCase>(fixture),
    IClassFixture<ClusterConformanceFixture<VirtualNetworkCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the virtual-network type.</summary>
public sealed class VirtualNetworkSiloKillConformance : SiloKillConformanceTests<VirtualNetworkCase>;

/// <summary>
///     The same two suites against the <b>child</b> type,
///     <c>CyberCloud.Network/virtualNetworks/subnets</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>What this half does that the Docker-free half cannot: create the NETWORK first.</b>
///     <c>ClusterConformanceHarness.CreateAncestorsAsync</c> drives the ancestor's own case through
///     the real write path before the child's address is usable, so the parent-existence <c>404</c> is
///     an assertion about a name that was really claimed. ⚠ It still runs no Kube-OVN controller, so
///     no subnet here has ever allocated an address.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class NetworkSubnetLifecycleConformance(
    ClusterConformanceFixture<NetworkSubnetCase> fixture
) : ClusterConformanceTests<NetworkSubnetCase>(fixture),
    IClassFixture<ClusterConformanceFixture<NetworkSubnetCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the subnet child type.</summary>
public sealed class NetworkSubnetSiloKillConformance : SiloKillConformanceTests<NetworkSubnetCase>;
