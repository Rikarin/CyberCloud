using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Network.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Conformance;

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST CASE IN THE TREE WHOSE OBJECT IS CLUSTER-SCOPED, AND IT IS THE FIRST TO
///         NEED A CHANGE TO <c>test/CyberCloud.Cluster.Conformance</c> SINCE THE CHILD-TYPE WORK.</b>
///         Nine families' worth of evidence said the harness hosts a provider unchanged; this one
///         found the one axis on which that was not true. <c>ClusterConformanceHarness</c> derived a
///         CRD stub per custom kind and hard-coded <c>Scope = "Namespaced"</c>, so the definition it
///         installed served <c>/apis/kubeovn.io/v1/namespaces/{ns}/vpcs</c> while this provider
///         applies to <c>/apis/kubeovn.io/v1/vpcs</c> — a <c>404</c> on every cluster-facing
///         assertion. The scope is now <b>derived from the case's own
///         <c>ObjectRef.IsClusterScoped</c></b>, which is the same rule the harness already applies to
///         group, version, kind and plural, and which changes nothing for the nine families whose
///         objects all carry a namespace. ⚠ The Docker-free half needed nothing.
///     </para>
///     <para>
///         ⚠ <b>WHAT THE CLUSTER-BACKED SUITE PROVES FOR THIS FAMILY, AND WHAT IT DOES NOT.</b> The
///         k3s it starts has <b>no Kube-OVN in it</b>. So the suite exercises the apply path, ADR-013
///         label injection under real admission, conflict parsing, and — now — that a cluster-scoped
///         object is addressable at all. It does <b>not</b> prove that these manifests satisfy
///         Kube-OVN's schema: the derived stub's schema is
///         <c>x-kubernetes-preserve-unknown-fields</c>, so a field Kube-OVN would refuse is accepted
///         here. And it cannot prove anything at all about the behaviour this family's
///         <c>Matches</c> is built around, because <b>the thing that rewrites the spec is the
///         controller</b>, and there is no controller. <c>NetworkMatchesTests</c> hand-writes the
///         controller-shaped read-back for exactly that reason.
///     </para>
/// </remarks>
public sealed class VirtualNetworkCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Network/virtualNetworks",
            CreateProvider = () => new NetworkProvider(),
            ReconcilerType = typeof(VirtualNetworkReconciler),
            CreateReconciler = clock => new VirtualNetworkReconciler(clock),
            Type = VirtualNetworks.Type,
            ApiVersion = VirtualNetworks.V2026,
            Body = cluster => VirtualNetworks.Body(cluster),
            // ⚠ Changes `enableExternal`, which is the ONE field this provider renders into the Vpc.
            // A changed body that moved only `addressSpace` would pass the update test while proving
            // nothing reached the cluster at all — the address space is deliberately not rendered, so
            // it is invisible from the object, which makes it exactly the wrong axis to vary here.
            ChangedBody = cluster => VirtualNetworks.Body(cluster, enableExternal: true),
            // Drops the required `/properties/addressSpace/v4`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutAddressSpace(VirtualNetworks.Body(cluster)),
            InvalidBodyTarget = "/properties/addressSpace/v4",
            ActionName = VirtualNetworks.ShowIsolationAction,
            Objects = (id, ns) => [VirtualNetworks.VpcRef(ns, id.Name)],
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return VirtualNetworks.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required IPv4 address space removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutAddressSpace(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["addressSpace"]!.AsObject().Remove("v4");
        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks/subnets</c> — the child, registered into the same shared
///     suite as its parent.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE MEMBER LONGER THAN <see cref="VirtualNetworkCase" /> AND NOTHING ELSE DIFFERS</b>
///         — the second time that has held, and the first time it was relied on rather than
///         discovered. <c>CyberCloud.Storage/accounts/buckets</c> established the shape on the day the
///         four blockers closed; this family took it as given and it was.
///     </para>
///     <para>
///         ⚠ <b>The applicable assertion count is 28 rather than the parent's 27</b>, for the reason
///         <c>StorageBucketCase</c> records:
///         <c>CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource</c> self-skips at
///         <see cref="ResourceTypeName.Depth" /> 1. On this family that assertion guards something
///         sharper than it did on buckets — a <c>Subnet</c> whose <c>spec.vpc</c> names a
///         <c>Vpc</c> that does not exist is not inert, it joins Kube-OVN's <b>default</b> VPC, which
///         is the platform's own.
///     </para>
/// </remarks>
public sealed class NetworkSubnetCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Network/virtualNetworks/subnets",
            CreateProvider = () => new NetworkProvider(),
            ReconcilerType = typeof(NetworkSubnetReconciler),
            CreateReconciler = clock => new NetworkSubnetReconciler(clock),
            Type = NetworkSubnets.Type,
            ApiVersion = NetworkSubnets.V2026,
            Body = cluster => NetworkSubnets.Body(cluster),
            // ⚠ Changes `natOutgoing`, which the rendered object carries as a bare boolean. The
            // tempting axis is the prefix, and it is the wrong one twice over: it is Immutable, and
            // the controller canonicalizes it, so a changed-prefix body would be testing the two
            // things this family most needs kept out of an update test.
            ChangedBody = cluster => NetworkSubnets.Body(cluster, natOutgoing: true),
            // Drops the required `/properties/addressPrefix/v4`.
            InvalidBody = cluster => WithoutPrefix(NetworkSubnets.Body(cluster)),
            InvalidBodyTarget = "/properties/addressPrefix/v4",
            ActionName = NetworkSubnets.AddressUsageAction,
            Objects = (id, ns) => [NetworkSubnets.SubnetRef(ns, id)],
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            // ⚠ THE WHOLE PREDICATE, AND `spec.vpc` IS THE FIELD IT WAS WORTH CHANGING THE HARNESS
            // FOR. A Subnet whose `vpc` is wrong hands out addresses inside a DIFFERENT tenant's
            // routing domain under this tenant's resource id, and `vpc` is derived from the address —
            // so this was `NetworkSubnets.MatchesBody` while `ObjectMatchesDesired` was
            // `(objectJson, desiredJson) => bool`. This family was the second sighting of that limit;
            // `MatchContext` closed it. charts/managed/kube-ovn-subnet/conformance.yaml § owed,
            // `object-matches-desired-cannot-see-an-address`.
            //
            // ⚠ THE NAMESPACE COMES FROM `match.Namespace`, NOT FROM `match.Target.Namespace`, AND
            // THE DIFFERENCE COST FIVE RED ASSERTIONS BEFORE IT WAS NOTICED. A kube-ovn `Subnet` is
            // CLUSTER-SCOPED, so `SubnetRef` deliberately sets `Namespace = string.Empty` — while the
            // derived namespace is still a NAME COMPONENT of what the object renders, because
            // `spec.vpc` is `VirtualNetworks.ObjectNameOf(ns, parent)`. Reading it off the ObjectRef
            // gave "" and every comparison failed.
            //
            // ⚠ `NetworkReconcilerTests` KEEPS ITS OWN ASSERTIONS: two networks in ONE resource group
            // each holding a subnet called `web` is a collision this harness still cannot build.
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return NetworkSubnets.Matches(
                    match.ObjectJson,
                    match.Namespace,
                    match.Id,
                    desired.RootElement
                );
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } =
        [VirtualNetworkCase.ProviderCase];

    /// <summary>A valid body with the required IPv4 prefix removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutPrefix(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["addressPrefix"]!.AsObject().Remove("v4");
        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks/securityGroups</c> — the second child, registered into
