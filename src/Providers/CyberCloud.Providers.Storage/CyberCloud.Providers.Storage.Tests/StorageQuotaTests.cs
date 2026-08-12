using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     What an object-storage account reserves.
/// </summary>
/// <remarks>
///     ⚠ <b>A meter is the one declaration whose bug is invisible in every functional test.</b> A
///     resource that reserves a third of what it costs provisions perfectly, reads back perfectly and
///     converges — and the subscription is billed for a third of it.
///     <para>
///         ⚠ <b>This type is the first whose meters are a sum over components that are not the same
///         size as each other.</b> <c>CyberCloud.DBforPostgreSQL/servers</c> established that an
///         amount is a quantity string rather than a number; <c>CyberCloud.Messaging/natsClusters</c>
///         added that it is a <i>product</i> of a replica count and one per-replica figure. A Seaweed
///         cluster is neither: the volume servers carry the tenant's preset and the masters, the filer
///         and the gateways carry a fixed platform share. The tests below are mostly about that seam,
///         because it is the one a reader of the three earlier providers would assume away.
///     </para>
/// </remarks>
public sealed class StorageQuotaTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void TheControlPlanePodsAreCountedAndNotOnlyTheVolumeServers() {
        // ⚠ THE ASSERTION NO EARLIER PROVIDER COULD MAKE, AND THE ONE THIS TYPE IS MOST LIKELY TO GET
        // WRONG BY COPYING ONE OF THEM. A `replicas × preset` derivation — the shape both providers
        // before this use — would be right about the volume servers and would miss six pods on the
        // default body: three masters, one filer and two gateways, each at 250m and 512Mi.
        var amounts = Amounts(StorageAccounts.Body(ClusterId, volumeServers: 3, masters: 3));

        // 3 × s1.small (1 core) = 3, plus (3 masters + 1 filer + 2 gateways) × 250m = 1.5.
        amounts[QuotaMeter.Vcpu].ShouldBe(4.5m);

