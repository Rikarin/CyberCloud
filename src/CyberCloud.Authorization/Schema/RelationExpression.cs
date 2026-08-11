using System.Collections.Immutable;

namespace CyberCloud.Authorization;

/// <summary>
///     How a relation or a permission is <i>computed</i> — the rewrite expression of docs/plan/07
///     § The model.
/// </summary>
/// <remarks>
///     <para>
///         Five node kinds, which is the whole of Zanzibar's userset rewrite as docs/plan/07 uses
///         it: <see cref="ThisExpression" /> (direct tuples), <see cref="RelationRefExpression" />
///         (another name on the same object), <see cref="TuplesetExpression" />
///         (tupleset-to-userset — the <c>From(x, y)</c> that is the whole of hierarchical
///         inheritance), <see cref="UnionExpression" /> and <see cref="IntersectionExpression" />,
///         plus <see cref="ExclusionExpression" /> for the one restricted use of <c>!</c>.
///     </para>
///     <para>
///         Expressions are immutable and are built with <c>|</c>, <c>&amp;</c> and <c>!</c> so that
///         the schema reads the way docs/plan/07 § The model writes it. The named alternates
///         (<see cref="Or" />, <see cref="And" />, <see cref="Negate" />) exist for callers in
///         languages without operator overloading and for CA2225.
///     </para>
/// </remarks>
public abstract class RelationExpression {
    /// <summary>The immediate children of this node, for a walk.</summary>
    public abstract IReadOnlyList<RelationExpression> Children { get; }

    private protected RelationExpression() { }

    /// <summary>Union — <c>a | b</c>. "Either way of having it counts."</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static RelationExpression operator |(RelationExpression left, RelationExpression right) => Or(left, right);

    /// <summary>Intersection — <c>a &amp; b</c>. "Both are required."</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static RelationExpression operator &(RelationExpression left, RelationExpression right) => And(left, right);

    /// <summary>
    ///     Exclusion — <c>!a</c>. ⚠ Legal only at the top level of a <b>permission</b>, over a
    ///     relation computed from direct tuples on the same object. The schema builder rejects
    ///     anything else; see <see cref="SchemaBuilder" />.
    /// </summary>
    /// <param name="operand">The operand.</param>
    public static RelationExpression operator !(RelationExpression operand) => Negate(operand);

    /// <summary>The named alternate for <c>|</c>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static RelationExpression Or(RelationExpression left, RelationExpression right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // Flattened, so `a | b | c` is one node with three operands rather than a left-leaning
        // tree. That matters for the top-level negation rule: "the direct children of the root"
        // has to mean the same thing however the author parenthesised the expression.
        var operands = ImmutableArray.CreateBuilder<RelationExpression>();
        operands.AddRange(left is UnionExpression leftUnion ? leftUnion.Operands : [left]);
        operands.AddRange(right is UnionExpression rightUnion ? rightUnion.Operands : [right]);
        return new UnionExpression(operands.ToImmutable());
    }

    /// <summary>The named alternate for <c>&amp;</c>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static RelationExpression And(RelationExpression left, RelationExpression right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var operands = ImmutableArray.CreateBuilder<RelationExpression>();
        operands.AddRange(left is IntersectionExpression leftAnd ? leftAnd.Operands : [left]);
        operands.AddRange(right is IntersectionExpression rightAnd ? rightAnd.Operands : [right]);
        return new IntersectionExpression(operands.ToImmutable());
    }

    /// <summary>The named alternate for <c>!</c>.</summary>
    /// <param name="operand">The operand.</param>
    public static RelationExpression Negate(RelationExpression operand) {
        ArgumentNullException.ThrowIfNull(operand);
        return new ExclusionExpression(operand);
    }

    /// <summary>This node and everything under it, pre-order.</summary>
    public IEnumerable<RelationExpression> DescendantsAndSelf() {
        yield return this;

        foreach (var child in Children) {
            foreach (var descendant in child.DescendantsAndSelf()) {
                yield return descendant;
            }
        }
    }
}

/// <summary>
///     <c>This</c> — the direct tuples written against this relation on this object. docs/plan/07
///     § Check, step 2.
/// </summary>
public sealed class ThisExpression : RelationExpression {
    /// <inheritdoc />
    public override IReadOnlyList<RelationExpression> Children => [];

