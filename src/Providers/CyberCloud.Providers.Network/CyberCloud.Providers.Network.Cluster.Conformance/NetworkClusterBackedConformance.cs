using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Network.Conformance;

namespace CyberCloud.Providers.Network.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the tenant networking provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Ten class declarations over the five cases
///         <c>CyberCloud.Providers.Network.Conformance</c> declares.</b> One provider, one
///         <c>ProviderConformanceCase</c> per type, two suites per case. ⚠ <b>This sentence read "four
///         over two" while the family had four types</b>, which is how the public address's pair went
///         missing: nothing counts the classes in this file against the cases in that one, so a row
///         with no class here is simply a row the docs/plan/24 exit criterion never reaches.
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

/// <summary>
///     The same two suites against the <b>second</b> child type,
///     <c>CyberCloud.Network/virtualNetworks/securityGroups</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>THE ONE THING THIS HALF PROVES THAT NOTHING ELSE IN THE TREE DOES FOR THIS TYPE: THE
///     HYPHENATED PLURAL.</b> <c>SecurityGroup</c>'s path is <c>security-groups</c>, not
///     <c>securitygroups</c>, and <c>ClusterConformanceHarness</c> derives its CRD stub's path from
///     <see cref="GroupVersionKind.Plural" /> — so a guessed plural installs a definition at a path
///     the apply never reaches and every assertion here would fail with a discovery error naming a
///     missing operator. ⚠ It still runs no Kube-OVN controller, so <b>no ACL in this suite has ever
///     been programmed</b> and nothing here has checked that <c>ipVersion: ipv4</c> is spelled the way
///     <c>validateSgRule</c> wants.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class NetworkSecurityGroupLifecycleConformance(
    ClusterConformanceFixture<NetworkSecurityGroupCase> fixture
) : ClusterConformanceTests<NetworkSecurityGroupCase>(fixture),
    IClassFixture<ClusterConformanceFixture<NetworkSecurityGroupCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the security-group child type.</summary>
public sealed class NetworkSecurityGroupSiloKillConformance
    : SiloKillConformanceTests<NetworkSecurityGroupCase>;

/// <summary>
///     The same two suites against <c>CyberCloud.Network/publicIpAddresses</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>THESE TWO WERE MISSING, AND NOTHING WOULD HAVE SAID SO.</b> The fourth type in this family
///     shipped with a <c>ProviderConformanceCase</c>, a Docker-free suite and a
///     <c>ClusterBackedConformanceTests</c> registration — and no class in <i>this</i> assembly, so it
///     had never been through a real API server at all. The docs/plan/24 § Phase 1 exit criterion is a
///     claim about every catalogue row, and a row with no class here is a row the criterion silently
///     skips: there is no roster to count against, only files somebody remembered to write.
///     ⚠ What it establishes for this type is the <b>hyphenated plural</b> <c>ovn-eips</c>, for
///     <see cref="NetworkSecurityGroupLifecycleConformance" />'s reason, and cluster-scoped addressing.
///     What it cannot establish is anything the Kube-OVN controller does, which on this type is
///     everything the resource is for —
///     <c>charts/managed/kube-ovn-eip/conformance.yaml § owed</c>,
///     <c>the-cluster-backed-suite-proves-less-than-it-looks</c>.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class PublicIpAddressLifecycleConformance(
    ClusterConformanceFixture<PublicIpAddressCase> fixture
) : ClusterConformanceTests<PublicIpAddressCase>(fixture),
    IClassFixture<ClusterConformanceFixture<PublicIpAddressCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the public-address type.</summary>
public sealed class PublicIpAddressSiloKillConformance : SiloKillConformanceTests<PublicIpAddressCase>;

/// <summary>
///     The same two suites against <c>CyberCloud.Network/virtualNetworks/loadBalancers</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>THE ONLY ROW IN THIS FAMILY WHOSE OBJECTS THIS HARNESS CAN REALLY VALIDATE.</b> The other
///     four render Kube-OVN custom resources into a k3s that has no Kube-OVN, so the derived CRD stub
///     is <c>x-kubernetes-preserve-unknown-fields</c> and a field the real fabric would refuse is
///     accepted here. A <c>ConfigMap</c> and a <c>Deployment</c> are <b>built in</b>: the API server
///     validates them against its own schemas and would refuse a malformed pod template outright — so
///     a container with no image, a selector that does not match its own template, or a sysctl the
///     kubelet does not recognise fails here rather than in a tenant's cluster.
///     ⚠ <b>What it still cannot prove is the part that needs Kube-OVN.</b> No CNI in that cluster
///     reads <c>ovn.kubernetes.io/logical_switch</c>, so the pod is scheduled onto the ordinary pod
///     network — or not scheduled at all — and nothing here has ever put a proxy inside a tenant's
///     routing domain.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class LoadBalancerLifecycleConformance(
    ClusterConformanceFixture<LoadBalancerCase> fixture
) : ClusterConformanceTests<LoadBalancerCase>(fixture),
    IClassFixture<ClusterConformanceFixture<LoadBalancerCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the load balancer.</summary>
public sealed class LoadBalancerSiloKillConformance : SiloKillConformanceTests<LoadBalancerCase>;
