using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Storage.Conformance;

namespace CyberCloud.Providers.Storage.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed object-storage provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations over the case
///         <c>CyberCloud.Providers.Storage.Conformance</c> already declares.</b> One provider, one
///         <c>ProviderConformanceCase</c>: a second copy here would be a second description of the
///         same provider, and the two would disagree the first time either changed.
///     </para>
///     <para>
///         ⚠ <b>One CRD stub, and it is derived rather than declared.</b>
///         <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c> reads group, version,
///         kind and plural off the case's own <c>Objects</c> and waits for <c>Established</c>. The
///         stub is a definition with an open schema, which is exactly what this suite needs and
///         exactly what it must not be confused with: it makes the <i>REST path</i> exist, it does not
///         run a SeaweedFS operator, so nothing here observes a running object store. What the suite
///         asserts is that the platform applied the object it said it applied — the operator's half is
///         <c>charts/managed/seaweedfs/conformance.yaml</c>'s assertions, which have no runner yet.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class StorageAccountLifecycleConformance(ClusterConformanceFixture<StorageCase> fixture)
    : ClusterConformanceTests<StorageCase>(fixture), IClassFixture<ClusterConformanceFixture<StorageCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed object-storage provider.</summary>
public sealed class StorageAccountSiloKillConformance : SiloKillConformanceTests<StorageCase>;
