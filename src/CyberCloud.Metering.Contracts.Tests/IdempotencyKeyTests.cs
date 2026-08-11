namespace CyberCloud.Metering.Contracts.Tests;

/// <summary>
///     docs/plan/22 § The pipeline's key: <c>sha256(resourceId | meter | windowStart | windowEnd)</c>,
///     "so a redelivery after a silo restart collapses".
/// </summary>
/// <remarks>
///     The key has to be two things at once and both halves need their own tests: <b>stable</b>
///     across everything that is not usage (a second computation, a different emission time, a
///     rebuilt record), and <b>sensitive</b> to everything that is (a different resource, meter,
///     window or occurrence). A key that is only stable swallows genuine usage; a key that is only
///     sensitive never collapses anything.
/// </remarks>
public sealed class IdempotencyKeyTests {
    static readonly Guid Resource = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    static readonly DateTimeOffset Start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset End = new(2026, 8, 11, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public void TheKeyIsDeterministic() =>
        UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End)
            .ShouldBe(UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End));

    [Fact]
    public void TheKeyIsSixtyFourLowerCaseHexCharacters() {
        var key = UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End);

        key.Length.ShouldBe(64);
        key.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    /// <summary>
    ///     ⚠ The instants go into the key as UTC ticks, so a window expressed in another offset is
    ///     the same window. Without this, a silo in a non-UTC region would produce different keys for
    ///     the same usage and nothing would ever collapse.
    /// </summary>
    [Fact]
    public void TheKeyIgnoresTheOffsetAnInstantIsExpressedIn() {
        var berlin = Start.ToOffset(TimeSpan.FromHours(2));
        var berlinEnd = End.ToOffset(TimeSpan.FromHours(2));

        UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, berlin, berlinEnd)
            .ShouldBe(UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End));
    }

    [Fact]
    public void ADifferentResourceIsADifferentKey() =>
        UsageEvent.KeyFor(Other, BillingMeter.VCpuHours, Start, End)
            .ShouldNotBe(UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End));

    [Fact]
    public void ADifferentMeterIsADifferentKey() =>
        UsageEvent.KeyFor(Resource, BillingMeter.MemoryGbHours, Start, End)
            .ShouldNotBe(UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End));

    [Fact]
    public void ADifferentWindowIsADifferentKey() =>
        UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, End, End.AddMinutes(5))
            .ShouldNotBe(UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End));

    /// <summary>
    ///     ⚠ The failure docs/plan/22 § The pipeline's four-component formula does not cover. Two
    ///     occurrences of an event-based meter on one resource inside one window are two genuine
    ///     records; without the event id they carry identical key material and the dedup swallows
    ///     one. See <c>UsageEvent.EventId</c>.
    /// </summary>
    [Fact]
    public void TwoEventBasedOccurrencesInOneWindowAreDifferentKeys() =>
        UsageEvent.KeyFor(Resource, BillingMeter.Requests, Start, End, "req-2")
            .ShouldNotBe(UsageEvent.KeyFor(Resource, BillingMeter.Requests, Start, End, "req-1"));

    /// <summary>
    ///     ⚠ And the same occurrence redelivered is still one record — the other half, without which
    ///     the fix above would just be "never deduplicate event-based meters".
    /// </summary>
    [Fact]
    public void TheSameEventIdRedeliveredIsTheSameKey() =>
        UsageEvent.KeyFor(Resource, BillingMeter.Requests, Start, End, "req-1")
            .ShouldBe(UsageEvent.KeyFor(Resource, BillingMeter.Requests, Start, End, "req-1"));

    /// <summary>
    ///     A state-based key is bit-for-bit docs/plan/22 § The pipeline's four-component formula —
    ///     the fifth component is empty and contributes nothing but the separator.
    /// </summary>
    [Fact]
    public void AStateBasedKeyIsTheDocumentsFourComponentFormula() =>
        UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End, string.Empty)
            .ShouldBe(UsageEvent.KeyFor(Resource, BillingMeter.VCpuHours, Start, End));

    /// <summary>
    ///     ⚠ An event id carrying the separator could otherwise forge another event's key — pick
    ///     <c>"a|b"</c> against a meter whose material ends in <c>"a"</c> and the digests collide.
    /// </summary>
    [Fact]
    public void AnEventIdCarryingTheSeparatorIsRefused() =>
        Should.Throw<ArgumentException>(
            () => UsageEvent.KeyFor(Resource, BillingMeter.Requests, Start, End, "a|b")
        );

    /// <summary>
    ///     ⚠ Emission time is deliberately not a key component. A redelivery after a silo restart
    ///     arrives with a later <c>EmittedAt</c> and must still be the same record.
    /// </summary>
    [Fact]
    public void EmissionTimeIsNotPartOfTheKey() {
        var first = Build(emittedAt: Start);
        var second = Build(emittedAt: Start.AddHours(3));

        second.IdempotencyKey.ShouldBe(first.IdempotencyKey);
    }

    /// <summary>
    ///     ⚠ Neither is the quantity, and that is the subtle one. If a re-sample saw a resized disk,
    ///     the window is still the same window and the second reading must collapse rather than add.
    ///     Two readings of one window summed would double-bill a resize.
    /// </summary>
    [Fact]
    public void QuantityIsNotPartOfTheKey() {
        var first = Build(quantity: 1m);
        var second = Build(quantity: 99m);

        second.IdempotencyKey.ShouldBe(first.IdempotencyKey);
    }

    [Fact]
    public void ABuiltRecordCarriesAConsistentKey() => Build().IsKeyConsistent().ShouldBeTrue();

    /// <summary>
    ///     A hand-built record is exactly what <c>UsageRollupGrain</c> refuses. Accepting one would
    ///     break dedup in whichever direction the wrong key happened to point.
    /// </summary>
    [Fact]
    public void AnInitialiserBuiltRecordDoesNotCarryAConsistentKey() {
        var forged = new UsageEvent {
            TenantId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            ResourceId = Resource,
            Meter = BillingMeter.VCpuHours,
            Quantity = 1m,
            WindowStart = Start,
            WindowEnd = End,
            IdempotencyKey = new string('0', 64)
        };

        forged.IsKeyConsistent().ShouldBeFalse();
    }

    [Fact]
    public void SamplingAnEventBasedMeterIsRefused() {
        var refused = UsageEvent.ForSample(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Resource,
            "/p",
            BillingMeter.Requests,
            1m,
            new(Start, End),
            "eu-central",
            Start
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("not a state-based meter");
    }

    [Fact]
    public void EmittingAStateBasedMeterIsRefused() {
        var refused = UsageEvent.ForEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Resource,
            "/p",
            BillingMeter.VCpuHours,
            1m,
            new(Start, End),
            "eu-central",
            "e1",
            Start
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("not an event-based meter");
    }

    [Fact]
    public void AnEventBasedEmissionWithoutAnEventIdIsRefused() {
        var refused = UsageEvent.ForEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Resource,
            "/p",
            BillingMeter.Requests,
            1m,
            new(Start, End),
            "eu-central",
            "  ",
            Start
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("needs an event id");
    }

    [Fact]
    public void ANegativeQuantityIsRefused() {
        var refused = UsageEvent.ForSample(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Resource,
            "/p",
            BillingMeter.VCpuHours,
            -1m,
            new(Start, End),
            "eu-central",
            Start
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("cannot be negative");
    }

    [Fact]
    public void AnUnresolvedResourceIdIsRefused() {
        var refused = UsageEvent.ForSample(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.Empty,
            "/p",
            BillingMeter.VCpuHours,
            1m,
            new(Start, End),
            "eu-central",
            Start
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    static UsageEvent Build(decimal quantity = 1m, DateTimeOffset? emittedAt = null) =>
        UsageEvent.ForSample(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                Resource,
                "/tenants/x/subscriptions/y/resourceGroups/prod/providers/CyberCloud.Sample/widgets/w",
                BillingMeter.VCpuHours,
                quantity,
                new(Start, End),
                "eu-central",
                emittedAt ?? Start
            )
            .GetValueOrThrow();
}
