namespace CyberCloud.Communication.Tests;

/// <summary>
///     docs/plan/17 § The parts that are actually the work:
///     <i>
///         "Every send carries a client-supplied
///         key; a retry after a timeout must not send twice. An OTP sent twice is confusing; an invoice
///         notice sent twice is a support call."
///     </i>
/// </summary>
[Collection(CommunicationClusterFixture.Name)]
public sealed class IdempotencyTests(CommunicationCluster cluster) {
    // ── FAILURE CLASS: the same key sends once, even across a grain deactivation ────────────────

    [Fact]
    public async Task TheSameIdempotencyKeySendsOnce() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var first = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-42")
        );

        var second = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-42")
        );

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();

        second.GetValueOrThrow()
            .MessageId
                .ShouldBe(first.GetValueOrThrow().MessageId, "a retry is the same message, not a second one");

        TestProviders.Sms.Sent.Count.ShouldBe(1);
        TestProviders.Sms.Calls.ShouldBe(1, "the second send must not have reached the carrier at all");
    }

    [Fact]
    public async Task TheSameIdempotencyKeyStillSendsOnceAcrossAGrainDeactivation() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var first = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "invoice-2026-08")
        );

        // ⚠ The realistic shape of the failure. A client times out, the silo collects the
        // activation, and the retry arrives at a cold grain — which is exactly when an in-memory
        // "have I seen this key" set would have forgotten, and would send an invoice notice twice.
        await cluster.Message(service, "invoice-2026-08").DeactivateAsync();

        var afterRestart = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "invoice-2026-08")
        );

        afterRestart.GetValueOrThrow().MessageId.ShouldBe(first.GetValueOrThrow().MessageId);
        TestProviders.Sms.Sent.Count.ShouldBe(1);
        TestProviders.Sms.Calls.ShouldBe(1);
    }

    // ── FAILURE CLASS: a DIFFERENT key sends twice ─────────────────────────────────────────────
    //
    // ⚠ The other half, and the more important one to keep. An idempotency check that swallows
    // genuine sends is worse than a duplicate: a duplicate is a support call, a swallowed OTP is a
    // user who cannot sign in and a platform that says everything worked.

    [Fact]
    public async Task ADifferentIdempotencyKeySendsTwice() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var first = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-42")
        );

        var second = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-43")
        );

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.GetValueOrThrow().MessageId.ShouldNotBe(first.GetValueOrThrow().MessageId);

        TestProviders.Sms.Sent.Count.ShouldBe(2, "two genuine sends are two messages");
        TestProviders.Sms.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ADifferentMessageUnderOneKeyIsAConflictRatherThanASilentReplay() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        _ = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-42")
        );

        var different = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-42", "+420777999999")
        );

        different.IsFailure.ShouldBeTrue(
            "answering with the first message's status would hide a caller generating keys wrongly "
            + "until somebody asked why the second never arrived"
        );

        different.Error!.Code.ShouldBe(ErrorCode.Conflict);
        TestProviders.Sms.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ASendWithNoIdempotencyKeyIsRefused() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, string.Empty)
        );

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        TestProviders.Sms.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task TwoTenantsUsingTheSameIdempotencyKeyDoNotCollide() {
        CommunicationCluster.ResetDoubles();

        var mine = await cluster.NewServiceAsync();
        var theirs = await cluster.NewServiceAsync(tenant: CommunicationCluster.OtherTenant);

        _ = await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(mine, "otp-1"));
        _ = await cluster.SendAsync(
            CommunicationCluster.OtherTenant,
            CommunicationCluster.Request(theirs, "otp-1")
        );

        TestProviders.Sms.Sent.Count.ShouldBe(
            2,
            "one tenant's idempotency key must never suppress another tenant's message — the key is "
            + "tenant-qualified through Orleans.Multitenant AND service-scoped in the derivation"
        );
    }

    [Fact]
    public async Task AStatusReadAnswersDidItArrive() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        _ = await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "otp-9"));

        var status = await cluster.StatusAsync(CommunicationCluster.Tenant, service, "otp-9");

        status.GetValueOrThrow().Status.ShouldBe(MessageStatus.Dispatched);
        status.GetValueOrThrow().ProviderMessageId.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AStatusReadForAKeyNothingWasSentUnderIsNotFound() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        (await cluster.StatusAsync(CommunicationCluster.Tenant, service, "never-sent"))
            .Error!
            .Code
            .ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AMessageGrainAddressedWithAMismatchedKeyRefusesRatherThanSending() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        // Addressed as "otp-A", carrying "otp-B". Idempotency against a message nobody can find
        // again is worse than none, so the grain refuses rather than recording it.
        var refused = await cluster.Message(service, "otp-A")
            .SendAsync(CommunicationCluster.Request(service, "otp-B"));

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidGrainKey);
        TestProviders.Sms.Calls.ShouldBe(0);
    }

    // ── FAILURE CLASS: a carrier that never answered is neither sent nor failed ─────────────────

    [Fact]
    public async Task ACarrierThatNeverAnsweredLeavesTheMessageQueuedForADeliberateRetry() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        var request = CommunicationCluster.Request(service, "invoice-2026-09");

        TestProviders.Sms.TimeOut = true;

        var stuck = (await cluster.SendAsync(CommunicationCluster.Tenant, request)).GetValueOrThrow();

        // ⚠ Queued and not Failed. Failed says "the carrier said no"; a carrier that said nothing may
        // have queued the message, and the grain does not know — which is exactly the ambiguity
        // IMessageGrain.RetryAsync exists to hand to a caller who does.
        stuck.Status.ShouldBe(MessageStatus.Queued, stuck.Detail);
        stuck.ProviderMessageId.ShouldBeEmpty();
        stuck.Detail.ShouldContain(
            "never answered",
            Case.Sensitive,
            "the carrier's own sentence is what a status reader sees"
        );

        // The reservation stays held: the message may have left, so the day's cap counts it.
        (await cluster.Limits(service).ReadAsync(ChannelKind.Sms)).GetValueOrThrow().Messages.ShouldBe(1);

        // A plain retry under the same key is the idempotency guarantee at work: nothing reaches the
        // carrier a second time without somebody deciding it should.
        var again = (await cluster.SendAsync(CommunicationCluster.Tenant, request)).GetValueOrThrow();
        again.Status.ShouldBe(MessageStatus.Queued);
        TestProviders.Sms.Calls.ShouldBe(1, "SendAsync must not re-drive an ambiguous message on its own");

        // The deliberate one. The carrier is back, the caller says a second copy is acceptable.
        TestProviders.Sms.TimeOut = false;

        var redriven = (await cluster.Message(service, request.IdempotencyKey).RetryAsync(request)).GetValueOrThrow();
        redriven.Status.ShouldBe(MessageStatus.Dispatched, redriven.Detail);
        redriven.MessageId.ShouldBe(stuck.MessageId, "a retry is the same message, not a second one");
        redriven.ProviderMessageId.ShouldNotBeEmpty();
        TestProviders.Sms.Calls.ShouldBe(2);

        // Counted once: the retry gave back the reservation the first attempt held before making its own.
        (await cluster.Limits(service).ReadAsync(ChannelKind.Sms)).GetValueOrThrow().Messages.ShouldBe(1);
    }

    [Fact]
    public async Task ACarrierThatSaidNoIsFailedAndCannotBeRetried() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        var request = CommunicationCluster.Request(service, "invoice-2026-10");

        TestProviders.Sms.Fail = true;

        (await cluster.SendAsync(CommunicationCluster.Tenant, request)).GetValueOrThrow()
            .Status.ShouldBe(MessageStatus.Failed);

        TestProviders.Sms.Fail = false;

        // ⚠ The asymmetry with the test above is the contract: a refusal has a known outcome, and
        // re-driving a known outcome is how a second copy gets sent.
        var refused = await cluster.Message(service, request.IdempotencyKey).RetryAsync(request);
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("only a Queued one can be re-driven");
        TestProviders.Sms.Calls.ShouldBe(1);
    }
}
