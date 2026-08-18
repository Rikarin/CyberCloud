using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     What a registry reserves, and the two copies that get it wrong.
/// </summary>
/// <remarks>
///     ⚠ <b>The third sighting of a sum over HETEROGENEOUS components, and the first where one
///     population is multiplied by a tenant-set replica count while two are fixed.</b>
///     <c>CyberCloud.DBforPostgreSQL/servers</c> found an amount is a quantity <i>string</i>;
///     <c>CyberCloud.Messaging/natsClusters</c> that it is a <i>product</i> of a replica count and one
///     figure; <c>CyberCloud.Storage/accounts</c> that it is a <i>sum</i> over components of different
///     sizes. Here it is
///     <c>1 registry × preset + 3 × replicas × 250m + 2 × 250m</c>.
/// </remarks>
public sealed class ContainerRegistryQuotaTests {
    [Fact]
    public void ChangingOnlyTheReplicaCountMovesThreeComponentsAndNotFive() {
        // ⚠ THE TEST THAT FAILS ON EITHER COPY. A derivation copied from natsClusters —
        // `replicas × one figure` — moves everything, including the database and Redis, which run one
        // replica each whatever the body says. One copied from StorageAccounts' masters-plus-filer
        // shape misses the ×3, because THREE components share this replica count rather than one.
        //
        // The arithmetic, spelled out so the expectation is a calculation rather than a recorded
        // output: at s1.small the registry draws 1 core; the control plane draws
        // (3 × replicas + 2) × 250m. At two replicas that is 8 × 250m = 2 cores, total 3. At five
        // replicas it is 17 × 250m = 4.25 cores, total 5.25. The difference is 2.25, which is
        // 3 × 3 × 250m — three components, three extra replicas each.
        Vcpu(replicas: 2).ShouldBe(3m);
        Vcpu(replicas: 5).ShouldBe(5.25m);

        (Vcpu(5) - Vcpu(2)).ShouldBe(
            2.25m,
            "three components carry the replica count and two do not. A derivation that moved five "
            + "would over-reserve by 1.5 cores at five replicas; one that moved one would under-reserve "
            + "by the same."
        );
    }

    [Fact]
    public void TheRegistryIsSizedByThePresetAndIsNotAlsoCountedAsAControlPlanePod() {
        // ⚠ THE DOUBLE-CHARGE THIS TYPE INVITES. The registry is the one component the tenant pays for
        // by name, and it is also a pod — so a reader adding it to the control-plane count would charge
        // it twice, once at its preset and once at 250m.
        //
        // At one replica the control plane is (3 × 1 + 2) = 5 pods; at s1.small the registry is 1 core.
        // 1 + 5 × 0.25 = 2.25.
        Vcpu(replicas: 1).ShouldBe(2.25m);

        // And moving ONLY the preset moves only the registry's term.
        (Vcpu(replicas: 1, preset: "s1.medium") - Vcpu(replicas: 1)).ShouldBe(
            1m,
            "s1.medium is 2 cores against s1.small's 1, so the difference is exactly one core — the "
            + "registry's, once."
        );
    }

    [Fact]
    public void MemoryFollowsTheSameTwoPopulationsInGibibytes() {
        // s1.small is 4 GiB; the control plane is 512Mi each. At two replicas: 4 + 8 × 0.5 = 8.
        Memory(replicas: 2).ShouldBe(8m);
        Memory(replicas: 5).ShouldBe(4m + (17m * 0.5m));
    }

    [Fact]
    public void StorageCountsTheImageVolumeTheDatabasesAndTheQueueAndIgnoresTheReplicaCount() {
        // ⚠ THE METER THAT MUST NOT READ `replicas`, AND THE REASON IS THE REVERSE OF THE ONE ABOVE.
        // The three components a replica count moves own no volume; the three that own a volume run one
        // replica each. A storage derivation that multiplied by `replicas` would reserve three times
        // the disk on the default body.
        Storage(replicas: 2).ShouldBe(100m + 10m + 1m);
        Storage(replicas: 9).ShouldBe(
            111m,
            "the storage meter moved with the replica count. Three components own a volume and each "
            + "runs exactly one replica."
        );

        Storage(replicas: 2, storageSize: "500Gi").ShouldBe(511m);
    }

    [Fact]
    public void NoMeterEverDerivesZeroOnAnyBodyTheSchemaAccepts() {
        // ⚠ QuotaGrain.TryReserveAsync refuses a non-positive amount — "A reservation must be positive;
        // 0 is not" — so a meter that could derive zero would refuse every create that reached that
        // shape. charts/managed/nats records this as the reason a CONDITIONAL meter is undeclarable;
        // this type has no conditional population, and the smallest legal body is what proves it.
        foreach (var preset in ContainerRegistries.Presets.Keys) {
            Vcpu(replicas: 1, preset: preset).ShouldBeGreaterThan(0m, preset);
            Memory(replicas: 1, preset: preset).ShouldBeGreaterThan(0m, preset);
        }

        Storage(replicas: 1, storageSize: "1Gi").ShouldBeGreaterThan(0m);
    }

    [Fact]
    public void EveryDerivationIsAPureFunctionOfTheBody() {
        // ⚠ The delete path re-derives committed amounts from the resource's STORED body through the
        // same step the create reserved with, so a derivation that read a clock or configuration would
        // make a delete return a different number than the create committed and quota would drift
        // upward on every cycle. ⚠ On a SOFT-DELETABLE type the argument gets sharper: the amounts are
        // returned on the PURGE rather than on the delete, so the body they are re-derived from may be
        // seven days older.
        foreach (var meter in Derived()) {
            var body = ContainerRegistries.Body(ClusterId, replicas: 3, storageSize: "250Gi");

            Amount(meter, body).ShouldBe(Amount(meter, body), meter.Meter.ToString());
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    static decimal Vcpu(int replicas, string preset = "s1.small") =>
        Draw(QuotaMeter.Vcpu, replicas, "100Gi", preset);

    static decimal Memory(int replicas, string preset = "s1.small") =>
        Draw(QuotaMeter.MemoryGb, replicas, "100Gi", preset);

    static decimal Storage(int replicas, string storageSize = "100Gi") =>
        Draw(QuotaMeter.StorageGb, replicas, storageSize, "s1.small");

    static decimal Draw(QuotaMeter meter, int replicas, string storageSize, string preset) {
        var body = WithPreset(
            ContainerRegistries.Body(ClusterId, replicas: replicas, storageSize: storageSize),
            preset
        );

        return Amount(Derived().Single(x => x.Meter == meter), body);
    }

    static decimal Amount(MeterRegistration meter, string body) {
        using var document = JsonDocument.Parse(body);

        var derived = meter.Derivation!.Amount(document.RootElement);

        derived.IsSuccess.ShouldBeTrue(derived.Error?.Message);

        return derived.GetValueOrThrow();
    }

    static string WithPreset(string body, string preset) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["sizing"] = new System.Text.Json.Nodes.JsonObject {
            ["preset"] = preset
        };

        return node.ToJsonString();
    }

    static List<MeterRegistration> Derived() {
        ProviderRegistry.Build([new ContainerRegistryProvider()])
            .TryGetType(ContainerRegistries.Type, out var registration)
            .ShouldBeTrue();

        return [.. registration.Meters.Where(x => x.Derivation is not null)];
    }
}
