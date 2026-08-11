using System.Globalization;
using CyberCloud.Authorization.Contracts;
using static CyberCloud.Authorization.Rewrite;

namespace CyberCloud.Authorization.Tests.Generated;

/// <summary>One generated graph: a schema, a tuple set, and the queries to ask of it.</summary>
/// <param name="Seed">The seed that produced it. A failure is reproducible from this alone.</param>
/// <param name="Schema">The schema.</param>
/// <param name="Tuples">The tuples.</param>
/// <param name="Queries">The <c>(object, name, subject)</c> triples to compare.</param>
public sealed record GeneratedGraph(
    int Seed,
    AuthorizationSchema Schema,
    IReadOnlyList<RelationTuple> Tuples,
    IReadOnlyList<(ObjectRef Object, string Name, SubjectRef Subject)> Queries)
{
    /// <summary>The graph as text, for a failure message that can be pasted into the corpus.</summary>
    public string Describe() =>
        "seed " + Seed.ToString(CultureInfo.InvariantCulture) + Environment.NewLine
        + "schema:" + Environment.NewLine
        + string.Join(
            Environment.NewLine,
            Schema.TypeNames.SelectMany(type =>
                Schema.Type(type)!.Members.Select(m =>
                    $"  {type}#{m.Name} {(m.IsPermission ? "(permission)" : "(relation)")} = {m.Expression}")))
        + Environment.NewLine + "tuples:" + Environment.NewLine
        + string.Join(Environment.NewLine, Tuples.Select(x => "  " + x));
}

/// <summary>
///     A deterministic generator of ReBAC graphs — <b>including cycles, deep nesting and
///     negation</b>, which is what docs/plan/07 § Testing asks the property test to cover.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Hand-rolled rather than FsCheck/CsCheck, for the reason
///         <c>CyberCloud.Core.Tests.Corpus</c> already records:</b> central package management
///         forbids an unpinned version and its own header says a package outside docs/plan/02's
///         register needs an ADR. Writing an ADR to get shrinking is not the right trade; a seeded
///         generator gives the same coverage, is reproducible from the seed printed in the failure
///         message, and adds no dependency.
///     </para>
///     <para>
///         <b>What makes cycles likely rather than possible.</b> Every object is drawn from a small
///         pool and a tupleset edge may point at <i>any</i> object including itself, so a
///         <c>parent</c> chain closes into a loop constantly. Userset subjects are drawn from the
///         same pool, so group nesting loops too. At six objects and twelve tuples the majority of
///         generated graphs contain at least one cycle; <c>MostGeneratedGraphsContainACycle</c>
///         asserts that rather than hoping for it, because a "cycle" property test over acyclic
///         graphs is the classic way to test nothing at all.
///     </para>
///     <para>
///         <b>What the generator will not produce: an invalid schema.</b> Every rewrite is assembled
///         from pieces that satisfy <c>SchemaBuilder</c>'s rules by construction — <c>This</c> only
///         in relations, tuplesets only over direct relations, negation only at a permission's top
///         level over a direct relation on the same type. <c>EveryGeneratedSchemaBuilds</c> is the
///         check on that claim.
///     </para>
/// </remarks>
public static class RandomGraphs
{
    /// <summary>The object types generated schemas define.</summary>
    public static IReadOnlyList<string> Types { get; } = ["ta", "tb", "tc"];

    /// <summary>The direct relations every generated type declares.</summary>
    public static IReadOnlyList<string> DirectRelations { get; } = ["parent", "da", "db"];

    /// <summary>The computed relations every generated type declares.</summary>
    public static IReadOnlyList<string> ComputedRelations { get; } = ["ca", "cb", "cc"];

    /// <summary>The permissions every generated type declares.</summary>
    public static IReadOnlyList<string> PermissionNames { get; } = ["pa", "pb", "pc"];

    /// <summary>Generates one graph.</summary>
    /// <param name="seed">The seed. The same seed always produces the same graph.</param>
    public static GeneratedGraph Generate(int seed)
    {
        var random = new Random(seed);
        var schema = BuildSchema(random);
        var objects = Objects(random);
        var subjects = Subjects();
        var tuples = BuildTuples(random, objects, subjects);

        List<(ObjectRef, string, SubjectRef)> queries = [];
        for (var i = 0; i < 4; i++)
        {
            var target = objects[random.Next(objects.Count)];
            var names = Names(random);
            queries.Add((target, names, subjects[random.Next(subjects.Count)]));
        }

        return new GeneratedGraph(seed, schema, tuples, queries);
    }

