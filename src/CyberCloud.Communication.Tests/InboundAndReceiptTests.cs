namespace CyberCloud.Communication.Tests;

/// <summary>
///     Inbound and delivery receipts — docs/plan/17 § The parts that are actually the work:
///     <i><c>STOP</c> handling is legally required in most jurisdictions</i>, and <i>"without them
///     'did it arrive' is unanswerable"</i>.
/// </summary>
[Collection(CommunicationClusterFixture.Name)]
public sealed class InboundAndReceiptTests(CommunicationCluster cluster) {
    // ── FAILURE CLASS: STOP adds to the suppression list, and the next send is refused ──────────

    [Fact]
    public async Task StopSuppressesTheSenderAndTheNextSendIsRefused() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var before = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "alert-1", "+420777123456")
        );

        before.IsSuccess.ShouldBeTrue();

        var handled = await cluster.InboundAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Sms,
                From = "+420 777 123 456",
                To = "CYBERCLOUD",
                Body = "STOP",
                ProviderMessageId = "SM-inbound-1",
                ReceivedAt = TestClock.Instance.UtcNow
            }
        );

        handled.GetValueOrThrow().WasStop.ShouldBeTrue();
        handled.GetValueOrThrow().Suppressed.ShouldBeTrue();
        handled.GetValueOrThrow().Keyword.ShouldBe("STOP");

        var after = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "alert-2", "+420777123456")
        );

        after.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        TestProviders.Sms.Calls.ShouldBe(1, "only the send from before the opt-out reached the carrier");
    }

    [Fact]
    public async Task AnOptOutRecordedFromAnInboundReplyCannotBeClearedByTheTenant() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        _ = await cluster.InboundAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Sms,
                From = "+420777123456",
                Body = "unsubscribe",
                ReceivedAt = TestClock.Instance.UtcNow
            }
        );

        (await cluster.Suppression(service).ReleaseAsync(ChannelKind.Sms, "+420777123456", "they changed their mind"))
            .Error!
            .Code
            .ShouldBe(
                ErrorCode.PolicyViolation,
                "STOP handling is legally required, and a tenant who could undo one has undone the "
                + "compliance rather than the entry"
            );
    }

    [Fact]
    public async Task AnOrdinaryReplyIsNotAnOptOut() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var handled = await cluster.InboundAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Sms,
                From = "+420777123456",
                Body = "please don't stop sending these",
                ReceivedAt = TestClock.Instance.UtcNow
            }
        );

        handled.GetValueOrThrow().WasStop.ShouldBeFalse();
        (await cluster.Suppression(service).CountAsync()).GetValueOrThrow().ShouldBe(0);
    }

    // ── FAILURE CLASS: a receipt for an unknown provider message id is ignored, not fatal ───────

    [Fact]
    public async Task AReceiptForAProviderMessageIdNobodyRemembersIsIgnored() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var handled = await cluster.ReceiptAsync(
            CommunicationCluster.Tenant,
            service,
            new() {
                ProviderMessageId = "SM-from-a-message-we-have-forgotten",
                Status = MessageStatus.Delivered,
                OccurredAt = TestClock.Instance.UtcNow
            }
        );

        handled.IsSuccess.ShouldBeTrue(
            "webhooks arrive late, twice, and for messages past IMessageGrain.Retention. Every one of "
            + "those is the carrier working correctly, so failing here pages somebody for somebody "
            + "else's retry logic"
        );

        handled.GetValueOrThrow().ShouldBeFalse("it found no message, and the count is reported rather than thrown");
    }

    [Fact]
    public async Task AReceiptWithNoProviderMessageIdIsIgnored() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var handled = await cluster.ReceiptAsync(
            CommunicationCluster.Tenant,
            service,
            new() { ProviderMessageId = string.Empty, Status = MessageStatus.Delivered }
        );

        handled.IsSuccess.ShouldBeTrue();
        handled.GetValueOrThrow().ShouldBeFalse();
    }

    [Fact]
    public async Task AReceiptFindsItsMessageAndAnswersDidItArrive() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        var providerId = sent.GetValueOrThrow().ProviderMessageId;

        (await cluster.ReceiptAsync(
            CommunicationCluster.Tenant,
            service,
            new() {
                ProviderMessageId = providerId,
                Status = MessageStatus.Delivered,
                ProviderStatus = "delivered",
                Cost = 0.07m,
                Currency = "EUR",
                OccurredAt = TestClock.Instance.UtcNow
            }
        )).GetValueOrThrow().ShouldBeTrue();

        var status = (await cluster.StatusAsync(CommunicationCluster.Tenant, service, "otp-1"))
            .GetValueOrThrow();

        status.Status.ShouldBe(MessageStatus.Delivered);
        status.Receipts.Length.ShouldBe(1);
        status.Cost.ShouldBe(0.07m, "the carrier priced it, and the day's spend settles to the real figure");
    }

    [Fact]
    public async Task ADuplicateReceiptIsRecordedAndDoesNotDoubleCountSpend() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(unitCost: 0.05m);

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        var receipt = new DeliveryReceipt {
            ProviderMessageId = sent.GetValueOrThrow().ProviderMessageId,
            Status = MessageStatus.Delivered,
            Cost = 0.09m,
            Currency = "EUR",
            OccurredAt = TestClock.Instance.UtcNow
        };

        _ = await cluster.ReceiptAsync(CommunicationCluster.Tenant, service, receipt);
        _ = await cluster.ReceiptAsync(CommunicationCluster.Tenant, service, receipt);

        var spend = (await cluster.Limits(service).ReadAsync(ChannelKind.Sms)).GetValueOrThrow();

        spend.Settled.ShouldBe(
            0.09m,
            "a carrier receipt arriving twice must not double-count spend — SettleAsync on a claim "
            + "that is already settled succeeds and changes nothing"
        );
    }

    [Fact]
    public async Task AnOutOfOrderReceiptDoesNotWalkADeliveredMessageBackwards() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        var providerId = sent.GetValueOrThrow().ProviderMessageId;

        _ = await cluster.ReceiptAsync(
            CommunicationCluster.Tenant,
            service,
            new() { ProviderMessageId = providerId, Status = MessageStatus.Delivered }
        );

        // Carriers produce "sent" and "delivered" from different systems and do not order them.
        _ = await cluster.ReceiptAsync(
            CommunicationCluster.Tenant,
            service,
            new() { ProviderMessageId = providerId, Status = MessageStatus.Dispatched }
        );

        var status = (await cluster.StatusAsync(CommunicationCluster.Tenant, service, "otp-1"))
            .GetValueOrThrow();

        status.Status.ShouldBe(MessageStatus.Delivered);
        status.Receipts.Length.ShouldBe(2, "the receipt list is a log, so both are kept even though only one moved the status");
    }

    [Fact]
    public async Task AReceiptThatReportsAHardBounceSuppressesTheAddress() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1", "+420777123456")
        );

        _ = await cluster.ReceiptAsync(
            CommunicationCluster.Tenant,
            service,
            new() {
                ProviderMessageId = sent.GetValueOrThrow().ProviderMessageId,
                Status = MessageStatus.Failed,
                ProviderStatus = "30003",
                Detail = "unreachable destination handset",
                Suppresses = SuppressionReason.HardBounce,
                OccurredAt = TestClock.Instance.UtcNow
            }
        );

        (await cluster.Suppression(service).CheckAsync(ChannelKind.Sms, "+420777123456"))
            .GetValueOrThrow()
            .IsSuppressed
            .ShouldBeTrue("a carrier saying an address is permanently unreachable is exactly what the list is for");
    }
}
