using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL;

/// <summary>
///     Converges one server onto a CloudNativePG <c>Cluster</c> and, while pooling is on, a
///     <c>Pooler</c>.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both applies are server-side, so a second pass with the same body is
///             an <c>Unchanged</c>. Nothing here counts, appends or timestamps, and the pooler
///             teardown below reads before it deletes so a converged pass with pooling off does no
///             work at all.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ This matters more
///             here than it did for the sample: a reconciler is registered
///             <b>
///                 as a singleton, by
///                 concrete type
///             </b> (<c>AddCyberCloudProvider</c>'s remarks), so one instance serves
///             every tenant in the process — a field caching, say, the last rendered
///             <c>Cluster</c> would hand tenant B tenant A's spec, and a single-tenant test could not
///             see it.
///         </item>
///         <item>
///             <b>Bounded.</b> In the steady state, at most three applies, four reads and one
///             conditional delete, all on the caller's token — and, with backups on, one bucket
///             <c>PUT</c> that answers "already there" and two vault reads; a restored server adds one read
///             of its restore <c>Secret</c> (and of the point, on its first pass), two vault reads and one
///             apply; a teardown that ends the server adds two deletes and two reads for the key
///             <c>Secret</c>s; a teardown or a re-creation over retained claims adds one
///             list and one read, one ownership change and one read-back per claim, which is a
///             handful of small metadata calls rather than a wait. ⚠ There is no wait for the
///             cluster to be <i>ready</i> — a
///             CloudNativePG bootstrap takes minutes and clause 3's budget is thirty seconds, so
///             readiness is reported as <see cref="ReconcileOutcome.InProgress" /> and the reminder
///             comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every object the body implies, never an apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             Converged here means "the CRs are applied and read back", not "PostgreSQL is
///             accepting connections".
///         </b> The honest stronger check is
///         <c>status.conditions[type=Ready]</c> on the <c>Cluster</c>, and it is not made because
///         nothing in this repository can produce that status: the conformance harness is a
///         dictionary (<c>FakeKubeCluster</c>'s own remarks say so) and no operator runs anywhere the
///         suite reaches. A readiness gate written against a world that never sets the condition would
///         make every resource in every test hang for eight passes and then fail — so the check
///         belongs with <c>ClusterBackedConformanceTests</c>, where a real API server does, and
///         <c>charts/managed/postgres/conformance.yaml</c>'s <c>connect-in-cluster</c> assertion is
///         where it is written down as owed.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///             never forced.
///         </b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite a tenant's own controller, and on this type that
///         controller is plausibly CloudNativePG itself editing a field it owns.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE CLAIMS CHANGE HANDS ON EVERY TEARDOWN AND EVERY RE-CREATION, AND THAT IS WHAT
///             MAKES THE SEVEN-DAY WINDOW REAL — issue #69.
///         </b> CloudNativePG creates each instance's <c>PersistentVolumeClaim</c>s itself and
///         stamps a controller reference on every one, so deleting the <c>Cluster</c> would have
///         Kubernetes garbage-collect the data, WAL and tablespace claims before the recovery
///         window began; a restore came back to an <c>initdb</c>. <see cref="DeleteAsync" /> now
///         pauses the operator, lists the claims by the operator's own <c>cnpg.io/cluster</c>
///         label, clears their owner references through <see cref="VolumeCustody.DetachAsync" />,
///         and only then deletes — so the claims outlive the <c>Cluster</c> exactly as a
///         <c>StatefulSet</c>'s would. <see cref="ReconcileAsync" /> does the reverse when it
///         finds such claims and no <c>Cluster</c>: it creates the <c>Cluster</c> paused, reads
///         its uid back, hands it the claims through <see cref="VolumeCustody.AdoptAsync" />, and
///         un-pauses. The operator then sees claims it controls with no pods, classifies them as
///         dangling, and re-creates the instances over the tenant's data rather than bootstrapping
///         a new primary. <see cref="RetainedVolumesAsync" /> names the same claims so a hard
///         delete or a purge removes them — the reclaim this type used to have nothing for.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What the unit and conformance suites prove, and what only a cluster with the
///             operator installed can.
///         </b> Everything on this side of the API server is exercised: the order of the
///         detach and the delete, the pause around both moves, the adoption naming the uid the
///         API server answered, the guard refusing a claim whose labels disagree, and — in
///         <c>ProviderConformanceTests</c>, whose fake garbage-collects a dependent with its
///         controller — that the claims survive a soft delete and belong to the restored
///         <c>Cluster</c> afterwards. What no suite here can observe is CloudNativePG electing a
///         primary over reattached claims whose status it has never held, because the
///         cluster-backed lane runs a bare k3s with no operator (#2). Read against v1.30.0's
///         <c>ensureInstancesAreCreated</c> and <c>reconcileTargetPrimaryFromPods</c> that path
///         is the ordinary failover one; it is still owed a run, and
///         <c>charts/managed/postgres/conformance.yaml § owed</c> says so.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class PostgresServerReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => PostgresServers.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // Unreachable through the driver, which refuses a RequiresCluster type with no connection
            // and names the type. Kept because a reconciler is also callable directly.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a PostgreSQL server is a "
                + "CloudNativePG Cluster in a cluster. CyberCloud.DBforPostgreSQL/servers declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var pooling = PostgresServers.PoolingEnabled(context.Desired);

        // ── A body the operator's definition would refuse, refused here with the tenant's name for
        //    the field. Before anything is written, so a refused server leaves no half-built Cluster.
        if (PostgresServers.BackupDestinationProblem(context.Desired) is { } backupProblem) {
            return ReconcileOutcome.Failed(
                new Error(ErrorCode.InvalidRequestBody, backupProblem, PostgresServers.BackupDestinationPointer)
            );
        }

        // ⚠ Before the Cluster, not before the Pooler: the Pooler is applied second, and a refusal
        // between the two would leave a server with no pooler behind an operation that failed.
        if (PostgresServers.PoolingModeProblem(context.Desired) is { } poolingProblem) {
            return ReconcileOutcome.Failed(
                new Error(ErrorCode.InvalidRequestBody, poolingProblem, PostgresServers.PoolingModePointer)
            );
        }

        // ── Where the backups go, before the Cluster that archives there ─────────────────────────
        var backups = PostgresServers.BackupEnabled(context.Desired);
        PostgresServers.BackupStore? store = null;

        if (backups) {
            var (backupProblemOutcome, provisioned) = await ProvisionBackupStoreAsync(context, cluster, cancellationToken);

            if (backupProblemOutcome is not null) {
                return backupProblemOutcome;
            }

            store = provisioned;
        }

        // ── A restore: the source's key, as this server's own Secret, before the Cluster names it ─
        var restoring = PostgresServers.RecoveryPoint(context.Desired).Length > 0;
        PostgresServers.RestoreOrigin? origin = null;

        if (restoring) {
            var (restoreProblem, resolved) = await ProvisionRestoreSourceAsync(context, cluster, cancellationToken);

            if (restoreProblem is not null) {
                return restoreProblem;
            }

            origin = resolved;
        }

        // ── The claims a previous life left, handed over before the operator looks ──────────────
        if (await AdoptRetainedClaimsAsync(context, cluster, store, origin, cancellationToken) is { } custodyProblem) {
            return custodyProblem;
        }

        context.Log.Report(
            "applying",
            $"applying the CloudNativePG Cluster '{name}' to {context.Namespace}",
            30
        );

        var applied = await Apply(
            context,
            cluster,
            PostgresServers.ClusterKind,
            PostgresServers.ClusterJson(name, context.Desired, store, origin),
            cancellationToken
        );

        if (applied is { } clusterProblem) {
            return clusterProblem;
        }

        if (pooling) {
            context.Log.Report(
                "applying",
                $"applying the PgBouncer Pooler '{PostgresServers.PoolerName(name)}'",
                60
            );

            var pooler = await Apply(
                context,
                cluster,
                PostgresServers.PoolerKind,
                PostgresServers.PoolerJson(name, context.Desired),
                cancellationToken
            );

            if (pooler is { } poolerProblem) {
                return poolerProblem;
            }
        } else if (await RemovePoolerAsync(context, cluster, cancellationToken) is { } removal) {
            return removal;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        foreach (var target in Targets(context.Namespace, name, pooling, backups, restoring)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            // The key's Secret is read back for existence and nothing more: its value is the vault's,
            // and comparing it here would put the secret into a comparison nothing needs.
            if (target.Kind == PostgresServers.SecretKind) {
                continue;
            }

            var stored = read.GetValueOrThrow().Json;

            if (!PostgresServers.Matches(stored, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }

            // ⚠ A paused Cluster matches its spec and is not a server. The un-pausing apply above
            // is a server-side apply that drops the annotation because this manager owned it; until
            // the read-back agrees, the operator is still waiting and nothing is coming up.
            if (target.Kind == PostgresServers.ClusterKind && PostgresServers.IsPaused(stored)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' still carries {PostgresServers.PauseAnnotation}={PostgresServers.PausedValue}, "
                    + "so the operator has not been released to reconcile it",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report(
            "ready",
            pooling
                ? $"the Cluster '{name}' and its Pooler read back as desired"
                : $"the Cluster '{name}' reads back as desired",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate — a
            // teardown with no cluster to reach has nothing left to remove, and failing would park the
            // resource in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"deleting the CloudNativePG objects of '{name}'");

        // ── Custody first: the claims must not belong to the object about to go ─────────────────
        if (await DetachRetainedClaimsAsync(context, cluster, cancellationToken) is { } custodyProblem) {
            return custodyProblem;
        }

        // ⚠ The Pooler first. It references the Cluster by name, and removing the referent before the
        // referrer leaves the operator reconciling a Pooler whose cluster is gone — which is noise in
        // the tenant's own event stream for as long as the two deletes are apart.
        foreach (var (kind, json) in new[] {
                     (PostgresServers.PoolerKind, PostgresServers.PoolerJson(name, context.Desired)),
                     (PostgresServers.ClusterKind, PostgresServers.ClusterJson(name, context.Desired))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        // ⚠ Converged once the objects are GONE, read back — not once the deletes were issued. Same
        // clause, other direction: believing a delete is how a resource stops being billed while its
        // pods are still running.
        if (await GoneAsync(cluster, Targets(context.Namespace, name, true, false, false), cancellationToken) is { } standing) {
            return standing;
        }

        // ── The key Secrets: kept by a park, removed by every teardown that ends the server ──────
        //
        // ⚠ #30'S RECLAIM. These used to stay on EVERY teardown, because a restore bootstrapped from
        // `bootstrap.recovery.backup` and CloudNativePG reads that point's credentials from the Secret
        // its status names — this server's {name}-backup-s3. So a hard-deleted or purged server left
        // a platform-written object in its namespace, and NamespaceReclaim (docs/plan/08) refused its
        // resource group forever. A restore now reads the source's key from the VAULT, where this
        // teardown leaves it, and renders it as the restored server's own Secret — so nothing a
        // restore needs lives here any more. What still outlives the server — the bucket, the store
        // identity, the vault path — is charts/managed/postgres/conformance.yaml § owed,
        // `the-backups-outlive-the-server-and-nothing-reclaims-them`.
        //
        // ⚠ A PARKING PASS KEEPS THEM, for the reason it keeps the claims: the soft delete's window
        // brings back the server as it was, and the restored Cluster names both. The purge drives
        // this teardown again with Parking false, which is the pass that removes them.
        //
        // ⚠ AFTER THE CLUSTER READS AS GONE, NOT BESIDE IT. An instance shutting down may still be
        // archiving its last segments; the recovery points are complete without them — a base backup
        // waits for its own WAL — so this is order for tidiness, not a wait on the pods.
        if (!context.Parking) {
            foreach (var secret in KeySecrets(context.Namespace, name)) {
                var deleted = await KubeCommand.For(cluster)
                    .WithTenantId(context.Id.TenantId)
                    .WithResourceId(context.Id)
                    .InNamespace(context.Namespace)
                    .WithKind(PostgresServers.SecretKind)
                    .WithApiVersion(context.ApiVersion)
                    .ObjectJson(new JsonObject { ["metadata"] = new JsonObject { ["name"] = secret.Name } }.ToJsonString())
                    .DeleteAsync(CascadePolicy.Background, cancellationToken);

                if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                    return ReconcileOutcome.FromFailure(deleteError);
                }
            }

            if (await GoneAsync(cluster, KeySecrets(context.Namespace, name), cancellationToken) is { } secretStanding) {
                return secretStanding;
            }
        }

        // ⚠ AND THE DATA STAYS, BECAUSE THE CLAIMS WERE DETACHED BEFORE THE CLUSTER WENT. This used
        // to say the opposite — that CloudNativePG's owner references took the claims with the
        // Cluster and there was therefore nothing for RetainedVolumesAsync to name — and that was
        // true, and it was issue #69: the same teardown runs on a soft delete, so the window's
        // restore came back to an initdb. DetachRetainedClaimsAsync above is what changed.
        context.Log.Report("deleted", $"the CloudNativePG objects of '{name}' are gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <summary>The two Secrets a server's keys are rendered into — its own, and a restore's copy of its source's.</summary>
    static IEnumerable<ObjectRef> KeySecrets(string ns, string name) {
        yield return PostgresServers.BackupSecretRef(ns, name);
        yield return PostgresServers.RestoreSecretRef(ns, name);
    }

    /// <summary>
    ///     Reads each target back and answers <see langword="null" /> once every one is gone, or the
    ///     outcome to return from the pass.
    /// </summary>
    static async Task<ReconcileOutcome?> GoneAsync(
        IKubeClusterConnection cluster,
        IEnumerable<ObjectRef> targets,
        CancellationToken cancellationToken
    ) {
        foreach (var target in targets) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Listed, not predicted, and the difference is a failover.
    ///         </b> A <c>StatefulSet</c>'s claims are <c>{volume}-{set}-{ordinal}</c> and a
    ///         provider can count them off the desired body. CloudNativePG's are
    ///         <c>{name}-{serial}</c>, <c>{name}-{serial}-wal</c> and <c>{name}-{serial}-tbs-*</c>,
    ///         where the serial is advanced by the operator every time it creates an instance —
    ///         so a two-instance server that has failed over once holds <c>main-1</c> and
    ///         <c>main-3</c>, and a reclaim that predicted <c>main-2</c> would remove nothing and
    ///         leave a disk. The operator labels every one with <c>cnpg.io/cluster={name}</c>, and
    ///         that label is both the selector and the evidence <c>VolumeReclaimer</c> re-checks on
    ///         the stored object.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Fails without a cluster connection rather than answering nothing.</b> An empty
    ///         array means "this type keeps nothing", and the reclaimer converges on it. This type
    ///         keeps every claim its teardown detached, so a pass that cannot ask has to say so.
    ///     </para>
    /// </remarks>
    public Task<Result<ImmutableArray<RetainedVolume>>> RetainedVolumesAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Task.FromResult(
                Result<ImmutableArray<RetainedVolume>>.Failure(
                    ErrorCode.InternalError,
                    $"'{context.Id.Path}' has no cluster connection, so the claims its teardown kept "
                    + "cannot be listed. An empty answer would converge the reclaim over disks that "
                    + "are still there."
                )
            );
        }

        return ClaimsAsync(context, cluster, cancellationToken);
    }

    /// <summary>
    ///     The claims CloudNativePG created for this server, as the API server lists them under the
    ///     operator's own <c>cnpg.io/cluster</c> label.
    /// </summary>
    static async Task<Result<ImmutableArray<RetainedVolume>>> ClaimsAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var name = context.Id.Name;

        var listed = await cluster.ListAsync(
            RetainedVolume.ClaimKind,
            context.Namespace,
            PostgresServers.ClaimSelector(name),
            cancellationToken
        );

        if (listed.TryGetError(out var listError)) {
            return Result<ImmutableArray<RetainedVolume>>.Failure(listError);
        }

        return Result<ImmutableArray<RetainedVolume>>.Success(
            RetainedVolume.Listed(
                listed.GetValueOrThrow(),
                context.Namespace,
                PostgresServers.ClaimOwnership(name),
                "the instance's data, WAL or tablespace volume"
            )
        );
    }

    /// <summary>
    ///     On a teardown: pauses the operator, lists the server's claims, and takes the
    ///     <c>Cluster</c>'s controller reference off every one of them.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when the claims are detached or there are none, or the outcome to
    ///     return from the pass.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The order is the whole fix.</b> Kubernetes garbage-collects a dependent when its
    ///         controller goes, and it does not wait: a detach issued after the <c>Cluster</c> delete
    ///         would race the collector for every claim and lose some. So this runs before the first
    ///         delete is issued, and a failure here stops the teardown with the <c>Cluster</c> still
    ///         standing — the resource stays <c>Deleting</c> with the reason on it, which is the
    ///         actionable outcome docs/plan/06 § Two-phase create asks for, rather than a converged
    ///         delete whose window is empty.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The pause is courtesy and the detach is correctness, so a conflict on the pause
    ///             is logged and not obeyed.
    ///         </b> Between the detach and the delete, an operator pass
    ///         would see a cluster whose claims it cannot find and register it unrecoverable — a
    ///         status line on an object that is gone a moment later. The pause spares the tenant's
    ///         event stream that line. But the pause is a server-side apply of the whole
    ///         <c>Cluster</c>, and a field the tenant's own controller has taken makes that apply
    ///         conflict; a teardown that waited on a conflict would be a delete the tenant cannot
    ///         perform. An unreachable cluster is different: nothing here can proceed, so it is
    ///         reported and retried as every apply is.
    ///     </para>
    ///     <para>
    ///         Idempotent by construction: a second pass finds the <c>Cluster</c> already paused or
    ///         already gone, and <see cref="VolumeCustody.DetachAsync" /> reports zero for claims
    ///         that carry no owner.
    ///     </para>
    /// </remarks>
    static async Task<ReconcileOutcome?> DetachRetainedClaimsAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var name = context.Id.Name;
        var existing = await cluster.GetAsync(PostgresServers.ClusterRef(context.Namespace, name), cancellationToken);

        if (existing.TryGetError(out var readError)) {
            // Already gone, so its claims already belong to nobody — a previous pass did this, or the
            // Cluster never existed. Nothing to detach; the deletes below read back and converge.
            return readError.Code == ErrorCode.ResourceNotFound ? null : ReconcileOutcome.FromFailure(readError);
        }

        var stored = existing.GetValueOrThrow().Json;

        if (!PostgresServers.IsPaused(stored)) {
            context.Log.Report(
                "pausing",
                $"asking CloudNativePG to leave '{name}' alone while its claims change hands"
            );

            // ⚠ A restored Cluster is paused over the origin it already names. The pause is a whole
            // server-side apply, and one that dropped `externalClusters` while `bootstrap.recovery.source`
            // still named the entry is one CloudNativePG's webhook refuses — a teardown the tenant could
            // not perform. The stored spec is the one place the origin is sure to be.
            var (problem, result) = await ApplyAsync(
                context,
                cluster,
                PostgresServers.ClusterKind,
                PostgresServers.ClusterJson(name, context.Desired, null, PostgresServers.RestoreOrigin.FromCluster(stored)),
                true,
                cancellationToken
            );

            if (problem is not null && result != ApplyResult.Conflict) {
                return problem;
            }
        }

        var claims = await ClaimsAsync(context, cluster, cancellationToken);

        if (claims.TryGetError(out var listError)) {
            return ReconcileOutcome.FromFailure(listError);
        }

        var detached = await VolumeCustody.DetachAsync(cluster, claims.GetValueOrThrow(), context, cancellationToken);

        if (detached.TryGetError(out var detachError)) {
            // ⚠ Retryable whatever the code, and NEVER converged. A claim that could not be
            // detached is a claim the collector takes with the Cluster, and the only way to keep the
            // window honest is to keep the Cluster until the detach lands or a person reads why not.
            return ReconcileOutcome.Failed(detachError, true);
        }

        if (detached.GetValueOrThrow() > 0) {
            context.Log.Report(
                "detached",
                $"{VolumeCustody.Count(detached.GetValueOrThrow())} of '{name}' will outlive the Cluster"
            );
        }

        return null;
    }

    /// <summary>
    ///     On a create that finds claims from a previous life: creates the <c>Cluster</c> paused,
    ///     reads its uid back, and hands it the claims before the operator is allowed to look.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when there was nothing to adopt or the claims now belong to the
    ///     <c>Cluster</c>, or the outcome to return from the pass.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Why the operator must be paused across this and not merely raced.</b> The
    ///         operator's first pass over a new <c>Cluster</c> lists claims by controller reference,
    ///         finds none, and calls <c>createPrimaryInstance</c>: it takes serial 1, creates
    ///         <c>{name}-1</c>, reads <c>AlreadyExists</c> as success, and starts an <c>initdb</c>
    ///         job over the retained volume — whose <c>EnsureTargetDirectoriesDoNotExist</c> renames
    ///         the tenant's data directory aside and initialises beside it. The database is not
    ///         lost, and it is not the database the tenant gets back.
    ///         <c>
    /// cnpg.io/reconciliationLoop:
    ///         disabled
    ///         </c> is checked first thing in the operator's <c>reconcile</c>, so a
    ///         <c>Cluster</c> that is created carrying it is a <c>Cluster</c> the operator has not
    ///         acted on when the adoption runs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Runs only when the <c>Cluster</c> is absent or still paused</b>, which is what
    ///         keeps the steady state at one apply and no list: a converged server's every reminder
    ///         pass reads the <c>Cluster</c>, finds it running, and goes straight to the apply. The
    ///         paused arm is a crash between the create and the un-pause — the next pass adopts
    ///         whatever is still unowned and lets the ordinary apply below release the operator.
    ///     </para>
    /// </remarks>
    static async Task<ReconcileOutcome?> AdoptRetainedClaimsAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        PostgresServers.BackupStore? store,
        PostgresServers.RestoreOrigin? origin,
        CancellationToken cancellationToken
    ) {
        var name = context.Id.Name;
        var target = PostgresServers.ClusterRef(context.Namespace, name);
        var existing = await cluster.GetAsync(target, cancellationToken);

        if (existing.IsSuccess && !PostgresServers.IsPaused(existing.GetValueOrThrow().Json)) {
            return null;
        }

        if (existing.TryGetError(out var readError) && readError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(readError);
        }

        var claims = await ClaimsAsync(context, cluster, cancellationToken);

        if (claims.TryGetError(out var listError)) {
            return ReconcileOutcome.FromFailure(listError);
        }

        var retained = claims.GetValueOrThrow();

        if (retained.IsEmpty) {
            // A first creation, or a restore whose claims were purged: nothing to hand over, and the
            // operator bootstraps a fresh primary, which is the right thing for an empty history.
            return null;
        }

        context.Log.Report(
            "restoring",
            $"{VolumeCustody.Count(retained.Length)} of '{name}' survived a previous teardown and will be handed to the new Cluster",
            10
        );

        var (problem, _) = await ApplyAsync(
            context,
            cluster,
            PostgresServers.ClusterKind,
            PostgresServers.ClusterJson(name, context.Desired, store, origin),
            true,
            cancellationToken
        );

        if (problem is not null) {
            return problem;
        }

        var created = await cluster.GetAsync(target, cancellationToken);

        if (created.TryGetError(out var createdError)) {
            return createdError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied paused and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(createdError);
        }

        var uid = KubeJson.UidOf(JsonNode.Parse(created.GetValueOrThrow().Json));

        if (uid.Length == 0) {
            // ⚠ Not a guess. An owner reference is compared by uid, and one naming a uid the API
            // server never issued is what the garbage collector deletes a dependent over.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{target}' read back without a metadata.uid, so the claims it should adopt cannot "
                + "be given an owner the garbage collector will honour. The restore stops here rather "
                + "than releasing the operator over claims it would not recognise."
            );
        }

        var adopted = await VolumeCustody.AdoptAsync(
            cluster,
            retained,
            PostgresServers.ClusterOwner(name, uid),
            context,
            cancellationToken
        );

        if (adopted.TryGetError(out var adoptError)) {
            return ReconcileOutcome.Failed(adoptError, true);
        }

        context.Log.Report(
            "restored",
            $"the Cluster '{name}' owns {VolumeCustody.Count(retained.Length)} again and the operator is being released",
            20
        );

        return null;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        // ⚠ The Cluster alone, and not the Pooler. A server IS its Cluster: the pooler is an optional
        // front end whose absence is a legal configuration, so folding it in would make "the resource
        // exists" depend on a setting rather than on the resource.
        var read = await cluster.GetAsync(
            PostgresServers.ClusterRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the CloudNativePG Cluster is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = PostgresServers.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the CloudNativePG Cluster carries the desired spec"
                : "the CloudNativePG Cluster has drifted"
        };
    }

    /// <summary>
    ///     Applies one object and turns the two outcomes that are not failures into progress.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when the apply landed, or the outcome to return from the pass.
    /// </returns>
    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string json,
        CancellationToken cancellationToken
    ) =>
        (await ApplyAsync(context, cluster, kind, json, false, cancellationToken)).Problem;

    /// <summary>
    ///     <see cref="Apply" /> with the outcome's cause beside it, for the one caller that treats a
    ///     conflict differently from an unreachable cluster.
    /// </summary>
    /// <param name="context">The pass.</param>
    /// <param name="cluster">The cluster to apply to.</param>
    /// <param name="kind">Which of the two kinds.</param>
    /// <param name="json">The rendered object.</param>
    /// <param name="paused">
    ///     Whether to carry <see cref="PostgresServers.PauseAnnotation" />. ⚠ An annotation this
    ///     manager owns, so the next apply WITHOUT it is what removes it — server-side apply drops a
    ///     field its manager stops applying. That is why un-pausing is not a step of its own
    ///     anywhere: it is the ordinary apply.
    /// </param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    static async Task<(ReconcileOutcome? Problem, ApplyResult Result)> ApplyAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string json,
        bool paused,
        CancellationToken cancellationToken
    ) {
        var builder = KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion);

        if (paused) {
            builder = builder.WithAnnotations((PostgresServers.PauseAnnotation, PostgresServers.PausedValue));
        }

        var applied = await builder
            .ObjectJson(json)
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, a
            // Cluster CRD the operator never installed, our own credentials — will be refused
            // identically for the next hour, and ReconcileOutcome.FromFailure is where the four codes
            // that mean that are listed.
            return (ReconcileOutcome.FromFailure(applyError), ApplyResult.Unknown);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles
                // rather than failing them. A tenant whose cluster is down has a resource that is
                // still coming, not one that broke.
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return (ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                ), outcome.Result);

            case ApplyResult.Conflict:
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return (ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                ), outcome.Result);

            default:
                return (null, outcome.Result);
        }
    }

    /// <summary>
    ///     Removes the pooler when the body has turned pooling off.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when there is nothing left to remove, or the outcome to return.
    /// </returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Reads before it deletes, so the steady state of a server with no pooler is zero
    ///         writes.
    ///     </b> An unconditional delete would be correct and would issue one request per
    ///     reminder for the life of the resource, which is clause 1's "changes nothing" read as "does
    ///     nothing observable" rather than as "does nothing".
    ///     <para>
    ///         ⚠ <b>Turning pooling off is a visible change to the connection string, by design.</b>
    ///         <c>charts/managed/postgres/conformance.yaml</c>'s
    ///         <c>pooler-is-the-default-endpoint</c> assertion is what says so: the advertised host is
    ///         the pooler's while pooling is on, so this teardown moves it. That is the honest
    ///         behaviour and it is why docs/plan/12 makes pooling on by default — the change is
    ///         cheaper to never make than to make later.
    ///     </para>
    /// </remarks>
    static async Task<ReconcileOutcome?> RemovePoolerAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var target = PostgresServers.PoolerRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? null
                : ReconcileOutcome.FromFailure(readError);
        }

        context.Log.Report("removing-pooler", $"pooling is off, so '{target}' is being removed", 60);

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(PostgresServers.PoolerKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(PostgresServers.PoolerJson(context.Id.Name, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        return deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound
            ? ReconcileOutcome.FromFailure(deleteError)
            : null;
    }

    /// <summary>The objects a body implies, in apply order.</summary>
    static IEnumerable<ObjectRef> Targets(string ns, string name, bool pooling, bool backups, bool restoring) {
        if (backups) {
            yield return PostgresServers.BackupSecretRef(ns, name);
        }

        if (restoring) {
            yield return PostgresServers.RestoreSecretRef(ns, name);
        }

        yield return PostgresServers.ClusterRef(ns, name);

        if (pooling) {
            yield return PostgresServers.PoolerRef(ns, name);
        }
    }

    /// <summary>
    ///     Gives the server a bucket of its own on the platform's store and a key to it, held in the
    ///     vault and rendered into the <c>Secret</c> the operator reads.
    /// </summary>
    /// <returns>
    ///     The outcome to return from the pass, or <see langword="null" /> and the store to render.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Before the <c>Cluster</c>, because the <c>Cluster</c> names the <c>Secret</c>.</b>
    ///         CloudNativePG starts archiving WAL the moment the primary is up, and an archive command
    ///         whose credential Secret is absent fails every segment until it appears — a server that
    ///         reported <c>Succeeded</c> with a broken archive, which is the failure #91 was about in
    ///         another shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The key never touches the body, the grain or the log.</b> It comes out of
    ///         <see cref="ObjectStoreCredentials.EnsureAsync" /> — the vault's copy, once one exists —
    ///         and goes into one <c>Secret</c> document, in a local, for the length of this pass.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A store that refuses is retried, not failed</b>: <c>FromFailure</c> treats the
    ///         store's <c>InternalError</c> as transient, which an unreachable store is. A host with no
    ///         store wired refuses with the same code and a message naming the configuration, so the
    ///         operation's progress says why after the first pass.
    ///     </para>
    /// </remarks>
    static async Task<(ReconcileOutcome? Problem, PostgresServers.BackupStore? Store)> ProvisionBackupStoreAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var name = context.Id.Name;
        var bucket = ObjectStoreCredentials.BucketFor(PostgresServers.BucketPrefix, context.Id.Id);

        // ⚠ A DEPLOYMENT WITH NO STORE FAILS THE PASS, TERMINALLY — #30's review. The refusing default's
        // InternalError is retryable, so on a host that wires no grants (the AppHost is one: its
        // SeaweedFS reads a static identities file that ignores issued keys) every default-bodied server
        // stayed Creating for the operation's full hour with nothing a tenant could act on. Waiting wires
        // nothing; the tenant's move is backup.enabled false, and the operator's is the store.
        if (context.Grants.DataPlaneEndpoint.Length == 0) {
            return (
                ReconcileOutcome.Failed(
                    new Error(
                        ErrorCode.InvalidRequestBody,
                        "Backups are on, and this deployment has no platform object store to put them in, so no "
                        + "bucket can be made and no key issued. Set backup.enabled to false to run the server "
                        + "without backups, or ask the platform operator to configure the store's IAM and "
                        + "data-plane endpoints (CyberCloud:ObjectStorage).",
                        "/properties/backup/enabled"
                    ),
                    false
                ),
                null
            );
        }

        context.Log.Report("backup-store", $"ensuring bucket '{bucket}' and a key to it on the platform's object store", 10);

        var key = await ObjectStoreCredentials.EnsureAsync(
            context.Grants,
            context.Secrets,
            context.SecretWriter,
            ObjectStoreCredentials.VaultPathFor(context.Id),
            bucket,
            bucket,
            cancellationToken
        );

        if (key.TryGetError(out var keyError)) {
            return (ReconcileOutcome.FromFailure(keyError), null);
        }

        var secret = await Apply(
            context,
            cluster,
            PostgresServers.SecretKind,
            PostgresServers.BackupSecretJson(name, key.GetValueOrThrow()),
            cancellationToken
        );

        return secret is not null
            ? (secret, null)
            : (null, PostgresServers.BackupStore.For(context.Id.Id, name, context.Grants.DataPlaneEndpoint));
    }

    /// <summary>
    ///     For a server restored from a recovery point: finds where the point's bytes are and renders
    ///     the SOURCE's key, read from the vault, into this server's own <c>{name}-restore-s3</c>.
    /// </summary>
    /// <returns>
    ///     The outcome to return from the pass, or <see langword="null" /> and the origin to render.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The source may be gone, and that is the restore a vault exists for — #30's reclaim.</b>
    ///         A restore used to read the source's <c>{source}-backup-s3</c> through the point's
    ///         status, so the source's teardown left that Secret behind and its resource group could
    ///         never be reclaimed. Now nothing here reads the source's Secret or the source's
    ///         resource: the point names the bucket (<c>pg-{sourceId}</c>, the one durable reference to
    ///         a server that no longer exists), and the bucket's GUID names the vault path the key is
    ///         held at, which the source's teardown leaves in place.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The origin is read off the point once and kept in the Secret.</b> The point is the
    ///         vault's and its retention prunes it; the Secret is this server's, kept by a parking
    ///         teardown, so a restored server brought back from its own soft delete after its point
    ///         expired still renders the <c>Cluster</c> it was created as. For the same reason a vault
    ///         path that has gone — a purge of the source, once one reclaims it — keeps the copy
    ///         already rendered rather than failing a server whose bootstrap is long finished.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bounded to this tenant and this type by construction.</b> The vault path is built from
    ///         this resource's own tenant and type and only the GUID comes from the point, and the
    ///         point is one <c>recover</c> checked belongs to the caller's vault — the only way in,
    ///         since <c>restore.recoveryPoint</c> is set only by an action.
    ///     </para>
    /// </remarks>
    static async Task<(ReconcileOutcome? Problem, PostgresServers.RestoreOrigin? Origin)> ProvisionRestoreSourceAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var name = context.Id.Name;
        var recoveryPoint = PostgresServers.RecoveryPoint(context.Desired);

        // ── Where from: the copy this server already holds, or the point ────────────────────────
        var rendered = await cluster.GetAsync(PostgresServers.RestoreSecretRef(context.Namespace, name), cancellationToken);

        if (rendered.TryGetError(out var renderedError) && renderedError.Code != ErrorCode.ResourceNotFound) {
            return (ReconcileOutcome.FromFailure(renderedError), null);
        }

        var origin = rendered.IsSuccess ? PostgresServers.RestoreOrigin.FromSecret(rendered.GetValueOrThrow()) : null;

        if (origin is null) {
            var point = await cluster.GetAsync(PostgresServers.BackupRef(context.Namespace, recoveryPoint), cancellationToken);

            if (point.TryGetError(out var pointError)) {
                return pointError.Code == ErrorCode.ResourceNotFound
                    ? (Refused(
                        ErrorCode.ResourceNotFound,
                        $"Recovery point '{recoveryPoint}' is gone and this server was never bootstrapped from it, so "
                        + "there is nothing to restore. A vault's retention prunes its points; restore from one "
                        + "listRecoveryPoints still lists."
                    ), null)
                    : (ReconcileOutcome.FromFailure(pointError), null);
            }

            origin = PostgresServers.RestoreOrigin.FromBackup(point.GetValueOrThrow().Json);

            if (origin is null) {
                return (Refused(
                    ErrorCode.PreconditionFailed,
                    $"Recovery point '{recoveryPoint}' does not record a base backup in a PostgreSQL server's bucket "
                    + "on the platform's store (status.destinationPath s3://pg-{id}/, status.serverName and "
                    + "status.backupId), so there is no source this server can be restored from."
                ), null);
            }

            if (origin.EndpointUrl.Length == 0) {
                origin = origin with { EndpointUrl = context.Grants.DataPlaneEndpoint };
            }

            if (origin.EndpointUrl.Length == 0) {
                return (Refused(
                    ErrorCode.PreconditionFailed,
                    $"Recovery point '{recoveryPoint}' records no store endpoint and this deployment has no platform "
                    + "object store configured, so the source's bucket cannot be reached."
                ), null);
            }
        }

        // ── The source's key, as the vault holds it ─────────────────────────────────────────────
        var source = new ResourceId(
            context.Id.TenantId,
            context.Id.SubscriptionId,
            context.Id.ResourceGroup,
            context.Id.Type,
            origin.ServerName,
            origin.SourceId
        );

        var key = await ObjectStoreCredentials.HeldAsync(
            context.Secrets,
            ObjectStoreCredentials.VaultPathFor(source),
            cancellationToken
        );

        if (key.TryGetError(out var keyError)) {
            if (keyError.Code != ErrorCode.ResourceNotFound) {
                return (ReconcileOutcome.FromFailure(keyError), null);
            }

            if (rendered.IsSuccess) {
                return (null, origin);
            }

            return (Refused(
                ErrorCode.ResourceNotFound,
                $"The vault holds no key to recovery point '{recoveryPoint}''s bucket "
                + $"'{ObjectStoreCredentials.BucketFor(PostgresServers.BucketPrefix, origin.SourceId)}', so the point "
                + "cannot be read. The key outlives its server until a purge reclaims the bucket; one that has gone "
                + "took the point's bytes with it."
            ), null);
        }

        context.Log.Report(
            "restore-source",
            $"rendering the key to '{origin.ServerName}''s bucket into '{PostgresServers.RestoreSecretName(name)}'",
            15
        );

        var applied = await Apply(
            context,
            cluster,
            PostgresServers.SecretKind,
            PostgresServers.RestoreSecretJson(name, key.GetValueOrThrow(), origin),
            cancellationToken
        );

        return applied is not null ? (applied, null) : (null, origin);
    }

    /// <summary>A terminal refusal at the restore's own property: waiting will not bring a point back.</summary>
    static ReconcileOutcome Refused(ErrorCode code, string message) =>
        ReconcileOutcome.Failed(new Error(code, message, PostgresServers.RecoveryPointPointer), false);
}
