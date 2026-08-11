using System.Globalization;

namespace CyberCloud.Communication.Tests;

/// <summary>
///     docs/plan/17 § The parts that are actually the work: <i>"An SMS loop is a five-figure incident
///     within an hour, and the limit is the only thing between a bug and that invoice."</i>
/// </summary>
[Collection(CommunicationClusterFixture.Name)]
public sealed class SpendLimitTests(CommunicationCluster cluster) {
    // ── FAILURE CLASS: a spend limit stops a loop ──────────────────────────────────────────────

    [Fact]
    public async Task ARunawayLoopStopsAtTheSpendLimitAndTheRefusalNamesTheLimitAndTheWindow() {
        CommunicationCluster.ResetDoubles();

        // Ten messages' worth of budget at five cents each, and a message cap far above it so the
        // spend cap is unambiguously what stops the loop.
        var service = await cluster.NewServiceAsync(messagesPerWindow: 10_000, spendPerWindow: 0.50m, unitCost: 0.05m);

        var sent = 0;
        Error? stopped = null;

        // The runaway. A bug that sends in a loop does not stop on its own; the limit is what stops
        // it, and this asserts the loop terminates rather than that a single send was refused.
        for (var i = 0; i < 500; i++) {
            var outcome = await cluster.SendAsync(
                CommunicationCluster.Tenant,
                CommunicationCluster.Request(service, "loop-" + i.ToString(CultureInfo.InvariantCulture))
            );

            if (outcome.TryGetError(out var error)) {
                stopped = error;
                break;
            }

            sent++;
        }

        stopped.ShouldNotBeNull("500 iterations at 0.05 against a 0.50 budget must not all have gone out");
        sent.ShouldBe(10, "0.50 of budget at 0.05 a message is exactly ten messages");

        TestProviders.Sms.Calls.ShouldBe(
            10,
            "the eleventh iteration must not reach the carrier — the limit is checked before dispatch, "
            + "so a loop costs nothing past the cap"
        );

        stopped.Code.ShouldBe(ErrorCode.QuotaExceeded);

        // docs/plan/22 § Quota: "429 naming the meter, the request, the current usage and the limit.
        // Never a bare 'quota exceeded'." At 03:00 the difference is whether on-call has to open a
        // dashboard to find out what happened.
        stopped.Message.ShouldContain("spend limit");
        stopped.Message.ShouldContain("0.50", Case.Sensitive, "the refusal names the limit");
        stopped.Message.ShouldContain("EUR", Case.Sensitive, "and its currency");
        stopped.Message.ShouldContain("2026-08-11", Case.Sensitive, "and the window it applies to");
        stopped.Message.ShouldContain("UTC midnight", Case.Sensitive, "and when it resets");
    }

    [Fact]
    public async Task AMessageCountLimitStopsALoopOfCheapMessagesAndNamesItself() {
        CommunicationCluster.ResetDoubles();

        // The other half. A loop sending free push notifications never crosses a spend cap, which is
        // why both limits exist.
        var service = await cluster.NewServiceAsync(messagesPerWindow: 3, spendPerWindow: 1_000m, unitCost: 0m);

        Error? stopped = null;
        var sent = 0;

        for (var i = 0; i < 50; i++) {
            var outcome = await cluster.SendAsync(
                CommunicationCluster.Tenant,
                CommunicationCluster.Request(service, "loop-" + i.ToString(CultureInfo.InvariantCulture))
            );

            if (outcome.TryGetError(out var error)) {
                stopped = error;
                break;
            }

            sent++;
        }

        sent.ShouldBe(3);
        TestProviders.Sms.Calls.ShouldBe(3);
        stopped.ShouldNotBeNull();
        stopped.Code.ShouldBe(ErrorCode.QuotaExceeded);
        stopped.Message.ShouldContain("message limit");
        stopped.Message.ShouldContain("2026-08-11");
    }

