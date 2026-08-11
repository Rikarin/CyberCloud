namespace CyberCloud.Metering.Tests;

/// <summary>
///     The append-only usage ledger — docs/plan/22 § The pipeline: <i>"Corrections are new entries
///     with a reason and a link to the original, never edits. An adjustable ledger cannot be audited
///     and cannot be defended in a dispute."</i>
/// </summary>
/// <remarks>
///     The <i>structural</i> half — that no mutator exists to call — is
///     <c>CyberCloud.Metering.Contracts.Tests.LedgerImmutabilityTests</c>. This is the behavioural
///     half: what happens when somebody tries.
/// </remarks>
[Collection(MeteringClusterFixture.Name)]
public sealed class LedgerTests(MeteringCluster cluster) {
    static readonly Guid Widget = Guid.Parse("cccccccc-cccc-4ccc-8ccc-ccccccccccc1");

    // ── FAILURE CLASS: the ledger cannot be edited ─────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The mutation attempt, and there is no method to make it with.</b> The only way to
    ///     change a recorded figure is to append a correction, so this test asserts what happens
    ///     instead: the original is untouched, the correction is a <i>new</i> entry with a later
    ///     sequence, it points at the original, and it carries a reason.
    /// </summary>
    [Fact]
    public async Task ACorrectionIsANewEntryPointingAtTheOriginalAndTheOriginalIsUntouched() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var ledger = cluster.Ledger(MeteringCluster.Tenant, subscription);
        var original = (await ledger.AppendAsync(Entry(10m))).GetValueOrThrow();

        var correction = (await ledger.AppendCorrectionAsync(original.EntryId, -4m, "double-counted a resize"))
            .GetValueOrThrow();

        // The correction is a NEW entry.
        correction.EntryId.ShouldNotBe(original.EntryId);
        correction.Sequence.ShouldBe(original.Sequence + 1);

        // ⚠ …with a link to the original and a reason. Both halves of docs/plan/22's sentence.
        correction.CorrectsEntryId.ShouldBe(original.EntryId);
        correction.Reason.ShouldBe("double-counted a resize");
        correction.IsCorrection.ShouldBeTrue();

        // ⚠ …and the ORIGINAL IS BYTE-FOR-BYTE WHAT IT WAS. Not marked, not superseded, not flagged.
        var entries = (await ledger.ListAsync()).GetValueOrThrow();
        var reread = entries.Single(x => x.EntryId == original.EntryId);

        reread.ShouldBe(original);
        reread.Quantity.ShouldBe(10m);
        reread.IsCorrection.ShouldBeFalse();

        // Both entries are in the history, in order.
        entries.Length.ShouldBe(2);
        entries[0].EntryId.ShouldBe(original.EntryId);
        entries[1].EntryId.ShouldBe(correction.EntryId);