///     the same shared suite as its parent and its sibling.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE MEMBER LONGER THAN <see cref="VirtualNetworkCase" /> AND NOTHING ELSE DIFFERS</b>
///         — the third time that has held, and the second time it was relied on rather than
///         discovered.
///     </para>
///     <para>
///         ⚠ <b>THE INVALID BODY IS A PORT RATHER THAN A MISSING PROPERTY, AND THAT IS THE POINT OF
///         THE TYPE.</b> Its two siblings drop a required property, because a required property is
///         all their schemas can refuse beyond a shape. This type has <b>no</b> required rule
///         property — an empty security group is the most restrictive one there is, so demanding a
///         rule would be demanding that a tenant open something to create a perimeter — and it
///         refuses <c>tcpPorts: "0"</c> at the API anyway, by
///         <see cref="PortRange.OptionalListPattern" />, with a <c>400</c> and this pointer. That is
///         the reshape's whole claim, run through the shared suite rather than asserted in a comment.
///     </para>
/// </remarks>
public sealed class NetworkSecurityGroupCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Network/virtualNetworks/securityGroups",
            CreateProvider = () => new NetworkProvider(),
            ReconcilerType = typeof(NetworkSecurityGroupReconciler),
            CreateReconciler = clock => new NetworkSecurityGroupReconciler(clock),
            Type = NetworkSecurityGroups.Type,
            ApiVersion = NetworkSecurityGroups.V2026,
            Body = cluster => NetworkSecurityGroups.Body(cluster),
            // ⚠ Adds a port, which changes the RULE COUNT as well as the rules — the rendered object
            // grows an element. The tempting axis is `allowSameGroupTraffic`, and it is the weaker
            // one: it is a single boolean the renderer copies straight through, so an update test
            // over it would pass against a renderer that never expanded a rule at all.
            ChangedBody = cluster => NetworkSecurityGroups.Body(cluster, ingressTcpPorts: "80,443,8080"),
            InvalidBody = cluster => NetworkSecurityGroups.Body(cluster, ingressTcpPorts: "0"),
            InvalidBodyTarget = "/properties/ingress/tcpPorts",
            ActionName = NetworkSecurityGroups.EffectiveRulesAction,
            Objects = (id, ns) => [NetworkSecurityGroups.SecurityGroupRef(ns, id)],
            // ⚠ THE WHOLE PREDICATE, UNLIKE THE SUBNET'S. `ObjectMatchesDesired` carries no address —
            // which for a subnet costs the `spec.vpc` half of its comparison. A SecurityGroup has no
            // field derived from the address at all, so nothing is left out here and the shared suite
            // checks exactly what the reconciler does.
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return NetworkSecurityGroups.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } =
        [VirtualNetworkCase.ProviderCase];
}

