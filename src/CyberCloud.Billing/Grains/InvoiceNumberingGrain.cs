using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Billing.Grains;

/// <summary>
///     <see cref="IInvoiceNumberingGrain" /> — platform singleton, Durable, key
///     <c>platform/invoice-numbering</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="IInvoiceNumberingGrain" /> first</b> for the three rules that make the
///     sequence gap-free. This class holds to them in one place each: <see cref="AllocateAsync" />
///     answers a known document from <see cref="NumberSeriesState.ByDocument" /> before it touches the
///     counter, and the counter moves and the allocation is recorded in one state write.
///     <para>
///         ⚠ <b>A number is answered only once it's on disk, and a call that threw leaves memory
///         suspect.</b> <see cref="AllocateAsync" /> moves the counter in memory before it writes, so a
///         failed write leaves memory one ahead of storage. Answering the retry from
///         <see cref="NumberSeriesState.ByDocument" /> would then hand out a number no storage holds,
///         and the next activation, reloading the lower counter, would give the same number to the
///         next tenant: two invoices, one number. So <see cref="Invoke" /> re-reads the state before
///         the first call after one that threw. ⚠ Re-read, not rolled back: a write that reports
///         failure may have committed (a connection lost after the commit), and only storage knows
///         which. Rolling memory back over a committed write would give that number out a second time
///         as well. <c>InvoicingTests.ANumberWhoseWriteFailedIsNeverAnsweredFromMemory</c> and
///         <c>InvoicingTests.AWriteThatCommittedBeforeItFailedKeepsItsNumber</c> fail the write each way.
///     </para>
///     <para>
///         ⚠ <b>The number's instant is taken here, with the number, and kept until it's confirmed.</b>
///         Several member states want invoice dates in the order of their numbers. An account that
///         dated its invoice by its own clock would read it at a different moment from the
///         allocation, and a retry after a failed write would read it hours later, after the next
///         tenant's invoice took the next number. One grain serializes the allocations, so the instant
///         it records rises with the number, and a retry is given the first attempt's instant back
///         (<c>InvoicingTests.AnInvoiceRetriedAfterALaterOneKeepsTheDateItsNumberWasTakenAt</c>).
///     </para>
/// </remarks>
public sealed class InvoiceNumberingGrain(
    [PersistentState("invoice-numbering", StorageTiers.Durable)]
    IPersistentState<InvoiceNumberingState> state,
    IClock clock
)
    : Grain, IInvoiceNumberingGrain, IIncomingGrainCallFilter {
    bool suspect;

    /// <summary>Re-reads the state before the first call after one that threw, then runs the call.</summary>
    /// <param name="context">The call.</param>
    public async Task Invoke(IIncomingGrainCallContext context) {
        ArgumentNullException.ThrowIfNull(context);

        if (suspect) {
            await state.ReadStateAsync();
            suspect = false;
        }

        try {
            await context.Invoke();
        } catch {
            suspect = true;
            throw;
        }
    }

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        // ⚠ A second copy of this grain qualified with a tenant would be a second counter — two
        // invoices with one number. The key is a constant, so the only way to get one is to reach it
        // through ForTenant, and that is refused here rather than served.
        if (this.GetTenantId() is { } tenant) {
            throw new InvalidOperationException(
                $"InvoiceNumberingGrain was activated for tenant '{tenant}'. It is a null-tenant platform "
                + "singleton — reach it with a plain GetGrain and GrainKeys.PlatformSingleton, never ForTenant."
            );
        }

        var expected = GrainKeys.PlatformSingleton(GrainKeys.InvoiceNumberingSingleton);
        if (!string.Equals(this.GetKeyWithinTenant(), expected, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"InvoiceNumberingGrain was activated with '{this.GetKeyWithinTenant()}'; its one key is '{expected}'."
            );
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<DocumentNumber>> AllocateAsync(InvoiceIssuer issuer, DocumentSeries series, string documentKey) {
        ArgumentNullException.ThrowIfNull(issuer);

        if (string.IsNullOrWhiteSpace(issuer.Code) || string.IsNullOrWhiteSpace(issuer.NumberPrefix)) {
            return Result<DocumentNumber>.Failure(
                ErrorCode.InvalidRequestBody,
                "An issuer needs a code, which keys its sequence, and a number prefix, which every number carries."
            );
        }

        if (series == DocumentSeries.Unknown) {
            return Result<DocumentNumber>.Failure(ErrorCode.InvalidRequestBody, "A number belongs to a series; Unknown is not one.");
        }

        if (string.IsNullOrWhiteSpace(documentKey)) {
            return Result<DocumentNumber>.Failure(
                ErrorCode.InvalidRequestBody,
                "A number is allocated to a document, and an empty document key would give every retry a new "
                + "number — the gap this grain exists to prevent."
            );
        }

        var sequence = Series(issuer.Code, series);

        // ⚠ THE RETRY PATH, AND THE REASON THE SEQUENCE HAS NO GAPS. A finalization that timed out after
        // this grain answered asks again with the same document key and gets the same number.
        if (sequence.ByDocument.TryGetValue(documentKey, out var known)) {
            return Result<DocumentNumber>.Success(
                new() { Number = known, AllocatedAt = sequence.AllocatedAt.TryGetValue(known, out var at) ? at : clock.UtcNow }
            );
        }

        sequence.Allocated++;
        var number = Format(issuer.NumberPrefix, series, sequence.Allocated);
        var allocatedAt = clock.UtcNow;

        sequence.ByDocument[documentKey] = number;
        sequence.Unconfirmed[number] = documentKey;
        sequence.AllocatedAt[number] = allocatedAt;
        await state.WriteStateAsync();

        return Result<DocumentNumber>.Success(new() { Number = number, AllocatedAt = allocatedAt });
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Confirming forgets the document's key.</b> A retry of a written document never reaches
    ///     this grain again: the account answers a finalized month, and a repeated credit-note request,
    ///     from its own state first. So only an unconfirmed allocation needs its key, and the state this
    ///     singleton rewrites on every call stays the size of what's in flight rather than of every
    ///     document the platform has issued.
    /// </remarks>
    public async Task<Result> ConfirmAsync(string issuerCode, DocumentSeries series, string number) {
        var sequence = Series(issuerCode, series);

        if (sequence.Unconfirmed.Remove(number, out var documentKey)) {
            sequence.ByDocument.Remove(documentKey);
            sequence.AllocatedAt.Remove(number);
            await state.WriteStateAsync();
            return Result.Success;
        }

        // Confirmed already, or never handed out. Once the key is gone, only the counter tells them apart.
        return sequence.ByDocument.ContainsValue(number) || IsIssued(number, sequence.Allocated)
            ? Result.Success
            : Result.Failure(
                ErrorCode.ResourceNotFound,
                $"'{number}' was never allocated in {issuerCode}'s {series} series. Only a number this grain handed "
                + "out can be confirmed."
            );
    }

    /// <inheritdoc />
    public Task<Result<NumberingAudit>> AuditAsync(string issuerCode, DocumentSeries series) {
        state.State.Series.TryGetValue(Key(issuerCode, series), out var sequence);

        return Task.FromResult(
            Result<NumberingAudit>.Success(
                new() {
                    Allocated = sequence?.Allocated ?? 0,
                    Unconfirmed = sequence is null ? [] : [.. sequence.Unconfirmed.Keys.Order(StringComparer.Ordinal)]
                }
            )
        );
    }

    /// <summary>The printed number: the issuer's prefix, the series, and eight digits.</summary>
    /// <param name="prefix">The issuer's prefix.</param>
    /// <param name="series">The series.</param>
    /// <param name="value">The sequence value, from 1.</param>
    /// <remarks>
    ///     ⚠ Eight digits and never reset — not per year. A yearly reset is common and legal in most
    ///     member states, and it makes the document key of a January retry ambiguous across the
    ///     boundary; a continuous sequence has one meaning forever. Ninety-nine million documents per
    ///     issuer per series is the ceiling, and past it the number simply grows a digit.
    /// </remarks>
    public static string Format(string prefix, DocumentSeries series, long value) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}-{(series == DocumentSeries.CreditNote ? "CN" : "INV")}-{value:D8}"
        );

    /// <summary>Whether a number's sequence value is one the series has already reached.</summary>
    static bool IsIssued(string number, long allocated) =>
        long.TryParse(number.AsSpan(number.LastIndexOf('-') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= 1
        && value <= allocated;

    NumberSeriesState Series(string issuerCode, DocumentSeries series) {
        var key = Key(issuerCode, series);

        if (!state.State.Series.TryGetValue(key, out var sequence)) {
            sequence = new();
            state.State.Series[key] = sequence;
        }

        return sequence;
    }

    static string Key(string issuerCode, DocumentSeries series) =>
        string.Create(CultureInfo.InvariantCulture, $"{issuerCode}|{series}");
}
