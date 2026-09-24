using CyberCloud.Billing.Tax;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     <see cref="EuVatTaxService" /> — the EU VAT decision for an electronically supplied service, over
///     the committed rate table. Its remarks carry the rules; each test is one of them.
/// </summary>
public sealed class TaxTests {
    static readonly EuVatTaxService Vat = EuVatTaxService.Committed;
    static readonly DateTimeOffset EndOfAugust = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1);

    static TaxQuote Quote(BillingProfile customer, decimal amount = 100m, DateTimeOffset? at = null) =>
        Vat.Quote(new(BillingCluster.Issuer, customer, amount, "EUR", at ?? EndOfAugust)).GetValueOrThrow();

    [Fact]
    public void TheCommittedTableKnowsTheTwentySevenMemberStates() {
        Vat.MemberStates.Count().ShouldBe(27);
        Vat.IsMemberState("GB").ShouldBeFalse("the United Kingdom left the EU VAT area in 2021");
    }

    [Fact]
    public void ADomesticConsumerPaysTheIssuersRate() {
        var quote = Quote(new() { LegalName = "Jan", Country = "CZ" });

        quote.Treatment.ShouldBe(TaxTreatment.Standard);
        quote.RatePercent.ShouldBe(21m);
        quote.Amount.ShouldBe(21m);
        quote.Note.ShouldBeEmpty();
    }

    [Fact]
    public void AConsumerInAnotherMemberStatePaysTheirOwnCountrysRate() {
        // Article 58: an electronically supplied service to a consumer is supplied where the consumer is.
        var quote = Quote(new() { LegalName = "Hans", Country = "DE" });

        quote.Treatment.ShouldBe(TaxTreatment.Standard);
        quote.Country.ShouldBe("DE");
        quote.RatePercent.ShouldBe(19m);
    }

    [Fact]
    public void ABusinessInAnotherMemberStateWithAVatNumberIsReverseCharged() {
        var quote = Quote(new() { LegalName = "Contoso GmbH", Country = "DE", VatId = "DE123456789", IsBusiness = true });

        quote.Treatment.ShouldBe(TaxTreatment.ReverseCharge);
        quote.Amount.ShouldBe(0m);
        quote.Note.ShouldContain("Article 196");
        quote.Note.ShouldContain("DE123456789");
        quote.Note.ShouldContain(BillingCluster.Issuer.VatId, Case.Sensitive, "the invoice carries both VAT numbers");
        quote.VatIdCheck.ShouldBe("format", "the number's existence was not checked — VIES is owed");
    }

    [Fact]
    public void ADomesticBusinessIsNotReverseCharged() =>
        Quote(new() { LegalName = "Firma s.r.o.", Country = "CZ", VatId = "CZ87654321", IsBusiness = true })
            .Treatment
            .ShouldBe(TaxTreatment.Standard);

    [Fact]
    public void ABusinessWhoseVatNumberHasTheWrongShapeIsChargedAndItsProfileRefused() {
        var profile = new BillingProfile { LegalName = "Contoso GmbH", Country = "DE", VatId = "123456789", IsBusiness = true };

        Quote(profile).Treatment.ShouldBe(TaxTreatment.Standard, "no reverse charge on a number that is not a German VAT number");
        Vat.CheckCustomer(profile).Error!.Target.ShouldBe("/vatId");
    }

    [Fact]
    public void GreeceIsPrefixedEl() {
        Vat.IsWellFormedVatId("GR", "EL123456789").ShouldBeTrue();
        Vat.IsWellFormedVatId("GR", "GR123456789").ShouldBeFalse();
    }

    [Fact]
    public void ACustomerOutsideTheEuIsOutOfScope() {
        var quote = Quote(new() { LegalName = "Acme Inc.", Country = "US", IsBusiness = true });

        quote.Treatment.ShouldBe(TaxTreatment.OutOfScope);
        quote.Amount.ShouldBe(0m);
        quote.Note.ShouldBe(EuVatTaxService.OutOfScopeNote);
    }

    [Fact]
    public void TheRateIsTheOneInForceAtTheEndOfThePeriod() {
        // Slovakia moved from 20 % to 23 % on 2025-01-01. December 2024 is supplied at 20 %, January at 23 %.
        var slovak = new BillingProfile { LegalName = "Jozef", Country = "SK" };
        var endOfDecember = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1);

        Quote(slovak, at: endOfDecember).RatePercent.ShouldBe(20m);
        Quote(slovak, at: endOfDecember.AddTicks(1)).RatePercent.ShouldBe(23m);
    }

    [Fact]
    public void TheTaxIsRoundedOnceHalfAwayFromZero() {
        // 21 % of 0.50 is 0.105 — 0.11, where banker's rounding would print 0.10.
        Quote(new() { LegalName = "Jan", Country = "CZ" }, 0.50m).Amount.ShouldBe(0.11m);
        Quote(new() { LegalName = "Jan", Country = "CZ" }, -0.50m).Amount.ShouldBe(-0.11m, "a credit note's tax is the mirror image");
    }

    [Fact]
    public void AnIssuerOutsideTheEuIsRefusedRatherThanGuessedFor() {
        var refused = Vat.Quote(new(BillingCluster.Issuer with { Country = "US" }, new() { Country = "DE" }, 10m, "EUR", EndOfAugust));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("not an EU member state");
    }

    [Fact]
    public void ARateTableOutOfDateOrderIsRefused() =>
        EuVatTaxService.Parse(
                """{ "rates": [ { "country": "EE", "vatPrefix": "EE", "from": "2025-07-01", "percent": 24 }, { "country": "EE", "vatPrefix": "EE", "from": "2024-01-01", "percent": 22 } ] }"""
            )
            .Error!.Message.ShouldContain("not in date order");
}
