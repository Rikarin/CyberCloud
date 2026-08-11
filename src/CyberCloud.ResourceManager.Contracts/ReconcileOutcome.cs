using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     What one reconcile pass concluded. Exactly three cases, per docs/plan/08 § The reconcile loop.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § The reconcile loop:
///         <i>
///             "<c>ReconcileOutcome</c> is one of <c>Converged</c>, <c>InProgress(reason,
///             retryAfter)</c>, <c>Failed(error, retryable)</c>. Nothing else. A reconciler that wants
///             to say something else wants to log, and <c>IReconcileLog</c> is how."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>The set is closed by construction, not by convention.</b> The constructor is private
///         and the only three ways to make one are <see cref="Converged" />,
///         <see cref="InProgress" /> and <see cref="Failed(Error, bool)" />. A provider cannot add a fourth case,
///         cannot return a partially-filled outcome, and cannot return <see langword="null" /> from a
///         method typed as this. That matters more here than in most closed sets: the scheduler
///         switches on <see cref="Kind" /> to decide between "stop", "back off and retry" and "fail
///         the operation", and a fourth case would land in whichever branch the compiler happened to
///         make the default.
///     </para>
///     <para>
///         ⚠ <b>There is no <c>Unknown</c> member and <c>default(ReconcileOutcome)</c> is
///         <see langword="null" />.</b> This is a class rather than a record struct precisely so that
///         "never assigned" is a null reference the nullable analysis catches at compile time, rather
///         than a fourth enum value every switch has to handle. That is the opposite of the choice
///         <see cref="Result" /> makes, and for the opposite reason: a <see cref="Result" /> is a
///         value type whose default must be safe, while an outcome is always returned from a method
///         and never sits in an unassigned field.
///     </para>
///     <para>
///         <b>The meaning of <see cref="Converged" /> is a contract clause, not a convenience.</b>
///         Clause 4 of docs/plan/08 § The reconcile loop — <i>"Observes, never assumes.
///         <c>Converged</c> means it <b>read back</b> the desired shape, not that the apply returned
///         200."</i> The conformance suite enforces it by making the observed world disagree with the
///         desired one after a reconciler has reported <see cref="Converged" />, and asserting the
///         next pass no longer does.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ReconcileOutcome")]
public sealed record ReconcileOutcome {
    /// <summary>The one <see cref="ReconcileOutcomeKind.Converged" /> instance.</summary>
    /// <remarks>
    ///     Cached because a converged pass carries no information beyond its own kind, and because
    ///     the reminder fires on already-converged grains constantly — docs/plan/08 § The reconcile
    ///     loop, clause 1.
    /// </remarks>
    public static ReconcileOutcome Converged { get; } = new(ReconcileOutcomeKind.Converged);

    /// <summary>Which of the three cases this is.</summary>
    [Id(0)]
    public ReconcileOutcomeKind Kind { get; private init; }

    /// <summary>
    ///     Why the pass is not finished, in words a tenant reads in <c>cyc --wait</c>. Empty except
    ///     for <see cref="ReconcileOutcomeKind.InProgress" />.
    /// </summary>
    [Id(1)]
    public string Reason { get; private init; } = string.Empty;

    /// <summary>
    ///     How long the reconciler asks to wait. <see cref="TimeSpan.Zero" /> means "you choose", and
    ///     the scheduler's backoff applies.
    /// </summary>
    /// <remarks>
    ///     ⚠ A reconciler's request is a <i>floor</i>, not an override: the scheduler takes the larger
    ///     of this and its own backoff step. A reconciler that asked for one second every second
    ///     would otherwise defeat the backoff that exists to protect a cluster we do not own.
    /// </remarks>
    [Id(2)]
    public TimeSpan RetryAfter { get; private init; }

    /// <summary>
    ///     What went wrong. <see langword="null" /> except for <see cref="ReconcileOutcomeKind.Failed" />.
    /// </summary>
    [Id(3)]
    public Error? Error { get; private init; }

    /// <summary>
    ///     Whether retrying could plausibly help. A non-retryable failure ends the operation now
    ///     rather than after sixty minutes of backoff.
    /// </summary>
    [Id(4)]
    public bool Retryable { get; private init; }

    ReconcileOutcome(ReconcileOutcomeKind kind) {
        Kind = kind;
    }

    /// <summary>
    ///     Work is under way and the scheduler should come back.
    /// </summary>
    /// <param name="reason">
    ///     What is happening, phrased for a person watching a four-minute cluster creation — "waiting
    ///     for 2 of 3 replicas to become ready". docs/plan/08 § The reconcile loop calls this what
    ///     "turns a four-minute cluster creation from a spinner into a story".
    /// </param>
    /// <param name="retryAfter">
    ///     The earliest the reconciler wants to be asked again, or <see cref="TimeSpan.Zero" /> to
    ///     leave it to the scheduler. Negative values are refused rather than clamped — a negative
    ///     delay is a calculation that went wrong, and silently treating it as zero hides that.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retryAfter" /> is negative.</exception>
    public static ReconcileOutcome InProgress(string reason, TimeSpan retryAfter = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryAfter, TimeSpan.Zero);

        return new(ReconcileOutcomeKind.InProgress) { Reason = reason, RetryAfter = retryAfter };
    }

    /// <summary>It did not work.</summary>
    /// <param name="error">
    ///     The failure, in the one error shape (docs/plan/08 § Errors). Its <c>message</c> names the
    ///     actual values and its <c>target</c> points at the offending field where there is one.
    /// </param>
    /// <param name="retryable">
    ///     Whether another pass could succeed without the tenant changing anything. An API-server
    ///     timeout is retryable; a rejected manifest is not.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="error" /> is null.</exception>
    public static ReconcileOutcome Failed(Error error, bool retryable = false) {
        ArgumentNullException.ThrowIfNull(error);
        return new(ReconcileOutcomeKind.Failed) { Error = error, Retryable = retryable };
    }

    /// <summary>The <see cref="Failed(Error, bool)" /> overload that builds the error from its parts.</summary>
    /// <param name="code">A registered code.</param>
    /// <param name="message">A message that names the actual values.</param>
    /// <param name="retryable">Whether another pass could succeed unaided.</param>
    public static ReconcileOutcome Failed(ErrorCode code, string message, bool retryable = false) =>
        Failed(new Error(code, message), retryable);

    /// <summary>Whether this is <see cref="ReconcileOutcomeKind.Converged" />.</summary>
    public bool IsConverged => Kind == ReconcileOutcomeKind.Converged;

    /// <summary>Whether this is terminal — converged or failed and not retryable.</summary>
    public bool IsTerminal => IsConverged || (Kind == ReconcileOutcomeKind.Failed && !Retryable);

    /// <inheritdoc />
    public override string ToString() =>
        Kind switch {
            ReconcileOutcomeKind.Converged => "Converged",
            ReconcileOutcomeKind.InProgress => string.Create(
                CultureInfo.InvariantCulture,
                $"InProgress({Reason}, retryAfter {RetryAfter})"
            ),
            _ => string.Create(CultureInfo.InvariantCulture, $"Failed({Error}, retryable {Retryable})")
        };
}
