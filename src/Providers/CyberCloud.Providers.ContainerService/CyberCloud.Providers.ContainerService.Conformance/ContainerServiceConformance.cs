using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.ContainerService.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Conformance;

/// <summary>
///     <c>CyberCloud.ContainerService/managedClusters</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, which is the tenth time that number has
///         held — and the first time it held for a type whose product is not a workload.</b> Nine
///         families had established the shape across object counts (one to five), API-group counts
///         (one to three), operator-ful and operator-less services, two milestones and two catalogue
///         documents. What this one adds is a resource whose <i>correctness cannot be observed from
///         the objects it applies</i>, and <c>test/CyberCloud.Conformance</c> still needed no change.
///     </para>
///     <para>
///         ⚠ <b>ONE THING IN THE HARNESS DID HAVE TO CHANGE, AND IT IS NOT ABOUT SHAPE.</b>
///         <c>ProviderTestCluster.LiftQuotaAsync</c> is new. This is the first type to draw
///         <see cref="QuotaMeter.Clusters" />, whose default limit is <b>five</b>, and the suite
///         creates against one subscription twenty-eight times with nothing releasing in between —
///         so before that change the sixth assertion onwards failed with a quota error naming neither
///         this provider nor the harness. <c>charts/managed/opensearch/conformance.yaml § owed</c> had
///         recorded the budget as a diagnostics problem; it is closed rather than diagnosed.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.ObjectMatchesDesired" /> is
///         <c>ManagedClusters.Matches</c>, which is a claim about the REQUEST.</b> It cannot be
///         anything else — the predicate takes an object and a body and returns a bool, and whether a
///         tenant has a working Kubernetes cluster is in neither. <c>ManagedClusters.Readiness</c> is
///         the other half, the reconciler consults it, and no assertion in this suite reaches it
///         because <see cref="FakeKubeCluster" /> echoes an apply back with no <c>status</c> on it.
///         <c>charts/managed/kubernetes/conformance.yaml § owed</c>, <c>converged-is-not-ready</c>.
///     </para>
/// </remarks>
public sealed class ManagedClusterCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ContainerService/managedClusters",
            CreateProvider = () => new ContainerServiceProvider(),
            ReconcilerType = typeof(ManagedClusterReconciler),
            CreateReconciler = clock => new ManagedClusterReconciler(clock),
            Type = ManagedClusters.Type,
            ApiVersion = ManagedClusters.V2026,
            Body = cluster => ManagedClusters.Body(cluster),
            // ⚠ Changes `controlPlane.replicas`, which the rendered KamajiControlPlane carries in TWO
            // places — `spec.replicas` and, through the meters, the amount the update re-reserves. A
            // body that differed only where the reconciler ignores it would pass the update test while
            // proving the update never left the grain.
            ChangedBody = cluster => ManagedClusters.Body(cluster, controlPlaneReplicas: 3),
            // Drops the required `/properties/network/podCidr`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and then
            // tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutPodCidr(ManagedClusters.Body(cluster)),
            InvalidBodyTarget = "/properties/network/podCidr",
            ActionName = ManagedClusters.ListCredentialsAction,
            Objects = (id, ns) => [
                ManagedClusters.InfrastructureRef(ns, id.Name),
                ManagedClusters.ControlPlaneRef(ns, id.Name),
                ManagedClusters.ClusterRef(ns, id.Name)
            ],
            // ⚠ TWO OBJECTS, AND NEITHER IS THIS PROVIDER'S TO WRITE. Cluster API creates the
            // kubeconfig Secret when the control plane comes up, and the Kamaji control-plane provider
            // PATCHES `spec.controlPlaneEndpoint` onto the Cluster it does not own — see
            // ManagedClusters.ExternallyManagedAnnotation for why exactly one controller owns that
            // field. `listCredentials` reads one of each, which is why both are here.
            //
            // ⚠ The Cluster placed here carries the endpoint and NOT the rest of the applied shape,
            // and that narrowness is safe for a stated reason rather than by luck: this hook runs only
            // inside the action assertion, after every test that judges the object against the desired
            // body has finished with it. A fixture that tried to reproduce the whole applied object
            // would be asserting the reconciler's output from the test's side, which is what
            // ObjectMatchesDesired is for.
            OperatorWritten = (id, ns) => [
                (KubeSecret.Ref(ns, ManagedClusters.KubeconfigSecretName(id.Name)),
                    OperatorSecret.Json(
                        KubeSecret.Ref(ns, ManagedClusters.KubeconfigSecretName(id.Name)),
                        [(ManagedClusters.KubeconfigKey, Kubeconfig(ns, id.Name))]
                    )),
                (ManagedClusters.ClusterRef(ns, id.Name), EndpointedCluster(ns, id.Name))
            ],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return ManagedClusters.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>The API server host the fixture's control plane reports.</summary>
    const string EndpointHost = "conformance-control-plane";

    /// <summary>Its port.</summary>
    const int EndpointPort = 6443;

    /// <summary>The Cluster as the control-plane provider leaves it, endpoint patched on.</summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="name">The resource's own name.</param>
    static string EndpointedCluster(string ns, string name) =>
        new JsonObject {
            ["apiVersion"] = "cluster.x-k8s.io/v1beta1",
            ["kind"] = "Cluster",
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = ns },
            ["spec"] = new JsonObject {
                ["controlPlaneEndpoint"] = new JsonObject {
                    ["host"] = EndpointHost,
                    ["port"] = EndpointPort
                }
            }
        }.ToJsonString();

    /// <summary>
    ///     A kubeconfig shaped like the one Cluster API writes, with a real client certificate in it.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>A GENUINE CERTIFICATE, GENERATED HERE, AND THAT IS THE POINT OF THE FIXTURE RATHER
    ///     THAN CEREMONY.</b> <c>ManagedClusters.CredentialExpiry</c> answers <c>/expiresAt</c> by
    ///     opening the certificate and reading its <c>notAfter</c>; a fixture with a placeholder string
    ///     where the certificate goes would exercise the line scan and never the parse, and the parse
    ///     is the half that decides whether a caller gets a date or a refusal.
    /// </remarks>
    static string Kubeconfig(string ns, string name) {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=kubernetes-admin, O=system:masters",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(10)
        );

        var pem = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(certificate.ExportCertificatePem())
        );

        // ⚠ Written as YAML text rather than through a serializer, because that is what the handler
        // meets: there is no YAML parser in this tree and CredentialExpiry scans for one line.
        return $"""
               apiVersion: v1
               kind: Config
               clusters:
               - name: {name}
                 cluster:
                   server: https://{EndpointHost}:{EndpointPort}
               contexts:
               - name: {name}-admin@{name}
                 context:
                   cluster: {name}
                   namespace: {ns}
                   user: {name}-admin
               current-context: {name}-admin@{name}
               users:
               - name: {name}-admin
                 user:
                   client-certificate-data: {pem}
               """;
    }

    /// <summary>A valid body with the required pod CIDR removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutPodCidr(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["network"]!.AsObject().Remove("podCidr");
        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.ContainerService/managedClusters/agentPools</c> — the second child type to ship in
