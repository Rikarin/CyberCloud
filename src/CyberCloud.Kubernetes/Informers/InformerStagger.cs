using System.Security.Cryptography;

namespace CyberCloud.Kubernetes.Informers;

/// <summary>
///     How long a cluster's informers wait before re-listing after an activation — the mitigation
///     docs/plan/09 § Observing says "matters more than it sounds".
/// </summary>
/// <remarks>
///     <para>
///         The failure this exists to prevent, quoting docs/plan/09 § Observing:
///         <i>
///             "The informer
///             cache is in one silo's memory and is lost when that silo dies. Re-establishing it is a
///             full list + watch, which for a large cluster is seconds and a burst of API load …
///             stagger re-establishment across clusters so a silo restart does not stampede every
///             tenant's API server at once. … a 30-silo rolling deploy without staggering is a
///             synchronized list storm."
///         </i>
///     </para>
///     <para>
///         <b>Why the delay is a function of the cluster id and nothing else.</b> There is exactly
///         one activation of <c>IClusterConnectionGrain</c> per cluster platform-wide (docs/plan/06
///         § Grain keys), so "which silo" is not a variable a cluster's delay may depend on — if it
///         were, the same cluster would get a different slot on every rehome, and the point is to
///         spread <i>clusters</i> across the window, not to randomise one. A pure function of the
///         cluster id also makes the schedule reproducible, which is what lets an operator answer
///         "when will cluster X re-list" during an incident instead of watching for it.
///     </para>
///     <para>
///         ⚠ <b>Not <see cref="Random" />, and not <see cref="object.GetHashCode" />.</b> A random
///         delay is unreproducible and, worse, untestable — the assertion "this actually spreads"
///         becomes flaky. <see cref="string.GetHashCode()" /> is randomised per process by design,
///         so two silos would disagree about a cluster's slot and the same cluster would move on
///         every restart. SHA-256 over the GUID's bytes is stable across processes, machines and
///         releases, which is the property that matters.
///     </para>
/// </remarks>
public static class InformerStagger {
    /// <summary>
    ///     The default window re-establishment is spread over — 30 seconds.
    /// </summary>
    /// <remarks>
    ///     Sized against docs/plan/09 § Observing's own worked example: a 30-silo rolling deploy. A
    ///     window materially shorter than the deploy's own pace does not spread anything; one much
    ///     longer leaves observed state stale for no benefit, and docs/plan/09 § Cluster connections
    ///     puts the health staleness bound at 90 seconds, so a stagger approaching that would start
    ///     to interact with it.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The delay before <paramref name="clusterId" />'s informers re-list, spread deterministically
    ///     over <paramref name="window" />.
    /// </summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="window">
    ///     The spread. <see cref="TimeSpan.Zero" /> or negative disables staggering and returns
    ///     <see cref="TimeSpan.Zero" /> — which is what a single-silo test wants and what production
    ///     must never be configured with.
    /// </param>
    /// <returns>A delay in <c>[0, window)</c>.</returns>
    public static TimeSpan DelayFor(Guid clusterId, TimeSpan window) {
        if (window <= TimeSpan.Zero) {
            return TimeSpan.Zero;
        }

        // The fraction is taken from 53 bits so that the double conversion below is exact, and the
        // half-open interval [0, window) is preserved: Fraction() < 1 strictly, so the result can
        // never equal the window and two adjacent windows cannot collide on their boundary.
        return TimeSpan.FromTicks((long)(window.Ticks * Fraction(clusterId)));
    }

    /// <summary><see cref="DelayFor(Guid, TimeSpan)" /> over <see cref="DefaultWindow" />.</summary>
    /// <param name="clusterId">The cluster.</param>
    public static TimeSpan DelayFor(Guid clusterId) => DelayFor(clusterId, DefaultWindow);

    /// <summary>
    ///     The cluster's position in the window, in <c>[0, 1)</c>. Exposed because it is the thing
    ///     worth asserting a distribution over.
    /// </summary>
    /// <param name="clusterId">The cluster.</param>
    public static double Fraction(Guid clusterId) {
        Span<byte> id = stackalloc byte[16];
        clusterId.TryWriteBytes(id);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(id, hash);

        // 53 bits — the exact integer range of a double, so the division below loses nothing.
        var bits = BitConverter.ToUInt64(hash[..8]) >> 11;
        return bits / (double)(1UL << 53);
    }
}
