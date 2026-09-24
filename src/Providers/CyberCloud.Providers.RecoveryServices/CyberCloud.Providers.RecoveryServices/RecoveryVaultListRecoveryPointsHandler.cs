// ⚠ For `Result<string>`. The `ErrorCode` alias in GlobalUsings still wins over the `Orleans.ErrorCode`
// this import would otherwise put back in play.

using CyberCloud.Core;

namespace CyberCloud.Providers.RecoveryServices;

/// <summary>
///     Answers <c>POST …/vaults/{name}/listRecoveryPoints</c>: every <c>Backup</c> the vault's
///     schedules have produced, across every protected item, newest first.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two listings and one read per point, and no view.</b> <see cref="ActionContext" />
///         carries no <see cref="IResourceView" />, so the handler finds the vault's ScheduledBackups
///         by the vault's own labels in the vault's own namespace — which is where every protected
///         item's objects are, because <see cref="RecoveryVaults.ItemOf" /> refuses an item in another
///         resource group. Each schedule's item is read off <see cref="RecoveryVaults.ProtectedItemLabel" />;
///         each schedule's points are the operator's own <see cref="RecoveryVaults.ParentScheduledBackupLabel" />
///         listing.
///     </para>
///     <para>
///         ⚠ <b>A failed point is listed, with the operator's error text, rather than filtered.</b> A
///         server whose store refuses its key or cannot be reached produces a <c>failed</c> point on
///         every tick, and that line — whatever barman said — is the one a tenant needs to see. <c>/completed</c> beside <c>/count</c> is the number that says
///         whether the vault holds anything restorable.
///     </para>
///     <para>
///         ⚠ <b>Fails rather than answering an empty collection when a listing fails.</b> A vault that
///         answered <c>count: 0</c> because it could not reach the cluster would read as "no
///         backups", which is the answer that sends somebody to check the store — the wrong place.
///     </para>
/// </remarks>
public sealed class RecoveryVaultListRecoveryPointsHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => RecoveryVaults.Type;

    /// <inheritdoc />
    public string Action => RecoveryVaults.ListRecoveryPointsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a vault's recovery points are Backup "
                + "objects in the cluster. The type declares RequiresCluster, so ActionDispatcher should "
                + "have refused this call."
            );
        }

        var points = await RecoveryPoints.ListAsync(cluster, context.Namespace, context.Id.Id, cancellationToken);

        return points.TryGetError(out var error)
            ? Result<string>.Failure(error)
            : Result<string>.Success(RecoveryVaults.RecoveryPointsJson(points.GetValueOrThrow()));
    }
}

/// <summary>The listing both handlers share: every recovery point of every schedule a vault owns.</summary>
static class RecoveryPoints {
    /// <summary>Every recovery point of a vault, with the item each belongs to.</summary>
    /// <param name="cluster">The vault's cluster connection.</param>
    /// <param name="ns">The vault's namespace.</param>
    /// <param name="vaultId">The vault's GUID.</param>
    /// <param name="cancellationToken">The caller's budget.</param>
    public static async Task<Result<List<RecoveryVaults.RecoveryPoint>>> ListAsync(
        IKubeClusterConnection cluster,
        string ns,
        Guid vaultId,
        CancellationToken cancellationToken
    ) {
        var schedules = await cluster.ListAsync(
            RecoveryVaults.ScheduledBackupKind,
            ns,
            RecoveryVaults.Selector(vaultId),
            cancellationToken
        );

        if (schedules.TryGetError(out var scheduleError)) {
            return Result<List<RecoveryVaults.RecoveryPoint>>.Failure(scheduleError);
        }

        var points = new List<RecoveryVaults.RecoveryPoint>();

        foreach (var schedule in schedules.GetValueOrThrow()) {
            var item = schedule.Labels.TryGetValue(RecoveryVaults.ProtectedItemLabel, out var labelled)
                ? labelled
                : schedule.Name;

            var backups = await cluster.ListAsync(
                RecoveryVaults.BackupKind,
                ns,
                RecoveryVaults.RecoveryPointSelector(schedule.Name),
                cancellationToken
            );

            if (backups.TryGetError(out var backupError)) {
                return Result<List<RecoveryVaults.RecoveryPoint>>.Failure(backupError);
            }

            foreach (var summary in backups.GetValueOrThrow()) {
                var read = await cluster.GetAsync(RecoveryVaults.BackupRef(ns, summary.Name), cancellationToken);

                if (read.TryGetError(out var readError)) {
                    if (readError.Code == ErrorCode.ResourceNotFound) {
                        // Pruned or garbage-collected between the listing and the read. Not a point.
                        continue;
                    }

                    return Result<List<RecoveryVaults.RecoveryPoint>>.Failure(readError);
                }

                if (RecoveryVaults.RecoveryPointOf(item, read.GetValueOrThrow().Json) is { } point) {
                    points.Add(point);
                }
            }
        }

        return Result<List<RecoveryVaults.RecoveryPoint>>.Success(points);
    }
}
