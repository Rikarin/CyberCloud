using Kusto.Language;
using Kusto.Language.Symbols;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.ResourceGraph.Query;

/// <summary>The KQL scalar type of a <c>resources</c> column, as the query language and the result see it.</summary>
// Suppressed on the enum rather than renamed: the members ARE Kusto's scalar type names, which the
// refusals and the wire spell exactly so, and `Text`/`Whole` in their place would be a second
// vocabulary for the same six words.
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "The members are Kusto's own scalar type names — string, long, real, bool, datetime, dynamic — and the wire spells them."
)]
public enum KqlType {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>A UTF-8 string. ClickHouse <c>String</c>.</summary>
    String,

    /// <summary>A 64-bit integer. ClickHouse <c>UInt64</c> or <c>Int64</c>.</summary>
    Long,

    /// <summary>A double. ClickHouse <c>Float64</c>.</summary>
    Real,

    /// <summary>A boolean. ClickHouse <c>Bool</c>.</summary>
    Bool,

    /// <summary>A UTC instant. ClickHouse <c>DateTime64(3, 'UTC')</c>.</summary>
    DateTime,

    /// <summary>A property bag — the tag map. ClickHouse <c>Map(String, String)</c>.</summary>
    Dynamic
}

/// <summary>
///     One column of the <c>resources</c> table as KQL sees it, and the SQL that produces it from the
///     projection's row.
/// </summary>
/// <param name="Name">The KQL name — Azure Resource Graph's spelling where Azure has the column.</param>
/// <param name="Type">The KQL type.</param>
/// <param name="Sql">The ClickHouse expression over <c>ResourceGraphTable</c>'s columns that yields it.</param>
public sealed record ResourceGraphColumnDefinition(string Name, KqlType Type, string Sql);

/// <summary>
///     The one table the resource graph's query language sees — <c>resources</c> — declared once for
///     the KQL binder and for the SQL the translator builds from it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The names are Azure Resource Graph's where Azure has the column, and camel case
///         where it does not, because the person typing the query knows Azure's table.</b>
///         <c>name</c>, <c>type</c>, <c>location</c>, <c>resourceGroup</c>, <c>subscriptionId</c>,
///         <c>tags</c> are spelled as ARG spells them; <c>type</c> is <c>{provider}/{type}</c>
///         exactly as ARG's is <c>microsoft.compute/virtualmachines</c>, so <c>type =~
///         'cybercloud.dbforpostgresql/servers'</c> works on the first try. The projection's own
///         snake-case columns (<c>ResourceGraphTable</c>) are the storage, and this table is the
///         view; the two are joined by <see cref="ResourceGraphColumnDefinition.Sql" /> and nowhere
///         else.
///     </para>
///     <para>
///         ⚠ <b>Four columns of the row are not here, and one of Azure's is not here either.</b>
///         <c>access</c> is the filter and never a result — a caller who could read it would read
///         who else may read a resource; <c>is_deleted</c> is applied by the base query, so a query
///         sees live resources and nothing parked or gone; <c>tenant_id</c> is the caller's own and
///         <c>desired_hash</c> and <c>projected_at</c> are the projection's bookkeeping. Azure's
///         <c>id</c> — the full resource path — is not here because the row does not hold a nested
///         type's parent names (<c>ResourceChangedEvent</c> carries <c>Type</c> and <c>Name</c> and
///         not <c>ParentNames</c>), and a path spelled without them would be wrong for every child
///         resource. <see cref="ResourceId" /> is the GUID instead, and docs/plan/08 § The
///         resource-graph projection records the path column as owed with the change that closes it.
///     </para>
///     <para>
///         <c>tags</c> is <c>dynamic</c> to KQL and <c>Map(String, String)</c> to ClickHouse, so
///         <c>tags.env</c> and <c>tags['env']</c> both become <c>tags['env']</c> — a map read that
///         yields <c>''</c> for a key the resource does not carry, which is why <c>isempty(tags.env)</c>
///         is the way to ask "untagged" and there is no null to test for.
///     </para>
/// </remarks>
public static class ResourceGraphSchema {
    /// <summary>The table's KQL name.</summary>
    public const string TableName = "resources";

    /// <summary>The database the binder puts the table in. Never appears in SQL.</summary>
    public const string DatabaseName = "resourcegraph";

