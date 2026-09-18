using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.RecoveryServices/vaults</c> — docs/plan/15 § Backup
///     as a service's <i>"policy resource that binds protected resources to schedules and
///     retention"</i>, as the type whose reconciler reaches other providers' resources.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST TYPE WHOSE RECONCILER READS ANOTHER PROVIDER'S RESOURCE, AND THE SEAM IT
///         READS THROUGH IS THE WHOLE DESIGN.</b> A protected item is a resource id path in the
///         vault's own body. The reconciler hands it to <c>ReconcileContext.View</c> (issue #90,
///         docs/plan/08 § What the resource manager deliberately does not do) and gets back two
///         things and nothing else: the other provider's public contract — the snapshot the gateway
///         would return, at that type's newest api-version, secret pointers dropped — and the
///         <i>addresses</i> of the objects that provider rendered, joined on ADR-013's resource-id
///         label. Nothing in this assembly names a type from
///         <c>CyberCloud.Providers.DBforPostgreSQL</c>; what it knows about a PostgreSQL server is
///         what <c>openapi/2026-08-01.json</c> publishes about one, and the two pointers it reads —
///         <c>/properties/backup/enabled</c> and <c>/properties/backup/retentionDays</c> — are
///         spelled here as strings against that document rather than as a reference to the other
///         provider's constants. src/Providers/README.md § Hard rule is why, and rule 2 of the
///         Assembly graph gate fails the build on the shortcut.
///     </para>
///     <para>
///         ⚠ <b>ONE BACKEND OF docs/plan/15's FOUR SHIPS, AND THE BRIEF NAMED TWO.</b> § Backup as a
///         service lists <i>"Velero for namespace-scoped Kubernetes state, volume snapshots for
///         block, engine-native backup for databases, bucket replication for object"</i>, and #30's
///         brief asked for PostgreSQL servers and file shares first — <i>"the two whose backing
///         objects have a snapshot story: CNPG's Backup/ScheduledBackup CRs and a PVC
///         VolumeSnapshot"</i>. Read against the sources, the second half is not true on this
///         platform: <c>pkg/driver/driver.go</c> of seaweedfs-csi-driver v1.4.20 — the driver
///         behind every file share's claim — advertises <c>CREATE_DELETE_VOLUME</c>,
///         <c>EXPAND_VOLUME</c>, <c>SINGLE_NODE_MULTI_WRITER</c> and <c>PUBLISH_UNPUBLISH_VOLUME</c>
///         and <b>no <c>CREATE_DELETE_SNAPSHOT</c></b>; the bundle installs no snapshot controller
///         and no class that could serve one. A <c>VolumeSnapshot</c> rendered against a share's
///         claim would be an object that never reaches <c>readyToUse</c> anywhere this platform
///         runs — the promise <c>protocol: NFS</c> was refused for on the share itself. So the
///         engine-native backend ships (<see cref="ClusterKind" /> → <see cref="ScheduledBackupKind" />),
///         a protected item whose rendered objects hold no CloudNativePG <c>Cluster</c> is refused by
///         name at the item's pointer, and the file-share half is
///         <c>charts/managed/recovery-vault/conformance.yaml § owed</c>,
///         <c>file-shares-have-no-snapshot-story</c>. docs/plan/15 is corrected in place.
///     </para>
///     <para>
///         ⚠ <b>THE STORE IS THE SERVER'S, AND THE VAULT OWNS THE SCHEDULE, THE RECOVERY-POINT RECORD
///         AND THE RESTORE.</b> CloudNativePG keeps a cluster's backup destination and its
///         <c>retentionPolicy</c> on the <c>Cluster</c> — <c>spec.backup.barmanObjectStore</c> — and
///         a <c>ScheduledBackup</c> names only the cluster, the cron and the method. The view is
///         read-only by design, so the vault cannot write a destination into a server it protects;
///         it renders the schedule beside the server, under its own id, and reads the server's
///         contract to refuse what it could not keep: a server with <c>backup.enabled: false</c>
///         renders a <c>Cluster</c> with no <c>backup</c> section, on which every <c>Backup</c> fails
///         with CloudNativePG's <i>"cannot proceed with the backup as the cluster has no backup
///         section"</i>; a server whose <c>backup.retentionDays</c> is shorter than the vault's
///         <c>retentionDays</c> has barman expiring bytes the vault would still list as restorable.
///         Both are refused at <c>/properties/protectedItems/{i}</c> rather than converged into a
///         vault that reports backups it does not have.
///     </para>
///     <para>
///         ⚠ <b>A RECOVERY POINT IS A <c>Backup</c> OBJECT THE OPERATOR LABELLED, NOT A RECORD THIS
///         PLATFORM KEEPS.</b> <c>internal/controller/scheduledbackup_controller.go</c> at v1.30.0
///         stamps <c>cnpg.io/scheduled-backup: {scheduledBackup.Name}</c> and
///         <c>cnpg.io/cluster: {cluster}</c> onto every <c>Backup</c> it creates, so the vault's
///         recovery points are one selected listing per protected item
///         (<see cref="RecoveryPointSelector" />) and no grain. ⚠ <c>CreateBackup</c> in
///         <c>api/v1/scheduledbackup_funcs.go</c> copies <b>no labels</b> of the ScheduledBackup's
///         own onto the Backup — only annotations the operator was configured to inherit — so
///         ADR-013's seven do not reach a recovery point and a join on <c>cybercloud.io/resource-id</c>
///         would find none. The operator's own label is the join, and <see cref="Selector" />
///         spells it once.
///     </para>
///     <para>
///         ⚠ <b><c>backupOwnerReference: self</c>, so deleting the vault deletes its recovery
///         points.</b> The three values are <c>none</c>, <c>self</c> and <c>cluster</c>. <c>none</c>
///         leaves Backup objects nobody addresses once the vault is gone — the untracked state
///         <c>reclaimPolicy: Delete</c> was chosen against on the file share; <c>cluster</c> ties
///         them to the server's life rather than the vault's, which is the wrong owner for a policy
///         resource. <c>self</c> makes the garbage collector follow the ScheduledBackup, which is
///         what makes the conformance suite's "delete tears down" true of this type. What that
///         costs is a recovery window on the vault itself, and it is
///         <c>conformance.yaml § owed</c>, <c>deleting-the-vault-deletes-its-recovery-points</c>.
///     </para>
///     <para>
///         ⚠ <b>Protected items live in the vault's own resource group, and the reason is the action
///         seam rather than the view.</b> <c>ActionContext</c> carries no <c>IResourceView</c>, so
///         <c>listRecoveryPoints</c> and <c>restore</c> find the vault's objects by listing the
///         namespace the dispatcher hands them — the vault's own — and the namespace of a resource
///         in another group is a rule (<c>ReconcileDriver.NamespaceFor</c>) that lives in the manager
///         and that rule 8 keeps a provider from calling. Azure requires a protected item to share
///         the vault's region; this requires the group, which is stricter and is recorded as
///         <c>items-in-other-resource-groups</c>.
///     </para>
/// </remarks>
public static class RecoveryVaults {
    /// <summary>The provider namespace, as docs/plan/15 § Backup as a service spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.RecoveryServices";

