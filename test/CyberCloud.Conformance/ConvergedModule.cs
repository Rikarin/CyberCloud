using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Conformance;

/// <summary>
///     The world a <b>clusterless</b> type converges onto — a module's grains rather than a cluster's
///     objects — and how the suite hosts it, reads it, and breaks it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> <c>ProviderConformanceCase.Objects</c> has said since the sample
///         provider that it is <i>"empty for a clusterless provider, and the cluster-facing half of
///         the suite skips itself for one"</i>, and for fourteen families that sentence was never
///         exercised: every type declared <c>RequiresCluster</c>. <c>CyberCloud.Communication/services</c>
///         is the first that does not — docs/plan/08 § What the resource manager deliberately does not
///         do's <i>"a provider with no cluster at all"</i> — and it turned out the sentence was half
///         true. The suite <i>could</i> skip; it could not <b>assert</b>, because everything it knew
///         how to read was <c>FakeKubeCluster</c>. A run over such a family that skipped every
///         world-facing assertion would report green over a reconciler that wrote nothing anywhere.
///     </para>
///     <para>
///         So a clusterless case hands the suite this instead of <c>Objects</c>: the same five
///         questions the fake cluster answers — is it there, does it match, remove it, corrupt it,
///         and how do I host it — asked of the module. The suite's assertions stay the suite's; a
///         module can no more grade its own homework than a case can, because every method here
///         returns a reading and the <i>verdict</i> on it is in <c>ProviderConformanceTests</c>.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Two of the five are read around the reconciler, and that is clause 4's ground
///             truth here exactly as <c>ObjectMatchesDesired</c> is for a cluster.
///         </b> <see cref="HoldsAsync" /> and <see cref="MatchesAsync" /> must reach the module
///         through its <i>own</i> seam over the harness's grain factory — never through the
///         reconciler's <c>ObserveAsync</c>, which is exactly as unreliable as the reconciler it
///         belongs to. <see cref="RemoveAsync" /> and <see cref="CorruptAsync" /> are the
///         <c>kubectl delete</c> and the hand edit, and they go behind the reconciler's back the same
///         way <c>FakeKubeCluster.MutateBehindTheirBack</c> does.
///     </para>
///     <para>
///         ⚠ <b>What a clusterless run cannot say, said here so nobody reads a green run as saying
///         it.</b> An admission refusal and a cluster that did not answer have no module analogue the
///         harness can inject, so the two assertions about them assert the inverse for a clusterless
///         type: the operation converges as if the refusing cluster were healthy, and nothing reached
///         it. The seven labels are asserted absent rather than present — a clusterless type that
///         applied an object would be lying about being clusterless. And the cluster-backed half in
///         <c>test/CyberCloud.Cluster.Conformance</c> refuses a case with no objects by name, so the
///         silo-kill criterion — docs/plan/24 § Phase 1's exit criterion 3 — has no clusterless
///         harness yet; that is owed, and <c>charts/bundle/bundle.yaml § owed</c> carries it.
///     </para>
///     <para>
///         ⚠ <b>This is one of two clusterless registrations, and the suite reads both through
///         <c>ClusterlessWorld</c>.</b> <c>ProviderConformanceCase.DataPlane</c> is the other — a
///         world on a platform host rather than in the silo, with one way to break and no hand edit.
///         A case registers exactly one; that type's remarks say why two exist.
///     </para>
/// </remarks>
public interface IConvergedModule {
    /// <summary>
    ///     Hosts the module in the harness silo, the way the silo host does. Called once, from the
    ///     silo configurator, before the resource manager is added.
    /// </summary>
    /// <param name="silo">The harness silo being built.</param>
    void ConfigureSilo(ISiloBuilder silo);

    /// <summary>
    ///     Registers what the type's synchronous action handlers hold, over the harness's grain
    ///     factory — the same seams the gateway registers in production.
    /// </summary>
    /// <param name="services">The handler container being built.</param>
    /// <param name="grains">The harness's cluster client.</param>
    void ConfigureHandlers(IServiceCollection services, IGrainFactory grains);

    /// <summary>
    ///     Called once the cluster is deployed, with the client-side grain factory every reading
    ///     below goes through — and the one a case's <c>CreateReconciler</c> may reach for, since a
    ///     reconciler that converges grains needs a way to them when the suite drives it directly.
    /// </summary>
    /// <param name="grains">The harness's cluster client.</param>
    void Attach(IGrainFactory grains);

    /// <summary>
    ///     Puts back whatever one test's resources left in the module under the harness's shared
    ///     ancestors, the way <c>FakeKubeCluster.Reset</c> empties the cluster between tests.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The suite never deletes what most of its tests create, and for a cluster that is
    ///     free.</b> Every test starts with a reset that empties the fake cluster, so a resource a
    ///     previous test left behind has no object for the next test to trip over. A module's grains
    ///     are not emptied by anything, and a type with a per-parent uniqueness rule — one
    ///     configuration per channel kind, owned by one resource — would refuse the second test's
    ///     resource because the first test's still holds the kind. This is where the module removes
    ///     what those leftovers hold. Synchronous, because every reset in the suite is; called before
    ///     <see cref="Attach" /> it must do nothing.
    /// </remarks>
    void Reset();

    /// <summary>Whether the module holds anything for the resource at all.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<bool> HoldsAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>Whether what the module holds for the resource is what a desired body asks for.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="desiredJson">The body it should carry, as JSON text.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<bool> MatchesAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken);

    /// <summary>Removes what the module holds for the resource, behind the reconciler's back.</summary>
    /// <param name="id">The resource.</param>
    /// <param name="desiredJson">The body it was converged from, so the module knows what to remove.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RemoveAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken);

    /// <summary>
    ///     Changes what the module holds for the resource so that it no longer matches the body,
    ///     without removing it — the hand edit.
    /// </summary>
    /// <param name="id">The resource.</param>
    /// <param name="desiredJson">The body it was converged from.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task CorruptAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken);
}