/// <summary>
///     <c>CyberCloud.Network/publicIpAddresses</c> — the family's fourth type and its first
///     top-level one since the network itself.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NO <c>Ancestors</c>, WHICH IS THE STRUCTURAL DIFFERENCE FROM THE OTHER TWO
///         ADDITIONS.</b> A subnet and a security group each declare the network's own case object so
///         the harness can create the parent first. An <c>OvnEip</c> names no VPC — it is allocated
///         from the operator's external subnet and attached later by a separate NAT object — so this
///         type is at <see cref="ResourceTypeName.Depth" /> 0 and there is nothing to create first.
///         ⚠ It therefore runs the parent's 27 applicable assertions rather than the children's 28:
///         <c>CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource</c> self-skips at
///         depth 0.
///     </para>
///     <para>
///         ⚠ <b><c>ChangedBody</c> VARIES A PROPERTY THE SCHEMA MARKS <c>Immutable</c>, AND THAT IS
///         NOT AN OVERSIGHT — IT IS THE ONLY AXIS THIS TYPE HAS.</b> Read firsthand in
///         <c>pkg/controller/ovn_eip.go</c> at <c>v1.16.2</c>: <c>handleUpdateOvnEip</c> refuses a
///         changed <c>v4Ip</c>, <c>v6Ip</c>, <c>macAddress</c> and <c>type</c> — four errors, one per
///         field, each beginning <i>"not support change"</i> — and <c>handleAddOvnEip</c> returns
///         early once <c>status.macAddress</c> is set. <b>Every field of an <c>OvnEip</c> is immutable
///         once it is ready</b>, so there is no body change that both reaches the cluster and would
///         survive a real controller, and <c>ProviderConformanceCase.ChangedBody</c> is <c>required</c>
///         with no way to say so. What this case therefore proves is that the renderer's output
///         reaches the cluster on an update; what it does <b>not</b> prove is that the update takes
///         effect on a real fabric — where the controller would log a refusal that nothing surfaces
///         while the platform reported <c>Succeeded</c>.
///         <c>charts/managed/kube-ovn-eip/conformance.yaml § owed</c>,
///         <c>an-allocated-address-cannot-be-changed</c>. ⚠ <c>SchemaProperty.Immutable</c> is a
///         declaration the manager does not enforce — its own remarks say so — so the write is
///         accepted here rather than refused, and that is the platform's recorded gap rather than this
///         provider's.
///     </para>
///     <para>
///         ⚠ <b>THE INVALID BODY IS A MALFORMED ADDRESS RATHER THAN A MISSING PROPERTY</b>, because
///         this type has no required property under <c>/properties</c> beyond the cluster: an address
///         the fabric picks is the ordinary request, so demanding one would be demanding that a tenant
///         know an address before they have been given one. <c>10.0.0</c> is refused at the API by
///         <see cref="IpAddresses.OptionalV4Pattern" />, with a <c>400</c> and this pointer, before the
///         write path answers.
///     </para>
/// </remarks>
public sealed class PublicIpAddressCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Network/publicIpAddresses",
            CreateProvider = () => new NetworkProvider(),
            ReconcilerType = typeof(PublicIpAddressReconciler),
            CreateReconciler = clock => new PublicIpAddressReconciler(clock),
            Type = PublicIpAddresses.Type,
            ApiVersion = PublicIpAddresses.V2026,
            // ⚠ The default asks for no particular address, which is the body whose rendered object
            // OMITS `spec.v4Ip` entirely. That is the case that deadlocks if the key is emitted empty
            // — see PublicIpAddresses.OvnEipJson — so it is the one the whole suite runs against.
            Body = cluster => PublicIpAddresses.Body(cluster),
            ChangedBody = cluster => PublicIpAddresses.Body(cluster, addressV4: "10.100.0.7"),
            InvalidBody = cluster => PublicIpAddresses.Body(cluster, addressV4: "10.0.0"),
            InvalidBodyTarget = "/properties/address/v4",
            ActionName = PublicIpAddresses.AllocationAction,
            Objects = (id, ns) => [PublicIpAddresses.OvnEipRef(ns, id.Name)],
            // ⚠ THE WHOLE PREDICATE, like the security group's and unlike the subnet's — and this
            // type reads NOTHING off `match`'s address half, which is a fact about the type rather
            // than a limit of the member. `PublicIpAddresses.Matches` compares `type`, `v4Ip` and
            // `v6Ip`, all of which the BODY decides. This is a depth-1 type, so there is no parent
            // for a spec field to point at — the subnet's `spec.vpc` problem cannot arise here.
            //
            // ⚠ AN OvnEip IS CLUSTER-SCOPED TOO, so `OvnEipRef` sets `Namespace = string.Empty` and
            // the derived namespace IS a component of the object's `metadata.name`. That still buys
            // this case nothing: the suite reads the object AT that ObjectRef, so a name rendered
            // against the wrong namespace fails as "not in the cluster" before any comparison runs.
            // `match.Namespace` is the right tool only where a name component reappears INSIDE the
            // spec, which is the subnet's case and not this one.
            // ⚠ Empty, and that is a statement rather than a formality — the member's own remarks
            // say so. An OvnEip carries no credential: the address is allocated by the controller
            // into `spec.v4Ip` and there is no Secret anywhere in this type's object set. The
            // provider most likely to omit this member is the next one to grow an
            // operator-generated credential, which is why it is `required`.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return PublicIpAddresses.Matches(match.ObjectJson, desired.RootElement);
            }
        };
}

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks/loadBalancers</c> — the family's fifth type and the first
///     whose objects are <b>namespaced</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST CASE IN THIS FAMILY WHOSE <c>ObjectRef</c>s CARRY A NAMESPACE, WHICH IS THE
///         AXIS THE HARNESS WAS CHANGED FOR AND IS NOW BEING EXERCISED IN THE OTHER DIRECTION.</b>
///         <c>ClusterConformanceHarness</c> derives each CRD stub's scope from the case's own
///         <c>ObjectRef.IsClusterScoped</c> because this family's four Kube-OVN kinds are
///         <c>scope="Cluster"</c>. A <c>ConfigMap</c> and a <c>Deployment</c> are built-in and
///         namespaced, so this case needs no stub at all — and <c>NetworkSuiteShapeTests</c> asserts
///         the refs are namespaced rather than leaving the difference to a reader.
///     </para>
///     <para>
///         ⚠ <b><c>ChangedBody</c> ADDS A BACKEND, WHICH IS THE ONLY AXIS THAT REACHES BOTH
///         OBJECTS.</b> The tempting change is the sizing preset — one field on the pod template, copied
///         straight through — and it would pass against a renderer that never regenerated the
///         configuration at all. A second backend address changes the <c>ConfigMap</c>'s <c>data</c>
///         <i>and</i> the pod template's config hash, which is the pair whose disagreement is this
///         type's headline failure: a config change that applies cleanly and restarts nothing.
///     </para>
///     <para>
///         ⚠ <b>THE INVALID BODY IS A PORT OF <c>0</c> RATHER THAN A MISSING PROPERTY.</b> Every
///         required property here carries a default — a required patterned property must, or the
///         generated chart's own literal fails <c>helm lint</c> — so removing one and letting the
///         default fill it in would test nothing. <c>0</c> is refused at
///         <c>/properties/frontend/port</c> by <c>Minimum</c>, with a <c>400</c> and that pointer,
///         before the write path answers.
///     </para>
///     <para>
///         ⚠ <b><c>ObjectMatchesDesired</c> READS <c>match.Namespace</c> AND <c>match.Id</c>, LIKE THE
///         SUBNET AND UNLIKE THE OTHER THREE.</b> The pod template's
///         <c>ovn.kubernetes.io/logical_switch</c> is <c>{namespace}-{network}-{subnet}</c> — a name
///         built from the address — so a comparison without them would silently skip the field that
///         decides <b>which tenant's network this proxy is inside</b>. It is <c>spec.vpc</c>'s
///         hazard on a different object.
///     </para>
/// </remarks>
public sealed class LoadBalancerCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Network/virtualNetworks/loadBalancers",
            CreateProvider = () => new NetworkProvider(),
            ReconcilerType = typeof(LoadBalancerReconciler),
            CreateReconciler = clock => new LoadBalancerReconciler(clock),
            Type = LoadBalancers.Type,
            ApiVersion = LoadBalancers.V2026,
            Body = cluster => LoadBalancers.Body(cluster),
            ChangedBody = cluster =>
                LoadBalancers.Body(cluster, backendAddresses: "10.20.1.11,10.20.1.12"),
            InvalidBody = cluster => LoadBalancers.Body(cluster, frontendPort: 0),
            InvalidBodyTarget = "/properties/frontend/port",
            ActionName = LoadBalancers.BackendsAction,
            Objects = (id, ns) => LoadBalancers.Objects(ns, id),
            // ⚠ Empty, and it is a statement rather than a formality. `showBackends` reads a
            // Deployment's status and the resource's own stored body; there is no Secret in this
            // type's object set at all, because an L4 proxy terminates nothing and holds no key.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);

                return LoadBalancers.Matches(
                    match.ObjectJson,
                    match.Namespace,
                    match.Id,
                    desired.RootElement
                );
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } =
        [VirtualNetworkCase.ProviderCase];
}

