using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>
///     The same tuple set as <see cref="InMemoryRelationReader" />, indexed the other way — what
///     <c>ISubjectRelationsGrain</c> would hold for each subject object.
/// </summary>
/// <remarks>
///     Built from the tuples exactly as <c>TupleStoreGrain</c>'s step 4 builds a
///     <see cref="SubjectIndexEntry" />: the subject's object half is the key, and its userset
///     relation is the entry's <see cref="SubjectIndexEntry.SubjectRelation" />. ⚠ The evaluator
///     under test is the evaluator that ships; only the two tuple sources differ.
/// </remarks>
public sealed class InMemoryReverseRelationReader : IReverseRelationReader {
    readonly Dictionary<ObjectRef, List<SubjectIndexEntry>> bySubject = [];

    /// <summary>How many reverse reads the walk has made.</summary>
    public int Reads { get; private set; }

    /// <summary>Builds a reader over a tuple set.</summary>
    /// <param name="tuples">The tuples.</param>
    public InMemoryReverseRelationReader(IEnumerable<RelationTuple> tuples) {
        ArgumentNullException.ThrowIfNull(tuples);

        foreach (var tuple in tuples) {
            if (!bySubject.TryGetValue(tuple.Subject.Object, out var entries)) {
                entries = [];
                bySubject[tuple.Subject.Object] = entries;
            }

            SubjectIndexEntry entry = new() {
                Object = tuple.Object, Relation = tuple.Relation, SubjectRelation = tuple.Subject.Relation
            };

            if (!entries.Contains(entry)) {
                entries.Add(entry);
            }
        }
    }

    /// <summary>Builds a reader from the tuple grammar.</summary>
    /// <param name="tuples">Tuples as <c>object#relation@subject</c>.</param>
    public static InMemoryReverseRelationReader Parse(params string[] tuples) =>
        new(tuples.Select(x => RelationTuple.Parse(x).GetValueOrThrow()));

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) {
        Reads++;

        IReadOnlyList<SubjectIndexEntry> entries = bySubject.TryGetValue(subjectObject, out var found)
            ? [.. found]
            : [];

        return ValueTask.FromResult(Result<IReadOnlyList<SubjectIndexEntry>>.Success(entries));
    }
}
