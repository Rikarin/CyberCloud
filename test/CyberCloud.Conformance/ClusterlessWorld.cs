using CyberCloud.ResourceManager.Conformance;

namespace CyberCloud.Conformance;

/// <summary>
///     The world of a <b>clusterless</b> type as the suite reads it — one shape over the two
///     registrations a clusterless case can make, so that every world-facing assertion in
///     <c>ProviderConformanceTests</c> has one clusterless branch rather than two.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             TWO CLUSTERLESS SHAPES EXIST BECAUSE TWO BRANCHES MERGED ON THE SAME DAY, EACH HAVING
///             TAUGHT THE SUITE ABOUT "THE FIRST CLUSTERLESS TYPE".
///         </b> #33's <c>CyberCloud.Communication/services</c> converges onto a <i>module</i> in the
///         silo and registers an <see cref="IConvergedModule" /> on its case source: is it there,
///         does it match, remove it, corrupt it, host it. #29's
///         <c>CyberCloud.ContainerRegistry/feeds</c> converges onto a <i>platform host's</i> catalogue
///         grain and registers a <c>DataPlane</c> on its case: break it, ask whether it matches —
///         plus a <c>StoragePrefix</c> for the bytes the host keeps on the platform's object store.
///         Neither registration was wrong, and neither was changed; this type is where the suite
///         stops caring which one it was handed.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What the two shapes agree on is read the same way, and what they differ on is data
///             on this type rather than a second branch in every test.
///         </b>
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="HoldsAsync" /> and <see cref="MatchesAsync" /> are the module's own two
///             questions; a data plane answers one question, so for it both are
///             <c>MatchesDesiredAsync</c> — which is exactly what #29 asserted after a converge
///             (matches) and after a teardown (does not match).
///         </item>
///         <item>
///             <see cref="BreakAsync" /> is the module's <c>RemoveAsync</c> and the data plane's
///             <c>BreakAsync</c> — the <c>kubectl delete</c> of either world.
///         </item>
///         <item>
///             <see cref="CorruptAsync" /> is the hand edit, and only a module has one: a
///             <c>ConformanceWorld</c> describes one way to break and no way to bend. A world without
///             it makes the hand-edit assertion skip loudly, as #29's branch did.
///         </item>
///         <item>
///             <see cref="RebuildsFromTheBody" /> is the one <i>semantic</i> difference, and the
///             drift assertion reads it. A module's grains are a function of the desired body, so a
///             pass after a break must PUT THEM BACK — #33's "corrected" — and the suite asserts the
///             pass did not fail, holds again and matches again. A feed's catalogue was the tenant's
///             and the body never carried it, so a pass after a close can only NOTICE — #29's
///             "noticed rather than corrected" — and what the suite asserts is clause 4: a pass that
///             reports <c>Converged</c> over a world that does not match is an assumption.
///         </item>
///         <item>
///             <see cref="StoragePrefix" /> is where the type keeps bytes on the platform's object
///             store, or <see langword="null" /> when it keeps none. The teardown plants an object
///             under it and asserts the teardown removed it. A data plane must name one — #29's "both
///             halves of the same fact" — and a module names none today; one that did would be
///             planted and asserted the same way.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Neither factory decides anything.</b> Every member returns a reading, and every
///         verdict on a reading is in <c>ProviderConformanceTests</c> — the line
///         <c>ProviderConformanceCase</c>'s remarks draw between data and a hook.
///     </para>
/// </remarks>
public sealed record ClusterlessWorld {
    /// <summary>Whether the world holds anything for the resource at all.</summary>
    public required Func<CancellationToken, Task<bool>> HoldsAsync { get; init; }

    /// <summary>Whether what the world holds for the resource is what a desired body asks for.</summary>
    public required Func<string, CancellationToken, Task<bool>> MatchesAsync { get; init; }

    /// <summary>Removes what the world holds for the resource, behind the reconciler's back.</summary>
    public required Func<string, CancellationToken, Task> BreakAsync { get; init; }

    /// <summary>
    ///     Changes what the world holds so that it no longer matches the body, without removing it —
    ///     or <see langword="null" /> for a world that has no such form.
    /// </summary>
    public required Func<string, CancellationToken, Task>? CorruptAsync { get; init; }

    /// <summary>
    ///     Whether a pass after <see cref="BreakAsync" /> can put the world back from the desired
    ///     body — see the remarks.
    /// </summary>
    public required bool RebuildsFromTheBody { get; init; }

    /// <summary>
    ///     The object-store prefix one resource's bytes live under, or <see langword="null" /> for a
    ///     type that keeps none there.
    /// </summary>
    public required string? StoragePrefix { get; init; }

    /// <summary>The world an <see cref="IConvergedModule" /> is, for one resource.</summary>
    /// <param name="module">The module the case source registered.</param>
    /// <param name="address">The resource, with its GUID resolved.</param>
    /// <param name="storagePrefix">The prefix the case names, or <see langword="null" />.</param>
    public static ClusterlessWorld Over(IConvergedModule module, ResourceId address, string? storagePrefix) =>
        new() {
            HoldsAsync = ct => module.HoldsAsync(address, ct),
            MatchesAsync = (desiredJson, ct) => module.MatchesAsync(address, desiredJson, ct),
            BreakAsync = (desiredJson, ct) => module.RemoveAsync(address, desiredJson, ct),
            CorruptAsync = (desiredJson, ct) => module.CorruptAsync(address, desiredJson, ct),
            RebuildsFromTheBody = true,
            StoragePrefix = storagePrefix
        };

    /// <summary>The world a case's <c>DataPlane</c> is, for one resource.</summary>
    /// <param name="dataPlane">What the case built over the harness's grain factory and the address.</param>
    /// <param name="storagePrefix">The prefix the case names.</param>
    public static ClusterlessWorld Over(ConformanceWorld dataPlane, string storagePrefix) =>
        new() {
            HoldsAsync = _ => dataPlane.MatchesDesiredAsync(),
            MatchesAsync = (_, _) => dataPlane.MatchesDesiredAsync(),
            BreakAsync = (_, _) => dataPlane.BreakAsync(),
            CorruptAsync = null,
            RebuildsFromTheBody = false,
            StoragePrefix = storagePrefix
        };

    /// <summary>
    ///     This world as the four-clause check takes it: broken by <see cref="BreakAsync" />, read
    ///     by <see cref="MatchesAsync" /> against one body.
    /// </summary>
    /// <param name="desiredJson">The body the resource was converged from.</param>
    /// <param name="cancellationToken">Cancels the readings.</param>
    public ConformanceWorld ForClauseFour(string desiredJson, CancellationToken cancellationToken) =>
        new(
            () => BreakAsync(desiredJson, cancellationToken),
            () => MatchesAsync(desiredJson, cancellationToken)
        );
}
