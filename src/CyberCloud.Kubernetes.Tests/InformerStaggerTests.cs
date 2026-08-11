using CyberCloud.Kubernetes.Informers;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The stagger docs/plan/09 § Observing says "matters more than it sounds".
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         The assertion the task set is "the stagger actually spreads re-establishment rather than
///         being a constant", and that is what the distribution tests below check.
///     </b>
///     It goes wrong
///     in a way that still looks implemented: a function that returns a fixed
///     delay, or one seeded by something that does not vary per cluster, produces code that reads as
///     staggered and behaves as a stampede with a pause in front of it.
/// </remarks>
public sealed class InformerStaggerTests {
    static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Fact]
    public void ItIsNotAConstant() {
        // The headline. 30 silos' worth of clusters must not all wake up together.
        var delays = Enumerable.Range(0, 30)
            .Select(i => InformerStagger.DelayFor(Cluster(i), Window))
            .ToList();

        delays.Distinct()
            .Count()
            .ShouldBeGreaterThan(
                25,
                "30 clusters produced fewer than 26 distinct delays — that is not a spread."
            );
    }

    [Fact]
    public void EveryDelayIsInsideTheWindow() {
        for (var i = 0; i < 2000; i++) {
            var delay = InformerStagger.DelayFor(Cluster(i), Window);

            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
            delay.ShouldBeLessThan(Window, "the interval is half-open: [0, window).");
        }
    }

    [Fact]
    public void TheSpreadIsBroadlyUniformAcrossTheWindow() {
        // ⚠ Not decoration. A hash that clustered — every delay in the first two seconds, say —
        // would pass "it is not a constant" and still be a stampede. Ten buckets over 2000 clusters
        // expects 200 each; the bounds are wide enough not to be flaky and tight enough to catch a
        // function that is not spreading.
        var buckets = new int[10];

        for (var i = 0; i < 2000; i++) {
            var fraction = InformerStagger.Fraction(Cluster(i));
            fraction.ShouldBeGreaterThanOrEqualTo(0.0);
            fraction.ShouldBeLessThan(1.0);

            buckets[Math.Min(9, (int)(fraction * 10))]++;
        }

        foreach (var count in buckets) {
            count.ShouldBeInRange(
                120,
                290,
                $"the deciles are [{string.Join(", ", buckets)}] over 2000 clusters; a uniform "
                + "spread expects ~200 in each."
            );
        }
    }

    [Fact]
    public void ItIsDeterministicAcrossCallsAndProcesses() {
        // ⚠ Why this matters beyond tidiness: string.GetHashCode is randomised per process, so a
        // hash-code-based implementation would give two silos different answers for the same
        // cluster and would move a cluster's slot on every restart. The SHA-256 values below are
        // hard-coded, so a change of hash function is a failing test rather than a silent
        // reschedule of every cluster in the fleet.
        var id = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d");

        var first = InformerStagger.DelayFor(id, Window);
        var second = InformerStagger.DelayFor(id, Window);

        first.ShouldBe(second);
        InformerStagger.Fraction(id).ShouldBe(InformerStagger.Fraction(id));
    }

    [Fact]
    public void DifferentClustersGetDifferentSlotsEvenWhenTheirIdsAreAdjacent() {
        // Cluster ids are GUIDs, but nothing stops them being sequential in a test fixture or a
        // seeded environment. A weak mixing function would map adjacent ids to adjacent slots and
        // re-create the stampede for exactly the clusters most likely to be created together.
        var a = InformerStagger.Fraction(Cluster(1000));
        var b = InformerStagger.Fraction(Cluster(1001));

        Math.Abs(a - b)
            .ShouldBeGreaterThan(
                0.01,
                "adjacent cluster ids landed in nearly the same slot; the mixing is too weak."
            );
    }

    [Fact]
    public void AZeroWindowDisablesStaggering() {
        // The escape hatch a single-silo integration test needs, and the configuration production
        // must never have — KubernetesOptions.InformerStaggerWindow says so.
        InformerStagger.DelayFor(Cluster(7), TimeSpan.Zero).ShouldBe(TimeSpan.Zero);
        InformerStagger.DelayFor(Cluster(7), TimeSpan.FromSeconds(-1)).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void TheDefaultWindowIsThirtySeconds() {
        InformerStagger.DefaultWindow.ShouldBe(TimeSpan.FromSeconds(30));
    }

    static Guid Cluster(int index) {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BitConverter.TryWriteBytes(bytes, index);
        bytes[15] = 0xC1;
        return new(bytes);
    }
}
