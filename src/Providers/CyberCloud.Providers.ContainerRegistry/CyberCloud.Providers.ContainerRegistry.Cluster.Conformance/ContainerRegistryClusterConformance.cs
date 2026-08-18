using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.ContainerRegistry.Conformance;

namespace CyberCloud.Providers.ContainerRegistry.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed container-registry provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations over the case
///         <c>CyberCloud.Providers.ContainerRegistry.Conformance</c> already declares.</b> One
///         provider, one <c>ProviderConformanceCase</c>: a second copy here would be a second
///         description of the same provider, and the two would disagree the first time either changed.
///     </para>
///     <para>
///         ⚠ <b>ONE CRD STUB, AND FOURTEEN OF THE FIFTEEN OBJECTS NEED NONE.</b>
///         <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c> derives a definition
///         per custom kind from the case's own <c>Objects</c>; here the only custom kind is
///         <c>monitoring.coreos.com/v1 PodMonitor</c>, because <c>Secret</c>, <c>ConfigMap</c>,
///         <c>Service</c>, <c>Deployment</c> and <c>StatefulSet</c> are all served by a bare k3s
///         without being told. <c>charts/managed/kafka</c> needed two stubs and nothing else;
///         <c>charts/managed/nats</c> needed one of five; <c>charts/managed/cloud-shell</c> needs
///         <b>none</b>, and records that its green is therefore no evidence the derivation works.
///         ⚠ <b>This suite is the first that needs both</b> — fourteen built-in kinds checked by a
///         real, schema-validating API server, and one derived stub exercised in the same run.
///     </para>
///     <para>
///         ⚠ <b>WHAT THIS SUITE PROVES AND WHAT IT EMPHATICALLY DOES NOT.</b> Because fourteen of the
///         objects are built-in kinds, this is the first family whose cluster-backed suite exercises a
///         <b>real, schema-validating</b> API server for almost everything it applies — a
///         <c>Deployment</c> with a selector that does not match its pod template is rejected here, and
///         a <c>Service</c> whose <c>targetPort</c> names no container port is not. That is strictly
///         more than the open-schema CRD stub gives every other family.
///         <para>
///             ⚠ It still proves nothing about Harbor. The k3s has no images pulled, no
///             <c>PersistentVolume</c> provisioner worth the name, and nothing that would notice that
///             an environment variable Harbor's core requires is missing. Every assertion that needs a
///             running Harbor — <c>docker login</c>, an anonymous pull being refused, the admin
///             password the platform minted being the one core accepts — is
///             <c>charts/managed/harbor/conformance.yaml § owed</c> rather than this project's.
///         </para>
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ContainerRegistryLifecycleConformance(
    ClusterConformanceFixture<ContainerRegistryCase> fixture
)
    : ClusterConformanceTests<ContainerRegistryCase>(fixture),
        IClassFixture<ClusterConformanceFixture<ContainerRegistryCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed container-registry provider.</summary>
public sealed class ContainerRegistrySiloKillConformance : SiloKillConformanceTests<ContainerRegistryCase>;
