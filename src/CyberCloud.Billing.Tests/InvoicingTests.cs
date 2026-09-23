namespace CyberCloud.Billing.Tests;

/// <summary>
///     The monthly invoice grain, against the real usage ledger — docs/plan/22 § Invoicing and payment:
///     a draft accrues, a finalized invoice is immutable and numbered without gaps, and a correction
///     is a credit note.
/// </summary>
[Collection(BillingClusterFixture.Name)]
public sealed class InvoicingTests(BillingCluster cluster) : IAsyncLifetime {
    static readonly DateTimeOffset August = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every test starts at <see cref="TestClock.Start" />, whatever the one before it did to the clock.</summary>
    public ValueTask InitializeAsync() {
        TestClock.Instance.Reset();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        TestClock.Instance.Reset();
        return ValueTask.CompletedTask;
    }
    static readonly DateTimeOffset September = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    static readonly BillingProfile Czech = new() { LegalName = "Firma s.r.o.", Country = "CZ", Currency = "EUR" };

    [Fact]
    public async Task TheDraftAccruesAsTheLedgerDoes() {
        TestClock.Instance.Reset();
        var (tenant, subscription) = await ConfiguredAsync(Czech);
        var widget = Guid.NewGuid();
        var path = BillingCluster.PathOf(tenant, subscription, "prod", "w");

        await cluster.UseHoursAsync(tenant, subscription, widget, path, BillingMeter.VCpuHours, September, 10, 2m);
        var first = (await cluster.Account(tenant).PreviewAsync(September)).GetValueOrThrow();

        await cluster.UseHoursAsync(tenant, subscription, widget, path, BillingMeter.VCpuHours, September.AddHours(10), 10, 2m);
        var second = (await cluster.Account(tenant).PreviewAsync(September)).GetValueOrThrow();

        first.Status.ShouldBe(InvoiceStatus.Draft);
        first.Number.ShouldBeEmpty("a draft has no number — numbers are allocated at finalization only");
        first.Lines.ShouldHaveSingleItem().Amount.ShouldBe(0.50m, "20 vCPU-hours at 0.025");
        second.Lines.ShouldHaveSingleItem().Amount.ShouldBe(1.00m, "the draft is the ledger rated now, so it grew with it");
        second.Tax.RatePercent.ShouldBe(21m);
        second.Total.ShouldBe(1.21m);
    }

    [Fact]
    public async Task AMonthCannotBeFinalizedInsideItsLateUsageWindow() {
        var (tenant, _) = await ConfiguredAsync(Czech);

        // One tick before the window ends: refused.
        TestClock.Instance.Set(September + IBillingAccountGrain.LateUsageWindow - TimeSpan.FromTicks(1));
        var early = await cluster.Account(tenant).FinalizeAsync(August);

        TestClock.Instance.Set(September + IBillingAccountGrain.LateUsageWindow);
        var onTime = await cluster.Account(tenant).FinalizeAsync(August);
        TestClock.Instance.Reset();

        early.Error!.Code.ShouldBe(ErrorCode.Conflict);
        early.Error.Message.ShouldContain("48-hour late-usage window");
        onTime.IsSuccess.ShouldBeTrue(onTime.Error?.Message);
    }

