using CyberCloud.Core.Contracts.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using System.Reflection;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     Every billing wire and state type through the silo's own serializer, and every one of them
///     named on the wire.
/// </summary>
/// <remarks>
///     ⚠ <b>The half of ADR-018's owed storage proof a codec can make.</b> In-memory grain storage
///     keeps the object graph, so <c>InvoicingTests</c> would pass over an invoice that could not be
///     written to PostgreSQL. A finalized invoice that fails to deserialize is a billing account that
///     has silently lost every document it issued, so the state types are round-tripped here with
///     every collection populated.
/// </remarks>
public sealed class BillingSerializationTests : IDisposable {
    readonly ServiceProvider provider;
    readonly Serializer serializer;

    public BillingSerializationTests() {
        var services = new ServiceCollection();
        services.AddSerializer(static builder => builder
                .AddAssembly(typeof(Invoice).Assembly)
                .AddAssembly(typeof(BillingAccountState).Assembly)
                .AddAssembly(typeof(UsageLedgerEntry).Assembly)
                .AddAssembly(typeof(ResultSurrogate).Assembly)
        );

        provider = services.BuildServiceProvider();
        serializer = provider.GetRequiredService<Serializer>();
    }

    public void Dispose() => provider.Dispose();

    [Fact]
    public void ABillingAccountWithAnInvoiceAndACreditNoteRoundTrips() {
        var invoice = new Invoice {
            InvoiceId = Guid.NewGuid(),
            Number = "CCT-INV-00000007",
            Status = InvoiceStatus.Finalized,
            TenantId = Guid.NewGuid(),
            PeriodStart = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEnd = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            Issuer = BillingCluster.Issuer,
            Customer = new() { LegalName = "Contoso GmbH", Country = "DE", VatId = "DE123456789", IsBusiness = true },
            Currency = "EUR",
            Lines = [
                new() {
                    SubscriptionId = Guid.NewGuid(),
                    Meter = BillingMeter.StorageGbMonths,
                    Unit = "GiB-month",
                    Quantity = 101.917808219178082191780822m,
                    Amount = 4.15m,
                    DeclaredQuantity = true,
                    Description = "StorageGbMonths"
                }
            ],
            Subtotal = 4.15m,
            Tax = new() { Treatment = TaxTreatment.ReverseCharge, Country = "DE", Note = "Reverse charge", VatIdCheck = "format" },
            Total = 4.15m,
            FinalizedAt = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero),
            PriceSheetVersions = [new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)],
            Notes = ["Reverse charge", "Storage quantities are declared"]
        };

        var state = new BillingAccountState {
            Profile = invoice.Customer,
            Configured = true,
            Subscriptions = [invoice.Lines[0].SubscriptionId],
            Invoices = [invoice],
            CreditNotes = [
                new() {
                    CreditNoteId = Guid.NewGuid(),
                    Number = "CCT-CN-00000001",
                    InvoiceNumber = invoice.Number,
                    Currency = "EUR",
                    Lines = [new() { LineIndex = 0, Meter = BillingMeter.StorageGbMonths, Amount = -1.00m }],
                    Subtotal = -1.00m,
                    Tax = invoice.Tax with { Base = -1.00m },
                    Total = -1.00m,
                    Reason = "resize",
                    ApprovedBy = "billing",
                    RequestId = "r-1"
                }
            ]
        };

        var back = RoundTrip(state);

        back.Invoices.ShouldHaveSingleItem();
        back.Invoices[0].Lines.ShouldBe(invoice.Lines);
        back.Invoices[0].Quantity().ShouldBe(invoice.Quantity(), "the meter's twelve-plus decimal places survive");
        back.Invoices[0].Notes.ShouldBe(invoice.Notes);
        back.Invoices[0].Tax.ShouldBe(invoice.Tax);
        back.CreditNotes.ShouldHaveSingleItem().Lines.ShouldBe(state.CreditNotes[0].Lines);
        back.Subscriptions.ShouldBe(state.Subscriptions);
    }

    [Fact]
    public void TheNumberingAndBudgetStatesRoundTrip() {
        var numbering = new InvoiceNumberingState();
        numbering.Series["cc-test|Invoice"] = new() {
            Allocated = 2,
            ByDocument = { ["a/2026-08"] = "CCT-INV-00000001", ["b/2026-08"] = "CCT-INV-00000002" },
            Unconfirmed = { ["CCT-INV-00000002"] = "b/2026-08" }
        };

        var budget = new BudgetState {
            Spec = new() {
                BudgetId = Guid.NewGuid(),
                Thresholds = [new() { Percent = 80m, Kind = ThresholdKind.Forecast }],
                Notification = new() { Recipients = ["a@example.com"], Channel = "email" }
            },
            Alerts = [new() { Percent = 80m, Kind = ThresholdKind.Forecast, Figure = 12.34m, Notification = "sent" }]
        };

        var numberingBack = RoundTrip(numbering).Series["cc-test|Invoice"];
        numberingBack.Allocated.ShouldBe(2);
        numberingBack.ByDocument.Count.ShouldBe(2);
        numberingBack.Unconfirmed.ShouldContainKey("CCT-INV-00000002");

        var budgetBack = RoundTrip(budget);
        budgetBack.Spec!.SameAs(budget.Spec).ShouldBeTrue();
        budgetBack.Alerts.ShouldBe(budget.Alerts);
    }

    /// <summary>
    ///     ⚠ Every serialized billing type carries an <c>[Alias]</c> under <c>CyberCloud.Billing.</c> —
    ///     the process-boundary rule a TestCluster hides (batch 3's post-merge defect, and #39's).
    /// </summary>
    [Fact]
    public void EveryWireTypeAndGrainInterfaceIsNamedOnTheWire() {
        var missing = new[] { typeof(Invoice).Assembly, typeof(BillingAccountState).Assembly }
            .SelectMany(static x => x.GetTypes())
            .Where(static x => x.GetCustomAttribute<GenerateSerializerAttribute>() is not null
                || (x.IsInterface && typeof(IGrain).IsAssignableFrom(x))
                || (x.IsEnum && x.Namespace == typeof(Invoice).Namespace))
            .Where(static x => x.GetCustomAttribute<AliasAttribute>() is not { } alias
                || !alias.Alias.StartsWith("CyberCloud.Billing.", StringComparison.Ordinal))
            .Select(static x => x.FullName)
            .ToList();

        missing.ShouldBeEmpty();
    }

    T RoundTrip<T>(T value) => serializer.Deserialize<T>(serializer.SerializeToArray(value));
}

static class InvoiceQuantities {
    public static decimal Quantity(this Invoice invoice) => invoice.Lines.Sum(static x => x.Quantity);
}
