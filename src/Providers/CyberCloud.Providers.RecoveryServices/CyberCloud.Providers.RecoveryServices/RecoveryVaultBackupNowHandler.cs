using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices;

/// <summary>
///     Answers <c>POST …/vaults/{name}/backupNow</c>: a recovery point of one protected item, taken
///     now rather than on the schedule.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The point is the schedule's, in every way the rest of the vault reads.</b> It is owned
///         by the item's <c>ScheduledBackup</c> — so the vault's teardown takes it with the schedule,
///         as it takes the controller's own points — and labelled
///         <see cref="RecoveryVaults.ParentScheduledBackupLabel" /> with the schedule's name, which is
///         the label <c>listRecoveryPoints</c>, <c>recover</c> and retention all select by. A point
///         that carried the vault's labels and not the schedule's would be one the vault listed
///         nowhere and never pruned.
///     </para>
///     <para>
///         ⚠ <b>The item must already be scheduled.</b> The handler finds the item's schedule by the
///         vault's own labels and refuses an item the vault does not protect with the same
///         <c>404</c> an absent one gets; a vault whose last pass refused the item has no schedule for
///         it, and the refusal on the vault says why.
///     </para>
/// </remarks>
/// <param name="clock">Names the point by the second it was asked for.</param>
public sealed class RecoveryVaultBackupNowHandler(IClock clock) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => RecoveryVaults.Type;

    /// <inheritdoc />
    public string Action => RecoveryVaults.BackupNowAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a recovery point is a Backup object in "
                + "the cluster. The type declares RequiresCluster, so ActionDispatcher should have refused "
                + "this call."
            );
        }

        var item = context.Body.ValueKind is JsonValueKind.Object
            && context.Body.TryGetProperty("item", out var named)
            && named.ValueKind is JsonValueKind.String
                ? named.GetString() ?? string.Empty
                : string.Empty;

        var schedules = await cluster.ListAsync(
            RecoveryVaults.ScheduledBackupKind,
            context.Namespace,
            RecoveryVaults.Selector(context.Id.Id),
            cancellationToken
        );

        if (schedules.TryGetError(out var listError)) {
            return Result<string>.Failure(listError);
        }

        var schedule = schedules.GetValueOrThrow()
            .FirstOrDefault(x => x.Labels.TryGetValue(RecoveryVaults.ProtectedItemLabel, out var labelled)
                && string.Equals(labelled, item, StringComparison.Ordinal)
            );

        if (schedule is null) {
            return Result<string>.Failure(
                ErrorCode.ResourceNotFound,
                $"'{item}' is not a protected item this vault has scheduled. listRecoveryPoints names the "
                + "ones it has; an item the vault refused has no schedule, and the vault's own failure says why.",
                "/item"
            );
        }

        // The schedule's uid for the owner reference, and its cluster for the Backup's spec — both off
        // the object itself, because a listing carries neither.
        var read = await cluster.GetAsync(
            new() { Kind = RecoveryVaults.ScheduledBackupKind, Namespace = context.Namespace, Name = schedule.Name },
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var document = JsonNode.Parse(read.GetValueOrThrow().Json);
        var uid = KubeJson.UidOf(document);
        var target = (document?["spec"]?["cluster"] as JsonObject)?["name"]?.GetValue<string>() ?? string.Empty;

        if (uid.Length == 0 || target.Length == 0) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"The schedule '{schedule.Name}' read back without a uid or a cluster, so a point it would own "
                + "cannot be written."
            );
        }

        var name = RecoveryVaults.OnDemandBackupNameOf(schedule.Name, clock.UtcNow);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(RecoveryVaults.BackupKind)
            .WithApiVersion(context.ApiVersion)
            .WithLabels(
                (RecoveryVaults.ProtectedItemLabel, item),
                (RecoveryVaults.ParentScheduledBackupLabel, schedule.Name)
            )
            .WithOwner(context.Id, RecoveryVaults.ScheduledBackupKind, schedule.Name, uid)
            .ObjectJson(RecoveryVaults.OnDemandBackupJson(name, target))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return Result<string>.Failure(applyError);
        }

        if (applied.GetValueOrThrow().Result is ApplyResult.Suspended or ApplyResult.Conflict) {
            return Result<string>.Failure(
                ErrorCode.OperationInProgress,
                $"The recovery point of '{item}' was not written: {applied.GetValueOrThrow().Message}. Ask again."
            );
        }

        return Result<string>.Success(new JsonObject { ["item"] = item, ["recoveryPoint"] = name }.ToJsonString());
    }
}
