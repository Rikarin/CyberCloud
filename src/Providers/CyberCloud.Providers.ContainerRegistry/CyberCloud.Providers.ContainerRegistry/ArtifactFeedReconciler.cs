using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Providers.ContainerRegistry;

/// <summary>
///     Converges one feed onto its catalogue grain, and empties its storage prefix on teardown.
///     docs/plan/13 § Artifact feeds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The first reconciler in the tree that applies nothing to a cluster, and the four
///             clauses of docs/plan/08 § The reconcile loop still each have a line.
///         </b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <see cref="IFeedGrain.OpenAsync" /> for the kind the feed is already
///             open as answers the same descriptor it did the first time and writes nothing.
///         </item>
///         <item>
///             <b>No hidden state.</b> The grain factory and the clock are dependencies, not memories;
///             every fact about the feed is read back from the grain on every pass.
///         </item>
///         <item>
///             <b>Bounded.</b> One grain call and one read-back on a create; on a delete, one grain
///             call, one listing, one delete per object and one listing again — and the objects are
///             a tenant's artefacts, whose count the operation's own timeout bounds.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows
///             <see cref="IFeedGrain.DescribeAsync" /> and <see cref="IObjectStore.ListAsync" />,
///             never the open's or the delete's own result.
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             The teardown removes bytes the reconciler never wrote, and the prefix is the whole
///             contract.
///         </b> The feeds host stores every artefact under
///         <see cref="ArtifactFeeds.StoragePrefix" /> and this pass lists that prefix and deletes
///         what it finds. It converges only when the listing reads back empty, because a feed whose
///         resource is gone and whose artefacts are still billed against the platform's bucket is
///         the quota and the store disagreeing — the shape <c>charts/managed/harbor</c> records as
///         <c>purge-leaves-the-volumes-behind</c>.
///     </para>
///     <para>
///         ⚠ <b>Reached through <c>ForTenant</c>, like every grain reference in the tree.</b> This is
///         the first provider implementation assembly to take an Orleans reference, and its
///         <c>.csproj</c> says why; CC1006 polices every <c>GetGrain</c> in it.
///     </para>
/// </remarks>
/// <param name="grains">For the catalogue grain.</param>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ArtifactFeedReconciler(IGrainFactory grains, IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ArtifactFeeds.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var kind = ArtifactFeeds.KindOf(context.Desired);

        if (kind == FeedKind.Unknown) {
            // Unreachable through the write path, which validates the body against Schema2026 and
            // its AllowedValues. Kept because a reconciler is also callable directly.
            return ReconcileOutcome.Failed(
                ErrorCode.InvalidRequestBody,
                $"'{context.Id.Path}' carries no usable {ArtifactFeeds.KindPointer}; the write path should have refused it."
            );
        }

        context.Log.Report("opening", $"opening the {ArtifactFeeds.NameOf(kind)} catalogue for '{context.Id.Name}'", 40);

        var opened = await Feed(context.Id).OpenAsync(kind);

        if (opened.TryGetError(out var openError)) {
            return ReconcileOutcome.FromFailure(openError);
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var described = await Feed(context.Id).DescribeAsync();

        if (described.TryGetError(out var describeError)) {
            return ReconcileOutcome.FromFailure(describeError);
        }

        var feed = described.GetValueOrThrow();

        if (!feed.IsOpen || feed.Kind != kind) {
            return ReconcileOutcome.InProgress(
                $"the catalogue for '{context.Id.Name}' does not yet read back as an open {ArtifactFeeds.NameOf(kind)} feed",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the {ArtifactFeeds.NameOf(kind)} catalogue for '{context.Id.Name}' is open", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        context.Log.Report("closing", $"closing the catalogue for '{context.Id.Name}'");

        var closed = await Feed(context.Id).CloseAsync();

        if (closed.TryGetError(out var closeError)) {
            return ReconcileOutcome.FromFailure(closeError);
        }

        var prefix = ArtifactFeeds.StoragePrefix(context.Id.TenantId, context.Id.Id);
        var listed = await context.Objects.ListAsync(prefix, cancellationToken);

        if (listed.TryGetError(out var listError)) {
            return ReconcileOutcome.FromFailure(listError);
        }

        var remaining = listed.GetValueOrThrow();

        if (remaining.Length > 0) {
            context.Log.Report(
                "removing-artefacts",
                $"removing {remaining.Length.ToString(CultureInfo.InvariantCulture)} artefact(s) under '{prefix}'"
            );

            foreach (var key in remaining) {
                var removed = await context.Objects.DeleteAsync(key, cancellationToken);

                if (removed.TryGetError(out var deleteError)) {
                    return ReconcileOutcome.FromFailure(deleteError);
                }
            }
        }

        // ⚠ Converged once the prefix is EMPTY, read back — not once the deletes were issued.
        var readBack = await context.Objects.ListAsync(prefix, cancellationToken);

        if (readBack.TryGetError(out var readError)) {
            return ReconcileOutcome.FromFailure(readError);
        }

        if (readBack.GetValueOrThrow().Length > 0) {
            return ReconcileOutcome.InProgress(
                $"artefacts are still listed under '{prefix}'",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("deleted", $"the catalogue for '{context.Id.Name}' is closed and its storage is empty", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        var described = await Feed(context.Id).DescribeAsync();

        if (described.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the catalogue could not be read" };
        }

        var feed = described.GetValueOrThrow();
        var kind = ArtifactFeeds.KindOf(context.Desired);

        return new() {
            Exists = feed.IsOpen,
            ObservedAt = clock.UtcNow,
            Summary = feed.IsOpen
                ? feed.Kind == kind
                    ? $"the {ArtifactFeeds.NameOf(feed.Kind)} catalogue is open with {feed.EntryCount.ToString(CultureInfo.InvariantCulture)} entries"
                    : "the catalogue is open as a different kind"
                : feed.IsClosed
                    ? "the catalogue is closed"
                    : "the catalogue is not open"
        };
    }

    IFeedGrain Feed(ResourceId id) =>
        grains.ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IFeedGrain>(GrainKeys.Resource(id.Id));
}
