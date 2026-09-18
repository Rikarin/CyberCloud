using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Compute;

/// <summary>
///     Converges one managed disk onto the blank CDI <c>DataVolume</c> that provisions it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Converged</c> means the claim exists and is waiting for its first consumer</b> —
///         <see cref="Cdi.IsProvisioned" />, which is a phase short of an image's. On the bundle's
///         node-local class a blank disk nothing has attached sits in <c>WaitForFirstConsumer</c> and
///         binds to the node its first machine is scheduled to, which is the correct resting state of
///         an unattached disk rather than a delay. A disk that reports <c>Pending</c> — no class, no
///         capacity — is <c>InProgress</c> with CDI's reason.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop are <see cref="DataVolumeReconciliation" />'s
///         to keep; the only field here is the primary constructor's <see cref="IClock" />.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class DiskReconciler(IClock clock) : IResourceReconciler {
    static readonly DataVolumeReconciliation.Shape Shape = new(
        "disk",
        Disks.DataVolumeJson,
        Disks.DataVolumeRef,
        Disks.Matches,
        Cdi.IsProvisioned
    );

    /// <inheritdoc />
    public ResourceTypeName Type => Disks.Type;

    /// <inheritdoc />
    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        DataVolumeReconciliation.ReconcileAsync(Shape, context, cancellationToken);

    /// <inheritdoc />
    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        DataVolumeReconciliation.DeleteAsync(Shape, context, cancellationToken);

    /// <inheritdoc />
    public Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) =>
        DataVolumeReconciliation.ObserveAsync(Shape, clock, context, cancellationToken);
}
