using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>
///     A tuple set in memory, behind the same <see cref="IRelationReader" /> the grains implement.
/// </summary>
/// <remarks>
///     ⚠ <b>The evaluator under test is the evaluator that ships.</b> Only the tuple source is
///     different, which is what makes tens of thousands of generated graphs affordable — see this
///     project's .csproj.
/// </remarks>
public sealed class InMemoryRelationReader : IRelationReader
{
    readonly Dictionary<string, Dictionary<string, List<SubjectRef>>> byObject =
        new(StringComparer.Ordinal);

    /// <summary>Builds a reader over a tuple set.</summary>
    /// <param name="tuples">The tuples.</param>
    public InMemoryRelationReader(IEnumerable<RelationTuple> tuples)
    {
        ArgumentNullException.ThrowIfNull(tuples);

        foreach (var tuple in tuples)
        {
            var key = tuple.Object.ToString();
            if (!byObject.TryGetValue(key, out var relations))
            {
                relations = new Dictionary<string, List<SubjectRef>>(StringComparer.Ordinal);
                byObject[key] = relations;
            }

            if (!relations.TryGetValue(tuple.Relation, out var subjects))
            {
                subjects = [];
                relations[tuple.Relation] = subjects;
            }

            if (!subjects.Contains(tuple.Subject))
            {
                subjects.Add(tuple.Subject);
            }
        }
    }

    /// <summary>Builds a reader from the tuple grammar.</summary>
    /// <param name="tuples">Tuples as <c>object#relation@subject</c>.</param>
    public static InMemoryRelationReader Parse(params string[] tuples) =>
        new(tuples.Select(x => RelationTuple.Parse(x).GetValueOrThrow()));

    /// <summary>How many object reads the walk has made. One per distinct object per request.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public ValueTask<Result<ObjectRelationsSnapshot>> ReadAsync(
        ObjectRef target, CancellationToken cancellationToken)
    {
        Reads++;

        var relations = byObject.TryGetValue(target.ToString(), out var found)
            ? found
            : [];

        Dictionary<string, IReadOnlyList<SubjectRef>> snapshot = new(StringComparer.Ordinal);
        var count = 0;

        foreach (var (relation, subjects) in relations)
        {
            snapshot[relation] = subjects;
            count += subjects.Count;
        }

        return ValueTask.FromResult(Result<ObjectRelationsSnapshot>.Success(
            new ObjectRelationsSnapshot
            {
                Object = target,
                ByRelation = snapshot,
                Count = count,
            }));
    }
}
