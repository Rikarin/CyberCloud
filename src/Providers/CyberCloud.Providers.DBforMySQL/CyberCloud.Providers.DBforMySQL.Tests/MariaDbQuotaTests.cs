using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Tests;

/// <summary>
///     What a MariaDB server draws on quota, in numbers.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The PostgreSQL row found that the true amount is <c>replicas × per-instance</c>, which
///         is not one value at one pointer. This type is the sharper version of the same finding: the
///         multiplier is not a number in the body AT ALL.</b> <c>MariaDbServers.GaleraReplicas</c>
///         explains why an instance count could not be a tenant-facing property — the CRD refuses an
///         even Galera count and <c>SchemaProperty</c> cannot spell "odd" — so the count is derived
///         from <c>/properties/highAvailability</c>, a <i>string naming a topology</i>. A
///         <c>Meter(meter, amountPointer, fallback)</c> had nothing to point at in either factor.
///     </para>
///     <para>
///         ⚠ <b>And the default shape is three instances</b>, where the PostgreSQL row's is two. A
///         per-instance reservation would be a third of the truth on the commonest body there is,
///         which is the worst ratio of any provider in the tree.
///     </para>
/// </remarks>
public sealed class MariaDbQuotaTests {
    [Fact]
    public void TheTypeDeclaresTheFourMetersARealServerDraws() {
        var meters = Registration().Meters;

        meters.Select(x => x.Meter)
            .ShouldBe(
                [QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb, QuotaMeter.Resources],
                ignoreOrder: true
            );
    }

    [Fact]
    public void TheDefaultBodyDrawsOneAndAHalfVcpuSixGibibytesOfMemoryAndSixtyOfStorage() {
        // ⚠ MariaDbServers.Body writes no `sizing` block at all, which is exactly why a quantity read
        // at /properties/sizing/cpu would have reserved nothing. The numbers come from
        // Presets["s1.small"] — 500m and 2Gi — multiplied by the THREE instances Galera runs, each
        // with its own resource requests and its own data PVC.
        var body = MariaDbServers.Body(Guid.NewGuid());

        Draw(QuotaMeter.Vcpu, body).ShouldBe(1.5m, "3 instances × 500m from the s1.small preset");
        Draw(QuotaMeter.MemoryGb, body).ShouldBe(6m, "3 instances × 2Gi from the s1.small preset");
        Draw(QuotaMeter.StorageGb, body).ShouldBe(60m, "3 instances × a 20Gi data volume each");
        Draw(QuotaMeter.Resources, body).ShouldBe(1m);
    }

    [Fact]
    public void TurningHighAvailabilityOffDividesEveryMeterByThree() {
        // ⚠ THE MULTIPLIER IS A TOPOLOGY STRING, AND THIS IS THE TEST THAT PROVES IT REACHES THE
        // NUMBERS. A derivation that ignored /properties/highAvailability would produce identical
        // amounts for a one-instance server and a three-instance one — over-reserving the cheap shape
        // by 3×, and under-reserving nothing, so nobody would ever complain and the platform would
        // refuse creates a subscription was entitled to.
        var single = MariaDbServers.Body(Guid.NewGuid(), highAvailability: "None");

        Draw(QuotaMeter.Vcpu, single).ShouldBe(0.5m);
        Draw(QuotaMeter.MemoryGb, single).ShouldBe(2m);
        Draw(QuotaMeter.StorageGb, single).ShouldBe(20m);
        Draw(QuotaMeter.Resources, single).ShouldBe(1m, "one resource is one resource at any size");
    }

    [Fact]
    public void ALargerVolumeScalesStorageWithTheInstanceCount() {
        var body = MariaDbServers.Body(Guid.NewGuid(), storageSize: "100Gi");

        Draw(QuotaMeter.StorageGb, body).ShouldBe(300m, "3 × a 100Gi data volume each");
    }

    [Fact]
    public void ThePresetTableIsWhatTheAmountsComeFromWhenNothingOverridesIt() {
        // Every preset, so that adding one to the schema's enum and forgetting the table is a failing
        // test rather than a server that reserves nothing.
        foreach (var (preset, (cpu, memory)) in MariaDbServers.Presets) {
            var body = WithSizing(MariaDbServers.Body(Guid.NewGuid()), preset);

            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue();
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue();

            Draw(QuotaMeter.Vcpu, body).ShouldBe(3 * cores, preset);
            Draw(QuotaMeter.MemoryGb, body).ShouldBe(3 * gibibytes, preset);
        }
    }

