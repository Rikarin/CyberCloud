using CyberCloud.ResourceManager.Contracts.Registry;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>
///     Hands each <c>resource-changed</c> event to the resources that asked to hear about its type in
///     its subscription — the delivery half of <see cref="IResourceWatch" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Runs at step 11 of the write path, beside the projection's sink, in the process that
///         accepted the write.</b> <c>ResourceManagerService.EmitAsync</c> calls
///         <see cref="DeliverAsync" /> after <see cref="IResourceChangedSink.PublishAsync" />, so a
///         watcher hears about a change when the projection does. It is not the sink and is not
///         registered as one: a host that replaces <see cref="IResourceChangedSink" /> with a real
///         projector must not lose the fan-out by doing so, and a decorator would.
///     </para>
///     <para>
///         ⚠ <b>Each delivery is checked by the read rule, watcher by watcher.</b> The event carries
///         the resource's path, name and tags, so handing it to a watcher that could not
///         <see cref="IResourceView.ReadAsync" /> the resource would disclose exactly what the
///         gateway's <c>404</c> withholds. <see cref="ResourceViews.MayReadAsync(ResourceId, ResourceId, string, CancellationToken)" />
///         is the one rule, and a refused watcher is skipped with a log line rather than told.
///     </para>
///     <para>
///         ⚠ <b>A failure to deliver never fails the write</b>, for the reason a failed publish does
///         not (docs/plan/08 § The resource-graph projection): the write stands, the watcher's next
///         pass rescans, and refusing a tenant's <c>PUT</c> because a bystander could not be told
///         about it would trade a correct write for a courtesy.
///     </para>
///     <para>
///         ⚠ <b>A watcher whose grain is gone is dropped from the index here.</b> Deleting a resource
///         does not unsubscribe it — the delete path knows nothing about what the resource watched,
///         and asking every reconciler to say would be one more thing to forget. So the index is
///         pruned lazily, on the first fan-out that finds the watcher's
///         <see cref="IResourceGrain.NotifyChangedAsync" /> answering
///         <see cref="ErrorCode.ResourceNotFound" />. Until then a dead watcher costs one grain call
///         per change to its type.
///     </para>
/// </remarks>
public sealed class ResourceWatchFanout(
    IProviderRegistry registry,
    IGrainFactory grains,
    IResourceAuthorizer authorizer,
    ILogger<ResourceWatchFanout> logger
) {
    /// <summary>Delivers one event to every watcher that may see it.</summary>
    /// <param name="change">The event, with <see cref="ResourceChangedEvent.Path" /> set.</param>
    /// <param name="cancellationToken">Cancels the fan-out between watchers, never mid-delivery.</param>
    /// <returns>How many watchers were handed the event.</returns>
    public async Task<int> DeliverAsync(ResourceChangedEvent change, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(change);

        var parsed = ResourceId.ParsePath(change.Path);
        if (parsed.TryGetError(out var pathError)) {
            logger.LogWarning(
                "resource-changed for {ResourceId} carries the path '{Path}', which does not parse ({Message}); "
                + "no watcher can be told about it.",
                change.ResourceId,
                change.Path,
                pathError.Message
            );

            return 0;
        }

        var target = parsed.GetValueOrThrow().WithId(change.ResourceId);

        if (!registry.TryGetType(target.Type, out var registration)) {
            return 0;
        }

        var tenant = grains.ForTenant(change.TenantId.ToString("D", CultureInfo.InvariantCulture));

        // ⚠ THE CHANGED RESOURCE'S SUBSCRIPTION, so only watchers registered in the same subscription
        // are read — that is the seam's scope, enforced by the key rather than by a filter.
        var index = tenant.GetGrain<IResourceWatchGrain>(GrainKeys.WatchIndex(change.SubscriptionId, target.Type));

        var listed = await index.ListAsync();
        if (listed.TryGetError(out var listError)) {
            logger.LogWarning(
                "The watchers of {Type} in subscription {Subscription} could not be listed: {Message}. "
                + "The write stands; whoever was watching will rescan on its next pass.",
                target.Type,
                change.SubscriptionId,
                listError.Message
            );

            return 0;
        }

        var delivered = 0;

        foreach (var watcher in listed.GetValueOrThrow()) {
            cancellationToken.ThrowIfCancellationRequested();

            // A resource is not told about itself: its own write is the pass it is already in.
            if (watcher.ResourceId == change.ResourceId) {
                continue;
            }

            var watcherAddress = ResourceId.ParsePath(watcher.Path);
            if (watcherAddress.IsFailure) {
                continue;
            }

            var reader = watcherAddress.GetValueOrThrow().WithId(watcher.ResourceId);

            var allowed = await ResourceViews.MayReadAsync(
                authorizer,
                reader,
                target,
                registration.ReadPermission,
                cancellationToken
            );

            if (allowed.TryGetError(out var refused)) {
                logger.LogInformation(
                    "'{Watcher}' watches {Type} and may not read '{Target}', so it is not told about the change: {Message}",
                    watcher.Path,
                    target.Type,
                    target.Path,
                    refused.Message
                );

                continue;
            }

            var handed = await tenant.GetGrain<IResourceGrain>(GrainKeys.Resource(watcher.ResourceId)).NotifyChangedAsync(change);

            if (handed.IsSuccess) {
                delivered++;
                continue;
            }

            if (handed.Error is { Code: var code } && code == ErrorCode.ResourceNotFound) {
                _ = await index.UnsubscribeAsync(watcher.ResourceId);
                logger.LogInformation(
                    "'{Watcher}' watched {Type} and no longer exists; its watch has been dropped.",
                    watcher.Path,
                    target.Type
                );

                continue;
            }

            logger.LogWarning(
                "'{Watcher}' could not be told that '{Target}' changed: {Message}. It will rescan on its next pass.",
                watcher.Path,
                target.Path,
                handed.Error?.Message
            );
        }

        return delivered;
    }
}
