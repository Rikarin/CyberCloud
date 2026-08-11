using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The reconcile scheduler's arithmetic: backoff, jitter, and the sixty-minute ceiling.
///     docs/plan/08 § The reconcile loop.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § The reconcile loop:
///         <i>
///             "<b>Backoff.</b> 10 s → 30 s → 2 min → 10 min, capped, with ±20 % jitter. After 60
///             minutes in <c>InProgress</c> the operation fails with a timeout error naming the last
///             progress entry — a resource stuck forever is worse than a resource that failed, because
///             a failure is actionable."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>Pure and static, and the jitter is a parameter rather than a
///         <see cref="Random" />.</b> A scheduler that drew its own randomness cannot be tested for
///         the property that matters — that the delay stays inside ±20 % of the step for
///         <i>every</i> sample, not for the ones a seeded run happened to draw. Passing the sample in
///         lets a test sweep the whole interval, and lets the grain draw from
///         <see cref="Random.Shared" /> at the one call site where non-determinism belongs.
///     </para>
///     <para>
///         <b>Why jitter at all.</b> A silo restart re-registers every reminder at once. Without
///         jitter, every resource on that silo retries in the same 10-second window, against clusters
///         we do not own and may be rate-limited by — the same synchronized-stampede argument
///         docs/plan/09 § Observing makes about informer re-establishment.
///     </para>
/// </remarks>
public static class ReconcileSchedule {
    /// <summary>The backoff ladder, exactly as docs/plan/08 § The reconcile loop gives it.</summary>
    /// <remarks>
    ///     ⚠ <b>Capped means the last entry repeats, not that scheduling stops.</b> An attempt past
    ///     the end of the ladder waits 10 minutes, again, until <see cref="Timeout" /> ends it. The
    ///     alternative — stopping at the last rung — would leave a resource with no reminder and no
    ///     terminal state, which is the "stuck forever" the timeout exists to prevent.
    /// </remarks>
    public static ImmutableArray<TimeSpan> Backoff { get; } = [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10)
    ];

    /// <summary>The jitter, as a fraction of the step. ±20 %.</summary>
    public const double JitterFraction = 0.20;

    /// <summary>
    ///     How long an operation may stay <see cref="ReconcileOutcomeKind.InProgress" /> before it
    ///     fails with a timeout.
    /// </summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(60);

    /// <summary>
    ///     The first <c>Retry-After</c>, in seconds. docs/plan/08 § The write path, end to end's
    ///     <c>202</c>.
    /// </summary>
    public const int InitialRetryAfterSeconds = 10;

    /// <summary>The base delay for an attempt, before jitter.</summary>
    /// <param name="attempt">
    ///     Which attempt is about to be scheduled, counting from zero. Negative is treated as zero —
    ///     a caller that got the arithmetic wrong should retry soon rather than never.
    /// </param>
    /// <returns>The ladder rung, or the last one once the ladder runs out.</returns>
    public static TimeSpan BaseDelayFor(int attempt) =>
        attempt <= 0 ? Backoff[0]
        : attempt >= Backoff.Length ? Backoff[^1]
        : Backoff[attempt];

    /// <summary>
    ///     The delay for an attempt, with jitter applied.
    /// </summary>
    /// <param name="attempt">Which attempt, counting from zero.</param>
    /// <param name="jitterSample">
    ///     A sample in <c>[0, 1)</c> — <see cref="Random.NextDouble" />'s range. 0 gives the
    ///     -20 % end, 0.5 the exact step, and values approaching 1 the +20 % end.
    /// </param>
    /// <param name="requestedByReconciler">
    ///     What the reconciler asked for in <see cref="ReconcileOutcome.RetryAfter" />, or
    ///     <see cref="TimeSpan.Zero" />. ⚠ Treated as a <i>floor</i>: the result is the larger of this
    ///     and the jittered ladder step. A reconciler that asked for one second every second would
    ///     otherwise defeat the backoff that exists to protect somebody else's API server.
    /// </param>
    /// <returns>The delay, always positive.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="jitterSample" /> is outside <c>[0, 1]</c>.</exception>
    public static TimeSpan DelayFor(int attempt, double jitterSample, TimeSpan requestedByReconciler = default) {
        ArgumentOutOfRangeException.ThrowIfLessThan(jitterSample, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterSample, 1.0);

        var basis = BaseDelayFor(attempt);

        // Map [0, 1) onto [-JitterFraction, +JitterFraction].
        var factor = 1.0 + (((jitterSample * 2.0) - 1.0) * JitterFraction);
        var jittered = TimeSpan.FromTicks((long)(basis.Ticks * factor));

        return jittered > requestedByReconciler ? jittered : requestedByReconciler;
    }

    /// <summary>
    ///     Whether an operation that started at <paramref name="startedAt" /> has run out of time.
    /// </summary>
    /// <param name="startedAt">When the operation was accepted.</param>
    /// <param name="now">The current time, from <c>IClock</c> rather than <c>DateTimeOffset.UtcNow</c>.</param>
    public static bool HasTimedOut(DateTimeOffset startedAt, DateTimeOffset now) =>
        now - startedAt >= Timeout;

    /// <summary>
    ///     The timeout error, naming the last progress entry.
    /// </summary>
    /// <param name="operationId">The operation that ran out of time.</param>
    /// <param name="resourcePath">The resource it was driving.</param>
    /// <param name="last">
    ///     The last progress entry, or <see langword="null" /> when the operation never reported one.
    /// </param>
    /// <returns>An <see cref="ErrorCode.OperationTimeout" /> whose message names where it got stuck.</returns>
    /// <remarks>
    ///     ⚠ <b>Naming the last entry is the whole requirement.</b> docs/plan/08 § The reconcile loop
    ///     asks for "a timeout error naming the last progress entry", and docs/plan/08 § Errors asks
    ///     for a message that "names the actual numbers". A timeout that said only "timed out" would
    ///     move the diagnosis to a log search — which is the support ticket the error shape exists to
    ///     avoid. The <see langword="null" /> branch says so explicitly rather than printing an empty
    ///     quotation, because "the last progress entry was ''" reads as data loss.
    /// </remarks>
    public static Error TimedOut(Guid operationId, string resourcePath, OperationProgress? last) {
        var where = last is null
            ? "It never reported progress, so the reconciler did not reach its first step."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The last progress entry was '{last.Step}: {last.Detail}' at {last.At:O}."
            );

        return new(
            ErrorCode.OperationTimeout,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Operation {operationId:D} on '{resourcePath}' stayed in progress for "
                + $"{Timeout.TotalMinutes:0} minutes and was failed. {where}"
            )
        );
    }
}