    internal ThisExpression() { }

    /// <inheritdoc />
    public override string ToString() => "This";
}

/// <summary>
///     <c>Rel(name)</c> — another relation or permission <b>on the same object</b>.
/// </summary>
public sealed class RelationRefExpression : RelationExpression {
    /// <summary>The name referred to.</summary>
    public string Relation { get; }

    /// <inheritdoc />
    public override IReadOnlyList<RelationExpression> Children => [];

    internal RelationRefExpression(string relation) {
        Relation = relation;
    }

    /// <inheritdoc />
    public override string ToString() => $"Rel(\"{Relation}\")";
}

/// <summary>
///     <c>From(tupleset, computed)</c> — Zanzibar's tupleset-to-userset: "whoever has
///     <c>computed</c> on the object I point to via <c>tupleset</c>".
/// </summary>
/// <remarks>
///     docs/plan/07 § The model:
///     <i>
///         "It is the whole of hierarchical inheritance and it is why a
///         role assignment at a subscription grants on every resource group in it without any tuple
///         being written per resource."
///     </i>
///     <c>RoleAssignmentViewTests</c> asserts exactly that
///     sentence, including the "no tuple written" half.
/// </remarks>
public sealed class TuplesetExpression : RelationExpression {
    /// <summary>The relation on <i>this</i> object whose subjects are the objects to follow.</summary>
    public string Tupleset { get; }

    /// <summary>The name to evaluate on each of those objects.</summary>
    public string Computed { get; }

    /// <inheritdoc />
    public override IReadOnlyList<RelationExpression> Children => [];

    internal TuplesetExpression(string tupleset, string computed) {
        Tupleset = tupleset;
        Computed = computed;
    }

    /// <inheritdoc />
    public override string ToString() => $"From(\"{Tupleset}\", \"{Computed}\")";
}

/// <summary>A union. True when any operand is true; the evaluator short-circuits.</summary>
public sealed class UnionExpression : RelationExpression {
    /// <summary>The operands, flattened.</summary>
    public ImmutableArray<RelationExpression> Operands { get; }

    /// <inheritdoc />
    public override IReadOnlyList<RelationExpression> Children => Operands;

    internal UnionExpression(ImmutableArray<RelationExpression> operands) {
        Operands = operands;
    }

    /// <inheritdoc />
    public override string ToString() => "(" + string.Join(" | ", Operands) + ")";
}

/// <summary>An intersection. True when every operand is true; the evaluator short-circuits on false.</summary>
public sealed class IntersectionExpression : RelationExpression {
    /// <summary>The operands, flattened.</summary>
    public ImmutableArray<RelationExpression> Operands { get; }

    /// <inheritdoc />
    public override IReadOnlyList<RelationExpression> Children => Operands;

    internal IntersectionExpression(ImmutableArray<RelationExpression> operands) {
        Operands = operands;
    }

    /// <inheritdoc />
    public override string ToString() => "(" + string.Join(" & ", Operands) + ")";
}

/// <summary>
///     An exclusion — <c>!a</c>. ⚠
///     <b>
///         The only non-monotone node, and the one the schema builder
///         spends most of its rules on.
///     </b>
/// </summary>
/// <remarks>
///     docs/plan/07 § Caching across requests:
///     <i>
///         "A permission of the form <c>A &amp; !B</c> is not
///         monotone: adding a tuple can remove access. Any cache that assumes 'more tuples can only
///         grant more' is wrong in the presence of <c>!</c>."
///     </i>
///     The restriction that makes the cache
///     safe is enforced by <see cref="SchemaBuilder" />, not by this type — see its remarks.
/// </remarks>
public sealed class ExclusionExpression : RelationExpression {
    /// <summary>What is being excluded.</summary>
    public RelationExpression Operand { get; }

    /// <inheritdoc />
    public override IReadOnlyList<RelationExpression> Children => [Operand];

    internal ExclusionExpression(RelationExpression operand) {
        Operand = operand;
    }

    /// <inheritdoc />
    public override string ToString() => "!" + Operand;
}
