using CyberCloud.Billing.Pricing;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Billing.Grains;

/// <summary>
///     <see cref="IBillingAccountGrain" /> — Coordinator, Durable, key <c>tenant/{tenantId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="IBillingAccountGrain" /> first.</b> This class keeps its two promises in
///     the only places they could be broken: <see cref="FinalizeAsync" /> and
///     <see cref="IssueCreditNoteAsync" /> are the only writers of <c>Invoices</c> and
///     <c>CreditNotes</c>, and both call <c>Add</c> and nothing else.
/// </remarks>
public sealed class BillingAccountGrain(
    [PersistentState("billing-account", StorageTiers.Durable)]
    IPersistentState<BillingAccountState> state,
    IGrainFactory grains,
    UsagePricing pricing,
    ITaxService tax,
    BillingOptions options,
    IClock clock
)
    : Grain, IBillingAccountGrain {
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = BillingGrainKeys.TenantOf(this);
        var key = BillingGrainKeys.Decode(this, GrainKeyKind.Tenant);

        if (key.Id != tenantId) {
            throw new InvalidOperationException(
                $"BillingAccountGrain for tenant {tenantId:D} was activated with the key of tenant {key.Id:D}. "
                + "An account is its own tenant's; a mismatch would invoice one tenant's usage to another."
            );
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<BillingAccountSnapshot>> ConfigureAsync(BillingProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(profile.LegalName)) {
            return Refuse("A billing profile names the legal entity invoiced.", "/legalName");
        }

        if (profile.Country is not { Length: 2 } country || !country.All(char.IsAsciiLetterUpper)) {
            return Refuse($"'{profile.Country}' is not an ISO 3166-1 alpha-2 country code, upper case.", "/country");
        }

        var exponent = Currencies.ExponentOf(profile.Currency);
        if (exponent.TryGetError(out var currencyError)) {
            return Refuse(currencyError.Message, "/currency");
        }

        var checkedProfile = tax.CheckCustomer(profile);
        if (checkedProfile.TryGetError(out var taxError)) {
            return Result<BillingAccountSnapshot>.Failure(taxError);
        }

        state.State.Profile = profile;
        state.State.Configured = true;
        await state.WriteStateAsync();

        return Result<BillingAccountSnapshot>.Success(Snapshot());
    }

    /// <inheritdoc />
    public async Task<Result<BillingAccountSnapshot>> AttachSubscriptionAsync(Guid subscriptionId) {
        if (subscriptionId == Guid.Empty) {
            return Refuse("A subscription is named by its GUID.", "/subscriptionId");
        }

        if (state.State.Subscriptions.Contains(subscriptionId)) {
            return Result<BillingAccountSnapshot>.Success(Snapshot());
        }

        // ⚠ Through the tenant's own subscription grain, so a GUID from another tenant — or one that
        // was never created — is refused rather than attached and billed at zero forever.
        var subscription = await Tenant()
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscriptionId))
            .GetAsync();

        if (subscription.TryGetError(out var missing)) {
            return Result<BillingAccountSnapshot>.Failure(
                ErrorCode.ResourceNotFound,
                $"Tenant {tenantId:D} has no subscription {subscriptionId:D} to attach: {missing.Message}"
            );
        }

        state.State.Subscriptions.Add(subscriptionId);
        await state.WriteStateAsync();

        return Result<BillingAccountSnapshot>.Success(Snapshot());
    }

    /// <inheritdoc />
    public Task<Result<BillingAccountSnapshot>> GetAsync() =>
        Task.FromResult(Result<BillingAccountSnapshot>.Success(Snapshot()));

    /// <inheritdoc />
    public async Task<Result<Invoice>> PreviewAsync(DateTimeOffset periodStart) {
        if (!Rating.IsMonthStart(periodStart)) {
            return NotAMonth(periodStart);
        }

        return Finalized(periodStart) is { } finalized
            ? Result<Invoice>.Success(finalized)
            : await DraftAsync(periodStart, state.State.Subscriptions);
    }

    /// <inheritdoc />
    public async Task<Result<Invoice>> FinalizeAsync(DateTimeOffset periodStart) {
        if (!Rating.IsMonthStart(periodStart)) {
            return NotAMonth(periodStart);
        }

        // ⚠ FIRST, AND BEFORE EVERY OTHER CHECK: a finalized month answers the stored invoice. A retry
        // after a timeout lands here and must not be refused by a rule that has changed since — a
        // profile edited after finalization is not a reason to fail the retry of the finalization.
        if (Finalized(periodStart) is { } already) {
            return Result<Invoice>.Success(already);
        }

        var closesAt = periodStart.AddMonths(1) + IBillingAccountGrain.LateUsageWindow;
        var now = clock.UtcNow;

        if (now < closesAt) {
            return Result<Invoice>.Failure(
                ErrorCode.Conflict,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{periodStart:yyyy-MM} cannot be finalized before {closesAt:O}: docs/plan/22 § Invoicing and payment keeps a 48-hour late-usage window after the month, because a usage event will arrive late and closing instantly means correcting invoices instead."
                )
            );
        }

        if (!state.State.Configured) {
            return Result<Invoice>.Failure(
                ErrorCode.Conflict,
                $"Tenant {tenantId:D}'s billing account has no profile. An invoice names the entity it is issued "
                + "to; set one with ConfigureAsync."
            );
        }

        var draft = await DraftAsync(periodStart, state.State.Subscriptions);
        if (draft.TryGetError(out var draftError)) {
            return Result<Invoice>.Failure(draftError);
        }

        var numbering = Numbering();
        var documentKey = string.Create(CultureInfo.InvariantCulture, $"{tenantId:N}/{periodStart:yyyy-MM}");

        var number = await numbering.AllocateAsync(options.Issuer, DocumentSeries.Invoice, documentKey);
        if (number.TryGetError(out var numberError)) {
            return Result<Invoice>.Failure(numberError);
        }

        var invoice = draft.GetValueOrThrow() with {
            InvoiceId = Guid.NewGuid(),
            Number = number.GetValueOrThrow(),
            Status = InvoiceStatus.Finalized,
            FinalizedAt = now
        };

        // The one write. Everything before it is recomputable, and the number above is the same number
        // on a retry — so a crash between the allocation and this line costs nothing but the retry.
        state.State.Invoices.Add(invoice);
        await state.WriteStateAsync();

        // ⚠ A confirmation that fails leaves the number listed as unconfirmed in the audit, which is a
        // false alarm and not a gap: the invoice is written. It is not a reason to fail the call.
        _ = await numbering.ConfirmAsync(options.Issuer.Code, DocumentSeries.Invoice, invoice.Number);

        return Result<Invoice>.Success(invoice);
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<Invoice>>> ListInvoicesAsync() =>
        Task.FromResult(Result<ImmutableArray<Invoice>>.Success([.. state.State.Invoices]));

    /// <inheritdoc />
    public Task<Result<Invoice>> GetInvoiceAsync(string number) =>
        Task.FromResult(
            Invoice(number) is { } invoice
                ? Result<Invoice>.Success(invoice)
                : Result<Invoice>.Failure(ErrorCode.ResourceNotFound, $"Tenant {tenantId:D} has no invoice '{number}'.")
        );

    /// <inheritdoc />
    public async Task<Result<CorrectionProposal>> ProposeCorrectionAsync(string invoiceNumber) {
        if (Invoice(invoiceNumber) is not { } invoice) {
            return Result<CorrectionProposal>.Failure(
                ErrorCode.ResourceNotFound,
                $"Tenant {tenantId:D} has no invoice '{invoiceNumber}'."
            );
        }

        var subscriptions = invoice.Lines.Select(static x => x.SubscriptionId).Distinct().ToList();
        var now = await DraftAsync(invoice.PeriodStart, subscriptions, invoice.Customer);
        if (now.TryGetError(out var draftError)) {
            return Result<CorrectionProposal>.Failure(draftError);
        }

        var credits = ImmutableArray.CreateBuilder<CreditNoteLineRequest>();
        var underbilled = ImmutableArray.CreateBuilder<InvoiceLine>();
        var rerated = now.GetValueOrThrow().Lines.ToDictionary(static x => (x.SubscriptionId, x.Meter));

        for (var index = 0; index < invoice.Lines.Length; index++) {
            var line = invoice.Lines[index];
            var carried = line.Amount - Credited(invoice.Number, index);
            var worth = rerated.Remove((line.SubscriptionId, line.Meter), out var current) ? current.Amount : 0m;

            if (worth < carried) {
                credits.Add(new() { LineIndex = index, Amount = carried - worth });
            } else if (worth > line.Amount) {
                underbilled.Add(current! with { Amount = worth - line.Amount, Quantity = current.Quantity - line.Quantity });
            }
        }

        // A meter that had no line at all when the month was finalized and has usage now.
        underbilled.AddRange(rerated.Values);

        return Result<CorrectionProposal>.Success(
            new() { InvoiceNumber = invoice.Number, Credits = credits.ToImmutable(), Underbilled = underbilled.ToImmutable() }
        );
    }

    /// <inheritdoc />
    public async Task<Result<CreditNote>> IssueCreditNoteAsync(CreditNoteRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RequestId)) {
            return RefuseCredit(
                "A credit note request carries a request id, and a retry repeats it — otherwise a timeout "
                + "credits twice.",
                "/requestId"
            );
        }

        // ⚠ THE RETRY PATH FIRST, for the reason FinalizeAsync answers a finalized month first.
        if (state.State.CreditNotes.FirstOrDefault(x => string.Equals(x.RequestId, request.RequestId, StringComparison.Ordinal)) is { } issued) {
            return Result<CreditNote>.Success(issued);
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.ApprovedBy)) {
            return RefuseCredit(
                """A credit note carries a reason and an approver. docs/plan/22 § Invoicing and payment: "Credits """
                + """and refunds: ledger entries with a reason, an approver, and an audit trail." """,
                string.IsNullOrWhiteSpace(request.Reason) ? "/reason" : "/approvedBy"
            );
        }

        if (Invoice(request.InvoiceNumber) is not { } invoice) {
            return Result<CreditNote>.Failure(
                ErrorCode.ResourceNotFound,
                $"Tenant {tenantId:D} has no finalized invoice '{request.InvoiceNumber}' to credit. A draft is corrected "
                + "by correcting the ledger — it is rated again on the next read."
            );
        }

        if (request.Lines.IsDefaultOrEmpty) {
            return RefuseCredit("A credit note credits at least one line.", "/lines");
        }

        var lines = ImmutableArray.CreateBuilder<CreditNoteLine>(request.Lines.Length);

        foreach (var wanted in request.Lines.GroupBy(static x => x.LineIndex)) {
            if (wanted.Key < 0 || wanted.Key >= invoice.Lines.Length) {
                return RefuseCredit($"Invoice '{invoice.Number}' has no line {wanted.Key}.", "/lines");
            }

            var amount = wanted.Sum(static x => x.Amount);
            if (wanted.Any(static x => x.Amount <= 0)) {
                return RefuseCredit("A credited amount is positive; the credit note carries it negated.", "/lines");
            }

            if (MoneyRounding.Round(amount, invoice.Currency).GetValueOrThrow() != amount) {
                return RefuseCredit(
                    $"{amount} has more decimals than {invoice.Currency} has. A document carries rounded amounts.",
                    "/lines"
                );
            }

            var line = invoice.Lines[wanted.Key];
            var remaining = line.Amount - Credited(invoice.Number, wanted.Key);

            if (amount > remaining) {
                return RefuseCredit(
                    $"Line {wanted.Key} of '{invoice.Number}' carries {MoneyRounding.Format(remaining, invoice.Currency)} "
                    + $"after the credit notes already issued against it, and {MoneyRounding.Format(amount, invoice.Currency)} "
                    + "was asked for. A credit note cannot credit more than was invoiced.",
                    "/lines"
                );
            }

            lines.Add(
                new() {
                    LineIndex = wanted.Key,
                    SubscriptionId = line.SubscriptionId,
                    Meter = line.Meter,
                    Amount = -amount,
                    Description = "Credit: " + line.Description
                }
            );
        }

        var subtotal = lines.Sum(static x => x.Amount);
        var credited = InvoiceBuilder.CreditTax(invoice, subtotal);
        if (credited.TryGetError(out var taxError)) {
            return Result<CreditNote>.Failure(taxError);
        }

        var creditNoteId = Guid.NewGuid();
        var numbering = Numbering();

        // ⚠ The document key is the REQUEST id, not the credit note's own GUID: a retry after a crash
        // between this allocation and the write below mints a new GUID and must still get this number.
        var number = await numbering.AllocateAsync(
            invoice.Issuer,
            DocumentSeries.CreditNote,
            string.Create(CultureInfo.InvariantCulture, $"{tenantId:N}/{request.RequestId}")
        );

        if (number.TryGetError(out var numberError)) {
            return Result<CreditNote>.Failure(numberError);
        }

        var note = new CreditNote {
            CreditNoteId = creditNoteId,
            Number = number.GetValueOrThrow(),
            InvoiceNumber = invoice.Number,
            TenantId = tenantId,
            Currency = invoice.Currency,
            Lines = lines.ToImmutable(),
            Subtotal = subtotal,
            Tax = credited.GetValueOrThrow(),
            Total = subtotal + credited.GetValueOrThrow().Amount,
            Reason = request.Reason,
            ApprovedBy = request.ApprovedBy,
            IssuedAt = clock.UtcNow,
            RequestId = request.RequestId
        };

        state.State.CreditNotes.Add(note);
        await state.WriteStateAsync();

        _ = await numbering.ConfirmAsync(invoice.Issuer.Code, DocumentSeries.CreditNote, note.Number);

        return Result<CreditNote>.Success(note);
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<CreditNote>>> ListCreditNotesAsync() =>
        Task.FromResult(Result<ImmutableArray<CreditNote>>.Success([.. state.State.CreditNotes]));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── The draft ────────────────────────────────────────────────────────────────────────────────

    async Task<Result<Invoice>> DraftAsync(
        DateTimeOffset periodStart,
        IReadOnlyList<Guid> subscriptions,
        BillingProfile? customer = null
    ) {
        // ⚠ A DRAFT NEEDS THE ISSUER TOO, and the first version said it did not. The tax on a line depends
        // on where the issuer is — reverse charge is "another member state than the issuer's" — so a
        // draft without one is a number with no tax decision behind it. BillingAcrossTheHostsTests found
        // it: the real silo, with no issuer configured, answered a draft with EuVatTaxService's refusal
        // of an issuer in country ''. Cost queries and budgets price usage and never tax it, and keep
        // working without one.
        if (!options.HasIssuer) {
            return Result<Invoice>.Failure(
                ErrorCode.InternalError,
                $"This silo has no invoice issuer — {BillingOptions.SectionName}:Issuer is not configured "
                + "(Code, LegalName, Country, VatId, NumberPrefix). An invoice, draft or final, names the entity "
                + "issuing it and is taxed from its country; it is not computed on behalf of a placeholder."
            );
        }

        var profile = customer ?? state.State.Profile;
        var lines = ImmutableArray.CreateBuilder<InvoiceLine>();
        var versions = new SortedSet<DateTimeOffset>();

        foreach (var subscription in subscriptions) {
            var rated = await pricing.RateAsync(tenantId, subscription, periodStart, periodStart.AddMonths(1));
            if (rated.TryGetError(out var rateError)) {
                return Result<Invoice>.Failure(rateError);
            }

            foreach (var hour in rated.GetValueOrThrow()) {
                versions.Add(hour.PriceVersion);
            }

            var built = InvoiceBuilder.Lines(subscription, rated.GetValueOrThrow(), profile.Currency);
            if (built.TryGetError(out var lineError)) {
                return Result<Invoice>.Failure(lineError);
            }

            lines.AddRange(built.GetValueOrThrow());
        }

        return InvoiceBuilder.Draft(tenantId, periodStart, options.Issuer, profile, lines.ToImmutable(), [.. versions], tax);
    }

    // ── Reads over state ─────────────────────────────────────────────────────────────────────────

    Invoice? Finalized(DateTimeOffset periodStart) =>
        state.State.Invoices.FirstOrDefault(x => x.PeriodStart == periodStart);

    Invoice? Invoice(string number) =>
        state.State.Invoices.FirstOrDefault(x => string.Equals(x.Number, number, StringComparison.Ordinal));

    decimal Credited(string invoiceNumber, int lineIndex) =>
        -state.State.CreditNotes
            .Where(x => string.Equals(x.InvoiceNumber, invoiceNumber, StringComparison.Ordinal))
            .SelectMany(static x => x.Lines)
            .Where(x => x.LineIndex == lineIndex)
            .Sum(static x => x.Amount);

    BillingAccountSnapshot Snapshot() =>
        new() {
            TenantId = tenantId,
            Profile = state.State.Profile,
            Subscriptions = [.. state.State.Subscriptions],
            Configured = state.State.Configured
        };

    TenantGrainFactory Tenant() => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    // ⚠ A plain GetGrain, deliberately: the numbering grain is null-tenant, and ForTenant would reach a
    // second, tenant-qualified copy with a second counter — which OnActivateAsync there refuses.
    IInvoiceNumberingGrain Numbering() =>
        grains.GetGrain<IInvoiceNumberingGrain>(GrainKeys.PlatformSingleton(GrainKeys.InvoiceNumberingSingleton));

    static Result<Invoice> NotAMonth(DateTimeOffset periodStart) =>
        Result<Invoice>.Failure(
            ErrorCode.InvalidRequestBody,
            string.Create(CultureInfo.InvariantCulture, $"{periodStart:O} is not the first instant of a month in UTC. An invoice's period is a calendar month.")
        );

    static Result<BillingAccountSnapshot> Refuse(string message, string target) =>
        Result<BillingAccountSnapshot>.Failure(ErrorCode.InvalidRequestBody, message, target);

    static Result<CreditNote> RefuseCredit(string message, string target) =>
        Result<CreditNote>.Failure(ErrorCode.InvalidRequestBody, message, target);
}
