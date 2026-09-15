using CyberCloud.Core.Time;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication;

/// <summary>
///     Converges one <c>services</c> resource onto its <c>ICommunicationServiceGrain</c>.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, on a reconciler with no cluster:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <c>EnsureServiceAsync</c> creates or reports, and sets the locale
///             only when it differs. A second pass writes nothing.
///         </item>
///         <item>
///             <b>No hidden state.</b> Two constructor dependencies — the clock that stamps an
///             observation and the seam every grain call goes through — and no field that remembers
///             a pass.
///         </item>
///         <item>
///             <b>Bounded.</b> Two grain calls, both on the caller's token.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>DescribeServiceAsync</c> after the ensure, compared through
///             <see cref="CommunicationServices.Matches" />, and never the ensure's own return value
///             — which is what the write <i>said</i> it did.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The delete retires and does not erase</b> — <c>ICommunicationServiceGrain.RetireAsync</c>.
///         Channels and template names go, so nothing can send through the service again; the
///         suppression list and the templates' version histories stay, because both are records of
///         other people's decisions and a tenant must not be able to clear them by deleting and
///         recreating a service. Converged once <c>DescribeServiceAsync</c> answers
///         <see cref="ErrorCode.ResourceNotFound" />, read back.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
/// <param name="plane">The module, reached the way identity reaches it.</param>
public sealed class CommunicationServiceReconciler(IClock clock, ICommunicationControlPlane plane) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationServices.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var serviceId = CommunicationServices.ServiceIdOf(context.Id);
        var locale = CommunicationServices.DefaultLocaleOf(context.Desired);

        context.Log.Report("ensuring", $"ensuring the communication service '{context.Id.Name}' exists", 40);

        var ensured = await plane.EnsureServiceAsync(context.Id.TenantId, serviceId, context.Id.Name, locale, cancellationToken);
        if (ensured.TryGetError(out var ensureError)) {
            return ReconcileOutcome.FromFailure(ensureError);
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var read = await plane.DescribeServiceAsync(context.Id.TenantId, serviceId, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress("the service was ensured and does not read back yet", TimeSpan.FromSeconds(5))
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!CommunicationServices.Matches(read.GetValueOrThrow(), context.Id, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"the service '{context.Id.Name}' reads back and does not yet carry the desired locale",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the communication service '{context.Id.Name}' reads back as desired", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var serviceId = CommunicationServices.ServiceIdOf(context.Id);

        context.Log.Report("retiring", $"retiring the communication service '{context.Id.Name}'");

        var retired = await plane.RetireServiceAsync(context.Id.TenantId, serviceId, cancellationToken);
        if (retired.TryGetError(out var retireError) && retireError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(retireError);
        }

        // ⚠ Converged once the grain answers not-found, read back — not once the retire returned.
        var read = await plane.DescribeServiceAsync(context.Id.TenantId, serviceId, cancellationToken);
        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"the service '{context.Id.Name}' still describes itself", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("retired", $"the communication service '{context.Id.Name}' is retired", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        var read = await plane.DescribeServiceAsync(context.Id.TenantId, CommunicationServices.ServiceIdOf(context.Id), cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the service is not provisioned" };
        }

        var service = read.GetValueOrThrow();
        var matches = CommunicationServices.Matches(service, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = new JsonObject {
                ["name"] = service.Name,
                ["defaultLocale"] = service.DefaultLocale,
                ["channels"] = service.Channels.IsDefault ? 0 : service.Channels.Length,
                ["createdAt"] = service.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Summary = matches ? "the service carries the desired locale" : "the service has drifted"
        };
    }
}
