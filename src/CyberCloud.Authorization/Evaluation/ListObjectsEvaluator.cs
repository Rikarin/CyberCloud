using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using System.Globalization;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>What one <c>ListObjects</c> walk concluded, before it is paged.</summary>
public sealed record ListObjectsEvaluation {
    /// <summary>
    ///     Every object of the requested type the subject has the requested name on, ordered by id.
    ///     Empty unless <see cref="Outcome" /> is <see cref="ListObjectsOutcome.Complete" />.
    /// </summary>
    public IReadOnlyList<ObjectRef> Objects { get; init; } = [];

    /// <summary>Complete, or the cap that stopped the walk.</summary>
    public ListObjectsOutcome Outcome { get; init; } = ListObjectsOutcome.Unknown;

    /// <summary>Where the cap was hit, or empty.</summary>
    public string CapDetail { get; init; } = string.Empty;

    /// <summary>How many <c>(object, name)</c> pairs were reached.</summary>
    public int PairsReached { get; init; }

    /// <summary>How many reverse-index reads were made.</summary>
    public int ReverseReads { get; init; }

    /// <summary>How many forward reads were made.</summary>
    public int ForwardReads { get; init; }

    /// <summary>Whether the candidates were re-checked forward.</summary>
    public bool Verified { get; init; }

    /// <summary>
    ///     Whether some pair lay past the depth cap and was left unexpanded. The objects returned
    ///     are still exactly the ones <c>Check</c> allows; the ones past the cap are the ones it
    ///     denies as <c>CheckOutcome.DepthCapExceeded</c>.
    /// </summary>
    public bool DepthCapHit { get; init; }
}

/// <summary>
///     The reverse walk of docs/plan/07 § ListObjects: from a subject, backwards through the rewrite
///     tree, to every object it may act on.
/// </summary>
/// <remarks>
///     <para>
///         One instance per request, like <see cref="CheckEvaluator" />: it holds the reached set
///         and both read caches, so it is not reusable and not thread-safe.
///     </para>
///     <para>
///         <b>The walk, as a set of rules over reached <c>(object, name)</c> pairs.</b> A pair
///         means "the subject has <c>name</c> on <c>object</c>". Seed: every tuple naming the
///         subject, <c>o#r@S</c>, reaches <c>(o, r)</c> when <c>r</c>'s rewrite has a <c>This</c>
///         in it. Then, for each reached <c>(o, r)</c>:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Computed userset.</b> Every name <c>y</c> on <c>o</c>'s type whose rewrite has
///             <c>Rel(r)</c> reaches <c>(o, y)</c>. No hop.
///         </item>
///         <item>
///             <b>Userset subject.</b> Every tuple <c>o2#r2@o#r</c> reaches <c>(o2, r2)</c> when
///             <c>r2</c> has a <c>This</c> — the subject is in the userset, so it holds whatever
///             the userset was granted. One hop.
///         </item>
///         <item>
///             <b>Tupleset.</b> Every tuple <c>c#ts@o</c> reaches <c>(c, y)</c> for every <c>y</c>
///             on <c>c</c>'s type whose rewrite has <c>From(ts, r)</c> — the parent's grant
///             inherited by the child. One hop.
///         </item>
///     </list>
///     <para>
///         Each rule is the reverse of one arm of <see cref="CheckEvaluator" />'s forward
///         evaluation, and the rewrite nodes are read in the same places: a <c>This</c> counts only
///         where a direct tuple counts, a userset target is read as its object half where the
///         forward tupleset does, and a userset subject is followed by its <b>own</b> rewrite, which
///         is how <c>group:eng#member</c> nested in <c>group:platform#member</c> is one more hop.
///         The answer is the pairs whose object has the requested type and whose name is the
///         requested one.
///     </para>
///     <para>
///         ⚠ <b>Exact for union rewrites, an over-approximation for the other two, and the
///         difference is what "<c>Check</c>-verified" costs.</b> A union is true when any arm is,
///         so reaching a pair through one arm is enough. An intersection is not — reaching
///         <c>(o, y)</c> through <c>A</c> says nothing about <c>B</c> — and an exclusion
///         <c>A &amp; !B</c> is only ever entered through <c>A</c> (the nodes under a <c>!</c> never
///         trigger a reach, because a tuple on <c>B</c> removes access rather than granting it). So
///         the walk records whether any rewrite it crossed was not a union, and if one was, every
///         candidate is re-run through <see cref="CheckEvaluator" /> before being returned. On
///         <c>CyberCloudSchema</c> that is <c>assignRole</c> and <c>purge</c>, and nothing on the
///         <c>read</c> path — <c>ListObjectsPropertyTests</c> is what shows the two evaluators agree
///         on generated schemas with all three node kinds.
///     </para>
///     <para>
///         ⚠ <b>What the walk cannot reproduce is the breadth cap.</b> <c>Check</c> gives up on a
///         node with more than <c>MaxBreadth</c> userset subjects and denies; this walk has no such
///         node, so a subject granted through the 1 001st userset on one object is listed here and
///         refused there. That is the permissive direction of a walk that <i>was</i> allowed, and
///         it is recorded rather than closed: closing it means re-checking every candidate on every
///         call, which is the per-member <c>Check</c> this evaluator exists to replace.
///     </para>
///     <para>
///         ⚠ <b>Scoping is a bound on the walk, not only a filter on the answer.</b> With
///         <see cref="ListObjectsRequest.Within" /> set, the tupleset rule descends from an ancestor
///         of the scope only into the next object on the chain toward it, and below the scope only
///         to <see cref="ListObjectsRequest.WithinDepth" />; every other object's descent is skipped.
///         An object reached some other way — a direct grant — is placed by walking its own
///         tupleset chain upward until the scope is met or the chain ends, one forward read per
///         object, memoized. See the remarks on <see cref="ListObjectsRequest" /> for the one
///         assumption this makes and the direction it fails in.
///     </para>
/// </remarks>
public sealed class ListObjectsEvaluator {
    readonly AuthorizationSchema schema;
    readonly CountingForwardReader forward;
    readonly CountingReverseReader reverse;
    readonly AuthorizationLimits limits;

