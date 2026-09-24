using CyberCloud.Billing.Payments;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     The Stripe adapter against <c>stripe/stripe-mock</c> — Stripe's own mock, which validates every
///     request against Stripe's published OpenAPI document and answers with its fixtures.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What a green run proves, and what it cannot.</b> Every request the adapter makes is one
///         Stripe's API accepts — endpoint, method, parameter names and types, the idempotency and
///         version headers — and every answer is parsed into the provider-neutral shape. It does not
///         prove the flow: stripe-mock is stateless, so the setup intent it returns is a fixture and
///         not the one created, and a charge never declines. docs/plan/22 § What is owed,
///         <c>psp-flow-against-test-mode</c>.
///     </para>
///     <para>
///         ⚠ <b>No secret in the repository.</b> stripe-mock accepts any key that looks like a test
///         key, so the key is minted per run.
///     </para>
/// </remarks>
public sealed class StripePaymentServiceProviderTests : IAsyncLifetime {
    /// <summary>Pinned by tag; <c>stripe-mock -version</c> reports 0.203.0 for it.</summary>
    public const string Image = "stripe/stripe-mock:v0.203.0";

    readonly IContainer mock = new ContainerBuilder(Image)
        .WithPortBinding(12111, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening for HTTP"))
        .Build();

    readonly HttpClient http = new();
    StripePaymentServiceProvider stripe = null!;
    StripeOptions options = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        await mock.StartAsync(TestContext.Current.CancellationToken);

        options = new() {
            BaseAddress = new($"http://{mock.Hostname}:{mock.GetMappedPublicPort(12111)}/"),
            ApiKey = "sk_test_" + Guid.NewGuid().ToString("N")
        };

        stripe = new(http, options);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        http.Dispose();
        await mock.DisposeAsync();
    }

    [Fact]
    public async Task ACustomerIsCreatedAndItsIdIsAStripeCustomerId() {
        var customer = (await stripe.CreateCustomerAsync(Guid.NewGuid(), "Contoso GmbH", "billing@contoso.example", TestContext.Current.CancellationToken))
            .GetValueOrThrow();

        customer.Id.ShouldStartWith("cus_");
    }

    [Fact]
    public async Task ASetupIntentIsCreatedForOffSessionUseAndReadBack() {
        var created = (await stripe.CreateSetupIntentAsync("cus_test", TestContext.Current.CancellationToken)).GetValueOrThrow();
        var read = (await stripe.GetSetupIntentAsync(created.Id, TestContext.Current.CancellationToken)).GetValueOrThrow();

        created.Id.ShouldStartWith("seti_");
        created.ClientSecret.ShouldNotBeNullOrEmpty("the browser confirms the intent with it; card data never reaches the platform");
        created.Status.ShouldNotBeNullOrEmpty();
        read.Id.ShouldStartWith("seti_");
    }

    [Fact]
    public async Task PaymentMethodsAreListedAsIdsAndSummariesNeverAsCardNumbers() {
        var methods = (await stripe.ListPaymentMethodsAsync("cus_test", TestContext.Current.CancellationToken)).GetValueOrThrow();

        var method = methods.ShouldHaveSingleItem();
        method.Id.ShouldStartWith("pm_");
        method.Type.ShouldBe("card");
        method.Summary.ShouldMatch("^[a-z]+ ending [0-9]{4}$");
    }

    [Fact]
    public async Task AnInvoiceIsChargedOffSessionWithAnIdempotencyKey() {
        var payment = (await stripe.PayInvoiceAsync(
                new("cus_test", "pm_card_visa", 1234, "EUR", "CCT-INV-00000001", "invoice-CCT-INV-00000001"),
                TestContext.Current.CancellationToken
            ))
            .GetValueOrThrow();

        payment.Id.ShouldStartWith("pi_");
        payment.Status.ShouldNotBeNullOrEmpty();
        payment.Currency.ShouldBe(payment.Currency.ToUpperInvariant(), "parsed back into the platform's upper-case spelling");
    }

    [Fact]
    public async Task AChargeWithoutAnIdempotencyKeyIsRefusedBeforeStripeIsAsked() {
        var refused = await stripe.PayInvoiceAsync(new("cus_test", "pm_card_visa", 1234, "EUR", "CCT-INV-1", " "), TestContext.Current.CancellationToken);

        refused.Error!.Message.ShouldContain("charges twice");
    }

    [Fact]
    public async Task AKeyStripeRefusesIsAFailureCarryingStripesOwnSentence() {
        var wrong = new StripePaymentServiceProvider(new HttpClient(), new() { BaseAddress = options.BaseAddress, ApiKey = "not-a-stripe-key" });

        var refused = await wrong.CreateCustomerAsync(Guid.NewGuid(), "x", "x@example.com", TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("Stripe refused POST /v1/customers with 401");
    }

    [Fact]
    public void TheAdapterRefusesToExistWithoutAKey() =>
        Should.Throw<ArgumentException>(() => new StripePaymentServiceProvider(new HttpClient(), new()))
            .Message.ShouldContain("holds none by design");
}
