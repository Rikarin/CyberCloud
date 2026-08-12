using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     What a managed cluster and a node pool reserve, and the two mistakes a copied derivation makes.
/// </summary>
public sealed class ManagedClusterQuotaTests {
    [Fact]
    public void AControlPlaneReplicaIsThreeContainers() {
        // ⚠ THE ASSERTION A DERIVATION COPIED FROM natsClusters FAILS, AND IT WOULD STAY PLAUSIBLE
        // FOREVER. That one is `replicas × one figure`; a Kamaji control-plane replica is
        // kube-apiserver, kube-controller-manager and kube-scheduler — ADR-009's "control-plane pod
        // set", and three separate component blocks on the CRD. A meter that multiplied by one would
        // reserve a THIRD of what the control plane costs, on every cluster, and the resource would
        // provision, read back and converge.
        //
        // ⚠ THE NUMBERS ARE TYPED OUT rather than computed from the constants, because a computation
        // from the same constants would agree with itself however they were spelled.
        Vcpu(ManagedClusters.Body(ClusterId, controlPlaneReplicas: 2)).ShouldBe(3m);   // 2 × 3 × 500m
        Memory(ManagedClusters.Body(ClusterId, controlPlaneReplicas: 2)).ShouldBe(6m); // 2 × 3 × 1Gi

        Vcpu(ManagedClusters.Body(ClusterId, controlPlaneReplicas: 1)).ShouldBe(1.5m);
        Memory(ManagedClusters.Body(ClusterId, controlPlaneReplicas: 5)).ShouldBe(15m);
    }

    [Fact]
    public void TheClusterReservesOneClusterAndOneResourceAndNoStorage() {
        var flat = Registration(ManagedClusters.Type).Meters
            .Where(x => x.Derivation is null)
            .Select(x => x.Meter)
            .ToList();

        flat.ShouldContain(QuotaMeter.Clusters);
        flat.ShouldContain(QuotaMeter.Resources);

        Registration(ManagedClusters.Type).Meters
            .Select(x => x.Meter)
            .ShouldNotContain(QuotaMeter.StorageGb);
    }

    // ── The pool, and the shape nothing in the catalogue had ────────────────────────────────────

    [Fact]
    public void APoolReservesEveryMachineAtItsPresetsSize() {
        // s1.small is (1, 4Gi); three machines with a 60Gi root volume each.
        PoolVcpu(AgentPools.Body(ClusterId, count: 3)).ShouldBe(3m);
        PoolMemory(AgentPools.Body(ClusterId, count: 3)).ShouldBe(12m);
        PoolStorage(AgentPools.Body(ClusterId, count: 3)).ShouldBe(180m);
    }

    [Fact]
    public void EnablingAutoscalingReservesTheCeilingRatherThanTheCount() {
        // ⚠ THE ASSERTION THIS TYPE EXISTS TO MAKE, AND THE ONE THE OBVIOUS IMPLEMENTATION FAILS.
        // Every quota meter in this platform is a pure function of a body, reserved at write time and
        // re-derived from the stored body at delete time, and nine providers satisfied that by being
        // sized once. A pool with an autoscaler is the first resource whose real consumption is moved
        // by something the platform does not observe, so "reserve what the body says" would reserve
        // three machines for a resource that may run twenty.
        var withAutoscaler = AgentPools.Body(ClusterId, count: 3, autoscale: true, minCount: 1, maxCount: 20);

        PoolVcpu(withAutoscaler).ShouldBe(20m, "the pool reserved its current count and not its ceiling");
        PoolMemory(withAutoscaler).ShouldBe(80m);
        PoolStorage(withAutoscaler).ShouldBe(1200m);

        // ⚠ And with the switch OFF the ceiling is ignored entirely, which is what stops a tenant who
        // set bounds and then turned autoscaling off from paying for them.
        var withoutAutoscaler = AgentPools.Body(ClusterId, count: 3, autoscale: false, maxCount: 20);

        PoolVcpu(withoutAutoscaler).ShouldBe(3m);
    }

