using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Storage/accounts/fileShares</c> — the RWX access
///     shape of docs/plan/15 § The three kinds, as a child of the account whose filer holds it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE AUTHORITY IS docs/plan/15 § File storage, AND ITS TABLE SPELLS THE TYPE WRONG.</b>
///         § The three kinds writes
///         <i>
///             "File · <c>CyberCloud.Storage/fileShares</c> · SeaweedFS
///             FUSE/NFS, or LINSTOR RWX + an NFS server · Mounted by VMs and pods"
///         </i> — a top-level type.
///         It ships as <c>accounts/fileShares</c>, a child, because a share's bytes live in a
///         <b>filer</b> and the only filer this platform runs is the one inside an account's
///         <c>Seaweed</c>. A top-level share would need a filer of its own or a platform-wide one, and
///         the second is a tenancy boundary docs/plan/06 does not have. The same argument put buckets
///         under the account; it holds harder here, because a bucket is a name inside the filer and a
///         share is a directory inside it. docs/plan/15 is corrected in place.
///     </para>
///     <para>
///         ⚠ <b>THE BACKEND IS THE SEAWEEDFS CSI DRIVER, AND THERE IS NO NFS ANYWHERE BEHIND IT.</b>
///         docs/plan/15 § File storage says <i>"NFS first"</i> and names the backing as
///         <i>
///             "SeaweedFS's NFS/FUSE mount for scale-out shares, or a LINSTOR RWX volume with an NFS
///             server pod"
///         </i>. Read against the sources rather than the README:
///         <c>weed/command/command.go</c> at 4.41 has <c>cmdMount</c>, <c>cmdFuse</c>,
///         <c>cmdWebDav</c>, <c>cmdSftp</c> and <c>cmdS3</c> and <b>no NFS command at all</b>, and
///         nothing in <c>charts/bundle/</c> installs LINSTOR. What SeaweedFS does have is a CSI
///         driver — <c>seaweedfs-csi-driver</c>, which <c>weed mount</c>s a filer path into a pod over
///         FUSE and advertises <c>MULTI_NODE_MULTI_WRITER</c> — and the operator pinned in the bundle
///         deploys it from a <c>SeaweedCSIDriver</c> custom resource. So the pod half of
///         <i>"Mountable from a tenant's VMs and pods"</i> is what ships, and the VM half is
///         <c>charts/managed/seaweedfs-fileshare/conformance.yaml § owed</c>, <c>nfs-is-not-served</c>.
///         ⚠ No <c>protocol</c> property is declared, for the reason <c>encryption-at-rest</c> gave on
///         the account: a property that says <c>NFS</c> over a backend that serves none is a promise
///         the product page makes and the cluster does not.
///     </para>
///     <para>
///         ⚠ <b>TWO OBJECTS, AND ONE OF THEM IS SHARED WITH EVERY OTHER SHARE IN THE ACCOUNT.</b> The
///         CSI driver is one instance per <i>filer</i> — <c>--filer=</c> is a driver argument, not a
///         StorageClass parameter (<c>seaweedfs-csi-driver</c>'s README:
///         <i>
///             "Adjust your SeaweedFS
///             Filer address via variable SEAWEEDFS_FILER"
///         </i>) — so the driver belongs to the account
///         and not to the share. It is rendered <i>here</i> rather than by
///         <c>StorageAccountReconciler</c> because a driver is a controller Deployment plus a node
///         DaemonSet plus a mount DaemonSet on <b>every node</b>, and an account with no shares
///         should not pay for one. So the first share of an account applies the driver, every share
///         re-applies it (server-side apply of a document that is a pure function of the account —
///         ⚠ not a no-op, because the builder stamps the applying share's labels; see
///         <c>StorageFileShareReconciler</c> for why it is applied anyway), and the <i>last</i> share
///         out removes it — see <c>StorageFileShareReconciler.DeleteAsync</c> for how "last" is
///         decided without the platform being able to enumerate children, and for why the driver
///         waits for the released volume as well as for the sibling claims.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The share's <c>quota.size</c> is enforced, and the thing that enforces it is the
///             mount.
///         </b> <c>pkg/driver/mounter.go</c> passes
///         <c>collectionQuotaMB: initialCollectionQuotaMB(volumeContext[volumeCapacityKey])</c> and
///         <c>collection: path.Base(filerPath)</c> to <c>weed mount</c>, so every share is its own
///         SeaweedFS <i>collection</i> with a master-enforced ceiling equal to the claim's capacity.
///         That makes the size a real limit — and, like a bucket's, a limit <i>inside</i> capacity the
///         account's volume servers already reserved. No derived quota meter is declared, for the
///         reason <see cref="StorageBuckets" /> gives: reserving the same gibibyte twice.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the body names the account</b>, for the reason
///         <see cref="StorageBuckets" /> states at length: the address does, and
///         <see cref="ResourceId.Parent" /> is a pure function of it. <see cref="AccountOf" /> is the
///         only reader.
///     </para>
/// </remarks>
public static class StorageFileShares {
    /// <summary>The provider namespace — the account's, because a child shares its parent's.</summary>
    public const string ProviderNamespace = StorageAccounts.ProviderNamespace;

