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

/// <summary>
///     The same two suites against the <b>child</b> type, <c>CyberCloud.Storage/accounts/buckets</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two more class declarations in the SAME project, not a project of its own.</b> A
///         second type in a namespace needs no second <c>*.Cluster.Conformance</c>: the fixture is
///         generic over the case source, so the only thing a child costs here is the two lines below.
///     </para>
///     <para>
///         ⚠ <b>What this half does against a real API server that the Docker-free half cannot:
///         create the ACCOUNT first.</b> <c>ClusterConformanceHarness.CreateAncestorsAsync</c> drives
///         the ancestor's own case through the real write path before the child's address is usable,
///         so a bucket here is applied into a namespace where a <c>Seaweed</c> genuinely exists —
///         and the parent-existence 404 is an assertion about a name that was really claimed rather
///         than about a fixture. ⚠ It still does not run a SeaweedFS operator: the CRD stub the
///         harness derives from <c>ProviderConformanceCase.Objects</c> makes the REST path exist and
///         nothing more, so no bucket here has ever held an object.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class StorageBucketLifecycleConformance(ClusterConformanceFixture<StorageBucketCase> fixture)
    : ClusterConformanceTests<StorageBucketCase>(fixture),
        IClassFixture<ClusterConformanceFixture<StorageBucketCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the bucket child type.</summary>
public sealed class StorageBucketSiloKillConformance : SiloKillConformanceTests<StorageBucketCase>;
