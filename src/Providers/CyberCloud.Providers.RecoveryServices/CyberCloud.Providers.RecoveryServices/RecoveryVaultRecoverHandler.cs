// ⚠ For `Result<string>`. The `ErrorCode` alias in GlobalUsings still wins over the `Orleans.ErrorCode`
// this import would otherwise put back in play.

using CyberCloud.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices;

/// <summary>
///     Answers <c>POST …/vaults/{name}/recover</c>: a <b>new</b> PostgreSQL server resource,
///     bootstrapped from one of the vault's completed recovery points, beside the protected server.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/15 § Backup as a service:
///         <i>
///             "Restore always creates a new resource.
///             Restore-in-place is how people lose the good copy while trying to recover it."
///         </i> Four
///         checks stand between the request and the apply, and each refuses by name: the recovery
///         point must exist, it must belong to one of <i>this</i> vault's schedules, its phase must be
///         <c>completed</c>, and nothing may already be called <c>targetName</c>. The third is the
///         one docs/plan/15 is about — <i>"a restore that has never been tested is not a backup"</i>
///         — and a handler that bootstrapped from a <c>failed</c> point would produce a cluster that
///         never comes up, hours later, with the cause in an operator log.
///     </para>
///     <para>
///         ⚠ <b>The source cluster may be gone, and that is the case a restore exists for.</b> The
///         restored server copies its version, storage, replicas and database from the protected
///         server's <c>Cluster</c> when it is still there; when it is not — the disaster the vault
///         was bought against — <see cref="RecoveryVaults.RestoredServerBody" /> falls back to the
///         server schema's defaults and the restore proceeds. A handler that required the source
///         would refuse the one restore that matters.
///     </para>
///     <para>
///         ⚠
///         <b>
///             It writes nothing to the cluster itself any more — #30.
///         </b> The first cut applied a CloudNativePG <c>Cluster</c> through <see cref="KubeCommand" />
///         under the vault's labels. Now it hands a server body to
///         <see cref="ActionContext.Creator" />, which the manager bound to this request's caller:
///         the server is created through the whole write path, as that caller's <c>PUT</c>, and its
///         own reconciler renders the <c>Cluster</c>. What the caller gets back is the new server's
///         id and the create's operation.
///     </para>
///     <para>
///         ⚠ <b>Four checks here, and the caller is checked twice by the manager.</b> Once for
///         <see cref="RecoveryVaults.RecoverPermission" /> on the vault, and again, by the create, for
///         the server type's <c>write</c> in this group. What nobody asks is whether the caller may
///         read the protected server the point was taken from — the vault's
///         <c>recover-is-gated-by-the-vault-alone</c>, narrowed to that.
///     </para>
/// </remarks>
public sealed class RecoveryVaultRecoverHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => RecoveryVaults.Type;

    /// <inheritdoc />
    public string Action => RecoveryVaults.RecoverAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a restore is a Cluster object in the "
                + "cluster. The type declares RequiresCluster, so ActionDispatcher should have refused "
                + "this call."
            );
        }

        var recoveryPoint = Text(context.Body, "recoveryPoint");
        var targetName = Text(context.Body, "targetName");

        if (recoveryPoint.Length == 0 || targetName.Length == 0) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                "A restore names the recovery point to restore and the name of the new cluster to restore "
                + "it into; one of the two is missing.",
                recoveryPoint.Length == 0 ? "/recoveryPoint" : "/targetName"
            );
        }

        // ── The point, and that it is this vault's ─────────────────────────────────────────────
        var backup = await cluster.GetAsync(
            RecoveryVaults.BackupRef(context.Namespace, recoveryPoint),
            cancellationToken
        );

        if (backup.TryGetError(out var backupError)) {
            return backupError.Code == ErrorCode.ResourceNotFound
                ? NotAPointOfThisVault(recoveryPoint)
                : Result<string>.Failure(backupError);
        }

        var backupJson = backup.GetValueOrThrow().Json;
        var parent = RecoveryVaults.BackupParentOf(backupJson);

        var schedules = await cluster.ListAsync(
            RecoveryVaults.ScheduledBackupKind,
            context.Namespace,
            RecoveryVaults.Selector(context.Id.Id),
            cancellationToken
        );

        if (schedules.TryGetError(out var scheduleError)) {
            return Result<string>.Failure(scheduleError);
        }

        var schedule = schedules.GetValueOrThrow()
            .FirstOrDefault(x => string.Equals(x.Name, parent, StringComparison.Ordinal));

        if (parent.Length == 0 || schedule is null) {
            // ⚠ The same answer as "no such Backup": a vault that said "that point belongs to another
            // vault" would confirm the point exists to a caller who may read this vault and not that one.
            return NotAPointOfThisVault(recoveryPoint);
        }

        var item = schedule.Labels.TryGetValue(RecoveryVaults.ProtectedItemLabel, out var labelled)
            ? labelled
            : schedule.Name;

        if (RecoveryVaults.RecoveryPointOf(item, backupJson) is not { } point) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{recoveryPoint}' is not a Backup document this platform can read."
            );
        }

        if (!point.IsCompleted) {
            return Result<string>.Failure(
                ErrorCode.PreconditionFailed,
                $"Recovery point '{recoveryPoint}' of '{item}' is '{point.Phase}', not '{RecoveryVaults.CompletedPhase}'"
                + (point.Error.Length > 0 ? $": {point.Error}" : string.Empty)
                + ". A restore bootstraps from a completed point only; listRecoveryPoints says which those are.",
                "/recoveryPoint"
            );
        }

        // ── The target must not exist. A restore never overwrites. ─────────────────────────────
        var target = RecoveryVaults.ClusterRef(context.Namespace, targetName);
        var existing = await cluster.GetAsync(target, cancellationToken);

        if (existing.IsSuccess) {
            return Result<string>.Failure(
                ErrorCode.ResourceAlreadyExists,
                $"A cluster named '{targetName}' already exists in this resource group. A restore creates a "
                + "new cluster and never writes into one that is there — choose another name.",
                "/targetName"
            );
        }

        if (existing.Error!.Code != ErrorCode.ResourceNotFound) {
            return Result<string>.Failure(existing.Error);
        }

        // ── The source, for sizing, if it is still there ───────────────────────────────────────
        var sourceName = RecoveryVaults.BackupClusterOf(backupJson);
        var source = sourceName.Length > 0
            ? await cluster.GetAsync(RecoveryVaults.ClusterRef(context.Namespace, sourceName), cancellationToken)
            : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, "the Backup names no cluster");

        var sourceJson = source.IsSuccess ? source.GetValueOrThrow().Json : "{}";

        var pooler = sourceName.Length > 0
            ? await cluster.GetAsync(
                new() { Kind = RecoveryVaults.PoolerKind, Namespace = context.Namespace, Name = sourceName + "-pooler" },
                cancellationToken
            )
            : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, "the Backup names no cluster");

        // ── The restore: a server, written as the caller ───────────────────────────────────────
        //
        // ⚠ THROUGH THE WRITE PATH AND NOT THROUGH KubeCommand. The server's own reconciler renders
        // the Cluster, gives it a bucket of its own and a key to it, and meters it; this handler only
        // says which point to start from. A refusal here — the caller may not create a server, the
        // name is taken, the quota is spent — is the write path's own, with its own code.
        var created = await context.Creator.CreateAsync(
            RecoveryVaults.PostgresServerType,
            targetName,
            RecoveryVaults.PostgresServerApiVersion,
            RecoveryVaults.RestoredServerBody(recoveryPoint, context.Desired, sourceJson, pooler.IsSuccess),
            cancellationToken
        );

        if (created.TryGetError(out var createError)) {
            return Result<string>.Failure(createError);
        }

        var sourcePath = new ResourceId(
            context.Id.TenantId,
            context.Id.SubscriptionId,
            context.Id.ResourceGroup,
            RecoveryVaults.PostgresServerType,
            item,
            Guid.Empty
        ).Path;

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it.
        return Result<string>.Success(
            new JsonObject {
                ["kind"] = RecoveryVaults.PostgresServerType.ToString(),
                ["name"] = targetName,
                ["namespace"] = context.Namespace,
                ["recoveryPoint"] = recoveryPoint,
                ["source"] = sourcePath,
                ["resourceId"] = created.GetValueOrThrow().Id.Path,
                ["operationId"] = created.GetValueOrThrow().OperationId.ToString("D", CultureInfo.InvariantCulture)
            }.ToJsonString()
        );
    }

    static Result<string> NotAPointOfThisVault(string recoveryPoint) =>
        Result<string>.Failure(
            ErrorCode.ResourceNotFound,
            $"'{recoveryPoint}' is not a recovery point of this vault. listRecoveryPoints names the ones that are.",
            "/recoveryPoint"
        );

    static string Text(JsonElement body, string name) =>
        body.ValueKind is JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
