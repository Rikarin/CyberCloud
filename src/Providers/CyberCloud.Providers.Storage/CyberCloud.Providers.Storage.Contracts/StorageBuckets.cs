using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Storage/accounts/buckets</c> — the first child type
///     to ship in this platform.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE AUTHORITY IS docs/plan/15 § The three kinds, AND ITS ROW NAMES BOTH HALVES:</b>
///         <i>"Object · <c>CyberCloud.Storage/accounts</c> <b>+ <c>/buckets</c></b> · SeaweedFS + S3
///         gateway"</i>. § The resource model spells the child's surface —
///         <i>"buckets/{name} ← globally-unique-per-account name, quota, versioning, lifecycle"</i> —
///         and <see cref="Schema2026" /> is three quarters of that line. The fourth, <c>lifecycle</c>,
///         is <c>BucketLifecyclePolicy</c>, which is docs/plan/15's <c>lifecyclePolicies/{name}</c> and
///         a type of its own rather than a property of this one.
///         ⚠ docs/plan/12's catalogue has no storage row at all, so nothing here is derived from it;
///         what it contributes is § The pattern, once's eight pieces, which apply unchanged.
///     </para>
///     <para>
///         ⚠ <b>NOTHING IN THE BODY NAMES THE ACCOUNT, AND THAT IS THE WHOLE OF A CHILD TYPE.</b> The
///         <i>address</i> names it — <c>…/accounts/{accountName}/buckets/{bucketName}</c> — and
///         <see cref="ResourceId.Parent" /> is a pure function of that address. A <c>parentAccount</c>
///         property would be a second spelling of the same fact, and the two would disagree the first
///         time anybody sent a body under the wrong path. The only place the parent's name is read is
///         <see cref="ClusterRefOf" /> and <see cref="ObjectNameOf" />, both of which take the id.
///     </para>
///     <para>
///         ⚠ <b>THE ACCOUNT DOES NOT COME UP, AND A BUCKET INHERITS THAT EXACTLY.</b>
///         <see cref="StorageAccounts.ConfigSecretName" /> is rendered against a <c>Secret</c> nothing
///         writes, so the S3 gateway never mounts its volume — and
///         <c>weed/s3api/auth_credentials.go</c>'s <c>isAuthEnabled = len(identities) &gt; 0</c> with an
///         <c>ACTION_ADMIN</c> identity returned when it is false is why that is the deliberate half of
///         the trade. <b>A bucket has no credential story of its own to build</b>: there is no
///         per-bucket access key in SeaweedFS, no <c>listKeys</c> here, and no <c>anonymousRead</c>
///         property (see <see cref="Schema2026" />). Data-plane access to a bucket is the <i>account's</i>
///         S3 identities — docs/plan/15 § The resource model keeps ReBAC off the object GET path
///         deliberately — and those identities do not exist. So the honest statement is that a bucket's
///         access story is <b>entirely its parent's</b>, and its parent's is
///         <c>conformance.yaml § owed</c>, <c>listkeys-has-no-handler</c>.
///     </para>
///     <para>
///         ⚠ <b>WHAT HAPPENS TO A BUCKET WHEN ITS ACCOUNT IS DELETED IS NOT THIS PROVIDER'S TO
///         DECIDE, AND IT IS ALREADY DECIDED.</b>
///         [08 § Deleting a parent resource that has children](../../../../docs/plan/08-resource-manager.md):
///         <i>"a delete is refused while the resource still has children — <c>409</c>, not a cascade,
///         and not a silent orphan"</i>. It is <b>not implemented</b> — the platform cannot enumerate
///         children — so today an account's delete leaves its buckets addressable with a ReBAC
///         <c>parent</c> tuple pointing at a GUID that no longer resolves. See
///         <c>conformance.yaml § owed</c>, <c>parent-delete-orphans-buckets</c>, for what that costs on
///         <i>this</i> type specifically and why refusal — not cascade — is the choice that survives a
///         7-day soft-delete window arriving later.
///     </para>
///     <para>
///         ⚠ <b>A bucket's reconciler must never re-read its account, and
///         <c>StorageBucketReconciler</c> does not.</b> docs/plan/08 § Deleting a parent resource that
///         has children: <i>"What the platform must not do instead is re-check the parent on every
///         write to a child"</i>. On this type there is a second and sharper reason: the account never
///         reports <c>Succeeded</c> at all, so a bucket that waited for its parent to be healthy would
///         never converge for anybody.
///     </para>
/// </remarks>
public static class StorageBuckets {
    /// <summary>The provider namespace — the account's, because a child shares its parent's.</summary>
    public const string ProviderNamespace = StorageAccounts.ProviderNamespace;

