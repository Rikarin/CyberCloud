using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     A request to list one resource type inside one resource group — the collection <c>GET</c> of
///     docs/plan/10 § Shape.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a <see cref="WriteRequest" />, because a collection is not a resource.</b> A
///         <see cref="WriteRequest" /> carries a verb, a body and an <c>If-Match</c>, and every one
///         of the three is meaningless here; more to the point its <see cref="WriteRequest.Path" />
///         is parsed by <c>ResourceId.ParsePath</c>, which refuses a collection path by design —
///         <c>ResourceCollectionId</c>'s remarks explain why the two grammars are disjoint. Reusing
///         the type would have meant a request that cannot be parsed by the parser its own field
///         names.
///     </para>
///     <para>
///         ⚠ <b><see cref="Top" /> is a cap this endpoint enforces and not a hint.</b> ReBAC's
///         <c>ListObjects</c> is M2 (docs/plan/07), so a listing is a <c>Check</c> <i>per member</i>
///         — see <c>IResourceManager.ListAsync</c>. An uncapped page therefore makes the cost of one
///         request a number the caller chooses, which is the shape of every enumeration endpoint that
///         has ever been used as an amplifier.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ListRequest")]
public sealed record ListRequest {
    /// <summary>The largest page this endpoint will build, whatever <see cref="Top" /> asks for.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The page size used when <see cref="Top" /> is zero.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The collection path from the URL — a <c>ResourceCollectionId</c>.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The <c>api-version</c> query parameter. ⚠ Required — there is no "latest".</summary>
    [Id(1)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>Who is asking.</summary>
    [Id(2)]
    public CallerContext Caller { get; init; } = new();

    /// <summary>
    ///     How many resources to return. Zero means <see cref="DefaultPageSize" />; anything above
    ///     <see cref="MaxPageSize" /> is clamped to it rather than refused.
    /// </summary>
    [Id(3)]
    public int Top { get; init; }

    /// <summary>
    ///     Where to resume, from a previous page's <see cref="ResourceListPage.Continuation" />,
    ///     or empty for the first page.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Named <c>Continuation</c> and not <c>ContinuationToken</c>, and that is
    ///     <c>CC1005</c> rather than taste.</b> The analyzer refuses an <c>[Id]</c>-annotated member
    ///     whose name ends in <c>Token</c>, because such a member serialises into grain state and
    ///     docs/plan/00 § Non-negotiables makes every secret a <c>SecretRef</c> handle. This value is
    ///     a resource path and not a credential, so the rule is a false positive here — but a
    ///     suppression would be a hole in a rule whose whole worth is that it has none, and the name
    ///     costs nothing.
    ///     <para>
    ///     ⚠ <b>The token is the last canonical path of the previous page and is therefore not a
    ///     snapshot.</b> Paging resumes at "the next member whose canonical path sorts after this
    ///     one", so a resource created or deleted between pages changes what the caller sees and
    ///     cannot make the walk skip or repeat an unrelated member. The alternative — a cursor into a
    ///     materialised list — needs the list to survive between requests, which for a group whose
    ///     membership is a durable grain means either holding it or being wrong about it.
    ///     </para>
    /// </remarks>
    [Id(4)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>The page size this request actually gets.</summary>
    public int PageSize => Top switch {
        <= 0 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => Top
    };
}

/// <summary>
///     One page of a collection <c>GET</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A page holds only what the caller may read, and a filtered-out member leaves no
///         trace.</b> There is no count of what was hidden, no gap in the ordering and no marker: all
///         three would be the enumeration oracle docs/plan/07 § The enforcement seam closes by
///         answering <c>404</c> rather than <c>403</c>. "You may not see six of these" tells the
///         caller six resources exist.
///     </para>
///     <para>
///         ⚠ <b>Therefore a page can be short, or empty, and still carry a continuation token.</b>
///         The page size bounds the <i>members examined</i>, because that is what bounds the cost;
///         it does not promise that many results. A client stops when the token comes back empty and
///         never when a page is smaller than it asked for.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ResourceListPage")]
public sealed record ResourceListPage {
    /// <summary>The resources, ordered by canonical path, ordinally.</summary>
    [Id(0)]
    public ImmutableArray<ResourceSnapshot> Resources { get; init; } = [];

    /// <summary>
    ///     What to pass as <see cref="ListRequest.Continuation" /> for the next page, or empty
    ///     when the walk reached the end of the group's membership.
    /// </summary>
    [Id(1)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>Whether there is another page.</summary>
    public bool HasMore => Continuation.Length > 0;
}