    static string Names(Random random) =>
        random.Next(4) == 0
            ? ComputedRelations[random.Next(ComputedRelations.Count)]
            : PermissionNames[random.Next(PermissionNames.Count)];

    static AuthorizationSchema BuildSchema(Random random)
    {
        var builder = Schema.Create(1);

        foreach (var type in Types)
        {
            var scope = builder.DefineType(type);

            foreach (var direct in DirectRelations)
            {
                scope.Relation(direct);
            }

            // Computed relations. `This` is legal here and nowhere else; the tupleset is always a
            // direct relation, which is SchemaBuilder rule 4.
            foreach (var computed in ComputedRelations)
            {
                scope.Relation(computed, RelationRewrite(random, depth: 0));
            }

            // Permissions. Never `This`; sometimes a top-level negation over a direct relation on
            // the same object, which is the only shape docs/plan/07 allows.
            foreach (var permission in PermissionNames)
            {
                var positive = PermissionRewrite(random);

                scope.Permission(
                    permission,
                    random.Next(3) == 0
                        ? positive & !Rel(DirectRelations[random.Next(DirectRelations.Count)])
                        : positive);
            }
        }

        return builder.Build();
    }

    static RelationExpression RelationRewrite(Random random, int depth)
    {
        if (depth >= 2)
        {
            return This;
        }

        return random.Next(6) switch
        {
            0 => This,
            1 => Rel(Pick(random, ComputedRelations)),
            2 => From(Pick(random, DirectRelations), Pick(random, ComputedRelations)),
            3 => This | From(Pick(random, DirectRelations), Pick(random, ComputedRelations)),
            4 => RelationRewrite(random, depth + 1) | RelationRewrite(random, depth + 1),
            _ => RelationRewrite(random, depth + 1) & RelationRewrite(random, depth + 1),
        };
    }

    static RelationExpression PermissionRewrite(Random random) =>
        random.Next(4) switch
        {
            0 => Rel(Pick(random, ComputedRelations)),
            1 => Rel(Pick(random, ComputedRelations)) | Rel(Pick(random, DirectRelations)),
            2 => Rel(Pick(random, ComputedRelations)) & Rel(Pick(random, ComputedRelations)),
            _ => Rel(Pick(random, ComputedRelations))
                | (Rel(Pick(random, ComputedRelations)) & Rel(Pick(random, ComputedRelations))),
        };

    static List<ObjectRef> Objects(Random random)
    {
        var count = 3 + random.Next(4);
        List<ObjectRef> objects = [];

        for (var i = 0; i < count; i++)
        {
            objects.Add(ObjectRef.Of(
                Types[i % Types.Count], "o" + i.ToString(CultureInfo.InvariantCulture)));
        }

        return objects;
    }

    /// <summary>
    ///     Two subjects, not twenty. A small pool is what makes a generated tuple likely to be
    ///     ABOUT the subject a query asks about — with a large pool almost every comparison would
    ///     be a deny, and a property test that only ever produces denials would pass against an
    ///     evaluator that returns false unconditionally.
    /// </summary>
    static List<SubjectRef> Subjects() => [SubjectRef.Of("ta", "u0"), SubjectRef.Of("tb", "u1")];

    static List<RelationTuple> BuildTuples(
        Random random, List<ObjectRef> objects, List<SubjectRef> subjects)
    {
        var count = 6 + random.Next(14);
        List<RelationTuple> tuples = [];

        for (var i = 0; i < count; i++)
        {
            var target = objects[random.Next(objects.Count)];

            // Tuples are written against relations, never permissions — SchemaBuilder's rule and
            // TupleStoreGrain's validation.
            var relation = random.Next(3) == 0
                ? Pick(random, ComputedRelations)
                : Pick(random, DirectRelations);

            SubjectRef subject;
            var roll = random.Next(10);
            if (roll < 6)
            {
                subject = subjects[random.Next(subjects.Count)];
            }
            else if (roll < 9)
            {
                // A userset subject — the source of group nesting and of most cycles.
                var userset = objects[random.Next(objects.Count)];
                subject = SubjectRef.Userset(
                    userset.Type,
                    userset.Id,
                    random.Next(2) == 0
                        ? Pick(random, ComputedRelations)
                        : Pick(random, DirectRelations));
            }
            else
            {
                // A concrete object as a subject — what a tupleset edge points at.
                var pointed = objects[random.Next(objects.Count)];
                subject = SubjectRef.Of(pointed.Type, pointed.Id);
            }

            var tuple = RelationTuple.Create(target, relation, subject);
            tuples.Add(tuple.GetValueOrThrow());
        }

        return tuples;
    }

    static string Pick(Random random, IReadOnlyList<string> from) => from[random.Next(from.Count)];
}
