using CyberCloud.Core.Contracts;
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
/// </remarks>
public sealed class InvoiceNumberingGrain(
    [PersistentState("invoice-numbering", StorageTiers.Durable)]
    IPersistentState<InvoiceNumberingState> state
)
    : Grain, IInvoiceNumberingGrain {
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
    public async Task<Result<string>> AllocateAsync(InvoiceIssuer issuer, DocumentSeries series, string documentKey) {
        ArgumentNullException.ThrowIfNull(issuer);

        if (string.IsNullOrWhiteSpace(issuer.Code) || string.IsNullOrWhiteSpace(issuer.NumberPrefix)) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                "An issuer needs a code, which keys its sequence, and a number prefix, which every number carries."
            );
        }

        if (series == DocumentSeries.Unknown) {
            return Result<string>.Failure(ErrorCode.InvalidRequestBody, "A number belongs to a series; Unknown is not one.");
        }

        if (string.IsNullOrWhiteSpace(documentKey)) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                "A number is allocated to a document, and an empty document key would give every retry a new "
                + "number — the gap this grain exists to prevent."
            );
        }

        var sequence = Series(issuer.Code, series);

        // ⚠ THE RETRY PATH, AND THE REASON THE SEQUENCE HAS NO GAPS. A finalization that timed out after
        // this grain answered asks again with the same document key and gets the same number.
        if (sequence.ByDocument.TryGetValue(documentKey, out var known)) {
            return Result<string>.Success(known);
        }

        sequence.Allocated++;
        var number = Format(issuer.NumberPrefix, series, sequence.Allocated);

        sequence.ByDocument[documentKey] = number;
        sequence.Unconfirmed[number] = documentKey;
        await state.WriteStateAsync();

        return Result<string>.Success(number);
    }

    /// <inheritdoc />
    public async Task<Result> ConfirmAsync(string issuerCode, DocumentSeries series, string number) {
        var sequence = Series(issuerCode, series);

        if (!sequence.ByDocument.ContainsValue(number)) {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"'{number}' was never allocated in {issuerCode}'s {series} series. Only a number this grain handed "
                + "out can be confirmed."
            );
        }

        if (sequence.Unconfirmed.Remove(number)) {
            await state.WriteStateAsync();
        }

        return Result.Success;
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
