using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Search.Conformance;

namespace CyberCloud.Providers.Search.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed search provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations over the case
///         <c>CyberCloud.Providers.Search.Conformance</c> already declares.</b> One provider, one
///         <c>ProviderConformanceCase</c>: a second copy here would be a second description of the
///         same provider, and the two would disagree the first time either changed.
///     </para>
///     <para>
///         ⚠ <b>One CRD stub, and it is derived rather than declared.</b>
///         <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c> reads group, version,
///         kind and plural off the case's own <c>Objects</c> and waits for <c>Established</c>. The
///         stub is a definition with an open schema, which is exactly what this suite needs and
///         exactly what it must not be confused with: it makes the <i>REST path</i> exist, it does not
///         run an OpenSearch operator, so nothing here observes a running search cluster.
///     </para>
///     <para>
///         ⚠ <b>AND THE OPEN SCHEMA IS WHY THIS SUITE CANNOT SEE THIS PROVIDER'S OWN LARGEST
///         HAZARD.</b> <c>OpenSearchServices.Matches</c> is a containment check because the real CRD
///         carries <c>+kubebuilder:default=true</c> and <c>+kubebuilder:validation:Required</c> on
///         <c>spec.confMgmt.smartScaler</c>, so a real API server writes a field back that this
///         provider never sent. A derived stub has no defaults at all, so a read-back here returns
///         exactly what was applied and an <i>equality</i> comparison would pass — the failure only
///         appears against a cluster with the operator's own definition installed.
///         <c>OpenSearchMatchesTests</c> is where that is asserted directly, against a document
///         carrying the fields the real CRD would add.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class OpenSearchServiceLifecycleConformance(ClusterConformanceFixture<OpenSearchCase> fixture)
    : ClusterConformanceTests<OpenSearchCase>(fixture),
        IClassFixture<ClusterConformanceFixture<OpenSearchCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed search provider.</summary>
public sealed class OpenSearchServiceSiloKillConformance : SiloKillConformanceTests<OpenSearchCase>;
