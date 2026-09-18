// ⚠ For `Result`, `Result<T>` and `Error`. The `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.

using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices;

/// <summary>
///     Converges one vault onto a CloudNativePG <c>ScheduledBackup</c> per protected item, each in
///     the protected server's namespace under the vault's own id, and prunes the recovery points
///     the vault's retention no longer covers.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST RECONCILER THAT READS THROUGH <see cref="ReconcileContext.View" />, AND
///         EVERY ANSWER IT ACTS ON IS ONE THE VIEW GAVE.</b> For each item it asks the view twice:
///         <see cref="IResourceView.ReadAsync" /> for the server's contract — its cluster id, its
///         provisioning state and the two backup pointers <see cref="RecoveryVaults.ServerBackupContract" />
///         reads — and <see cref="IResourceView.RenderedObjectsAsync" /> for the <i>address</i> of the
///         CloudNativePG <c>Cluster</c> the server's provider rendered. It never predicts that name.
///         <c>PostgresServers.ObjectNameOf</c> happens to be the server's own name today, and a vault
///         that assumed so would be bound to another provider's naming through a coincidence the
///         Hard rule exists to make impossible to rely on.
///     </para>
///     <para>
///         ⚠ <b>A refused item FAILS the pass, at the item's pointer, and does not converge around
///         it.</b> docs/plan/15 § Backup as a service: <i>"a backup system nobody can see the status
///         of is a backup system that is quietly broken"</i>. A vault that reported
///         <c>Succeeded</c> while one of its items was another tenant's, deleted, ungranted, on
///         another cluster, or a server with backups off would be exactly that. The failure names the
///         index — <c>/properties/protectedItems/{i}</c> — so the portal highlights the row, and the
///         message says what to do: grant the vault <c>reader</c> on the item, or take the item out.
///         ⚠ The view answers one <c>ResourceNotFound</c> for "does not exist", "another tenant" and
///         "not granted", deliberately; the message here says all three, because the vault cannot
///         tell them apart either and a message that guessed would be wrong two times in three.
///     </para>
///     <para>
///         ⚠ <b>Retention is enforced on passes, and a converged vault has no pass of its own.</b>
///         The vault prunes every <c>Backup</c> older than <c>policy.retentionDays</c> on each pass —
///         a <c>PUT</c>, a restore, the drift scan. What it does not have is a pass at 02:00 each
///         night: docs/plan/08 § What the resource manager deliberately does not do records the
///         manager-started pass as owed, and until it exists a recovery point can outlive its
///         retention by however long the vault goes untouched. The <i>bytes</i> are bounded
///         regardless, by the server's own <c>backup.retentionDays</c>, which the vault refuses to
///         exceed. Recorded at <c>charts/managed/recovery-vault/conformance.yaml § owed</c>,
///         <c>retention-is-enforced-on-passes</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="IResourceWatch.SubscribeAsync" /> is called on every pass and a refusal
///         does not fail it.</b> The watch is how the next pass learns which servers changed; a
///         vault whose subscription could not be recorded still protects what it protects, and the
///         drift scan still finds a Cluster that moved. A context built by hand carries
///         <c>RefusingResourceWatch</c>, so a pass driven by a test reports the refusal and goes on —
///         the alternative, failing, would make every reconciler test a test of the harness's
///         wiring.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Every ScheduledBackup is a pure function of the address, the body
///             and the cluster name the view returned. The prune is idempotent by construction — a
///             Backup is deleted once and is then not there to be listed.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which the prune measures retention against.
///             <c>RecoveryVaultReconcilerTests.TheReconcilerHoldsNoMutableState</c> asserts it.
///         </item>
///         <item>
///             <b>Bounded.</b> Two view calls, one apply, one read, one listing and at most one
///             delete per recovery point, per item, over at most
///             <see cref="RecoveryVaults.MaxProtectedItems" /> items, on the caller's token.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every ScheduledBackup and a <see cref="RecoveryVaults.Matches" />
///             on each, never the apply's own result.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" /> and measures retention.</param>
public sealed class RecoveryVaultReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => RecoveryVaults.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a vault's schedules are objects in "
                + "a cluster. CyberCloud.RecoveryServices/vaults declares RequiresCluster, so the driver "
                + "should have refused this pass — see ReconcileDriver."
            );
        }

        // ── The watch: idempotent, every pass, never fatal ─────────────────────────────────────
        var subscribed = await context.Watch.SubscribeAsync(RecoveryVaults.PostgresServerType, cancellationToken);
        if (subscribed.TryGetError(out var watchError)) {
            context.Log.Report("watch", $"could not subscribe to {RecoveryVaults.PostgresServerType} changes: {watchError.Message}");
        }

        if (context.ChangesDropped > 0) {
            // ⚠ The list is a hint about where to look, never the only record — IResourceWatch's
            // remarks. This reconciler rescans every item on every pass anyway, so a dropped event
            // costs nothing here; it is reported so the operation's progress says it happened.
            context.Log.Report("watch", $"{context.ChangesDropped} change event(s) were dropped since the last pass; every item is re-read regardless");
        }

        var paths = RecoveryVaults.ProtectedItemPaths(context.Desired);

        if (paths.Length > RecoveryVaults.MaxProtectedItems) {
            return ReconcileOutcome.Failed(
                new Error(
                    ErrorCode.InvalidRequestBody,
                    $"'{context.Id.Path}' names {paths.Length} protected items and a vault protects at most "
                    + $"{RecoveryVaults.MaxProtectedItems}: each costs two cross-resource reads and a namespace "
                    + "listing inside the reconciler's budget. Split them across vaults.",
                    RecoveryVaults.ItemPointer(RecoveryVaults.MaxProtectedItems)
                )
            );
        }

        var retentionDays = RecoveryVaults.RetentionDays(context.Desired);
        var kept = new List<(ResourceId Item, string Cluster)>();

        for (var index = 0; index < paths.Length; index++) {
            var resolved = await ResolveAsync(context, cluster, index, paths[index], retentionDays, cancellationToken);

            if (resolved.TryGetError(out var refused)) {
                return Refused(refused);
            }

            kept.Add(resolved.GetValueOrThrow());
        }

        // ── The schedules, one per item, and the prune beside each ─────────────────────────────
        for (var index = 0; index < kept.Count; index++) {
            var (item, clusterName) = kept[index];

            context.Log.Report(
                "applying",
                $"scheduling backups of '{item.Name}' ({clusterName}) for vault '{context.Id.Name}'",
                20 + (60 * index) / Math.Max(kept.Count, 1)
            );

            var applied = await Apply(context, cluster, item.Name, RecoveryVaults.ScheduledBackupJson(context.Id.Name, item.Name, clusterName, context.Desired))
                .ApplyAsync(cancellationToken);

            if (applied.TryGetError(out var applyError)) {
                return ReconcileOutcome.FromFailure(applyError);
            }

            if (Unfinished(context, applied.GetValueOrThrow(), $"the schedule for '{item.Name}'") is { } stalled) {
                return stalled;
            }

            var pruned = await PruneAsync(context, cluster, item.Name, retentionDays, cancellationToken);
            if (pruned.TryGetError(out var pruneError)) {
                return ReconcileOutcome.FromFailure(pruneError);
            }
        }

        // ── The schedules of items that left the body ───────────────────────────────────────────
        var orphaned = await RemoveUnlistedAsync(context, cluster, [.. kept.Select(x => x.Item.Name)], cancellationToken);
        if (orphaned.TryGetError(out var orphanError)) {
            return ReconcileOutcome.FromFailure(orphanError);
        }

        // ── Clause 4. Everything above this line is a claim; these are the readings. ────────────
        foreach (var (item, clusterName) in kept) {
            var target = RecoveryVaults.ScheduledBackupRef(context.Namespace, context.Id.Name, item.Name);
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress($"'{target}' was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!RecoveryVaults.Matches(read.GetValueOrThrow().Json, clusterName, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired schedule",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report(
            "ready",
            kept.Count == 0
                ? $"vault '{context.Id.Name}' protects nothing yet; add protected items and grant the vault reader on each"
                : $"vault '{context.Id.Name}' schedules backups of {kept.Count} protected item(s)",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ScheduledBackups and nothing else, by the vault's own labels.</b> Every
    ///         recovery point carries an owner reference to its ScheduledBackup
    ///         (<see cref="RecoveryVaults.BackupOwnerReference" />), so the garbage collector removes
    ///         the Backups when the schedule goes; the vault does not enumerate them itself. A restored
    ///         cluster carries the vault's labels too and is <b>not</b> deleted — it is a database the
    ///         tenant just recovered, and it is left standing under
    ///         <see cref="RecoveryVaults.RestoreRoleLabel" /> for the reason that label's remarks give.
    ///     </para>
    ///     <para>
    ///         ⚠ Listed rather than derived from the body: a vault whose last body named two items may
    ///         have three schedules on the cluster from a body before that, and a teardown that
    ///         trusted the body would leave the third. The listing fails closed — a connection that
    ///         cannot list answers a failure, and a failure leaves the objects standing rather than
    ///         reporting a teardown that did not happen.
    ///     </para>
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var listed = await cluster.ListAsync(
            RecoveryVaults.ScheduledBackupKind,
            context.Namespace,
            RecoveryVaults.Selector(context.Id.Id),
            cancellationToken
        );

        if (listed.TryGetError(out var listError)) {
            return ReconcileOutcome.FromFailure(listError);
        }

        var schedules = listed.GetValueOrThrow();

        foreach (var schedule in schedules) {
            context.Log.Report("deleting", $"deleting schedule '{schedule.Name}' of vault '{context.Id.Name}'");

            var deleted = await Apply(context, cluster, ItemOfLabel(schedule), Placeholder(schedule.Name))
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        var remaining = await cluster.ListAsync(
            RecoveryVaults.ScheduledBackupKind,
            context.Namespace,
            RecoveryVaults.Selector(context.Id.Id),
            cancellationToken
        );

        if (remaining.TryGetError(out var remainingError)) {
            return ReconcileOutcome.FromFailure(remainingError);
        }

        if (remaining.GetValueOrThrow().Count > 0) {
            return ReconcileOutcome.InProgress(
                $"{remaining.GetValueOrThrow().Count} schedule(s) of vault '{context.Id.Name}' are still readable",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("deleted", $"the schedules of '{context.Id.Name}' are gone; their recovery points follow by owner reference", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Exists when at least one schedule is on the cluster. A vault that protects nothing has
    ///     nothing to observe and reports absent with a summary that says why — which is a true
    ///     statement about it, not a drift.
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var listed = await cluster.ListAsync(
            RecoveryVaults.ScheduledBackupKind,
            context.Namespace,
            RecoveryVaults.Selector(context.Id.Id),
            cancellationToken
        );

        if (listed.TryGetError(out _) || listed.GetValueOrThrow().Count == 0) {
            return new() {
                Exists = false,
                ObservedAt = clock.UtcNow,
                Summary = RecoveryVaults.ProtectedItemPaths(context.Desired).Length == 0
                    ? "the vault protects nothing, so it has no schedule on the cluster"
                    : "the vault's schedules are absent"
            };
        }

        var schedules = listed.GetValueOrThrow();
        var expected = RecoveryVaults.ProtectedItemPaths(context.Desired).Length;

        return new() {
            Exists = true,
            Json = new JsonObject {
                ["schedules"] = new JsonArray([.. schedules.Select(x => (JsonNode?)x.Name)])
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Summary = schedules.Count == expected
                ? $"{schedules.Count} schedule(s), one per protected item"
                : $"{schedules.Count} schedule(s) on the cluster and {expected} protected item(s) in the body"
        };
    }

    // ── One item, through the view ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Turns one protected item path into the CloudNativePG cluster it renders, or into the
    ///     outcome that refuses it.
    /// </summary>
    static async Task<Result<(ResourceId Item, string Cluster)>> ResolveAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        int index,
        string path,
        int retentionDays,
        CancellationToken cancellationToken
    ) {
        var pointer = RecoveryVaults.ItemPointer(index);

        var parsed = RecoveryVaults.ItemOf(context.Id, index, path);
        if (parsed.TryGetError(out var parseError)) {
            return Refuse(parseError);
        }

        var item = parsed.GetValueOrThrow();

        // ── The contract, as the gateway would return it to the vault ──────────────────────────
        var read = await context.View.ReadAsync(item, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? Refuse(
                    new Error(
                        ErrorCode.ResourceNotFound,
                        $"'{path}' does not exist, or vault '{context.Id.Name}' has not been granted read on it. "
                        + "A vault reads what it protects as itself — grant resource:"
                        + context.Id.Id.ToString("N", CultureInfo.InvariantCulture)
                        + " the reader role on the server or on its resource group, then PUT the vault again "
                        + "(docs/plan/08 § What the resource manager deliberately does not do).",
                        pointer
                    )
                )
                : Result<(ResourceId, string)>.Failure(readError);
        }

        var snapshot = read.GetValueOrThrow();

        if (snapshot.ClusterId != cluster.ClusterId) {
            return Refuse(
                new Error(
                    ErrorCode.InvalidRequestBody,
                    $"'{path}' is placed on cluster {snapshot.ClusterId:D} and the vault on {cluster.ClusterId:D}. A "
                    + "vault schedules backups through its own cluster connection, so it protects resources on "
                    + "its own cluster only.",
                    pointer
                )
            );
        }

        var (enabled, serverRetention) = RecoveryVaults.ServerBackupContract(snapshot.Body);

        if (!enabled) {
            return Refuse(
                new Error(
                    ErrorCode.InvalidRequestBody,
                    $"'{path}' has backups disabled (backup.enabled is false), so its Cluster carries no backup "
                    + "section and CloudNativePG refuses every Backup of it with \"cannot proceed with the "
                    + "backup as the cluster has no backup section\". Enable backups on the server first.",
                    pointer
                )
            );
        }

        if (serverRetention < retentionDays) {
            return Refuse(
                new Error(
                    ErrorCode.InvalidRequestBody,
                    $"'{path}' keeps base backups for {serverRetention} day(s) and the vault's policy.retentionDays "
                    + $"is {retentionDays}. The bytes behind a recovery point live in the server's own store under "
                    + "the server's retention, so a vault that promised more would list points barman had already "
                    + "expired. Lower the vault's retention or raise the server's.",
                    pointer
                )
            );
        }

        // ── The address of the Cluster the server's provider rendered ──────────────────────────
        var rendered = await context.View.RenderedObjectsAsync(item, cancellationToken);
        if (rendered.TryGetError(out var renderedError)) {
            return Result<(ResourceId, string)>.Failure(renderedError);
        }

        var clusterObject = rendered.GetValueOrThrow()
            .FirstOrDefault(x => x.Kind.Group == RecoveryVaults.ClusterKind.Group && x.Kind.Kind == RecoveryVaults.ClusterKind.Kind);

        if (clusterObject is null) {
            // ⚠ InProgress and not a refusal: a server that is Creating has not rendered yet, and one
            // whose Cluster was removed behind its back gets it put back by its own drift scan. The
            // body is not wrong in either case; the world is not ready.
            return Result<(ResourceId, string)>.Failure(
                ErrorCode.OperationInProgress,
                $"'{path}' ({snapshot.ProvisioningState}) has not rendered a {RecoveryVaults.ClusterKind.Group} "
                + $"{RecoveryVaults.ClusterKind.Kind} yet, so there is nothing to schedule a backup of. The vault "
                + "will try again.",
                pointer
            );
        }

        if (!string.Equals(clusterObject.Namespace, context.Namespace, StringComparison.Ordinal)) {
            return Refuse(
                new Error(
                    ErrorCode.InvalidRequestBody,
                    $"'{path}' renders into namespace '{clusterObject.Namespace}' and the vault into "
                    + $"'{context.Namespace}'. A vault protects resources in its own resource group.",
                    pointer
                )
            );
        }

        return Result<(ResourceId, string)>.Success((item, clusterObject.Name));

        static Result<(ResourceId, string)> Refuse(Error error) => Result<(ResourceId, string)>.Failure(error);
    }

    /// <summary>Turns a failed resolution into the pass's outcome, with the right retryability for each code.</summary>
    static ReconcileOutcome Refused(Error error) =>
        error.Code == ErrorCode.OperationInProgress
            ? ReconcileOutcome.InProgress(error.Message, TimeSpan.FromSeconds(15))
            : ReconcileOutcome.FromFailure(error);

    // ── Retention ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Deletes every recovery point of one item that is older than the vault's retention.</summary>
    /// <remarks>
    ///     ⚠ Reads each candidate before deciding: a listing carries labels and names, and retention
    ///     is measured off <c>status.stoppedAt</c>, which only the object carries. Bounded by the
    ///     number of points the schedule has produced within the retention window plus the expired
    ///     ones this pass removes.
    /// </remarks>
    async Task<Result> PruneAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        string item,
        int retentionDays,
        CancellationToken cancellationToken
    ) {
        var scheduledBackup = RecoveryVaults.ScheduledBackupNameOf(context.Id.Name, item);

        var listed = await cluster.ListAsync(
            RecoveryVaults.BackupKind,
            context.Namespace,
            RecoveryVaults.RecoveryPointSelector(scheduledBackup),
            cancellationToken
        );

        if (listed.TryGetError(out var listError)) {
            return Result.Failure(listError);
        }

        var now = clock.UtcNow;

        foreach (var summary in listed.GetValueOrThrow()) {
            var read = await cluster.GetAsync(RecoveryVaults.BackupRef(context.Namespace, summary.Name), cancellationToken);
            if (read.TryGetError(out var readError)) {
                if (readError.Code == ErrorCode.ResourceNotFound) {
                    continue;
                }

                return Result.Failure(readError);
            }

            if (RecoveryVaults.RecoveryPointOf(item, read.GetValueOrThrow().Json) is not { } point
                || !RecoveryVaults.IsExpired(point, retentionDays, now)) {
                continue;
            }

            context.Log.Report("pruning", $"recovery point '{point.Name}' of '{item}' is older than {retentionDays} day(s) and is being removed");

            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(RecoveryVaults.BackupKind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(Placeholder(point.Name))
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return Result.Failure(deleteError);
            }
        }

        return Result.Success;
    }

    /// <summary>Deletes the schedules whose item is no longer in the body.</summary>
    static async Task<Result> RemoveUnlistedAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        ImmutableArray<string> items,
        CancellationToken cancellationToken
    ) {
        var listed = await cluster.ListAsync(
            RecoveryVaults.ScheduledBackupKind,
            context.Namespace,
            RecoveryVaults.Selector(context.Id.Id),
            cancellationToken
        );

        if (listed.TryGetError(out var listError)) {
            return Result.Failure(listError);
        }

        foreach (var schedule in listed.GetValueOrThrow()) {
            var item = ItemOfLabel(schedule);
            if (items.Contains(item, StringComparer.Ordinal)) {
                continue;
            }

            context.Log.Report("deleting", $"'{item}' left the vault's protected items; deleting schedule '{schedule.Name}'");

            var deleted = await Apply(context, cluster, item, Placeholder(schedule.Name))
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return Result.Failure(deleteError);
            }
        }

        return Result.Success;
    }

    static string ItemOfLabel(KubeObjectSummary schedule) =>
        schedule.Labels.TryGetValue(RecoveryVaults.ProtectedItemLabel, out var item) ? item : schedule.Name;

    /// <summary>A command over one of this vault's schedules, carrying the item label.</summary>
    static IKubeCommandBuilder Apply(ReconcileContext context, IKubeClusterConnection cluster, string item, string json) =>
        KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(RecoveryVaults.ScheduledBackupKind)
            .WithApiVersion(context.ApiVersion)
            .WithLabels((RecoveryVaults.ProtectedItemLabel, item))
            .ObjectJson(json);

    /// <summary>The smallest object a delete command will accept — a name and nothing else.</summary>
    static string Placeholder(string name) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } }.ToJsonString();

    /// <summary>
    ///     Turns an apply that did not land into the outcome that comes back for it, or
    ///     <see langword="null" /> when it landed.
    /// </summary>
    static ReconcileOutcome? Unfinished(ReconcileContext context, ApplyOutcome outcome, string what) {
        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                // ⚠ `.spec.schedule` is the plausible one: a tenant editing the cron by hand on the
                // object rather than on the vault, and forcing would undo them every pass.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe() ?? $"another field manager owns part of {what} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }
}
