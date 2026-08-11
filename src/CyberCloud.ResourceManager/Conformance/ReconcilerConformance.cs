using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Conformance;

/// <summary>
///     The two things a caller must be able to do to their own world for clause 4 to be checkable.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two delegates rather than one, and the second is the one that does the work.</b> The
///         obvious harness — break the world, then assert the next pass is not
///         <see cref="ReconcileOutcome.Converged" /> — is <i>wrong</i>, and finding that out cost a
///         test run: a <b>conforming</b> reconciler notices the world is broken, re-applies, reads
///         back and correctly reports <see cref="ReconcileOutcome.Converged" /> in the same pass. So
///         "still says Converged" does not separate the two.
///     </para>
///     <para>
///         What separates them is what happened to the <i>world</i>. A reconciler that observes either
///         repairs it or says it is not ready; one that assumes leaves it broken and says it is fine.
///         The harness cannot check the world itself — the reconciler's own <c>ObserveAsync</c> is
///         exactly as unreliable as the reconciler — so the caller, who broke it, supplies the ground
///         truth.
///     </para>
/// </remarks>
/// <param name="BreakAsync">
///     Removes whatever the reconciler applied, without telling it. ⚠ Must go around the reconciler,
///     not through it.
/// </param>
/// <param name="MatchesDesiredAsync">
///     Whether the world now matches the desired shape, read <b>independently of the reconciler</b>.
/// </param>
public readonly record struct ConformanceWorld(
    Func<Task> BreakAsync,
    Func<Task<bool>> MatchesDesiredAsync
);

/// <summary>Which of the four clauses a conformance finding is about.</summary>
public enum ReconcilerClause {
    /// <summary>Not a clause.</summary>
    None = 0,

    /// <summary>Called twice with identical inputs, the second is <c>Converged</c> and changes nothing.</summary>
    Idempotent = 1,

    /// <summary>Everything it needs comes from <c>ReconcileContext</c>. A field breaks on a silo move.</summary>
    NoHiddenState = 2,

    /// <summary>Returns within 30 seconds or returns <c>InProgress</c>.</summary>
    Bounded = 3,

    /// <summary><c>Converged</c> means it read the desired shape <b>back</b>.</summary>
    ObservesNeverAssumes = 4
}

/// <summary>One way a reconciler failed the contract.</summary>
/// <param name="Clause">Which clause.</param>
/// <param name="Detail">What happened, naming the reconciler and the observation.</param>
public readonly record struct ConformanceFinding(ReconcilerClause Clause, string Detail) {
    /// <inheritdoc />
    public override string ToString() => $"Clause {(int)Clause} ({Clause}): {Detail}";
}

/// <summary>What a conformance run concluded.</summary>
/// <param name="Reconciler">The type checked.</param>
/// <param name="Findings">What failed. Empty means it conforms.</param>
public readonly record struct ConformanceReport(string Reconciler, ImmutableArray<ConformanceFinding> Findings) {
    /// <summary>Whether the reconciler satisfies all four clauses.</summary>
    public bool Conforms => Findings.IsDefaultOrEmpty;

    /// <inheritdoc />
    public override string ToString() =>
        Conforms
            ? $"{Reconciler} conforms."
            : $"{Reconciler} fails the reconciler contract:{Environment.NewLine}"
            + string.Join(Environment.NewLine, Findings.Select(x => "  - " + x));
}

