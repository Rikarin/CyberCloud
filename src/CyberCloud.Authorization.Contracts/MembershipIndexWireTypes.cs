namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     One subject object's slice of the Leopard index, as <c>IMembershipIndexGrain</c> returns it —
///     docs/plan/07 § The Leopard index.
/// </summary>
/// <remarks>
///     <para>
///         Two closures over the same graph, read from opposite ends. The graph's edges are the
///         tuples written against a <i>direct-only</i> relation — one whose rewrite is <c>This</c>
///         and nothing else, <c>group#member</c> on the shipping schema — and a chain of them is
///         followed only through usersets whose relation is direct-only too. <see cref="Members" />
///         is that closure read <b>down</b> from a userset formed on this object: every subject a
///         chain reaches. <see cref="Usersets" /> is the same closure read <b>up</b> from this
///         object as a subject: every userset a chain reaches it from.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A userset whose relation is not direct-only appears in <see cref="Members" /> and is
///             never expanded there.
///         </b> A tuple <c>group:g#member@resourceGroup:r#owner</c> is legal,
///         and what <c>resourceGroup:r#owner</c> contains is a <c>From("parent", …)</c> away from
///         anything a closure over tuples can say. The index records the userset as a member and
///         stops; a reader that finds one knows the set is not the whole membership and says
///         "walk it" rather than "no". <c>MembershipIndexReader</c> is where that reading lives.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.MembershipIndexSnapshot")]
public sealed record MembershipIndexSnapshot {
    /// <summary>The subject object this slice is about.</summary>
    [Id(0)]
    public ObjectRef Object { get; init; } = new();

    /// <summary>
    ///     The schema version the closures were computed under, or <c>0</c> when nothing has been
    ///     written yet. A closure depends on the schema only through which relations are direct-only,
    ///     so a slice stamped with another version is one the reader must not trust — and an
    ///     unwritten one is not an empty closure either, because the tuples it should close over may
    ///     be older than the index. Both are rebuilt from the tuples before anything derives from
    ///     them; see <c>MembershipIndexGrain</c>.
    /// </summary>
    [Id(1)]
    public int SchemaVersion { get; init; }

    /// <summary>
    ///     Relation → the transitively closed members of the userset <c>{Object}#{relation}</c>.
    ///     Only direct-only relations ever have an entry.
    /// </summary>
    [Id(2)]
    public IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>> Members { get; init; } =
        new Dictionary<string, IReadOnlyList<SubjectRef>>(StringComparer.Ordinal);

    /// <summary>
    ///     Subject relation → every userset the subject <c>{Object}</c> (empty key) or
    ///     <c>{Object}#{relation}</c> is transitively in. Each entry is a userset subject.
    /// </summary>
    [Id(3)]
    public IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>> Usersets { get; init; } =
        new Dictionary<string, IReadOnlyList<SubjectRef>>(StringComparer.Ordinal);

    /// <summary>
    ///     The relations formed on this object whose closure leaves out an expiring edge — one on
    ///     the userset itself or on any userset the closure reaches. A reader never answers "no"
    ///     from such a closure; it answers "walk it". docs/plan/07 § Time-bounded relations.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An expiring tuple is never an edge of the closure, and this is what that costs.</b>
    ///     A closure holds no clock: a member it records is a member until a write removes it. So
    ///     an edge that stops granting on its own at an instant is left out of
    ///     <see cref="Members" /> and <see cref="Usersets" />, and every userset above it is marked
    ///     here instead. A member it would have carried is then found by the walk, which filters
    ///     expired tuples at read time, and a "no" is never taken from a closure that couldn't have
    ///     said "yes". The alternative — each member stamped with the widest-path expiry over every
    ///     route to it — is recorded as owed in the same section.
    /// </remarks>
    [Id(4)]
    public IReadOnlyList<string> Unclosed { get; init; } = [];

    /// <summary>The closed members of <c>{Object}#{relation}</c>, or an empty list.</summary>
    /// <param name="relation">The userset relation formed on this object.</param>
    public IReadOnlyList<SubjectRef> MembersOf(string relation) =>
        Members.TryGetValue(relation, out var members) ? members : [];