    // (object, name) → hops from the subject. Filled in BFS order, so the recorded depth is the
    // shortest derivation, which is the one Check would find inside its cap.
    readonly Dictionary<Pair, int> reached = [];
    readonly HashSet<ObjectRef> objectsReached = [];
    readonly Queue<(ObjectRef Object, string Name, int Depth)> queue = new();

    readonly Dictionary<ObjectRef, IReadOnlyList<SubjectIndexEntry>> reverseEntries = [];
    readonly Dictionary<ObjectRef, ObjectRelationsSnapshot> forwardSnapshots = [];

    // Scope bookkeeping — see ScopeOf.
    readonly Dictionary<ObjectRef, int> above = [];
    readonly Dictionary<ObjectRef, int?> below = [];
    readonly HashSet<ObjectRef> placing = [];

    ObjectRef? within;
    int? withinDepth;

    bool approximate;
    bool depthCapHit;
    Error? readFailure;
    string capDetail = string.Empty;
    ListObjectsOutcome cap = ListObjectsOutcome.Complete;

    /// <summary>Creates an evaluator for one request.</summary>
    /// <param name="schema">The schema. Already validated — see <see cref="AuthorizationSchema" />.</param>
    /// <param name="forwardReader">Where forward tuples come from, for scoping and verification.</param>
    /// <param name="reverseReader">Where the reverse index comes from — the walk itself.</param>
    /// <param name="limits">The caps. <c>null</c> means <see cref="AuthorizationLimits.Default" />.</param>
    public ListObjectsEvaluator(
        AuthorizationSchema schema,
        IRelationReader forwardReader,
        IReverseRelationReader reverseReader,
        AuthorizationLimits? limits = null
    ) {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(forwardReader);
        ArgumentNullException.ThrowIfNull(reverseReader);

        this.schema = schema;
        forward = new(forwardReader);
        reverse = new(reverseReader);
        this.limits = limits ?? AuthorizationLimits.Default;
    }

    /// <summary>Runs one walk.</summary>
    /// <param name="subject">Who is listing.</param>
    /// <param name="request">What to list, and the bounds. Paging fields are ignored here.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    ///     A <c>Result</c> failure means the question was not answerable —
    ///     <see cref="ErrorCode.SchemaInvalid" /> for a type or name the schema does not define,
    ///     <see cref="ErrorCode.InvalidRequestBody" /> for a malformed scope, or a storage error
    ///     from either reader. A capped walk is a <i>successful</i> evaluation with no objects.
    /// </remarks>
    public async Task<Result<ListObjectsEvaluation>> EvaluateAsync(
        SubjectRef subject,
        ListObjectsRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);

