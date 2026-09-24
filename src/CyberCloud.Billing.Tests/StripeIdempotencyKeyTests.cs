using CyberCloud.Billing.Payments;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     The customer-creation key, without a container: stripe-mock is stateless and ignores the key, so
///     only the key itself can be checked.
/// </summary>
public sealed class StripeIdempotencyKeyTests {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void ARetryOfTheSameCustomerSendsTheSameKey() =>
        StripePaymentServiceProvider.CustomerIdempotencyKey(Tenant, "Contoso GmbH", "billing@contoso.example")
            .ShouldBe(StripePaymentServiceProvider.CustomerIdempotencyKey(Tenant, "Contoso GmbH", "billing@contoso.example"));

    /// <summary>⚠ Stripe refuses a reused key whose parameters changed, so a corrected email needs a new one.</summary>
    [Fact]
    public void AChangedNameOrEmailSendsANewKeyForTheSameTenant() {
        var first = StripePaymentServiceProvider.CustomerIdempotencyKey(Tenant, "Contoso GmbH", "billing@contoso.example");

        StripePaymentServiceProvider.CustomerIdempotencyKey(Tenant, "Contoso GmbH", "finance@contoso.example").ShouldNotBe(first);
        StripePaymentServiceProvider.CustomerIdempotencyKey(Tenant, "Contoso AG", "billing@contoso.example").ShouldNotBe(first);
        first.ShouldStartWith($"customer-{Tenant:N}-");
    }
}