        // 3 × 4Gi = 12, plus 6 × 512Mi = 3.
        amounts[QuotaMeter.MemoryGb].ShouldBe(15m);
    }

    [Fact]
    public void ChangingOnlyTheMasterCountStillMovesTheAmounts() {
        // ⚠ THE DIRECT TEST OF THE SAME THING, AND THE ONE THAT FAILS LOUDEST ON A COPIED DERIVATION.
        // `masters` reaches no volume server, so a meter that read only the volume population would
        // return the same number for a one-master account and a five-master one.
        var one = Amounts(StorageAccounts.Body(ClusterId, masters: 1));
        var five = Amounts(StorageAccounts.Body(ClusterId, masters: 5));

        five[QuotaMeter.Vcpu].ShouldBe(one[QuotaMeter.Vcpu] + 1m, "four extra masters at 250m each");
        five[QuotaMeter.MemoryGb].ShouldBe(one[QuotaMeter.MemoryGb] + 2m, "four extra masters at 512Mi");

        // And storage is unmoved by masters, because a master has no volume.
        five[QuotaMeter.StorageGb].ShouldBe(one[QuotaMeter.StorageGb]);
    }

    [Fact]
    public void StorageIsTheVolumeServersProductPlusTheFilersOwnVolume() {
        var three = Amounts(StorageAccounts.Body(ClusterId, volumeServers: 3, storageSize: "100Gi"));
        var six = Amounts(StorageAccounts.Body(ClusterId, volumeServers: 6, storageSize: "100Gi"));

        three[QuotaMeter.StorageGb].ShouldBe(310m, "3 × 100Gi + the filer's 10Gi");
        six[QuotaMeter.StorageGb].ShouldBe(610m);
    }

    [Fact]
    public void StorageIsNotMultipliedByTheReplicationFactor() {
        // ⚠ THE DECISION, ASSERTED SO IT CANNOT BE UNDONE BY SOMEBODY READING docs/plan/15 § Metering
        // AND APPLYING THE BLOCK ROW TO THIS ONE. That table bills storage.block.gb_month as
        // "Provisioned × replication factor"; object storage's row says "Sampled hourly from SeaweedFS
        // volume stats per bucket" and says nothing about a factor. The copies live ON the volume
        // servers' PVCs — a replication code decides how they are spread across the same disks, not
        // how many disks there are — so multiplying would charge the second copy twice.
        var none = Amounts(WithReplication(StorageAccounts.Body(ClusterId), "None"));
        var acrossRacks = Amounts(WithReplication(StorageAccounts.Body(ClusterId), "DifferentRack"));

        acrossRacks[QuotaMeter.StorageGb].ShouldBe(none[QuotaMeter.StorageGb]);
    }

    [Fact]
    public void ThePresetIsResolvedEvenThoughTheOrdinaryBodyDoesNotSpellTheQuantities() {
        // ⚠ THE FINDING PostgresProvider RECORDS, RE-CHECKED HERE RATHER THAN INHERITED. The body
        // StorageAccounts.Body builds — the one every test, fixture and conformance case uses — writes
        // no `sizing` block at all, so a quantity read at /properties/sizing/cpu resolves to NOTHING.
        // The amount comes from the preset, which is what a pointer cannot reach.
        StorageAccounts.Body(ClusterId).ShouldNotContain("sizing");

        Amounts(StorageAccounts.Body(ClusterId))[QuotaMeter.Vcpu].ShouldBe(4.5m);
    }

    [Fact]
    public void AnExplicitOverrideBeatsThePresetAndIsCountedPerVolumeServer() {
        var amounts = Amounts(WithSizing(StorageAccounts.Body(ClusterId, volumeServers: 3), "500m", "1Gi"));

        amounts[QuotaMeter.Vcpu].ShouldBe(3m, "500m × 3 volume servers is 1.5, plus 1.5 for the rest");
        amounts[QuotaMeter.MemoryGb].ShouldBe(6m, "1Gi × 3 is 3, plus 6 × 512Mi is 3");
    }

    [Fact]
    public void TheSameBodyDerivesTheSameAmountsTwice() {
        // ⚠ THE PURITY CLAIM, AS DIRECTLY AS IT CAN BE ASSERTED FROM HERE. The delete path re-derives
        // committed amounts from the STORED body through this same function, so a derivation reading
        // a clock, configuration or a static that changes would return a different number on the
        // delete than the create committed — quota drifting upward on every cycle.
        var body = StorageAccounts.Body(ClusterId, volumeServers: 6, storageSize: "250Gi");

        Amounts(body).ShouldBe(Amounts(body));
    }

    [Fact]
    public void AnUnresolvableQuantityRefusesRatherThanReservingZero() {
        // ⚠ A METER THAT CANNOT SAY HOW MUCH REFUSES THE WRITE. Reserving zero would provision the
        // resource and charge for none of it, and zero PASSES every limit there is. This is
        // unreachable from a validated body — AllowedValues closes the preset and the Pattern closes
        // the quantity — which is why it is asserted against a body that never reaches the schema.
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        registry.TryGetType(StorageAccounts.Type, out var registration).ShouldBeTrue();

        using var body = JsonDocument.Parse(
            WithSizing(StorageAccounts.Body(ClusterId), "not-a-quantity", "1Gi")
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
        // control-plane term is at least three pods and the storage term includes the filer's volume,
        // so even the smallest legal account draws something on all three.
        foreach (var volumeServers in new[] { 1, 16 }) {
            foreach (var masters in new[] { 1, 5 }) {
                foreach (var size in new[] { "1", "1Ti" }) {
                    foreach (var amount in Amounts(
                                 StorageAccounts.Body(
                                     ClusterId,
                                     volumeServers: volumeServers,
                                     storageSize: size,
                                     masters: masters
                                 )
                             ).Values) {
                        amount.ShouldBeGreaterThan(
                            0m,
                            $"volumeServers={volumeServers}, masters={masters}, storage={size} derives "
                            + "a zero amount, which QuotaGrain.TryReserveAsync refuses outright — so "
                            + "this body could never be created at all."
                        );
                    }
                }
            }
        }
    }

    static Dictionary<QuotaMeter, decimal> Amounts(string bodyJson) {
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        registry.TryGetType(StorageAccounts.Type, out var registration).ShouldBeTrue();

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

    static string WithReplication(string body, string replication) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["replication"] = replication;
        return node.ToJsonString();
    }
}
