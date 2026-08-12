using System.Text.Json;

namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     Kubernetes quantities become quota amounts exactly, and a meter that cannot say how much
///     refuses rather than guessing.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap these cover.</b> <c>MeterRegistration</c> reserved the <i>number</i> at a JSON
///         pointer, and every amount a managed service actually meters is a quantity string —
///         <c>500m</c>, <c>2</c>, <c>4Gi</c>, <c>20Gi</c>. So a provider could declare
///         <see cref="QuotaMeter.Resources" />, a count of one, and nothing else: quota was built and
///         enforced, and the two things a customer buys were outside it.
///     </para>
///     <para>
///         ⚠ <b>Rounding, and which way.</b> Every figure downstream of here is
///         <see langword="decimal" /> — <c>IQuotaGrain.TryReserveAsync</c>, <see cref="QuotaLease" />,
///         <see cref="QuotaUsage" /> — so the conversion is <b>exact</b> and there is no rounding
///         direction to choose. <c>500m</c> reserves <c>0.5</c>: as <c>0</c> it would be free CPU, as
///         <c>1</c> an overcharge, and the reason neither is necessary is that nothing forces an
///         integer. The cases below are the boundaries where a parser that took a shortcut would
///         diverge, and the assertions are equalities rather than tolerances on purpose.
///     </para>
/// </remarks>
public sealed class KubeQuantityTests {
    [Theory]
    // No unit at all — the case a pointer-reading meter used to handle, and it must still mean itself.
    [InlineData("2", 2)]
    [InlineData("0", 0)]
    // ⚠ `m` is a MILLI and is the only suffix below one. 500m is half a vCPU; a parser that read it as
    // `M` would be out by a factor of a billion in the direction of refusing every create.
    [InlineData("1m", 0.001)]
    [InlineData("500m", 0.5)]
    [InlineData("999m", 0.999)]
    [InlineData("1500m", 1.5)]
    // ⚠ 1.5 vCPU is the case the brief names: reserved as 1 it is free CPU, as 2 an overcharge. It is
    // neither, because the ledger holds a decimal.
    [InlineData("1.5", 1.5)]
    public void ACpuQuantityIsExactInCores(string text, double expected) {
        KubeQuantity.TryParse(text, out var value).ShouldBeTrue();
        value.ShouldBe((decimal)expected);
    }

    [Fact]
    public void OneKiloIsAThousandAndOneKibiIsAThousandAndTwentyFour() {
        // ⚠ THE PAIR THAT MUST NOT COLLAPSE. A parser that folded `k` into `Ki` would undercount every
        // decimal-suffixed value by 2.4%, silently, on every reservation.
        KubeQuantity.TryParse("1k", out var kilo).ShouldBeTrue();
        KubeQuantity.TryParse("1Ki", out var kibi).ShouldBeTrue();

        kilo.ShouldBe(1_000m);
        kibi.ShouldBe(1_024m);
        kibi.ShouldBeGreaterThan(kilo);
    }

    [Fact]
    public void FourGibibytesIsNotFourBillion() {
        // docs/plan/06 § Quota's memoryGb and storageGb are GIBIbytes, and MeterCatalog's units are
        // GiB-hour and GiB-month. A conversion that used 10^9 would put quota and billing 7% apart on
        // the same resource — the kind of disagreement nobody notices until a dispute.
        KubeQuantity.TryParse("4Gi", out var binary).ShouldBeTrue();
        KubeQuantity.TryParse("4G", out var deci).ShouldBeTrue();

        binary.ShouldBe(4_294_967_296m);
        deci.ShouldBe(4_000_000_000m);
    }

    [Theory]
    [InlineData("4Gi", 4)]
    [InlineData("20Gi", 20)]
    [InlineData("512Mi", 0.5)]
    [InlineData("2Gi", 2)]
    [InlineData("1Ti", 1024)]
    public void AByteQuantityConvertsToGibibytesExactly(string text, double expected) {
        KubeQuantity.TryGibibytes(text, out var value).ShouldBeTrue();
        value.ShouldBe((decimal)expected);
    }