        // The net is the plain sum — a figure a dispute can be walked through line by line.
        (await ledger.NetQuantityAsync(BillingMeter.VCpuHours)).GetValueOrThrow().ShouldBe(6m);
    }

    /// <summary>
    ///     ⚠ A correction with no reason is an edit wearing a hat: it changes the total and explains
    ///     nothing, which is exactly what an auditable ledger exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ACorrectionWithoutAReasonIsRefused(string reason) {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var ledger = cluster.Ledger(MeteringCluster.Tenant, subscription);
        var original = (await ledger.AppendAsync(Entry(10m))).GetValueOrThrow();

        var refused = await ledger.AppendCorrectionAsync(original.EntryId, -1m, reason);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("needs a reason");

        (await ledger.CountAsync()).GetValueOrThrow().ShouldBe(1);
    }

    /// <summary>
    ///     A correction pointing at nothing is an unexplained adjustment — the shape of the thing the
    ///     ledger refuses to be.
    /// </summary>
    [Fact]
    public async Task ACorrectionPointingAtNothingIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var refused = await cluster.Ledger(MeteringCluster.Tenant, subscription)
            .AppendCorrectionAsync(Guid.NewGuid(), -1m, "typo");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    /// <summary>
    ///     ⚠ Two independent corrections to one entry <b>sum</b> rather than racing. That is why a
    ///     correction carries a delta and not a replacement value: two replacements would race and
    ///     the later would silently discard the earlier.
    /// </summary>
    [Fact]
    public async Task TwoCorrectionsToOneEntrySum() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var ledger = cluster.Ledger(MeteringCluster.Tenant, subscription);
        var original = (await ledger.AppendAsync(Entry(10m))).GetValueOrThrow();

        _ = await ledger.AppendCorrectionAsync(original.EntryId, -3m, "first review");
        _ = await ledger.AppendCorrectionAsync(original.EntryId, -2m, "second review");

        (await ledger.NetQuantityAsync(BillingMeter.VCpuHours)).GetValueOrThrow().ShouldBe(5m);

        var corrections = (await ledger.ListCorrectionsAsync(original.EntryId)).GetValueOrThrow();

        corrections.Length.ShouldBe(2);
        corrections.ShouldAllBe(x => x.CorrectsEntryId == original.EntryId);
        corrections[0].Sequence.ShouldBeLessThan(corrections[1].Sequence);
    }

    /// <summary>A correction of zero says a figure was disputed and upheld — that is an audit note, not a meter.</summary>
    [Fact]
    public async Task AZeroCorrectionIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var ledger = cluster.Ledger(MeteringCluster.Tenant, subscription);
        var original = (await ledger.AppendAsync(Entry(10m))).GetValueOrThrow();

        (await ledger.AppendCorrectionAsync(original.EntryId, 0m, "checked")).IsFailure.ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ The last line of defence. A duplicate reaching an append-only ledger is permanent, so a
    ///     second entry for one (resource, meter, window) is refused here even though
    ///     <c>UsageRollupGrain</c> should never let one through.
    /// </summary>
    [Fact]
    public async Task ASecondEntryForOneWindowIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var ledger = cluster.Ledger(MeteringCluster.Tenant, subscription);

        (await ledger.AppendAsync(Entry(10m))).IsSuccess.ShouldBeTrue();

        var refused = await ledger.AppendAsync(Entry(10m));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error!.Message.ShouldContain("permanent double count");

        (await ledger.CountAsync()).GetValueOrThrow().ShouldBe(1);
    }

    /// <summary>
    ///     A negative usage entry is refused — reducing a recorded figure is a correction, with a
    ///     reason and a link. A negative sample would reduce the total and explain nothing.
    /// </summary>
    [Fact]
    public async Task ANegativeUsageEntryIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var refused = await cluster.Ledger(MeteringCluster.Tenant, subscription).AppendAsync(Entry(-1m));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("AppendCorrectionAsync");
    }

    /// <summary>
    ///     The sequence is dense and gapless. Nothing can create a gap, so a gap is evidence of
    ///     tampering rather than of a bug — which is only a useful statement if it starts dense.
    /// </summary>
    [Fact]
    public async Task TheSequenceIsDenseAndGapless() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var ledger = cluster.Ledger(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);

        for (var i = 0; i < 5; i++) {
            _ = await ledger.AppendAsync(Entry(1m, hour.Start.AddHours(i)));
        }

        var entries = (await ledger.ListAsync()).GetValueOrThrow();

        entries.Length.ShouldBe(5);
        entries.Select(x => x.Sequence).ShouldBe([1L, 2L, 3L, 4L, 5L]);
    }

    /// <summary>
    ///     ⚠ The ledger survives deactivation. It is durable-tier for exactly this, and a ledger that
    ///     forgot on deactivation would be neither auditable nor defensible.
    /// </summary>
    [Fact]
    public async Task TheLedgerSurvivesDeactivation() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var ledger = cluster.Ledger(MeteringCluster.Tenant, subscription);
        var original = (await ledger.AppendAsync(Entry(10m))).GetValueOrThrow();
        _ = await ledger.AppendCorrectionAsync(original.EntryId, -4m, "resize");

        await ledger.DeactivateAsync();

        var afterRestart = cluster.Ledger(MeteringCluster.Tenant, subscription);
        var entries = (await afterRestart.ListAsync()).GetValueOrThrow();

        entries.Length.ShouldBe(2);
        entries[0].ShouldBe(original);
        (await afterRestart.NetQuantityAsync(BillingMeter.VCpuHours)).GetValueOrThrow().ShouldBe(6m);
    }

    static UsageLedgerAppend Entry(decimal quantity, DateTimeOffset? at = null) {
        var hour = UsageWindow.HourAt(at ?? TestClock.Start);

        return new() {
            TenantId = MeteringCluster.Tenant,
            ResourceId = Widget,
            ResourcePath = "/tenants/t/subscriptions/s/resourceGroups/prod/providers/CyberCloud.Sample/widgets/w",
            Meter = BillingMeter.VCpuHours,
            Region = "eu-central",
            WindowStart = hour.Start,
            WindowEnd = hour.End,
            Quantity = quantity,
            SampleCount = 12
        };
    }

    static Guid Fresh() => Guid.NewGuid();
}