    [Fact]
    public async Task AFinalizedInvoiceIsNumberedStoredAndNeverChangedByLaterUsage() {
        TestClock.Instance.Reset();
        var (tenant, subscription) = await ConfiguredAsync(Czech);
        var disk = Guid.NewGuid();
        var path = BillingCluster.PathOf(tenant, subscription, "prod", "disk", "CyberCloud.Compute/disks");

        // 100 GiB for all of August, one hourly entry at a time: 744 × 100 / 730 GiB-months.
        await cluster.UseHoursAsync(tenant, subscription, disk, path, BillingMeter.StorageGbMonths, August, 744, 100m / 730m);

        var finalized = (await cluster.Account(tenant).FinalizeAsync(August)).GetValueOrThrow();

        // Late usage lands after finalization, for August.
        var late = await cluster.UseAsync(tenant, subscription, disk, path, BillingMeter.PublicIpHours, August.AddDays(20), 1m);

        var again = (await cluster.Account(tenant).FinalizeAsync(August)).GetValueOrThrow();
        var reread = (await cluster.Account(tenant).GetInvoiceAsync(finalized.Number)).GetValueOrThrow();

        finalized.Status.ShouldBe(InvoiceStatus.Finalized);
        finalized.Number.ShouldStartWith("CCT-INV-");
        again.InvoiceId.ShouldBe(finalized.InvoiceId, "finalizing twice answers the stored invoice");
        reread.Lines.Length.ShouldBe(1, "the late IP hour is not on the finalized invoice");
        reread.Total.ShouldBe(finalized.Total);
        late.Quantity.ShouldBe(1m);

        var storage = reread.Lines.Single();
        storage.DeclaredQuantity.ShouldBeTrue();
        reread.Notes.ShouldContain(Pricing.InvoiceBuilder.DeclaredQuantityNote);

        // 101.917… GiB-months: 50 free, the rest at 0.08.
        storage.Amount.ShouldBe(MoneyRounding.Round((storage.Quantity - 50m) * 0.080m, "EUR").GetValueOrThrow());
    }

    [Fact]
    public async Task TwoAccountsFinalizingGetConsecutiveNumbersAndARetryGetsTheSameOne() {
        var (first, _) = await ConfiguredAsync(Czech);
        var (second, _) = await ConfiguredAsync(Czech);
        var before = (await cluster.Numbering.AuditAsync(BillingCluster.Issuer.Code, DocumentSeries.Invoice)).GetValueOrThrow().Allocated;

        // ⚠ The crash between allocation and write, simulated: the numbering grain has already handed
        // the second account its number for August when the finalization runs.
        var preallocated = (await cluster.Numbering.AllocateAsync(BillingCluster.Issuer, DocumentSeries.Invoice, $"{second:N}/2026-08"))
            .GetValueOrThrow();

        var a = (await cluster.Account(first).FinalizeAsync(August)).GetValueOrThrow();
        var b = (await cluster.Account(second).FinalizeAsync(August)).GetValueOrThrow();
        var audit = (await cluster.Numbering.AuditAsync(BillingCluster.Issuer.Code, DocumentSeries.Invoice)).GetValueOrThrow();

        b.Number.ShouldBe(preallocated, "the retry of a finalization gets the number it was already given");
        Sequence(a.Number).ShouldBe(before + 2);
        Sequence(b.Number).ShouldBe(before + 1);
        audit.Allocated.ShouldBe(before + 2, "two invoices, two numbers — no gap");
        audit.Unconfirmed.ShouldNotContain(a.Number);
        audit.Unconfirmed.ShouldNotContain(b.Number);
    }

    [Fact]
    public async Task TenConcurrentFinalizationsGetTenConsecutiveNumbers() {
        var accounts = new List<Guid>();
        for (var i = 0; i < 10; i++) {
            accounts.Add((await ConfiguredAsync(Czech)).Tenant);
        }

        var before = (await cluster.Numbering.AuditAsync(BillingCluster.Issuer.Code, DocumentSeries.Invoice)).GetValueOrThrow().Allocated;
        var invoices = await Task.WhenAll(accounts.Select(x => cluster.Account(x).FinalizeAsync(August)));

        invoices.Select(static x => Sequence(x.GetValueOrThrow().Number))
            .Order()
            .ShouldBe(Enumerable.Range(1, 10).Select(x => before + x));
    }

    [Fact]
    public async Task AReverseChargedInvoiceSaysSoAndChargesNoVat() {
        var (tenant, subscription) = await ConfiguredAsync(
            new() { LegalName = "Contoso GmbH", Country = "DE", VatId = "DE123456789", IsBusiness = true, Currency = "EUR" }
        );
        await cluster.UseHoursAsync(tenant, subscription, Guid.NewGuid(), BillingCluster.PathOf(tenant, subscription, "prod", "w"), BillingMeter.VCpuHours, August, 100, 4m);

        var invoice = (await cluster.Account(tenant).FinalizeAsync(August)).GetValueOrThrow();

        invoice.Subtotal.ShouldBe(10.00m);
        invoice.Tax.Treatment.ShouldBe(TaxTreatment.ReverseCharge);
        invoice.Tax.Amount.ShouldBe(0m);
        invoice.Total.ShouldBe(10.00m);
        invoice.Notes.ShouldContain(x => x.Contains("Article 196", StringComparison.Ordinal));
        invoice.Issuer.VatId.ShouldBe(BillingCluster.Issuer.VatId);
    }

