using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Analytics.Tests;

/// <summary>
///     What a ClickHouse cluster reserves.
/// </summary>
/// <remarks>
///     ⚠ <b>A meter is the one declaration whose bug is invisible in every functional test.</b> A
///     resource that reserves a third of what it costs provisions perfectly, reads back perfectly and
///     converges — and the subscription is billed for a third of it.
///     <para>
///         ⚠ <b>This type's meters are a PRODUCT and a SUM at once, which is a fourth shape.</b>
///         <c>CyberCloud.DBforPostgreSQL/servers</c> established that an amount is a quantity string
///         rather than a number; <c>CyberCloud.Messaging/natsClusters</c> added that it is a
///         <i>product</i> of a replica count and one per-replica figure;
///         <c>CyberCloud.Storage/accounts</c> added that it is a <i>sum</i> over heterogeneous
///         components. Here it is <c>shards × replicas × preset + keeperNodes × 250m</c> — and the
///         product has <b>two factors the tenant sets separately</b>, which is the part a derivation
///         copied from any of the three gets wrong while staying plausible.
///     </para>
/// </remarks>
public sealed class ClickHouseQuotaTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    [Fact]
    public void ShardsAndReplicasBothMoveTheAmounts() {
        // ⚠ THE TEST THAT FAILS ON A DERIVATION COPIED FROM natsClusters, WHICH IS THE MOST LIKELY
        // SOURCE TO COPY FROM: that one is `servers × figure` and reads ONE count. Here the operator
        // creates one StatefulSet per (shard, replica) pair, so a meter that multiplied by `replicas`
        // alone would be exactly right on the default body — one shard — and would reserve a third of
        // a three-shard cluster.
        var one = Amounts(ClickHouseClusters.Body(ClusterId, shards: 1, replicas: 2));
        var three = Amounts(ClickHouseClusters.Body(ClusterId, shards: 3, replicas: 2));

        // 2 servers × m1.small (500m) = 1, plus 3 keepers × 250m = 0.75.
        one[QuotaMeter.Vcpu].ShouldBe(1.75m);

        // 6 servers × 500m = 3, plus 0.75.
        three[QuotaMeter.Vcpu].ShouldBe(3.75m);

        // ⚠ And the same body with the factors SWAPPED draws the same amounts, which is what says the
        // derivation multiplies rather than picking one.
        Amounts(ClickHouseClusters.Body(ClusterId, shards: 2, replicas: 3))[QuotaMeter.Vcpu]
            .ShouldBe(three[QuotaMeter.Vcpu]);
    }

    [Fact]
    public void ChangingOnlyTheKeeperCountStillMovesTheAmounts() {
        // ⚠ THE OTHER HALF, AND THE ONE A DERIVATION COPIED FROM A PRODUCT-ONLY PROVIDER MISSES.
        // `keeperNodes` reaches no ClickHouse server, so a meter that read only the server population
        // would return the same number for a one-node quorum and a five-node one — three pods and
        // three volumes nobody is charged for.
        var one = Amounts(ClickHouseClusters.Body(ClusterId, keeperNodes: 1));
        var five = Amounts(ClickHouseClusters.Body(ClusterId, keeperNodes: 5));

        five[QuotaMeter.Vcpu].ShouldBe(one[QuotaMeter.Vcpu] + 1m, "four extra Keepers at 250m each");
        five[QuotaMeter.MemoryGb].ShouldBe(one[QuotaMeter.MemoryGb] + 2m, "four extra Keepers at 512Mi");
        five[QuotaMeter.StorageGb].ShouldBe(one[QuotaMeter.StorageGb] + 40m, "four extra 10Gi volumes");
    }

    [Fact]
    public void StorageIsTheServerProductPlusEveryKeepersOwnVolume() {
        var small = Amounts(ClickHouseClusters.Body(ClusterId, shards: 1, replicas: 2, storageSize: "100Gi"));
        var big = Amounts(ClickHouseClusters.Body(ClusterId, shards: 3, replicas: 2, storageSize: "100Gi"));

        small[QuotaMeter.StorageGb].ShouldBe(230m, "2 × 100Gi + 3 Keepers × 10Gi");
        big[QuotaMeter.StorageGb].ShouldBe(630m, "6 × 100Gi + 3 Keepers × 10Gi");
    }

    [Fact]
    public void StorageIsMultipliedByReplicasAndThatIsTheOppositeOfTheObjectStoreDecision() {
        // ⚠ TWO SERVICES, TWO OPPOSITE-LOOKING DECISIONS, ONE RULE — asserted so that neither can be
        // "made consistent" with the other by somebody reading only one.
        // StorageQuotaTests.StorageIsNotMultipliedByTheReplicationFactor refuses to multiply, because
        // a SeaweedFS replication code decides how copies are spread ACROSS the same PVCs. A
        // ClickHouse replica is a whole second copy of the shard on ITS OWN volume, provisioned by its
        // own StatefulSet. The rule both obey: charge for the disks the cluster provisions, once each.
        var one = Amounts(ClickHouseClusters.Body(ClusterId, shards: 1, replicas: 1, storageSize: "100Gi"));
        var two = Amounts(ClickHouseClusters.Body(ClusterId, shards: 1, replicas: 2, storageSize: "100Gi"));

        (two[QuotaMeter.StorageGb] - one[QuotaMeter.StorageGb]).ShouldBe(
            100m,
            "the second replica's own 100Gi volume was not counted"
        );
    }

    [Fact]
    public void ThePresetIsResolvedEvenThoughTheOrdinaryBodyDoesNotSpellTheQuantities() {
        // ⚠ THE FINDING PostgresProvider RECORDS, RE-CHECKED HERE RATHER THAN INHERITED. The body
        // ClickHouseClusters.Body builds — the one every test, fixture and conformance case uses —
        // writes no `sizing` block at all, so a quantity read at /properties/sizing/cpu resolves to
        // NOTHING. The amount comes from the preset, which is what a pointer cannot reach.
        ClickHouseClusters.Body(ClusterId).ShouldNotContain("sizing");

        Amounts(ClickHouseClusters.Body(ClusterId))[QuotaMeter.Vcpu].ShouldBe(1.75m);
    }

    [Fact]
    public void AnExplicitOverrideBeatsThePresetAndIsCountedPerServer() {
        var amounts = Amounts(
            WithSizing(ClickHouseClusters.Body(ClusterId, shards: 2, replicas: 2), "1", "4Gi")
        );

        amounts[QuotaMeter.Vcpu].ShouldBe(4.75m, "1 core × 4 servers is 4, plus 3 Keepers × 250m");
        amounts[QuotaMeter.MemoryGb].ShouldBe(17.5m, "4Gi × 4 is 16, plus 3 × 512Mi is 1.5");
    }

    [Fact]
    public void TheSameBodyDerivesTheSameAmountsTwice() {
        // ⚠ THE PURITY CLAIM, AS DIRECTLY AS IT CAN BE ASSERTED FROM HERE. The delete path re-derives
        // committed amounts from the STORED body through this same function, so a derivation reading
        // a clock, configuration or a static that changes would return a different number on the
        // delete than the create committed — quota drifting upward on every cycle.
        var body = ClickHouseClusters.Body(ClusterId, shards: 3, replicas: 3, storageSize: "250Gi");

        Amounts(body).ShouldBe(Amounts(body));
    }

    [Fact]
    public void AnUnresolvableQuantityRefusesRatherThanReservingZero() {
        // ⚠ A METER THAT CANNOT SAY HOW MUCH REFUSES THE WRITE. Reserving zero would provision the
        // resource and charge for none of it, and zero PASSES every limit there is. This is
        // unreachable from a validated body — AllowedValues closes the preset and the Pattern closes
        // the quantity — which is why it is asserted against a body that never reaches the schema.
        var registry = ProviderRegistry.Build([new AnalyticsProvider()]);
        registry.TryGetType(ClickHouseClusters.Type, out var registration).ShouldBeTrue();

        using var body = JsonDocument.Parse(
            WithSizing(ClickHouseClusters.Body(ClusterId), "not-a-quantity", "4Gi")
        );

        var vcpu = registration.Meters.Single(x => x.Meter == QuotaMeter.Vcpu).Derivation!;

        vcpu.Amount(body.RootElement).IsFailure.ShouldBeTrue(
            "a body whose cpu quantity does not parse reserved an amount instead of refusing."
        );
    }

    [Fact]
    public void NoMeterEverDerivesZero() {
        // ⚠ THE CONSTRAINT NOTHING ELSE IN THE PLATFORM STATES. QuotaGrain.TryReserveAsync refuses a
        // non-positive amount — "A reservation must be positive; 0 is not" — so a meter that CAN be
        // zero refuses the create rather than skipping itself. Every meter here has a floor: the
        // Keeper term is at least one pod with its own volume, and shards × replicas is at least one.
        foreach (var shards in new[] { 1, 10 }) {
            foreach (var replicas in new[] { 1, 5 }) {
                foreach (var keeperNodes in new[] { 1, 5 }) {
                    foreach (var size in new[] { "1", "1Ti" }) {
                        foreach (var amount in Amounts(
                                     ClickHouseClusters.Body(
                                         ClusterId,
                                         shards: shards,
                                         replicas: replicas,
                                         storageSize: size,
                                         keeperNodes: keeperNodes
                                     )
                                 ).Values) {
                            amount.ShouldBeGreaterThan(
                                0m,
                                $"shards={shards}, replicas={replicas}, keeperNodes={keeperNodes}, "
                                + $"storage={size} derives a zero amount, which "
                                + "QuotaGrain.TryReserveAsync refuses outright — so this body could "
                                + "never be created at all."
                            );
                        }
                    }
                }
            }
        }
    }

    static Dictionary<QuotaMeter, decimal> Amounts(string bodyJson) {
        var registry = ProviderRegistry.Build([new AnalyticsProvider()]);
        registry.TryGetType(ClickHouseClusters.Type, out var registration).ShouldBeTrue();

        using var body = JsonDocument.Parse(bodyJson);
        var found = new Dictionary<QuotaMeter, decimal>();

        foreach (var meter in registration.Meters.Where(x => x.Derivation is not null)) {
            var amount = meter.Derivation!.Amount(body.RootElement);
            amount.IsSuccess.ShouldBeTrue(meter.Meter.ToString());
            found[meter.Meter] = amount.GetValueOrThrow();
        }

        return found;
    }

    static string WithSizing(string body, string cpu, string memory) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["sizing"] = new JsonObject {
            ["cpu"] = cpu, ["memory"] = memory
        };

        return node.ToJsonString();
    }
}
