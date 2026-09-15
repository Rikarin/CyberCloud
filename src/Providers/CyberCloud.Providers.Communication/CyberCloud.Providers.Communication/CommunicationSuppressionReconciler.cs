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
///         tenant may not overwrite. Only an address that is clear, or held as a manual block this
///         resource owns or nobody owns, is written — and written with this resource's id as the
///         owner, so an entry from before <see cref="SuppressionEntry.OwnerResourceId" /> existed is
///         adopted by the first pass that touches it.
///     </para>
///     <para>
///         ⚠ <b>A manual block held by another resource fails the pass with
///         <see cref="ErrorCode.Conflict" /> and writes nothing.</b> The alternative was measured:
///         two resources over one entry both read it as theirs, and the delete of either released
///         it while the other still said the address was blocked — with no drift scan to notice,
///         because this family has no cluster for the per-cluster scan to walk. The same rule
///         <see cref="CommunicationChannelReconciler" /> applies to a channel kind, for the same
///         reason, and the refusal names the resource that holds the address.
///     </para>
///     <para>
///         ⚠ <b>The delete releases a manual block this resource owns, and nothing else, and
///         succeeds either way.</b> A resource whose address has since complained is deleted cleanly
///         and the complaint stands; a delete that tried to release it would be refused by the grain
///         with <see cref="ErrorCode.PolicyViolation" /> and the resource would sit in
///         <c>Deleting</c> forever — visible, undeletable, and protecting nothing the grain was not
///         already protecting. A block another resource owns is left exactly as it is, the way a
///         channel kind another resource owns survives a channel's delete. Converged once the entry
///         is gone or is no longer this resource's manual block, read back.
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

            // ⚠ THE SECOND LINE, FOUND BY REVIEW. Without it a second resource for the address read
            // the first's block as its own, and the first's delete then released what the second
            // still declared.
            if (!CommunicationSuppressions.Owns(existing, context.Id)) {
                return ReconcileOutcome.Failed(
                    ErrorCode.Conflict,
                    $"{existing.Destination} is already blocked on {ChannelKinds.Spell(channel)} of service "
                    + $"'{context.Id.ParentNames}' by suppression resource {existing.OwnerResourceId:D}. One "
                    + "address holds one manual block; change that resource, or delete it first."
                );
            }

            if (CommunicationSuppressions.Matches(existing, context.Id, context.Desired)) {
                context.Log.Report("ready", $"{existing.Destination} is already blocked on {ChannelKinds.Spell(channel)} as desired", 100);
                return ReconcileOutcome.Converged;
            }
        }

        context.Log.Report("suppressing", $"blocking {destination} on {ChannelKinds.Spell(channel)}", 40);

        var suppressed = await plane.SuppressAsync(
            tenantId,
            serviceId,
            channel,
            destination,
            SuppressionReason.ManualBlock,
            note,
            context.Id.Id,
            cancellationToken
        );

        if (suppressed.TryGetError(out var suppressError)) {
            return ReconcileOutcome.FromFailure(suppressError);
        }

        // ── Clause 4. ───────────────────────────────────────────────────────────────────────────
        var read = await plane.CheckSuppressionAsync(tenantId, serviceId, channel, destination, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return ReconcileOutcome.FromFailure(readError);
        }

        if (read.GetValueOrThrow() is not { IsSuppressed: true, Entry: { } entry }
            || !CommunicationSuppressions.Matches(entry, context.Id, context.Desired)) {
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

        // ⚠ Not ours, not touched. The block another resource owns survives this one's delete — the
        // same rule CommunicationChannelReconciler applies to a channel kind.
        if (!CommunicationSuppressions.Owns(entry, context.Id)) {
            context.Log.Report(
                "left-alone",
                $"{entry.Destination} stays blocked on {ChannelKinds.Spell(channel)}: the block belongs to "
                + $"suppression resource {entry.OwnerResourceId:D} and was left as it is",
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

        if (read.GetValueOrThrow() is { IsSuppressed: true, Entry: { Reason: SuppressionReason.ManualBlock } still }
            && CommunicationSuppressions.Owns(still, context.Id)) {
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

        var ours = CommunicationSuppressions.Owns(entry, context.Id);

        return new() {
            Exists = ours,
            Json = new JsonObject {
                ["channel"] = ChannelKinds.Spell(entry.Channel),
                ["destination"] = entry.Destination,
                ["reason"] = CommunicationServices.SpellReason(entry.Reason),
                ["suppressedAt"] = entry.SuppressedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["note"] = entry.Note,
                ["owner"] = entry.OwnerResourceId.ToString("D", System.Globalization.CultureInfo.InvariantCulture)
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Summary = !ours ? "the address is blocked by another resource"
                : CommunicationSuppressions.Matches(entry, context.Id, context.Desired) ? "the address is suppressed as desired"
                : "the entry has drifted from the body"
        };
    }
}
