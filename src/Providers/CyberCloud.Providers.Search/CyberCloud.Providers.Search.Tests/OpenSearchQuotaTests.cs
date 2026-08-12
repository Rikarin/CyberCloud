using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search.Tests;

/// <summary>
///     What a search service reserves.
/// </summary>
/// <remarks>
///     ⚠ <b>A meter is the one declaration whose bug is invisible in every functional test.</b> A
///     resource that reserves a third of what it costs provisions perfectly, reads back perfectly and
///     converges — and the subscription is billed for a third of it.
///     <para>
///         ⚠ <b>This type's meters are a sum over heterogeneous components — the shape
///         <c>CyberCloud.Storage/accounts</c> found — and a second sighting is what turns that from an
///         anecdote into a pattern.</b> What is new here is that <b>the three meters split the
///         populations differently from each other</b>: a coordinating node is sized like a
///         <i>data</i> node for CPU and memory and like a <i>cluster-manager</i> node for disk, so a
///         single parameterised derivation would be wrong on one of the three however it was written.
///     </para>
/// </remarks>
public sealed class OpenSearchQuotaTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    [Fact]
    public void TheClusterManagerNodesAreCountedAndNotOnlyTheDataNodes() {
        // ⚠ THE ASSERTION A DERIVATION COPIED FROM CyberCloud.Messaging/natsClusters FAILS. That
        // provider's shape is `replicas × preset`, which is right about the data nodes here and misses
        // three whole JVMs on the default body.
        var amounts = Amounts(OpenSearchServices.Body(ClusterId, dataNodes: 3, masterNodes: 3));

        // 3 data × m1.medium (1 core) = 3, plus 3 cluster-managers × 500m = 1.5.
        amounts[QuotaMeter.Vcpu].ShouldBe(4.5m);

        // 3 × 8Gi = 24, plus 3 × 2Gi = 6.
        amounts[QuotaMeter.MemoryGb].ShouldBe(30m);
    }

    [Fact]
    public void ChangingOnlyTheMasterCountStillMovesTheAmounts() {
        // ⚠ THE DIRECT TEST OF THE SAME THING, AND THE ONE THAT FAILS LOUDEST ON A COPIED DERIVATION.
        // `masterNodes` reaches no data node, so a meter that read only the sized population would
        // return the same number for a one-master service and a five-master one.
        var one = Amounts(OpenSearchServices.Body(ClusterId, masterNodes: 1));
        var five = Amounts(OpenSearchServices.Body(ClusterId, masterNodes: 5));

        five[QuotaMeter.Vcpu].ShouldBe(one[QuotaMeter.Vcpu] + 2m, "four extra managers at 500m each");
        five[QuotaMeter.MemoryGb].ShouldBe(one[QuotaMeter.MemoryGb] + 8m, "four extra managers at 2Gi");

        // ⚠ And storage moves too, which is where this type differs from CyberCloud.Storage/accounts:
        // a SeaweedFS master has no volume and an OpenSearch cluster-manager node does — the cluster
        // metadata is the only copy of what indices exist and where their shards live.
        five[QuotaMeter.StorageGb].ShouldBe(one[QuotaMeter.StorageGb] + 40m, "four extra 10Gi volumes");
    }

    [Fact]
    public void ACoordinatingNodeIsSizedLikeADataNodeForCpuAndLikeAManagerForDisk() {
        // ⚠ THE FACT THAT MAKES THREE DERIVATIONS NECESSARY WHERE ONE PARAMETERISED ONE WOULD LOOK
        // TIDIER. A coordinating node merges result sets, which costs CPU and memory exactly as a data
        // node's preset says; it holds no shards, so its disk is the fixed 10Gi. A derivation that
        // reused one split for all three meters would over-reserve every coordinating node's disk by
        // the tenant's whole storage.size — 100Gi apiece on the default body.
        var none = Amounts(OpenSearchServices.Body(ClusterId, coordinatingNodes: 0));
        var two = Amounts(OpenSearchServices.Body(ClusterId, coordinatingNodes: 2));

        two[QuotaMeter.Vcpu].ShouldBe(none[QuotaMeter.Vcpu] + 2m, "two coordinators at m1.medium's 1");
        two[QuotaMeter.MemoryGb].ShouldBe(none[QuotaMeter.MemoryGb] + 16m, "two at m1.medium's 8Gi");

        two[QuotaMeter.StorageGb].ShouldBe(
            none[QuotaMeter.StorageGb] + 20m,
            "two coordinators at the fixed 10Gi, NOT at the tenant's storage.size — a coordinating "
            + "node holds no shards"
        );
    }

    [Fact]
    public void StorageIsTheDataNodesProductPlusEveryOtherNodesFixedVolume() {
        var three = Amounts(OpenSearchServices.Body(ClusterId, dataNodes: 3, storageSize: "100Gi"));
        var six = Amounts(OpenSearchServices.Body(ClusterId, dataNodes: 6, storageSize: "100Gi"));

        three[QuotaMeter.StorageGb].ShouldBe(330m, "3 × 100Gi + 3 managers × 10Gi");
        six[QuotaMeter.StorageGb].ShouldBe(630m);
    }

    [Fact]
    public void ThePresetIsResolvedEvenThoughTheOrdinaryBodyDoesNotSpellTheQuantities() {
        // ⚠ THE FINDING CyberCloud.DBforPostgreSQL/servers RECORDS, RE-CHECKED HERE RATHER THAN
        // INHERITED. The body OpenSearchServices.Body builds — the one every test, fixture and
        // conformance case uses — writes no `sizing` block at all, so a quantity read at
        // /properties/sizing/cpu resolves to NOTHING. The amount comes from the preset, which is what
        // a pointer cannot reach.
        OpenSearchServices.Body(ClusterId).ShouldNotContain("sizing");

        Amounts(OpenSearchServices.Body(ClusterId))[QuotaMeter.Vcpu].ShouldBe(4.5m);
    }

    [Fact]
    public void AnExplicitOverrideBeatsThePresetAndIsCountedPerSizedNode() {
        var amounts = Amounts(
            WithSizing(OpenSearchServices.Body(ClusterId, dataNodes: 3, coordinatingNodes: 1), "2", "16Gi")
        );

        amounts[QuotaMeter.Vcpu].ShouldBe(9.5m, "2 × 4 sized nodes is 8, plus 3 managers × 500m");
        amounts[QuotaMeter.MemoryGb].ShouldBe(70m, "16Gi × 4 is 64, plus 3 × 2Gi is 6");
    }

    [Fact]
    public void TheSameBodyDerivesTheSameAmountsTwice() {
        // ⚠ THE PURITY CLAIM, AS DIRECTLY AS IT CAN BE ASSERTED FROM HERE. The delete path re-derives
        // committed amounts from the STORED body through this same function, so a derivation reading a
        // clock, configuration or a static that changes would return a different number on the delete
        // than the create committed — quota drifting upward on every cycle.
        var body = OpenSearchServices.Body(ClusterId, dataNodes: 6, storageSize: "250Gi");

        Amounts(body).ShouldBe(Amounts(body));
    }

    [Fact]
    public void AnUnresolvableQuantityRefusesRatherThanReservingZero() {
        // ⚠ A METER THAT CANNOT SAY HOW MUCH REFUSES THE WRITE. Reserving zero would provision the
        // resource and charge for none of it, and zero PASSES every limit there is. This is
        // unreachable from a validated body — AllowedValues closes the preset and the Pattern closes
        // the quantity — which is why it is asserted against a body that never reaches the schema.
        var registry = ProviderRegistry.Build([new SearchProvider()]);
        registry.TryGetType(OpenSearchServices.Type, out var registration).ShouldBeTrue();

        using var body = JsonDocument.Parse(
            WithSizing(OpenSearchServices.Body(ClusterId), "not-a-quantity", "8Gi")
        );

        var vcpu = registration.Meters.Single(x => x.Meter == QuotaMeter.Vcpu).Derivation!;

        vcpu.Amount(body.RootElement).IsFailure.ShouldBeTrue(
            "a body whose cpu quantity does not parse reserved an amount instead of refusing."
        );
    }

    [Fact]
    public void NoMeterEverDerivesZeroEvenWithNoCoordinatingNodes() {
        // ⚠ THE FINDING THIS TYPE CONTRIBUTES, AND IT IS A CORRECTION TO THE SCOPE OF ONE THAT ALREADY
        // EXISTS RATHER THAN A NEW ONE. charts/managed/nats/conformance.yaml's
        // `publicips-meter-is-undeclarable` records that QuotaGrain.TryReserveAsync refuses a
        // non-positive amount — "A reservation must be positive; 0 is not" — and concludes that a
        // conditional meter is undeclarable. That is true of a WHOLE METER and it had never been
        // tested against a meter with a conditional TERM, because no earlier type had an optional
        // population.
        //
        // /properties/coordinatingNodes defaults to 0, so the coordinating term of all three
        // derivations is zero on every ordinary create. The sum is not, because the data term has a
        // floor of one node and the manager term a floor of one node. A meter whose TOTAL has a floor
        // may contain a term that is zero; what it may not do is BE zero.
        //
        // ⚠ 0 is deliberately in the coordinating list, and it is the case the whole test is about.
        foreach (var dataNodes in new[] { 1, 20 }) {
            foreach (var masterNodes in new[] { 1, 5 }) {
                foreach (var coordinatingNodes in new[] { 0, 10 }) {
                    foreach (var size in new[] { "1", "1Ti" }) {
                        var body = OpenSearchServices.Body(
                            ClusterId,
                            dataNodes: dataNodes,
                            storageSize: size,
                            masterNodes: masterNodes,
                            coordinatingNodes: coordinatingNodes
                        );

                        foreach (var (meter, amount) in Amounts(body)) {
                            amount.ShouldBeGreaterThan(
                                0m,
                                $"{meter} derives zero for dataNodes={dataNodes}, "
                                + $"masterNodes={masterNodes}, coordinatingNodes={coordinatingNodes}, "
                                + $"storage={size} — which QuotaGrain.TryReserveAsync refuses outright, "
                                + "so this body could never be created at all."
                            );
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void TheSmallestLegalServiceStillDrawsSomethingOnEveryMeter() {
        // The floor, named rather than inferred from the sweep above: one data node, one manager, no
        // coordinators, the smallest disk the pattern accepts.
        var amounts = Amounts(
            OpenSearchServices.Body(
                ClusterId,
                dataNodes: 1,
                storageSize: "1",
                masterNodes: 1,
                coordinatingNodes: 0
            )
        );

        amounts[QuotaMeter.Vcpu].ShouldBe(1.5m, "one m1.medium core plus one manager at 500m");
        amounts[QuotaMeter.MemoryGb].ShouldBe(10m, "8Gi plus 2Gi");
        amounts[QuotaMeter.StorageGb].ShouldBeGreaterThan(0m);
    }

    static Dictionary<QuotaMeter, decimal> Amounts(string bodyJson) {
        var registry = ProviderRegistry.Build([new SearchProvider()]);
        registry.TryGetType(OpenSearchServices.Type, out var registration).ShouldBeTrue();

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
