using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>
///     A tuple set in memory, behind the same <see cref="IRelationReader" /> the grains implement.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The evaluator under test is the evaluator that ships.</b> Only the tuple source is
///         different, which is what makes tens of thousands of generated graphs affordable — see this
///         project's .csproj.
///     </para>
///     <para>
///         Expiry is applied the way <c>ObjectRelationsGrain</c> applies it: a tuple whose
///         <see cref="RelationTuple.ExpiresOn" /> is not later than <see cref="Now" /> is left out
///         of every snapshot, and a live one's expiry travels in
///         <see cref="ObjectRelationsSnapshot.Expiries" />. A tuple given twice keeps the expiry of
///         the last one, which is what a rewrite through the store does.
///     </para>
/// </remarks>
public sealed class InMemoryRelationReader : IRelationReader {
    readonly Dictionary<string, Dictionary<string, List<SubjectRef>>> byObject =
        new(StringComparer.Ordinal);

    readonly Dictionary<string, Dictionary<string, DateTimeOffset>> expiries = new(StringComparer.Ordinal);

    /// <summary>How many object reads the walk has made. One per distinct object per request.</summary>
    public int Reads { get; private set; }

    /// <summary>
    ///     The instant reads are made at. <see cref="DateTimeOffset.MinValue" /> — the default —
    ///     makes every expiring tuple live.
    /// </summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Builds a reader over a tuple set.</summary>
    /// <param name="tuples">The tuples.</param>
    public InMemoryRelationReader(IEnumerable<RelationTuple> tuples) {
        ArgumentNullException.ThrowIfNull(tuples);

        foreach (var tuple in tuples) {
            var key = tuple.Object.ToString();
            if (!byObject.TryGetValue(key, out var relations)) {
                relations = new(StringComparer.Ordinal);
                byObject[key] = relations;
                expiries[key] = new(StringComparer.Ordinal);
            }

            if (!relations.TryGetValue(tuple.Relation, out var subjects)) {
                subjects = [];
                relations[tuple.Relation] = subjects;
            }

            if (!subjects.Contains(tuple.Subject)) {
                subjects.Add(tuple.Subject);
            }

            var expiryKey = TupleExpiry.Key(tuple.Relation, tuple.Subject);
            if (tuple.ExpiresOn is { } expiresOn) {
                expiries[key][expiryKey] = expiresOn;
            } else {
                expiries[key].Remove(expiryKey);
            }
        }
    }

    /// <summary>Builds a reader from the tuple grammar.</summary>
    /// <param name="tuples">Tuples as <c>object#relation@subject</c>.</param>
    public static InMemoryRelationReader Parse(params string[] tuples) =>
        new(tuples.Select(static x => RelationTuple.Parse(x).GetValueOrThrow()));

    /// <inheritdoc />
    public ValueTask<Result<ObjectRelationsSnapshot>> ReadAsync(
        ObjectRef target,
        CancellationToken cancellationToken
    ) {
        Reads++;

        var key = target.ToString();
        var relations = byObject.TryGetValue(key, out var found)
            ? found
            : [];

        var expiring = expiries.TryGetValue(key, out var stored) ? stored : [];

        Dictionary<string, IReadOnlyList<SubjectRef>> snapshot = new(StringComparer.Ordinal);
        Dictionary<string, DateTimeOffset> live = new(StringComparer.Ordinal);
        var count = 0;

        foreach (var (relation, subjects) in relations) {
            List<SubjectRef> kept = [];

            foreach (var subject in subjects) {
                var expiryKey = TupleExpiry.Key(relation, subject);
                DateTimeOffset? expiresOn = expiring.TryGetValue(expiryKey, out var expiry) ? expiry : null;

                if (!TupleExpiry.IsLive(expiresOn, Now)) {
                    continue;
                }

                kept.Add(subject);
                if (expiresOn is { } value) {
                    live[expiryKey] = value;
                }
            }

            if (kept.Count > 0) {
                snapshot[relation] = kept;
                count += kept.Count;
            }
        }

        return ValueTask.FromResult(
            Result<ObjectRelationsSnapshot>.Success(
                new() { Object = target, ByRelation = snapshot, Count = count, Expiries = live }
            )
        );
    }
}
