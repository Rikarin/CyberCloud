using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     What a RabbitMQ cluster reserves, and the two properties the delete path depends on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A meter is the one declaration whose bug is invisible in every functional test.</b> A
///         resource that reserves a third of what it costs provisions perfectly, reads back perfectly
///         and converges — and the subscription is billed for a third of it. So these assertions are
///         about arithmetic rather than behaviour, and the two that matter most are about
///         <i>symmetry</i>: <c>ResourceManagerService.CommittedBy</c> re-derives committed amounts
///         from the stored body at delete time through the same function the create reserved with.
///     </para>
///     <para>
///         ⚠ <b>The storage arithmetic is the one a reader is most likely to get wrong on THIS row,
///         and it is wrong in the opposite direction from the intuition.</b> A quorum queue is a Raft
///         group and every member holds the whole log, so <c>storage.size</c> is what each node needs
///         rather than a cluster-wide figure to divide. Somebody used to a sharded store would divide
///         by the node count and under-reserve by exactly that factor.
///     </para>
/// </remarks>
public sealed class RabbitmqQuotaTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000009");

    [Fact]
    public void EachMeterIsAProductOfTheNodeCountAndTheSizeRatherThanOneNodesShare() {
        var one = Amounts(RabbitmqClusters.Body(ClusterId, nodes: 1, storageSize: "20Gi"));
        var three = Amounts(RabbitmqClusters.Body(ClusterId, nodes: 3, storageSize: "20Gi"));

        three[QuotaMeter.Vcpu].ShouldBe(one[QuotaMeter.Vcpu] * 3);
        three[QuotaMeter.MemoryGb].ShouldBe(one[QuotaMeter.MemoryGb] * 3);
        three[QuotaMeter.StorageGb].ShouldBe(
            one[QuotaMeter.StorageGb] * 3,
            "storage was not multiplied by the node count. A quorum queue's Raft log is held WHOLE by "
            + "every member, so three nodes at 20Gi is 60Gi of disk rather than 20Gi shared."
        );
    }

    [Fact]
    public void ThePresetIsResolvedEvenThoughTheOrdinaryBodyDoesNotSpellTheQuantities() {
        // ⚠ The body RabbitmqClusters.Body builds — the one every test, fixture and conformance case
        // uses — writes no `sizing` block at all, so a quantity read at /properties/sizing/cpu
        // resolves to NOTHING. The amount comes from the preset, which is what a pointer cannot reach
        // and what MeterDerivation exists for.
        RabbitmqClusters.Body(ClusterId).ShouldNotContain("sizing");

        var amounts = Amounts(RabbitmqClusters.Body(ClusterId, nodes: 3));

        // c1.small is 1 vCPU and 2Gi, three nodes, 20Gi each.
        amounts[QuotaMeter.Vcpu].ShouldBe(3m);
        amounts[QuotaMeter.MemoryGb].ShouldBe(6m);
        amounts[QuotaMeter.StorageGb].ShouldBe(60m);
    }

    [Fact]
    public void AnExplicitOverrideBeatsThePresetAndIsCountedPerNode() {
        var amounts = Amounts(WithSizing(RabbitmqClusters.Body(ClusterId, nodes: 3), "500m", "1Gi"));

        amounts[QuotaMeter.Vcpu].ShouldBe(1.5m, "500m × 3 is 1.5 cores, not 1 and not 2");
        amounts[QuotaMeter.MemoryGb].ShouldBe(3m);
    }

    [Fact]
    public void TheSameBodyDerivesTheSameAmountsTwice() {
        // ⚠ THE PURITY CLAIM, AS DIRECTLY AS IT CAN BE ASSERTED FROM HERE. The delete path re-derives
        // committed amounts from the STORED body through this same function, so a derivation reading
        // a clock, configuration or a mutable static would return a different number on the delete
        // than the create committed — quota drifting upward on every cycle.
        var body = RabbitmqClusters.Body(ClusterId, nodes: 5, storageSize: "25Gi");

        Amounts(body).ShouldBe(Amounts(body));
    }

    [Fact]
    public void AnUnresolvableQuantityRefusesRatherThanReservingZero() {
        // ⚠ A METER THAT CANNOT SAY HOW MUCH REFUSES THE WRITE. Reserving zero would provision the
        // resource and charge for none of it, and zero PASSES every limit there is.
        //
        // ⚠ AND ON THIS TYPE THE REFUSAL DOES A SECOND JOB THE SIBLINGS' DOES NOT. An unresolvable
        // preset makes RabbitmqClusters.ClusterJson render no `resources` block, and this CRD
        // DEFAULTS spec.resources to a Burstable block at quantities nobody chose — so the branch
        // this meter refuses is the branch that would otherwise provision a differently-sized,
        // differently-classed pod against no quota at all.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        registry.TryGetType(RabbitmqClusters.Type, out var registration).ShouldBeTrue();

        using var body = JsonDocument.Parse(
            WithSizing(RabbitmqClusters.Body(ClusterId), "not-a-quantity", "1Gi")
        );

        registration.Meters.Single(x => x.Meter == QuotaMeter.Vcpu).Derivation!
            .Amount(body.RootElement).IsFailure.ShouldBeTrue(
                "a body whose cpu quantity does not parse reserved an amount instead of refusing."
            );
    }

    [Fact]
    public void NoMeterEverDerivesZero() {
        // ⚠ THE CONSTRAINT NOTHING ELSE IN THE PLATFORM STATES. QuotaGrain.TryReserveAsync refuses a
        // non-positive amount — "A reservation must be positive; 0 is not" — so a meter that CAN be
        // zero refuses the create rather than skipping itself. That is why the sibling types cannot
        // declare `publicIps`; this type has no such meter to want, because it declares no external
        // listener at all. This asserts none of the four declared here has that shape, across the
        // extremes of the schema's own ranges.
        foreach (var nodes in new[] { 1, 7 }) {
            foreach (var size in new[] { "1", "20Gi" }) {
                foreach (var amount in Amounts(
                             RabbitmqClusters.Body(ClusterId, nodes: nodes, storageSize: size)
                         ).Values) {
                    amount.ShouldBeGreaterThan(
                        0m,
                        $"nodes={nodes}, storage={size} derives a zero amount, which "
                        + "QuotaGrain.TryReserveAsync refuses outright — so this body could never be "
                        + "created at all."
                    );
                }
            }
        }
    }

    static Dictionary<QuotaMeter, decimal> Amounts(string bodyJson) {
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        registry.TryGetType(RabbitmqClusters.Type, out var registration).ShouldBeTrue();

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
