using CyberCloud.Core.Resources;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CyberCloud.Tenancy.Shards;

/// <summary>
///     Pulls <see cref="IShardMapGrain" />'s deltas into <see cref="GrainBackedShardMapCache" />.
/// </summary>
/// <remarks>
///     <para>
///         This is the "refresh that fills the dictionary [and] happens elsewhere" of
///         <see cref="IShardMapCache" />'s remarks. It exists as a separate object rather than as a
///         call inside the cache because the cache is read from
///         <c>Orleans.Multitenant</c>'s <c>configureTenantOptions</c>, under a per-tenant lock,
///         inside a grain activation — a path on which a network round trip would put a hop on the
///         first activation of every tenant on every silo and could trip
///         <c>MultitenantStorageOptions.TenantStorageProviderInitTimeout</c>.
///     </para>
///     <para>
///         ⚠ <b>A failed refresh is logged and swallowed.</b> The shard map is the same shape of
///         global thing as the tenant directory, and docs/plan/05 § The tenant directory's failure
///         mode applies to both: with the global cluster unreachable no <i>new</i> placements
///         propagate, but every tenant already in the snapshot keeps resolving. Throwing here would
///         convert a global-cluster outage into a silo that cannot serve the tenants it already
///         knows about, which is the opposite of the intended blast radius.
///     </para>
///     <para>
///         ⚠
///         <b>
///             It is also the silo's <see cref="IShardMapMirror" />, and that is the one time the
///             refresh is pulled rather than polled.
///         </b> A tenant create records its assignment and then
///         needs every silo to have it before the tenant's first durable row — a pinned tenant's
///         record is not the hash the un-refreshed cache would fall back to, and a silo activating
///         the tenant's first grain on the hash would split the tenant across two shards.
///         <c>ShardMapPropagation.ConfirmAsync</c> reaches every silo through
///         <c>IManagementGrain.SendControlCommandToProvider</c> and <c>ShardMapMirrorController</c>,
///         which lands on <see cref="RefreshAndResolveAsync" /> here: one refresh, then the cache's
///         own answer for the tenant, so the caller compares what the storage layer on this silo
///         would actually do.
///     </para>
/// </remarks>
public sealed class ShardMapRefresher(
    GrainBackedShardMapCache cache,
    IGrainFactory grains,
    ILogger<ShardMapRefresher> logger
)
    : IShardMapMirror {
    /// <summary>How many refreshes have failed since the process started.</summary>
    public long Failures { get; private set; }

    /// <summary>How many refreshes have succeeded since the process started.</summary>
    public long Successes { get; private set; }

    /// <summary>The shard map grain — one activation worldwide.</summary>
    public IShardMapGrain Grain => grains.GetGrain<IShardMapGrain>(GrainKeys.ShardMap());

    /// <summary>Polls once. Never throws.</summary>
    /// <param name="cancellationToken">The host's shutdown token.</param>
    /// <returns><see langword="true" /> if a snapshot was applied.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default) {
        try {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await Grain.GetSnapshotAsync(cache.Version);
            if (snapshot.TryGetError(out var error)) {
                Failures++;
                logger.LogWarning("Shard map refresh returned {Code}: {Message}", error.Code.Value, error.Message);
                return false;
            }

            Successes++;
            return cache.Apply(snapshot.GetValueOrThrow());
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            Failures++;

            // Deliberately swallowed — see the ⚠ on the type. The count is the signal; docs/plan/05
            // § The tenant directory says a miss on this path is "measured, alerted on".
            logger.LogWarning(
                exception,
                "Shard map refresh failed. The cached map (version {Version}, {Count} recorded "
                + "assignments) stays in use, so every tenant already in it keeps resolving.",
                cache.Version,
                cache.RecordedAssignments
            );

            return false;
        }
    }

    /// <summary>How many times the fan-out has asked this silo to refresh and resolve.</summary>
    public long Commands { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Answers with the cache's answer even when the refresh failed, on purpose: the caller is
    ///     asking what THIS silo's storage layer would do for the tenant right now, and a stale
    ///     mirror's hash fallback is that answer. Hiding it behind an exception would let the caller
    ///     treat "did not confirm" as something other than "would write to the wrong shard".
    /// </remarks>
    public async Task<string> RefreshAndResolveAsync(string tenantId) {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        Commands++;
        await RefreshAsync();

        return cache.DurableShardFor(tenantId);
    }
}

/// <summary>Runs <see cref="ShardMapRefresher" /> on a timer for the life of the silo.</summary>
public sealed class ShardMapRefreshService(
    ShardMapRefresher refresher,
    IOptions<TenancyRefreshOptions> options
)
    : BackgroundService {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Value.RunBackgroundRefresh) {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.ShardMapInterval);

        await refresher.RefreshAsync(stoppingToken);

        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                await refresher.RefreshAsync(stoppingToken);
            }
        } catch (OperationCanceledException) {
            // Shutdown.
        }
    }
}
