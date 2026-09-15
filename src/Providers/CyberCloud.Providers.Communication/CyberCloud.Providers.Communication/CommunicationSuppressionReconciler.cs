using CyberCloud.Core.Time;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication;

/// <summary>
///     Converges one <c>services/suppressions</c> resource onto one entry on its service's
///     <c>ISuppressionListGrain</c>.
/// </summary>
/// <remarks>
///     <para>
///         The same four clauses <see cref="CommunicationServiceReconciler" /> lists, and the rule
///         <see cref="CommunicationSuppressions" />'s remarks state: <b>a resource owns a manual block
///         and never downgrades a stronger entry.</b> The pass reads before it writes. An address the
///         list already holds for a complaint, an opt-out or a hard bounce is left alone and reported
///         converged — the body asked for the address to be suppressed, and it is, for a reason the
///         tenant may not overwrite. Only an address that is clear, or held as a manual block with a
///         different note, is written.
///     </para>
///     <para>
///         ⚠ <b>The delete releases a manual block and nothing else, and succeeds either way.</b> A
///         resource whose address has since complained is deleted cleanly and the complaint stands;
///         a delete that tried to release it would be refused by the grain with
///         <see cref="ErrorCode.PolicyViolation" /> and the resource would sit in <c>Deleting</c>
///         forever — visible, undeletable, and protecting nothing the grain was not already
///         protecting. Converged once the entry is gone or is no longer a manual block, read back.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
/// <param name="plane">The module.</param>
public sealed class CommunicationSuppressionReconciler(IClock clock, ICommunicationControlPlane plane) : IResourceReconciler {
    /// <summary>What the release records when a resource is deleted.</summary>
    public const string ReleaseReason = "The suppression resource that placed this manual block was deleted.";

    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationSuppressions.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var channel = CommunicationSuppressions.ChannelOf(context.Desired);
        if (channel == ChannelKind.Unknown) {
            return ReconcileOutcome.Failed(
                ErrorCode.InvalidRequestBody,
                "The suppression's channel is not one of " + string.Join(", ", ChannelKinds.AllowedValues) + "."
            );
        }

        var tenantId = context.Id.TenantId;
        var serviceId = CommunicationServices.ServiceIdOf(context.Id);
        var destination = CommunicationSuppressions.DestinationOf(context.Desired);
        var note = CommunicationSuppressions.NoteOf(context.Desired);

        var held = await plane.CheckSuppressionAsync(tenantId, serviceId, channel, destination, cancellationToken);
        if (held.TryGetError(out var checkError)) {
            return ReconcileOutcome.FromFailure(checkError);
        }

        if (held.GetValueOrThrow() is { IsSuppressed: true, Entry: { } existing }) {
            if (existing.Reason != SuppressionReason.ManualBlock) {
                // ⚠ THE LINE THIS RECONCILER EXISTS FOR. Writing here would turn the recipient's
                // statement into the tenant's, and the tenant may release the tenant's.
                context.Log.Report(
                    "ready",
                    $"{existing.Destination} is already suppressed on {ChannelKinds.Spell(channel)} for a "
                    + $"{CommunicationServices.SpellReason(existing.Reason)}, which this resource does not override",
                    100
                );

                return ReconcileOutcome.Converged;
            }

            if (CommunicationSuppressions.Matches(existing, context.Desired)) {
                context.Log.Report("ready", $"{existing.Destination} is already blocked on {ChannelKinds.Spell(channel)} as desired", 100);
                return ReconcileOutcome.Converged;
            }
        }

        context.Log.Report("suppressing", $"blocking {destination} on {ChannelKinds.Spell(channel)}", 40);

        var suppressed = await plane.SuppressAsync(tenantId, serviceId, channel, destination, SuppressionReason.ManualBlock, note, cancellationToken);
        if (suppressed.TryGetError(out var suppressError)) {
            return ReconcileOutcome.FromFailure(suppressError);
        }

        // ── Clause 4. ───────────────────────────────────────────────────────────────────────────
        var read = await plane.CheckSuppressionAsync(tenantId, serviceId, channel, destination, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return ReconcileOutcome.FromFailure(readError);
        }

        if (read.GetValueOrThrow() is not { IsSuppressed: true, Entry: { } entry }
            || !CommunicationSuppressions.Matches(entry, context.Desired)) {
            return ReconcileOutcome.InProgress("the entry was written and does not read back as desired yet", TimeSpan.FromSeconds(5));
        }

        context.Log.Report("ready", $"{entry.Destination} reads back as blocked on {ChannelKinds.Spell(channel)}", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var channel = CommunicationSuppressions.ChannelOf(context.Desired);
        if (channel == ChannelKind.Unknown) {
            return ReconcileOutcome.Converged;
        }

        var tenantId = context.Id.TenantId;
        var serviceId = CommunicationServices.ServiceIdOf(context.Id);
        var destination = CommunicationSuppressions.DestinationOf(context.Desired);

        var held = await plane.CheckSuppressionAsync(tenantId, serviceId, channel, destination, cancellationToken);
        if (held.TryGetError(out var checkError)) {
            return ReconcileOutcome.FromFailure(checkError);
        }

        if (held.GetValueOrThrow() is not { IsSuppressed: true, Entry: { } entry }) {
            return ReconcileOutcome.Converged;
        }

        if (entry.Reason != SuppressionReason.ManualBlock) {
            context.Log.Report(
                "left-alone",
                $"{entry.Destination} stays suppressed on {ChannelKinds.Spell(channel)}: the entry is a "
                + $"{CommunicationServices.SpellReason(entry.Reason)}, which only the recipient can lift",
                100
            );

            return ReconcileOutcome.Converged;
        }

        context.Log.Report("releasing", $"releasing the manual block on {entry.Destination}");

        var released = await plane.ReleaseSuppressionAsync(tenantId, serviceId, channel, destination, ReleaseReason, cancellationToken);
        if (released.TryGetError(out var releaseError) && releaseError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(releaseError);
        }

        var read = await plane.CheckSuppressionAsync(tenantId, serviceId, channel, destination, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return ReconcileOutcome.FromFailure(readError);
        }

        if (read.GetValueOrThrow() is { IsSuppressed: true, Entry.Reason: SuppressionReason.ManualBlock }) {
            return ReconcileOutcome.InProgress($"{entry.Destination} still reads back as blocked", TimeSpan.FromSeconds(5));
        }

        context.Log.Report("released", $"the manual block on {entry.Destination} is gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        var channel = CommunicationSuppressions.ChannelOf(context.Desired);

        var read = channel == ChannelKind.Unknown
            ? Result<SuppressionCheck>.Success(SuppressionCheck.Clear)
            : await plane.CheckSuppressionAsync(
                context.Id.TenantId,
                CommunicationServices.ServiceIdOf(context.Id),
                channel,
                CommunicationSuppressions.DestinationOf(context.Desired),
                cancellationToken
            );

        if (!read.TryGetValue(out var check) || check is not { IsSuppressed: true, Entry: { } entry }) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the address is not suppressed" };
        }

        return new() {
            Exists = true,
            Json = new JsonObject {
                ["channel"] = ChannelKinds.Spell(entry.Channel),
                ["destination"] = entry.Destination,
                ["reason"] = CommunicationServices.SpellReason(entry.Reason),
                ["suppressedAt"] = entry.SuppressedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["note"] = entry.Note
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Summary = CommunicationSuppressions.Matches(entry, context.Desired)
                ? "the address is suppressed as desired"
                : "the entry has drifted from the body"
        };
    }
}
