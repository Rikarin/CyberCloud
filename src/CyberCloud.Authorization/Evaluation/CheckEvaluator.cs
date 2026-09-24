using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using System.Globalization;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>What one evaluation concluded, before it becomes a <see cref="CheckResult" />.</summary>
public sealed record CheckEvaluation {
    /// <summary>The decision. Always false unless <see cref="Outcome" /> is allowed.</summary>
    public bool Allowed { get; init; }

    /// <summary>Allowed, a genuine deny, or one of the two caps.</summary>
    public CheckOutcome Outcome { get; init; } = CheckOutcome.Unknown;

    /// <summary>How many <c>(object, relation, subject)</c> triples were visited.</summary>
    public int TriplesVisited { get; init; }

    /// <summary>The deepest object hop reached.</summary>
    public int MaxDepthReached { get; init; }

    /// <summary>Where the cap was hit, or empty.</summary>
    public string CapDetail { get; init; } = string.Empty;

    /// <summary>
    ///     The instant the answer may change with no write, or <see langword="null" /> for never —
    ///     <see cref="CheckResult.ValidUntil" />, before it goes on the wire.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>
    ///     Whether the answer is safe to cache. ⚠ False for a truncated walk: caching "I gave up"
    ///     would make one unlucky walk permanent.
    /// </summary>
    public bool IsCacheable => Outcome is CheckOutcome.Allowed or CheckOutcome.Denied;
}

/// <summary>
///     <c>Check</c>'s bounded, memoized search over the rewrite tree — docs/plan/07 § Check.
/// </summary>
/// <remarks>
///     <para>
///         One instance per request. It holds the memo, so it is <b>not</b> reusable and is not
///         thread-safe; a grain creates one per call.
///     </para>
///     <para>
///         <b>Cycles are broken by the memo, not by cycle detection</b> — docs/plan/07 § Check. A
///         revisit of a triple that is still on the stack returns "in progress → false for this
///         path". That is exactly right for a union and it is what stops a diamond-shaped org chart
///         from being exponential.
///     </para>
///     <para>
///         ⚠
///         <b>
///             But an in-progress false must not be <i>memoized</i>, and that half is not in the
///             document.
///         </b>
///         Consider <c>a = Rel(b) | This</c>, <c>b = Rel(a)</c>, with a direct tuple
///         on <c>a</c>. Evaluating <c>a</c> descends into <c>b</c>, which loops back to <c>a</c> and
///         gets the in-progress false, so <c>b</c> concludes false; then <c>a</c>'s
///         <c>This</c> succeeds and <c>a</c> is true — at which point <c>b</c> is true as well, and
///         a memoized <c>b = false</c> is wrong. It would surface as
///         <c>Permission(p, Rel(a) &amp; Rel(b))</c> denying a subject who has both.
///     </para>
///     <para>
///         So every result carries a <i>cyclic</i> flag, set when the computation consulted an
///         in-progress marker and propagated to its parents. A <b>false</b> that is cyclic is
///         returned but not memoized, so a later query recomputes it against whatever has since
///         become known. A <b>true</b> is always memoized: a true is a real derivation and cannot be
///         withdrawn. That is the standard proviso for tabled evaluation of a definite program, and
///         with it the value at the root of an evaluation is exactly the least fixed point — which
///         is what <c>CheckAgreesWithTheReferenceEvaluator</c> asserts over generated cyclic graphs
///         against an obviously-correct iterate-to-fixpoint reference.
///     </para>
///     <para>
///         ⚠ The cost of not memoizing cyclic falses is that a triple inside a cycle can be walked
///         more than once per request. The caps bound it, and the memo still holds every
///         non-cyclic result, so in practice it is one recomputation per cycle. Trading that for a
///         wrong answer under intersection is not a trade worth making.
///     </para>
///     <para>
///         <b>Every result also carries the instant it may change with no write</b> — docs/plan/07
///         § Time-bounded relations. The reader has already dropped every expired tuple, so the
///         walk never compares a clock; what it does is carry, beside each value, the earliest
///         expiry of the tuples that value rests on, and the check cache stops serving the answer at
///         that instant. Time only ever removes tuples, so the rule per node is short:
///     </para>
///     <list type="bullet">
///         <item>
///             A <b>true</b> is good until the earliest expiry along the derivation that proved it:
///             the matching tuple, the userset or tupleset tuple a hop crossed, and whatever the hop
///             itself was good until. A union takes the operand that decided it; an intersection
///             takes the earliest of all its operands.
///         </item>
///         <item>
///             A <b>false</b> built from falses is good until the earliest of theirs, and a false
///             with no negation beneath it is good for ever, because a grant can't appear by
///             expiring. The one false that can flip is <c>A &amp; !B</c> denied by a live <c>B</c>
///             — a <c>#suspended</c> with an expiry — and an exclusion passes its operand's instant
///             through for exactly that.
///         </item>
///         <item>
///             An index answer is good for ever, and the tuple that led to it is what bounds it: the
///             membership index closes over permanent edges only (<c>MembershipIndexMaintainer</c>),
///             so a membership it confirms can't expire.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The instant is a lower bound and is allowed to be early.</b> A union short-circuits
///         on its first true, so a subject holding a permission two ways is told the grant ends
///         when the first way it found does; the cache then re-walks at that instant and finds the
///         second. Being early costs a walk. Being late is a privilege that outlives its grant, and
///         <c>ExpiryPropertyTests</c> is what holds every instant to "never late" against the
///         reference evaluator.
///     </para>
/// </remarks>
public sealed class CheckEvaluator {
    readonly AuthorizationSchema schema;
    readonly IRelationReader reader;
    readonly AuthorizationLimits limits;
    readonly IMembershipIndex membershipIndex;

