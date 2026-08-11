namespace CyberCloud.Sdk;

/// <summary>One page of a list response.</summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class Page<T> {
    /// <summary>Creates a page.</summary>
    /// <param name="values">The items.</param>
    /// <param name="continuationToken">The <c>nextLink</c>, or <see langword="null" /> on the last page.</param>
    /// <param name="response">The response the page came from.</param>
    public Page(IReadOnlyList<T> values, string? continuationToken, Response response) {
        Values = values;
        ContinuationToken = continuationToken;
        Response = response;
    }

    /// <summary>The items.</summary>
    public IReadOnlyList<T> Values { get; }

    /// <summary>The <c>nextLink</c>, or <see langword="null" /> when this is the last page.</summary>
    public string? ContinuationToken { get; }

    /// <summary>The response this page was read from.</summary>
    public Response Response { get; }
}

/// <summary>
///     A paged list, enumerable item by item or page by page — docs/plan/21 § The .NET SDK's
///     <c>AsyncPageable&lt;T&gt;</c> row, <i>"<c>await foreach</c> over paged lists"</i>.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <remarks>
///     <para>
///         ⚠ <b>Pages are fetched as they are needed, never up front.</b> docs/plan/08 § Resource-graph
///         projection puts list queries behind a projection that can legitimately return thousands of
///         resources; an implementation that materialised them all before the first item would turn
///         <c>.FirstAsync()</c> into a full scan. Every page after the first costs a round trip at the
///         moment the consumer asks for the item that needs it.
///     </para>
///     <para>
///         ⚠ <b>Retries happen underneath, not here.</b> A <c>429</c> between page two and page three
///         is the pipeline's problem (<see cref="RetryHandler" />), so a consumer never sees a
///         half-enumerated sequence throw for a reason the SDK could have handled.
///     </para>
/// </remarks>
public abstract class AsyncPageable<T> : IAsyncEnumerable<T> {
    /// <summary>Enumerates page by page — the form to use when the continuation token matters.</summary>
    /// <param name="continuationToken">Where to resume, or <see langword="null" /> to start.</param>
    /// <param name="pageSizeHint">A requested page size. The service may ignore it.</param>
    public abstract IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null);

    /// <inheritdoc />
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
        await foreach (var page in AsPages().WithCancellation(cancellationToken).ConfigureAwait(false)) {
            foreach (var value in page.Values)
                yield return value;
        }
    }

    /// <summary>Builds a pageable from a function that fetches one page.</summary>
    /// <param name="fetchPage">
    ///     Fetches the page at a continuation token. Called once per page, at the moment the consumer
    ///     needs it.
    /// </param>
    /// <param name="cancellationToken">The token.</param>
    public static AsyncPageable<T> Create(
        Func<string?, int?, CancellationToken, ValueTask<Page<T>>> fetchPage,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(fetchPage);

        return new FuncAsyncPageable<T>(fetchPage, cancellationToken);
    }

    /// <summary>Builds a pageable over pages already in hand. Used by tests and by convenience methods.</summary>
    /// <param name="pages">The pages.</param>
    public static AsyncPageable<T> FromPages(IEnumerable<Page<T>> pages) {
        ArgumentNullException.ThrowIfNull(pages);

        return new FuncAsyncPageable<T>(
            (token, size, cancellation) => throw new InvalidOperationException("A static pageable fetches nothing."),
            CancellationToken.None) { Static = [.. pages] };
    }
}

sealed class FuncAsyncPageable<T> : AsyncPageable<T> {
    readonly Func<string?, int?, CancellationToken, ValueTask<Page<T>>> fetchPage;
    readonly CancellationToken cancellationToken;

    internal FuncAsyncPageable(Func<string?, int?, CancellationToken, ValueTask<Page<T>>> fetchPage, CancellationToken cancellationToken) {
        this.fetchPage = fetchPage;
        this.cancellationToken = cancellationToken;
    }

    internal List<Page<T>>? Static { get; init; }

    public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null) {
        if (Static is { } pages) {
            foreach (var page in pages)
                yield return page;

            yield break;
        }

        do {
            var page = await fetchPage(continuationToken, pageSizeHint, cancellationToken).ConfigureAwait(false);

            yield return page;

            continuationToken = page.ContinuationToken;
        } while (!string.IsNullOrEmpty(continuationToken));
    }
}