/// <summary>
///     The conformance suite of docs/plan/08 § The reconcile loop — <i>"The contract every reconciler
///     must satisfy, checked by the conformance suite"</i>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every provider's test project runs this against every reconciler it ships.</b> The
///         suite is here, in the platform assembly, rather than copied into each provider, because
///         twenty copies is twenty chances for one of them to quietly drop a clause — which is the
///         same argument docs/plan/08 makes for the write path being one component.
///     </para>
///     <para>
///         ⚠ <b>Clause 4 is checked by making the world disagree, which is the only way to check
///         it.</b> A reconciler that <i>assumes</i> and one that <i>observes</i> are
///         indistinguishable while the apply keeps working — both report
///         <see cref="ReconcileOutcome.Converged" />. So the harness converges the reconciler, then
///         asks the caller's <c>breakTheWorld</c> to remove what was applied, and asserts
///         that the next pass <b>no longer</b> reports <see cref="ReconcileOutcome.Converged" />. A
///         reconciler that cached "I applied this, so it is there" fails; one that reads back passes.
///         There is no static check that could tell them apart.
///     </para>
///     <para>
///         ⚠ <b>Clause 2 is checked structurally and the check has a known blind spot.</b> Instance
///         fields are the shape docs/plan/08 names — <i>"a reconciler with a field is a reconciler
///         that breaks when the grain moves silo"</i> — and they are what
///         <see cref="CheckNoHiddenState" /> finds. It cannot find state behind a <c>static</c>, an
///         injected mutable singleton, or an <c>AsyncLocal</c>. Those are real and are left to review;
///         the field case is the one that happens by accident, and the accident is what an automated
///         check is for.
///     </para>
/// </remarks>
public static class ReconcilerConformance {
    /// <summary>
    ///     Runs all four clauses.
    /// </summary>
    /// <param name="reconciler">The reconciler under test.</param>
    /// <param name="context">
    ///     A context to reconcile with. ⚠ Must address a resource the caller is willing to have
    ///     created and torn down — the harness converges it for real.
    /// </param>
    /// <param name="world">
    ///     How to break the caller's world and how to tell, independently of the reconciler, whether
    ///     it matches the desired shape. Clause 4 is checked with this; a caller that cannot do both
    ///     passes <see langword="null" /> and clause 4 is <b>skipped and reported as skipped</b>
    ///     rather than silently passed.
    /// </param>
    /// <param name="clock">The clock, for the bounded check's own timing.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The report. <see cref="ConformanceReport.Conforms" /> is the assertion to make.</returns>
    public static async Task<ConformanceReport> RunAsync(
        IResourceReconciler reconciler,
        ReconcileContext context,
        ConformanceWorld? world,
        IClock clock,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(clock);

        var findings = ImmutableArray.CreateBuilder<ConformanceFinding>();

        findings.AddRange(CheckNoHiddenState(reconciler));

        // ── Clause 3, and clause 1's first half: drive to convergence, bounded. ─────────────────
        var first = await BoundedAsync(reconciler, context, findings, cancellationToken);
        if (first is null) {
            return new(reconciler.GetType().Name, findings.ToImmutable());
        }

        // A reconciler that legitimately needs several passes is driven to convergence before the
        // idempotency clause is tested — clause 1 is about a SECOND call on a CONVERGED resource, not
        // about a reconciler finishing in one pass.
        var passes = 1;
        while (first.Kind == ReconcileOutcomeKind.InProgress && passes < MaxPasses) {
            first = await BoundedAsync(reconciler, context, findings, cancellationToken);
            passes++;

            if (first is null) {
                return new(reconciler.GetType().Name, findings.ToImmutable());
            }
        }

        if (first.Kind == ReconcileOutcomeKind.Failed) {
            findings.Add(
                new(
                    ReconcilerClause.Idempotent,
                    $"The reconciler failed rather than converging, so the contract cannot be checked: "
                    + $"{first.Error?.Message}"
                )
            );

            return new(reconciler.GetType().Name, findings.ToImmutable());
        }

        if (!first.IsConverged) {
            findings.Add(
                new(
                    ReconcilerClause.Bounded,
                    $"The reconciler stayed InProgress across "
                    + $"{MaxPasses.ToString(CultureInfo.InvariantCulture)} passes. A conformance run "
                    + "cannot wait out a real provisioning; give the harness a context whose world is "
                    + "already reachable."
                )
            );

            return new(reconciler.GetType().Name, findings.ToImmutable());
        }

        // ── Clause 1: called again on a converged resource, converged and unchanged. ────────────
        var observedBefore = await ObserveAsync(reconciler, context, cancellationToken);
        var second = await BoundedAsync(reconciler, context, findings, cancellationToken);

        if (second is not null && !second.IsConverged) {
            findings.Add(
                new(
                    ReconcilerClause.Idempotent,
                    $"A second pass on a converged resource returned {second}. The reminder will fire "
                    + "on a converged grain constantly, so a reconciler that does work every time does "
                    + "that work forever."
                )
            );
        }

        var observedAfter = await ObserveAsync(reconciler, context, cancellationToken);

        if (observedBefore is not null
            && observedAfter is not null
            && !string.Equals(observedBefore.Json, observedAfter.Json, StringComparison.Ordinal)) {
            findings.Add(
                new(
                    ReconcilerClause.Idempotent,
                    "A second pass on a converged resource changed the observed state. 'Converged and "
                    + "changes nothing' is both halves; changing something on every pass is a resource "
                    + "that is rewritten once per reminder."
                )
            );
        }

        // ── Clause 4: break the world and see whether Converged was a claim or a reading. ───────
        if (world is not { } breakable) {
            findings.Add(
                new(
                    ReconcilerClause.ObservesNeverAssumes,
                    "SKIPPED — no ConformanceWorld was supplied, so clause 4 was not checked. A "
                    + "reconciler that assumes and one that observes are indistinguishable while the "
                    + "apply keeps working, so this is reported as a finding rather than passed."
                )
            );

            return new(reconciler.GetType().Name, findings.ToImmutable());
        }

        await breakable.BreakAsync();

        var afterBreak = await BoundedAsync(reconciler, context, findings, cancellationToken);

        // ⚠ THE CHECK IS ON THE WORLD, NOT ON THE OUTCOME, AND THAT DISTINCTION IS THE WHOLE CLAUSE.
        //
        // A CONFORMING reconciler may legitimately still say Converged here: it noticed the world was
        // broken, re-applied, read back, and is right. Asserting "no longer Converged" would fail
        // exactly the reconcilers the clause is meant to bless. What an assuming reconciler does that a
        // conforming one never does is claim Converged while leaving the world broken — so the ground
        // truth the caller supplies, read around the reconciler, is what decides.
        if (afterBreak is not null && afterBreak.IsConverged && !await breakable.MatchesDesiredAsync()) {
            findings.Add(
                new(
                    ReconcilerClause.ObservesNeverAssumes,
                    "The reconciler reported Converged while the world did not match the desired shape. "
                    + "Converged means it READ THE DESIRED SHAPE BACK — this one remembered that an "
                    + "apply had succeeded. docs/plan/08 § The reconcile loop, clause 4."
                )
            );
        }

        return new(reconciler.GetType().Name, findings.ToImmutable());
    }