    [Fact]
    public void ACeilingBelowTheCountDoesNotReserveLessThanTheCount() {
        // ⚠ Nothing validates that maxCount >= count — that is a relation between two properties of one
        // body and ResourceSchema checks each property against constants. So the derivation takes the
        // larger of the two rather than trusting the tenant's arithmetic; the alternative is a pool
        // running five machines against a reservation for one.
        PoolVcpu(AgentPools.Body(ClusterId, count: 5, autoscale: true, maxCount: 1)).ShouldBe(5m);
    }

    // ── The rules every derivation in the platform obeys ────────────────────────────────────────

    [Fact]
    public void TheSameBodyDerivesTheSameAmountsTwice() {
        // Purity, checked the cheap way. The delete path re-derives committed amounts from the stored
        // body through the same step the create reserved with, so a derivation that read a clock or
        // configuration would make a delete return a different number than the create committed — and
        // quota would drift upward on every create/delete cycle.
        var body = AgentPools.Body(ClusterId, count: 4);

        PoolVcpu(body).ShouldBe(PoolVcpu(body));
        PoolStorage(body).ShouldBe(PoolStorage(body));

        var cluster = ManagedClusters.Body(ClusterId, controlPlaneReplicas: 3);
        Vcpu(cluster).ShouldBe(Vcpu(cluster));
    }

    [Fact]
    public void NoMeterEverDerivesZero() {
        // ⚠ QuotaGrain.TryReserveAsync refuses a non-positive amount — "A reservation must be positive;
        // 0 is not" — so a meter that derives zero on any legal body refuses that create outright. It
        // is the blocker three other providers record for `publicIps`, and it is checked here against
        // the smallest legal body of each type rather than assumed.
        foreach (var amount in new[] {
                     Vcpu(ManagedClusters.Body(ClusterId, controlPlaneReplicas: 1)),
                     Memory(ManagedClusters.Body(ClusterId, controlPlaneReplicas: 1)),
                     PoolVcpu(AgentPools.Body(ClusterId, count: 1, size: "s1.nano", osDiskSize: "1Gi")),
                     PoolMemory(AgentPools.Body(ClusterId, count: 1, size: "s1.nano", osDiskSize: "1Gi")),
                     PoolStorage(AgentPools.Body(ClusterId, count: 1, size: "s1.nano", osDiskSize: "1Gi"))
                 }) {
            amount.ShouldBeGreaterThan(0m);
        }
    }

    [Fact]
    public void AnUnresolvableQuantityRefusesRatherThanReservingZero() {
        // ⚠ Unreachable from a validated body — the size property's AllowedValues make it so — and it
        // is exactly the drift worth failing on when somebody adds a preset to the enum and forgets the
        // table. Reserving zero would mean a resource that provisions against no quota, which is a
        // resource nobody is charged for.
        var body = "{\"location\":\"eu-central\",\"properties\":{\"count\":2,\"size\":\"s1.enormous\","
            + "\"osDiskSize\":\"60Gi\"}}";

        using var document = JsonDocument.Parse(body);

        var result = Derivation(AgentPools.Type, QuotaMeter.Vcpu).Amount(document.RootElement);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("size preset");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    static decimal Vcpu(string body) => Amount(ManagedClusters.Type, QuotaMeter.Vcpu, body);

    static decimal Memory(string body) => Amount(ManagedClusters.Type, QuotaMeter.MemoryGb, body);

    static decimal PoolVcpu(string body) => Amount(AgentPools.Type, QuotaMeter.Vcpu, body);

    static decimal PoolMemory(string body) => Amount(AgentPools.Type, QuotaMeter.MemoryGb, body);

    static decimal PoolStorage(string body) => Amount(AgentPools.Type, QuotaMeter.StorageGb, body);

    static decimal Amount(ResourceTypeName type, QuotaMeter meter, string body) {
        using var document = JsonDocument.Parse(body);
        var result = Derivation(type, meter).Amount(document.RootElement);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.GetValueOrThrow();
    }

    static MeterDerivation Derivation(ResourceTypeName type, QuotaMeter meter) =>
        Registration(type).Meters.Single(x => x.Meter == meter).Derivation!;

    static ResourceTypeRegistration Registration(ResourceTypeName type) {
        ProviderRegistry.Build([new ContainerServiceProvider()]).TryGetType(type, out var registration)
            .ShouldBeTrue();

        return registration;
    }
}
