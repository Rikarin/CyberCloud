using CyberCloud.Authorization.Contracts;

namespace CyberCloud.Authorization.Tests.Generated;

/// <summary>
///     A slow, obviously-correct evaluator: iterate to a least fixed point, then read the answer off.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § Testing asks for
///         <i>
///             "<c>Check</c> agrees with a slow, obviously-correct
///             reference evaluator on … random graphs including cycles, deep nesting, and negation"
///         </i>
///         .
///         This is that evaluator, and "obviously correct" is the design goal rather than a
///         compliment: it has <b>no memo</b>, <b>no short-circuit</b>, <b>no depth cap</b>,
///         <b>
///             no
///             cycle handling of any kind
///         </b>
///         , and no shared code with
///         <c>CyberCloud.Authorization.Evaluation</c>. If the two agree, they agree for reasons that
///         are not a common bug.
///     </para>
///     <para>
///         <b>How it handles cycles: it does not have to.</b> Naive iteration to a fixed point over
///         a monotone rule set is the textbook definition of the least fixed point, and a cycle is
///         a set of facts that never become true. That is precisely the semantics
///         <c>CheckEvaluator</c>'s in-progress-false is trying to reproduce with a stack, and
///         comparing the two is the point.
///     </para>
///     <para>
///         <b>
///             How it handles negation: stratification, which the schema builder has already
///             guaranteed.
///         </b>
///         Nothing a relation or a negation-free permission references may carry a
///         <c>!</c> (<c>SchemaBuilder</c>, rule 11), so the rule set splits cleanly in two: stratum
///         0 is everything without a negation and is monotone, so it is iterated to a fixed point;
///         stratum 1 is the permissions that carry one and is evaluated exactly once on top. That is
///         the standard semantics of a stratified program and it is well defined — which is the real
///         payoff of the restriction docs/plan/07 § Caching across requests imposes.
///     </para>
/// </remarks>
public static class ReferenceEvaluator {
    /// <summary>Whether <paramref name="subject" /> has <paramref name="name" /> on <paramref name="target" />.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="tuples">Every tuple in the world.</param>
    /// <param name="target">The object.</param>
    /// <param name="name">The permission or relation.</param>
    /// <param name="subject">The subject.</param>
    public static bool Evaluate(
        AuthorizationSchema schema,
        IReadOnlyList<RelationTuple> tuples,
        ObjectRef target,
        string name,
        SubjectRef subject
    ) {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(tuples);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(subject);

        var universe = Universe(tuples, target, subject);
        var facts = Fixpoint(schema, tuples, universe, subject);

        var member = schema.Member(target.Type, name);
        if (member is null) {
            return false;
        }

        return member.ContainsNegation
            ? Eval(schema, tuples, target, name, member.Expression, subject, facts)
            : facts.Contains((target.ToString(), name));
    }

    /// <summary>Every object any tuple or the query mentions.</summary>
    static List<ObjectRef> Universe(
        IReadOnlyList<RelationTuple> tuples,
        ObjectRef target,
        SubjectRef subject
    ) {
        Dictionary<string, ObjectRef> found = new(StringComparer.Ordinal) {
            [target.ToString()] = target, [subject.Object.ToString()] = subject.Object
        };

        foreach (var tuple in tuples) {
            found[tuple.Object.ToString()] = tuple.Object;
            found[tuple.Subject.Object.ToString()] = tuple.Subject.Object;
        }

        return [.. found.Values];
    }

    /// <summary>
    ///     Stratum 0: iterate every negation-free member of every object until nothing new becomes
    ///     true.
    /// </summary>
    static HashSet<(string Object, string Name)> Fixpoint(
        AuthorizationSchema schema,
        IReadOnlyList<RelationTuple> tuples,
        List<ObjectRef> universe,
        SubjectRef subject
    ) {
        HashSet<(string Object, string Name)> facts = [];

        bool changed;
        do {
            changed = false;

            foreach (var target in universe) {
                var type = schema.Type(target.Type);
                if (type is null) {
                    continue;
                }

                foreach (var member in type.Members) {
                    if (member.ContainsNegation
                        || facts.Contains((target.ToString(), member.Name))) {
                        continue;
                    }

                    if (Eval(schema, tuples, target, member.Name, member.Expression, subject, facts)) {
                        facts.Add((target.ToString(), member.Name));
                        changed = true;
                    }
                }
            }
        } while (changed);

        return facts;
    }

    static bool Eval(
        AuthorizationSchema schema,
        IReadOnlyList<RelationTuple> tuples,
        ObjectRef target,
        string relation,
        RelationExpression expression,
        SubjectRef subject,
        HashSet<(string Object, string Name)> facts
    ) =>
        expression switch {
            ThisExpression => Direct(tuples, target, relation, subject, facts),

            RelationRefExpression reference =>
                facts.Contains((target.ToString(), reference.Relation)),

            TuplesetExpression tupleset => Subjects(tuples, target, tupleset.Tupleset)
                .Any(x => facts.Contains((x.Object.ToString(), tupleset.Computed))),

            UnionExpression union => union.Operands.Any(x =>
                Eval(schema, tuples, target, relation, x, subject, facts)
            ),

            IntersectionExpression intersection => intersection.Operands.All(x =>
                Eval(schema, tuples, target, relation, x, subject, facts)
            ),

            ExclusionExpression exclusion => !Eval(schema, tuples, target, relation, exclusion.Operand, subject, facts),

            _ => false
        };

    static bool Direct(
        IReadOnlyList<RelationTuple> tuples,
        ObjectRef target,
        string relation,
        SubjectRef subject,
        HashSet<(string Object, string Name)> facts
    ) {
        foreach (var candidate in Subjects(tuples, target, relation)) {
            if (candidate == subject) {
                return true;
            }

            if (candidate.IsUserset
                && facts.Contains((candidate.Object.ToString(), candidate.Relation))) {
                return true;
            }
        }

        return false;
    }

    static IEnumerable<SubjectRef> Subjects(
        IReadOnlyList<RelationTuple> tuples,
        ObjectRef target,
        string relation
    ) =>
        tuples
            .Where(x => x.Object == target
                && string.Equals(x.Relation, relation, StringComparison.Ordinal)
            )
            .Select(x => x.Subject);
}
