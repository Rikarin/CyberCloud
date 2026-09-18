using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceGraph;

/// <summary>What the store did with one row.</summary>
public enum ProjectionOutcome {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>The row was inserted; it is the newest version of its resource.</summary>
    Applied = 1,

    /// <summary>
    ///     The table already held this version or a later one, so nothing was written. A replay, a
    ///     redelivery, or the gateway's event landing after the silo's.
    /// </summary>
    Dropped = 2
}

/// <summary>One resource looked up in the projection: its row, or nothing.</summary>
/// <param name="Row">The collapsed row, or <c>null</c> when the table holds no row for the resource.</param>
public sealed record ResourceGraphLookup(ResourceGraphRow? Row) {
    /// <summary>Whether a row was found.</summary>
    public bool Found => Row is not null;
}

/// <summary>
///     The projection's writer and reader: one tenant's <c>resource_graph</c> table in the
///     platform's ClickHouse.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The tenant's database and table are created on first contact and remembered per
///         process.</b> Nothing else creates them — the Monitor workspace's owed
///         <c>clickhouse-ttl-is-published-not-applied</c> says in as many words that "nothing in this
///         catalogue applies SQL", and this is the first thing that does. <c>IF NOT EXISTS</c> on
///         both statements makes two silos racing to the first event of a new tenant both succeed;
///         the memo makes the second event of a known tenant cost one statement rather than three.
///         A memo is per process, so a fresh silo pays the two DDL round trips once per tenant it
///         meets, which is the right price for not holding a tenant list anywhere.
///     </para>
///     <para>
///         ⚠ <b>The version check is read-then-write, and that is acceptable only because of the
///         engine.</b> Two silos projecting two versions of one resource at once can both read the
///         same <c>max(version)</c> and both insert; <c>ReplacingMergeTree(version)</c> then keeps the
///         higher and a reader saying <c>FINAL</c> never sees the lower. The check exists to make a
///         replay of a thousand events a thousand reads and no writes, not to guarantee anything the
///         table does not already.
///     </para>
/// </remarks>
public sealed class ClickHouseResourceGraphStore {
    readonly ClickHouseClient clickHouse;
    readonly ConcurrentDictionary<Guid, Task<Result>> prepared = new();

    /// <summary>Creates a store over one client.</summary>
    /// <param name="clickHouse">The HTTP client for the platform's ClickHouse.</param>
    public ClickHouseResourceGraphStore(ClickHouseClient clickHouse) {
        ArgumentNullException.ThrowIfNull(clickHouse);
        this.clickHouse = clickHouse;
    }