    // ── Corrections: by credit note only ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ALedgerCorrectionAfterFinalizationBecomesACreditNoteAndTheInvoiceIsUntouched() {
        var (tenant, subscription) = await ConfiguredAsync(Czech);
        var vm = Guid.NewGuid();
        var path = BillingCluster.PathOf(tenant, subscription, "prod", "vm", "CyberCloud.Compute/virtualMachines");
        var entries = new List<UsageLedgerEntry>();

        for (var hour = 0; hour < 40; hour++) {
            entries.Add(await cluster.UseAsync(tenant, subscription, vm, path, BillingMeter.VCpuHours, August.AddHours(hour), 4m));
        }

        var invoice = (await cluster.Account(tenant).FinalizeAsync(August)).GetValueOrThrow();
        invoice.Subtotal.ShouldBe(4.00m);

        // Half of it was double-counted: the ledger is corrected, which is the only way it changes.
        foreach (var entry in entries.Take(20)) {
            (await cluster.Ledger(tenant, subscription).AppendCorrectionAsync(entry.EntryId, -4m, "the sampler double-counted a resize")).IsSuccess.ShouldBeTrue();
        }

        var proposal = (await cluster.Account(tenant).ProposeCorrectionAsync(invoice.Number)).GetValueOrThrow();
        proposal.Credits.ShouldHaveSingleItem().Amount.ShouldBe(2.00m);
        proposal.Underbilled.ShouldBeEmpty();

        var note = (await cluster.Account(tenant).IssueCreditNoteAsync(
                new() {
                    InvoiceNumber = invoice.Number,
                    Lines = proposal.Credits,
                    Reason = "the sampler double-counted a resize",
                    ApprovedBy = "billing@cybercloud.test",
                    RequestId = "correction-1"
                }
            ))
            .GetValueOrThrow();

        note.Number.ShouldStartWith("CCT-CN-");
        note.Subtotal.ShouldBe(-2.00m);
        note.Tax.Amount.ShouldBe(-0.42m, "the invoice's own rate, 21 %");
        note.Total.ShouldBe(-2.42m);

        // ⚠ THE INVOICE IS BYTE-FOR-BYTE WHAT IT WAS. Not edited, not marked, not superseded.
        // (Equivalence rather than equality: a record compares its ImmutableArray members by reference.)
        (await cluster.Account(tenant).GetInvoiceAsync(invoice.Number)).GetValueOrThrow().ShouldBeEquivalentTo(invoice);

        // And the proposal now proposes nothing: the credit note is counted against the line.
        (await cluster.Account(tenant).ProposeCorrectionAsync(invoice.Number)).GetValueOrThrow().Credits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACreditNoteCannotCreditMoreThanTheLineStillCarries() {
        var invoice = await InvoicedAsync(4.00m);
        var account = cluster.Account(invoice.TenantId);

        (await account.IssueCreditNoteAsync(Credit(invoice, 3.00m, "one"))).IsSuccess.ShouldBeTrue();

        var refused = await account.IssueCreditNoteAsync(Credit(invoice, 1.01m, "two"));

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("1.00 EUR");
        (await account.IssueCreditNoteAsync(Credit(invoice, 1.00m, "three"))).IsSuccess.ShouldBeTrue("exactly what is left is allowed");
    }

    [Fact]
    public async Task ACreditNoteWithoutAReasonOrAnApproverIsRefused() {
        var invoice = await InvoicedAsync(4.00m);
        var account = cluster.Account(invoice.TenantId);

        (await account.IssueCreditNoteAsync(Credit(invoice, 1m, "a") with { Reason = " " })).Error!.Target.ShouldBe("/reason");
        (await account.IssueCreditNoteAsync(Credit(invoice, 1m, "b") with { ApprovedBy = "" })).Error!.Target.ShouldBe("/approvedBy");
    }