    /// <summary>The type path.</summary>
    public const string TypePath = "vaults";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/recovery-vault";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    /// <remarks>
    ///     ⚠ A vault is placed, and every protected item must be placed on the <i>same</i> cluster:
    ///     the ScheduledBackup goes into the protected server's namespace through the vault's own
    ///     connection, and a reconciler has no way to reach a second cluster (the connection
    ///     factory is one of the seams rule 8 keeps from a provider). An item on another cluster is
    ///     refused at its pointer; see <see cref="ItemOf" />'s caller.
    /// </remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that lists the recovery points every protected item has.</summary>
    /// <remarks>
    ///     docs/plan/15 § Backup as a service: <i>"A protected resource shows its backup status on
    ///     its own blade — a backup system nobody can see the status of is a backup system that is
    ///     quietly broken."</i> This is the vault's side of that blade. <c>read</c>, because nothing
    ///     in the response is a credential — a recovery point is a name, a phase and two timestamps.
    /// </remarks>
    public const string ListRecoveryPointsAction = "listRecoveryPoints";

    /// <summary>The permission <see cref="ListRecoveryPointsAction" /> checks.</summary>
    public const string ListRecoveryPointsPermission = "read";

    /// <summary>The action that restores a recovery point into a <b>new</b> cluster.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/15 § Backup as a service: <i>"Restore always creates a new resource.
    ///         Restore-in-place is how people lose the good copy while trying to recover it."</i>
    ///         The handler renders a CloudNativePG <c>Cluster</c> named by the caller, bootstrapped
    ///         from the recovery point (<c>bootstrap.recovery.backup.name</c>), beside the protected
    ///         server and never into it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Spelled <c>recover</c>, because <c>restore</c> is the platform's.</b> docs/plan/08
    ///         § Soft delete declares <c>restore</c> and <c>purge</c> on every type with a recovery
    ///         window and dispatches both to the manager's own path, and <c>ProviderBuilder.Action</c>
    ///         refuses a provider that declares either — <i>"'restore' is reserved"</i>, found by
    ///         this type's first conformance run. A vault's restore is a different verb anyway: it
    ///         does not bring the vault back, it brings a database back from one of the vault's
    ///         points.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>write</c>, and what comes back is a cluster object rather than a platform
    ///         resource.</b> The vault cannot <c>PUT</c> a <c>CyberCloud.DBforPostgreSQL/servers</c>
    ///         — the seam has no member that writes, by design — and that type has no property that
    ///         says "bootstrap me from this recovery point". So the restored cluster is a scratch
    ///         object in the resource group's namespace: reachable from the tenant's pods, carrying
    ///         the vault's labels and <see cref="RestoreRoleLabel" />, metered by nothing and known to
    ///         no <c>listKeys</c>. Adopting it as a server is <c>conformance.yaml § owed</c>,
    ///         <c>a-restore-is-not-yet-a-resource</c>, and the property it needs is that type's.
    ///     </para>
    /// </remarks>
    public const string RecoverAction = "recover";

    /// <summary>The permission <see cref="RecoverAction" /> checks — on the vault, and on nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>The vault's <c>write</c> is the whole gate, and the bytes are the server's.</b> The
    ///     manager asks the authorizer for this permission on the vault's own id and nothing asks for
    ///     any permission on the protected server, which the vault itself only needs <c>read</c> on.
    ///     A contributor on the vault can therefore bring up a copy of any server it protects, with a
    ///     fresh superuser secret, without holding anything on that server. The handler cannot check:
    ///     <c>ActionContext</c> carries no caller and no authorizer. Recorded as
    ///     <c>conformance.yaml § owed</c>, <c>recover-is-gated-by-the-vault-alone</c>, whose closing
    ///     move is the server's <c>restoreFrom</c> — the same one <c>a-restore-is-not-yet-a-resource</c>
    ///     waits on — or a related-resource clause on the action's declaration for the manager to gate.
    /// </remarks>
    public const string RecoverPermission = "write";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>
    ///     The one protected type this vault knows how to back up, spelled as the registry spells
    ///     it and not as a reference to its provider.
    /// </summary>
    /// <remarks>
    ///     ⚠ A <see cref="ResourceTypeName" /> rather than a <c>using</c>: this is what the reconciler
    ///     hands to <c>ReconcileContext.Watch</c> and compares a snapshot's type against, and it is
    ///     the whole of what this assembly knows about where a PostgreSQL server lives in the
    ///     catalogue.
    /// </remarks>
    public static ResourceTypeName PostgresServerType { get; } = new("CyberCloud.DBforPostgreSQL", "servers");

    /// <summary>
    ///     The most items one vault may protect, and the reason is the view's price.
    /// </summary>
    /// <remarks>
    ///     <see cref="IResourceView" />'s remarks: <i>"Every call is a grain hop to the index, a fully
    ///     consistent ReBAC walk over durable rows, and a grain hop to the resource, inside the
    ///     reconciler's 30-second budget"</i>, and <see cref="IResourceView.RenderedObjectsAsync" />
    ///     adds a namespace listing. Two view calls, one listing, one apply and one read per item
    ///     puts sixteen items at roughly eighty round trips, which fits the budget on a real cluster
    ///     with room. A seventeenth is refused at its own pointer rather than accepted into a vault
    ///     whose pass times out.
    /// </remarks>
    public const int MaxProtectedItems = 16;

    // ── The objects a vault renders and reads ─────────────────────────────────────────────────