        var validated = Validate(subject, request);
        if (validated.TryGetError(out var invalid)) {
            return Result<ListObjectsEvaluation>.Failure(invalid);
        }

        within = request.Within;
        withinDepth = request.Within is null ? null : request.WithinDepth;

        AuthorizationMetrics.RecordListObjects();

        if (within is not null) {
            await PlaceAncestorsAsync(within, cancellationToken).ConfigureAwait(false);
        }

        // ── Seed: every tuple naming the subject ───────────────────────────────────────────────
        var seeds = await ReverseAsync(subject.Object, cancellationToken).ConfigureAwait(false);
        if (seeds is not null) {
            foreach (var entry in seeds) {
                if (string.Equals(entry.SubjectRelation, subject.Relation, StringComparison.Ordinal)
                    && HasThis(entry.Object.Type, entry.Relation)) {
                    Reach(entry.Object, entry.Relation, 0);
                }
            }
        }

        // ── The walk ───────────────────────────────────────────────────────────────────────────
        while (queue.Count > 0 && readFailure is null && cap == ListObjectsOutcome.Complete) {
            var (target, name, depth) = queue.Dequeue();
            await ExpandAsync(target, name, depth, cancellationToken).ConfigureAwait(false);
        }

        if (readFailure is not null) {
            return Result<ListObjectsEvaluation>.Failure(readFailure);
        }

        if (cap != ListObjectsOutcome.Complete) {
            AuthorizationMetrics.RecordListObjectsCap();

            return Result<ListObjectsEvaluation>.Success(
                new() {
                    Outcome = cap,
                    CapDetail = capDetail,
                    PairsReached = reached.Count,
                    ReverseReads = reverse.Reads,
                    ForwardReads = forward.Reads,
                    DepthCapHit = depthCapHit
                }
            );
        }

        // ── The answer: the requested type and name, inside the scope ─────────────────────────
        List<ObjectRef> candidates = [];

        foreach (var (pair, _) in reached) {
            if (!string.Equals(pair.Object.Type, request.ObjectType, StringComparison.Ordinal)
                || !string.Equals(pair.Name, request.Permission, StringComparison.Ordinal)) {
                continue;
            }

            if (within is not null) {
                var placement = await PlaceAsync(pair.Object, cancellationToken).ConfigureAwait(false);
                if (readFailure is not null) {
                    return Result<ListObjectsEvaluation>.Failure(readFailure);
                }

                if (!placement.IsInScope(withinDepth)) {
                    continue;
                }
            }

            candidates.Add(pair.Object);
        }

        // ── Verification, when the walk over-approximated ─────────────────────────────────────
        if (approximate && candidates.Count > 0) {
            var checker = new CheckEvaluator(schema, forward, limits);
            List<ObjectRef> allowed = new(candidates.Count);

            foreach (var candidate in candidates) {
                var checkedResult = await checker.EvaluateAsync(candidate, request.Permission, subject, cancellationToken)
                    .ConfigureAwait(false);

                if (checkedResult.TryGetError(out var checkError)) {
                    return Result<ListObjectsEvaluation>.Failure(checkError);
                }

                if (checkedResult.GetValueOrThrow().Allowed) {
                    allowed.Add(candidate);
                }
            }

            candidates = allowed;
        }

