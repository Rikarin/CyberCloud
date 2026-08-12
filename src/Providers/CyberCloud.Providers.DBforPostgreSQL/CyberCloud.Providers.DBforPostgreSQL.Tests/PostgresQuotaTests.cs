using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;

namespace CyberCloud.Providers.DBforPostgreSQL.Tests;

/// <summary>
///     What a PostgreSQL server draws on quota, in numbers.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until the registry grew a derived-amount seam this type declared
///         <see cref="QuotaMeter.Resources" /> and nothing else</b> — a count of one — because
///         <c>Meter(meter, amountPointer, fallback)</c> reserves the <i>number</i> at a JSON pointer and
///         none of vcpu, memoryGb or storageGb is a number in this body. <c>storage.size</c> is
///         <c>20Gi</c>, <c>sizing.cpu</c> is <c>500m</c>, and the ordinary body carries neither, because
///         <c>sizing.preset</c> names them indirectly. Quota was built, enforced, and blind to the two
///         things a customer buys.
///     </para>
///     <para>
///         ⚠ <b>A unit on the pointer would not have fixed it, and these numbers are why.</b> The
///         amounts below come from <c>replicas</c> and from a preset table, neither of which is at the
///         pointer a quantity would have been read from — and the product of two properties is not
///         something a pointer can address at all.
///     </para>
/// </remarks>
public sealed class PostgresQuotaTests {
    static ResourceTypeRegistration Registration() {
        var registry = ProviderRegistry.Build([new PostgresProvider()]);
        registry.TryGetType(PostgresServers.Type, out var registration).ShouldBeTrue();
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

    [Fact]
    public void TheTypeDeclaresTheFourMetersARealServerDraws() {
        var meters = Registration().Meters;

        meters.Select(x => x.Meter)
            .ShouldBe(
                [QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb, QuotaMeter.Resources],
                ignoreOrder: true
            );
    }

    /// <summary>
    ///     The default body — two instances, the <c>s1.small</c> preset, a 20 GiB volume.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>PostgresServers.Body</c> writes no <c>sizing</c> block at all</b>, which is exactly
    ///     why a quantity read at <c>/properties/sizing/cpu</c> would have reserved nothing. The
    ///     numbers below come from <c>Presets["s1.small"]</c> — <c>500m</c> and <c>2Gi</c> — multiplied
    ///     by the two instances CloudNativePG will run, each with its own resource requests and its own
    ///     data PVC.
    /// </remarks>
    [Fact]
    public void TheDefaultBodyDrawsOneVcpuFourGibibytesOfMemoryAndFortyOfStorage() {
        var body = PostgresServers.Body(Guid.NewGuid());

        Draw(QuotaMeter.Vcpu, body).ShouldBe(1m, "2 instances × 500m from the s1.small preset");
        Draw(QuotaMeter.MemoryGb, body).ShouldBe(4m, "2 instances × 2Gi from the s1.small preset");
        Draw(QuotaMeter.StorageGb, body).ShouldBe(40m, "2 instances × a 20Gi data volume each");
        Draw(QuotaMeter.Resources, body).ShouldBe(1m);
    }

    [Fact]
    public void AThreeInstanceServerOnALargerVolumeScalesEveryMeterWithIt() {
        var body = PostgresServers.Body(Guid.NewGuid(), replicas: 3, storageSize: "100Gi");

        Draw(QuotaMeter.Vcpu, body).ShouldBe(1.5m, "3 × 500m — not 1, which is free CPU, and not 2");
        Draw(QuotaMeter.MemoryGb, body).ShouldBe(6m);
        Draw(QuotaMeter.StorageGb, body).ShouldBe(300m);
    }

    [Fact]
    public void ThePresetTableIsWhatTheAmountsComeFromWhenNothingOverridesIt() {
        // Every preset, so that adding one to the schema's enum and forgetting the table is a failing
        // test rather than a server that reserves nothing.
        foreach (var (preset, (cpu, memory)) in PostgresServers.Presets) {
            var body = WithSizing(PostgresServers.Body(Guid.NewGuid()), preset);

            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue();
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue();

            Draw(QuotaMeter.Vcpu, body).ShouldBe(2 * cores, preset);
            Draw(QuotaMeter.MemoryGb, body).ShouldBe(2 * gibibytes, preset);
        }
    }

    [Fact]
    public void AnExplicitSizingOverridesThePreset() {
        var body = WithSizing(PostgresServers.Body(Guid.NewGuid()), "s1.small", "1500m", "6Gi");

        Draw(QuotaMeter.Vcpu, body).ShouldBe(3m, "2 × 1500m, and 1500m is one and a half cores");
        Draw(QuotaMeter.MemoryGb, body).ShouldBe(12m);
    }

    [Fact]
    public void ASeparateWalVolumeIsCountedAndASharedOneIsNotCountedTwice() {
        // ⚠ `walSize: ""` means the WAL shares the data volume — one volume, not zero storage. Both
        // readings produce a plausible number and only one of them is right.
        var shared = PostgresServers.Body(Guid.NewGuid());
        var separate = WithStorage(shared, "10Gi");

        Draw(QuotaMeter.StorageGb, shared).ShouldBe(40m);
        Draw(QuotaMeter.StorageGb, separate).ShouldBe(60m, "2 × (20Gi data + 10Gi WAL)");
    }

    [Fact]
    public void AnUnknownPresetRefusesTheAmountRatherThanReservingZero() {
        // ⚠ Unreachable from a validated body — the schema's AllowedValues close the set — and worth
        // failing on anyway: the failure it catches is somebody adding a preset to the enum and not to
        // PostgresServers.Presets, at which point the server would provision unmetered.
        var body = WithSizing(PostgresServers.Body(Guid.NewGuid()), "s1.enormous");
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
                PostgresServers.Pointers2026.ShouldContain(
                    pointer,
                    $"'{pointer}' is read by the {meter.Meter} meter and is not a declared property"
                );
            }
        }
    }

    static string WithSizing(string body, string preset, string? cpu = null, string? memory = null) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        var properties = node["properties"]!.AsObject();
        var sizing = new System.Text.Json.Nodes.JsonObject { ["preset"] = preset };

        if (cpu is not null) {
            sizing["cpu"] = cpu;
        }

        if (memory is not null) {
            sizing["memory"] = memory;
        }

        properties["sizing"] = sizing;
        return node.ToJsonString();
    }

    static string WithStorage(string body, string walSize) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        node["properties"]!["storage"]!["walSize"] = walSize;
        return node.ToJsonString();
    }
}
