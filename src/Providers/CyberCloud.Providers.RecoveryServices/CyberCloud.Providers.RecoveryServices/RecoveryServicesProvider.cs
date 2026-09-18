namespace CyberCloud.Providers.RecoveryServices;

/// <summary>
///     Backup as a service — a vault that binds other providers' resources to a schedule and a
///     retention, docs/plan/15 § Backup as a service.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE SEVENTEENTH PROVIDER FAMILY, AND THE FIRST WHOSE RESOURCE IS ABOUT OTHER
///         PROVIDERS' RESOURCES.</b> Sixteen families each render their own objects from their own
///         body. This one renders a CloudNativePG <c>ScheduledBackup</c> beside a <i>PostgreSQL
///         server another provider owns</i>, and it learns where that server's <c>Cluster</c> is
///         through <see cref="ReconcileContext.View" /> — issue #90's seam, built for exactly this type
///         and used by nothing until it. What it cost, measured: nothing in the manager changed, one
///         reconciler reads the view where the others read the body, and the conformance harness
///         gained <c>IProviderCaseSource.Companions</c> — a way to say "this suite needs a resource of
///         another provider to exist before its own can converge", which no case had needed.
///     </para>
///     <para>
///         ⚠ <b>NOT A ROW OF docs/plan/12, AND NOT docs/plan/12's PATTERN EITHER.</b> That document's
///         eight pieces describe a managed <i>service</i> — an operator, a chart, a credential, a
///         scrape. A vault is a <i>policy</i>: docs/plan/15 § Backup as a service opens <i>"Not a
///         storage type; a policy resource"</i>. So there is no credential to mint, no scrape object
///         to ask an operator for, no sizing table and no derived quota meter: a vault draws
///         <see cref="QuotaMeter.Resources" /> and nothing else, because the pods and volumes its
///         schedules use are the protected server's and are already reserved against the server.
///         <c>storage.backup.gb_month</c> in docs/plan/15 § Metering — <i>"After compression"</i> — is
///         a usage meter over the store, which is docs/plan/22's pipeline and not a pure function of a
///         body.
///     </para>
///     <para>
///         ⚠ <b>TWO ACTIONS WITH HANDLERS AND NO LONG-RUNNING ONE, WHICH IS A DECISION ABOUT THE
///         RESTORE.</b> A restore is one server-side apply of a <c>Cluster</c> — seconds — and what
///         then takes minutes is CloudNativePG recovering it, which is the operator's work on an
///         object the tenant can watch. A long-running action would have this platform poll that
///         recovery on the tenant's behalf and report a status the object already carries. It answers
///         <c>200</c> with the new cluster's address instead, and the day a restored cluster becomes a
///         <c>CyberCloud.DBforPostgreSQL/servers</c> resource (owed), its own operation is where the
///         recovery is watched.
///     </para>
///     <para>
///         ⚠ <b>NO <c>SupportsSoftDelete</c>, AND ON THIS TYPE IT IS A GAP.</b> docs/plan/06 § Tags,
///         locks promises 7 days for <i>"resources carrying data"</i>, and a vault's recovery points
///         are garbage-collected with its schedules — <c>backupOwnerReference: self</c>. A window
///         here would need the schedules to survive the delete detached from the vault and be
///         re-owned on restore, which is the shape the PostgreSQL server's claims take (<c>RetainedVolume</c>)
///         and is not built for a CloudNativePG object that is not a claim.
///         <c>charts/managed/recovery-vault/conformance.yaml § owed</c>,
///         <c>deleting-the-vault-deletes-its-recovery-points</c>.
///     </para>
/// </remarks>
public sealed class RecoveryServicesProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => RecoveryVaults.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(RecoveryVaults.TypePath)
            .ApiVersion(RecoveryVaults.V2026, RecoveryVaults.Schema2026)
            .Reconciler<RecoveryVaultReconciler>()
            // ⚠ THE COUNT AND NOTHING ELSE — see the remarks on this class. A ScheduledBackup is a
            // controller's intent, not a pod; the pods that run a backup are the protected server's,
            // sized by its body and reserved against its meters.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                RecoveryVaults.ListRecoveryPointsAction,
                ActionKind.Post,
                RecoveryVaults.ListRecoveryPointsPermission,
                response: RecoveryVaults.ListRecoveryPointsResponse,
                handler: typeof(RecoveryVaultListRecoveryPointsHandler)
            )
            // ⚠ `recover`, NOT `restore`: the builder reserves `restore` and `purge` for docs/plan/08
            // § Soft delete's own dispatch, and this type's first conformance run found the refusal.
            .Action(
                RecoveryVaults.RecoverAction,
                ActionKind.Post,
                RecoveryVaults.RecoverPermission,
                request: RecoveryVaults.RecoverRequest,
                response: RecoveryVaults.RecoverResponse,
                handler: typeof(RecoveryVaultRecoverHandler)
            )
            // ⚠ `backupvault`, and not `vault`: docs/plan/24 § What has landed lists
            // CyberCloud.KeyVault/vaults as a catalogue type this tree has not published, and `az
            // keyvault` / `az backup vault` is the split Azure's own CLI makes. Neither this
            // namespace's group key (`recoveryservices`) nor any sibling's — there are none.
            .Display(
                "Backup vault",
                "Backup vaults",
                shortName: "backupvault",
                summary: "A backup policy — a schedule and a retention — over the PostgreSQL servers in a "
                + "resource group, with the recovery points it produces and a restore into a new cluster."
            )
            .Chart(RecoveryVaults.ChartName)
            .SupportsTags()
            .RequiresCluster(RecoveryVaults.ClusterIdPointer);
    }
}
