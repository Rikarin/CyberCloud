using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>
///     The same tuple set as <see cref="InMemoryRelationReader" />, indexed the other way — what
///     <c>ISubjectRelationsGrain</c> would hold for each subject object — and closed the way the
///     Leopard index closes it.
/// </summary>
/// <remarks>
///     <para>
///         Built from the tuples exactly as <c>TupleStoreGrain</c>'s step 5 builds a
///         <see cref="SubjectIndexEntry" />: the subject's object half is the key, and its userset
///         relation is the entry's <see cref="SubjectIndexEntry.SubjectRelation" />. ⚠ The evaluator
///         under test is the evaluator that ships; only the two tuple sources differ.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The index is built by the maintainer that ships, one tuple at a time, in the order
///             the store would see them.
///         </b> Every tuple is applied through
///         <see cref="MembershipIndexMaintainer.ApplyWriteAsync" /> over an
///         <see cref="InMemoryMembershipIndexStore" />, so the closure the walk reads here is the
///         closure the write path would have produced, not a brute force standing in for it. The
///         property tests therefore hold the write path to the reference evaluator on every graph
///         they generate, as well as the walk. Both readers hold the whole tuple set while the
///         index is built, so the first write to touch a slice rebuilds it over every tuple, as
///         the store's remarks say it must, and the writes after it are the unions;
///         <c>MembershipIndexPropertyTests</c> is where the unions are held to the brute force
///         one tuple at a time.
///     </para>
/// </remarks>
public sealed class InMemoryReverseRelationReader : IReverseRelationReader {
    readonly Dictionary<ObjectRef, List<SubjectIndexEntry>> bySubject = [];

    /// <summary>How many reverse reads the walk has made.</summary>
    public int Reads { get; private set; }

    /// <summary>How many closure reads the walk has made.</summary>
    public int IndexReads { get; private set; }

    /// <summary>The index's slices, as the maintainer left them.</summary>
    public InMemoryMembershipIndexStore Store { get; } = new();

    /// <summary>The index as the evaluators read it. Fresh per request in production; here it is one per reader.</summary>
    public MembershipIndexReader Index { get; }

    /// <summary>
    ///     The instant reads are made at, as <see cref="InMemoryRelationReader.Now" />: an entry whose
    ///     tuple has expired by then is left out, the way <c>ISubjectRelationsGrain.ListAsync</c>
    ///     leaves it out. The index is built as the store would have built it — every tuple live
    ///     when it was written — so an expiring edge is marked unclosed rather than closed over.
    /// </summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Builds a reader over a tuple set, closed under <paramref name="schema" />.</summary>
    /// <param name="schema">The schema — it decides which relations the index follows.</param>
    /// <param name="tuples">The tuples.</param>
    public InMemoryReverseRelationReader(AuthorizationSchema schema, IEnumerable<RelationTuple> tuples) {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(tuples);

        var all = tuples.ToList();

        foreach (var tuple in all) {
            if (!bySubject.TryGetValue(tuple.Subject.Object, out var entries)) {
                entries = [];
                bySubject[tuple.Subject.Object] = entries;
            }

            SubjectIndexEntry entry = new() {
                Object = tuple.Object,
                Relation = tuple.Relation,
                SubjectRelation = tuple.Subject.Relation,
                ExpiresOn = tuple.ExpiresOn
            };

            // Matched as the grain matches: a later write of the same tuple replaces its expiry.
            entries.RemoveAll(x => x.IsSameEntryAs(entry));
            entries.Add(entry);
        }

        var maintainer = new MembershipIndexMaintainer(schema, new InMemoryRelationReader(all), this, Store);
        Store.Rebuild = maintainer.RebuildAsync;

        foreach (var tuple in all) {
            var applied = maintainer.ApplyWriteAsync(tuple, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (applied.IsFailure) {
                throw new InvalidOperationException($"Indexing '{tuple}' failed: {applied.Error!.Message}");
            }
        }

        Index = new(schema, Store);

        // Building the index read this reader's entries — the rebuilds of first-touched slices do.
        // The counter is the walk's, and the walk has not started.
        Reads = 0;
    }

    /// <summary>Builds a reader from the tuple grammar, closed under <see cref="CyberCloudSchema" />.</summary>
    /// <param name="tuples">Tuples as <c>object#relation@subject</c>.</param>
    public static InMemoryReverseRelationReader Parse(params string[] tuples) =>
        Parse(CyberCloudSchema.Instance, tuples);

    /// <summary>Builds a reader from the tuple grammar, closed under <paramref name="schema" />.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="tuples">Tuples as <c>object#relation@subject</c>.</param>
    public static InMemoryReverseRelationReader Parse(AuthorizationSchema schema, params string[] tuples) =>
        new(schema, tuples.Select(static x => RelationTuple.Parse(x).GetValueOrThrow()));

    /// <summary>
    ///     Drops a tuple's reverse entry — <c>TupleStoreGrain</c>'s step 5 of a delete, so a test
    ///     that ran the index step of a delete through <see cref="Store" /> can list through the
    ///     store it recomputed rather than through a fresh one.
    /// </summary>
    /// <param name="tuple">The tuple being deleted.</param>
    public void Remove(RelationTuple tuple) {
        ArgumentNullException.ThrowIfNull(tuple);

        if (bySubject.TryGetValue(tuple.Subject.Object, out var entries)) {
            SubjectIndexEntry entry = new() {
                Object = tuple.Object, Relation = tuple.Relation, SubjectRelation = tuple.Subject.Relation
            };

            entries.RemoveAll(x => x.IsSameEntryAs(entry));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) {
        Reads++;

        IReadOnlyList<SubjectIndexEntry> entries = bySubject.TryGetValue(subjectObject, out var found)
            ? [.. found.Where(x => TupleExpiry.IsLive(x.ExpiresOn, Now))]
            : [];

        return ValueTask.FromResult(Result<IReadOnlyList<SubjectIndexEntry>>.Success(entries));
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<SubjectRef>>> ReadUsersetsAsync(
        SubjectRef subject,
        CancellationToken cancellationToken
    ) {
        IndexReads++;
        return Index.UsersetsOfAsync(subject, cancellationToken);
    }
}