    /// <summary>
    ///     The type path. ⚠ <b><c>accounts/buckets</c>, interleaved — not a flattened <c>buckets</c>.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/12 § Child resources chose <c>…/accounts/{account}/buckets/{bucket}</c> over the
    ///     flattened form for the reason <c>test/CyberCloud.Isolation/ParentEdgeTests</c> states: a
    ///     flattened address has nowhere to put the parent's name, so the ReBAC <c>parent</c> edge would
    ///     have to name the resource <i>group</i> — and granting somebody the account would then grant
    ///     nothing on its buckets. ⚠ It is also what keeps
    ///     <c>ProviderConformanceTests.CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource</c>
    ///     from self-skipping: that assertion is inapplicable at <see cref="ResourceTypeName.Depth" /> 1,
    ///     so a flattened spelling would drop the one assertion this type exists to exercise, silently.
    ///     <c>StorageBucketDeclarationTests</c> pins the depth against the literal.
    /// </remarks>
    public const string TypePath = "accounts/buckets";

    /// <summary>The one api-version. ⚠ Equal to the account's, and it must be.</summary>
    /// <remarks>
    ///     ⚠ A parent and a child served at different dates would make
    ///     <c>…/accounts/{a}/buckets/{b}?api-version=…</c> a request whose single version parameter has
    ///     to mean two things. It also has to equal the <c>cybercloud.io/api-version</c> annotation in
    ///     <c>charts/managed/seaweedfs-bucket/Chart.yaml</c>.
    /// </remarks>
    public const string V2026 = StorageAccounts.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    /// <remarks>
    ///     ⚠ <b>A chart of its own rather than a second block in <c>managed/seaweedfs</c>.</b>
    ///     <c>Build.Charts</c> pairs a chart with a resource type through one
    ///     <c>cybercloud.io/resource-type</c> annotation per <c>Chart.yaml</c>, so one directory cannot
    ///     be the surface of two types. It is also the honest shape: a <c>Bucket</c> is a different
    ///     object with a different lifetime from the <c>Seaweed</c> it lives in.
    /// </remarks>
    public const string ChartName = "managed/seaweedfs-bucket";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    /// <remarks>
    ///     ⚠ <b>A bucket carries its own <c>clusterId</c> and NOTHING CHECKS IT AGAINST ITS ACCOUNT'S.</b>
    ///     A body naming a different cluster from the parent's produces a <c>Bucket</c> applied into a
    ///     namespace whose <c>Seaweed</c> is somewhere else, and the operator would leave it unreconciled
    ///     forever. That is a relation between two resources' bodies, which <c>ResourceSchema</c>
    ///     validates nothing about — it checks each property against constants. Recorded at
    ///     <c>conformance.yaml § owed</c>, <c>bucket-cluster-may-differ-from-its-accounts</c>.
    /// </remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that reports what a bucket holds.</summary>
    /// <remarks>
    ///     ⚠ <b>Declared with no handler, and it is <i>not</i> the <c>listKeys</c> shape.</b> There is no
    ///     <c>listKeys</c> on a bucket because SeaweedFS has no per-bucket credential — see the remarks
    ///     on this class. What a bucket can answer is how much is in it, which docs/plan/15 § Metering
    ///     already samples <i>"hourly from SeaweedFS volume stats per bucket"</i>. ⚠ The numbers come
    ///     from docs/plan/22's usage pipeline rather than from the resource body, for exactly the reason
    ///     <c>conformance.yaml § owed</c>'s <c>egress-is-not-derivable</c> gives: a counter only exists
    ///     once traffic has flowed. So this is the tenant-facing half of a pipeline that is not built,
    ///     and it is declared rather than omitted because an undeclared response is the one part of an
    ///     API surface with no contract.
    /// </remarks>
    public const string StatsAction = "stats";

