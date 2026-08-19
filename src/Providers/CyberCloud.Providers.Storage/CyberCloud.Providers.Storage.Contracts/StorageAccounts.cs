// ⚠ For SecretRef, which lives here rather than in CyberCloud.ResourceManager.Contracts where it
// started — see its own remarks on why the [Alias] stayed put through the move. This is the first
// provider in the tree that names the type at all, so it is the first that needs the import.
using CyberCloud.Core.Contracts;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Storage/accounts</c>: the type, its api-version, its
///     body shape, and the one Kubernetes object it becomes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS ROW IS NOT IN docs/plan/12, AND THAT IS THE FIRST THING TO KNOW ABOUT IT.</b> The
///         four providers before this one are all rows of
///         [12 § The catalogue](../../../../docs/plan/12-managed-data-services.md), whose subject is
///         <i>"databases, caches, brokers, search"</i>. Object storage is
///         [15 § The three kinds](../../../../docs/plan/15-storage-blob-file.md):
///         <i>"Object · <c>CyberCloud.Storage/accounts</c> + <c>/buckets</c> · SeaweedFS + S3 gateway ·
///         HTTPS, S3 API"</i>, and <i>"Object storage — M1 · 2.0 EM"</i>. docs/plan/12 mentions the
///         service only as something it <i>consumes</i> — CloudNativePG's <i>"declarative backup to S3
///         (which we have — [15])"</i> — which is the gap this provider closes:
///         <c>charts/managed/postgres</c> renders a backup destination of
///         <c>s3://tenant-bucket/postgres</c> against a provider that did not exist.
///     </para>
///     <para>
///         ⚠ <b>What docs/plan/12 <i>does</i> own here is the operator</b>, through ADR-010 clause 1,
///         whose survey names <c>SeaweedFS</c>. docs/plan/15 § Object storage gives the reasoning —
///         MinIO's licence moved to AGPL, Ceph RGW <i>"is a full-time role"</i>, SeaweedFS is
///         Apache-2.0 with an O(1)-lookup design. So the eight pieces of docs/plan/12 § The pattern,
///         once apply to this row even though the catalogue table does not list it, and this type is
///         built to them.
///     </para>
///     <para>
///         ⚠ <b>ONE OBJECT, AND IT IS THE WHOLE CLUSTER.</b> A <c>Seaweed</c>
///         (<c>seaweed.seaweedfs.com/v1</c>) expands into masters, volume servers, a filer and a
///         standalone S3 gateway — four StatefulSets/Deployments, their Services, and (when asked) four
///         <c>ServiceMonitor</c>s. Nothing else is applied, which makes this the <i>smallest</i>
///         rendered object set in the catalogue and the <i>largest</i> resulting workload. See
///         <see cref="SeaweedKind" />.
///     </para>
///     <para>
///         ⚠ <b>THE CREDENTIAL IS THE INTERESTING PART OF THIS SERVICE AND IT IS NOT BUILT.</b>
///         <see cref="ConfigSecretName" /> is rendered into <c>spec.s3.configSecret</c> on every apply
///         and nothing writes the <c>Secret</c> it names, so the gateway pod cannot mount its volume
///         and the account visibly does not finish. That is the
///         <c>CyberCloud.Cache/redis</c> decision applied to a service where it matters more, and the
///         argument is at <see cref="ConfigSecretName" />: SeaweedFS with no identities configured does
///         not merely skip authentication, it grants <b>admin</b> to every request. Checked against
///         <c>weed/s3api/auth_credentials.go</c> rather than against a README —
///         <c>charts/managed/seaweedfs/SOURCE</c> records the line.
///     </para>
///     <para>
///         ⚠ <b><c>buckets</c> is docs/plan/15's other half and SHIPS — see
///         <see cref="StorageBuckets" />.</b> ⚠ <b>The claim this paragraph used to make was wrong
///         within the hour and the correction is worth keeping.</b> It said the blocker was "the
///         conformance harness being single-type, which is somebody else's to close"; that harness
///         gained <c>IProviderCaseSource.Ancestors</c> on 2026-08-12, the same day, and what was left
///         was scope. What an account owes its child now is one thing and it is a
///         <i>read-only</i> one: <see cref="StorageBuckets.ClusterRefOf" /> renders this account's
///         <b>name</b> into every <c>Bucket</c>'s <c>spec.clusterRef</c>, off the child's address, so
///         renaming an account is not a thing that could ever work and nothing offers it.
///     </para>
///     <para>
///         ⚠ <b>WHAT AN ACCOUNT DOES NOT DO WHEN IT IS DELETED, WHICH IS THE HALF THAT MATTERS.</b>
///         [08 § Deleting a parent resource that has children](../../../../docs/plan/08-resource-manager.md)
///         decides that a delete is <i>refused</i> — <c>409</c> with a count — while a resource still
///         has children, and that decision is <b>not implemented</b>: the platform cannot enumerate
///         children. So deleting an account today tears down its <c>Seaweed</c> and leaves every
///         bucket under it addressable, writable, and pointing at a cluster that is gone.
///         <c>charts/managed/seaweedfs-bucket/conformance.yaml § owed</c>,
///         <c>parent-delete-orphans-buckets</c>, says what it costs and why refusal — not cascade —
///         is the choice that stays right when the 7-day soft-delete window docs/plan/06 promises
///         arrives.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/seaweedfs/values.yaml</c> is the other half — ADR-010 § Which end authors
///         the schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class StorageAccounts {
    /// <summary>The provider namespace, as docs/plan/15 § The three kinds spells it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>CyberCloud.ObjectStorage</c>, and the distinction is docs/plan/15's own.</b> That
    ///     document puts <i>three</i> kinds under one namespace — <c>accounts</c>/<c>buckets</c> for
    ///     object, <c>fileShares</c> for file — and puts block storage under
    ///     <c>CyberCloud.Compute/disks</c> instead, <i>"because merging them into 'storage' produces an
    ///     API where two thirds of the properties are inapplicable"</i>. The namespace is shared; the
    ///     types are not.
    /// </remarks>
    public const string ProviderNamespace = "CyberCloud.Storage";

    /// <summary>The resource type. docs/plan/15 § The resource model.</summary>
    public const string TypePath = "accounts";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/seaweedfs/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/seaweedfs";

    /// <summary>How long a deleted account can be restored for. docs/plan/06 § Tags, locks.</summary>
    /// <remarks>
    ///     ⚠ <b>Seven days, and this is the type docs/plan/06 § Tags, locks names first</b> — <i>"7 days
    ///     for resources carrying data (Vault, Storage, databases)"</i>. An object-storage account
    ///     carries more of it than anything else in the catalogue. What the window preserves is what the
    ///     teardown leaves: deleting the volume servers' <c>StatefulSet</c> does not delete the
    ///     <c>PersistentVolumeClaim</c>s its <c>volumeClaimTemplate</c> made, so the objects are still on
    ///     disk; and <c>ISecretWriter</c> mints once and has no delete, so the restored account answers to
    ///     the same access-key pair <see cref="ListKeysAction" /> handed out before the delete.
    /// </remarks>
    public const int SoftDeleteDays = 7;

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the S3 endpoint and an access-key pair.</summary>
    /// <remarks>
    ///     docs/plan/15 § The resource model puts <c>accessKeys/{name}</c> under the account with
    ///     <i>"secret into Vault, <c>listKeys</c> action"</i>, and docs/plan/12 § Cross-cutting
    ///     decisions makes it <i>"an action with its own permission, audited on every call"</i>.
    ///     ⚠ <c>regenerateKeys</c> is named in the same paragraph and is <b>not</b> declared, for the
    ///     reason the four providers before this one give: it is specified with a rolling grace period
    ///     and nothing in the platform can hold two live credentials for one resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. Sharing
    ///     <c>read</c> would make every viewer of an account a holder of its S3 credentials — and on
    ///     this type those credentials are the <i>only</i> access control the data plane has, because
    ///     docs/plan/15 § The resource model deliberately keeps ReBAC off the object GET path.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The object an account IS ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>Seaweed</c> custom resource — the whole cluster, in one object.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>seaweeds</c>.</b> It is carried rather than derived for the reason
    ///     <see cref="GroupVersionKind.Plural" /> gives, and it is what the cluster-backed harness
    ///     derives a definition stub from — <c>ClusterConformanceHarness</c> reads it off
    ///     <c>ProviderConformanceCase.Objects</c>.
    /// </remarks>
    public static GroupVersionKind SeaweedKind { get; } =
        new() {
            Group = "seaweed.seaweedfs.com", Version = "v1", Kind = "Seaweed", Plural = "seaweeds"
        };

    // ── Ports and names the operator owns ─────────────────────────────────────────────────────

    /// <summary>The S3 port. <c>seaweedv1.FilerS3Port</c>, and the operator's own fallback.</summary>
    /// <remarks>
    ///     ⚠ <b>Not declared as a property, because the operator's <c>s3EffectivePort</c> already
    ///     defaults to it and a port a tenant can move is a connection string that changes shape.</b>
    ///     The IAM API is served on the same port when <c>spec.s3.iam</c> is on, which it is by default
    ///     — <c>api/v1/seaweed_types.go</c>, <c>+kubebuilder:default:=true</c> on
    ///     <c>S3GatewaySpec.IAM</c>.
    /// </remarks>
    public const int S3Port = 8333;

    /// <summary>The port every component serves Prometheus metrics on when monitoring is asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>Setting this is the whole of docs/plan/12 § The pattern, once, piece 6's FIRST
    ///     branch on this service, and it is that branch's second sighting.</b> The corrected piece 6
    ///     reads <i>"ask the operator for the scrape object wherever the operator accepts the
    ///     request"</i>; CloudNativePG answered with <c>spec.monitoring.enablePodMonitor</c> and this
    ///     operator answers with <c>metricsPort</c>. <c>internal/controller/controller_s3.go</c>:
    ///     <c>if m.Spec.S3.MetricsPort != nil { ensureS3ServiceMonitor(m) }</c>, with an <c>else</c>
    ///     branch that <i>deletes</i> the <c>ServiceMonitor</c> when the port is taken away. So the
    ///     scrape object is the operator's, its selector is the operator's, and this provider renders
    ///     no monitoring object at all.
    ///     <para>
    ///         ⚠ It is <b>not</b> <c>spec.metricsAddress</c>, which is the other thing in that CRD with
    ///         "metrics" in its name and is a <i>push</i> gateway address the master forwards to the
    ///         rest of the cluster. Setting that one would produce metrics nothing scrapes and no
    ///         <c>ServiceMonitor</c> anywhere.
    ///     </para>
    /// </remarks>
    public const int MetricsPort = 9327;

    /// <summary>The filer's metadata volume, per docs/plan/05's durability rules.</summary>
    /// <remarks>
    ///     ⚠ <b>The filer's embedded store is the object namespace, and without this volume it lives in
    ///     the pod's writable layer.</b> With no <c>spec.filer.config</c> the operator mounts no
    ///     <c>filer.toml</c> at all — deliberately, see
    ///     <c>internal/controller/controller_filer_configmap.go</c>'s <c>hasFilerConfig</c> — and
    ///     SeaweedFS falls back to its embedded <c>leveldb2</c> store at
    ///     <c>{-defaultStoreDir}/filerldb2</c>. <c>weed/command/filer.go</c> defaults
    ///     <c>-defaultStoreDir</c> to <c>"."</c> and the operator never sets it, so the store lands in
    ///     the container's working directory. ⚠ <b>That working directory is <c>/data</c> only because
    ///     the upstream image says <c>WORKDIR /data</c>, and the operator's
    ///     <c>PersistenceSpec.MountPath</c> defaults to <c>"/data"</c> — two independently defaulted
    ///     values in two repositories that happen to agree, with nothing in either stating the
    ///     dependency.</b> <see cref="SeaweedJson" /> writes the mount path explicitly rather than
    ///     inheriting it, so the coupling is a line in this file instead of a coincidence.
    /// </remarks>
    public const string FilerVolumeSize = "10Gi";

    /// <summary>Where the filer's metadata volume is mounted. ⚠ Written out — see <see cref="FilerVolumeSize" />.</summary>
    public const string FilerMountPath = "/data";

    /// <summary>The image, without a tag. ⚠ The operator has no default and an empty image is legal.</summary>
    /// <remarks>
    ///     ⚠ <b>Written on every apply because the operator supplies nothing.</b>
    ///     <c>api/v1/image.go</c>'s <c>applyVersion(image, version)</c> returns <c>image</c> unchanged
    ///     when <c>image</c> is empty, and every component's <c>Image()</c> goes through it — so a
    ///     <c>Seaweed</c> with no <c>spec.image</c> renders pods with an empty image and fails at the
    ///     API server, per pod, after the caller was told <c>202</c>. This is the same class of
    ///     finding <c>charts/managed/valkey</c> records about spotahome's <c>defaultImage</c>, reached
    ///     from the opposite side: there the operator's default was the wrong <i>licence</i>, here
    ///     there is no default at all.
    ///     <para>
    ///         ⚠ <see cref="SeaweedJson" /> writes the tag into <c>spec.image</c> and leaves
    ///         <c>spec.version</c> unset, rather than setting both. <c>applyVersion</c> would rewrite
    ///         the tag from <c>spec.version</c> if both were present, so two spellings of one fact
    ///         would be one fact and one silent override.
    ///     </para>
    /// </remarks>
    public const string ImageRepository = "chrislusf/seaweedfs";

    /// <summary>The <c>Seaweed</c> an account owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef SeaweedRef(string ns, string name) =>
        new() { Kind = SeaweedKind, Namespace = ns, Name = name };

    /// <summary>The <c>Service</c> the operator puts in front of the S3 gateway.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Not an object this provider applies, and it is here because <see cref="Endpoint" />
    ///     needs it.</b> <c>internal/controller/controller_s3.go</c>'s <c>buildS3Service</c> names it
    ///     <c>{name}-s3</c>. Writing a <c>Service</c> of our own would be this chart competing with the
    ///     controller that owns it — the same rule <c>charts/managed/valkey</c> states.
    /// </remarks>
    public static string S3ServiceName(string name) => name + "-s3";

    /// <summary>The <c>Secret</c> holding the S3 identities file. ⚠ Nothing writes it — see the remarks.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE SECRET IS WRITTEN NOW, AND THE REASON IT HAS TO BE IS THE MOST IMPORTANT FACT ON
    ///         THIS TYPE.</b> <c>StorageAccountReconciler</c> mints an S3 key pair into the
    ///         tenant's vault before it applies anything, renders it into this <c>Secret</c> through
    ///         <see cref="ConfigSecretJson" />, and hands it back through <c>listKeys</c>. Until
    ///         2026-08-13 none of that was possible: docs/plan/12 § The pattern, once assigned piece 5
    ///         to <c>ISecretResolver</c>, which reads and cannot provision, so the reference below was
    ///         rendered against a <c>Secret</c> nobody could write. That doc row is corrected and the
    ///         seam it should have named is <c>ISecretWriter</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the old shape actually was, kept because it is the argument for the ordering.</b>
    ///         With the reference rendered and the <c>Secret</c> absent, the gateway pod stayed in
    ///         <c>ContainerCreating</c> and the account never reported <c>Succeeded</c> — visibly
    ///         unfinished, which was the safe half. The unsafe half was one edit away, and the
    ///         reconciler now mints <i>before</i> it applies so that a partial failure cannot produce
    ///         it. See its own remarks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The alternative is worse here than on any service before it, and the reason was
    ///         checked in SeaweedFS' own source rather than inferred.</b>
    ///         <c>weed/s3api/auth_credentials.go</c> sets <c>iam.isAuthEnabled = len(iam.identities) &gt;
    ///         0</c>, and <c>AuthenticateRequest</c> begins <c>if !iam.isAuthEnabled { return
    ///         &amp;Identity{Name: "admin", Account: &amp;AccountAdmin, Actions: []Action{ACTION_ADMIN}}
    ///         }</c>. A gateway with no identity file therefore does not merely skip authentication: it
    ///         answers every unauthenticated request <b>as an administrator</b>. docs/plan/12's own
    ///         <i>"a managed database on a public IP with a weak password is the single most common
    ///         cloud breach"</i> applies with no password at all, over HTTP, to a protocol every tool in
    ///         the industry already speaks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why this service is where piece 5 landed rather than
    ///         <c>CyberCloud.DBforPostgreSQL/servers</c>.</b> CloudNativePG <i>generates</i> a password
    ///         when the <c>Secret</c> its CR references is absent, so that service had a working
    ///         database whose credentials <c>listKeys</c> merely could not hand out — an inconvenience.
    ///         There is no equivalent here: an S3 endpoint is reachable only with an access-key pair,
    ///         and with no pair the gateway does not refuse callers, it promotes them. So this was the
    ///         one type where the absence was a security hole rather than a gap, and it is the one the
    ///         write path was built against.
    ///     </para>
    /// </remarks>
    public static string ConfigSecretName(string name) => name + "-s3-config";

    /// <summary>The <c>core/v1</c> <c>Secret</c> the S3 gateway's identities file lives in.</summary>
    /// <remarks>
    ///     ⚠ <b>The core group, so <see cref="GroupVersionKind.Group" /> is empty and
    ///     <see cref="GroupVersionKind.IsCoreGroup" /> is what reads it.</b> The plural is carried
    ///     rather than derived, for the reason that property gives.
    /// </remarks>
    public static GroupVersionKind SecretKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    /// <summary>The <c>Secret</c> an account owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ConfigSecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = ConfigSecretName(name) };

    /// <summary>The key <see cref="ConfigSecretName" /> files the identities under.</summary>
    /// <remarks>
    ///     ⚠ Projected as <c>-config=/etc/sw/{key}</c> —
    ///     <c>internal/controller/controller_s3.go</c>'s <c>buildS3GatewayStartupScript</c>. The
    ///     extension is <c>.json</c> because SeaweedFS reads the identities file as the JSON form of
    ///     <c>iam_pb.S3ApiConfiguration</c>.
    /// </remarks>
    public const string ConfigSecretKey = "s3.json";

    /// <summary>The in-cluster S3 endpoint <c>listKeys</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>http</c> and not <c>https</c>, and that is a statement rather than an oversight. The
    ///     operator's <c>TLSSpec</c> issues certificates through cert-manager for the cluster's
    ///     <i>internal</i> gRPC, and nothing terminates TLS on the S3 port; docs/plan/15's <i>"Consumed
    ///     as HTTPS"</i> is an ingress-side promise this type cannot yet keep. See
    ///     <c>conformance.yaml § owed</c>, <c>external-exposure</c>.
    /// </remarks>
    public static string Endpoint(string ns, string name) =>
        "http://"
        + S3ServiceName(name)
        + "."
        + ns
        + ".svc:"
        + S3Port.ToString(CultureInfo.InvariantCulture);

    // ── The credential: where it lives, what it is called, and how one is made ────────────────
    //
    // ⚠ THIS BLOCK IS docs/plan/12 § The pattern, once, PIECE 5, AND IT IS WHAT MAKES THIS SERVICE
    // USABLE AT ALL. Everything above it renders a data plane; without this the data plane comes up
    // answering every unauthenticated request as an administrator — see ConfigSecretName.

    /// <summary>The vault path an account's S3 key pair lives at.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>KEYED ON THE RESOURCE GUID AND NOT ON ITS NAME, WHICH IS THE DIFFERENCE BETWEEN A
    ///         RECREATED ACCOUNT AND THE OLD ONE'S CREDENTIAL.</b> docs/plan/06 § Identifiers makes a
    ///         name reusable the moment the index entry is released — the delete path releases it
    ///         first, on purpose, "so the name is immediately reusable". A path built from the name
    ///         would therefore hand a brand-new account the key pair of the account somebody deleted an
    ///         hour ago, and <see cref="ISecretWriter" />'s mint-once rule would make that permanent:
    ///         the new account could never mint its own. The GUID is unique per resource for the life
    ///         of the platform, so a recreate is a new path and a new pair.
    ///     </para>
    ///     <para>
    ///         ⚠ The tenant is the first segment because docs/plan/18 scopes vault policy per tenant.
    ///         A path that led with the provider would make "everything this tenant owns" unexpressible
    ///         as a policy prefix.
    ///     </para>
    /// </remarks>
    public static string SecretPath(ResourceId id) {
        ArgumentNullException.ThrowIfNull(id.Path);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tenants/{id.TenantId:D}/{ProviderNamespace}/{TypePath}/{id.Id:D}"
        );
    }

    /// <summary>The field the access key id is filed under.</summary>
    public const string AccessKeyIdField = "accessKeyId";

    /// <summary>The field the secret access key is filed under.</summary>
    public const string SecretAccessKeyField = "secretAccessKey";

    /// <summary>The handle that reads an account's access key id back.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static SecretRef AccessKeyIdRef(ResourceId id) =>
        new() { Path = SecretPath(id), Field = AccessKeyIdField };

    /// <summary>The handle that reads an account's secret access key back.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static SecretRef SecretAccessKeyRef(ResourceId id) =>
        new() { Path = SecretPath(id), Field = SecretAccessKeyField };

    /// <summary>
    ///     The identity name inside the S3 identities file.
    /// </summary>
    /// <remarks>
    ///     ⚠ One identity per account, and it is the account's owner. docs/plan/15 § The resource model
    ///     puts <c>accessKeys/{name}</c> under the account as its own type, which would be several
    ///     identities in this file — that type is not declared, so a second identity would be one the
    ///     platform could create and no API could name.
    /// </remarks>
    public const string IdentityName = "owner";

    /// <summary>How many characters an access key id has.</summary>
    /// <remarks>
    ///     ⚠ 20, and the length is the interoperability constraint rather than a security one. An
    ///     access key id is not secret; it is the identifier a SigV4 <c>Authorization</c> header
    ///     carries in the clear, and several S3 clients validate its shape against AWS's 20-character
    ///     uppercase form before they will sign. The entropy that matters is in the secret key.
    /// </remarks>
    public const int AccessKeyIdLength = 20;

    /// <summary>How many characters a secret access key has.</summary>
    /// <remarks>
    ///     ⚠ 40, matching AWS, which over <see cref="KeyAlphabet" />'s 32 symbols is 200 bits. That is
    ///     well past the point where the key is the weakest thing about an endpoint served over plain
    ///     <c>http</c> — see <see cref="Endpoint" /> — and the honest reason to go no further is that
    ///     a longer key would break clients that size a buffer to AWS's.
    /// </remarks>
    public const int SecretAccessKeyLength = 40;

    /// <summary>
    ///     The symbols a generated key is drawn from: RFC 4648 base32, without padding.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No lower case and no <c>0</c>, <c>1</c> or <c>8</c>, which is base32's own choice and
    ///     is worth keeping rather than widening.</b> A key pair is a value people copy out of a
    ///     terminal and into a configuration file by hand at least once, and <c>O</c>/<c>0</c> and
    ///     <c>l</c>/<c>1</c> are where that goes wrong. Widening to base64 would buy 0.2 bits per
    ///     character and cost the property that a mistyped key fails at the client rather than
    ///     signing wrongly.
    /// </remarks>
    public const string KeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Generates one S3 key pair.</summary>
    /// <returns>The access key id and the secret access key.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NOT IDEMPOTENT, AND THE PASS THAT CALLS IT STILL IS.</b> This returns a different
    ///         pair every call, which reads like a clause 1 violation and is not: the reconciler offers
    ///         the pair to <see cref="ISecretWriter.MintAsync" />, whose <c>cas=0</c> keeps the first
    ///         one and discards every later candidate, and then <i>resolves</i> what the vault actually
    ///         holds before rendering anything. So the generated value reaches a manifest only on the
    ///         pass that minted it, and every rendered object is byte-stable across passes. The
    ///         alternative — deriving a key from the resource id so that it is reproducible — is a
    ///         credential anyone who knows a GUID can compute.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="RandomNumberGenerator.GetString" /> and not <c>Random.Shared</c>. This is a
    ///         credential; a non-cryptographic generator seeded from a clock is the classic way to make
    ///         one guessable.
    ///     </para>
    /// </remarks>
    public static (string AccessKeyId, string SecretAccessKey) GenerateKeyPair() =>
        (
            RandomNumberGenerator.GetString(KeyAlphabet, AccessKeyIdLength),
            RandomNumberGenerator.GetString(KeyAlphabet, SecretAccessKeyLength)
        );

    /// <summary>
    ///     Builds the S3 identities file the gateway authenticates against.
    /// </summary>
    /// <param name="accessKeyId">The access key id, as the vault holds it.</param>
    /// <param name="secretAccessKey">The secret access key, as the vault holds it.</param>
    /// <remarks>
    ///     <para>
    ///         The JSON form of SeaweedFS' <c>iam_pb.S3ApiConfiguration</c>, which is what
    ///         <c>-config</c> reads — see <see cref="ConfigSecretKey" /> for how the operator projects
    ///         the file in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The action list is what stops the "no identities" behaviour rather than what grants
    ///         access.</b> <c>weed/s3api/auth_credentials.go</c> sets
    ///         <c>isAuthEnabled = len(identities) &gt; 0</c>, so the <i>presence</i> of one identity is
    ///         what turns authentication on at all; <c>Admin</c> is then what this identity may do
    ///         once it has authenticated. An empty action list would authenticate the tenant and
    ///         authorise nothing, which is a bucket they own and cannot read.
    ///     </para>
    /// </remarks>
    public static string IdentitiesJson(string accessKeyId, string secretAccessKey) {
        ArgumentException.ThrowIfNullOrEmpty(accessKeyId);
        ArgumentException.ThrowIfNullOrEmpty(secretAccessKey);

        return new JsonObject {
            ["identities"] = new JsonArray {
                new JsonObject {
                    ["name"] = IdentityName,
                    ["credentials"] = new JsonArray {
                        new JsonObject {
                            ["accessKey"] = accessKeyId, ["secretKey"] = secretAccessKey
                        }
                    },
                    ["actions"] = new JsonArray { "Admin", "Read", "Write", "List", "Tagging" }
                }
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     Builds the <c>Secret</c> the S3 gateway mounts.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="accessKeyId">The access key id, as the vault holds it.</param>
    /// <param name="secretAccessKey">The secret access key, as the vault holds it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ONE OBJECT THIS PROVIDER RENDERS THAT CARRIES A SECRET VALUE, AND IT IS BUILT
    ///         FROM WHAT THE VAULT RETURNED RATHER THAN FROM DESIRED STATE.</b> The account's body has
    ///         no credential property and must not grow one: docs/plan/00 § Non-negotiables keeps
    ///         secrets out of grain state, and a body is grain state. The value exists in a local
    ///         inside one reconcile pass, goes into this document, and is gone when the pass returns.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>data</c> with the base64 written out, rather than the <c>stringData</c>
    ///         convenience field, and the reason is the read-back.</b> <c>stringData</c> is write-only:
    ///         the API server folds it into <c>data</c> and never returns it, so an object applied with
    ///         one field and read back with another is an object <see cref="Matches" /> would have to
    ///         accept in two shapes — one of which no real cluster ever produces, and which therefore
    ///         only ever gets exercised by a fake. Encoding here costs one line and makes apply and
    ///         read-back the same document.
    ///     </para>
    /// </remarks>
    public static string ConfigSecretJson(string name, string accessKeyId, string secretAccessKey) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ConfigSecretName(name) },
            ["type"] = "Opaque",
            ["data"] = new JsonObject {
                [ConfigSecretKey] = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(IdentitiesJson(accessKeyId, secretAccessKey))
                )
            }
        }.ToJsonString();
    }

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ <b>Pointed at <see cref="KubeQuantity" /> rather than copied.</b> Four providers kept their
    ///     own copy of this grammar and one of them grew a second <i>parser</i> next to it, in
    ///     <see langword="double" />, which disagreed on value rather than on verdict.
    ///     <c>QuantityParserTests</c> fails if a fifth copy or a second suffix table appears. There is
    ///     no rule-2 problem in reaching for it: <see cref="KubeQuantity" /> lives in
    ///     <c>CyberCloud.ResourceManager.Contracts</c>, which every provider may reference.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, <c>s1</c> family.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/12 § Sizing vocabulary has no row for a storage service, and the table was
    ///     followed anyway rather than extended.</b> Its five families are burstable, CPU-bound,
    ///     general, memory-bound and latency-sensitive; a volume server is bound by disk and by network,
    ///     which is a sixth thing. <c>s1</c> — <i>"1:4 · General — most databases"</i> — is the closest,
    ///     and taking it is the point of having a vocabulary: a service that invents a family because
    ///     its author reasoned about its workload is the drift the table exists to stop. If object
    ///     storage wants an <c>i1</c>, that is a change to docs/plan/12 and then to this row, in that
    ///     order.
    ///     <para>
    ///         ⚠ <b>This sizes the <i>volume servers</i> and nothing else.</b> The masters, the filer
    ///         and the S3 gateway are sized by <see cref="ControlPlaneCpu" />, which is what makes this
    ///         the first type in the catalogue whose quota meters are a sum over <i>heterogeneous</i>
    ///         components rather than <c>replicas × one figure</c>. See <c>StorageProvider</c>.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["s1.nano"] = ("250m", "1Gi"),
            ["s1.micro"] = ("500m", "2Gi"),
            ["s1.small"] = ("1", "4Gi"),
            ["s1.medium"] = ("2", "8Gi"),
            ["s1.large"] = ("4", "16Gi"),
            ["s1.xlarge"] = ("8", "32Gi"),
            ["s1.2xlarge"] = ("16", "64Gi"),
            ["s1.4xlarge"] = ("32", "128Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The CPU every non-volume component requests.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant rather than a property, and it still costs quota.</b> A master keeps the
    ///     volume topology in memory, a filer serves metadata and the S3 gateway is a stateless proxy;
    ///     none of them scales with the tenant's data and none of them is a knob worth publishing. What
    ///     they are not is free — an account with three masters, a filer and two gateways runs six pods
    ///     before a byte is stored, and a meter that counted only the volume servers would under-reserve
    ///     by exactly that. <c>StorageProvider</c>'s derivations carry the sum.
    /// </remarks>
    public const string ControlPlaneCpu = "250m";

    /// <summary>The memory every non-volume component requests. See <see cref="ControlPlaneCpu" />.</summary>
    public const string ControlPlaneMemory = "512Mi";

    /// <summary>
    ///     The replication codes docs/plan/15's <i>"replication"</i> account default maps onto.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>SeaweedFS spells replication as three digits and the digits are not a count.</b>
    ///     <c>xyz</c> is <i>x</i> extra copies in other data centres, <i>y</i> in other racks of the
    ///     same data centre, <i>z</i> on other servers in the same rack — so <c>001</c> is two copies
    ///     total and <c>000</c> is one. Publishing the codes as an API would be publishing somebody
    ///     else's encoding; publishing a count would lose the placement, which is the whole of what the
    ///     code says. The member names are the placement and the code is the rendering.
    ///     <para>
    ///         ⚠ <b>A code the topology cannot satisfy is accepted by the API and never satisfied by
    ///         the cluster.</b> <c>DifferentDataCenter</c> on a single-zone cluster leaves every write
    ///         under-replicated, and SeaweedFS reports that as a volume that will not go writable rather
    ///         than as an error on the create. That is a relation between this property and the
    ///         cluster's own topology, which <c>ResourceSchema</c> validates nothing about — the same
    ///         shape as <c>charts/managed/kafka</c>'s <c>replication-factor-versus-node-count</c>, and
    ///         the second sighting of it. <c>conformance.yaml § owed</c>.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, string> ReplicationCodes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["None"] = "000",
            ["SameRack"] = "001",
            ["DifferentRack"] = "010",
            ["DifferentDataCenter"] = "100"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every default here is the chart's default, spelled as JSON.</b> There is no
    ///     <c>@default</c> directive — charts/README.md § The annotation format — because the chart's
    ///     default <i>is</i> the YAML literal on the annotated line, and <c>ChartAnnotationEmitter</c>
    ///     writes that literal from <see cref="SchemaProperty.DefaultJson" />.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the account is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The account's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the object store."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },

                // ── The chart's API surface, in the chart's own declaration order ───────────────
                new(
                    "/properties/version",
                    SchemaKind.Text,
                    Required: true,
                    Description: "SeaweedFS version. ⚠ SeaweedFS ships a release roughly weekly and "
                    + "maintains no long-term branch, so docs/plan/12's \"supported major versions\" is "
                    + "a shape this project does not have; the two values here are the two most recent "
                    + "releases and a new api-version is what adds a third."
                ) {
                    AllowedValues = ["4.40", "4.41"],
                    DefaultJson = "\"4.41\""
                },
                new(
                    "/properties/replication",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Where the second copy of every object is placed. None keeps one copy "
                    + "and is for scratch data only. A placement the cluster's topology cannot satisfy "
                    + "leaves volumes read-only rather than failing the create. Immutable, because "
                    + "SeaweedFS applies this to volumes as they are created and never rewrites the "
                    + "ones that already exist — a change would split the account into two durability "
                    + "promises with no way to tell which object got which."
                ) {
                    AllowedValues = [.. ReplicationCodes.Keys.Order(StringComparer.Ordinal)],
                    DefaultJson = "\"SameRack\"",
                    Immutable = true
                },
                new(
                    "/properties/masters",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of master servers. The masters hold the volume topology in a "
                    + "Raft group, so three is the smallest count that survives losing one. One is "
                    + "offered for development and has no quorum at all."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "3"
                },
                new(
                    "/properties/volumeServers",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of volume servers. This is the capacity axis: total raw "
                    + "capacity is this count times the volume size below, before replication."
                ) {
                    Minimum = 1,
                    Maximum = 16,
                    DefaultJson = "3"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory per volume server, either by preset or explicitly. The "
                    + "masters, the filer and the S3 gateway are sized by the platform and are not "
                    + "affected."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Volume servers use the s1 family, "
                    + "which is 1 vCPU to 4 GiB."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"s1.small\""
                },
                new(
                    "/properties/sizing/cpu",
                    SchemaKind.Text,
                    Description: "Explicit vCPU quantity in Kubernetes form, for example 500m or 2. "
                    + "Empty means take it from the preset."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/sizing/memory",
                    SchemaKind.Text,
                    Description: "Explicit memory quantity in Kubernetes form, for example 4Gi. Empty "
                    + "means take it from the preset."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/storage",
                    SchemaKind.Nested,
                    Description: "The data volume, per volume server."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Data volume size per volume server, in Kubernetes quantity form. Grows "
                    + "online; never shrinks."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"100Gi\"",
                    ExampleJson = "\"100Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name for the volume servers. Empty means the cluster "
                    + "default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/gateway",
                    SchemaKind.Nested,
                    Description: "The S3 gateway — docs/plan/15's ADR-008: \"the API is S3\"."
                ),
                new(
                    "/properties/gateway/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of S3 gateway pods. The gateway is stateless, so this is a "
                    + "throughput and availability setting rather than a topology one."
                ) {
                    Minimum = 1,
                    Maximum = 10,
                    DefaultJson = "2"
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the operator is asked for a ServiceMonitor per component. On "
                    + "by default — docs/plan/12: \"a managed service the tenant cannot see the health "
                    + "of is a black box they will not trust with production\". Turning it off removes "
                    + "the metrics port as well as the scrape, which is the operator's own behaviour "
                    + "rather than this provider's."
                ) {
                    DefaultJson = "true"
                }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/listKeys</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Declared even though no handler serves it, because an undeclared response is the one
    ///     part of the API surface with no contract.</b> What leaves the platform through a
    ///     <c>secret: true</c> action is exactly the thing that should be written down before it
    ///     leaves. There is no request shape, for the reason <c>ActionRegistration</c> gives.
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/endpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster S3 endpoint, http://host:port. ⚠ No external address is "
                    + "returned, because there is none — see the account's own documentation on "
                    + "exposure."
                ),
                new(
                    "/region",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region name an S3 client signs with. SeaweedFS does not route on "
                    + "it, and SigV4 refuses to sign without one, so it is returned rather than left "
                    + "for the caller to guess."
                ),
                new(
                    "/accessKeyId",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The access key id. Not secret on its own; useless without the pair "
                    + "below."
                ),
                new(
                    "/secretAccessKey",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The secret access key, read from the tenant's Vault for this call "
                    + "only."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The SeaweedFS version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultVersion
            : DefaultVersion;

    /// <summary>
    ///     The region an S3 client signs against, from the body's <c>location</c>.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>The billing region and the SigV4 region are one string here, and they are not the same
    ///     idea.</b> SeaweedFS does not route on a region at all — it accepts whatever the client
    ///     signed with — so the platform is free to choose, and choosing the value the tenant already
    ///     sees on the resource means <c>listKeys</c> hands back something they can check. The day
    ///     SeaweedFS starts caring, this becomes its own property rather than a second reading of
    ///     <c>location</c>.
    /// </remarks>
    public static string Region(JsonElement desired) =>
        Root(desired, "location") is { ValueKind: JsonValueKind.String } value
        && value.GetString() is { Length: > 0 } location
            ? location
            : DefaultRegion;

    /// <summary>The region reported when a body carries no <c>location</c>.</summary>
    /// <remarks>
    ///     ⚠ Reachable only through a body that was never validated — <c>location</c> is required — and
    ///     it is a real value rather than an empty string because SigV4 refuses to sign without one.
    /// </remarks>
    public const string DefaultRegion = "us-east-1";

    /// <summary>The master count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Masters(JsonElement desired) => Number(desired, "masters", DefaultMasters);

    /// <summary>The volume-server count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int VolumeServers(JsonElement desired) =>
        Number(desired, "volumeServers", DefaultVolumeServers);

    /// <summary>The S3 gateway replica count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int GatewayReplicas(JsonElement desired) =>
        Number(desired, "gateway", "replicas", DefaultGatewayReplicas);

    /// <summary>The data-volume size per volume server a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>The SeaweedFS replication code a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The three-digit code. ⚠ Falls back to the default's code rather than to <c>000</c>: an
    ///     unrecognised member name must not silently become "one copy".
    /// </returns>
    public static string ReplicationCode(JsonElement desired) {
        var declared = Root(desired, "replication") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultReplication
            : DefaultReplication;

        return ReplicationCodes.TryGetValue(declared, out var code)
            ? code
            : ReplicationCodes[DefaultReplication];
    }

    /// <summary>Whether the desired body asks for the operator's scrape objects.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) =>
        Flag(desired, "monitoring", "enabled", true);

    /// <summary>
    ///     The CPU and memory one volume server asks for: the explicit quantities when both are given,
    ///     otherwise the preset's.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     Both quantities, or both empty when neither the preset nor an override supplies them —
    ///     which renders no <c>requests</c>/<c>limits</c> pair at all rather than a half-specified one.
    /// </returns>
    public static (string Cpu, string Memory) Resources(JsonElement desired) {
        var preset = Text(desired, "sizing", "preset", DefaultPreset);
        var fallback = Presets.TryGetValue(preset, out var found)
            ? found
            : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>Seaweed</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>spec.s3</c> and not <c>spec.filer.s3</c>, and the CRD refuses both.</b>
    ///         <c>internal/controller/controller_s3.go</c>'s header: the standalone gateway <i>"is the
    ///         preferred way to expose S3. The older <c>FilerSpec.S3</c> path (embedded S3 inside every
    ///         filer pod) is retained for backward compatibility but is deprecated. When both are set
    ///         the webhook rejects the CR."</i> The embedded path would also tie gateway throughput to
    ///         filer replicas, which on this type is pinned at one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>spec.filer.replicas</c> is 1 and is not a property, and the reason is a store
    ///         rather than a preference.</b> With no <c>spec.filer.config</c> the filer runs its
    ///         embedded <c>leveldb2</c> — see <see cref="FilerVolumeSize" /> — which is per-pod. Two
    ///         filers would be two divergent object namespaces behind one Service, and neither would
    ///         report an error. An HA filer needs a shared metadata store, which is a Postgres this
    ///         platform can already run and cannot yet let one resource depend on;
    ///         <c>conformance.yaml § owed</c>, <c>filer-is-a-single-point-of-failure</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.s3.service.type</c>, so the gateway is <c>ClusterIP</c>.</b> The
    ///         operator's <c>ServiceSpec</c> is a four-field subset — <c>type</c>, <c>annotations</c>,
    ///         <c>loadBalancerIP</c>, <c>clusterIP</c> — with <b>no <c>loadBalancerSourceRanges</c></b>,
    ///         so docs/plan/12 § Cross-cutting decisions' mandatory firewall allow-list has nothing to
    ///         render into. Offering exposure without it would be the one thing that paragraph forbids
    ///         in as many words. <c>conformance.yaml § owed</c>, <c>external-exposure</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///         place a key and a value are syntax-checked.
    ///     </para>
    /// </remarks>
    public static string SeaweedJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var monitoring = MonitoringEnabled(desired);
        var (cpu, memory) = Resources(desired);

        var master = new JsonObject {
            ["replicas"] = Masters(desired),
            ["defaultReplication"] = ReplicationCode(desired),
            ["requests"] = ControlPlaneQuantities(),
            ["limits"] = ControlPlaneQuantities()
        };

        // ⚠ The storage request sits beside cpu/memory rather than under a `storage:` block, because
        // VolumeSpec inlines corev1.ResourceRequirements: `spec.volume.requests.storage` is what
        // controller_volume_statefulset.go lifts into the claim template, and everything else in the
        // same map is filtered down to the container. One map, two destinations, decided by key.
        var volumeRequests = new JsonObject { ["storage"] = StorageSize(desired) };
        var volume = new JsonObject {
            ["replicas"] = VolumeServers(desired), ["requests"] = volumeRequests
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            volumeRequests["cpu"] = cpu;
            volumeRequests["memory"] = memory;
            volume["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
        }

        var storageClass = Text(desired, "storage", "class", string.Empty);
        if (storageClass.Length > 0) {
            volume["storageClassName"] = storageClass;
        }

        var filer = new JsonObject {
            ["replicas"] = 1,
            ["requests"] = ControlPlaneQuantities(),
            ["limits"] = ControlPlaneQuantities(),
            ["persistence"] = new JsonObject {
                ["enabled"] = true,
                // ⚠ Written out rather than inherited — see FilerVolumeSize for the two defaults in
                // two repositories this line stops depending on.
                ["mountPath"] = FilerMountPath,
                ["accessModes"] = new JsonArray { "ReadWriteOnce" },
                ["resources"] = new JsonObject {
                    ["requests"] = new JsonObject { ["storage"] = FilerVolumeSize }
                }
            }
        };

        var s3 = new JsonObject {
            ["replicas"] = GatewayReplicas(desired),
            ["requests"] = ControlPlaneQuantities(),
            ["limits"] = ControlPlaneQuantities(),
            // ⚠ The reference is unconditional. See ConfigSecretName: a gateway with no identities
            // authenticates nobody and authorises everybody.
            ["configSecret"] = new JsonObject {
                ["name"] = ConfigSecretName(name), ["key"] = ConfigSecretKey
            }
        };

        if (monitoring) {
            master["metricsPort"] = MetricsPort;
            volume["metricsPort"] = MetricsPort;
            filer["metricsPort"] = MetricsPort;
            s3["metricsPort"] = MetricsPort;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                // ⚠ The tag goes here and `spec.version` stays unset — see ImageRepository.
                ["image"] = ImageRepository + ":" + Version(desired),
                ["master"] = master,
                ["volume"] = volume,
                ["filer"] = filer,
                ["s3"] = s3
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns><c>true</c> when the fields this provider owns hold the desired values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, not equality, and this type has TWO reasons where the three before it
    ///         had one apiece — which is worth stating as a contrast rather than as a restatement.</b>
    ///         <c>KafkaClusters.Matches</c> found that Strimzi's CRD declares <b>no <c>default:</c>
    ///         anywhere</b>, so the structural-defaulting argument was false for it;
    ///         <c>NatsClusters.Matches</c> found the opposite, because built-in kinds are the most
    ///         heavily defaulted objects in Kubernetes. Here the object is a <i>custom</i> resource and
    ///         the CRD declares defaults anyway — <c>api/v1/seaweed_types.go</c> carries
    ///         <c>+kubebuilder:default:=1</c> on <c>S3GatewaySpec.Replicas</c>,
    ///         <c>+kubebuilder:default:=true</c> on its <c>IAM</c>, <c>:=StatefulSet</c> on
    ///         <c>VolumeSpec.Kind</c> and four more — so an applied <c>spec.s3</c> comes back carrying
    ///         <c>iam: true</c> this provider never sent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second reason is the one no earlier provider met: this operator installs a
    ///         MUTATING webhook, and its defaulter is a scaffold.</b>
    ///         <c>api/v1/seaweed_webhook.go</c> registers
    ///         <c>/mutate-seaweed-seaweedfs-com-v1-seaweed</c> on <c>create;update</c>, and the body of
    ///         <c>SeaweedCustomDefaulter.Default</c> is a log line and <c>// TODO(user): fill in your
    ///         defaulting logic.</c> — it mutates nothing <i>today</i>. An equality comparison would
    ///         therefore pass on this version of the operator and break silently on whichever release
    ///         fills that TODO in, with the symptom being every account stuck in <c>InProgress</c>
    ///         while its cluster is perfectly correct. That is a different hazard from
    ///         <c>spotahome</c>'s <c>validate.go</c>, which mutates now, and it is the one an author
    ///         who read the file and saw an empty function would have talked themselves out of.
    ///     </para>
    ///     <para>
    ///         ⚠ Dispatches on the object's <c>kind</c> even though this type owns one kind, because a
    ///         conformance case supplies this as one function over every object the resource owns and
    ///         an unrecognised document must be <c>false</c> rather than assumed.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document) {
            return false;
        }

        // ⚠ A SWITCH RATHER THAN ONE KIND, BECAUSE THIS TYPE NOW OWNS TWO OBJECTS. The `null` case is
        // the Seaweed as this provider RENDERS it — KubeCommandBuilder injects `kind` from the
        // GroupVersionKind, so a rendered body carries none until it has been applied and read back.
        return document["kind"]?.GetValue<string>() switch {
            null or "Seaweed" => MatchesSeaweed(document, desired),
            "Secret" => MatchesConfigSecret(document),
            _ => false
        };
    }

    /// <summary>
    ///     Whether the gateway's identities <c>Secret</c> is there and carries its key.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>PRESENCE AND NOT CONTENT, AND THAT IS FORCED RATHER THAN LAZY.</b> Every other
    ///         <c>Matches</c> in the catalogue compares an object against the desired <i>body</i>, and
    ///         the whole point of this object is that its content is <b>not</b> in the body — the
    ///         credential lives in the vault and reaches a manifest for the length of one pass. There
    ///         is nothing here to compare against, and inventing something would mean putting the
    ///         credential in desired state, which is the rule this provider exists to keep.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it still catches is the failure that matters.</b> A <c>Secret</c> deleted by
    ///         a well-meant <c>kubectl</c>, emptied by an admission policy, or never applied is a
    ///         gateway that comes up with no identities and answers every request as an administrator.
    ///         All three land as an absent <see cref="ConfigSecretKey" /> and all three make this
    ///         false, so the next pass re-renders it from the vault.
    ///     </para>
    ///     <para>
    ///         ⚠ Reads <c>data</c>, which is what <see cref="ConfigSecretJson" /> writes and what a
    ///         real API server returns. The <c>stringData</c> convenience field is write-only and
    ///         deliberately not used — see that method's remarks.
    ///     </para>
    /// </remarks>
    static bool MatchesConfigSecret(JsonObject document) =>
        document["data"] is JsonObject data
        && data[ConfigSecretKey]?.GetValue<string>() is { Length: > 0 };

    static bool MatchesSeaweed(JsonObject document, JsonElement desired) {
        if (document["spec"] is not JsonObject spec) {
            return false;
        }

        return spec["master"] is JsonObject master
            && master["replicas"]?.GetValue<int>() == Masters(desired)
            && master["defaultReplication"]?.GetValue<string>() == ReplicationCode(desired)
            && spec["volume"] is JsonObject volume
            && volume["replicas"]?.GetValue<int>() == VolumeServers(desired)
            && (volume["requests"] as JsonObject)?["storage"]?.GetValue<string>() == StorageSize(desired)
            && spec["s3"] is JsonObject s3
            && s3["replicas"]?.GetValue<int>() == GatewayReplicas(desired)
            // ⚠ THE CREDENTIAL REFERENCE IS PART OF THE DESIRED STATE AND IS READ BACK LIKE ONE. A
            // Seaweed whose configSecret was stripped by an admission policy, a merge, or a well-meant
            // `kubectl edit` is a gateway that comes up and serves every request as an administrator —
            // see ConfigSecretName. It is the one field here whose absence makes the resource WORK,
            // which is why nothing else would ever report it.
            && (s3["configSecret"] as JsonObject)?["name"]?.GetValue<string>() is { Length: > 0 };
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the account in.</param>
    /// <param name="volumeServers">How many volume servers.</param>
    /// <param name="storageSize">The data volume size per volume server.</param>
    /// <param name="masters">How many masters.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int volumeServers = 3,
        string storageSize = "100Gi",
        int masters = 3,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = DefaultVersion,
                ["replication"] = DefaultReplication,
                ["masters"] = masters,
                ["volumeServers"] = volumeServers,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["gateway"] = new JsonObject { ["replicas"] = DefaultGatewayReplicas },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader below has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.

    const string DefaultVersion = "4.41";
    const string DefaultReplication = "SameRack";
    const string DefaultPreset = "s1.small";
    const string DefaultStorageSize = "100Gi";
    const int DefaultMasters = 3;
    const int DefaultVolumeServers = 3;
    const int DefaultGatewayReplicas = 2;

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    static JsonObject ControlPlaneQuantities() =>
        new() { ["cpu"] = ControlPlaneCpu, ["memory"] = ControlPlaneMemory };

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Root(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string parent, string name, string fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static int Number(JsonElement desired, string parent, string name, int fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var found)
            ? found
            : fallback;

    static int Number(JsonElement desired, string name, int fallback) =>
        Root(desired, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var found)
            ? found
            : fallback;

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Member(desired, parent, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
