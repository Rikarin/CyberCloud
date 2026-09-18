using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Compute;

/// <summary>
///     Converges one image onto the CDI <c>DataVolume</c> that imports it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Converged</c> means the bytes are there</b> — <see cref="Cdi.IsPopulated" />, which is
///         <c>Succeeded</c> alone. An image whose import is running is <c>InProgress</c> with CDI's own
///         progress sentence, and one whose import failed is <c>Failed</c> for good: its source and
///         size are immutable, so a retry would ask the same registry for the same bytes forever.
///     </para>
///     <para>
///         ⚠ <b>It refuses a <c>url</c> body with no address before it renders</b>, because that is
///         the one relation between two properties the schema cannot state — <c>kind: url</c>
///         requires <c>url</c> — and a <c>DataVolume</c> with an empty HTTP source is admitted by a
///         derived stub and refused by CDI with a message that names neither property.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop are <see cref="DataVolumeReconciliation" />'s
///         to keep; the only field here is the primary constructor's <see cref="IClock" />.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ImageReconciler(IClock clock) : IResourceReconciler {
    static readonly DataVolumeReconciliation.Shape Shape = new(
        "image",
        Images.DataVolumeJson,
        Images.DataVolumeRef,
        Images.Matches,
        Cdi.IsPopulated
    );

    /// <inheritdoc />
    public ResourceTypeName Type => Images.Type;

    /// <inheritdoc />
    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (Images.ImportUrl(context.Desired).Length == 0) {
            return Task.FromResult(
                ReconcileOutcome.Failed(
                    ErrorCode.InvalidRequestBody,
                    $"'{context.Id.Path}' says source.kind is url and gives no source.url, so there is "
                    + "nothing to import. Name a docker:// container disk or an http(s):// image, or use "
                    + "source.kind catalogue."
                )
            );
        }

        return DataVolumeReconciliation.ReconcileAsync(Shape, context, cancellationToken);
    }

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