    readonly Dictionary<Triple, (bool Value, DateTimeOffset? Until)> memo = [];
    readonly HashSet<Triple> inProgress = [];
    readonly Dictionary<(string Type, string Id), ObjectRelationsSnapshot> snapshots = [];

    int triplesVisited;
    int maxDepthReached;
    CheckOutcome cap = CheckOutcome.Denied;
    string capDetail = string.Empty;
    Error? readFailure;

    /// <summary>Creates an evaluator for one request.</summary>
    /// <param name="schema">The schema. Already validated — see <see cref="AuthorizationSchema" />.</param>
    /// <param name="reader">Where tuples come from.</param>
    /// <param name="limits">The caps. <c>null</c> means <see cref="AuthorizationLimits.Default" />.</param>
    /// <param name="membershipIndex">
    ///     The Leopard fast path. <c>null</c> means <see cref="NoMembershipIndex" /> — every
    ///     userset is walked.
    /// </param>
    public CheckEvaluator(
        AuthorizationSchema schema,
        IRelationReader reader,
        AuthorizationLimits? limits = null,
        IMembershipIndex? membershipIndex = null
    ) {
        this.schema = schema;
        this.reader = reader;
        this.limits = limits ?? AuthorizationLimits.Default;
        this.membershipIndex = membershipIndex ?? NoMembershipIndex.Instance;
    }

    /// <summary>Evaluates one check.</summary>
    /// <param name="object">The object.</param>
    /// <param name="permission">The permission or relation name.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    ///     A <c>Result</c> failure means the question was not answerable —
    ///     <see cref="ErrorCode.SchemaInvalid" /> for a name the schema does not define, or a
    ///     storage error from the reader. A denial is a <i>successful</i>
    ///     <see cref="CheckEvaluation" /> with <see cref="CheckEvaluation.Allowed" /> false.
    /// </remarks>
    public async Task<Result<CheckEvaluation>> EvaluateAsync(
        ObjectRef @object,
        string permission,
        SubjectRef subject,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentNullException.ThrowIfNull(subject);

        if (schema.Type(@object.Type) is null) {
            return Result<CheckEvaluation>.Failure(
                ErrorCode.SchemaInvalid,
                $"'{@object.Type}' is not an object type in schema version "
                + schema.Version.ToString(CultureInfo.InvariantCulture)
                + ". It defines ["
                + string.Join(", ", schema.TypeNames)
                + "]."
            );
        }

        if (schema.Member(@object.Type, permission) is null) {
            return Result<CheckEvaluation>.Failure(
                ErrorCode.SchemaInvalid,
                $"'{@object.Type}' defines no '{permission}'. Its permissions are ["
                + string.Join(", ", schema.Type(@object.Type)!.Permissions)
                + "] and its relations are ["
                + string.Join(", ", schema.Type(@object.Type)!.Relations)
                + "]. docs/plan/07 § The model — a typo'd permission name must be neither a silent "
                + "allow-nothing nor a silent allow-everything, so it is this failure instead."
            );
        }

        AuthorizationMetrics.RecordCheck();

        var result = await EvaluateNameAsync(@object, permission, subject, 0, cancellationToken)
            .ConfigureAwait(false);

        if (readFailure is not null) {
            return Result<CheckEvaluation>.Failure(readFailure);
        }

        var outcome = result.Value ? CheckOutcome.Allowed
            : result.Truncated ? cap
            : CheckOutcome.Denied;

        if (outcome == CheckOutcome.DepthCapExceeded) {
            AuthorizationMetrics.RecordDepthCap();
        } else if (outcome == CheckOutcome.BreadthCapExceeded) {
            AuthorizationMetrics.RecordBreadthCap();
        }

        return Result<CheckEvaluation>.Success(
            new() {
                Allowed = result.Value,
                Outcome = outcome,
                TriplesVisited = triplesVisited,
                MaxDepthReached = maxDepthReached,
                CapDetail = outcome is CheckOutcome.Allowed or CheckOutcome.Denied ? string.Empty : capDetail,
                ValidUntil = result.Until
            }
        );
    }

