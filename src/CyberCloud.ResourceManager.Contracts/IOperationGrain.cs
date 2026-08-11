namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     A long-running operation. docs/plan/08 § Long-running operations.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Coordinator · <b>Tier</b> Durable · <b>Key</b> <c>op/{operationId:N}</c>,
///         tenant-qualified (docs/plan/06 § Grain keys). Build it with <c>GrainKeys.Operation</c>.
///     </para>
///     <para>
///         ⚠ <b>Durable, and everything needed to re-drive is in the state.</b>
///         docs/plan/08 § Long-running operations:
///         <i>
///             "State is <b>durable</b> (ADR-003) and includes everything needed to re-drive: the
///             resource id, the desired body, the quota lease, the index claim, the step cursor. On
///             activation after a silo loss the grain re-registers its reminder and continues. This is
///             the mechanism behind docs/plan/00's non-negotiable <i>every LRO is resumable</i>, and
///             the chaos suite kills silos mid-provision specifically to test it."
///         </i>
///         <see cref="OperationSpec" /> is that state, and <see cref="OperationStatus.Activations" />
///         is how a test sees the re-drive happen.
///     </para>
///     <para>
///         ⚠ <b>Cancellation completes rather than abandoning, and that is a billing property.</b>
///         docs/plan/08 § Long-running operations: <i>"Cancellation is cooperative: it sets a flag the
///         reconciler observes, and for anything already applied it runs the delete path. A
///         'cancelled' create that leaves resources running is a billing dispute waiting to happen, so
///         cancellation <b>completes</b> rather than abandoning."</i> So
///         <see cref="CancelAsync" /> returns as soon as the flag is set, and the operation reaches
///         <see cref="OperationState.Canceled" /> only after the delete path has run. A caller that
///         treats the return of <see cref="CancelAsync" /> as "it stopped" is reading the wrong
///         signal; <see cref="OperationStatus.CancelRequested" /> and
///         <see cref="OperationStatus.State" /> are two fields for exactly that reason.
///     </para>
///     <para>
///         ⚠ <b>The alias is <c>Rm.Operation</c>, which docs/plan/08 gives verbatim</b> and which is
///         shorter than this codebase's <c>CyberCloud.{Assembly}.{Interface}</c> convention. The
///         document's spelling wins because an <c>[Alias]</c> is a wire identifier: changing it after
///         anything has been persisted or has spoken to a peer is a break, and matching the document
///         means nobody has to decide later which of the two was authoritative.
///     </para>
/// </remarks>
[Alias("Rm.Operation")]
public interface IOperationGrain : IGrainWithStringKey {
    /// <summary>
    ///     Records what this operation is for and begins driving it. Step 9 of
    ///     docs/plan/08 § The write path, end to end.
    /// </summary>
    /// <param name="spec">
    ///     Everything a re-drive needs. ⚠ The quota lease ids and
    ///     <see cref="OperationSpec.IndexClaimed" /> must be filled in by the caller, because they
    ///     record work steps 6 and 7 already did — an operation that could not tell whether it holds a
    ///     lease would either leak quota or release somebody else's.
    /// </param>
    /// <returns>
    ///     Success, or <see cref="ErrorCode.Conflict" /> when this operation has already been started
    ///     with a different spec. ⚠ Starting the <i>same</i> spec twice succeeds and changes nothing,
    ///     which is what makes a retried <c>PUT</c> a no-op all the way down.
    /// </returns>
    Task<Result> StartAsync(OperationSpec spec);

    /// <summary>The operation's current state and its whole progress array.</summary>
    /// <returns>
    ///     The status, or <see cref="ErrorCode.ResourceNotFound" /> when nothing was ever started
    ///     under this id.
    /// </returns>
    Task<Result<OperationStatus>> GetAsync();

    /// <summary>Appends one progress entry.</summary>
    /// <param name="progress">
    ///     What happened. Called by the reconcile driver after each pass, with whatever the
    ///     reconciler put through <see cref="IReconcileLog" />.
    /// </param>
    /// <remarks>
    ///     ⚠ The array is capped. An unbounded progress array on a durable grain is a row that grows
    ///     until the write fails, and a four-hour provisioning run reporting per second would produce
    ///     14 400 entries nobody reads. The oldest entries are dropped and the drop is itself recorded,
    ///     so a reader can tell a truncated history from a short one.
    /// </remarks>
    Task<Result> ReportAsync(OperationProgress progress);

    /// <summary>
    ///     Asks for cancellation. Returns as soon as the flag is set — <b>not</b> when the operation
    ///     has stopped.
    /// </summary>
    /// <param name="reason">
    ///     Why, for the audit log and for the status. Named rather than optional because "cancelled"
    ///     with no reason is the entry that generates the support call.
    /// </param>
    /// <returns>
    ///     Success, or <see cref="ErrorCode.Conflict" /> when the operation has already reached a
    ///     terminal state — a succeeded operation cannot be cancelled, and pretending otherwise would
    ///     leave a caller expecting a teardown that will never run.
    /// </returns>
    Task<Result> CancelAsync(string reason);

    /// <summary>
    ///     Runs one reconcile pass and schedules the next. The reminder's body, exposed so a test can
    ///     drive it.
    /// </summary>
    /// <returns>
    ///     The status after the pass. ⚠ A pass that converged, failed or completed a cancellation
    ///     leaves <see cref="OperationStatus.IsTerminal" /> true and registers no further reminder.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>This is not in docs/plan/08's interface and has to exist.</b> The document gives four
    ///     methods and says a reminder drives convergence; a reminder is the <i>trigger</i> and the
    ///     work it triggers still needs a name. Making that name public rather than private is what
    ///     lets the suite test resumability, backoff and cancellation without waiting on Orleans'
    ///     one-minute minimum reminder period — the alternative is a test nobody runs.
    /// </remarks>
    Task<Result<OperationStatus>> DriveAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    /// <remarks>
    ///     ⚠ Used by the suite to prove resumability: deactivate mid-operation, call anything, and the
    ///     grain re-drives from durable state with <see cref="OperationStatus.Activations" />
    ///     incremented.
    /// </remarks>
    Task DeactivateAsync();
}
