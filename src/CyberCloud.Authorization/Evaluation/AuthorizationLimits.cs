namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     The two caps of docs/plan/07 § Check, step 5: <b>depth 12</b>, <b>breadth 1 000 per level</b>.
/// </summary>
/// <remarks>
///     <para>
///         <b>What "depth" counts, stated because the document does not.</b> An <i>object hop</i>:
///         following a <c>From(tupleset, computed)</c> to another object, or recursing into a
///         userset subject. It does <b>not</b> count same-object rewrites (<c>Rel(x)</c>, the
///         operands of a union or an intersection), because those cannot recurse without bound —
///         the schema has finitely many names per type and the memo makes each
///         <c>(object, relation, subject)</c> triple terminal. Counting them would make the cap
///         depend on how the schema author happened to factor an expression, which is not a
///         property anyone can reason about.
///     </para>
///     <para>
///         So <see cref="MaxDepth" /> = 12 means twelve hops away from the object being checked. A
///         chain of twelve <c>parent</c> edges resolves; thirteen does not.
///     </para>
///     <para>
///         <b>What "breadth" counts.</b> The number of <i>recursive expansions</i> at one node: the
///         userset subjects a <c>This</c> node has to walk into, or the targets a <c>From</c> node
///         has to follow. It does <b>not</b> count a direct, concrete subject match, which is a set
///         test over tuples already read and costs no grain call. Counting those would deny
///         <c>read</c> on an object with 1 001 direct readers, which is a cap doing harm rather than
///         work.
///     </para>
///     <para>
///         ⚠ <b>The walk does not stop dead at the cap — it stops <i>after</i> the cap.</b> Up to
///         <see cref="MaxBreadth" /> expansions are evaluated, short-circuiting on the first true;
///         only if none of them is true and there were more does the node report a truncation. A
///         node that refused to look at anything once it was over budget would turn a legitimate
///         subject sitting in position 3 into a denial.
///     </para>
/// </remarks>
public sealed record AuthorizationLimits
{
    /// <summary>docs/plan/07 § Check: "Depth cap 12".</summary>
    public int MaxDepth { get; init; } = 12;

    /// <summary>docs/plan/07 § Check: "breadth cap 1 000 per level".</summary>
    public int MaxBreadth { get; init; } = 1_000;

    /// <summary>The document's numbers. The only instance production uses.</summary>
    public static AuthorizationLimits Default { get; } = new();
}
