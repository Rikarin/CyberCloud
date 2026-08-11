namespace CyberCloud.Communication.Tests;

/// <summary>
///     docs/plan/17 § The parts that are actually the work: <i>"Bounces, complaints, opt-outs — per
///     tenant, honoured before dispatch. Ignoring a complaint is how a sending domain gets
///     blocked."</i>
/// </summary>
[Collection(CommunicationClusterFixture.Name)]
public sealed class SuppressionTests(CommunicationCluster cluster) {
    // ── FAILURE CLASS: a suppressed address is refused BEFORE dispatch ──────────────────────────

    [Fact]
    public async Task ASuppressedAddressIsRefusedAndTheProviderIsNeverCalled() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        (await cluster.Suppression(service)
            .SuppressAsync(ChannelKind.Sms, "+420777123456", SuppressionReason.Complaint, "marked as spam"))
            .IsSuccess.ShouldBeTrue();

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1", "+420777123456")
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("suppression list");

        // ⚠ THE ASSERTION THIS SUITE EXISTS FOR. Not "nothing was sent" — "the provider was never
        // asked". A provider that was called and refused has already seen the address, and on a real
        // carrier that is a billable API call and a log line naming somebody who opted out.
        TestProviders.Sms.Calls.ShouldBe(0);
        TestProviders.Sms.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task SuppressionMatchesAcrossSpellingsOfTheSameNumber() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        _ = await cluster.Suppression(service)
            .SuppressAsync(ChannelKind.Sms, "+420 777 123 456", SuppressionReason.OptOut, "STOP");

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1", "+420-777-123-456")
        );

        refused.Error!.Code.ShouldBe(
            ErrorCode.PolicyViolation,
            "a spelling that does not collapse onto the stored entry is a way around an opt-out"
        );

        TestProviders.Sms.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task SuppressionIsPerChannel() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(ChannelKind.Sms);

        _ = await cluster.Service(service)
            .ConfigureChannelAsync(
                new() {
                    Channel = ChannelKind.Email,
                    Provider = "in-memory",
                    Credentials = new() { Mode = CredentialMode.PlatformAccount },
                    Limits = new() { MaxMessagesPerWindow = 100, MaxSpendPerWindow = 100m },
                    EstimatedUnitCost = 0.001m,
                    Enabled = true
                }
            );

        _ = await cluster.Suppression(service)
            .SuppressAsync(ChannelKind.Email, "alice@example.com", SuppressionReason.HardBounce, "550");

        // An email bounce says nothing about a phone number, and vice versa.
        var sms = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        sms.IsSuccess.ShouldBeTrue();
        TestProviders.Sms.Calls.ShouldBe(1);
        TestProviders.Email.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task SuppressionIsPerServiceAndDoesNotLeakBetweenTenants() {
        CommunicationCluster.ResetDoubles();

        var mine = await cluster.NewServiceAsync();
        var theirs = await cluster.NewServiceAsync(tenant: CommunicationCluster.OtherTenant);

        _ = await cluster.Suppression(mine)
            .SuppressAsync(ChannelKind.Sms, "+420777123456", SuppressionReason.OptOut, "STOP");

        var otherTenant = await cluster.SendAsync(
            CommunicationCluster.OtherTenant,
            CommunicationCluster.Request(theirs, "otp-1", "+420777123456")
        );

        otherTenant.IsSuccess.ShouldBeTrue(
            "docs/plan/17 makes the suppression list per tenant. An opt-out is consent withdrawn from "
            + "one sender, not a global do-not-call registry"
        );
    }

    [Fact]
    public async Task SuppressingTwiceIsNotAConflictAndKeepsTheOriginalTimestamp() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var first = await cluster.Suppression(service)
            .SuppressAsync(ChannelKind.Sms, "+420777123456", SuppressionReason.OptOut, "STOP");

        TestClock.Instance.Advance(TimeSpan.FromHours(2));

        var again = await cluster.Suppression(service)
            .SuppressAsync(ChannelKind.Sms, "+420777123456", SuppressionReason.OptOut, "STOP again");

        again.IsSuccess.ShouldBeTrue("a carrier redelivers its webhooks and a handset re-sends STOP");
        again.GetValueOrThrow()
            .SuppressedAt
            .ShouldBe(
                first.GetValueOrThrow().SuppressedAt,
                "the opt-out happened once; a second copy of the same event must not restart the clock"
            );
    }

    // ── The refusal that is the point of ReleaseAsync ───────────────────────────────────────────

    [Fact]
    public async Task ATenantCannotClearAComplaintOrAnOptOut() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        var list = cluster.Suppression(service);

        _ = await list.SuppressAsync(ChannelKind.Sms, "+420777000001", SuppressionReason.Complaint, "spam");
        _ = await list.SuppressAsync(ChannelKind.Sms, "+420777000002", SuppressionReason.OptOut, "STOP");

        foreach (var number in new[] { "+420777000001", "+420777000002" }) {
            var refused = await list.ReleaseAsync(ChannelKind.Sms, number, "customer asked us to");

            refused.Error!
                .Code
                .ShouldBe(
                    ErrorCode.PolicyViolation,
                    "a tenant who could clear these could un-unsubscribe their own recipients, which "
                    + "is both what regulators write rules about and what gets a sending domain blocked"
                );
        }
    }

    [Fact]
    public async Task ATenantCanClearABounceOrABlockBecauseThoseAreFactsRatherThanDecisions() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        var list = cluster.Suppression(service);

        _ = await list.SuppressAsync(ChannelKind.Sms, "+420777000003", SuppressionReason.HardBounce, "unknown");
        (await list.ReleaseAsync(ChannelKind.Sms, "+420777000003", "typo corrected")).IsSuccess.ShouldBeTrue();

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1", "+420777000003")
        );

        sent.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ReleasingWithNoReasonIsRefused() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        var list = cluster.Suppression(service);

        _ = await list.SuppressAsync(ChannelKind.Sms, "+420777000004", SuppressionReason.ManualBlock, "abuse hold");

        (await list.ReleaseAsync(ChannelKind.Sms, "+420777000004", "  ")).Error!
            .Code
            .ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task TheListCanBeReadAndCounted() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        var list = cluster.Suppression(service);

        _ = await list.SuppressAsync(ChannelKind.Sms, "+420777000005", SuppressionReason.OptOut, "STOP");
        _ = await list.SuppressAsync(ChannelKind.Email, "bob@example.com", SuppressionReason.Complaint, "spam");

        (await list.CountAsync()).GetValueOrThrow().ShouldBe(2);
        (await list.ListAsync(ChannelKind.Sms)).GetValueOrThrow().Length.ShouldBe(1);
        (await list.ListAsync(ChannelKind.Unknown)).GetValueOrThrow().Length.ShouldBe(2);
    }

    [Fact]
    public async Task ASuppressionNeedsAReason() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        (await cluster.Suppression(service)
            .SuppressAsync(ChannelKind.Sms, "+420777000006", SuppressionReason.Unknown, string.Empty))
            .Error!
            .Code
            .ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task ASuppressedSendCanSucceedOnceTheAddressIsReleased() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        var list = cluster.Suppression(service);

        _ = await list.SuppressAsync(ChannelKind.Sms, "+420777000007", SuppressionReason.HardBounce, "unknown");

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-77", "+420777000007")
        );

        refused.IsFailure.ShouldBeTrue();

        _ = await list.ReleaseAsync(ChannelKind.Sms, "+420777000007", "typo corrected");

        // ⚠ A refusal is re-attemptable under the same key, and a dispatch is not. Nothing left the
        // platform, so re-running the checks is free — and a caller whose key is derived from the
        // thing being notified about cannot invent a new one just to get past a fixed problem.
        var retried = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-77", "+420777000007")
        );

        retried.IsSuccess.ShouldBeTrue();
        TestProviders.Sms.Sent.Count.ShouldBe(1);
    }
}