    /// <summary>The closed usersets of this object as a subject, or an empty list.</summary>
    /// <param name="subjectRelation">The subject's own userset relation, or empty for the concrete object.</param>
    public IReadOnlyList<SubjectRef> UsersetsOf(string subjectRelation) =>
        Usersets.TryGetValue(subjectRelation, out var usersets) ? usersets : [];

    /// <summary>Whether the closure of <c>{Object}#{relation}</c> leaves out an expiring edge.</summary>
    /// <param name="relation">The userset relation formed on this object.</param>
    public bool IsUnclosed(string relation) => Unclosed.Contains(relation, StringComparer.Ordinal);
}

/// <summary>
///     One grain's share of an index update — what the tuple store hands
///     <c>IMembershipIndexGrain.ApplyAsync</c> after working out which slices a write or a delete
///     touches.
/// </summary>
/// <remarks>
///     <para>
///         A write is two unions and a delete is a replacement and a subtraction, and the four
///         fields are exactly those. Every one is idempotent — a union applied twice is the union,
///         a replacement applied twice is the replacement — because the store's journal replays a
///         change that did not finish (<c>ITupleStoreGrain.SweepAsync</c>), and a replay that
///         could double-count would turn the sweeper into a source of damage.
///     </para>
///     <para>
///         Dictionaries rather than lists of pairs because one grain holds several relations and a
///         write to a group that is in two usersets of one object touches both in one durable write.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.MembershipIndexChange")]
public sealed record MembershipIndexChange {
    /// <summary>The schema version the change was computed under. Stamped on the slice.</summary>
    [Id(0)]
    public int SchemaVersion { get; init; }

    /// <summary>Relation → members to add to that userset's closure.</summary>
    [Id(1)]
    public IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>> AddMembers { get; init; } =
        new Dictionary<string, IReadOnlyList<SubjectRef>>(StringComparer.Ordinal);

    /// <summary>
    ///     Relation → the recomputed closure that replaces the stored one — a delete's answer,
    ///     docs/plan/07 § The Leopard index's "recompute-on-delete".
    /// </summary>
    [Id(2)]
    public IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>> ReplaceMembers { get; init; } =
        new Dictionary<string, IReadOnlyList<SubjectRef>>(StringComparer.Ordinal);

    /// <summary>Subject relation → usersets the subject is now in.</summary>
    [Id(3)]
    public IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>> AddUsersets { get; init; } =
        new Dictionary<string, IReadOnlyList<SubjectRef>>(StringComparer.Ordinal);

    /// <summary>Subject relation → usersets the subject is no longer in.</summary>
    [Id(4)]
    public IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>> RemoveUsersets { get; init; } =
        new Dictionary<string, IReadOnlyList<SubjectRef>>(StringComparer.Ordinal);

    /// <summary>
    ///     Whether both closures are cleared before the rest is applied — a rebuild's whole-slice
    ///     replacement, which has to drop relations the tuples no longer form as well as fill the
    ///     ones they do.
    /// </summary>
    [Id(5)]
    public bool Reset { get; init; }

    /// <summary>
    ///     Relations whose closure leaves out an expiring edge — see
    ///     <see cref="MembershipIndexSnapshot.Unclosed" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Two readings, told apart by <see cref="ReplaceMembers" />.</b> For a relation this
    ///     change replaces, the mark is replaced too: set if listed here, cleared if not — a
    ///     recomputation knows the whole closure, so it knows whether any expiring edge is left in
    ///     it. For every other relation a listed one is marked and nothing is cleared, which is the
    ///     union a write is. Both readings are idempotent, so the sweeper's replay stays harmless.
    /// </remarks>
    [Id(6)]
    public IReadOnlyList<string> Unclosed { get; init; } = [];

    /// <summary>Whether the change would touch nothing.</summary>
    public bool IsEmpty =>
        !Reset
        && Unclosed.Count == 0
        && AddMembers.Count == 0
        && ReplaceMembers.Count == 0
        && AddUsersets.Count == 0
        && RemoveUsersets.Count == 0;
}
