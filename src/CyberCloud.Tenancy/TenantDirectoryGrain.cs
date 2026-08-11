using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="ITenantDirectoryGrain" /> — Platform, Durable, <b>null tenant</b>, key
///     <c>platform/tenant-directory</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One activation holding every tenant, and that is the design rather than a smell.</b>
///         docs/plan/05 § The tenant directory sizes it: "About 200 bytes. For 1 000 000 tenants that
///         is 200 MB — small enough to be resident in every gateway process", with a write rate of
///         "0.12 writes per second" at 10 000 new tenants a day. A single-threaded activation at
///         0.12 writes per second is not a bottleneck; what would be one is putting it on a
///         per-request read path, which is why <c>ITenantDirectoryCache</c> exists and why
///         docs/plan/04 § The clusters, plural says "if it is ever on a per-request path, that is a
///         bug with a name".
///     </para>
///     <para>
///         ⚠ <b>The delta window is bounded and a cache that falls behind gets a full snapshot.</b>
///         Keeping every version forever would make the state grow without limit; the alternative —
///         telling a stale reader it is stale and handing it everything — is one large response on a
///         path that happens at silo start.
///     </para>
/// </remarks>
public sealed class TenantDirectoryGrain(
    [PersistentState("directory", StorageTiers.Durable)] IPersistentState<TenantDirectoryState> state
)
    : Grain, ITenantDirectoryGrain {
    /// <summary>
    ///     How far back a caller's cursor may be before it is handed the whole directory instead of
    ///     a delta.
    /// </summary>
    const long DeltaWindow = 10_000;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        TenancyGrainKeys.EnsurePlatformSingleton(this, GrainKeys.TenantDirectorySingleton);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<TenantDirectoryEntry>> RegisterAsync(TenantDirectoryEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.TenantId == Guid.Empty) {
            return Result<TenantDirectoryEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A directory entry needs a tenant id."
            );
        }

        var slug = ResourceNaming.Validate(entry.Slug, "tenant slug");
        if (slug.TryGetError(out var invalid)) {
            return Result<TenantDirectoryEntry>.Failure(invalid);
        }

        if (state.State.TombstonedSlugs.Contains(entry.Slug, StringComparer.Ordinal)) {
            return Result<TenantDirectoryEntry>.Failure(
                ErrorCode.Conflict,
                $"Slug '{entry.Slug}' belonged to a purged tenant. docs/plan/06 § Tenant lifecycle "
                + "tombstones a purged tenant's directory entry forever and never reuses an id; "
                + "reusing the slug would make an old audit trail point at a new customer."
            );
        }

        if (state.State.BySlug.TryGetValue(entry.Slug, out var owner) && owner != entry.TenantId) {
            return Result<TenantDirectoryEntry>.Failure(
                ErrorCode.Conflict,
                $"Slug '{entry.Slug}' is already held by tenant {owner:D}. Tenant slugs are globally "
                + "unique — docs/plan/04 § The clusters, plural."
            );
        }

        if (state.State.Entries.TryGetValue(entry.TenantId, out var previous)
            && !string.Equals(previous.Slug, entry.Slug, StringComparison.Ordinal)) {
            state.State.BySlug.Remove(previous.Slug);
        }

        var written = entry with { DirectoryVersion = ++state.State.Version };
        state.State.Entries[entry.TenantId] = written;
        state.State.BySlug[entry.Slug] = entry.TenantId;

        await state.WriteStateAsync();
        return Result<TenantDirectoryEntry>.Success(written);
    }

    /// <inheritdoc />
    public Task<Result<TenantDirectoryEntry>> LookupAsync(Guid tenantId) =>
        Task.FromResult(
            state.State.Entries.TryGetValue(tenantId, out var entry)
                ? Result<TenantDirectoryEntry>.Success(entry)
                : Result<TenantDirectoryEntry>.Failure(
                    ErrorCode.TenantNotFound,
                    $"Tenant {tenantId:D} is not in the directory."
                )
        );

    /// <inheritdoc />
    public Task<Result<TenantDirectoryEntry>> LookupBySlugAsync(string slug) =>
        Task.FromResult(
            state.State.BySlug.TryGetValue(slug ?? string.Empty, out var tenantId)
            && state.State.Entries.TryGetValue(tenantId, out var entry)
                ? Result<TenantDirectoryEntry>.Success(entry)
                : Result<TenantDirectoryEntry>.Failure(ErrorCode.TenantNotFound, $"No tenant has the slug '{slug}'.")
        );

    /// <inheritdoc />
    public async Task<Result<TenantDirectoryEntry>> SetStatusAsync(Guid tenantId, TenantStatus status) {
        if (!state.State.Entries.TryGetValue(tenantId, out var entry)) {
            return Result<TenantDirectoryEntry>.Failure(
                ErrorCode.TenantNotFound,
                $"Tenant {tenantId:D} is not in the directory."
            );
        }

        if (entry.Status == TenantStatus.Purged) {
            return Result<TenantDirectoryEntry>.Failure(
                ErrorCode.Conflict,
                $"Tenant {tenantId:D} is Purged, which docs/plan/06 § Tenant lifecycle makes "
                + "terminal — the entry is tombstoned forever."
            );
        }

        var written = entry with { Status = status, DirectoryVersion = ++state.State.Version };
        state.State.Entries[tenantId] = written;

        if (status == TenantStatus.Purged) {
            // The slug is burned, not freed. The entry stays so that the id can never be reissued.
            if (!state.State.TombstonedSlugs.Contains(entry.Slug, StringComparer.Ordinal)) {
                state.State.TombstonedSlugs.Add(entry.Slug);
            }

            state.State.BySlug.Remove(entry.Slug);
        }

        await state.WriteStateAsync();
        return Result<TenantDirectoryEntry>.Success(written);
    }

    /// <inheritdoc />
    public Task<Result<TenantDirectoryDelta>> GetDeltaAsync(long knownVersion) {
        var full = knownVersion <= 0 || state.State.Version - knownVersion > DeltaWindow;

        var entries = full
            ? state.State.Entries.Values.OrderBy(x => x.DirectoryVersion).ToList()
            : state.State.Entries.Values
                .Where(x => x.DirectoryVersion > knownVersion)
                .OrderBy(x => x.DirectoryVersion)
                .ToList();

        return Task.FromResult(
            Result<TenantDirectoryDelta>.Success(
                new() { Version = state.State.Version, Entries = entries, IsFullSnapshot = full }
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<int>> CountAsync() => Task.FromResult(Result<int>.Success(state.State.Entries.Count));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
