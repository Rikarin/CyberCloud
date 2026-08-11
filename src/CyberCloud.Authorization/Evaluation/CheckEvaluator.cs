using System.Globalization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>What one evaluation concluded, before it becomes a <see cref="CheckResult" />.</summary>
public sealed record CheckEvaluation
{
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
///         ⚠ <b>But an in-progress false must not be <i>memoized</i>, and that half is not in the
///         document.</b> Consider <c>a = Rel(b) | This</c>, <c>b = Rel(a)</c>, with a direct tuple
///         on <c>a</c>. Evaluating <c>a</c> descends into <c>b</c>, which loops back to <c>a</c> and
///         gets the in-progress false, so <c>b</c> concludes false; then <c>a</c>'s
///         <c>This</c> succeeds and <c>a</c> is true — at which point <c>b</c> is true as well, and
///         a memoized <c>b = false</c> is simply wrong. It would surface as
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
/// </remarks>
public sealed class CheckEvaluator
{
    readonly AuthorizationSchema schema;
    readonly IRelationReader reader;
    readonly AuthorizationLimits limits;
    readonly IMembershipIndex membershipIndex;

    readonly Dictionary<Triple, bool> memo = [];
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
    ///     The Leopard fast path. <c>null</c> means <see cref="NoMembershipIndex" /> — M1.
    /// </param>
    public CheckEvaluator(
        AuthorizationSchema schema,
        IRelationReader reader,
        AuthorizationLimits? limits = null,
        IMembershipIndex? membershipIndex = null)
    {
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentNullException.ThrowIfNull(subject);

        if (schema.Type(@object.Type) is null)
        {
            return Result<CheckEvaluation>.Failure(
                ErrorCode.SchemaInvalid,
                $"'{@object.Type}' is not an object type in schema version "
                + schema.Version.ToString(CultureInfo.InvariantCulture) + ". It defines ["
                + string.Join(", ", schema.TypeNames) + "].");
        }

        if (schema.Member(@object.Type, permission) is null)
        {
            return Result<CheckEvaluation>.Failure(
                ErrorCode.SchemaInvalid,
                $"'{@object.Type}' defines no '{permission}'. Its permissions are ["
                + string.Join(", ", schema.Type(@object.Type)!.Permissions)
                + "] and its relations are ["
                + string.Join(", ", schema.Type(@object.Type)!.Relations)
                + "]. docs/plan/07 § The model — a typo'd permission name must be neither a silent "
                + "allow-nothing nor a silent allow-everything, so it is this failure instead.");
        }

        AuthorizationMetrics.RecordCheck();

        var result = await EvaluateNameAsync(@object, permission, subject, 0, cancellationToken)
            .ConfigureAwait(false);

        if (readFailure is not null)
        {
            return Result<CheckEvaluation>.Failure(readFailure);
        }

        var outcome = result.Value ? CheckOutcome.Allowed
            : result.Truncated ? cap
            : CheckOutcome.Denied;

        if (outcome == CheckOutcome.DepthCapExceeded)
        {
            AuthorizationMetrics.RecordDepthCap();
        }
        else if (outcome == CheckOutcome.BreadthCapExceeded)
        {
            AuthorizationMetrics.RecordBreadthCap();
        }

        return Result<CheckEvaluation>.Success(new CheckEvaluation
        {
            Allowed = result.Value,
            Outcome = outcome,
            TriplesVisited = triplesVisited,
            MaxDepthReached = maxDepthReached,
            CapDetail = outcome is CheckOutcome.Allowed or CheckOutcome.Denied ? string.Empty : capDetail,
        });
    }