    /// <summary>
    ///     The type path. ⚠ <b><c>accounts/fileShares</c>, interleaved</b> — see the remarks on this
    ///     class for why not docs/plan/15's flattened <c>fileShares</c>.
    /// </summary>
    public const string TypePath = "accounts/fileShares";

    /// <summary>The one api-version. ⚠ Equal to the account's, for the reason <see cref="StorageBuckets.V2026" /> gives.</summary>
    public const string V2026 = StorageAccounts.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/seaweedfs-fileshare";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    /// <remarks>
    ///     ⚠ Carried and not compared to the account's, exactly as the bucket's is —
    ///     <c>charts/managed/seaweedfs-bucket/conformance.yaml § owed</c>,
    ///     <c>bucket-cluster-may-differ-from-its-accounts</c>. On this type the wrong cluster produces a
    ///     <c>SeaweedCSIDriver</c> whose <c>seaweedRef</c> resolves to nothing, which the operator
    ///     reports as <c>ClusterReachable=False</c> on the driver and the claim stays <c>Pending</c>.
    /// </remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller what it needs to mount the share.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/15 § File storage: <i>"Mountable from a tenant's VMs and pods"</i>. What a pod
    ///         needs is the claim's name; what a VM running <c>weed mount</c> needs is the filer
    ///         address, the path under it and the collection the quota is enforced on. All of it is
    ///         computed or observed, none of it is minted — see
    ///         <c>StorageFileShareListMountTargetsHandler</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>read</c>, not a permission of its own, and not <c>secret: true</c>.</b> Nothing in
    ///         the response is a credential: the filer's HTTP port answers any caller on the cluster
    ///         network, and a claim is mountable by any pod in its namespace whether or not this action
    ///         was called. docs/plan/07 § Consistency reserves the fully-consistent row for a
    ///         <i>
    ///             key
    ///             export
    ///         </i>, and a mount target is an address. The access control that would make it more
    ///         than an address — docs/plan/15's <i>"access rules by subnet and by managed identity"</i>
    ///         — is owed, and this permission is where it would land.
    ///     </para>
    /// </remarks>
    public const string ListMountTargetsAction = "listMountTargets";

    /// <summary>The permission <see cref="ListMountTargetsAction" /> checks. See its remarks.</summary>
    public const string ListMountTargetsPermission = "read";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The objects a share IS ────────────────────────────────────────────────────────────────

