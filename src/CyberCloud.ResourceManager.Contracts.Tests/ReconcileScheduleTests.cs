namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     The backoff ladder, the jitter bound, and the 60-minute timeout that names the last progress
///     entry. docs/plan/08 § The reconcile loop.
/// </summary>
public sealed class ReconcileScheduleTests {
    [Fact]
    public void TheLadderIsExactlyTheOneTheDocumentGives() {
        // docs/plan/08 § The reconcile loop: "10 s → 30 s → 2 min → 10 min, capped".
        ReconcileSchedule.Backoff.ShouldBe(
            [
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(10)
            ]
        );
    }

    [Fact]
    public void PastTheEndOfTheLadderTheLastRungRepeatsRatherThanSchedulingStopping() {
        // ⚠ "Capped" means the last rung repeats. If it meant "stop scheduling", a resource would be
        // left with no reminder and no terminal state — the "stuck forever" the timeout exists to
        // prevent, arrived at by a different route.
        ReconcileSchedule.BaseDelayFor(4).ShouldBe(TimeSpan.FromMinutes(10));
        ReconcileSchedule.BaseDelayFor(400).ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void ANegativeAttemptSchedulesSoonRatherThanNever() {
        ReconcileSchedule.BaseDelayFor(-1).ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)]
    public void JitterStaysInsideTwentyPercentForEverySampleInTheInterval(int attempt) {
        // ⚠ Swept rather than sampled. A seeded Random would prove the property for the handful of
        // samples that seed happens to draw; the property is about EVERY sample, which is why
        // DelayFor takes the sample as a parameter.
        var basis = ReconcileSchedule.BaseDelayFor(attempt);
        var low = basis * (1.0 - ReconcileSchedule.JitterFraction);
        var high = basis * (1.0 + ReconcileSchedule.JitterFraction);

        for (var step = 0; step <= 1000; step++) {
            var delay = ReconcileSchedule.DelayFor(attempt, step / 1000.0);

            delay.ShouldBeGreaterThanOrEqualTo(low, $"sample {step / 1000.0}");
            delay.ShouldBeLessThanOrEqualTo(high, $"sample {step / 1000.0}");
        }
    }

    [Fact]
    public void TheExtremesOfTheJitterIntervalAreExactlyTheTwentyPercentBounds() {
        var basis = ReconcileSchedule.BaseDelayFor(0);

        ReconcileSchedule.DelayFor(0, 0.0).ShouldBe(basis * 0.8, TimeSpan.FromMilliseconds(1));
        ReconcileSchedule.DelayFor(0, 0.5).ShouldBe(basis, TimeSpan.FromMilliseconds(1));
        ReconcileSchedule.DelayFor(0, 1.0).ShouldBe(basis * 1.2, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void AReconcilersRequestIsAFloorAndNeverShortensTheLadder() {
        // ⚠ A reconciler asking for one second every second would defeat the backoff that exists to
        // protect a cluster we do not own. The larger of the two wins, always.
        var shorter = ReconcileSchedule.DelayFor(3, 0.5, TimeSpan.FromSeconds(1));
        shorter.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(8));

        var longer = ReconcileSchedule.DelayFor(0, 0.5, TimeSpan.FromMinutes(30));
        longer.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void ASampleOutsideTheUnitIntervalIsRefusedRatherThanClamped() {
        Should.Throw<ArgumentOutOfRangeException>(() => ReconcileSchedule.DelayFor(0, -0.1));
        Should.Throw<ArgumentOutOfRangeException>(() => ReconcileSchedule.DelayFor(0, 1.1));
    }

    [Fact]
    public void SixtyMinutesIsTheCeilingAndItIsInclusive() {
        var started = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        ReconcileSchedule.HasTimedOut(started, started.AddMinutes(59)).ShouldBeFalse();
        ReconcileSchedule.HasTimedOut(started, started.AddMinutes(60)).ShouldBeTrue();
        ReconcileSchedule.HasTimedOut(started, started.AddMinutes(61)).ShouldBeTrue();
    }

    [Fact]
    public void TheTimeoutErrorNamesTheLastProgressEntry() {
        // ⚠ THE REQUIREMENT, VERBATIM. docs/plan/08 § The reconcile loop: "After 60 minutes in
        // InProgress the operation fails with a timeout error naming the last progress entry — a
        // resource stuck forever is worse than a resource that failed, because a failure is
        // actionable." A timeout that said only "timed out" moves the diagnosis to a log search.
        var operationId = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
        var last = new OperationProgress {
            At = new DateTimeOffset(2026, 8, 11, 12, 41, 0, TimeSpan.Zero),
            Step = "waiting-for-ready",
            Detail = "2 of 3 replicas ready"
        };

        var error = ReconcileSchedule.TimedOut(operationId, "/tenants/x/…/servers/main", last);

        error.Code.ShouldBe(ErrorCode.OperationTimeout);
        error.Message.ShouldContain("waiting-for-ready");
        error.Message.ShouldContain("2 of 3 replicas ready");
        error.Message.ShouldContain("/tenants/x/…/servers/main");
        error.Message.ShouldContain("60 minutes");
    }

    [Fact]
    public void WithNoProgressTheTimeoutSaysSoRatherThanQuotingAnEmptyEntry() {
        // "The last progress entry was ''" reads as data loss. Saying it never reported is the
        // diagnosis.
        var error = ReconcileSchedule.TimedOut(Guid.NewGuid(), "/tenants/x/…/servers/main", null);

        error.Code.ShouldBe(ErrorCode.OperationTimeout);
        error.Message.ShouldContain("never reported progress");
        error.Message.ShouldNotContain("''");
    }

    [Fact]
    public void TheErrorCarriesNoExceptionDetail() {
        // docs/plan/08 § Errors: "No exception details, ever." The Error type has no field that could
        // carry one, and this asserts the timeout builder does not smuggle one into the message.
        var error = ReconcileSchedule.TimedOut(Guid.NewGuid(), "/x", null);

        error.Message.ShouldNotContain("   at ");
        error.Message.ShouldNotContain("Exception");
        error.Details.ShouldBeEmpty();
    }
}
