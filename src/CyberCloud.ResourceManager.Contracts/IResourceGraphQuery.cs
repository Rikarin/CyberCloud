using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The resource graph's read side — a tenant's KQL query over its projected resources, answered
///     from the per-tenant ClickHouse table docs/plan/08 § The resource-graph projection describes.
///     The query half of issue #54.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The fourth entry point beside <see cref="IResourceManager" />, <see cref="IScopeManager" />
///         and <see cref="IRoleAssignmentManager" />, and the one that reads no grain.</b> The
///         other three answer from the resource grains and the ReBAC engine; this one answers from
///         the projection, which is eventually consistent and says so — a resource created a second
///         ago is in its own blade and not yet in this table. It is a service held by the gateway,
///         like the other three, so a query costs the gateway one ClickHouse round trip and the
///         cluster one read of the caller's membership slice.
///     </para>
///     <para>
///         ⚠ <b>The seam is here and the implementation is in <c>CyberCloud.ResourceGraph</c>, for
///         the reason <see cref="IResourceChangedSink" /> is here and its NATS publisher is there.</b>
///         This assembly names no query language, no ClickHouse and no authorization engine; the
///         gateway dispatches to this interface and the host that has a ClickHouse endpoint
///         registers the real service over it. The default <c>AddCyberCloudResourceManager</c>
///         registers refuses by name, as every other unwired seam does.
///     </para>
///     <para>
///         ⚠ <b>Authorization is the access column, ANDed into every query by the implementation,
///         and nothing at the gateway.</b> docs/plan/08 § The resource-graph projection:
///         <i>"Access filtering on this table comes from the denormalized column"</i>. The caller's
///         subject and the usersets it is closed into are the one parameter no query can leave out
///         or override — the caller's KQL never sees the column, and a row the caller may not read is
///         not in the result and not in any count. That is the same 404-never-403 shape the other
///         three entry points have, expressed as "not there" rather than "forbidden".
///     </para>
///     <para>
///         <b>The language is a KQL subset, translated to SQL — never SQL exposed.</b> docs/plan/01
///         § Management used to say the opposite and is corrected with this seam: a tenant who knows
///         Azure Resource Graph types <c>resources | where type =~ '…' | project name</c> and gets an
///         answer, and everything outside the subset is refused with a message naming the operator or
///         function and the list that is supported. The subset is <c>CyberCloud.ResourceGraph</c>'s
///         <c>KqlSubset</c>; docs/plan/08 § The resource-graph projection carries it in prose.
///     </para>
/// </remarks>
public interface IResourceGraphQuery {
    /// <summary>
    ///     Runs one query and returns one page of its result. <c>POST</c> on the resource graph's
    ///     address.
    /// </summary>
    /// <param name="request">The query, the caller and the page parameters, as the gateway parsed them.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     One page of rows, or a failure: <see cref="ErrorCode.InvalidRequestBody" /> for a query
    ///     outside the subset, a malformed one, one too large to parse, or one that ran past the
    ///     store's budget — the message names what was refused and what is supported — and
    ///     <see cref="ErrorCode.InternalError" /> when the store did not answer. ⚠ Every message
    ///     is written for the caller: the store's own words, which quote the statement and the
    ///     caller's usersets back, go to the implementation's log and never into a failure —
    ///     docs/plan/08 § Errors. And never <see cref="ErrorCode.AuthorizationFailed" />: what the
    ///     caller may not read is absent rather than refused.
    /// </returns>
    Task<Result<ResourceGraphQueryPage>> QueryAsync(
        ResourceGraphQueryRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     A resource graph query as it reaches <see cref="IResourceGraphQuery" />, after the gateway has
///     authenticated the caller and read the body.
/// </summary>
/// <remarks>
///     ⚠ Its page rules are <see cref="ListRequest" />'s, spelled in the same two constants, so a
///     client that pages one collection of this API pages this one the same way — and
///     <see cref="Continuation" /> is named as that record names it, for the reason it gives
///     (<c>CC1005</c>).
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ResourceGraphQueryRequest")]
public sealed record ResourceGraphQueryRequest {
    /// <summary>The KQL text — <c>resources | where … | project …</c>.</summary>
    [Id(0)]
    public string Query { get; init; } = string.Empty;

    /// <summary>Who is asking. The tenant is the token's; the subject is what the access filter is built from.</summary>
    [Id(1)]
    public CallerContext Caller { get; init; } = new();

    /// <summary>
    ///     How many rows to return. Zero means <see cref="ListRequest.DefaultPageSize" />; anything
    ///     above <see cref="ListRequest.MaxPageSize" /> is clamped to it rather than refused.
    /// </summary>
    [Id(2)]
    public int Top { get; init; }

    /// <summary>
    ///     Where to resume, from a previous page's <see cref="ResourceGraphQueryPage.Continuation" />,
    ///     or empty for the first page.
    /// </summary>
    /// <remarks>
    ///     ⚠ The token is an offset into the same query's result and carries a fingerprint of the
    ///     query text, so a token handed out for one query is refused with a <c>400</c> when it
    ///     arrives with another. Unlike <see cref="ListRequest.Continuation" /> it is not a snapshot
    ///     either: the projection moves between pages, and a row that changes its position in the
    ///     order between two pages can appear twice or not at all. A query with its own
    ///     <c>order by</c> on a stable key — <c>resourceId</c> — pages exactly; the implementation
    ///     adds an order over every output column when the query names none.
    /// </remarks>
    [Id(3)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>The page size this request actually gets.</summary>
    public int PageSize =>
        Top switch {
            <= 0 => ListRequest.DefaultPageSize,
            > ListRequest.MaxPageSize => ListRequest.MaxPageSize,
            _ => Top
        };
}

/// <summary>One column of a query's result, as ClickHouse reported it.</summary>
/// <param name="Name">The column's name — the KQL name, as the query spelled or the language derived it.</param>
/// <param name="Type">The KQL scalar type: <c>string</c>, <c>long</c>, <c>real</c>, <c>bool</c>, <c>datetime</c> or <c>dynamic</c>.</param>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ResourceGraphColumn")]
public readonly record struct ResourceGraphColumn([property: Id(0)] string Name, [property: Id(1)] string Type);

/// <summary>One page of a resource graph query's result.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Rows are JSON objects as text, not typed records, because the shape is the query's.</b>
///         A <c>project</c> or a <c>summarize</c> makes a result whose columns no compiled type can
///         name in advance; each row is the object ClickHouse rendered, keyed by <see cref="Columns" />'
///         names, and the gateway writes it into the <c>value</c> array verbatim. Numbers are
///         numbers — the store is asked not to quote 64-bit integers — and a <c>dynamic</c> column
///         (the tag map) is an object.
///     </para>
///     <para>
///         ⚠ <b>A page holds only what the caller may read, and there is no total.</b> The access
///         filter is inside the query, so a row the caller cannot read is not counted, not paged over
///         and not hinted at — the same property <see cref="ResourceListPage" /> states for a
///         collection, and for the same reason.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ResourceGraphQueryPage")]
public sealed record ResourceGraphQueryPage {
    /// <summary>The result's columns, in the order the query produced them.</summary>
    [Id(0)]
    public ImmutableArray<ResourceGraphColumn> Columns { get; init; } = [];

    /// <summary>The rows, each one JSON object text keyed by the column names.</summary>
    [Id(1)]
    public ImmutableArray<string> Rows { get; init; } = [];

    /// <summary>
    ///     What to pass as <see cref="ResourceGraphQueryRequest.Continuation" /> for the next page, or
    ///     empty when this page reached the end.
    /// </summary>
    [Id(2)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>Whether there is another page.</summary>
    public bool HasMore => Continuation.Length > 0;
}