    /// <summary>The CloudNativePG <c>ScheduledBackup</c> — one per protected item, and the only kind this type applies.</summary>
    /// <remarks>
    ///     <c>api/v1/scheduledbackup_types.go</c> at v1.30.0: <c>{suspend, immediate, schedule,
    ///     cluster, backupOwnerReference, target, method, pluginConfiguration, online,
    ///     onlineConfiguration}</c>. Namespaced; plural <c>scheduledbackups</c>.
    /// </remarks>
    public static GroupVersionKind ScheduledBackupKind { get; } =
        new() { Group = "postgresql.cnpg.io", Version = "v1", Kind = "ScheduledBackup", Plural = "scheduledbackups" };

    /// <summary>The CloudNativePG <c>Backup</c> — a recovery point. Read and pruned, never rendered by the reconciler.</summary>
    public static GroupVersionKind BackupKind { get; } =
        new() { Group = "postgresql.cnpg.io", Version = "v1", Kind = "Backup", Plural = "backups" };

    /// <summary>
    ///     The CloudNativePG <c>Cluster</c> — what a protected PostgreSQL server renders, and what a
    ///     restore creates.
    /// </summary>
    /// <remarks>
    ///     ⚠ Spelled here a second time rather than taken from <c>PostgresServers.ClusterKind</c> —
    ///     the Hard rule. The two spellings are held together by nothing but the operator's API, which
    ///     is what both are a reading of.
    /// </remarks>
    public static GroupVersionKind ClusterKind { get; } =
        new() { Group = "postgresql.cnpg.io", Version = "v1", Kind = "Cluster", Plural = "clusters" };

    /// <summary>The label the ScheduledBackup controller stamps on every Backup it creates. <c>utils.ParentScheduledBackupLabelName</c>.</summary>
    public const string ParentScheduledBackupLabel = "cnpg.io/scheduled-backup";

    /// <summary>The label the ScheduledBackup controller stamps with the cluster's name. <c>utils.ClusterLabelName</c>.</summary>
    public const string ClusterLabel = "cnpg.io/cluster";

    /// <summary>
    ///     The vault's own eighth label: which protected item an object belongs to, by the item's
    ///     resource name.
    /// </summary>
    /// <remarks>
    ///     ADR-013's seven name the <i>vault</i>; nothing among them says which of its items a
    ///     ScheduledBackup serves, and the object name folds the two together with a digest that
    ///     cannot be inverted (the same finding as <c>storage.cybercloud.io/account</c>, where the
    ///     fold is a hyphen that cannot be split back). Carried through <c>WithLabels</c>, so it
    ///     passes the same syntax check the seven do — a resource name is a legal label value by
    ///     construction.
    /// </remarks>
    public const string ProtectedItemLabel = "recoveryservices.cybercloud.io/protected-item";

    /// <summary>
    ///     The label that marks an object a <see cref="RecoverAction" /> created, so the vault's
    ///     teardown leaves it standing.
    /// </summary>
    /// <remarks>
    ///     ⚠ A restored cluster carries the vault's resource-id label, because every object this
    ///     platform writes carries ADR-013's seven. Without this second label the vault's
    ///     <c>DeleteAsync</c> could not tell a restored database from a ScheduledBackup by anything
    ///     but kind, and "deleting the vault deletes the database you just recovered" is the failure
    ///     docs/plan/15's restore rule exists to prevent. The teardown deletes
    ///     <see cref="ScheduledBackupKind" /> and nothing else; what a restored cluster then is —
    ///     an object whose resource-id names a vault that may be gone — is
    ///     <c>conformance.yaml § owed</c>, <c>a-restore-is-not-yet-a-resource</c>.
    /// </remarks>
    public const string RestoreRoleLabel = "recoveryservices.cybercloud.io/role";

    /// <summary>The value <see cref="RestoreRoleLabel" /> carries on a restored cluster.</summary>
    public const string RestoreRoleValue = "restore";

    /// <summary>
    ///     The value CloudNativePG's <c>backupOwnerReference</c> takes so the Backup dies with the
    ///     ScheduledBackup. See the remarks on this class.
    /// </summary>
    public const string BackupOwnerReference = "self";

    /// <summary>The backup method the vault schedules. The server's own store, by design — see the remarks on this class.</summary>
    public const string BackupMethod = "barmanObjectStore";

