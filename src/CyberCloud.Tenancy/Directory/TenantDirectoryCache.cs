using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;

namespace CyberCloud.Tenancy.Directory;

/// <summary>
///     The in-process mirror of the global tenant directory — docs/plan/05 § The tenant directory.
/// </summary>
/// <remarks>
///     <para>
///         The three claims this type has to pay for, each of which is a test in
///         <c>TenantDirectoryCacheTests</c>:
///     </para>
///     <list type="number">
///         <item>
///             <b>"Reads never leave the process."</b> <see cref="TryLookup" /> is a dictionary read
///             against an immutable snapshot and cannot do I/O at all — it is not even async.
///         </item>
///         <item>
///             <b>
///                 "A cache miss … falls back to a grain call — measured, alerted on, and expected to
///                 be a handful per second worldwide."
///             </b>
///             <see cref="LookupAsync" /> is the fallback and
///             <see cref="Misses" /> is the measurement.
///         </item>
///         <item>
///             <b>
///                 "If the global cluster is unreachable … every existing tenant keeps working from
///                 cache, in every region, indefinitely."
///             </b>
///             — chaos-invariant 5 in docs/plan/23. Every
///             failure path here returns cached data if it has any, and the word <i>indefinitely</i>
///             is load-bearing: there is <b>no</b> staleness deadline, no TTL and no circuit that
///             starts failing reads after N minutes. A cached entry serves until something replaces
///             it. A TTL here would be a timer that takes the platform down during an outage of the
///             one component whose outage the design promises to survive.
///         </item>
///     </list>
/// </remarks>
public sealed class TenantDirectoryCache(
    IGrainFactory grains,
    ILogger<TenantDirectoryCache> logger
) {
    volatile Snapshot current = new(
        0,
        ImmutableDictionary<Guid, TenantDirectoryEntry>.Empty,
        ImmutableDictionary<string, Guid>.Empty
    );

    /// <summary>The directory version this snapshot was built from.</summary>
    public long Version => current.Version;

    /// <summary>How many tenants are resident.</summary>
    public int Count => current.ById.Count;

    /// <summary>How many lookups were served from memory.</summary>
    public long Hits { get; private set; }

    /// <summary>
    ///     How many lookups fell through to a grain call. docs/plan/05 § The tenant directory
    ///     expects "a handful per second worldwide"; a rate above that means the delta feed is
    ///     broken, not that the cache is small.
    /// </summary>
    public long Misses { get; private set; }

    /// <summary>How many fallback grain calls failed and were served stale (or not at all).</summary>
    public long FallbackFailures { get; private set; }

    /// <summary>The directory grain — one activation worldwide, null tenant.</summary>
    public ITenantDirectoryGrain Grain => grains.GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory());

    /// <summary>
    ///     The in-process read. <b>Cannot do I/O</b> and cannot fail.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="entry">The entry, when resident.</param>
    public bool TryLookup(Guid tenantId, out TenantDirectoryEntry? entry) =>
        current.ById.TryGetValue(tenantId, out entry);

    /// <summary>
    ///     The in-process read, then the grain call on a miss.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <remarks>
    ///     ⚠ When the grain call fails and the tenant <i>is</i> resident, the cached entry is
    ///     returned and the failure is only counted. That is the whole of chaos-invariant 5: an
    ///     existing tenant is unaffected by the global cluster being gone.
    /// </remarks>
    public async Task<Result<TenantDirectoryEntry>> LookupAsync(Guid tenantId) {
        if (current.ById.TryGetValue(tenantId, out var cached)) {
            Hits++;
            return Result<TenantDirectoryEntry>.Success(cached);
        }

        Misses++;

        try {
            var looked = await Grain.LookupAsync(tenantId);
            if (looked.TryGetValue(out var entry)) {
                Absorb(entry);
            }

            return looked;
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            FallbackFailures++;

            logger.LogWarning(
                exception,
                "Tenant directory lookup for {TenantId} could not reach the global cluster and the "
                + "tenant is not resident in the local snapshot (version {Version}, {Count} "
                + "tenants).",
                tenantId,
                current.Version,
                current.ById.Count
            );

            return Result<TenantDirectoryEntry>.Failure(
                ErrorCode.TenantNotFound,
                $"Tenant {tenantId:D} is not in this process's directory snapshot and the global "
                + "directory cluster is unreachable. docs/plan/05 § The tenant directory: with the "
                + "global cluster down, no NEW tenant can be resolved anywhere — every existing one "
                + "keeps working from cache. This tenant is either new or does not exist."
            );
        }
    }

    /// <summary>Looks a tenant up by slug, in memory only.</summary>
    /// <param name="slug">The slug.</param>
    /// <param name="entry">The entry, when resident.</param>
    public bool TryLookupBySlug(string slug, out TenantDirectoryEntry? entry) {
        entry = null;
        return current.BySlug.TryGetValue(slug ?? string.Empty, out var id)
            && current.ById.TryGetValue(id, out entry);
    }

    /// <summary>Applies a delta from <see cref="ITenantDirectoryGrain.GetDeltaAsync" />.</summary>
    /// <param name="delta">The delta.</param>
    /// <returns><see langword="true" /> if anything changed.</returns>
    public bool Apply(TenantDirectoryDelta delta) {
        ArgumentNullException.ThrowIfNull(delta);

        var previous = current;
        if (delta.Version < previous.Version) {
            return false;
        }

        var byId = delta.IsFullSnapshot
            ? ImmutableDictionary.CreateBuilder<Guid, TenantDirectoryEntry>()
            : previous.ById.ToBuilder();

        var bySlug = delta.IsFullSnapshot
            ? ImmutableDictionary.CreateBuilder<string, Guid>(StringComparer.Ordinal)
            : previous.BySlug.ToBuilder();

        foreach (var entry in delta.Entries) {
            byId[entry.TenantId] = entry;
            bySlug[entry.Slug] = entry.TenantId;
        }

        // Replaced wholesale, so a reader never sees half a delta.
        current = new(delta.Version, byId.ToImmutable(), bySlug.ToImmutable());
        return delta.Version != previous.Version || byId.Count != previous.ById.Count;
    }

    /// <summary>Refreshes from the grain. Never throws.</summary>
    /// <param name="cancellationToken">The host's shutdown token.</param>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default) {
        try {
            cancellationToken.ThrowIfCancellationRequested();

            var delta = await Grain.GetDeltaAsync(current.Version);
            if (delta.TryGetValue(out var value)) {
                return Apply(value);
            }

            FallbackFailures++;
            return false;
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            FallbackFailures++;

            logger.LogWarning(
                exception,
                "Tenant directory refresh failed. The cached snapshot (version {Version}, {Count} "
                + "tenants) stays in use indefinitely — docs/plan/05 § The tenant directory.",
                current.Version,
                current.ById.Count
            );

            return false;
        }
    }

    void Absorb(TenantDirectoryEntry entry) {
        var previous = current;
        current = new(
            Math.Max(previous.Version, entry.DirectoryVersion),
            previous.ById.SetItem(entry.TenantId, entry),
            previous.BySlug.SetItem(entry.Slug, entry.TenantId)
        );
    }

    sealed record Snapshot(
        long Version,
        ImmutableDictionary<Guid, TenantDirectoryEntry> ById,
        ImmutableDictionary<string, Guid> BySlug
    );
}

/// <summary>Runs <see cref="TenantDirectoryCache" />'s refresh on a timer.</summary>
public sealed class TenantDirectoryRefreshService(
    TenantDirectoryCache cache,
    IOptions<TenancyRefreshOptions> options
)
    : BackgroundService {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Value.RunBackgroundRefresh) {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.DirectoryInterval);

        await cache.RefreshAsync(stoppingToken);

        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                await cache.RefreshAsync(stoppingToken);
            }
        } catch (OperationCanceledException) {
            // Shutdown.
        }
    }
}
