using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     What a NATS cluster reserves, and the two properties the delete path depends on.
/// </summary>
/// <remarks>
///     ⚠ <b>A meter is the one declaration whose bug is invisible in every functional test.</b> A
///     resource that reserves a third of what it costs provisions perfectly, reads back perfectly and
///     converges — and the subscription is billed for a third of it. So these assertions are about
///     arithmetic rather than about behaviour, and the two that matter most are the ones about
///     <i>symmetry</i>: <c>ResourceManagerService.CommittedBy</c> re-derives committed amounts from
///     the stored body at delete time through the same function the create reserved with, so a
///     derivation that is not a pure function of its argument makes quota drift upward on every
///     create/delete cycle.
/// </remarks>
public sealed class NatsQuotaTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void EachMeterIsAProductOfTheServerCountAndTheSizeRatherThanOneServersShare() {
        // ⚠ THE PART A POINTER COULD NEVER HAVE SAID, AND THE ONE THAT IS WRONG BY A FACTOR OF THREE
        // ON THE DEFAULT BODY. A StatefulSet runs `spec.replicas` pods; each carries the whole
        // resources block and each gets its own volume from the claim template.
        // NatsClusters.StatefulSetJson writes both figures once because a pod template is per-set,
        // not because the cost is.
        var one = Amounts(NatsClusters.Body(ClusterId, servers: 1, storageSize: "10Gi"));
        var three = Amounts(NatsClusters.Body(ClusterId, servers: 3, storageSize: "10Gi"));

        three[QuotaMeter.Vcpu].ShouldBe(one[QuotaMeter.Vcpu] * 3);
        three[QuotaMeter.MemoryGb].ShouldBe(one[QuotaMeter.MemoryGb] * 3);
        three[QuotaMeter.StorageGb].ShouldBe(one[QuotaMeter.StorageGb] * 3);
    }

    [Fact]
    public void ThePresetIsResolvedEvenThoughTheOrdinaryBodyDoesNotSpellTheQuantities() {
        // ⚠ THE FINDING PostgresProvider RECORDS, RE-CHECKED HERE RATHER THAN INHERITED. The body
        // NatsClusters.Body builds — the one every test, fixture and conformance case uses — writes
        // no `sizing` block at all, so a quantity read at /properties/sizing/cpu resolves to NOTHING.
        // The amount comes from the preset, which is what a pointer cannot reach.
        NatsClusters.Body(ClusterId).ShouldNotContain("sizing");

        var amounts = Amounts(NatsClusters.Body(ClusterId, servers: 3));

        // c1.small is 1 vCPU and 2Gi, three servers.
        amounts[QuotaMeter.Vcpu].ShouldBe(3m);
        amounts[QuotaMeter.MemoryGb].ShouldBe(6m);
        amounts[QuotaMeter.StorageGb].ShouldBe(30m);
    }

    [Fact]
    public void AnExplicitOverrideBeatsThePresetAndIsCountedPerServer() {
        var amounts = Amounts(WithSizing(NatsClusters.Body(ClusterId, servers: 3), "500m", "1Gi"));

        amounts[QuotaMeter.Vcpu].ShouldBe(1.5m, "500m × 3 is 1.5 cores, not 1 and not 2");
        amounts[QuotaMeter.MemoryGb].ShouldBe(3m);
    }

    [Fact]
    public void TheSameBodyDerivesTheSameAmountsTwice() {
        // ⚠ THE PURITY CLAIM, AS DIRECTLY AS IT CAN BE ASSERTED FROM HERE. The delete path re-derives
        // committed amounts from the STORED body through this same function, so a derivation reading
        // a clock, configuration or a static that changes would return a different number on the
        // delete than the create committed — quota drifting upward on every cycle. This cannot prove
        // purity, but it catches the shapes that fail it most often.
        var body = NatsClusters.Body(ClusterId, servers: 5, storageSize: "25Gi");

        Amounts(body).ShouldBe(Amounts(body));
    }

    [Fact]
    public void AnUnresolvableQuantityRefusesRatherThanReservingZero() {
        // ⚠ A METER THAT CANNOT SAY HOW MUCH REFUSES THE WRITE. Reserving zero would provision the
        // resource and charge for none of it, and zero PASSES every limit there is. This is
        // unreachable from a validated body — AllowedValues closes the preset and the Pattern closes
        // the quantity — which is why it is asserted here against a body that never reaches the
        // schema.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        registry.TryGetType(NatsClusters.Type, out var registration).ShouldBeTrue();

        using var body = JsonDocument.Parse(WithSizing(NatsClusters.Body(ClusterId), "not-a-quantity", "1Gi"));

        var vcpu = registration.Meters.Single(x => x.Meter == QuotaMeter.Vcpu).Derivation!;

        vcpu.Amount(body.RootElement).IsFailure.ShouldBeTrue(
            "a body whose cpu quantity does not parse reserved an amount instead of refusing."
        );
    }

    [Fact]
    public void NoMeterEverDerivesZero() {
        // ⚠ THE CONSTRAINT NOTHING ELSE IN THE PLATFORM STATES, AND THE REASON `publicIps` IS NOT
        // DECLARED. QuotaGrain.TryReserveAsync refuses a non-positive amount — "A reservation must be
        // positive; 0 is not" — so a meter that CAN be zero refuses the create rather than skipping
        // itself. A conditional meter (external.enabled ? 1 : 0) is therefore undeclarable today
        // whatever MeterDerivation can express, which is a different finding from the one
        // MessagingProvider's Kafka registration records. This asserts none of the four declared here
        // has that shape, across the extremes of the schema's own ranges.
        foreach (var servers in new[] { 1, 5 }) {
            foreach (var size in new[] { "1", "10Gi" }) {
                foreach (var amount in Amounts(
                             NatsClusters.Body(ClusterId, servers: servers, storageSize: size)
                         ).Values) {
                    amount.ShouldBeGreaterThan(
                        0m,
                        $"servers={servers}, storage={size} derives a zero amount, which "
                        + "QuotaGrain.TryReserveAsync refuses outright — so this body could never be "
                        + "created at all."
                    );
                }
            }
        }
    }

    static Dictionary<QuotaMeter, decimal> Amounts(string bodyJson) {
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        registry.TryGetType(NatsClusters.Type, out var registration).ShouldBeTrue();

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