    /// <summary>The permission <see cref="StatsAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <b><c>read</c>, and the contrast with the account's <c>listKeys</c> is the point.</b> That
    ///     one has a permission of its own because what leaves through it is a credential —
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. A size and
    ///     an object count are neither a credential nor a capability, so a second permission would be a
    ///     role nobody grants and an audit line nobody reads.
    /// </remarks>
    public const string StatsPermission = "read";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The object a bucket IS ────────────────────────────────────────────────────────────────

    /// <summary>The <c>Bucket</c> custom resource.</summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>buckets</c> and it collides with the type path's last segment by
    ///     coincidence rather than by construction.</b> It is carried for the reason
    ///     <see cref="GroupVersionKind.Plural" /> gives, and the cluster-backed harness derives a CRD
    ///     stub from it — <c>ClusterConformanceHarness</c> reads group, version, kind and plural off
    ///     <c>ProviderConformanceCase.Objects</c>, which is why this type declares no
    ///     <c>RequiredCrds</c> member and there is none to declare.
    /// </remarks>
    public static GroupVersionKind BucketKind { get; } =
        new() {
            Group = "seaweed.seaweedfs.com", Version = "v1", Kind = "Bucket", Plural = "buckets"
        };

    /// <summary>
    ///     The name of the object a bucket renders: its account's name and its own, joined.
    /// </summary>
    /// <param name="id">The bucket's address.</param>
    /// <remarks>
    ///     ⚠ <b>THE PARENT'S NAME IS IN THE OBJECT NAME BECAUSE THE NAMESPACE DOES NOT DISTINGUISH
    ///     THEM.</b> <c>ReconcileDriver.NamespaceFor</c> is <c>{subscriptionId:N}-{resourceGroup}</c> —
    ///     a parent resource is <i>inside</i> one namespace, not a namespace of its own — so two
    ///     accounts in one resource group may each hold a bucket called <c>assets</c> and a renderer
    ///     that ignored <see cref="ResourceId.ParentNames" /> would have them fighting over one
    ///     <c>Bucket</c> object, each converging by overwriting the other. Every cluster-facing
    ///     conformance assertion depends on this having read the ancestors: an id built without them
    ///     renders a different name and the read-back fails.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string ObjectNameOf(ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the Bucket object it renders would collide "
                + "with every other account's bucket of the same name in the same resource group. A "
                + "bucket is a child type and its address always interleaves its account — see "
                + "StorageBuckets.TypePath.",
                nameof(id)
            )
            : id.ParentNames.Replace('/', '-') + "-" + id.Name;

    /// <summary>The <c>Seaweed</c> a bucket names, which is its account's own name.</summary>
    /// <param name="id">The bucket's address.</param>
    /// <remarks>
    ///     ⚠ <b>Read off the ADDRESS and never off the body</b> — see the remarks on this class.
    ///     <see cref="ResourceId.Parent" /> is a pure function of the address, so this cannot disagree
    ///     with what the write path resolved the parent's index binding against.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string ClusterRefOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no Seaweed for its Bucket to reference.",
            nameof(id)
        );

    /// <summary>The <c>Bucket</c> a bucket owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The bucket's address.</param>
    public static ObjectRef BucketRef(string ns, ResourceId id) =>
        new() { Kind = BucketKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>
    ///     What the S3 protocol requires of a bucket name that <see cref="ResourceNaming" /> does not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>ONE NUMBER, AND IT IS THE ONLY PLACE THE TWO GRAMMARS DISAGREE.</b>
    ///     <see cref="ResourceNaming.Pattern" /> is DNS-1123 —
    ///     <c>[a-z0-9]([-a-z0-9]*[a-z0-9])?</c>, 1 to 63 characters — which already forbids upper case,
    ///     underscores and dots, so an S3 bucket name cannot be produced that looks like an IPv4 address
    ///     or that a signer would reject for case. What it does <i>not</i> forbid is a name of one or two
    ///     characters, and S3 requires three.
    ///     <para>
    ///         ⚠ <b>NOTHING ENFORCES IT AND THERE IS NOWHERE TO PUT THE RULE.</b> A resource's name is
    ///         part of its <i>address</i>, not a schema property, so there is no <c>@pattern</c> to
    ///         carry it and <c>ResourceSchema</c> never sees it. The refusal that does arrive comes from
    ///         the tenant's own S3 client library, which validates the name before it opens a socket —
    ///         so the symptom is a bucket the platform created, the operator reconciled, and no SDK will
    ///         address. <c>conformance.yaml § owed</c>, <c>name-grammar-is-wider-than-s3s</c>;
    ///         <c>StorageBucketDeclarationTests</c> pins the discrepancy so it goes red the day either
    ///         rule moves.
    ///     </para>
    /// </remarks>
    public const int MinimumS3NameLength = 3;

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>FOUR OF <c>BucketSpec</c>'S ELEVEN FIELDS ARE DECLARED AND THE SEVEN OMISSIONS ARE
    ///         EACH A DECISION.</b> <c>charts/managed/seaweedfs/SOURCE</c>'s review records
    ///         <c>BucketSpec</c> as <c>(name, clusterRef, reclaimPolicy, adoptExisting, versioning,
    ///         objectLock, quota, owner, access, placement, anonymousRead)</c>. What is here is
    ///         docs/plan/15 § The resource model's own list — <i>"name, quota, versioning"</i> — plus the
    ///         <c>clusterRef</c> that comes from the address. What is not, and why:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             ⚠ <b><c>anonymousRead</c> — THE ONE THAT LOOKS LIKE AN OMISSION AND IS THE OPPOSITE.</b>
    ///             <c>conformance.yaml § owed</c>'s <c>public-access-is-not-a-property</c> said this
    ///             property was waiting for the child type. It was, and it still cannot ship, and the
    ///             reason has MOVED rather than closed. docs/plan/15: <i>"Public access is off by default
    ///             at the account level and requires an explicit <b>two-step</b> opt-in"</i>, and calls a
    ///             publicly readable bucket <i>"the most-reported cloud misconfiguration in
    ///             existence"</i>. Shipping the bucket half alone is a ONE-step opt-in — precisely the
    ///             thing that sentence forbids. The first step is an account-level switch, and the
    ///             account's api-version is published and immutable, so it is a new date on the PARENT
    ///             rather than a property here.
    ///         </item>
    ///         <item>
    ///             <b><c>objectLock</c></b> — docs/plan/15's own caveat says <i>"object lock/WORM … are
    ///             partial or absent depending on version"</i>, and § The resource model does not list it
    ///             under buckets. A retention guarantee whose engine may not keep it is the shape
    ///             <c>encryption-at-rest</c> already refused.
    ///         </item>
    ///         <item>
    ///             <b><c>placement</c></b> — the account decides. <c>conformance.yaml</c>'s
    ///             <c>replication-is-the-declared-placement</c> says in as many words that the account's
    ///             replication is what a new volume inherits and that changing it is refused at the API;
    ///             a per-bucket override would make one account hold two durability promises with nothing
    ///             saying which object got which, which is why the account's own <c>replication</c> is
    ///             <c>Immutable</c>.
    ///         </item>
    ///         <item>
    ///             <b><c>owner</c> and <c>access</c></b> — both name S3 identities, and there are none.
    ///             See the remarks on this class: a bucket has no credential story of its own.
    ///         </item>
    ///         <item>
    ///             <b><c>adoptExisting</c></b> — it binds a platform resource to storage the platform did
    ///             not create. A tenant who can name a pre-existing bucket can take over one, and the
    ///             quota this type reserves would be reserved against something already full.
    ///         </item>
    ///         <item>
    ///             <b><c>reclaimPolicy</c></b> — <c>Retain</c> would leave object data alive in an account
    ///             with no resource addressing it: untracked, unbilled, and removable only by hand. It is
    ///             also the wrong axis for the problem it looks like it solves — a recovery window is
    ///             docs/plan/06's <c>SupportsSoftDelete</c>, which nothing in the manager reads.
    ///             <c>StorageBucketReconciler</c> renders no <c>reclaimPolicy</c> and takes the CRD's own
    ///             default, so this platform never asks for retention it cannot account for.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>Every default here is the chart's default, spelled as JSON</b> — there is no
    ///         <c>@default</c> directive, because the chart's default <i>is</i> the YAML literal on the
    ///         annotated line.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the bucket is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The bucket's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the bucket. Must be the cluster the "
                    + "account is in — nothing checks that, and a bucket placed elsewhere is applied "
                    + "into a namespace with no object store in it."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/quota",
                    SchemaKind.Nested,
                    Description: "How much the bucket may hold."
                ),
                new(
                    "/properties/quota/size",
                    SchemaKind.Text,
                    Description: "The bucket's size limit, in Kubernetes quantity form. Empty means no "
                    + "limit other than the account's own provisioned capacity. ⚠ This is a limit "
                    + "inside capacity the account already reserved; it does not add any, and it is not "
                    + "what the bucket is billed on."
                ) {
                    Pattern = StorageAccounts.OptionalQuantityPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"50Gi\""
                },
                new(
                    "/properties/versioning",
                    SchemaKind.Boolean,
                    Description: "Whether the bucket keeps previous versions of an overwritten object. "
                    + "Off by default. ⚠ docs/plan/15 § Object storage: SeaweedFS' S3 implementation is "
                    + "\"good but not complete\" and names object versioning as one of the partial "
                    + "areas, so the supported-operations table for the deployed version — not this "
                    + "property — is what says how completely it behaves."
                ) {
                    DefaultJson = "false"
                }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/stats</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ Declared with nothing serving it — see <see cref="StatsAction" />. Nothing in it is
    ///     <c>Secret</c>, which is the difference from the account's <c>listKeys</c> response and the
    ///     reason this action shares the <c>read</c> permission.
    /// </remarks>
    public static ResourceSchema StatsResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/objectCount",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many objects the bucket holds, as of the last sample."
                ),
                new(
                    "/sizeBytes",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many bytes the bucket holds before replication, as of the last "
                    + "sample. ⚠ Sampled rather than live — docs/plan/15 § Metering samples SeaweedFS "
                    + "volume stats hourly per bucket — so it is not a number to write an assertion "
                    + "against immediately after a PUT."
                ),
                new(
                    "/sampledAt",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When the two figures above were sampled, RFC 3339. ⚠ Returned because "
                    + "a sampled number with no timestamp is a number a caller will read as live."
                ) {
                    Format = SchemaFormat.DateTime
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The size limit a body asks for, or empty for none.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string QuotaSize(JsonElement desired) => Text(desired, "quota", "size");

    /// <summary>Whether a body asks for object versioning.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool Versioning(JsonElement desired) => Flag(desired, "versioning");

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>Bucket</c> document a desired body becomes.</summary>
    /// <param name="id">The bucket's address — its account's name comes from here.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>spec.name</c> IS THE RESOURCE'S OWN NAME AND <c>metadata.name</c> IS NOT.</b>
    ///         The S3 bucket name a tenant addresses is <c>{bucketName}</c>; the Kubernetes object is
    ///         <c>{accountName}-{bucketName}</c>, because the namespace does not separate two accounts
    ///         — see <see cref="ObjectNameOf" />. Rendering the qualified name into <c>spec.name</c>
    ///         would give the tenant a bucket at an address neither they nor docs/plan/15 named.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>reclaimPolicy</c>, no <c>adoptExisting</c>, no <c>anonymousRead</c> and no
    ///         <c>owner</c>.</b> Each is an omission with an argument, in <see cref="Schema2026" />'s
    ///         remarks. The one worth repeating here is <c>anonymousRead</c>: leaving it unset is what
    ///         honours docs/plan/15's <i>"public access is off by default"</i>, and it is the default
    ///         rather than a rendered <c>false</c> so that the day an account-level first step exists,
    ///         the property that turns it on is a new field rather than a changed value.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here — ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably.
    ///     </para>
    /// </remarks>
    public static string BucketJson(ResourceId id, JsonElement desired) {
        var spec = new JsonObject {
            ["name"] = id.Name,
            // ⚠ THE ONE FIELD THAT MAKES THIS A CHILD, AND IT COMES FROM THE ADDRESS. A body property
            // holding the account's name would be a second spelling of a fact ResourceId.Parent
            // already answers, and the two would disagree the first time a body was sent under the
            // wrong path.
            ["clusterRef"] = ClusterRefOf(id),
            ["versioning"] = Versioning(desired)
        };

        var quota = QuotaSize(desired);
        if (quota.Length > 0) {
            spec["quota"] = quota;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) }, ["spec"] = spec
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="id">The bucket's address.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, for the two reasons <see cref="StorageAccounts.Matches" /> gives</b> —
    ///         the CRD carries <c>+kubebuilder:default</c> markers, and the operator installs a mutating
    ///         webhook whose defaulter is an unimplemented scaffold that some future release will fill
    ///         in. An equality comparison would pass against this operator and break silently against
    ///         the next.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>clusterRef</c> IS COMPARED AND IT IS THE FIELD THIS TYPE MOST NEEDS COMPARED.</b>
    ///         A <c>Bucket</c> whose <c>clusterRef</c> was rewritten — by a merge, by an admission
    ///         policy, by a <c>kubectl edit</c> — is a bucket serving a different account's data under
    ///         this account's resource id, with nothing anywhere reporting it. It is derived from the
    ///         address rather than from the body precisely so that the comparison cannot be satisfied by
    ///         a body that agrees with itself.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, ResourceId id, JsonElement desired) =>
        MatchesBody(objectJson, desired)
        && Spec(objectJson) is { } spec
        && spec["name"]?.GetValue<string>() == id.Name
        && spec["clusterRef"]?.GetValue<string>() == ClusterRefOf(id);

    /// <summary>
    ///     The half of <see cref="Matches" /> that a desired <b>body</b> alone decides.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS SPLIT EXISTS BECAUSE OF A LIMIT IN THE SHARED CONFORMANCE HARNESS THAT ONLY A
    ///         CHILD TYPE MEETS, AND IT IS WORTH STATING RATHER THAN WORKING AROUND SILENTLY.</b>
    ///         <c>ProviderConformanceCase.ObjectMatchesDesired</c> is
    ///         <c>(objectJson, desiredJson) =&gt; bool</c> and carries <b>no address</b> — which is
    ///         exactly right for the five top-level types that ship, whose whole rendered spec is a
    ///         function of the body. A child's is not: <c>spec.name</c> is the resource's own name and
    ///         <c>spec.clusterRef</c> is its <i>parent's</i>, and both live in the address. So the
    ///         predicate the shared suite can evaluate for a bucket is strictly smaller than the one
    ///         the reconciler evaluates, and the two are separated here so that the smaller one is a
    ///         named thing with an argument rather than a quietly weakened <c>Matches</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tempting alternative is worse and was tried.</b> Reconstructing the harness's
    ///         own address inside the case — it builds one from <c>ConformanceIds.AncestorName(0)</c>
    ///         and a per-test resource name — fails on the resource name, which every assertion picks
    ///         differently; and deriving the address <i>from the object</i> (<c>metadata.name</c> minus
    ///         <c>spec.name</c>) would compare the object against itself, which is the shape that let
    ///         an earlier provider's casing sabotage stay green.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is lost, because the assertion moved rather than vanished.</b>
    ///         <c>StorageBucketReconcilerTests</c> asserts <c>spec.clusterRef</c> and <c>spec.name</c>
    ///         against a real address, including the case the suite could never have reached: two
    ///         accounts in one resource group each holding a bucket of the same name.
    ///         <c>charts/managed/seaweedfs-bucket/conformance.yaml § owed</c>,
    ///         <c>object-matches-desired-cannot-see-an-address</c>.
    ///     </para>
    /// </remarks>
    public static bool MatchesBody(string objectJson, JsonElement desired) {
        if (Spec(objectJson) is not { } spec) {
            return false;
        }

        var quota = QuotaSize(desired);

        return spec["versioning"]?.GetValue<bool>() == Versioning(desired)
            && (quota.Length == 0 || spec["quota"]?.GetValue<string>() == quota);
    }

    /// <summary>The <c>spec</c> of a <c>Bucket</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    /// <remarks>
    ///     ⚠ Dispatches on the object's <c>kind</c> even though this type owns one kind, because a
    ///     conformance case supplies the comparison as one function over every object the resource owns
    ///     and an unrecognised document must be <see langword="false" /> rather than assumed.
    /// </remarks>
    static JsonObject? Spec(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document
            && document["kind"]?.GetValue<string>() is (null or "Bucket")
            && document["spec"] is JsonObject spec
                ? spec
                : null;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the bucket in.</param>
    /// <param name="quotaSize">The size limit, or empty for none.</param>
    /// <param name="versioning">Whether to keep previous versions.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>, for the reason
    ///     <see cref="StorageAccounts.Body" /> gives: <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string quotaSize = "50Gi",
        bool versioning = false,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["quota"] = new JsonObject { ["size"] = quotaSize },
                ["versioning"] = versioning
            }
        }.ToJsonString();

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Root(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string parent, string name) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    static bool Flag(JsonElement desired, string name) =>
        Root(desired, name) is { ValueKind: JsonValueKind.True };
}