    /// <summary>
    ///     A ScheduledBackup's name: the vault's and the item's, joined, then twelve hex digits of
    ///     the pair's digest that make the join unambiguous — the two stemmed to 24 characters each
    ///     when the whole does not fit.
    /// </summary>
    /// <param name="vault">The vault's own name.</param>
    /// <param name="item">The protected item's resource name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ Two vaults in one resource group may protect the same server, and one vault protects
    ///         many servers, so neither name alone is unique in the namespace. Both are at most
    ///         <see cref="ResourceNaming.MaxLength" /> characters and a Kubernetes name is capped at
    ///         the same 63, so the join can exceed the cap; when it does, the first 24 of each are
    ///         kept so <c>kubectl get scheduledbackups</c> still reads as something a person can place.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The digest is there whether or not the names fit, because a hyphen join is not
    ///         unique.</b> A resource name may carry hyphens, so vault <c>a</c> protecting <c>b-c</c>
    ///         and vault <c>a-b</c> protecting <c>c</c> both spelled <c>a-b-c</c> in the first cut —
    ///         and both vaults apply under one field manager,
    ///         <c>cybercloud/cybercloud.recoveryservices</c>, so the API server would not even
    ///         conflict: each pass would silently take <c>spec.cluster.name</c> and the resource-id
    ///         label from the other, and each vault's retention would prune the other's points
    ///         through <see cref="RecoveryPointSelector" />. The digest is over <c>{vault}/{item}</c>,
    ///         and <c>/</c> is outside the name alphabet, so two different pairs never digest the
    ///         same bytes. <c>RecoveryVaultDeclarationTests.TwoVaultsWhoseNamesJoinToOneSpellingOwnTwoSchedules</c>
    ///         pins the pair above apart.
    ///     </para>
    /// </remarks>
    public static string ScheduledBackupNameOf(string vault, string item) {
        ArgumentException.ThrowIfNullOrEmpty(vault);
        ArgumentException.ThrowIfNullOrEmpty(item);

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(vault + "/" + item)))[..12];

        var joined = vault + "-" + item + "-" + digest;
        if (joined.Length <= ResourceNaming.MaxLength) {
            return joined;
        }

        return Stem(vault) + "-" + Stem(item) + "-" + digest;

        static string Stem(string name) => (name.Length > 24 ? name[..24] : name).TrimEnd('-');
    }

    /// <summary>The ScheduledBackup a vault owns for one item.</summary>
    /// <param name="ns">The resource group's namespace.</param>
    /// <param name="vault">The vault's own name.</param>
    /// <param name="item">The protected item's resource name.</param>
    public static ObjectRef ScheduledBackupRef(string ns, string vault, string item) =>
        new() { Kind = ScheduledBackupKind, Namespace = ns, Name = ScheduledBackupNameOf(vault, item) };

    /// <summary>A recovery point, by the Backup's own name.</summary>
    /// <param name="ns">The resource group's namespace.</param>
    /// <param name="name">The Backup's name, as <see cref="ListRecoveryPointsResponse" />'s lines spell it.</param>
    public static ObjectRef BackupRef(string ns, string name) =>
        new() { Kind = BackupKind, Namespace = ns, Name = name };

    /// <summary>A CloudNativePG cluster, by name.</summary>
    /// <param name="ns">The resource group's namespace.</param>
    /// <param name="name">The cluster's name.</param>
    public static ObjectRef ClusterRef(string ns, string name) =>
        new() { Kind = ClusterKind, Namespace = ns, Name = name };

    /// <summary>The selector that finds one item's recovery points: the operator's own label, the ScheduledBackup's name.</summary>
    /// <param name="scheduledBackup">The ScheduledBackup's name.</param>
    public static string RecoveryPointSelector(string scheduledBackup) => ParentScheduledBackupLabel + "=" + scheduledBackup;

    /// <summary>The selector that finds every ScheduledBackup one vault owns in a namespace.</summary>
    /// <param name="vaultId">The vault's GUID.</param>
    public static string Selector(Guid vaultId) =>
        KubeLabels.ResourceType + "=" + KubeLabels.ResourceTypeValue(Type)
        + "," + KubeLabels.ResourceId + "=" + KubeLabels.GuidValue(vaultId);

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>A five-field cron expression — minute, hour, day of month, month, day of week — in numbers and the four operators.</summary>
    /// <remarks>
    ///     ⚠ Five fields at the API and six on the object. CloudNativePG parses a ScheduledBackup's
    ///     schedule with <c>robfig/cron</c>'s <c>cron.Parse</c>, whose grammar leads with a
    ///     <i>seconds</i> field — the CRD's own comment: <i>"The schedule does not follow the same
    ///     format used in Kubernetes CronJobs as it includes an additional seconds specifier"</i>.
    ///     A tenant writes the format every other cron on earth uses and <see cref="ScheduledBackupJson" />
    ///     prepends the <c>0</c>. Names (<c>MON</c>, <c>JAN</c>) and descriptors (<c>@daily</c>) are
    ///     refused: they would pass the pattern into a field this platform never parses, and the
    ///     one parser that does is the operator's, after the caller has been told <c>202</c>.
    /// </remarks>
    public const string CronPattern = "^([0-9*,/-]+ ){4}[0-9*,/-]+$";

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>ONE POLICY PER VAULT, AND docs/plan/15 SAYS "SCHEDULES AND RETENTION" IN THE
    ///         PLURAL.</b> An array of objects is not expressible in a <see cref="ResourceSchema" />
    ///         — <see cref="SchemaKind.Array" />'s remarks say why the flat pointer list refuses it —
    ///         so a vault with several policies would be a <c>vaults/backupPolicies</c> child type,
    ///         which is Azure's shape and a second chart, reconciler and conformance case. The
    ///         policy lands as a nested block here first; the day two servers in one vault need two
    ///         schedules, the child type is the next api-version and this block becomes its default.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>protectedItems</c> is an array of resource id paths, and the API does not check
    ///         the shape of each — the reconciler does.</b> <see cref="SchemaFormat.ResourceId" /> on
    ///         the element is what the registry would carry, and <c>./build.sh Charts</c> refuses
    ///         <c>@format</c> and <c>@length</c> on a <c>{array}</c> @param because JSON Schema ignores
    ///         both on anything but a string — the finding <c>charts/managed/kafka/conformance.yaml</c>
    ///         records as <c>cidr-shape-is-unenforced</c>, met here a second time. So a path that does
    ///         not parse reaches the reconciler and is refused there at
    ///         <c>/properties/protectedItems/{i}</c>, after the caller was told <c>202</c>
    ///         (<c>protected-item-shape-is-unenforced-at-the-api</c>). Whose tenant a path is in,
    ///         whether it exists, whether it is a type this vault can back up and whether the vault
    ///         has been granted <c>read</c> on it were always the reconciler's questions, and each
    ///         refusal targets the same pointer so the portal can highlight the row. An empty array is
    ///         a vault that protects nothing, which is legal and is the state a tenant leaves a vault
    ///         in before granting it reader on anything.
    ///     </para>
    ///     <para>
    ///         ⚠ Every default here is the chart's default, spelled as JSON — see
    ///         <c>StorageAccounts.Schema2026</c> for the rule.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the vault is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The vault's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the vault's protected items are placed on. Every protected "
                    + "item must be on this cluster; one placed elsewhere is refused by name when the "
                    + "vault is reconciled."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/policy",
                    SchemaKind.Nested,
                    Description: "The one policy every protected item follows: when a recovery point is "
                    + "taken and how long it is kept."
                ),
                new(
                    "/properties/policy/schedule",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When a recovery point is taken, as a five-field cron expression in UTC: "
                    + "minute, hour, day of month, month, day of week. Numbers, `*`, `,`, `-` and `/` "
                    + "only. Rendered to CloudNativePG with the seconds field it requires prepended."
                ) { Pattern = CronPattern, DefaultJson = "\"0 2 * * *\"", ExampleJson = "\"0 2 * * *\"" },
                new(
                    "/properties/policy/retentionDays",
                    SchemaKind.WholeNumber,
                    Description: "How many days a recovery point is kept before the vault prunes it. ⚠ The "
                    + "bytes behind a PostgreSQL recovery point live in the server's own backup store "
                    + "under the server's backup.retentionDays; a server whose retention is shorter than "
                    + "this is refused, because the store would forget what the vault still lists."
                ) { Minimum = 1, Maximum = 3650, DefaultJson = "14", ExampleJson = "14" },
                new(
                    "/properties/protectedItems",
                    SchemaKind.Array,
                    Required: true,
                    Description: "The resources this vault protects, as full resource id paths. Each must "
                    + "be a CyberCloud.DBforPostgreSQL/servers resource in this vault's resource group, "
                    + "on this vault's cluster, with backups enabled, that the vault has been granted "
                    + "read on. At most 16; anything else is refused by name at its own index when the "
                    + "vault is reconciled."
                ) {
                    // ⚠ NO Format AND NO MaxLength, and both belong here — see the remarks on this
                    // property. The chart surface refuses them on an array, and ItemOf is where the
                    // shape is checked instead.
                    ElementKind = SchemaKind.Text,
                    ExampleJson = "[\"/tenants/11111111-1111-4111-8111-111111111111/subscriptions/"
                    + "33333333-3333-4333-8333-333333333333/resourceGroups/prod/providers/"
                    + "CyberCloud.DBforPostgreSQL/servers/main\"]"
                }
            ]
        );

    /// <summary>What a <c>POST …/listRecoveryPoints</c> returns.</summary>
    /// <remarks>
    ///     The collection is one text line per recovery point, the shape
    ///     <c>MonitorAlertRules.ListInstancesResponse</c> gave a collection first: an array of objects
    ///     is not expressible in a <see cref="ResourceSchema" />, and a line a person can read in the
    ///     CLI is the honest form of that limit. <see cref="RecoveryPointLine" /> spells it.
    /// </remarks>
    public static ResourceSchema ListRecoveryPointsResponse { get; } =
        ResourceSchema.Of(
            [
                new("/count", SchemaKind.WholeNumber, Required: true, Description: "How many recovery points the vault holds, across every protected item."),
                new("/completed", SchemaKind.WholeNumber, Required: true, Description: "How many of them are restorable — CloudNativePG phase `completed`."),
                new(
                    "/recoveryPoints",
                    SchemaKind.Array,
                    Required: true,
                    Description: "One line per recovery point, newest first: "
                    + "'{item} {name} {phase} started {startedAt} stopped {stoppedAt} method {method}', "
                    + "followed by ': {error}' when the operator recorded one. The name is what recover takes."
                ) { ElementKind = SchemaKind.Text }
            ]
        );

    /// <summary>What a <c>POST …/recover</c> takes.</summary>
    public static ResourceSchema RecoverRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/recoveryPoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The recovery point to restore, by the name listRecoveryPoints gives it. "
                    + "It must be one of this vault's and its phase must be `completed`."
                ) { MaxLength = ResourceNaming.MaxLength },
                new(
                    "/targetName",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The name of the NEW cluster the recovery point is restored into, in the "
                    + "vault's resource group. Refused when a cluster of that name already exists — a "
                    + "restore never overwrites."
                ) { Pattern = "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", MaxLength = ResourceNaming.MaxLength }
            ]
        );

    /// <summary>What a <c>POST …/recover</c> returns.</summary>
    public static ResourceSchema RecoverResponse { get; } =
        ResourceSchema.Of(
            [
                new("/kind", SchemaKind.Text, Required: true, Description: "What was created. Always `Cluster` — a CloudNativePG cluster object."),
                new("/name", SchemaKind.Text, Required: true, Description: "The restored cluster's name, as asked for."),
                new("/namespace", SchemaKind.Text, Required: true, Description: "The namespace it was created in — the vault's resource group's."),
                new("/recoveryPoint", SchemaKind.Text, Required: true, Description: "The recovery point it was bootstrapped from."),
                new("/source", SchemaKind.Text, Required: true, Description: "The protected item the recovery point was taken of, as its resource id path.")
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The schedule a body asks for, five fields.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Schedule(JsonElement desired) {
        var found = Text(Member(desired, "policy"), "schedule");
        return found.Length > 0 ? found : DefaultSchedule;
    }

    /// <summary>How many days a body keeps a recovery point.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int RetentionDays(JsonElement desired) => Number(Member(desired, "policy"), "retentionDays", DefaultRetentionDays);

    /// <summary>The protected item paths a body names, in body order, unparsed.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> ProtectedItemPaths(JsonElement desired) {
        var items = Property(desired, "protectedItems");
        if (items is not { ValueKind: JsonValueKind.Array } array) {
            return [];
        }

        return [.. array.EnumerateArray().Where(x => x.ValueKind is JsonValueKind.String).Select(x => x.GetString() ?? string.Empty)];
    }

    /// <summary>The JSON pointer of the item at <paramref name="index" /> — where a refusal of it is targeted.</summary>
    /// <param name="index">The item's position in <c>protectedItems</c>.</param>
    public static string ItemPointer(int index) => "/properties/protectedItems/" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Parses one protected item path and checks the three things the schema's format cannot:
    ///     that the item is in the vault's tenant, in the vault's resource group, and of the one type
    ///     this vault backs up.
    /// </summary>
    /// <param name="vault">The vault's address.</param>
    /// <param name="index">The item's position, for the refusal's target.</param>
    /// <param name="path">The path, as the body carries it.</param>
    /// <returns>The parsed address with no GUID, or <c>InvalidRequestBody</c> targeting the item.</returns>
    /// <remarks>
    ///     ⚠ <b>The tenant check is the one the view would make anyway, and it is made here too so
    ///     the refusal has a pointer.</b> The view answers <c>ResourceNotFound</c> for another
    ///     tenant's path — deliberately indistinguishable from an absent one — and a vault that
    ///     passed that answer on would tell a tenant "does not exist" about a path they can see is
    ///     spelled with somebody else's tenant id. Refusing the spelling at the API is not an
    ///     oracle: the tenant id in the path is the caller's own to compare against.
    /// </remarks>
    public static Result<ResourceId> ItemOf(ResourceId vault, int index, string path) {
        if (!ResourceId.TryParsePath(path, out var item)) {
            return Result<ResourceId>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{path}' is not a resource id path.",
                ItemPointer(index)
            );
        }

        if (item.TenantId != vault.TenantId) {
            return Result<ResourceId>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{path}' names tenant {item.TenantId:D}, and the vault is in tenant {vault.TenantId:D}. A "
                + "vault protects resources in its own tenant only.",
                ItemPointer(index)
            );
        }

        if (item.SubscriptionId != vault.SubscriptionId
            || !string.Equals(item.ResourceGroup, vault.ResourceGroup, StringComparison.Ordinal)) {
            return Result<ResourceId>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{path}' is in resource group '{item.ResourceGroup}' of subscription {item.SubscriptionId:D}, "
                + $"and the vault is in '{vault.ResourceGroup}' of {vault.SubscriptionId:D}. A vault protects "
                + "resources in its own resource group: its actions find its objects in that group's "
                + "namespace and nowhere else (charts/managed/recovery-vault/conformance.yaml § owed, "
                + "items-in-other-resource-groups).",
                ItemPointer(index)
            );
        }

        // ⚠ ResourceTypeName's equality is case-insensitive, the way the registry's lookup is.
        if (item.Type != PostgresServerType) {
            return Result<ResourceId>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{path}' is a {item.Type}, and this vault backs up {PostgresServerType} only. A file "
                + "share has no snapshot story on this platform — the SeaweedFS CSI driver advertises no "
                + "snapshot capability — and every other type is owed "
                + "(charts/managed/recovery-vault/conformance.yaml § owed).",
                ItemPointer(index)
            );
        }

        return Result<ResourceId>.Success(item);
    }

    /// <summary>
    ///     What a protected server's own contract says about its backups — read off the snapshot the
    ///     view returned, at the pointers <c>openapi/2026-08-01.json</c> publishes for
    ///     <c>CyberCloud.DBforPostgreSQL/servers</c>.
    /// </summary>
    /// <param name="serverBody">The server's body, as <see cref="ResourceSnapshot.Body" /> carries it.</param>
    /// <returns>Whether the server archives at all, and for how many days it keeps a base backup.</returns>
    /// <remarks>
    ///     ⚠ <b>The defaults are the other provider's, copied from its published document and not from
    ///     its code.</b> The write path stores a body as sent, so a server created with no
    ///     <c>backup</c> block has <c>enabled: true</c> and <c>retentionDays: 14</c> by that type's
    ///     schema defaults, and a reader that treated absence as <c>false</c> would refuse every
    ///     server created from the portal's defaults. The two numbers are pinned against the document
    ///     by <c>RecoveryVaultDeclarationTests.TheServerContractDefaultsAreTheOnesThePublishedDocumentGives</c>,
    ///     so a change to that type's api-version turns this reader red rather than silently wrong.
    /// </remarks>
    public static (bool Enabled, int RetentionDays) ServerBackupContract(string serverBody) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(serverBody);
        } catch (JsonException) {
            return (ServerBackupEnabledDefault, ServerRetentionDaysDefault);
        }

        var backup = (parsed as JsonObject)?["properties"] is JsonObject properties ? properties["backup"] as JsonObject : null;

        var enabled = backup?["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var value)
            ? value
            : ServerBackupEnabledDefault;

        var retention = backup?["retentionDays"] is JsonValue days && days.TryGetValue<int>(out var number)
            ? number
            : ServerRetentionDaysDefault;

        return (enabled, retention);
    }

    /// <summary>The published default of <c>/properties/backup/enabled</c> on a PostgreSQL server.</summary>
    public const bool ServerBackupEnabledDefault = true;

    /// <summary>The published default of <c>/properties/backup/retentionDays</c> on a PostgreSQL server.</summary>
    public const int ServerRetentionDaysDefault = 14;

    /// <summary>The pointers this vault reads off a protected server's contract, for the test that pins them.</summary>
    public static ImmutableArray<string> ServerContractPointers { get; } = ["/properties/backup/enabled", "/properties/backup/retentionDays"];

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The <c>ScheduledBackup</c> document one protected item becomes.</summary>
    /// <param name="vault">The vault's own name.</param>
    /// <param name="item">The protected item's resource name.</param>
    /// <param name="cluster">The CloudNativePG cluster the item rendered — found through the view, never predicted.</param>
    /// <param name="desired">The vault's validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>immediate: true</c>: protection begins with a recovery point, not with a wait
    ///         for the first tick.</b> A vault created at 09:00 with the default schedule would
    ///         otherwise hold nothing until 02:00 the next day, and a tenant who checked it at 10:00
    ///         would read "0 recovery points" as the vault not working. The first Backup also
    ///         surfaces a store that cannot take one — a server whose <c>destinationPath</c> was
    ///         never filled in — within minutes rather than the next morning.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here — ADR-013's seven and
    ///         <see cref="ProtectedItemLabel" /> are injected by <c>KubeCommand</c>.
    ///     </para>
    /// </remarks>
    public static string ScheduledBackupJson(string vault, string item, string cluster, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(cluster);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ScheduledBackupNameOf(vault, item) },
            ["spec"] = new JsonObject {
                ["schedule"] = SixFieldSchedule(Schedule(desired)),
                ["cluster"] = new JsonObject { ["name"] = cluster },
                ["backupOwnerReference"] = BackupOwnerReference,
                ["method"] = BackupMethod,
                ["immediate"] = true
            }
        }.ToJsonString();
    }

    /// <summary>The five-field schedule with CloudNativePG's seconds field prepended.</summary>
    /// <param name="fiveFields">A schedule <see cref="CronPattern" /> admits.</param>
    public static string SixFieldSchedule(string fiveFields) => "0 " + fiveFields;

    /// <summary>
    ///     The <c>Cluster</c> document a restore creates: bootstrapped from a recovery point, sized
    ///     like the source, and carrying no backup section of its own.
    /// </summary>
    /// <param name="targetName">The new cluster's name.</param>
    /// <param name="recoveryPoint">The Backup's name.</param>
    /// <param name="sourceClusterJson">The protected server's <c>Cluster</c>, as the API server returned it.</param>
    /// <remarks>
    ///     <para>
    ///         <c>bootstrap.recovery.backup.name</c> is CloudNativePG's "recover from a Backup object
    ///         in this namespace" — the operator reads the Backup's <c>status.barmanObjectStore</c>
    ///         for the store and the server name, so nothing about the store is repeated here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One instance and NO <c>backup</c> block, on purpose.</b> A restored cluster that
    ///         inherited the source's <c>barmanObjectStore</c> would archive into the <i>same</i>
    ///         destination under the same server name and overwrite the source's WAL — the
    ///         restore-in-place docs/plan/15 forbids, reached by a side door. The storage size and
    ///         class and the image are copied from the source because a recovery needs a volume at
    ///         least as large as the one it came from and a PostgreSQL major at least as new.
    ///     </para>
    /// </remarks>
    public static string RestoredClusterJson(string targetName, string recoveryPoint, string sourceClusterJson) {
        ArgumentException.ThrowIfNullOrEmpty(targetName);
        ArgumentException.ThrowIfNullOrEmpty(recoveryPoint);

        var source = (JsonNode.Parse(sourceClusterJson) as JsonObject)?["spec"] as JsonObject;

        var storage = new JsonObject { ["size"] = (source?["storage"] as JsonObject)?["size"]?.GetValue<string>() ?? "20Gi" };
        if ((source?["storage"] as JsonObject)?["storageClass"]?.GetValue<string>() is { Length: > 0 } storageClass) {
            storage["storageClass"] = storageClass;
        }

        var spec = new JsonObject {
            ["instances"] = 1,
            ["storage"] = storage,
            ["bootstrap"] = new JsonObject {
                ["recovery"] = new JsonObject { ["backup"] = new JsonObject { ["name"] = recoveryPoint } }
            }
        };

        if (source?["imageName"]?.GetValue<string>() is { Length: > 0 } image) {
            spec["imageName"] = image;
        }

        return new JsonObject { ["metadata"] = new JsonObject { ["name"] = targetName }, ["spec"] = spec }.ToJsonString();
    }

    /// <summary>
    ///     Whether a ScheduledBackup read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="cluster">The CloudNativePG cluster the item should name.</param>
    /// <param name="desired">The vault's desired body.</param>
    /// <remarks>
    ///     ⚠ Containment: the operator writes <c>status</c> and its mutating webhook may default
    ///     <c>target</c> and <c>online</c>, so an equality compare would report every reconciled
    ///     schedule as drifted. The three fields the vault owns are compared — the schedule, the
    ///     cluster and the owner reference — and the schedule is the one that most needs comparing,
    ///     because a schedule rewritten by hand is a policy the tenant did not set.
    /// </remarks>
    public static bool Matches(string objectJson, string cluster, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document || document["spec"] is not JsonObject spec) {
            return false;
        }

        // ⚠ The `null` case is the object as this provider RENDERS it — KubeCommandBuilder injects
        // `kind`, so a rendered body carries none until it has been applied and read back.
        if (document["kind"]?.GetValue<string>() is { } kind && kind != ScheduledBackupKind.Kind) {
            return false;
        }

        return spec["schedule"]?.GetValue<string>() == SixFieldSchedule(Schedule(desired))
            && (spec["cluster"] as JsonObject)?["name"]?.GetValue<string>() == cluster
            && spec["backupOwnerReference"]?.GetValue<string>() == BackupOwnerReference;
    }

    // ── Recovery points, read ─────────────────────────────────────────────────────────────────

    /// <summary>One recovery point, as read off a <c>Backup</c> object.</summary>
    /// <param name="Item">The protected item's resource name.</param>
    /// <param name="Name">The Backup's name — what <see cref="RecoverAction" /> takes.</param>
    /// <param name="Phase">CloudNativePG's <c>status.phase</c>, or <c>pending</c> when it has written none.</param>
    /// <param name="StartedAt">When the backup started, or <see langword="null" />.</param>
    /// <param name="StoppedAt">When it stopped, or <see langword="null" />.</param>
    /// <param name="Method">The method the operator recorded, or the spec's.</param>
    /// <param name="Error">The operator's error text, or empty.</param>
    /// <param name="CreatedAt">The object's <c>creationTimestamp</c>, which is what retention is measured from.</param>
    public readonly record struct RecoveryPoint(
        string Item,
        string Name,
        string Phase,
        DateTimeOffset? StartedAt,
        DateTimeOffset? StoppedAt,
        string Method,
        string Error,
        DateTimeOffset? CreatedAt
    ) {
        /// <summary>Whether the point is restorable — CloudNativePG's <c>completed</c>.</summary>
        public bool IsCompleted => string.Equals(Phase, CompletedPhase, StringComparison.Ordinal);
    }

    /// <summary>CloudNativePG's phase for a Backup that can be restored. <c>BackupPhaseCompleted</c>.</summary>
    public const string CompletedPhase = "completed";

    /// <summary>Reads a recovery point off a Backup's JSON.</summary>
    /// <param name="item">The protected item the Backup belongs to.</param>
    /// <param name="backupJson">The Backup, as the API server returned it.</param>
    /// <returns>The point, or <see langword="null" /> when the document is not a Backup.</returns>
    public static RecoveryPoint? RecoveryPointOf(string item, string backupJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(backupJson);
        } catch (JsonException) {
            return null;
        }

        if (parsed is not JsonObject document || document["metadata"] is not JsonObject metadata) {
            return null;
        }

        var name = metadata["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        var status = document["status"] as JsonObject;
        var spec = document["spec"] as JsonObject;

        return new(
            item,
            name,
            status?["phase"]?.GetValue<string>() is { Length: > 0 } phase ? phase : "pending",
            Stamp(status?["startedAt"]),
            Stamp(status?["stoppedAt"]),
            status?["method"]?.GetValue<string>() ?? spec?["method"]?.GetValue<string>() ?? BackupMethod,
            status?["error"]?.GetValue<string>() ?? string.Empty,
            Stamp(metadata["creationTimestamp"])
        );
    }

    /// <summary>One recovery point, as <see cref="ListRecoveryPointsResponse" />'s <c>/recoveryPoints</c> spells it.</summary>
    /// <param name="point">The point.</param>
    public static string RecoveryPointLine(RecoveryPoint point) {
        var line = $"{point.Item} {point.Name} {point.Phase} started {Spell(point.StartedAt)} stopped {Spell(point.StoppedAt)} method {point.Method}";
        return point.Error.Length > 0 ? line + ": " + point.Error : line;
    }

    /// <summary>The <see cref="ListRecoveryPointsResponse" /> body for a set of points.</summary>
    /// <param name="points">Every point found, in any order; newest first in the answer.</param>
    public static string RecoveryPointsJson(IEnumerable<RecoveryPoint> points) {
        var ordered = points
            .OrderByDescending(x => x.StartedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        return new JsonObject {
            ["count"] = ordered.Count,
            ["completed"] = ordered.Count(x => x.IsCompleted),
            ["recoveryPoints"] = new JsonArray([.. ordered.Select(x => (JsonNode?)RecoveryPointLine(x))])
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether a recovery point is older than the vault's retention and should be pruned.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <param name="retentionDays">The vault's <c>policy.retentionDays</c>.</param>
    /// <param name="now">The pass's clock.</param>
    /// <remarks>
    ///     ⚠ Measured from when the backup <i>stopped</i>, falling back to when the object was created;
    ///     a point with neither is never pruned, because a Backup the operator has not stamped yet is
    ///     one it is still working on. A failed point is pruned on the same clock as a completed
    ///     one — it is a record of a failure the tenant should have read by then, and keeping failures
    ///     forever is how <c>listRecoveryPoints</c> becomes unreadable.
    /// </remarks>
    public static bool IsExpired(RecoveryPoint point, int retentionDays, DateTimeOffset now) =>
        (point.StoppedAt ?? point.CreatedAt) is { } at && now - at > TimeSpan.FromDays(retentionDays);

    /// <summary>The name of the CloudNativePG cluster a Backup was taken of, off <c>spec.cluster.name</c>.</summary>
    /// <param name="backupJson">The Backup, as the API server returned it.</param>
    public static string BackupClusterOf(string backupJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(backupJson);
        } catch (JsonException) {
            return string.Empty;
        }

        return (((parsed as JsonObject)?["spec"] as JsonObject)?["cluster"] as JsonObject)?["name"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>The ScheduledBackup a Backup was made by, off the operator's own label, or empty.</summary>
    /// <param name="backupJson">The Backup, as the API server returned it.</param>
    public static string BackupParentOf(string backupJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(backupJson);
        } catch (JsonException) {
            return string.Empty;
        }

        return (((parsed as JsonObject)?["metadata"] as JsonObject)?["labels"] as JsonObject)?[ParentScheduledBackupLabel]?.GetValue<string>()
            ?? string.Empty;
    }

    /// <summary>
    ///     A Backup document as the ScheduledBackup controller leaves one — for the conformance case,
    ///     the handler's tests and the fake cluster, none of which run an operator.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="scheduledBackup">The ScheduledBackup that made it — written into the operator's own label.</param>
    /// <param name="cluster">The cluster it was taken of.</param>
    /// <param name="name">The Backup's name.</param>
    /// <param name="phase">The phase to record.</param>
    /// <param name="startedAt">When it started.</param>
    /// <param name="stoppedAt">When it stopped, or <see langword="null" />.</param>
    /// <param name="ownerUid">
    ///     The ScheduledBackup's uid to write into <c>ownerReferences</c>; <see cref="string.Empty" />
    ///     for an owner reference whose uid the conformance harness fills in from the object the
    ///     reconciler applied; <see langword="null" /> for no owner reference at all.
    /// </param>
    /// <param name="error">The operator's error text, or empty.</param>
    /// <remarks>
    ///     ⚠ Labelled the way the operator labels — <see cref="ParentScheduledBackupLabel" /> and
    ///     <see cref="ClusterLabel" /> — and carrying <b>none</b> of the platform's seven, which is the
    ///     real shape: <c>CreateBackup</c> copies no labels from the ScheduledBackup.
    /// </remarks>
    public static string OperatorBackupJson(
        string ns,
        string scheduledBackup,
        string cluster,
        string name,
        string phase,
        DateTimeOffset startedAt,
        DateTimeOffset? stoppedAt,
        string? ownerUid = null,
        string error = ""
    ) {
        var metadata = new JsonObject {
            ["name"] = name,
            ["namespace"] = ns,
            ["creationTimestamp"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
            ["labels"] = new JsonObject {
                [ParentScheduledBackupLabel] = scheduledBackup,
                [ClusterLabel] = cluster
            }
        };

        if (ownerUid is not null) {
            metadata["ownerReferences"] = new JsonArray(
                KubeJson.OwnerReference(
                    new() { ApiVersion = ScheduledBackupKind.ApiVersion, Kind = ScheduledBackupKind.Kind, Name = scheduledBackup, Uid = ownerUid }
                )
            );
        }

        var status = new JsonObject {
            ["phase"] = phase,
            ["method"] = BackupMethod,
            ["startedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture)
        };

        if (stoppedAt is { } stopped) {
            status["stoppedAt"] = stopped.ToString("O", CultureInfo.InvariantCulture);
        }

        if (error.Length > 0) {
            status["error"] = error;
        }

        return new JsonObject {
            ["apiVersion"] = BackupKind.ApiVersion,
            ["kind"] = BackupKind.Kind,
            ["metadata"] = metadata,
            ["spec"] = new JsonObject { ["cluster"] = new JsonObject { ["name"] = cluster }, ["method"] = BackupMethod },
            ["status"] = status
        }.ToJsonString();
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the vault is placed on.</param>
    /// <param name="protectedItems">The resource id paths the vault protects.</param>
    /// <param name="schedule">The five-field schedule.</param>
    /// <param name="retentionDays">How long a point is kept.</param>
    /// <param name="location">The region.</param>
    /// <remarks>⚠ Every property it writes is a <b>leaf</b>, for the reason <c>StorageAccounts.Body</c> gives.</remarks>
    public static string Body(
        Guid clusterId,
        ImmutableArray<string> protectedItems,
        string schedule = DefaultSchedule,
        int retentionDays = DefaultRetentionDays,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["policy"] = new JsonObject { ["schedule"] = schedule, ["retentionDays"] = retentionDays },
                ["protectedItems"] = new JsonArray([.. protectedItems.Select(x => (JsonNode?)x)])
            }
        }.ToJsonString();

    // ⚠ The same literals as the `DefaultJson` above, for the reason StorageAccounts states: the write
    // path stores a body AS SENT, and a reader that spelled the default inline would be a second place
    // it lives.
    const string DefaultSchedule = "0 2 * * *";
    const int DefaultRetentionDays = 14;

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string name) =>
        Property(desired, name) is { ValueKind: JsonValueKind.Object } section ? section : null;

    static string Text(JsonElement? section, string name) =>
        section is { } s && s.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    static int Number(JsonElement? section, string name, int fallback) =>
        section is { } s && s.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : fallback;

    static DateTimeOffset? Stamp(JsonNode? node) =>
        node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    static string Spell(DateTimeOffset? at) => at is { } value ? value.ToString("O", CultureInfo.InvariantCulture) : "-";
}