/// <summary>The shared suite, run against the virtual-network provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class VirtualNetworkConformance(ProviderTestCluster<VirtualNetworkCase> cluster)
    : ProviderConformanceTests<VirtualNetworkCase>(cluster),
        IClassFixture<ProviderTestCluster<VirtualNetworkCase>>;

/// <summary>
///     The <b>same</b> suite, run against the subnet child type.
/// </summary>
/// <remarks>
///     ⚠ <b>The same class, not a child-shaped copy of it.</b> A separate suite for children would be
///     free to assert less and nothing would say which assertions it had dropped.
/// </remarks>
/// <param name="cluster">The harness.</param>
public sealed class NetworkSubnetConformance(ProviderTestCluster<NetworkSubnetCase> cluster)
    : ProviderConformanceTests<NetworkSubnetCase>(cluster),
        IClassFixture<ProviderTestCluster<NetworkSubnetCase>>;

/// <summary>
///     The <b>same</b> suite again, run against the security-group child type.
/// </summary>
/// <param name="cluster">The harness.</param>
public sealed class NetworkSecurityGroupConformance(
    ProviderTestCluster<NetworkSecurityGroupCase> cluster
) : ProviderConformanceTests<NetworkSecurityGroupCase>(cluster),
    IClassFixture<ProviderTestCluster<NetworkSecurityGroupCase>>;

