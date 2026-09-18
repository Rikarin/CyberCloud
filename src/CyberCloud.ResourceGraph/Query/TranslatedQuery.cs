using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CyberCloud.ResourceGraph.Query;

/// <summary>
///     One bound value of a translated query — a <c>{name:Type}</c> placeholder in the SQL and the
///     value ClickHouse binds to it, as its HTTP interface takes them.
/// </summary>
/// <param name="Name">The placeholder's name, <c>p0</c>, <c>p1</c>, … or <c>access</c>.</param>
/// <param name="ClickHouseType">
///     The type the placeholder declares — <c>String</c>, <c>Int64</c>, <c>Float64</c>,
///     <c>Bool</c>, <see cref="DateTimeType" />, <c>Array(String)</c>.
/// </param>
/// <param name="Value">The value in ClickHouse's parameter spelling, before URL encoding.</param>
/// <remarks>
///     ⚠ <b>A literal from the query text never reaches the SQL; it reaches this record.</b> That is
///     the whole of the injection defence, and <c>KqlInjectionTests</c> pins it: a string literal
///     containing a quote, a semicolon and a <c>DROP TABLE</c> is a <see cref="Value" /> the server
///     binds as a string, and the SQL carries <c>{p0:String}</c> where it was.
/// </remarks>
public sealed record SqlParameter(string Name, string ClickHouseType, string Value) {
    /// <summary>
    ///     The type a datetime binds as: <c>DateTime64(3, 'UTC')</c>, with the zone spelled.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not <c>DateTime64(3)</c>. ClickHouse parses a parameter of a zoneless type in the
    ///     server's own time zone, and the columns it is compared with are
    ///     <c>DateTime64(3, 'UTC')</c> — so on a server in Europe/Prague a value this module
    ///     computed in UTC would have been read two hours early (#54 review). Naming the zone on the
    ///     parameter makes the server's zone irrelevant, which is what a stored instant wants.
    /// </remarks>
    public const string DateTimeType = "DateTime64(3, 'UTC')";

    /// <summary>The placeholder as the SQL spells it.</summary>
    public string Placeholder => "{" + Name + ":" + ClickHouseType + "}";

    /// <summary>
    ///     An <c>Array(String)</c> value in ClickHouse's literal spelling: <c>['a','b']</c>, with a
    ///     backslash before a quote or a backslash inside an element.
    /// </summary>
    /// <param name="values">The elements.</param>
    public static string ArrayOfStrings(IEnumerable<string> values) {
        ArgumentNullException.ThrowIfNull(values);

        var built = new StringBuilder("[");
        var first = true;

        foreach (var value in values) {
            if (!first) {
                built.Append(',');
            }

            first = false;
            built.Append('\'')
                .Append(
                    value.Replace("""\""", """\\""", StringComparison.Ordinal)
                        .Replace("'", """\'""", StringComparison.Ordinal)
                )
                .Append('\'');
        }

        return built.Append(']').ToString();
    }

    /// <summary>A <see cref="DateTimeType" /> value in the spelling ClickHouse's parameter parser takes, in UTC.</summary>
    /// <param name="value">The instant.</param>
    public static string DateTime64(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

/// <summary>
///     A KQL query after translation: one parameterised ClickHouse statement over one table in the
///     caller's tenant database, the values it binds, and the columns it returns.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Sql" /> ends without a <c>FORMAT</c>: the caller picks the format it reads.
///         The statement is paged already — <c>LIMIT</c> one more than the page so the reader knows
///         whether a next page exists, <c>OFFSET</c> the continuation — and it is ordered: by the
///         query's own <c>order by</c>, or by every output column when the query has none, so two
///         pages of one query are two slices of one order.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The access filter is in <see cref="Sql" /> and its subjects are in
///             <see cref="Parameters" />, and neither came from the query text.
///         </b> The translator puts
///         <c>hasAny(access, {access:Array(String)})</c> on the base <c>SELECT</c> before the first
///         operator sees a row, and the caller's KQL cannot name <c>access</c> at all — the binder
///         does not know the column. What the caller may read is therefore decided before their
///         <c>where</c> runs, and a <c>count</c> counts only that.
///     </para>
/// </remarks>
public sealed record TranslatedQuery {
    /// <summary>The statement, with <c>{name:Type}</c> placeholders and no <c>FORMAT</c>.</summary>
    public required string Sql { get; init; }

    /// <summary>The placeholders' values, in the order they appear.</summary>
    public required ImmutableArray<SqlParameter> Parameters { get; init; }

    /// <summary>The result's columns, in output order, with their KQL types.</summary>
    public required ImmutableArray<ResourceGraphColumn> Columns { get; init; }

    /// <summary>The page size the statement was built for — <c>LIMIT</c> is one more.</summary>
    public required int PageSize { get; init; }

    /// <summary>The offset the statement was built for.</summary>
    public required long Offset { get; init; }

    /// <summary>The parameters as the <see cref="ClickHouseClient" /> takes them.</summary>
    public IReadOnlyDictionary<string, string> ParameterValues =>
        Parameters.ToDictionary(static x => x.Name, static x => x.Value, StringComparer.Ordinal);
}