        candidates.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));

        return Result<ListObjectsEvaluation>.Success(
            new() {
                Objects = candidates,
                Outcome = ListObjectsOutcome.Complete,
                PairsReached = reached.Count,
                ReverseReads = reverse.Reads,
                ForwardReads = forward.Reads,
                Verified = approximate,
                DepthCapHit = depthCapHit
            }
        );
    }

    Result Validate(SubjectRef subject, ListObjectsRequest request) {
        if (!subject.IsValid) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{subject}' is not a well-formed subject. It is 'type:id' or "
                + "'type:id#relation' — docs/plan/07 § The model."
            );
        }

        var type = schema.Type(request.ObjectType);
        if (type is null) {
            return Result.Failure(
                ErrorCode.SchemaInvalid,
                $"'{request.ObjectType}' is not an object type in schema version "
                + schema.Version.ToString(CultureInfo.InvariantCulture)
                + ". It defines ["
                + string.Join(", ", schema.TypeNames)
                + "]."
            );
        }

        if (type.Member(request.Permission) is null) {
            return Result.Failure(
                ErrorCode.SchemaInvalid,
                $"'{request.ObjectType}' defines no '{request.Permission}'. Its permissions are ["
                + string.Join(", ", type.Permissions)
                + "] and its relations are ["
                + string.Join(", ", type.Relations)
                + "]. docs/plan/07 § The model — a typo'd permission name must be neither a silent "
                + "allow-nothing nor a silent allow-everything, so it is this failure instead."
            );
        }

        if (request.Within is { } scope) {
            if (!scope.IsValid) {
                return Result.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{scope}' is not a well-formed object reference to list within."
                );
            }

            if (!schema.HasType(scope.Type)) {
                return Result.Failure(
                    ErrorCode.SchemaInvalid,
                    $"'{scope.Type}' is not an object type in schema version "
                    + schema.Version.ToString(CultureInfo.InvariantCulture)
                    + ", so nothing can be listed within '{scope}'."
                );
            }

            if (request.WithinDepth is < 0) {
                return Result.Failure(
                    ErrorCode.InvalidRequestBody,
                    "WithinDepth is how many tupleset hops below Within to include and cannot be "
                    + "negative. Zero is Within itself; null is every descendant."
                );
            }
        }

        return Result.Success;
    }

    // ── Reaching ───────────────────────────────────────────────────────────────────────────────

    void Reach(ObjectRef target, string name, int depth) {
        if (cap != ListObjectsOutcome.Complete) {
            return;
        }

        if (depth > limits.MaxDepth) {
            // Check would cut this derivation — see AuthorizationLimits: "twelve hops away from the
            // object being checked". Not reaching it keeps the two evaluators agreeing on Allowed.
            depthCapHit = true;
            return;
        }

        var pair = new Pair(target, name);
        if (reached.ContainsKey(pair)) {
            return;
        }

        reached[pair] = depth;

        if (objectsReached.Add(target) && objectsReached.Count > limits.MaxListObjects) {
            cap = ListObjectsOutcome.ObjectCapExceeded;
            capDetail =
                "the walk reached more than "
                + limits.MaxListObjects.ToString(CultureInfo.InvariantCulture)
                + " objects; the last was "
                + target;

            return;
        }

        queue.Enqueue((target, name, depth));
    }

    async ValueTask ExpandAsync(ObjectRef target, string name, int depth, CancellationToken cancellationToken) {
        var type = schema.Type(target.Type);
        if (type is null) {
            return;
        }

        // Rule 1 — computed userset: every y on this type whose rewrite has Rel(name).
        foreach (var member in type.Members) {
            if (Triggers(member, node => node is RelationRefExpression reference
                    && string.Equals(reference.Relation, name, StringComparison.Ordinal)
                )) {
                Reach(target, member.Name, depth);
            }
        }

        Placement placement = default;

        if (within is not null) {
            placement = await PlaceAsync(target, cancellationToken).ConfigureAwait(false);
            if (readFailure is not null) {
                return;
            }

            if (placement.Below is { } below && withinDepth is { } max && below >= max) {
                // ⚠ AT THE REQUESTED DEPTH, AND THE REVERSE INDEX IS NOT READ AT ALL. Nothing below
                // this object is wanted, so its children are not worth a read — and that is the
                // read a group listing would otherwise pay once per member, which is the cost
                // ListObjects exists to remove. The same skip leaves a userset formed ON this
                // object unfollowed; see ListObjectsRequest's remarks for why that is the second
                // scoping assumption and which direction it fails in.
                return;
            }
        }

        var entries = await ReverseAsync(target, cancellationToken).ConfigureAwait(false);
        if (entries is null) {
            return;
        }

        // Rule 2 — userset subject: every tuple o2#r2@target#name.
        foreach (var entry in entries) {
            if (string.Equals(entry.SubjectRelation, name, StringComparison.Ordinal)
                && HasThis(entry.Object.Type, entry.Relation)) {
                Reach(entry.Object, entry.Relation, depth + 1);
            }
        }

        // Rule 3 — tupleset: every tuple c#ts@target (any subject relation — the forward tupleset
        // reads a userset target as its object half), for every y on c's type with From(ts, name).
        foreach (var entry in entries) {
            var childType = schema.Type(entry.Object.Type);
            if (childType is null) {
                continue;
            }

            var childDepth = depth + 1;
            var descentDecided = false;

            foreach (var member in childType.Members) {
                if (!Triggers(member, node => node is TuplesetExpression tupleset
                        && string.Equals(tupleset.Tupleset, entry.Relation, StringComparison.Ordinal)
                        && string.Equals(tupleset.Computed, name, StringComparison.Ordinal)
                    )) {
                    continue;
                }

                if (!descentDecided) {
                    descentDecided = true;

                    if (within is not null && !MayDescend(placement, entry.Object)) {
                        break;
                    }
                }

                Reach(entry.Object, member.Name, childDepth);
            }
        }
    }

    /// <summary>
    ///     Whether <paramref name="member" />'s rewrite would be reached by a node
    ///     <paramref name="trigger" /> accepts becoming true — and records when that reach is only an
    ///     approximation.
    /// </summary>
    bool Triggers(SchemaMember member, Func<RelationExpression, bool> trigger) {
        var hit = false;
        Visit(member.Expression, ref hit, trigger);

        if (hit && !IsUnionOnly(member.Expression)) {
            approximate = true;
        }

        return hit;
    }

    static void Visit(RelationExpression node, ref bool hit, Func<RelationExpression, bool> trigger) {
        switch (node) {
            case ExclusionExpression:
                // A tuple under a `!` takes access away. It never reaches anything.
                return;

            case UnionExpression or IntersectionExpression:
                foreach (var child in node.Children) {
                    Visit(child, ref hit, trigger);
                    if (hit) {
                        return;
                    }
                }

                return;

            default:
                if (trigger(node)) {
                    hit = true;
                }

                return;
        }
    }

    static bool IsUnionOnly(RelationExpression expression) =>
        expression.DescendantsAndSelf().All(x => x is not (IntersectionExpression or ExclusionExpression));

    bool HasThis(string type, string relation) =>
        schema.Member(type, relation) is { } member
        && Triggers(member, static node => node is ThisExpression);

    // ── Scoping ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Where an object stands relative to <c>Within</c>: on the chain above it (distance up),
    ///     at or below it (distance down), neither, or both — a chain that cycles through the scope
    ///     puts one object on both sides, which is why the two are resolved separately.
    /// </summary>
    readonly record struct Placement(int? Above, int? Below) {
        public bool IsInScope(int? maxBelow) => Below is { } distance && (maxBelow is null || distance <= maxBelow);
    }

    /// <summary>
    ///     Walks up from the scope along every tupleset relation and records each ancestor's
    ///     distance, so that descending from an ancestor can be steered toward the scope.
    /// </summary>
    async ValueTask PlaceAncestorsAsync(ObjectRef scope, CancellationToken cancellationToken) {
        above[scope] = 0;
        below[scope] = 0;

        Queue<(ObjectRef Object, int Distance)> pending = new();
        pending.Enqueue((scope, 0));

        while (pending.Count > 0) {
            var (current, distance) = pending.Dequeue();
            if (distance >= limits.MaxDepth) {
                continue;
            }

            var parents = await ParentsAsync(current, cancellationToken).ConfigureAwait(false);
            if (parents is null) {
                return;
            }

            foreach (var parent in parents) {
                if (above.ContainsKey(parent)) {
                    continue;
                }

                above[parent] = distance + 1;
                pending.Enqueue((parent, distance + 1));
            }
        }
    }

    /// <summary>
    ///     Places an object: its distance above the scope is already known or absent, and its
    ///     distance below is resolved by walking its own chain upward until the scope is met.
    /// </summary>
    async ValueTask<Placement> PlaceAsync(ObjectRef target, CancellationToken cancellationToken) {
        var distanceBelow = await BelowAsync(target, cancellationToken).ConfigureAwait(false);

        return new(above.TryGetValue(target, out var distanceAbove) ? distanceAbove : null, distanceBelow);
    }

    async ValueTask<int?> BelowAsync(ObjectRef target, CancellationToken cancellationToken) {
        if (below.TryGetValue(target, out var known)) {
            return known;
        }

        if (!placing.Add(target) || placing.Count > limits.MaxDepth) {
            // A cycle in the chain that does not pass through the scope, or a chain longer than
            // the cap. Neither leads to the scope, and the answer is not memoized because the
            // object may still be placed through the path that is in progress.
            return null;
        }

        try {
            var parents = await ParentsAsync(target, cancellationToken).ConfigureAwait(false);
            if (parents is null) {
                return null;
            }

            int? distance = null;

            foreach (var parent in parents) {
                var parentBelow = await BelowAsync(parent, cancellationToken).ConfigureAwait(false);
                if (parentBelow is { } value && (distance is null || value + 1 < distance)) {
                    distance = value + 1;
                }
            }

            below[target] = distance;
            return distance;
        } finally {
            placing.Remove(target);
        }
    }

    /// <summary>
    ///     Whether the tupleset rule may descend from <paramref name="parent" /> (already placed)
    ///     into <paramref name="child" />, and places the child when it may.
    /// </summary>
    bool MayDescend(Placement parent, ObjectRef child) {
        if (parent.Below is { } distance) {
            // At or under the scope: descend to the requested depth and no further.
            if (withinDepth is { } max && distance + 1 > max) {
                return false;
            }

            if (!below.TryGetValue(child, out var existing) || existing is null || existing > distance + 1) {
                below[child] = distance + 1;
            }

            return true;
        }

        if (parent.Above is not null) {
            // Above the scope: only the next object on the chain toward it. The chain was placed
            // up front, so a child that is not on it is off it.
            return above.ContainsKey(child);
        }

        // Neither above nor below: an object whose subtree cannot meet the scope. This is the
        // chain assumption ListObjectsRequest's remarks record.
        return false;
    }

    /// <summary>The objects this one points at through any tupleset relation the schema names.</summary>
    async ValueTask<List<ObjectRef>?> ParentsAsync(ObjectRef target, CancellationToken cancellationToken) {
        var type = schema.Type(target.Type);
        if (type is null) {
            return [];
        }

        var snapshot = await ForwardAsync(target, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) {
            return null;
        }

        List<ObjectRef> parents = [];

        foreach (var tupleset in TuplesetRelations(type)) {
            foreach (var subject in snapshot.Subjects(tupleset)) {
                if (!parents.Contains(subject.Object)) {
                    parents.Add(subject.Object);
                }
            }
        }

        return parents;
    }

    static IEnumerable<string> TuplesetRelations(SchemaType type) =>
        type.Members
            .SelectMany(member => member.Expression.DescendantsAndSelf())
            .OfType<TuplesetExpression>()
            .Select(tupleset => tupleset.Tupleset)
            .Distinct(StringComparer.Ordinal);

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────

    async ValueTask<IReadOnlyList<SubjectIndexEntry>?> ReverseAsync(ObjectRef target, CancellationToken cancellationToken) {
        if (reverseEntries.TryGetValue(target, out var cached)) {
            return cached;
        }

        var read = await reverse.ReadAsync(target, cancellationToken).ConfigureAwait(false);
        if (read.TryGetError(out var error)) {
            readFailure = error;
            return null;
        }

        var entries = read.GetValueOrThrow();
        reverseEntries[target] = entries;
        return entries;
    }

    async ValueTask<ObjectRelationsSnapshot?> ForwardAsync(ObjectRef target, CancellationToken cancellationToken) {
        if (forwardSnapshots.TryGetValue(target, out var cached)) {
            return cached;
        }

        var read = await forward.ReadAsync(target, cancellationToken).ConfigureAwait(false);
        if (read.TryGetError(out var error)) {
            readFailure = error;
            return null;
        }

        var snapshot = read.GetValueOrThrow();
        forwardSnapshots[target] = snapshot;
        return snapshot;
    }

    readonly record struct Pair(ObjectRef Object, string Name);

    /// <summary>Counts reads, and is the reader the verifying <see cref="CheckEvaluator" /> shares.</summary>
    sealed class CountingForwardReader(IRelationReader inner) : IRelationReader {
        public int Reads { get; private set; }

        public async ValueTask<Result<ObjectRelationsSnapshot>> ReadAsync(
            ObjectRef target,
            CancellationToken cancellationToken
        ) {
            Reads++;
            return await inner.ReadAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    sealed class CountingReverseReader(IReverseRelationReader inner) : IReverseRelationReader {
        public int Reads { get; private set; }

        public async ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
            ObjectRef subjectObject,
            CancellationToken cancellationToken
        ) {
            Reads++;
            return await inner.ReadAsync(subjectObject, cancellationToken).ConfigureAwait(false);
        }
    }
}