/// <summary>
///     The <b>same</b> suite again, run against the public-address type.
/// </summary>
/// <param name="cluster">The harness.</param>
public sealed class PublicIpAddressConformance(ProviderTestCluster<PublicIpAddressCase> cluster)
    : ProviderConformanceTests<PublicIpAddressCase>(cluster),
        IClassFixture<ProviderTestCluster<PublicIpAddressCase>>;

/// <summary>
///     The <b>same</b> suite again, run against the load balancer — the family's second child-shaped
///     addition and its first namespaced one.
/// </summary>
/// <param name="cluster">The harness.</param>
public sealed class LoadBalancerConformance(ProviderTestCluster<LoadBalancerCase> cluster)
    : ProviderConformanceTests<LoadBalancerCase>(cluster),
        IClassFixture<ProviderTestCluster<LoadBalancerCase>>;

/// <summary>The container-backed half, skipped loudly, against the virtual-network type.</summary>
public sealed class VirtualNetworkClusterBackedConformance()
    : ClusterBackedConformanceTests(VirtualNetworkCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the subnet child type.</summary>
public sealed class NetworkSubnetClusterBackedConformance()
    : ClusterBackedConformanceTests(NetworkSubnetCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the security-group child type.</summary>
public sealed class NetworkSecurityGroupClusterBackedConformance()
    : ClusterBackedConformanceTests(NetworkSecurityGroupCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the public-address type.</summary>
public sealed class PublicIpAddressClusterBackedConformance()
    : ClusterBackedConformanceTests(PublicIpAddressCase.ProviderCase);

/// <summary>
///     The container-backed half against the load balancer.
/// </summary>
/// <remarks>
///     ⚠ <b>THIS IS THE ONE CASE IN THE FAMILY THE CLUSTER-BACKED SUITE PROVES SOMETHING REAL
///     ABOUT.</b> Its four siblings render Kube-OVN custom resources into a k3s that has no Kube-OVN,
///     so the derived CRD stub's schema is <c>x-kubernetes-preserve-unknown-fields</c> and a field the
///     real fabric would refuse is accepted. A <c>ConfigMap</c> and a <c>Deployment</c> are <b>built
///     in</b>: the API server validates them against its own schemas, defaults them, and would refuse
///     a malformed pod template outright. ⚠ What it still cannot prove is the part that needs
///     Kube-OVN — that the <c>logical_switch</c> annotation puts the pod on a tenant's subnet — and
///     the pod will not schedule at all in that harness, which is why the assertions are about the
///     objects rather than about traffic.
/// </remarks>
public sealed class LoadBalancerClusterBackedConformance()
    : ClusterBackedConformanceTests(LoadBalancerCase.ProviderCase);

/// <summary>
///     What this provider's two registrations into the shared suite are <b>shaped</b> like.
/// </summary>
public sealed class NetworkSuiteShapeTests {
    [Fact]
    public void TheChildRunsEveryAssertionItsParentDoesRatherThanASubset() {
        // ⚠ "The subnet runs the same suite as the network" is a claim about a COUNT, and a claim
        // about a count that nothing counts is how a suite goes green by asking less.
        var parent = RunnableFactsOf(typeof(VirtualNetworkConformance));
        var child = RunnableFactsOf(typeof(NetworkSubnetConformance));

        child.ShouldBe(
            parent,
            "the subnet runs a different set of assertions than the virtual network does. A "
            + "child-shaped copy of the suite is free to assert less, and nothing but this test would "
            + "say which assertions it had dropped."
        );

        RunnableFactsOf(typeof(NetworkSecurityGroupConformance)).ShouldBe(
            parent,
            "the security group runs a different set of assertions than the virtual network does."
        );

        RunnableFactsOf(typeof(PublicIpAddressConformance)).ShouldBe(
            parent,
            "the public address runs a different set of assertions than the virtual network does. It "
            + "is the family's second TOP-LEVEL type, so it must run exactly the network's set — a "
            + "type that is neither a child nor a copy of the parent is the one a shape test would "
            + "otherwise let drift."
        );

        RunnableFactsOf(typeof(LoadBalancerConformance)).ShouldBe(
            parent,
            "the load balancer runs a different set of assertions than the virtual network does."
        );

        parent.Length.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void OnlyTheChildrenDescribeAnAncestorAndItIsTheNetworksOwnCaseObject() {
        AncestorsOf<VirtualNetworkCase>().ShouldBeEmpty();

        // ⚠ AND THE PUBLIC ADDRESS IS THE ONE ADDITION THAT MUST *NOT* DECLARE ONE. Three of the four
        // types in this family are inside a virtual network and the fourth reads as though it should
        // be. An ancestor here would make the harness create a Vpc before every address case, which
        // would pass — and would encode into the suite a containment the substrate does not have: an
        // OvnEip carries no field naming a VPC at all.
        AncestorsOf<PublicIpAddressCase>().ShouldBeEmpty();

        // ⚠ BOTH children, and the assertion is `ShouldBeSameAs` rather than an equality: an ancestor
        // that is a SECOND DESCRIPTION of the virtual network can disagree with the network's own
        // case object the first time either changes, and the symptom is a child suite creating a
        // parent whose body no longer validates.
        foreach (var ancestors in
                 (ReadOnlySpan<ImmutableArray<ProviderConformanceCase>>)[
                     AncestorsOf<NetworkSubnetCase>(),
                     AncestorsOf<NetworkSecurityGroupCase>(),
                     // ⚠ The load balancer DOES declare one, which is the opposite of the public
                     // address's answer and for the opposite reason: every object it renders is
                     // annotated onto a subnet of one VPC, so the harness must create the network
                     // first or the case is testing a proxy in a network that does not exist.
                     AncestorsOf<LoadBalancerCase>()
                 ]) {
            ancestors.Length.ShouldBe(1);

            ancestors[0].ShouldBeSameAs(
                VirtualNetworkCase.ProviderCase,
                "a child's ancestor is a SECOND DESCRIPTION of the virtual network rather than the "
                + "network's own case object."
            );

            ancestors[0].Type.ShouldBe(VirtualNetworks.Type);
        }
    }

    [Fact]
    public void BothCasesRenderClusterScopedObjectsAndTheHarnessCanSeeThat() {
        // ⚠ THE ASSERTION THAT WOULD HAVE SAVED A DAY, AND IT IS ABOUT THE CASE RATHER THAN THE
        // PROVIDER. ClusterConformanceHarness derives each CRD stub's `scope` from these ObjectRefs;
        // before this family it hard-coded "Namespaced" and there was no case in the tree for which
        // that was wrong. If a future edit renders these objects INTO a namespace, the harness would
        // silently go back to installing a Namespaced definition — which would work, and would mean
        // the provider was applying tenant VPCs to a REST path Kube-OVN does not serve.
        var id = new ResourceId(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "rg",
            VirtualNetworks.Type,
            "net",
            Guid.NewGuid()
        );

        var vpc = VirtualNetworkCase.ProviderCase.Objects(id, "ns");

        vpc.Length.ShouldBe(1);

        vpc[0].IsClusterScoped.ShouldBeTrue(
            "a Kube-OVN Vpc is +kubebuilder:resource:scope=\"Cluster\". An ObjectRef carrying a "
            + "namespace would make the harness install a Namespaced CRD stub, and the suite would go "
            + "green against a REST path the real substrate does not serve."
        );

        // ⚠ The security group's ObjectRef needs a PARENT to render its name at all, so it is built
        // from a child address rather than from the network's.
        var child = new ResourceId(
            id.TenantId,
            id.SubscriptionId,
            "rg",
            NetworkSecurityGroups.Type,
            "web",
            Guid.NewGuid(),
            "net"
        );

        var group = NetworkSecurityGroupCase.ProviderCase.Objects(child, "ns");

        group.Length.ShouldBe(1);

        group[0].IsClusterScoped.ShouldBeTrue(
            "a Kube-OVN SecurityGroup is +kubebuilder:resource:scope=\"Cluster\"."
        );

        var address = PublicIpAddressCase.ProviderCase.Objects(id, "ns");

        address.Length.ShouldBe(1);

        address[0].IsClusterScoped.ShouldBeTrue(
            "a Kube-OVN OvnEip is +kubebuilder:resource:scope=\"Cluster\"."
        );

        address[0].Kind.Plural.ShouldBe(
            "ovn-eips",
            "the plural is HYPHENATED — +kubebuilder:resource:path=\"ovn-eips\". "
            + "ClusterConformanceHarness derives its CRD stub's path from GroupVersionKind.Plural, so "
            + "`ovneips` would install a definition at a path the apply never reaches — and the "
            + "symptom is a discovery error naming a missing operator rather than a wrong plural."
        );

        group[0].Kind.Plural.ShouldBe(
            "security-groups",
            "the plural is HYPHENATED. ClusterConformanceHarness derives its CRD stub's path from "
            + "GroupVersionKind.Plural, so `securitygroups` would install a definition at a path the "
            + "apply never reaches — and the symptom is a discovery error naming a missing operator "
            + "rather than a wrong plural."
        );

        // ⚠ AND THE FIFTH TYPE IS THE ONE THAT MUST NOT BE CLUSTER-SCOPED, which is worth asserting
        // in the same test rather than trusting the difference to be remembered. A ConfigMap and a
        // Deployment are namespaced; an ObjectRef with an empty namespace would be applied to
        // /api/v1/configmaps, which the API server does not serve, and the symptom is a 404 that
        // reads as a missing object.
        var balancer = LoadBalancerCase.ProviderCase.Objects(
            child with { Type = LoadBalancers.Type },
            "ns"
        );

        balancer.Length.ShouldBe(2);

        foreach (var target in balancer) {
            target.IsClusterScoped.ShouldBeFalse(
                $"'{target.Kind.Kind}' is namespaced, and ReconcileDriver.NamespaceFor is what keeps "
                + "two subscriptions' load balancers apart — which is why LoadBalancers.ObjectNameOf "
                + "folds in the parent network and NOT the namespace."
            );

            target.Namespace.ShouldBe("ns");
            target.Name.ShouldBe("net-web");
        }
    }

    static ImmutableArray<ProviderConformanceCase> AncestorsOf<TSource>()
        where TSource : IProviderCaseSource => TSource.Ancestors;

    /// <summary>Every <c>[Fact]</c> a test class runs, by name, ordered.</summary>
    /// <param name="suite">The closed test class.</param>
    static ImmutableArray<string> RunnableFactsOf(Type suite) =>
        [
            .. suite
                .GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                )
                .Where(x => x.GetCustomAttributes(typeof(FactAttribute), true).Length > 0)
                .Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
        ];
}
