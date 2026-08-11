namespace CyberCloud.Authorization;

/// <summary>
///     The three rewrite atoms, spelled the way docs/plan/07 § The model spells them. Import with
///     <c>using static CyberCloud.Authorization.Rewrite;</c>.
/// </summary>
/// <example>
///     <code>
///     using static CyberCloud.Authorization.Rewrite;
///
///     Schema.DefineType("resourceGroup")
///         .Relation("parent")
///         .Relation("owner",       This | From("parent", "owner"))
///         .Relation("contributor", This | From("parent", "contributor") | Rel("owner"))
///         .Permission("assignRole", Rel("owner") &amp; !Rel("suspended"));
///     </code>
/// </example>
public static class Rewrite
{
    /// <summary>The direct tuples on this relation and this object.</summary>
    public static RelationExpression This { get; } = new ThisExpression();

    /// <summary>Another relation or permission on the same object.</summary>
    /// <param name="relation">The name.</param>
    public static RelationExpression Rel(string relation) => new RelationRefExpression(relation);

    /// <summary>
    ///     Tupleset-to-userset: "whoever has <paramref name="computed" /> on the object I point to
    ///     via <paramref name="tupleset" />".
    /// </summary>
    /// <param name="tupleset">A <b>direct</b> relation on this object — see <see cref="SchemaBuilder" />.</param>
    /// <param name="computed">The name to evaluate on each object that relation points to.</param>
    public static RelationExpression From(string tupleset, string computed) =>
        new TuplesetExpression(tupleset, computed);
}