    async ValueTask<NodeResult> EvaluateNameAsync(
        ObjectRef @object,
        string name,
        SubjectRef subject,
        int depth,
        CancellationToken cancellationToken
    ) {
        if (readFailure is not null) {
            return NodeResult.False;
        }

        maxDepthReached = Math.Max(maxDepthReached, depth);

        if (depth > limits.MaxDepth) {
            RecordCap(
                CheckOutcome.DepthCapExceeded,
                $"the walk reached depth {depth.ToString(CultureInfo.InvariantCulture)} at "
                + $"{@object}#{name}, past the cap of "
                + limits.MaxDepth.ToString(CultureInfo.InvariantCulture)
            );

            return NodeResult.Cut;
        }

        var triple = new Triple(@object.Type, @object.Id, name, subject.ToString());

        if (memo.TryGetValue(triple, out var memoized)) {
            return new(memoized.Value, false, false, memoized.Until);
        }

        if (inProgress.Contains(triple)) {
            // docs/plan/07 § Check: "a revisit is a cache hit that returns 'in progress → false for
            // this path', which is the correct semantics for a union". The Cyclic flag is what
            // stops that false from being written down — see the remarks on this class.
            return new(false, true, false, null);
        }

        var member = schema.Member(@object.Type, name);
        if (member is null) {
            // A tuple points at an object whose type does not define this name. That is data
            // referring to a vocabulary that has moved, not a caller error, so it is a deny for
            // this path rather than a failed request — and it is fail-closed.
            return NodeResult.False;
        }

        triplesVisited++;
        inProgress.Add(triple);

        NodeResult result;
        try {
            result = await EvaluateExpressionAsync(@object, name, member.Expression, subject, depth, cancellationToken)
                .ConfigureAwait(false);
        } finally {
            inProgress.Remove(triple);
        }

        // A true is a real derivation and can never be withdrawn, so it is always written down.
        // A false is only written down when nothing under it was cut short and nothing under it
        // leaned on an in-progress marker.
        if (result.Value || result is { Cyclic: false, Truncated: false }) {
            memo[triple] = (result.Value, result.Until);
        }

        return result;
    }