    /// <summary>The <c>core/v1</c> <c>PersistentVolumeClaim</c> a share is.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim is the share.</b> Every other object this provider renders is a custom
    ///     resource an operator expands; this one is a built-in kind the CSI external-provisioner
    ///     binds, and it is what a tenant's pod names. The plural is carried for the reason
    ///     <see cref="GroupVersionKind.Plural" /> gives, and the core group is empty as
    ///     <see cref="StorageAccounts.SecretKind" /> is.
    /// </remarks>
    public static GroupVersionKind ClaimKind { get; } =
        new() { Group = "", Version = "v1", Kind = "PersistentVolumeClaim", Plural = "persistentvolumeclaims" };

    /// <summary>The <c>SeaweedCSIDriver</c> custom resource — one per account, applied by its shares.</summary>
    /// <remarks>
    ///     ⚠ <c>api/v1/seaweedcsidriver_types.go</c> at operator 0.1.38: <b>namespaced</b>, exactly
    ///     one of <c>seaweedRef</c> or <c>filerAddress</c>, an immutable <c>driverName</c> that is a
    ///     cluster-wide singleton, and an optional <c>storageClass</c> block the controller turns into
    ///     a cluster-scoped <c>StorageClass</c> named after the driver. The plural is <c>seaweedcsidrivers</c>.
    /// </remarks>
    public static GroupVersionKind CsiDriverKind { get; } =
        new() {
            Group = "seaweed.seaweedfs.com", Version = "v1", Kind = "SeaweedCSIDriver", Plural = "seaweedcsidrivers"
        };

    /// <summary>
    ///     The <c>core/v1</c> <c>PersistentVolume</c> the provisioner binds a share's claim to — read
    ///     and labelled by the delete, never rendered.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE VOLUME OUTLIVES THE CLAIM, AND THE DRIVER MUST OUTLIVE THE VOLUME.</b> Deleting
    ///         a claim releases its volume; with <c>reclaimPolicy: Delete</c> the CSI external-provisioner
    ///         then calls <c>DeleteVolume</c> on the controller plugin and removes the
    ///         <c>PersistentVolume</c> — and that provisioner runs in the <c>SeaweedCSIDriver</c>'s
    ///         controller Deployment. A delete that removed the driver the moment the claim read
    ///         <c>NotFound</c> would, whenever the volume was still <c>Released</c>, take away the one
    ///         process able to reclaim it: the volume and the filer directory <c>/fileshares/pvc-…</c>
    ///         then stay forever, which is the untracked, unbilled state <see cref="DriverJson" /> chose
    ///         <c>Delete</c> over <c>Retain</c> to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cluster-scoped and not this platform's to create</b> — the provisioner writes it,
    ///         with no labels of this platform's. So <c>StorageFileShareReconciler.DeleteAsync</c> reads
    ///         the claim's <c>spec.volumeName</c> <i>before</i> deleting the claim and stamps the eight
    ///         labels onto the volume through the same builder every rendered object goes through. That
    ///         is the only state a later pass has: once the claim is gone nothing else names the volume,
    ///         and a stateless reconciler cannot remember it. The labelled volume is then listed, by
    ///         account, until it is gone, and only then does the driver go.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind VolumeKind { get; } =
        new() { Group = "", Version = "v1", Kind = "PersistentVolume", Plural = "persistentvolumes" };

    /// <summary>The one access mode a share is mounted with.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole point of the type, and checked in the driver's source rather than assumed.</b>
    ///     <c>pkg/driver/driver.go</c> at v1.4.20 advertises
    ///     <c>MULTI_NODE_MULTI_WRITER</c> beside the three single-node modes, which is what lets the
    ///     external-provisioner bind a <c>ReadWriteMany</c> claim. Not a property: a share that is not
    ///     RWX is a disk, and disks are <c>CyberCloud.Compute/disks</c>.
    /// </remarks>
    public const string AccessMode = "ReadWriteMany";