///     this platform, registered into the same shared suite as its parent.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE MEMBER LONGER THAN <see cref="ManagedClusterCase" /> AND NOTHING ELSE DIFFERS</b>,
///         which is the second confirmation of what <c>StorageBucketCase</c> measured: a child costs a
///         <see cref="Ancestors" /> and no change anywhere in the harness. ⚠ It is the stronger
///         confirmation of the two, because this child is not a small thing next to its parent — it
///         renders three objects to its parent's three and it draws three derived meters to its
///         parent's two.
///     </para>
///     <para>
///         ⚠ <b>The one assertion only a child exercises is worth more here than on a bucket.</b>
///         <c>CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource</c> self-skips at
///         <see cref="ResourceTypeName.Depth" /> 1. For a bucket, the create it refuses would have
///         produced an unreconciled object; for a node pool it would produce a
///         <c>MachineDeployment</c> naming a <c>Cluster</c> that is not there — which Cluster API
///         admits, stores and never provisions, so the tenant would have a node pool reporting
///         <c>Succeeded</c> with no machines in it and nothing anywhere saying why.
///     </para>
///     <para>
///         ⚠ <see cref="Ancestors" /> is the parent's own case <i>object</i> rather than a description
///         of it — see <c>IProviderCaseSource.Ancestors</c>. A second description of a cluster, written
///         for the pool's benefit, would be a second thing to keep in step with the cluster's schema.
///     </para>
/// </remarks>
public sealed class AgentPoolCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ContainerService/managedClusters/agentPools",
            CreateProvider = () => new ContainerServiceProvider(),
            ReconcilerType = typeof(AgentPoolReconciler),
            CreateReconciler = clock => new AgentPoolReconciler(clock),
            Type = AgentPools.Type,
            ApiVersion = AgentPools.V2026,
            Body = cluster => AgentPools.Body(cluster),
            // ⚠ Changes `count`, which the rendered MachineDeployment carries as `spec.replicas` and
            // which all three meters read. A changed body whose difference the renderer can drop would
            // pass the update test while proving nothing about whether the update reached the cluster.
            ChangedBody = cluster => AgentPools.Body(cluster, count: 5),
            // Drops the required `/properties/osDiskSize`.
            InvalidBody = cluster => WithoutOsDiskSize(AgentPools.Body(cluster)),
            InvalidBodyTarget = "/properties/osDiskSize",
            ActionName = AgentPools.UpgradeNodeImageAction,
            Objects = (id, ns) => [
                AgentPools.MachineTemplateRef(ns, id),
                AgentPools.BootstrapRef(ns, id),
                AgentPools.MachineDeploymentRef(ns, id)
            ],
            // ⚠ THE BODY HALF ONLY — second sighting of the limit
            // charts/managed/seaweedfs-bucket/conformance.yaml records as
            // `object-matches-desired-cannot-see-an-address`. `ObjectMatchesDesired` is
            // `(objectJson, desiredJson) => bool` and carries no ADDRESS, and a pool's `clusterName`
            // and selector are both derived from one. `AgentPools.MatchesBody` is the reachable half
            // and `AgentPoolReconcilerTests` asserts the other against real addresses — including the
            // case this harness could never reach, two clusters in ONE resource group each holding a
            // pool called `workers`.
            // `upgradeNodeImage` is long-running and reads nothing; the node image roll is the
            // reconciler's work, driven through the operation grain. Stated rather than defaulted —
            // see ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return AgentPools.MatchesBody(objectJson, desired.RootElement);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } =
        [ManagedClusterCase.ProviderCase];

    /// <summary>A valid body with the required root-volume size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutOsDiskSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove("osDiskSize");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-Kubernetes provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ManagedClusterConformance(ProviderTestCluster<ManagedClusterCase> cluster)
    : ProviderConformanceTests<ManagedClusterCase>(cluster),
        IClassFixture<ProviderTestCluster<ManagedClusterCase>>;