    async ValueTask<NodeResult> EvaluateNameAsync(
        ObjectRef @object,
        string name,
        SubjectRef subject,
        int depth,
        CancellationToken cancellationToken)
    {
        if (readFailure is not null)
        {
            return NodeResult.False;
        }

        maxDepthReached = Math.Max(maxDepthReached, depth);

        if (depth > limits.MaxDepth)
        {
            RecordCap(
                CheckOutcome.DepthCapExceeded,
                $"the walk reached depth {depth.ToString(CultureInfo.InvariantCulture)} at "
                + $"{@object}#{name}, past the cap of "
                + limits.MaxDepth.ToString(CultureInfo.InvariantCulture));

            return NodeResult.Cut;
        }

        var triple = new Triple(@object.Type, @object.Id, name, subject.ToString());

        if (memo.TryGetValue(triple, out var memoized))
        {
            return new NodeResult(memoized, Cyclic: false, Truncated: false);
        }

        if (inProgress.Contains(triple))
        {
            // docs/plan/07 § Check: "a revisit is a cache hit that returns 'in progress → false for
            // this path', which is the correct semantics for a union". The Cyclic flag is what
            // stops that false from being written down — see the remarks on this class.
            return new NodeResult(Value: false, Cyclic: true, Truncated: false);
        }

        var member = schema.Member(@object.Type, name);
        if (member is null)
        {
            // A tuple points at an object whose type does not define this name. That is data
            // referring to a vocabulary that has moved, not a caller error, so it is a deny for
            // this path rather than a failed request — and it is fail-closed.
            return NodeResult.False;
        }

        triplesVisited++;
        inProgress.Add(triple);

        NodeResult result;
        try
        {
            result = await EvaluateExpressionAsync(
                    @object, name, member.Expression, subject, depth, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            inProgress.Remove(triple);
        }

        // A true is a real derivation and can never be withdrawn, so it is always written down.
        // A false is only written down when nothing under it was cut short and nothing under it
        // leaned on an in-progress marker.
        if (result.Value || (!result.Cyclic && !result.Truncated))
        {
            memo[triple] = result.Value;
        }

        return result;
    }

    async ValueTask<NodeResult> EvaluateExpressionAsync(
        ObjectRef @object,
        string relation,
        RelationExpression expression,
        SubjectRef subject,
        int depth,
        CancellationToken cancellationToken)
    {
        switch (expression)
        {
            case ThisExpression:
                return await EvaluateDirectAsync(@object, relation, subject, depth, cancellationToken)
                    .ConfigureAwait(false);

            case RelationRefExpression reference:
                // Same object, so no hop and no depth increment — see AuthorizationLimits.
                return await EvaluateNameAsync(
                        @object, reference.Relation, subject, depth, cancellationToken)
                    .ConfigureAwait(false);

            case TuplesetExpression tupleset:
                return await EvaluateTuplesetAsync(@object, tupleset, subject, depth, cancellationToken)
                    .ConfigureAwait(false);

            case UnionExpression union:
                {
                    var flags = NodeResult.False;
                    foreach (var operand in union.Operands)
                    {
                        var operandResult = await EvaluateExpressionAsync(
                                @object, relation, operand, subject, depth, cancellationToken)
                            .ConfigureAwait(false);

                        flags = flags.Merge(operandResult);
                        if (operandResult.Value)
                        {
                            // docs/plan/07 § Check, step 5: "Short-circuit on the first true."
                            return flags.WithValue(true);
                        }
                    }

                    return flags.WithValue(false);
                }

            case IntersectionExpression intersection:
                {
                    var flags = NodeResult.False;
                    foreach (var operand in intersection.Operands)
                    {
                        var operandResult = await EvaluateExpressionAsync(
                                @object, relation, operand, subject, depth, cancellationToken)
                            .ConfigureAwait(false);

                        flags = flags.Merge(operandResult);
                        if (!operandResult.Value)
                        {
                            return flags.WithValue(false);
                        }
                    }

                    return flags.WithValue(true);
                }

            case ExclusionExpression exclusion:
                {
                    var operandResult = await EvaluateExpressionAsync(
                            @object, relation, exclusion.Operand, subject, depth, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(@object, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return NodeResult.False;
        }

        var subjects = snapshot.Subjects(relation);

        // A concrete match is a set test over tuples already read. It costs no grain call and no
        // recursion, so it is not charged against the breadth cap — see AuthorizationLimits.
        foreach (var candidate in subjects)
        {
            if (candidate == subject)
            {
                return NodeResult.True;
            }
        }

        var flags = NodeResult.False;
        var expansions = 0;

        foreach (var candidate in subjects)
        {
            if (!candidate.IsUserset)
            {
                continue;
            }

            if (expansions == limits.MaxBreadth)
            {
                RecordCap(
                    CheckOutcome.BreadthCapExceeded,
                    $"{@object}#{relation} has more than "
                    + limits.MaxBreadth.ToString(CultureInfo.InvariantCulture)
                    + " userset subjects to expand");

                return flags.Merge(NodeResult.Cut).WithValue(false);
            }

            expansions++;

            var indexed = await membershipIndex
                .TryTestMembershipAsync(candidate, subject, cancellationToken)
                .ConfigureAwait(false);

            if (indexed is not null)
            {
                if (indexed.Value)
                {
                    return flags.WithValue(true);
                }

                continue;
            }

            var nested = await EvaluateNameAsync(
                candidate.Object, candidate.Relation, subject, depth + 1, cancellationToken)
                .ConfigureAwait(false);

            flags = flags.Merge(nested);
            if (nested.Value)
            {
                return flags.WithValue(true);
            }
        }

        return flags.WithValue(false);
    }

    async ValueTask<NodeResult> EvaluateTuplesetAsync(
        ObjectRef @object,
        TuplesetExpression tupleset,
        SubjectRef subject,
        int depth,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(@object, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return NodeResult.False;
        }

        var flags = NodeResult.False;
        var expansions = 0;

        foreach (var target in snapshot.Subjects(tupleset.Tupleset))
        {
            if (expansions == limits.MaxBreadth)
            {
                RecordCap(
                    CheckOutcome.BreadthCapExceeded,
                    $"{@object}#{tupleset.Tupleset} points at more than "
                    + limits.MaxBreadth.ToString(CultureInfo.InvariantCulture) + " objects");

                return flags.Merge(NodeResult.Cut).WithValue(false);
            }

            expansions++;

            // A tupleset yields OBJECTS. A userset written there is read as its object half, which
            // is the reading that keeps `From("parent", …)` meaningful when somebody writes
            // `subscription:S#parent@tenant:T#owner` by mistake.
            var nested = await EvaluateNameAsync(
                target.Object, tupleset.Computed, subject, depth + 1, cancellationToken)
                .ConfigureAwait(false);

            flags = flags.Merge(nested);
            if (nested.Value)
            {
                return flags.WithValue(true);
            }
        }

        return flags.WithValue(false);
    }

    async ValueTask<ObjectRelationsSnapshot?> SnapshotAsync(
        ObjectRef @object, CancellationToken cancellationToken)
    {
        var key = (@object.Type, @object.Id);
        if (snapshots.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var read = await reader.ReadAsync(@object, cancellationToken).ConfigureAwait(false);
        if (read.TryGetError(out var error))
        {
            readFailure = error;
            return null;
        }

        var snapshot = read.GetValueOrThrow();
        snapshots[key] = snapshot;
        return snapshot;
    }

    void RecordCap(CheckOutcome outcome, string detail)
    {
        // The first cap hit is the one reported. A walk that hits both is reported as whichever it
        // met first, which is the one that actually shaped the answer.
        if (cap != CheckOutcome.Denied)
        {
            return;
        }

        cap = outcome;
        capDetail = detail;
    }

    readonly record struct Triple(string Type, string Id, string Name, string Subject);

    readonly record struct NodeResult(bool Value, bool Cyclic, bool Truncated)
    {
        public static NodeResult False { get; } = new(false, false, false);

        public static NodeResult True { get; } = new(true, false, false);

        public static NodeResult Cut { get; } = new(false, false, true);

        public NodeResult Merge(NodeResult other) =>
            new(Value, Cyclic || other.Cyclic, Truncated || other.Truncated);

        public NodeResult WithValue(bool value) => new(value, Cyclic, Truncated);
    }
}