    /// <summary>The filer directory every share's volume is created under.</summary>
    /// <remarks>
    ///     ⚠ <b><c>/fileshares</c> and not the driver's default, which is <c>/buckets</c>.</b>
    ///     <c>pkg/driver/controllerserver.go</c> creates a volume at <c>{parentDir}/{name}</c> and
    ///     defaults <c>parentDir</c> to <c>/buckets</c> — the directory the S3 gateway serves as its
    ///     bucket list. A share left there would appear to every S3 client of the account as a bucket
    ///     called <c>pvc-…</c>, listable and deletable through the wrong API. Rendered as the
    ///     StorageClass's <c>parentDir</c> parameter, so it applies to every claim the class provisions.
    /// </remarks>
    public const string SharesParentDir = "/fileshares";

    /// <summary>The label that says which account a share's objects belong to.</summary>
    /// <remarks>
    ///     ⚠ <b>The eighth label, and the reason it exists is the delete.</b> ADR-013's seven name the
    ///     <i>resource</i>; nothing among them says which account a claim is in, and the object name
    ///     — <c>{account}-{share}</c> — cannot be split back unambiguously when account and share
    ///     names both contain hyphens. <c>StorageFileShareReconciler.DeleteAsync</c> lists the claims
    ///     carrying this label to decide whether the account's driver still has a user, and the
    ///     volumes carrying it to decide whether the driver still has something to reclaim. Carried
    ///     through <c>WithLabels</c>, so it goes through the same syntax check the seven do.
    /// </remarks>
    public const string AccountLabel = "storage.cybercloud.io/account";

    /// <summary>The filer's HTTP port. <c>seaweedv1.FilerHTTPPort</c>.</summary>
    public const int FilerPort = 8888;