    [Fact]
    public void AnExplicitSizingOverridesThePreset() {
        var body = WithSizing(MariaDbServers.Body(Guid.NewGuid()), "s1.small", "1500m", "6Gi");

        Draw(QuotaMeter.Vcpu, body).ShouldBe(4.5m, "3 × 1500m, and 1500m is one and a half cores");
        Draw(QuotaMeter.MemoryGb, body).ShouldBe(18m);
    }

    [Fact]
    public void AnUnknownPresetRefusesTheAmountRatherThanReservingZero() {
        // ⚠ Unreachable from a validated body — the schema's AllowedValues close the set — and worth
        // failing on anyway: the failure it catches is somebody adding a preset to the enum and not to
        // MariaDbServers.Presets, at which point the server would provision unmetered.
        //
        // ⚠ AND A ZERO RESERVATION IS NOT A CHEAP MISTAKE: IQuotaGrain.TryReserveAsync refuses a
        // reservation of 0 outright, so the alternative to this refusal is not "reserved nothing" but a
        // create that fails somewhere less legible with no reason attached to the preset.
        var body = WithSizing(MariaDbServers.Body(Guid.NewGuid()), "s1.enormous");
        var declared = Registration().Meters.Single(x => x.Meter == QuotaMeter.Vcpu);

        using var document = JsonDocument.Parse(body);
        var amount = declared.Derivation!.Amount(document.RootElement);

        amount.IsFailure.ShouldBeTrue();
        amount.Error!.Message.ShouldContain("refused");
    }

    [Fact]
    public void EveryMeterPublishesWhatItComputesAndWhichPropertiesItReads() {
        // ⚠ The reason a delegate is acceptable at all. IResourceTypeBuilder.Meter's remarks argue that
        // a delegate "cannot be GENERATED from"; a derivation that carries its own description can be,
        // and OpenApiEmitter writes both members for every meter.
        foreach (var meter in Registration().Meters.Where(x => x.Derivation is not null)) {
            meter.Expression.ShouldNotBeNullOrWhiteSpace();
            meter.Reads.ShouldNotBeEmpty();

            foreach (var pointer in meter.Reads) {
                MariaDbServers.Pointers2026.ShouldContain(
                    pointer,
                    $"'{pointer}' is read by the {meter.Meter} meter and is not a declared property"
                );
            }
        }
    }

    [Fact]
    public void TheTopologyPointerIsInTheReadSetOfEveryMeterItMultiplies() {
        // ⚠ THE HALF A `Reads` DECLARATION EXISTS FOR. The derivation is a delegate nothing sandboxes,
        // so the only thing that makes the claim checkable is a reviewer — or this — noting that a
        // formula multiplying by an instance count must say it reads the property the count comes
        // from. On this type that property is a topology string, which is the least obvious member of
        // any read set in the tree.
        foreach (var meter in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb }) {
            Registration().Meters.Single(x => x.Meter == meter).Derivation!.Reads.ShouldContain(
                "/properties/highAvailability",
                meter.ToString()
            );
        }
    }

    [Fact]
    public void TheDerivationIsAPureFunctionOfTheBodyAndAnswersTheSameTwice() {
        // ⚠ ResourceManagerService.CommittedBy re-derives a resource's committed amounts from its
        // STORED body at delete time, through this same step. A derivation that consulted a clock,
        // configuration or anything outside its argument would make a delete return a different number
        // than the create committed — quota drifting upward on every create/delete cycle until a
        // subscription is refused creates it is entitled to. That defect has happened in this platform
        // once already.
        var body = MariaDbServers.Body(Guid.NewGuid());

        foreach (var meter in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb }) {
            Draw(meter, body).ShouldBe(Draw(meter, body), meter.ToString());
        }
    }

    static ResourceTypeRegistration Registration() {
        var registry = ProviderRegistry.Build([new MariaDbProvider()]);
        registry.TryGetType(MariaDbServers.Type, out var registration).ShouldBeTrue();

        return registration;
    }

    static decimal Draw(QuotaMeter meter, string body) {
        var declared = Registration().Meters.Single(x => x.Meter == meter);
        using var document = JsonDocument.Parse(body);

        var amount = declared.Derivation is { } derivation
            ? derivation.Amount(document.RootElement)
            : Result<decimal>.Success(declared.Fallback ?? 1m);

        amount.IsSuccess.ShouldBeTrue(amount.Error?.Message);

        return amount.GetValueOrThrow();
    }

    static string WithSizing(string body, string preset, string? cpu = null, string? memory = null) {
        var node = JsonNode.Parse(body)!.AsObject();
        var sizing = new JsonObject { ["preset"] = preset };

        if (cpu is not null) {
            sizing["cpu"] = cpu;
        }

        if (memory is not null) {
            sizing["memory"] = memory;
        }

        node["properties"]!.AsObject()["sizing"] = sizing;

        return node.ToJsonString();
    }
}
