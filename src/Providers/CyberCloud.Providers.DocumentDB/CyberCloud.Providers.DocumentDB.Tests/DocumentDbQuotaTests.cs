using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;

namespace CyberCloud.Providers.DocumentDB.Tests;

/// <summary>
///     What an account reserves, and the two populations a derivation copied from a neighbour would
///     miss.
/// </summary>
/// <remarks>
///     ⚠ <b>Every derivation is a PURE FUNCTION OF THE BODY and the delete path depends on it.</b>
///     <c>ResourceManagerService.CommittedBy</c> re-derives committed amounts from the resource's
///     stored body through the same step the create reserved with, so a derivation that read a clock
///     or configuration would make a delete return a different number than the create committed and
///     quota would drift upward on every create/delete cycle.
/// </remarks>
public sealed class DocumentDbQuotaTests {
    static readonly Guid ClusterId = Guid.Parse("dddddddd-0000-4000-8000-000000000006");

    [Fact]
    public void ChangingOnlyTheGatewayCountStillMovesCpuAndMemory() {
        // ⚠ THE TEST A DERIVATION COPIED FROM CyberCloud.DBforPostgreSQL/servers FAILS. That row is
        // one workload and its meters are `replicas × preset`. This row is TWO workloads in two
        // different objects, and a copy would count the Cluster and miss the Deployment entirely —
        // which on the default body is two whole pods that provision perfectly and bill for nothing.
        var vcpu = Derivation(QuotaMeter.Vcpu);
        var memory = Derivation(QuotaMeter.MemoryGb);

        using var two = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId, gatewayReplicas: 2));
        using var seven = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId, gatewayReplicas: 7));

        var cpuTwo = Amount(vcpu, two.RootElement);
        var cpuSeven = Amount(vcpu, seven.RootElement);

        cpuSeven.ShouldBeGreaterThan(
            cpuTwo,
            "the vCPU meter did not move when the gateway count did, so the FerretDB pods are "
            + "provisioned and not reserved."
        );

        // Five more pods at 250m apiece.
        (cpuSeven - cpuTwo).ShouldBe(1.25m);

        // Five more pods at 512Mi apiece.
        (Amount(memory, seven.RootElement) - Amount(memory, two.RootElement)).ShouldBe(2.5m);
    }

    [Fact]
    public void TheDefaultBodyReservesBothPopulations() {
        // ⚠ THE ARITHMETIC, WRITTEN OUT, BECAUSE THE ONLY THING THAT MAKES A DELEGATE CHECKABLE IS A
        // READER DOING THE SUM. Default body: 2 PostgreSQL instances at s1.small (1 core, 4Gi) plus 2
        // gateway pods at 250m/512Mi.
        //
        //   vCPU   = 2 × 1     + 2 × 0.25  = 2.5
        //   memory = 2 × 4     + 2 × 0.5   = 9
        //   storage= 2 × 20Gi              = 40
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        Amount(Derivation(QuotaMeter.Vcpu), body.RootElement).ShouldBe(2.5m);
        Amount(Derivation(QuotaMeter.MemoryGb), body.RootElement).ShouldBe(9m);
        Amount(Derivation(QuotaMeter.StorageGb), body.RootElement).ShouldBe(40m);
    }

    [Fact]
    public void StorageIsMultipliedByTheInstanceCountBecauseEveryReplicaIsAFullCopy() {
        // ⚠ CloudNativePG gives every instance its OWN PVC of the declared size — a replica is a full
        // physical copy, not a share of one volume. A meter that reserved `storage.size` once would
        // under-reserve by (instances - 1) volumes, which on a five-instance account is four times the
        // declared figure.
        using var one = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId, instances: 1));
        using var five = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId, instances: 5));

        var storage = Derivation(QuotaMeter.StorageGb);

        Amount(storage, one.RootElement).ShouldBe(20m);
        Amount(storage, five.RootElement).ShouldBe(100m);

        // ⚠ And the gateway pods add nothing to it. FerretDB writes nothing durable, which is the same
        // reason it is a Deployment rather than a StatefulSet.
        using var manyGateways = JsonDocument.Parse(
            DocumentDbAccounts.Body(ClusterId, instances: 1, gatewayReplicas: 10)
        );

        Amount(storage, manyGateways.RootElement).ShouldBe(20m);
    }

    [Fact]
    public void NoMeterEverDerivesZero() {
        // ⚠ QuotaGrain.TryReserveAsync REFUSES A NON-POSITIVE AMOUNT — "A reservation must be
        // positive; 0 is not" — so a meter that derives zero on any reachable body refuses that
        // create outright. CyberCloud.Messaging/natsClusters found this the hard way with a
        // conditional external listener; this type has no conditional meter, and this is what keeps
        // it that way when somebody adds one.
        var derivations = new[] {
            Derivation(QuotaMeter.Vcpu), Derivation(QuotaMeter.MemoryGb), Derivation(QuotaMeter.StorageGb)
        };

        foreach (var instances in new[] { 1, 5 }) {
            foreach (var gateways in new[] { 1, 10 }) {
                using var body = JsonDocument.Parse(
                    DocumentDbAccounts.Body(ClusterId, instances: instances, gatewayReplicas: gateways)
                );

                foreach (var derivation in derivations) {
                    Amount(derivation, body.RootElement).ShouldBeGreaterThan(
                        0m,
                        $"{derivation.Expression} derives zero at instances={instances}, "
                        + $"gateway.replicas={gateways}. QuotaGrain refuses a reservation of 0, so "
                        + "that body cannot be created at all."
                    );
                }
            }
        }
    }

    [Fact]
    public void AnUnparseableQuantityRefusesTheWriteRatherThanReservingZero() {
        // ⚠ Unreachable from a validated body — AllowedValues closes the preset and the Pattern closes
        // the overrides — and it is the refusal that matters. Reserving zero would mean the write
        // succeeds, the resource provisions, and nobody is charged; docs/plan/06 § Quota says that is
        // the failure to prevent. This is also the drift worth failing on when somebody adds a preset
        // to the enum and forgets the table.
        using var body = JsonDocument.Parse(WithPreset(DocumentDbAccounts.Body(ClusterId), "s1.gigantic"));

        foreach (var meter in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb }) {
            var result = Derivation(meter).Amount(body.RootElement);

            result.IsSuccess.ShouldBeFalse(meter.ToString());
            result.Error!.Message.ShouldContain("refused rather than reserved at zero");
        }
    }

    static MeterDerivation Derivation(QuotaMeter meter) {
        var registry = ProviderRegistry.Build([new DocumentDbProvider()]);
        registry.TryGetType(DocumentDbAccounts.Type, out var registration).ShouldBeTrue();

        return registration.Meters.Single(x => x.Meter == meter).Derivation!;
    }

    static decimal Amount(MeterDerivation derivation, JsonElement body) {
        var result = derivation.Amount(body);
        result.IsSuccess.ShouldBeTrue(derivation.Expression);
        return result.GetValueOrThrow();
    }

    static string WithPreset(string body, string preset) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["sizing"] =
            new System.Text.Json.Nodes.JsonObject { ["preset"] = preset };

        return node.ToJsonString();
    }
}