    /// <summary>
    ///     Clause 2, checked structurally: a reconciler with instance fields.
    /// </summary>
    /// <param name="reconciler">The reconciler.</param>
    /// <returns>One finding per offending field, or none.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Primary-constructor parameters are fields and are not the violation.</b> The C#
    ///         compiler turns a captured primary-constructor parameter into a private field named
    ///         <c>&lt;paramName&gt;P</c>, so a perfectly correct
    ///         <c>sealed class X(ILogger log) : IResourceReconciler</c> has a field. Those are
    ///         <i>dependencies</i>, which a singleton is supposed to hold, and they are excluded by
    ///         name. What is left is a field somebody declared to remember something between passes,
    ///         which is the thing that breaks when the grain moves silo.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see langword="readonly" /> fields are excluded for the same reason</b> — an
    ///         injected dependency assigned once in a constructor is the normal shape and is not
    ///         per-pass state. A <see langword="readonly" /> <i>mutable collection</i> defeats that
    ///         exclusion and this check does not catch it; see the remarks on the type about the blind
    ///         spot.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<ConformanceFinding> CheckNoHiddenState(IResourceReconciler reconciler) {
        ArgumentNullException.ThrowIfNull(reconciler);

        var findings = ImmutableArray.CreateBuilder<ConformanceFinding>();
        var type = reconciler.GetType();