    /// <summary>The resource's GUID as a string. The stable key to <c>order by</c> for exact paging.</summary>
    public const string ResourceId = "resourceId";

    /// <summary>The columns, in the order an unprojected query returns them.</summary>
    public static ImmutableArray<ResourceGraphColumnDefinition> Columns { get; } = [
        new(ResourceId, KqlType.String, "toString(resource_id)"),
        new("name", KqlType.String, "name"),
        new("type", KqlType.String, "concat(provider, '/', type)"),
        new("provider", KqlType.String, "provider"),
        new("location", KqlType.String, "location"),
        new("resourceGroup", KqlType.String, "resource_group"),
        new("subscriptionId", KqlType.String, "toString(subscription_id)"),
        new("apiVersion", KqlType.String, "api_version"),
        new("provisioningState", KqlType.String, "provisioning_state"),
        new("clusterId", KqlType.String, "toString(cluster_id)"),
        new("tags", KqlType.Dynamic, "tags"),
        new("createdAt", KqlType.DateTime, "created_at"),
        new("modifiedAt", KqlType.DateTime, "modified_at"),
        new("version", KqlType.Long, "version"),
        new("change", KqlType.String, "change")
    ];

    /// <summary>The columns by name, ordinally.</summary>
    public static FrozenDictionary<string, ResourceGraphColumnDefinition> ByName { get; } =
        Columns.ToFrozenDictionary(x => x.Name, StringComparer.Ordinal);

    /// <summary>
    ///     The binder's view of the world: one database holding one table with these columns and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     <c>GlobalState.Default</c> carries every built-in function and operator Kusto has, so the
    ///     binder resolves <c>ago()</c> or <c>mv-expand</c> happily and the <i>translator</i> is what
    ///     refuses them — by name, with the supported list. Letting the binder see the whole language
    ///     is what makes its diagnostics useful: a query that is well-formed KQL and outside the subset
    ///     gets "not supported", and one that is not KQL at all gets the parser's own sentence.
    /// </remarks>
    public static GlobalState Globals { get; } = GlobalState.Default.WithDatabase(
        new DatabaseSymbol(
            DatabaseName,
            new TableSymbol(TableName, Columns.Select(column => new ColumnSymbol(column.Name, Scalar(column.Type))))
        )
    );

    /// <summary>The Kusto scalar symbol for a column type.</summary>
    /// <param name="type">The type.</param>
    public static ScalarSymbol Scalar(KqlType type) =>
        type switch {
            KqlType.String => ScalarTypes.String,
            KqlType.Long => ScalarTypes.Long,
            KqlType.Real => ScalarTypes.Real,
            KqlType.Bool => ScalarTypes.Bool,
            KqlType.DateTime => ScalarTypes.DateTime,
            KqlType.Dynamic => ScalarTypes.Dynamic,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a resource graph column type.")
        };

    /// <summary>The KQL type a bound expression's result symbol names, or <see cref="KqlType.Unknown" />.</summary>
    /// <param name="symbol">The binder's result type.</param>
    public static KqlType TypeOf(TypeSymbol? symbol) =>
        symbol switch {
            null => KqlType.Unknown,
            _ when symbol == ScalarTypes.String => KqlType.String,
            _ when symbol == ScalarTypes.Long || symbol == ScalarTypes.Int => KqlType.Long,
            _ when symbol == ScalarTypes.Real || symbol == ScalarTypes.Decimal => KqlType.Real,
            _ when symbol == ScalarTypes.Bool => KqlType.Bool,
            _ when symbol == ScalarTypes.DateTime => KqlType.DateTime,
            _ when symbol == ScalarTypes.Dynamic || symbol is DynamicPrimitiveSymbol or DynamicArraySymbol or DynamicBagSymbol => KqlType.Dynamic,
            _ => KqlType.Unknown
        };

    /// <summary>The name the wire gives a KQL type — lower case, as Kusto spells it.</summary>
    /// <param name="type">The type.</param>
    public static string WireName(KqlType type) =>
        type switch {
            KqlType.String => "string",
            KqlType.Long => "long",
            KqlType.Real => "real",
            KqlType.Bool => "bool",
            KqlType.DateTime => "datetime",
            KqlType.Dynamic => "dynamic",
            _ => "unknown"
        };
}
