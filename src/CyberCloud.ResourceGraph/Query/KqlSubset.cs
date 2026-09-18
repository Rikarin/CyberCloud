using System.Collections.Frozen;
using System.Collections.Immutable;

namespace CyberCloud.ResourceGraph.Query;

/// <summary>
///     What the resource graph's query language accepts — the KQL subset docs/plan/08 § The
///     resource-graph projection names — stated once, so the translator, its refusals and the tests
///     read one list.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A subset by allow-list, refused by name, with the list in the refusal.</b>
///         docs/plan/02 § ADR-011 puts it in one line for FerretDB — <i>"A compatibility layer, not
///         MongoDB; state the supported subset explicitly"</i> — and the same rule holds for a KQL
///         that is not Kusto. Every operator and function the translator does not name is refused
///         before any SQL is built, and the refusal says which token was refused and what would have
///         been accepted, because "unsupported" alone sends a person to a document and the document is
///         this list.
///     </para>
///     <para>
///         <b>Why this list and not a longer one.</b> The tabular operators are the ones a listing,
///         a filter, a tag query and a per-type count need — the four uses docs/plan/08 gives the
///         table. The scalar functions are the ones that read a string or a tag. Nothing here can
///         reach a second table (<c>join</c>, <c>union</c>), a row multiplier (<c>mv-expand</c>), a
///         regular expression the caller writes (<c>matches regex</c>, <c>extract</c>), or a clock
///         (<c>ago</c>, <c>now</c>, <c>bin</c>); each of those is a cost or a surface that wants its
///         own decision, and docs/plan/08 records the ones a caller is likely to miss as owed.
///     </para>
/// </remarks>
public static class KqlSubset {
    /// <summary>The tabular operators, as they are spelled after the pipe.</summary>
    public static ImmutableArray<string> Operators { get; } = [
        "where", "project", "extend", "summarize", "order by", "sort by", "take", "limit", "distinct", "count"
    ];

    /// <summary>The aggregation functions <c>summarize</c> accepts.</summary>
    public static ImmutableArray<string> Aggregates { get; } = ["count", "dcount", "min", "max", "sum", "avg"];

    /// <summary>The scalar functions, anywhere an expression is accepted.</summary>
    public static ImmutableArray<string> Functions { get; } = [
        "tolower", "toupper", "strcat", "split", "isnotempty", "isempty", "tostring", "todynamic", "not"
    ];

    /// <summary>The comparison and string operators between two expressions.</summary>
    public static ImmutableArray<string> Comparisons { get; } = [
        "==", "!=", "=~", "!~", "<", "<=", ">", ">=", "has", "contains", "startswith", "endswith", "in", "and", "or"
    ];

    /// <summary>The scalar functions as a set, for the translator's lookup.</summary>
    public static FrozenSet<string> FunctionSet { get; } = Functions.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The aggregates as a set.</summary>
    public static FrozenSet<string> AggregateSet { get; } = Aggregates.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The one sentence every refusal ends with: the four lists, spelled.
    /// </summary>
    public static string SupportedSentence { get; } =
        $"The resource graph's KQL subset is: operators {Join(Operators)}; aggregates {Join(Aggregates)}; "
        + $"scalar functions {Join(Functions)}; comparisons {Join(Comparisons)}; and the literals string, "
        + "long, real, bool and datetime(…). tags.<key> and tags['<key>'] read the tag map — "
        + "docs/plan/08 § The resource-graph projection.";

    static string Join(ImmutableArray<string> names) => string.Join(", ", names);
}