    async ValueTask<NodeResult> EvaluateExpressionAsync(
        ObjectRef @object,
        string relation,
        RelationExpression expression,
        SubjectRef subject,
        int depth,
        CancellationToken cancellationToken
    ) {
        switch (expression) {
            case ThisExpression:
                return await EvaluateDirectAsync(@object, relation, subject, depth, cancellationToken)
                    .ConfigureAwait(false);

            case RelationRefExpression reference:
                // Same object, so no hop and no depth increment — see AuthorizationLimits.
                return await EvaluateNameAsync(@object, reference.Relation, subject, depth, cancellationToken)
                    .ConfigureAwait(false);

            case TuplesetExpression tupleset:
                return await EvaluateTuplesetAsync(@object, tupleset, subject, depth, cancellationToken)
                    .ConfigureAwait(false);

            case UnionExpression union: {
                var flags = NodeResult.False;
                foreach (var operand in union.Operands) {
                    var operandResult = await EvaluateExpressionAsync(
                        @object,
                        relation,
                        operand,
                        subject,
                        depth,
                        cancellationToken
                    )
                            .ConfigureAwait(false);

                    if (operandResult.Value) {
                        // docs/plan/07 § Check, step 5: "Short-circuit on the first true." It stays
                        // true for as long as this operand does, whatever the others would do.
                        return flags.DecidedBy(operandResult);
                    }

                    flags = flags.Merge(operandResult);
                }

                return flags.WithValue(false);
            }

            case IntersectionExpression intersection: {
                var flags = NodeResult.False;
                foreach (var operand in intersection.Operands) {
                    var operandResult = await EvaluateExpressionAsync(
                        @object,
                        relation,
                        operand,
                        subject,
                        depth,
                        cancellationToken
                    )
                            .ConfigureAwait(false);

                    if (!operandResult.Value) {
                        // False for as long as this operand is, whatever the others would do.
                        return flags.DecidedBy(operandResult);
                    }

                    flags = flags.Merge(operandResult);
                }

                // True only while every operand is, so good until the earliest of them.
                return flags.WithValue(true);
            }

            case ExclusionExpression exclusion: {
                var operandResult = await EvaluateExpressionAsync(
                    @object,
                    relation,
                    exclusion.Operand,
                    subject,
                    depth,
                    cancellationToken
                )
                        .ConfigureAwait(false);

                // ⚠ FAIL-CLOSED THROUGH A NEGATION, which is the one place a cap could
                // otherwise GRANT access. A truncated operand evaluates to false, and `!false`
                // is true — so a walk that ran out of budget inside `!Rel("suspended")` would
                // conclude "not suspended" and allow. The truncation is therefore propagated
                // instead of being negated with the value.
                //
                // This is reachable in practice even though the negated relation is
                // direct-only: a tuple on it may name a USERSET subject, and walking that
                // userset can hit either cap.
                //
                // The operand's instant passes through unchanged: the negation flips exactly when
                // its operand does, and that is how an expiring #suspended un-denies on time.
                return operandResult.Truncated
                    ? operandResult.WithValue(false)
                    : operandResult.WithValue(!operandResult.Value);
            }

            default:
                return NodeResult.False;
        }
    }

    async ValueTask<NodeResult> EvaluateDirectAsync(
        ObjectRef @object,
        string relation,
        SubjectRef subject,
        int depth,
        CancellationToken cancellationToken
    ) {
        var snapshot = await SnapshotAsync(@object, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) {
            return NodeResult.False;
        }

        var subjects = snapshot.Subjects(relation);

        // A concrete match is a set test over tuples already read. It costs no grain call and no
        // recursion, so it is not charged against the breadth cap — see AuthorizationLimits.
        foreach (var candidate in subjects) {
            if (candidate == subject) {
                return NodeResult.TrueUntil(snapshot.ExpiryOf(relation, candidate));
            }
        }

        var flags = NodeResult.False;
        var expansions = 0;

        foreach (var candidate in subjects) {
            if (!candidate.IsUserset) {
                continue;
            }

            // docs/plan/07 § Check, step 3: the index first, the walk only when it declines. An
            // answered userset is not charged against the breadth cap — AuthorizationLimits says
            // the cap counts "recursive expansions", and an index read is a set test, not a walk.
            // That is what lets a subject granted through the 1 001st group on one object be
            // allowed here and listed by ListObjects alike, which the walk's remarks record as the
            // one place the two evaluators used to disagree.
            var indexed = await membershipIndex
                .TryTestMembershipAsync(candidate, subject, cancellationToken)
                .ConfigureAwait(false);

            if (indexed is not null) {
                if (indexed.Value) {
                    // The index closes over permanent edges only, so the membership can't expire;
                    // the tuple that points at the userset still can.
                    return flags.DecidedBy(NodeResult.TrueUntil(snapshot.ExpiryOf(relation, candidate)));
                }

                continue;
            }

            if (expansions == limits.MaxBreadth) {
                RecordCap(
                    CheckOutcome.BreadthCapExceeded,
                    $"{@object}#{relation} has more than "
                    + limits.MaxBreadth.ToString(CultureInfo.InvariantCulture)
                    + " userset subjects to expand"
                );

                return flags.Merge(NodeResult.Cut).WithValue(false);
            }

            expansions++;

            var nested = await EvaluateNameAsync(
                candidate.Object,
                candidate.Relation,
                subject,
                depth + 1,
                cancellationToken
            )
                    .ConfigureAwait(false);

            var hop = Through(nested, snapshot.ExpiryOf(relation, candidate));
            if (hop.Value) {
                return flags.DecidedBy(hop);
            }

            flags = flags.Merge(hop);
        }

        return flags.WithValue(false);
    }