    [Fact]
    public async Task TheWindowResetsAtTheNextUtcMidnight() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(messagesPerWindow: 1, spendPerWindow: 1_000m);

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "a")))
            .IsSuccess.ShouldBeTrue();

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "b")))
            .Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);

        // 14:22 on the 11th plus ten hours is 00:22 on the 12th — the next UTC day.
        TestClock.Instance.Advance(TimeSpan.FromHours(10));

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "c")))
            .IsSuccess.ShouldBeTrue("the cap is per UTC calendar day and the day has turned");
    }

    [Fact]
    public async Task ADefaultServiceAllowsNothingRatherThanEverything() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(messagesPerWindow: 0, spendPerWindow: 0m);

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        refused.Error!
            .Code
            .ShouldBe(
                ErrorCode.QuotaExceeded,
                "ChannelLimits.None is zero rather than unlimited: a tenant who has not set a limit "
                + "gets a refusal naming the limit, not an invoice naming a number"
            );

        TestProviders.Sms.Calls.ShouldBe(0);
    }

    // ── The reservation, and what it is for ────────────────────────────────────────────────────

    [Fact]
    public async Task ACarrierFailureGivesTheReservationBack() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(messagesPerWindow: 2, spendPerWindow: 1m, unitCost: 0.05m);

        TestProviders.Sms.Fail = true;

        var failed = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        failed.GetValueOrThrow().Status.ShouldBe(MessageStatus.Failed);

        var spend = (await cluster.Limits(service).ReadAsync(ChannelKind.Sms)).GetValueOrThrow();

        spend.Committed.ShouldBe(
            0m,
            "a channel whose carrier is down would otherwise burn the day's allowance on messages "
            + "that never left, and the tenant's first working send would be refused with a limit "
            + "message that is true and useless"
        );

        spend.Messages.ShouldBe(0);

        TestProviders.Sms.Fail = false;

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "otp-2")))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnsettledReservationIsGivenBackWhenItsLeaseExpires() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(messagesPerWindow: 100, spendPerWindow: 100m, unitCost: 1m);
        var limits = cluster.Limits(service);

        // Reserved directly, as a silo that died between reserving and dispatching would leave it.
        var held = (await limits.ReserveAsync(
            ChannelKind.Sms,
            new() { MaxMessagesPerWindow = 100, MaxSpendPerWindow = 100m, Currency = "EUR" },
            1m
        )).GetValueOrThrow();

        (await limits.ReadAsync(ChannelKind.Sms)).GetValueOrThrow().Reserved.ShouldBe(1m);

        TestClock.Instance.Advance(ISendLimitGrain.ReservationLease + TimeSpan.FromMinutes(1));

        var after = (await limits.ReadAsync(ChannelKind.Sms)).GetValueOrThrow();

        after.Reserved.ShouldBe(0m, "without the lease, a slice of the day's budget is held until midnight");
        after.Messages.ShouldBe(0, "and the message never left, so it does not count either");
        held.ReservationId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task SettlingAboveTheEstimateIsAllowedAndTheCapThenBindsFromTheHigherFigure() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(messagesPerWindow: 100, spendPerWindow: 1m, unitCost: 0.05m);

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        // An international SMS routinely costs several times a domestic one, and the receipt is the
        // first place the real price appears — which is after the point a limit could stop anything.
        _ = await cluster.ReceiptAsync(
            CommunicationCluster.Tenant,
            service,
            new() {
                ProviderMessageId = sent.GetValueOrThrow().ProviderMessageId,
                Status = MessageStatus.Delivered,
                Cost = 0.98m,
                Currency = "EUR",
                OccurredAt = TestClock.Instance.UtcNow
            }
        );

        (await cluster.Limits(service).ReadAsync(ChannelKind.Sms)).GetValueOrThrow().Settled.ShouldBe(0.98m);

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "otp-2")))
            .Error!
            .Code
            .ShouldBe(
                ErrorCode.QuotaExceeded,
                "the cap's job is to stop the NEXT one, from the real figure rather than the estimate"
            );
    }

    [Fact]
    public async Task SettlingAnUnknownReservationSucceedsAndChangesNothing() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        (await cluster.Limits(service).SettleAsync(Guid.NewGuid(), 1m)).IsSuccess.ShouldBeTrue();
        (await cluster.Limits(service).ReleaseAsync(Guid.NewGuid())).IsSuccess.ShouldBeTrue();
    }
}