    [Fact]
    public async Task ARetriedCreditNoteRequestIssuesOneCreditNote() {
        var invoice = await InvoicedAsync(4.00m);
        var account = cluster.Account(invoice.TenantId);

        var first = (await account.IssueCreditNoteAsync(Credit(invoice, 1m, "same"))).GetValueOrThrow();
        var retried = (await account.IssueCreditNoteAsync(Credit(invoice, 1m, "same"))).GetValueOrThrow();

        retried.Number.ShouldBe(first.Number);
        (await account.ListCreditNotesAsync()).GetValueOrThrow().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ADraftCannotBeCreditedOnlyCorrectedAtTheLedger() {
        var (tenant, _) = await ConfiguredAsync(Czech);

        var refused = await cluster.Account(tenant).IssueCreditNoteAsync(
            new() { InvoiceNumber = "CCT-INV-99999999", Lines = [new() { LineIndex = 0, Amount = 1m }], Reason = "r", ApprovedBy = "a", RequestId = "x" }
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── The account ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASubscriptionTheTenantDoesNotHoldCannotBeAttached() {
        var (tenant, _) = await cluster.NewSubscriptionAsync("prod");
        var (_, someoneElses) = await cluster.NewSubscriptionAsync("prod");

        var refused = await cluster.Account(tenant).AttachSubscriptionAsync(someoneElses);

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AnAccountInvoicingInACurrencyNoMeterIsPricedInCannotBeFinalized() {
        var (tenant, subscription) = await ConfiguredAsync(Czech with { Currency = "USD" });
        await cluster.UseAsync(tenant, subscription, Guid.NewGuid(), BillingCluster.PathOf(tenant, subscription, "prod", "w"), BillingMeter.VCpuHours, August, 1m);

        var refused = await cluster.Account(tenant).FinalizeAsync(August);

        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("does not convert currencies");
    }

    [Fact]
    public async Task AnUnconfiguredAccountCannotFinalize() {
        TestClock.Instance.Reset();
        var (tenant, _) = await cluster.NewSubscriptionAsync("prod");

        (await cluster.Account(tenant).FinalizeAsync(August)).Error!.Message.ShouldContain("no profile");
    }

    [Fact]
    public async Task AMalformedVatNumberIsRefusedWhenItIsTypedNotAtTheMonthsClose() {
        var (tenant, _) = await cluster.NewSubscriptionAsync("prod");

        var refused = await cluster.Account(tenant).ConfigureAsync(Czech with { Country = "DE", VatId = "DE 123 456 789", IsBusiness = true });

        refused.Error!.Target.ShouldBe("/vatId");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    async Task<(Guid Tenant, Guid Subscription)> ConfiguredAsync(BillingProfile profile) {
        var (tenant, subscription) = await cluster.NewSubscriptionAsync("prod");
        var account = cluster.Account(tenant);

        (await account.ConfigureAsync(profile)).IsSuccess.ShouldBeTrue();
        (await account.AttachSubscriptionAsync(subscription)).IsSuccess.ShouldBeTrue();

        return (tenant, subscription);
    }

    /// <summary>A finalized August invoice with one vCPU line worth <paramref name="amount" />.</summary>
    async Task<Invoice> InvoicedAsync(decimal amount) {
        var (tenant, subscription) = await ConfiguredAsync(Czech);
        await cluster.UseAsync(tenant, subscription, Guid.NewGuid(), BillingCluster.PathOf(tenant, subscription, "prod", "w"), BillingMeter.VCpuHours, August, amount / 0.025m);

        TestClock.Instance.Reset();
        return (await cluster.Account(tenant).FinalizeAsync(August)).GetValueOrThrow();
    }

    static CreditNoteRequest Credit(Invoice invoice, decimal amount, string requestId) =>
        new() {
            InvoiceNumber = invoice.Number,
            Lines = [new() { LineIndex = 0, Amount = amount }],
            Reason = "goodwill after an outage",
            ApprovedBy = "billing@cybercloud.test",
            RequestId = requestId
        };

    static long Sequence(string number) => long.Parse(number[^8..], System.Globalization.CultureInfo.InvariantCulture);
}
