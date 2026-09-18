using CyberCloud.Core.Time;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication;

/// <summary>
///     Converges one <c>services/channels</c> resource onto one <see cref="ChannelConfiguration" />
///     on its service's grain.
/// </summary>
/// <remarks>
///     <para>
///         The same four clauses <see cref="CommunicationServiceReconciler" /> lists, and one rule
///         of its own: <b>a channel kind has one owner.</b> The grain holds one configuration per
///         <see cref="ChannelKind" /> and records which resource wrote it
///         (<see cref="ChannelConfiguration.OwnerResourceId" />). A pass that finds the kind held by
///         another resource fails with <see cref="ErrorCode.Conflict" /> and writes nothing — the
///         alternative is two reconcilers overwriting each other on every drift scan, which
///         converges perfectly and sends on whichever configuration ran last.
///     </para>
///     <para>
///         ⚠ <b>A service that is not there yet is <c>InProgress</c>, not <c>Failed</c>.</b> The
///         resource manager guarantees the parent <i>resource</i> exists before a child is created;
///         it does not wait for the parent's operation to converge, so the first pass of a channel
///         PUT seconds after its service's PUT can find no grain. Ten seconds later it will.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
/// <param name="plane">The module.</param>
public sealed class CommunicationChannelReconciler(IClock clock, ICommunicationControlPlane plane) :
    IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationChannels.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var wanted = CommunicationChannels.ToConfiguration(context.Id, context.Desired);
        if (wanted.TryGetError(out var invalid)) {
            return ReconcileOutcome.FromFailure(invalid);
        }

        var configuration = wanted.GetValueOrThrow();
        var serviceId = CommunicationServices.ServiceIdOf(context.Id);
        var tenantId = context.Id.TenantId;

        var held = await plane.GetChannelAsync(tenantId, serviceId, configuration.Channel, cancellationToken);

        if (held.TryGetValue(out var existing)) {
            if (existing.OwnerResourceId != Guid.Empty && existing.OwnerResourceId != context.Id.Id) {
                return ReconcileOutcome.Failed(
                    ErrorCode.Conflict,
                    $"The {ChannelKinds.Spell(configuration.Channel)} channel of service "
                    + $"'{context.Id.ParentNames}' is already configured by channel resource "
                    + $"{existing.OwnerResourceId:D}. One service holds one configuration per kind; "
                    + "change that resource, or delete it first."
                );
            }

            if (existing == configuration) {
                context.Log.Report(
                    "ready",
                    $"the {ChannelKinds.Spell(configuration.Channel)} channel already carries the desired configuration",
                    100
                );
                return ReconcileOutcome.Converged;
            }
        } else if (held.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(held.Error);
        }

        context.Log.Report("configuring", $"configuring the {ChannelKinds.Spell(configuration.Channel)} channel", 40);

        var configured = await plane.ConfigureChannelAsync(tenantId, serviceId, configuration, cancellationToken);
        if (configured.TryGetError(out var configureError)) {
            return configureError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"the service '{context.Id.ParentNames}' is not provisioned yet; the channel waits for it",
                    TimeSpan.FromSeconds(10)
                )
                : ReconcileOutcome.FromFailure(configureError);
        }

        // ── Clause 4. ───────────────────────────────────────────────────────────────────────────
        var read = await plane.GetChannelAsync(tenantId, serviceId, configuration.Channel, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    "the channel was configured and does not read back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (read.GetValueOrThrow() != configuration) {
            return ReconcileOutcome.InProgress(
                "the channel reads back and does not yet carry the desired configuration",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report(
            "ready",
            $"the {ChannelKinds.Spell(configuration.Channel)} channel reads back as desired",
            100
        );
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var kind = CommunicationChannels.KindOf(context.Desired);
        if (kind == ChannelKind.Unknown) {
            // A body the schema would have refused never configured anything, so there is nothing
            // to remove.
            return ReconcileOutcome.Converged;
        }

        var serviceId = CommunicationServices.ServiceIdOf(context.Id);
        var tenantId = context.Id.TenantId;

        var held = await plane.GetChannelAsync(tenantId, serviceId, kind, cancellationToken);
        if (held.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.Converged
                : ReconcileOutcome.FromFailure(readError);
        }

        // ⚠ Not ours, not touched. The configuration another resource owns survives this one's
        // delete — the same rule ICommunicationServiceGrain.UnregisterTemplateAsync applies to a
        // template name.
        if (held.GetValueOrThrow().OwnerResourceId != Guid.Empty
            && held.GetValueOrThrow().OwnerResourceId != context.Id.Id) {
            context.Log.Report(
                "left-alone",
                $"the {ChannelKinds.Spell(kind)} channel belongs to resource {held.GetValueOrThrow().OwnerResourceId:D} and was left as it is",
                100
            );

            return ReconcileOutcome.Converged;
        }

        context.Log.Report("removing", $"removing the {ChannelKinds.Spell(kind)} channel");

        var removed = await plane.RemoveChannelAsync(tenantId, serviceId, kind, cancellationToken);
        if (removed.TryGetError(out var removeError) && removeError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(removeError);
        }

        var read = await plane.GetChannelAsync(tenantId, serviceId, kind, cancellationToken);
        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress(
                $"the {ChannelKinds.Spell(kind)} channel still reads back",
                TimeSpan.FromSeconds(5)
            );
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("removed", $"the {ChannelKinds.Spell(kind)} channel is gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        var kind = CommunicationChannels.KindOf(context.Desired);

        var read = kind == ChannelKind.Unknown
            ? Result<ChannelConfiguration>.Failure(ErrorCode.InvalidRequestBody, "the body names no channel kind")
            : await plane.GetChannelAsync(
                context.Id.TenantId,
                CommunicationServices.ServiceIdOf(context.Id),
                kind,
                cancellationToken
            );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the channel is not configured" };
        }

        var held = read.GetValueOrThrow();
        var ours = held.OwnerResourceId == Guid.Empty || held.OwnerResourceId == context.Id.Id;
        var matches = ours && CommunicationChannels.Matches(held, context.Id, context.Desired);

        return new() {
            Exists = ours,
            Json = new JsonObject {
                ["kind"] = ChannelKinds.Spell(held.Channel),
                ["provider"] = held.Provider,
                ["enabled"] = held.Enabled,
                ["account"] = held.Credentials.Mode == CredentialMode.TenantAccount ? "tenant" : "platform",
                ["maxMessagesPerDay"] = held.Limits.MaxMessagesPerWindow,
                ["maxSpendPerDay"] = held.Limits.MaxSpendPerWindow,
                ["currency"] = held.Limits.Currency,
                ["owner"] = held.OwnerResourceId.ToString("D", System.Globalization.CultureInfo.InvariantCulture)
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Summary = !ours ? "the channel kind is held by another resource"
                : matches ? "the channel carries the desired configuration"
                : "the channel has drifted"
        };
    }
}