/// <summary>
///     The <b>same</b> suite, run against the node-pool child type.
/// </summary>
/// <remarks>
///     ⚠ <b>The same class, not a child-shaped copy of it.</b> A separate suite for children would be
///     free to assert less and nothing would say which assertions it had dropped. Deriving from
///     <c>ProviderConformanceTests&lt;T&gt;</c> makes the count a fact of the compiler rather than of
///     anybody's diligence.
/// </remarks>
/// <param name="cluster">The harness.</param>
public sealed class AgentPoolConformance(ProviderTestCluster<AgentPoolCase> cluster)
    : ProviderConformanceTests<AgentPoolCase>(cluster), IClassFixture<ProviderTestCluster<AgentPoolCase>>;

/// <summary>The container-backed half, skipped loudly, against the cluster type.</summary>
public sealed class ManagedClusterClusterBackedConformance()
    : ClusterBackedConformanceTests(ManagedClusterCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the node-pool type.</summary>
public sealed class AgentPoolClusterBackedConformance()
    : ClusterBackedConformanceTests(AgentPoolCase.ProviderCase);

/// <summary>
///     What this provider's two registrations into the shared suite are <b>shaped</b> like.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about the SUITE'S shape, not about the provider.</b> It lives in
///     this project rather than in <c>CyberCloud.Providers.ContainerService.Tests</c> because that
///     project deliberately does not reference this one, and these two test classes are the subjects.
/// </remarks>
public sealed class ContainerServiceSuiteShapeTests {
    [Fact]
    public void TheChildRunsEveryAssertionItsParentDoesRatherThanASubset() {
        // ⚠ "The pool runs the same suite as the cluster" is a claim about a COUNT, and a claim about a
        // count that nothing counts is how a suite goes green by asking less.
        var parent = RunnableFactsOf(typeof(ManagedClusterConformance));
        var child = RunnableFactsOf(typeof(AgentPoolConformance));

        child.ShouldBe(
            parent,
            "the node pool runs a different set of assertions than the cluster does. A child-shaped "
            + "copy of the suite is free to assert less, and nothing but this test would say which "
            + "assertions it had dropped."
        );

        parent.Length.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void OnlyTheChildDescribesAnAncestorAndItIsTheClustersOwnCaseObject() {
        // ⚠ Reached through a type PARAMETER rather than as `ManagedClusterCase.Ancestors`. A
        // `static virtual` interface member is only accessible through a constrained generic, which is
        // also what stops a provider from "implementing" it as an ordinary static the harness would
        // never call.
        AncestorsOf<ManagedClusterCase>().ShouldBeEmpty();

        var ancestors = AncestorsOf<AgentPoolCase>();

        ancestors.Length.ShouldBe(1);

        ancestors[0].ShouldBeSameAs(
            ManagedClusterCase.ProviderCase,
            "the pool's ancestor is a SECOND DESCRIPTION of the cluster rather than the cluster's own "
            + "case object, so the two can disagree the first time either changes."
        );

        ancestors[0].Type.ShouldBe(ManagedClusters.Type);
    }

    [Fact]
    public void BothCasesOwnThreeObjectsAndNoChildBeforeThisOneMatchedItsParentsCount() {
        // ⚠ THE MEASUREMENT THIS FAMILY ADDS, PINNED SO IT CANNOT QUIETLY STOP BEING TRUE. The only
        // other shipping child renders ONE object against its parent's one; the claim in this file's
        // remarks is that a child is not structurally smaller than its parent, and it is a claim about
        // two numbers.
        // ⚠ TWO ADDRESSES RATHER THAN ONE `with`, AND THE COMPILER IS NOT WHAT ENFORCES THAT.
        // `ResourceId` validates `ParentNames.Count == Type.Depth - 1` in the SETTER of each, so a
        // `with { Type = …, ParentNames = … }` throws on whichever of the two is applied first.
        var pool = new ResourceId(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "prod",
            AgentPools.Type,
            "workers",
            Guid.NewGuid(),
            "prod-cluster"
        );

        var cluster = new ResourceId(
            pool.TenantId,
            pool.SubscriptionId,
            "prod",
            ManagedClusters.Type,
            "prod-cluster",
            Guid.NewGuid()
        );

        AgentPoolCase.ProviderCase.Objects(pool, "ns").Length.ShouldBe(3);
        ManagedClusterCase.ProviderCase.Objects(cluster, "ns").Length.ShouldBe(3);
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