    /// <summary>
    ///     Creates the tenant's database and table if they are not there, once per process per
    ///     tenant. A failure is not remembered, so the next event tries again.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the two statements.</param>
    public async Task<Result> EnsureTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) {
        var attempt = prepared.GetOrAdd(tenantId, id => PrepareAsync(id, cancellationToken));

        Result result;

        try {
            result = await attempt;
        }
        catch (OperationCanceledException) {
            // ⚠ The memo holds the FIRST caller's attempt, on the first caller's token. A cancelled
            // attempt must not stay memoized, or every later event for the tenant would await a
            // task that can only throw.
            prepared.TryRemove(new(tenantId, attempt));
            throw;
        }

        if (!result.IsSuccess) {
            prepared.TryRemove(new(tenantId, attempt));
        }

        return result;
    }

    /// <summary>
    ///     Inserts the row unless the table already holds its version or a later one.
    /// </summary>
    /// <param name="row">The row, with <see cref="ResourceGraphRow.Version" /> set from the event.</param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    public async Task<Result<ProjectionOutcome>> ApplyAsync(ResourceGraphRow row, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(row);

        var ensured = await EnsureTenantAsync(row.TenantId, cancellationToken);

        if (ensured.TryGetError(out var ensureError)) {
            return Result<ProjectionOutcome>.Failure(ensureError);
        }

        var held = await HeldVersionAsync(row.TenantId, row.ResourceId, cancellationToken);

        if (held.TryGetError(out var heldError)) {
            return Result<ProjectionOutcome>.Failure(heldError);
        }

        if (held.GetValueOrThrow() >= row.Version) {
            return Result<ProjectionOutcome>.Success(ProjectionOutcome.Dropped);
        }

        var inserted = await clickHouse.ExecuteAsync(
            ResourceGraphTable.Insert(row.TenantId) + ResourceGraphJson.EncodeRow(row),
            cancellationToken: cancellationToken
        );

        return inserted.TryGetError(out var insertError)
            ? Result<ProjectionOutcome>.Failure(insertError)
            : Result<ProjectionOutcome>.Success(ProjectionOutcome.Applied);
    }

    /// <summary>The resource's current row, collapsed to its highest version, or an empty lookup.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="resourceId">The resource.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public async Task<Result<ResourceGraphLookup>> ReadAsync(Guid tenantId, Guid resourceId, CancellationToken cancellationToken = default) {
        var ensured = await EnsureTenantAsync(tenantId, cancellationToken);

        if (ensured.TryGetError(out var ensureError)) {
            return Result<ResourceGraphLookup>.Failure(ensureError);
        }

        var read = await clickHouse.ExecuteAsync(ResourceGraphTable.SelectRow(tenantId), ResourceParameter(resourceId), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return Result<ResourceGraphLookup>.Failure(readError);
        }

        try {
            return Result<ResourceGraphLookup>.Success(new(ResourceGraphJson.DecodeFirstRow(read.GetValueOrThrow())));
        }
        catch (JsonException exception) {
            return NotJson<ResourceGraphLookup>(read.GetValueOrThrow(), exception);
        }
    }

    /// <summary>
    ///     The highest version held for the resource, or 0 for none. ⚠ <c>max()</c> over no rows is 0
    ///     for a <c>UInt64</c> rather than null, and 0 is below every real version — the grain counts
    ///     from 1 — so "no row" and "version 0" are one answer and it means "nothing held".
    /// </summary>
    async Task<Result<long>> HeldVersionAsync(Guid tenantId, Guid resourceId, CancellationToken cancellationToken) {
        var read = await clickHouse.ExecuteAsync(ResourceGraphTable.SelectVersion(tenantId), ResourceParameter(resourceId), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return Result<long>.Failure(readError);
        }

        try {
            return Result<long>.Success(ResourceGraphJson.DecodeNumber(read.GetValueOrThrow(), "version") ?? 0);
        }
        catch (JsonException exception) {
            return NotJson<long>(read.GetValueOrThrow(), exception);
        }
    }

    /// <summary>
    ///     A <c>200</c> whose body is not the <c>JSONEachRow</c> the statement asked for, as a
    ///     failure the caller retries. ⚠ A proxy's HTML error page on a 200 is the case that found
    ///     this; before it the <see cref="JsonException" /> escaped the projector's loop (#54 review).
    /// </summary>
    static Result<T> NotJson<T>(string body, JsonException exception) where T : notnull {
        var excerpt = body.Length > 200 ? body[..200] + "…" : body;

        return Result<T>.Failure(
            ErrorCode.InternalError,
            $"ClickHouse answered 200 with a body that is not JSONEachRow: {exception.Message}. The body began: {excerpt.Trim()}"
        );
    }

    async Task<Result> PrepareAsync(Guid tenantId, CancellationToken cancellationToken) {
        var database = await clickHouse.ExecuteAsync(ResourceGraphTable.CreateDatabase(tenantId), cancellationToken: cancellationToken);

        if (database.TryGetError(out var databaseError)) {
            return Result.Failure(databaseError);
        }

        var table = await clickHouse.ExecuteAsync(ResourceGraphTable.CreateTable(tenantId), cancellationToken: cancellationToken);

        return table.TryGetError(out var tableError) ? Result.Failure(tableError) : Result.Success;
    }

    static Dictionary<string, string> ResourceParameter(Guid resourceId) =>
        new(StringComparer.Ordinal) { ["resource_id"] = resourceId.ToString("D", CultureInfo.InvariantCulture) };
}