        for (var current = type; current is not null && current != typeof(object); current = current.BaseType) {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                if (field.IsInitOnly || IsPrimaryConstructorCapture(field)) {
                    continue;
                }

                findings.Add(
                    new(
                        ReconcilerClause.NoHiddenState,
                        $"'{current.Name}.{field.Name}' is a mutable instance field. Everything a "
                        + "reconciler needs comes from ReconcileContext — a reconciler with a field is "
                        + "a reconciler that breaks when the grain moves silo, and it breaks by "
                        + "converging on stale state rather than by throwing. "
                        + "docs/plan/08 § The reconcile loop, clause 2."
                    )
                );
            }
        }

        return findings.ToImmutable();
    }

    /// <summary>How many passes the harness will drive before giving up on convergence.</summary>
    const int MaxPasses = 5;

    /// <summary>Runs one pass under clause 3's budget and records an overrun.</summary>
    static async Task<ReconcileOutcome?> BoundedAsync(
        IResourceReconciler reconciler,
        ReconcileContext context,
        ImmutableArray<ConformanceFinding>.Builder findings,
        CancellationToken cancellationToken
    ) {
        var budget = Reconcile.ReconcileDriver.PassBudget;

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(budget);

        var started = Stopwatch.GetTimestamp();

        try {
            var outcome = await reconciler.ReconcileAsync(context, source.Token);
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (elapsed > budget) {
                findings.Add(
                    new(
                        ReconcilerClause.Bounded,
                        $"A pass took {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s "
                        + $"and the budget is {budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s. "
                        + "Orleans grains are single-threaded, so a slow pass blocks that grain's turn "
                        + "— return InProgress instead."
                    )
                );
            }

            return outcome;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
            findings.Add(
                new(
                    ReconcilerClause.Bounded,
                    $"A pass did not return within "
                    + $"{budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds. A "
                    + "reconciler that blocks on a four-minute cluster creation blocks that grain's "
                    + "turn — docs/plan/08 § The reconcile loop, clause 3."
                )
            );

            return null;
        }
    }

    static async Task<ObservedState?> ObserveAsync(
        IResourceReconciler reconciler,
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        try {
            return await reconciler.ObserveAsync(
                new(context.Id, context.ApiVersion, context.Desired, context.Namespace, context.Cluster),
                cancellationToken
            );
        }
        catch (OperationCanceledException) {
            return null;
        }
    }

    /// <summary>
    ///     Whether a field is the compiler's capture of a primary-constructor parameter.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Matched on the unspeakable name, because that is the only thing that distinguishes
    ///     it.</b> Roslyn emits <c>&lt;name&gt;P</c> for a captured primary-constructor parameter, and
    ///     the angle brackets make the name unspeakable in C# — so a hand-written field cannot collide
    ///     with the pattern. There is no attribute and no metadata flag; this was verified against the
    ///     emitted IL rather than assumed, and it is the same shape the compiler has used since C# 12.
    ///     If a future compiler changes the mangling, this check gets <i>stricter</i> (it starts
    ///     reporting dependencies), which is the safe direction for it to break in.
    /// </remarks>
    static bool IsPrimaryConstructorCapture(FieldInfo field) =>
        field.Name.StartsWith('<') && field.Name.EndsWith(">P", StringComparison.Ordinal);

    /// <summary>
    ///     Builds a <see cref="ReconcileContext" /> for a harness, with no cluster and no secrets.
    /// </summary>
    /// <param name="id">The resource to reconcile.</param>
    /// <param name="apiVersion">The api-version.</param>
    /// <param name="desired">The desired body.</param>
    /// <param name="log">Where the reconciler reports. A test's recorder.</param>
    /// <param name="observed">What the last observation saw, or <see langword="null" />.</param>
    /// <remarks>
    ///     A convenience for the clusterless case, which is what a conformance run without a live API
    ///     server can exercise. A provider whose reconciler needs a cluster builds its own context
    ///     against a real connection.
    /// </remarks>
    public static ReconcileContext ContextFor(
        ResourceId id,
        string apiVersion,
        JsonElement desired,
        IReconcileLog log,
        ObservedState? observed = null
    ) =>
        new(
            id,
            apiVersion,
            desired,
            observed,
            Reconcile.ReconcileDriver.NamespaceFor(id),
            null,
            new UnavailableSecretResolver(),
            log
        );
}
