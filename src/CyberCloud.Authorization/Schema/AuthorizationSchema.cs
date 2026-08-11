using System.Collections.Frozen;
using System.Collections.Immutable;

namespace CyberCloud.Authorization;

/// <summary>
///     One name on one object type, and how it is computed.
/// </summary>
/// <param name="Name">The relation or permission name.</param>
/// <param name="Expression">The rewrite.</param>
/// <param name="IsPermission">Whether it is a permission rather than a relation.</param>
/// <param name="IsRole">
///     Whether it is a <b>named role</b> for the Azure view — docs/plan/07 § Azure RBAC, expressed
///     in it: "the API can present <c>GET /roleAssignments</c> by listing tuples whose relation is a
///     named role".
/// </param>
public sealed record SchemaMember(
    string Name,
    RelationExpression Expression,
    bool IsPermission,
    bool IsRole)
{
    /// <summary>
    ///     Whether this member is computed from direct tuples on this object and nothing else.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This predicate is what makes negation safe to cache.</b> docs/plan/07 § Caching
    ///     across requests permits <c>!</c> only "over a relation that is computed from direct
    ///     tuples on the same object", because that keeps invalidation to "the same object
    ///     changed", which the tenant version stamp already covers. It is also why a negated
    ///     operand can never be truncated by a cap: evaluating it does no recursion and no I/O
    ///     beyond the object's own tuple read.
    /// </remarks>
    public bool IsDirectOnly { get; } =
        Expression.DescendantsAndSelf().All(x => x is ThisExpression or UnionExpression);

    /// <summary>Whether the rewrite contains an exclusion anywhere.</summary>
    public bool ContainsNegation { get; } =
        Expression.DescendantsAndSelf().Any(x => x is ExclusionExpression);
}

/// <summary>One object type and its members.</summary>
public sealed class SchemaType
{
    readonly FrozenDictionary<string, SchemaMember> members;

    internal SchemaType(string name, IEnumerable<SchemaMember> members)
    {
        Name = name;
        this.members = members.ToFrozenDictionary(x => x.Name, StringComparer.Ordinal);
        Relations = [.. this.members.Values.Where(x => !x.IsPermission).Select(x => x.Name).Order(StringComparer.Ordinal)];
        Permissions = [.. this.members.Values.Where(x => x.IsPermission).Select(x => x.Name).Order(StringComparer.Ordinal)];
        Roles = [.. this.members.Values.Where(x => x.IsRole).Select(x => x.Name).Order(StringComparer.Ordinal)];
    }

    /// <summary>The type name.</summary>
    public string Name { get; }

    /// <summary>The relation names, ordinally sorted.</summary>
    public ImmutableArray<string> Relations { get; }

    /// <summary>The permission names, ordinally sorted.</summary>
    public ImmutableArray<string> Permissions { get; }

    /// <summary>The relation names that are named roles, ordinally sorted.</summary>
    public ImmutableArray<string> Roles { get; }

    /// <summary>Every member.</summary>
    public IEnumerable<SchemaMember> Members => members.Values;

    /// <summary>A member by name, or <see langword="null" />.</summary>
    /// <param name="name">The relation or permission name.</param>
    public SchemaMember? Member(string name) => members.TryGetValue(name, out var member) ? member : null;
}

/// <summary>
///     A built, immutable authorization schema — docs/plan/07 § The model, concept three.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An instance of this type has already passed every rule in
///         <see cref="SchemaBuilder" />.</b> It cannot be constructed any other way: the constructor
///         is internal and <see cref="SchemaBuilder.Build" /> throws rather than returning an
///         invalid one. So code downstream — the evaluator especially — may assume the negation
///         restriction holds, and does.
///     </para>
///     <para>
///         <see cref="Version" /> is one of the components docs/plan/07 § Caching across requests
///         puts in the check cache key. It is the caller's number and it must be bumped whenever the
///         schema changes, because a cached answer computed under a different rewrite is not an
///         answer to the same question.
///     </para>
/// </remarks>
public sealed class AuthorizationSchema
{
    readonly FrozenDictionary<string, SchemaType> types;

    internal AuthorizationSchema(int version, IEnumerable<SchemaType> types)
    {
        Version = version;
        this.types = types.ToFrozenDictionary(x => x.Name, StringComparer.Ordinal);
        TypeNames = [.. this.types.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>The schema version — a component of the check cache key.</summary>
    public int Version { get; }

    /// <summary>Every object type, ordinally sorted.</summary>
    public ImmutableArray<string> TypeNames { get; }

    /// <summary>A type by name, or <see langword="null" />.</summary>
    /// <param name="type">The type name.</param>
    public SchemaType? Type(string type) => types.TryGetValue(type, out var found) ? found : null;

    /// <summary>Whether the schema defines <paramref name="type" />.</summary>
    /// <param name="type">The type name.</param>
    public bool HasType(string type) => types.ContainsKey(type);

    /// <summary>
    ///     The member <paramref name="name" /> on <paramref name="type" />, or
    ///     <see langword="null" /> if either is unknown.
    /// </summary>
    /// <param name="type">The type name.</param>
    /// <param name="name">The relation or permission name.</param>
    public SchemaMember? Member(string type, string name) => Type(type)?.Member(name);
}