    [Fact]
    public void AKibibyteInGibibytesIsExactRatherThanRoundedToZero() {
        // ⚠ The smallest value anything can express, and it must not become zero. A meter that
        // truncated to whole GiB would reserve nothing for a 1Ki volume — and nothing is what a
        // resource that provisions unmetered costs.
        KubeQuantity.TryGibibytes("1Ki", out var value).ShouldBeTrue();

        value.ShouldBe(1_024m / 1_073_741_824m);
        value.ShouldBeGreaterThan(0m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // ⚠ `""` is the default of every optional quantity in this platform's schemas and means "take it
    // from somewhere else". Reading it as zero would meter a preset-sized server at nothing.
    [InlineData(" ")]
    [InlineData("Gi")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("500 m")]
    [InlineData("4gi")]
    [InlineData("4GB")]
    [InlineData("1.2.3")]
    [InlineData(".")]
    public void AnythingThatIsNotAQuantityIsRefusedRatherThanReadAsZero(string? text) {
        KubeQuantity.TryParse(text, out var value).ShouldBeFalse();
        value.ShouldBe(0m);
    }
}

/// <summary>
///     A derivation says what it computes and from where, and refuses when it cannot.
/// </summary>
public sealed class MeterDerivationTests {
    static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AQuantityAtAPointerBecomesAnAmount() {
        var derivation = MeterDerivation.Quantity("/properties/disk", QuantityUnit.Gibibytes);

        var amount = derivation.Amount(Body("""{"properties":{"disk":"20Gi"}}"""));

        amount.IsSuccess.ShouldBeTrue(amount.Error?.Message);
        amount.GetValueOrThrow().ShouldBe(20m);
    }

    [Fact]
    public void APointerThatStopsResolvingRefusesRatherThanReservingZero() {
        // ⚠ THE FAILURE CLASS THIS SEAM EXISTS TO MAKE LOUD. A property is renamed, an api-version
        // moves, a schema and a meter drift apart — and the meter reserves nothing. Zero passes quota:
        // the write succeeds, the resource provisions, and nobody is charged. So does one, which is
        // what `decimal Fallback = 1m` used to supply. Only a refusal is visible.
        var derivation = MeterDerivation.Quantity("/properties/disk", QuantityUnit.Gibibytes);

        var amount = derivation.Amount(Body("""{"properties":{"volume":"20Gi"}}"""));

        amount.IsFailure.ShouldBeTrue();
        amount.Error!.Message.ShouldContain("/properties/disk");
        amount.Error.Message.ShouldContain("refused");
    }

    [Fact]
    public void AnEmptyStringIsNotZeroStorage() {
        // Every optional quantity in the platform's schemas defaults to "" — "take it from the preset",
        // "the WAL shares the data volume". None of them means "none".
        var derivation = MeterDerivation.Quantity("/properties/disk", QuantityUnit.Gibibytes);

        derivation.Amount(Body("""{"properties":{"disk":""}}""")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ADeclaredFallbackAppliesOnlyWhenThePropertyIsAbsent() {
        // ⚠ Absent and unparseable are different states. A property with a server default legitimately
        // is not in the body; a property holding "not-a-quantity" is a bug, and inheriting the fallback
        // would hide it.
        var derivation = MeterDerivation.Quantity("/properties/disk", QuantityUnit.Gibibytes, "10Gi");

        derivation.Amount(Body("""{"properties":{}}""")).GetValueOrThrow().ShouldBe(10m);
        derivation.Amount(Body("""{"properties":{"disk":"nonsense"}}""")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AFallbackThatIsNotAQuantityIsRefusedAtDeclarationTimeRatherThanAtReserveTime() {
        // Silo start is where a declaration bug belongs. The alternative is one that surfaces per
        // request, after the caller has been told the shape of the API.
        Should.Throw<ArgumentException>(
                () => MeterDerivation.Quantity("/properties/disk", QuantityUnit.Gibibytes, "10 gigs")
            )
            .Message.ShouldContain("Kubernetes quantity");
    }

    [Fact]
    public void ADerivationDeclaresWhatItReadsSoTheDocumentCanSayIt() {
        // ⚠ The objection to a delegate was that it "cannot be GENERATED from". This is the answer to
        // it: the derivation carries its own description, and OpenApiEmitter publishes both members.
        var derivation = MeterDerivation.Of(
            "replicas × cpu, in cores",
            ["/properties/replicas", "/properties/cpu"],
            _ => Result<decimal>.Success(1m)
        );

        derivation.Expression.ShouldBe("replicas × cpu, in cores");
        derivation.Reads.ShouldBe(["/properties/replicas", "/properties/cpu"]);
    }

    [Fact]
    public void ADerivationThatReadsNothingIsRefused() =>
        Should.Throw<ArgumentException>(() => MeterDerivation.Of("one", [], _ => Result<decimal>.Success(1m)))
            .Message.ShouldContain("constant");
}

/// <summary>Every meter reports what it draws, whichever of the three shapes it is.</summary>
/// <remarks>
///     ⚠ Uniform on purpose: a generated document that described pointed meters and went silent on
///     derived ones would answer "what moves this quota" for the types that never needed asking.
/// </remarks>
public sealed class MeterRegistrationTests {
    [Fact]
    public void AFlatMeterReportsItsAmount() {
        var meter = new MeterRegistration(QuotaMeter.Resources, string.Empty, 1m);

        meter.Expression.ShouldBe("1");
        meter.Reads.ShouldBeEmpty();
    }

    [Fact]
    public void APointedMeterReportsItsPointer() {
        var meter = new MeterRegistration(QuotaMeter.Vcpu, "/properties/size", null);

        meter.Expression.ShouldBe("/properties/size");
        meter.Reads.ShouldBe(["/properties/size"]);
    }

    [Fact]
    public void ADerivedMeterReportsTheFormulaAndItsInputs() {
        var meter = new MeterRegistration(QuotaMeter.Vcpu, string.Empty, null) {
            Derivation = MeterDerivation.Of(
                "replicas × cpu",
                ["/properties/replicas", "/properties/cpu"],
                _ => Result<decimal>.Success(1m)
            )
        };

        meter.Expression.ShouldBe("replicas × cpu");
        meter.Reads.ShouldBe(["/properties/replicas", "/properties/cpu"]);
    }
}