    /// <summary>
    ///     A hop's result seen from the node that made it: bounded by the tuple crossed as well as
    ///     by whatever the far side was good until.
    /// </summary>
    static NodeResult Through(NodeResult nested, DateTimeOffset? crossed) =>
        nested with { Until = TupleExpiry.Earliest(nested.Until, crossed) };

    async ValueTask<NodeResult> EvaluateTuplesetAsync(
        ObjectRef @object,
        TuplesetExpression tupleset,
        SubjectRef subject,
        int depth,
        CancellationToken cancellationToken
    ) {
        var snapshot = await SnapshotAsync(@object, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) {
            return NodeResult.False;
        }

        var flags = NodeResult.False;
        var expansions = 0;

        foreach (var target in snapshot.Subjects(tupleset.Tupleset)) {
            if (expansions == limits.MaxBreadth) {
                RecordCap(
                    CheckOutcome.BreadthCapExceeded,
                    $"{@object}#{tupleset.Tupleset} points at more than "
                    + limits.MaxBreadth.ToString(CultureInfo.InvariantCulture)
                    + " objects"
                );

                return flags.Merge(NodeResult.Cut).WithValue(false);
            }

            expansions++;

            // A tupleset yields OBJECTS. A userset written there is read as its object half, which
            // is the reading that keeps `From("parent", …)` meaningful when somebody writes
            // `subscription:S#parent@tenant:T#owner` by mistake.
            var nested = await EvaluateNameAsync(
                target.Object,
                tupleset.Computed,
                subject,
                depth + 1,
                cancellationToken
            )
                    .ConfigureAwait(false);

            var hop = Through(nested, snapshot.ExpiryOf(tupleset.Tupleset, target));
            if (hop.Value) {
                return flags.DecidedBy(hop);
            }

            flags = flags.Merge(hop);
        }

        return flags.WithValue(false);
    }

    async ValueTask<ObjectRelationsSnapshot?> SnapshotAsync(
        ObjectRef @object,
        CancellationToken cancellationToken
    ) {
        var key = (@object.Type, @object.Id);
        if (snapshots.TryGetValue(key, out var cached)) {
            return cached;
        }

        var read = await reader.ReadAsync(@object, cancellationToken).ConfigureAwait(false);
        if (read.TryGetError(out var error)) {
            readFailure = error;
            return null;
        }

        var snapshot = read.GetValueOrThrow();
        snapshots[key] = snapshot;
        return snapshot;
    }

    void RecordCap(CheckOutcome outcome, string detail) {
        // The first cap hit is the one reported. A walk that hits both is reported as whichever it
        // met first, which is the one that actually shaped the answer.
        if (cap != CheckOutcome.Denied) {
            return;
        }

        cap = outcome;
        capDetail = detail;
    }

    readonly record struct Triple(string Type, string Id, string Name, string Subject);

    /// <summary>One node's answer, and how far it can be trusted.</summary>
    /// <param name="Value">The answer.</param>
    /// <param name="Cyclic">Whether it leaned on an in-progress marker — see the remarks on the class.</param>
    /// <param name="Truncated">Whether a cap cut something beneath it.</param>
    /// <param name="Until">
    ///     The instant the value may change with no write, or <see langword="null" /> for never — see
    ///     the remarks on the class.
    /// </param>
    readonly record struct NodeResult(bool Value, bool Cyclic, bool Truncated, DateTimeOffset? Until) {
        public static NodeResult False { get; } = new(false, false, false, null);

        public static NodeResult Cut { get; } = new(false, false, true, null);

        /// <summary>A true proved by one tuple that expires at <paramref name="until" />.</summary>
        public static NodeResult TrueUntil(DateTimeOffset? until) => new(true, false, false, until);

        /// <summary>
        ///     Folds a sibling in: the flags are an OR, and the instant is the earlier of the two,
        ///     which is what a false made of several falses, or a true that needs every operand, is
        ///     good until.
        /// </summary>
        public NodeResult Merge(NodeResult other) =>
            new(
                Value,
                Cyclic || other.Cyclic,
                Truncated || other.Truncated,
                TupleExpiry.Earliest(Until, other.Until)
            );

        public NodeResult WithValue(bool value) => this with { Value = value };

        /// <summary>
        ///     This node's flags with the value and the instant of the one operand that decided it —
        ///     a union's first true, an intersection's first false.
        /// </summary>
        public NodeResult DecidedBy(NodeResult decider) =>
            new(decider.Value, Cyclic || decider.Cyclic, Truncated || decider.Truncated, decider.Until);
    }
}
