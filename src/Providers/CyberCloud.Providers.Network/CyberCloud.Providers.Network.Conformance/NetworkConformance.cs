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
            // ⚠ THE BODY HALF ONLY — SECOND SIGHTING OF `StorageBuckets.MatchesBody`'S FINDING, AND
            // THE FIRST TIME IT WAS PREDICTED RATHER THAN FOUND BY A RED SUITE.
            // `ObjectMatchesDesired` is `(objectJson, desiredJson) => bool` and carries no ADDRESS,
            // and a subnet's `spec.vpc` is its PARENT'S name, which lives only in the address.
            //
            // ⚠ WHAT THE SUITE THEREFORE DOES NOT CHECK FOR A SUBNET, said out loud so nobody has to
            // infer it: that the rendered object binds to the right Vpc. That is the single most
            // consequential field on the object — a Subnet bound to the wrong Vpc hands out addresses
            // inside another tenant's routing domain — and NetworkReconcilerTests asserts it
            // against real addresses, including the case this harness cannot build: two networks in
            // ONE resource group each holding a subnet called `web`.
            // charts/managed/kube-ovn-subnet/conformance.yaml § owed,
            // `object-matches-desired-cannot-see-an-address`.
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return NetworkSubnets.MatchesBody(match.ObjectJson, desired.RootElement);
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
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return NetworkSecurityGroups.Matches(match.ObjectJson, desired.RootElement);
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

/// <summary>The container-backed half, skipped loudly, against the virtual-network type.</summary>
public sealed class VirtualNetworkClusterBackedConformance()
    : ClusterBackedConformanceTests(VirtualNetworkCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the subnet child type.</summary>
public sealed class NetworkSubnetClusterBackedConformance()
    : ClusterBackedConformanceTests(NetworkSubnetCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the security-group child type.</summary>
public sealed class NetworkSecurityGroupClusterBackedConformance()
    : ClusterBackedConformanceTests(NetworkSecurityGroupCase.ProviderCase);

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

        parent.Length.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void OnlyTheChildrenDescribeAnAncestorAndItIsTheNetworksOwnCaseObject() {
        AncestorsOf<VirtualNetworkCase>().ShouldBeEmpty();

        // ⚠ BOTH children, and the assertion is `ShouldBeSameAs` rather than an equality: an ancestor
        // that is a SECOND DESCRIPTION of the virtual network can disagree with the network's own
        // case object the first time either changes, and the symptom is a child suite creating a
        // parent whose body no longer validates.
        foreach (var ancestors in
                 (ReadOnlySpan<ImmutableArray<ProviderConformanceCase>>)[
                     AncestorsOf<NetworkSubnetCase>(), AncestorsOf<NetworkSecurityGroupCase>()
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

        group[0].Kind.Plural.ShouldBe(
            "security-groups",
            "the plural is HYPHENATED. ClusterConformanceHarness derives its CRD stub's path from "
            + "GroupVersionKind.Plural, so `securitygroups` would install a definition at a path the "
            + "apply never reaches — and the symptom is a discovery error naming a missing operator "
            + "rather than a wrong plural."
        );
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
