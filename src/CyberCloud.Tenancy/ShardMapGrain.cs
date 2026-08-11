using System.Globalization;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="IShardMapGrain" /> — Platform, Durable, <b>null tenant</b>, key
///     <c>platform/shard-map</c>.
/// </summary>
/// <remarks>
///     <para>
///         This is the type <c>StaticShardMapCache</c>'s remarks call "the real thing, which records
///         each assignment rather than recomputing it", and recording is the entire difference. The
///         stub's second documented limit — "adding a shard here re-places existing tenants,
///         <c>hash mod n</c> moves roughly <c>1 - 1/n</c> of tenants when <c>n</c> changes" — is
///         closed here by never recomputing a recorded assignment.
///     </para>
///     <para>
///         ⚠ <b>The placement function agrees with the stub's hash in the ordinary case, on
///         purpose.</b> <see cref="AssignAsync" /> prefers the shard the deterministic hash would
///         pick and only departs from it when that shard is out of the rotation. That keeps a
///         property worth having: for a tenant that has been assigned but whose assignment has not
///         yet reached a silo's cache, the cache's fallback and the recorded answer are the
///         <i>same</i> shard, so there is no window in which one silo writes to shard P while
///         another reads from shard Q. When they must differ — a full or draining shard — the
///         recording happens at tenant creation, before the tenant has any state, and the recorded
///         value wins everywhere from then on.
///     </para>
/// </remarks>
public sealed class ShardMapGrain(
    [PersistentState("shardMap", StorageTiers.Durable)] IPersistentState<ShardMapState> state,
    IClock clock)
    : Grain, IShardMapGrain
{
    /// <summary>
    ///     How far back a caller's cursor may be before it is handed the whole map instead of a
    ///     delta.
    /// </summary>
    const long DeltaWindow = 10_000;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        TenancyGrainKeys.EnsurePlatformSingleton(this, GrainKeys.ShardMapSingleton);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ShardMapSnapshot>> ConfigureShardsAsync(IReadOnlyList<string> durableShards)
    {
        ArgumentNullException.ThrowIfNull(durableShards);

        if (durableShards.Count == 0)
        {
            return Result<ShardMapSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                "The durable tier is N independent PostgreSQL servers (docs/plan/05 § Durable) and a "
                + "map with none of them cannot place a tenant.");
        }

        var added = false;
        foreach (var shard in durableShards)
        {
            if (state.State.Shards.TryAdd(shard, true))
            {
                added = true;
            }
        }

        // ⚠ Shards are added, never removed. A shard dropped from the configuration would orphan
        // every tenant recorded against it — their rows would still be in that database and nothing
        // would know where to look. Draining is SetAcceptingNewTenantsAsync, which is docs/plan/05
        // § The shard map's "stop assigning new tenants to it, which costs nothing".
        var missing = state.State.Shards.Keys
            .Except(durableShards, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0)
        {
            return Result<ShardMapSnapshot>.Failure(
                ErrorCode.Conflict,
                $"Shard(s) {string.Join(", ", missing)} are in the map and not in the supplied list. "
                + "A shard is never removed from the map: tenants are recorded against it and their "
                + "durable state is in that database. Take it out of the placement rotation with "
                + "SetAcceptingNewTenantsAsync instead — docs/plan/05 § The shard map.");
        }

        if (added)
        {
            state.State.Version++;
            await state.WriteStateAsync();
        }

        return Result<ShardMapSnapshot>.Success(Snapshot(0));
    }

    /// <inheritdoc />
    public async Task<Result<ShardAssignment>> AssignAsync(Guid tenantId, string region)
    {
        if (state.State.Assignments.TryGetValue(tenantId, out var existing))
        {
            // ⚠ THE PROPERTY. docs/plan/05 § The shard map: "Assignment is at tenant creation and it
            // is permanent … There is no automatic rebalancing, and that is a decision rather than
            // an omission." Nothing below this line runs for a tenant that already has an
            // assignment, whatever the shard list looks like now.
            return Result<ShardAssignment>.Success(existing);
        }

        if (state.State.Shards.Count == 0)
        {
            return Result<ShardAssignment>.Failure(
                ErrorCode.InternalError,
                "The shard map has no shards. Call ConfigureShardsAsync before assigning a tenant.");
        }

        var accepting = state.State.Shards
            .Where(x => x.Value)
            .Select(x => x.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (accepting.Count == 0)
        {
            return Result<ShardAssignment>.Failure(
                ErrorCode.QuotaExceeded,
                "Every durable shard is out of the placement rotation, so no new tenant can be "
                + "created. docs/plan/05 § The shard map: capacity is added at the front — start a "
                + "PostgreSQL server and put it in the map.");
        }

        var assignment = new ShardAssignment
        {
            TenantId = tenantId,
            DurableShard = Place(tenantId, accepting),
            HotHashTag = StaticShardMapCache.HotTagPrefix + tenantId.ToString("N", CultureInfo.InvariantCulture),
            Region = region,
            AssignedAt = clock.UtcNow,
            Version = ++state.State.Version,
        };

        state.State.Assignments[tenantId] = assignment;
        await state.WriteStateAsync();

        return Result<ShardAssignment>.Success(assignment);
    }

    /// <inheritdoc />
    public Task<Result<ShardAssignment>> GetAssignmentAsync(Guid tenantId) =>
        Task.FromResult(state.State.Assignments.TryGetValue(tenantId, out var assignment)
            ? Result<ShardAssignment>.Success(assignment)
            : Result<ShardAssignment>.Failure(
                ErrorCode.TenantNotFound,
                $"Tenant {tenantId:D} has never been assigned a shard."));

    /// <inheritdoc />
    public Task<Result<ShardMapSnapshot>> GetSnapshotAsync(long knownVersion) =>
        Task.FromResult(Result<ShardMapSnapshot>.Success(Snapshot(knownVersion)));

    /// <inheritdoc />
    public async Task<Result<ShardMapSnapshot>> SetAcceptingNewTenantsAsync(string shard, bool accepting)
    {
        if (!state.State.Shards.TryGetValue(shard, out var current))
        {
            return Result<ShardMapSnapshot>.Failure(
                ErrorCode.ResourceNotFound, $"Shard '{shard}' is not in the map.");
        }

        if (current != accepting)
        {
            state.State.Shards[shard] = accepting;
            state.State.Version++;
            await state.WriteStateAsync();
        }

        return Result<ShardMapSnapshot>.Success(Snapshot(0));
    }

    /// <inheritdoc />
    public Task<Result> PinAsync(Guid tenantId, string durableShard, string? hotOverride) =>
        throw new NotSupportedException(
            "PinAsync is not implemented. docs/plan/05 § The shard map budgets it at 0.5 EM in M2, "
            + "and what makes it safe is not the map edit but the four steps around it: quiesce the "
            + "tenant (rejecting writes with 503 Retry-After), copy the grain rows, flip the map, "
            + "un-quiesce. Flipping the map alone would repoint a live tenant at an empty database — "
            + "worse than not having the method. Until M2, an operator pin is configuration: "
            + "CyberCloud:Storage:Durable:Pins, honoured by IShardMapCache at wiring time.");

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Picks a shard for a tenant that has never had one.
    /// </summary>
    /// <remarks>
    ///     The deterministic hash first, so that the answer agrees with the fallback an
    ///     un-refreshed cache would compute; the least-loaded accepting shard when the hash lands on
    ///     one that is out of the rotation. That second arm is docs/plan/05 § The shard map's
    ///     "simple weighted pick", weighted by tenant count, with the tenant-id hash breaking ties so
    ///     the choice is still deterministic.
    /// </remarks>
    string Place(Guid tenantId, List<string> accepting)
    {
        var all = state.State.Shards.Keys.Order(StringComparer.Ordinal).ToList();
        var hash = StaticShardMapCache.StableHash(tenantId.ToString("D", CultureInfo.InvariantCulture));
        var preferred = all[(int)(hash % (uint)all.Count)];

        if (accepting.Contains(preferred, StringComparer.Ordinal))
        {
            return preferred;
        }

        var load = accepting.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        foreach (var assignment in state.State.Assignments.Values)
        {
            if (load.TryGetValue(assignment.DurableShard, out var count))
            {
                load[assignment.DurableShard] = count + 1;
            }
        }

        return load
            .OrderBy(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .First()
            .Key;
    }

    ShardMapSnapshot Snapshot(long knownVersion)
    {
        var full = knownVersion <= 0 || state.State.Version - knownVersion > DeltaWindow;

        var assignments = full
            ? state.State.Assignments.Values.OrderBy(x => x.Version).ToList()
            : state.State.Assignments.Values
                .Where(x => x.Version > knownVersion)
                .OrderBy(x => x.Version)
                .ToList();

        return new ShardMapSnapshot
        {
            Version = state.State.Version,
            // ⚠ EVERY shard, including drained ones, in the same order Place() hashes over. A cache
            // that filtered to the accepting shards would compute a different fallback than the one
            // Place() agrees with, which would reopen the window this design closes: one silo
            // writing to shard P while another reads from shard Q for the same unrefreshed tenant.
            DurableShards = [.. state.State.Shards.Keys.Order(StringComparer.Ordinal)],
            Assignments = assignments,
            IsFullSnapshot = full,
        };
    }
}
