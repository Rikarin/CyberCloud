using CyberCloud.Billing.Payments;
using System.Globalization;
using System.Security.Cryptography;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     Stripe webhook signature verification — nothing in a webhook is believed until this says so.
/// </summary>
/// <remarks>
///     ⚠ The signing secret is minted per run. A committed <c>whsec_</c> value would be a secret in the
///     repository whether or not any endpoint used it, and the scanner cannot tell the difference.
/// </remarks>
public sealed class WebhookSignatureTests {
    static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    readonly string secret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    const string Payload =
        """{"id":"evt_1","object":"event","type":"payment_intent.succeeded","created":1789041600,"data":{"object":{"id":"pi_1","object":"payment_intent"}}}""";

    StripePaymentServiceProvider Stripe(string? signing = null) =>
        new(new HttpClient(), new() { ApiKey = "sk_test_" + Guid.NewGuid().ToString("N"), WebhookSigningSecret = signing ?? secret });

    string Header(long timestamp, string payload, string? signing = null) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"t={timestamp},v1={Convert.ToHexStringLower(StripePaymentServiceProvider.Sign(signing ?? secret, timestamp, payload))}"
        );

    [Fact]
    public void AGenuineWebhookIsVerifiedAndParsed() {
        var verified = Stripe().VerifyWebhook(Payload, Header(Now.ToUnixTimeSeconds(), Payload), Now).GetValueOrThrow();

        verified.Id.ShouldBe("evt_1");
        verified.Type.ShouldBe("payment_intent.succeeded");
        verified.ObjectId.ShouldBe("pi_1");
    }

    [Fact]
    public void AnAlteredPayloadIsRefused() {
        var header = Header(Now.ToUnixTimeSeconds(), Payload);
        var altered = Payload.Replace("pi_1", "pi_2", StringComparison.Ordinal);

        Stripe().VerifyWebhook(altered, header, Now).Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
    }

    [Fact]
    public void AWebhookSignedWithAnotherSecretIsRefused() =>
        Stripe().VerifyWebhook(Payload, Header(Now.ToUnixTimeSeconds(), Payload, "whsec_" + Guid.NewGuid().ToString("N")), Now).IsFailure.ShouldBeTrue();

    [Theory]
    [InlineData(301)]
    [InlineData(-301)]
    public void ASignatureOutsideTheToleranceIsARefusedReplay(int secondsAgo) {
        var header = Header(Now.AddSeconds(-secondsAgo).ToUnixTimeSeconds(), Payload);

        Stripe().VerifyWebhook(Payload, header, Now).Error!.Message.ShouldContain("replay");
    }

    [Fact]
    public void ASignatureAtTheToleranceIsAccepted() =>
        Stripe().VerifyWebhook(Payload, Header(Now.AddSeconds(-300).ToUnixTimeSeconds(), Payload), Now).IsSuccess.ShouldBeTrue();

    [Fact]
    public void WhileASecretIsRolledEitherSignatureIsEnough() {
        var timestamp = Now.ToUnixTimeSeconds();
        var old = Convert.ToHexStringLower(StripePaymentServiceProvider.Sign("whsec_" + Guid.NewGuid().ToString("N"), timestamp, Payload));
        var current = Convert.ToHexStringLower(StripePaymentServiceProvider.Sign(secret, timestamp, Payload));

        Stripe().VerifyWebhook(Payload, $"t={timestamp},v1={old},v1={current}", Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1=abcd")]
    [InlineData("t=1789041600")]
    [InlineData("t=abc,v1=zz")]
    public void AHeaderWithoutATimestampAndASignatureIsRefused(string header) =>
        Stripe().VerifyWebhook(Payload, header, Now).Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

    [Fact]
    public void AnEndpointWithNoSigningSecretBelievesNothing() =>
        Stripe(signing: "").VerifyWebhook(Payload, Header(Now.ToUnixTimeSeconds(), Payload), Now).IsFailure.ShouldBeTrue();
}