    /// <summary>The account a share's address names.</summary>
    /// <param name="id">The share's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string AccountOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no account whose filer could hold the share.",
            nameof(id)
        );

    /// <summary>The claim's name: the account's and the share's, joined.</summary>
    /// <param name="id">The share's address.</param>
    /// <remarks>
    ///     ⚠ Qualified by the account for the reason <see cref="StorageBuckets.ObjectNameOf" /> gives:
    ///     two accounts in one resource group share a namespace, and each may hold a share called
    ///     <c>home</c>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string ObjectNameOf(ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the claim it renders would collide with every "
                + "other account's share of the same name in the same resource group.",
                nameof(id)
            )
            : id.ParentNames.Replace('/', '-') + "-" + id.Name;

    /// <summary>The <c>SeaweedCSIDriver</c> object's name: <c>{account}-csi</c>.</summary>
    /// <param name="account">The account's own name.</param>
    /// <remarks>
    ///     ⚠ A share called <c>csi</c> renders a <i>claim</i> called <c>{account}-csi</c> too, and that
    ///     is not a collision: a claim and a driver are different kinds on different REST paths. The
    ///     operator's own children — <c>{account}-csi-controller</c>, <c>-node</c>, <c>-mount</c> — are
    ///     Deployments and DaemonSets, which this provider never renders.
    /// </remarks>
    public static string DriverObjectName(string account) => account + "-csi";

    /// <summary>
    ///     The CSI driver's <c>driverName</c>, which is also the <c>StorageClass</c>'s name.
    /// </summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="account">The account's own name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CLUSTER-UNIQUE, AND AN ACCOUNT NAME IS NOT.</b> The controller writes a
    ///         cluster-scoped <c>CSIDriver</c> and a cluster-scoped <c>StorageClass</c> under this
    ///         name, and refuses a second <c>SeaweedCSIDriver</c> claiming it — the older one wins,
    ///         <c>CSIConditionDriverNameConflict</c>. Two accounts called <c>main</c> in two resource
    ///         groups are ordinary, so the namespace is folded in through a digest rather than spelled
    ///         out: <c>{subscriptionId:N}-{resourceGroup}</c> alone can be 96 characters and the CRD
    ///         caps this field at 63 under <c>^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$</c>.
    ///     </para>
    ///     <para>
    ///         The first 24 characters of the account's name are kept so that <c>kubectl get sc</c>
    ///         still reads as something a person can place; the 16 hex digits after it are what make
    ///         it unique.
    ///     </para>
    /// </remarks>
    public static string DriverNameOf(string ns, string account) {
        ArgumentException.ThrowIfNullOrEmpty(ns);
        ArgumentException.ThrowIfNullOrEmpty(account);

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ns + "/" + account)))[..16];
        var stem = (account.Length > 24 ? account[..24] : account).TrimEnd('-');

        return stem + "-" + digest + ".csi.cybercloud.io";
    }

    /// <summary>The in-cluster filer address a VM's <c>weed mount</c> would name.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="account">The account's own name.</param>
    /// <remarks>
    ///     ⚠ <c>controller_filer_service.go</c> names the Service <c>m.Name + "-filer"</c> and exposes
    ///     <c>filer-http</c> on <see cref="FilerPort" />. Not an object this provider applies — the
    ///     same rule <see cref="StorageAccounts.S3ServiceName" /> states.
    /// </remarks>
    public static string FilerAddress(string ns, string account) =>
        account + "-filer." + ns + ".svc:" + FilerPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The filer path a bound claim's volume lives at.</summary>
    /// <param name="volumeName">The <c>PersistentVolume</c>'s name, read off <c>spec.volumeName</c>.</param>
    /// <remarks>
    ///     ⚠ <c>controllerserver.go</c>: <c>VolumeId = volumePath = {parentDir}/{name}</c>, where the
    ///     name is what the external-provisioner asked for, which is the PV's name. Observed rather than
    ///     predicted — the PV name is <c>pvc-{uid}</c> by convention and nothing here should depend on
    ///     the convention.
    /// </remarks>
    public static string FilerPathOf(string volumeName) => SharesParentDir + "/" + volumeName;

    /// <summary>The claim a share owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The share's address.</param>
    public static ObjectRef ClaimRef(string ns, ResourceId id) =>
        new() { Kind = ClaimKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The account's driver, which every share of the account applies and reads.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The share's address.</param>
    public static ObjectRef DriverRef(string ns, ResourceId id) =>
        new() { Kind = CsiDriverKind, Namespace = ns, Name = DriverObjectName(AccountOf(id)) };

    /// <summary>The volume a bound claim names — cluster-scoped, so no namespace.</summary>
    /// <param name="volumeName">The name off the claim's <c>spec.volumeName</c>, never empty.</param>
    /// <exception cref="ArgumentException"><paramref name="volumeName" /> is empty.</exception>
    public static ObjectRef VolumeRef(string volumeName) {
        ArgumentException.ThrowIfNullOrEmpty(volumeName);

        return new() { Kind = VolumeKind, Namespace = string.Empty, Name = volumeName };
    }

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             ONE TENANT-FACING LEAF, AND THE THREE docs/plan/15 NAMES BESIDE IT ARE EACH
    ///             DECLINED WITH A REASON.
    ///         </b> § File storage lists
    ///         <i>
    ///             "size, performance tier, protocol,
    ///             access rules by subnet and by managed identity"
    ///         </i>. Size is here. The rest:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>
    ///                 <c>protocol</c>
    ///             </b> — the only value that document names is NFS and nothing behind
    ///             this type serves it; see the remarks on this class. Declared, it would be an enum
    ///             of one value that the cluster does not honour.
    ///         </item>
    ///         <item>
    ///             <b>
    ///                 <c>tier</c>
    ///             </b> — <i>"derived from the tier, not exposed"</i> chooses between the
    ///             SeaweedFS mount and a LINSTOR RWX volume, and <c>charts/bundle/</c> installs no
    ///             LINSTOR (docs/plan/15 § Block storage says so in as many words). An enum of one
    ///             value is a property that does nothing.
    ///         </item>
    ///         <item>
    ///             <b>access rules</b> — by subnet needs a network policy the CSI mount path does not
    ///             pass through, and by managed identity needs <c>CyberCloud.ManagedIdentity/*</c>,
    ///             which docs/plan/24 § What has landed lists as not shipped.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ Every default here is the chart's default, spelled as JSON — see
    ///         <see cref="StorageAccounts.Schema2026" />.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the share is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The share's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    true,
                    Description: "The cluster whose namespace holds the share. Must be the cluster the "
                    + "account is in — nothing checks that, and a share placed elsewhere is a claim "
                    + "against a driver whose filer reference resolves to nothing."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/quota",
                    SchemaKind.Nested,
                    Description: "How much the share may hold."
                ),
                new(
                    "/properties/quota/size",
                    SchemaKind.Text,
                    true,
                    Description: "The share's size, in Kubernetes quantity form. Enforced as a "
                    + "SeaweedFS collection quota on the mount. Grows online; never shrinks. ⚠ This "
                    + "is a ceiling inside capacity the account's volume servers already reserved; it "
                    + "does not add any, and docs/plan/15 § Metering bills the provisioned figure "
                    + "rather than what is used."
                ) { Pattern = StorageAccounts.QuantityPattern, DefaultJson = "\"100Gi\"", ExampleJson = "\"100Gi\"" }
            ]
        );

    /// <summary>What a <c>POST …/listMountTargets</c> returns.</summary>
    /// <remarks>
    ///     ⚠ Nothing in it is <c>Secret</c> — see <see cref="ListMountTargetsAction" /> — and every
    ///     field is <c>Required</c>, which is why the handler refuses rather than answering with an
    ///     empty <c>path</c> while the claim is still <c>Pending</c>.
    /// </remarks>
    public static ResourceSchema ListMountTargetsResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/claimName",
                    SchemaKind.Text,
                    true,
                    Description: "The PersistentVolumeClaim a pod in the resource group's namespace "
                    + "names under volumes[].persistentVolumeClaim.claimName."
                ),
                new(
                    "/accessMode",
                    SchemaKind.Text,
                    true,
                    Description: "The access mode the claim was bound with. Always ReadWriteMany."
                ),
                new(
                    "/filer",
                    SchemaKind.Text,
                    true,
                    Description: "The account's filer, host:port, for a `weed mount -filer=` from a VM "
                    + "on the cluster network. ⚠ In-cluster only, for the reason the account's "
                    + "listKeys endpoint is."
                ),
                new(
                    "/path",
                    SchemaKind.Text,
                    true,
                    Description: "The filer path the share lives at — `weed mount -filer.path=`."
                ),
                new(
                    "/collection",
                    SchemaKind.Text,
                    true,
                    Description: "The SeaweedFS collection the share's quota is enforced on — "
                    + "`weed mount -collection=`. A mount that omits it writes outside the quota."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The size a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string QuotaSize(JsonElement desired) {
        var found = Text(desired, "quota", "size");
        return found.Length > 0 ? found : DefaultQuotaSize;
    }

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The <c>SeaweedCSIDriver</c> document an account's shares apply.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The share's address — its account's name comes from here.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A pure function of the account and the namespace, and it has to be.</b> Every share
    ///         in the account applies this document on every pass, under one field manager; two shares
    ///         rendering it differently would take turns rewriting each other's version and neither
    ///         would ever report drift. Nothing from the share's own body reaches it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>seaweedRef</c> and not <c>filerAddress</c></b>, so the operator resolves the
    ///         filer itself and reports <c>ClusterReachable=False</c> by name when the account is
    ///         missing — an address string would fail as a mount timeout on a node instead. Same
    ///         namespace, so no <c>ResourceReferenceGrant</c> is needed:
    ///         <c>referencegrant.go</c>, <c>if from.Namespace == to.Namespace { return true, nil }</c>.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>reclaimPolicy: Delete</c>
    ///         </b>, for the reason <see cref="StorageBuckets" /> gave
    ///         against <c>Retain</c>: a released volume holding a directory no resource addresses is
    ///         untracked, unbilled and removable only by hand. What that costs is a recovery window, and
    ///         it is recorded rather than half-built — <c>conformance.yaml § owed</c>,
    ///         <c>no-recovery-window</c>.
    ///     </para>
    /// </remarks>
    public static string DriverJson(string ns, ResourceId id) {
        var account = AccountOf(id);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = DriverObjectName(account) },
            ["spec"] = new JsonObject {
                ["seaweedRef"] = new JsonObject { ["name"] = account },
                ["driverName"] = DriverNameOf(ns, account),
                ["storageClass"] = new JsonObject {
                    ["reclaimPolicy"] = "Delete",
                    ["allowVolumeExpansion"] = true,
                    ["parameters"] = new JsonObject { ["parentDir"] = SharesParentDir }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The <c>PersistentVolumeClaim</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The share's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ No labels, no annotations and no namespace here — ADR-013's seven and
    ///     <see cref="AccountLabel" /> are injected by <c>KubeCommand</c>, the eighth through
    ///     <c>WithLabels</c> so that it passes the same syntax check.
    /// </remarks>
    public static string ClaimJson(string ns, ResourceId id, JsonElement desired) =>
        new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) },
            ["spec"] = new JsonObject {
                ["accessModes"] = new JsonArray { AccessMode },
                ["storageClassName"] = DriverNameOf(ns, AccountOf(id)),
                ["resources"] = new JsonObject { ["requests"] = new JsonObject { ["storage"] = QuotaSize(desired) } }
            }
        }.ToJsonString();

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="id">The share's address.</param>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, and on the claim it is forced by Kubernetes itself</b>: the API server
    ///         writes <c>spec.volumeName</c>, <c>spec.volumeMode</c> and <c>status</c> onto a claim it
    ///         binds, so an equality comparison would report every bound share as drifted.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The <c>storageClassName</c> is compared and it is the field that most needs
    ///             comparing
    ///         </b>: a claim whose class was rewritten is a share provisioned by another
    ///         account's driver, on another account's filer, under this share's resource id.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE SIZE IS COMPARED AS A NUMBER, BECAUSE THE API SERVER REWRITES THE STRING.</b>
    ///         A <c>PersistentVolumeClaim</c> is a built-in kind, and apimachinery canonicalises every
    ///         <c>resource.Quantity</c> it stores: <c>1024Mi</c> reads back as <c>1Gi</c>, <c>1.5Gi</c>
    ///         as <c>1536Mi</c>, <c>1000M</c> as <c>1G</c>, and a suffix-less <c>1.5</c> as
    ///         <c>1500m</c>. Every one of those is admitted by <see cref="Schema2026" />'s pattern, and
    ///         a byte-for-byte compare against any of them never converges — the reconciler reports
    ///         "does not yet carry the desired spec" every five seconds forever and the observation
    ///         says "drifted" about a claim that is exactly right. The bucket never met this because a
    ///         CRD stores a quantity string as sent. <see cref="KubeQuantity.TryParse" /> is the one
    ///         parser the platform has, and <c>StorageMatchesTests.ACanonicalisedSizeStillMatches</c>
    ///         holds each spelling above against its canonical form.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, ResourceId id, string ns, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document || document["spec"] is not JsonObject spec) {
            return false;
        }

        var account = AccountOf(id);

        // ⚠ The `null` case is the claim as this provider RENDERS it — KubeCommandBuilder injects
        // `kind`, so a rendered body carries none until it has been applied and read back.
        return document["kind"]?.GetValue<string>() switch {
            null or "PersistentVolumeClaim" => MatchesClaim(spec, ns, account, desired),
            "SeaweedCSIDriver" => MatchesDriver(spec, ns, account),
            _ => false
        };
    }

    static bool MatchesClaim(JsonObject spec, string ns, string account, JsonElement desired) =>
        spec["accessModes"] is JsonArray modes
        && modes.Any(static x => x?.GetValue<string>() == AccessMode)
        && spec["storageClassName"]?.GetValue<string>() == DriverNameOf(ns, account)
        && SameQuantity(
            ((spec["resources"] as JsonObject)?["requests"] as JsonObject)?["storage"]?.GetValue<string>(),
            QuotaSize(desired)
        );

    /// <summary>Whether two quantity strings name the same number of bytes.</summary>
    /// <remarks>
    ///     ⚠ Falls back to the strings when either side is not a quantity this platform parses. The
    ///     desired side always is — the schema's pattern is <see cref="KubeQuantity.Pattern" /> — so
    ///     the fallback is reached only by a read-back the API server spelled in a form the platform's
    ///     grammar refuses, and equality on the string is then the honest answer rather than a guess.
    /// </remarks>
    static bool SameQuantity(string? read, string desired) =>
        KubeQuantity.TryParse(read, out var readBytes) && KubeQuantity.TryParse(desired, out var desiredBytes)
            ? readBytes == desiredBytes
            : read == desired;

    static bool MatchesDriver(JsonObject spec, string ns, string account) =>
        (spec["seaweedRef"] as JsonObject)?["name"]?.GetValue<string>() == account
        && spec["driverName"]?.GetValue<string>() == DriverNameOf(ns, account)
        && ((spec["storageClass"] as JsonObject)?["parameters"] as JsonObject)?["parentDir"]?.GetValue<string>()
        == SharesParentDir;

    /// <summary>The bound volume's name off a claim, or empty while it is still <c>Pending</c>.</summary>
    /// <param name="claimJson">The claim's JSON, as the API server returned it.</param>
    public static string VolumeNameOf(string claimJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(claimJson);
        } catch (JsonException) {
            return string.Empty;
        }

        return ((parsed as JsonObject)?["spec"] as JsonObject)?["volumeName"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>
    ///     A claim document as the CSI external-provisioner leaves it once bound — for the conformance
    ///     case and the handler's tests, which have no provisioner.
    /// </summary>
    /// <param name="claimJson">The document, as <see cref="ClaimJson" /> rendered it.</param>
    /// <param name="volumeName">The <c>PersistentVolume</c> the provisioner bound it to.</param>
    /// <remarks>
    ///     ⚠ <c>spec.volumeName</c> and <c>status.phase: Bound</c>, and <b>no owner reference</b> —
    ///     which is the real shape. Nothing owns a dynamically provisioned claim; the provisioner sets
    ///     the volume name and the bind-completed annotation and leaves <c>metadata.ownerReferences</c>
    ///     alone. <c>ProviderConformanceTests.TheClaimsATeardownKeepsSurviveItAndTheFinalTeardownRemovesThem</c>
    ///     follows an unowned planted claim through the teardown without asking it for a controller,
    ///     for this reason.
    /// </remarks>
    public static string WithBoundVolume(string claimJson, string volumeName) {
        ArgumentException.ThrowIfNullOrEmpty(volumeName);

        var root = JsonNode.Parse(claimJson)!.AsObject();
        var spec = root["spec"] as JsonObject ?? [];

        spec["volumeName"] = volumeName;
        root["spec"] = spec;
        root["status"] = new JsonObject { ["phase"] = "Bound" };

        return root.ToJsonString();
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the share in.</param>
    /// <param name="quotaSize">The share's size.</param>
    /// <param name="location">The region.</param>
    /// <remarks>⚠ Every property it writes is a <b>leaf</b>, for the reason <see cref="StorageAccounts.Body" /> gives.</remarks>
    public static string Body(Guid clusterId, string quotaSize = "100Gi", string location = "eu-central") =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["quota"] = new JsonObject { ["size"] = quotaSize }
            }
        }.ToJsonString();

    // ⚠ The same literal as the `DefaultJson` above, for the reason StorageAccounts states: the write
    // path stores a body AS SENT, and a reader that spelled the default inline would be a second place
    // it lives.
    const string DefaultQuotaSize = "100Gi";

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static string Text(JsonElement desired, string parent, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(parent, out var section)
        && section.ValueKind is JsonValueKind.Object
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
