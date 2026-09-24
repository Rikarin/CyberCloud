using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.DBforPostgreSQL/servers</c>: the type, its
///     api-version, its body shape, and the CloudNativePG objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue, <i>"PostgreSQL — CyberCloud.DBforPostgreSQL/servers"</i>. This
///         is the first provider that is a <b>feature</b>: <c>CyberCloud.Providers.Sample</c> is
///         deliberately trivial (docs/plan/24 § Phase 1, docs/plan/25 § R1) and exists to measure the
///         platform, so nothing here may lean on it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="Schema2026" /> is the authored side of the pair, and
///             <c>charts/managed/postgres/values.yaml</c> is the other half.
///         </b> ADR-010 § Which end
///         authors the schema, DECIDED 2026-08-11:
///         <i>
///             "The C# <c>ResourceSchema</c> is authored. The
///             chart's <c>@param</c> annotations are generated from it and diffed."
///         </i> Every property
///         below whose pointer begins <c>/properties/</c> and is not <see cref="ClusterIdPointer" />
///         has a <c>@param</c> row in that file at the same pointer — the chart already emits
///         <c>x-cybercloud-pointer</c>, so the correspondence is checkable by eye today and by a
///         generator when one exists.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Two properties here are deliberately <i>not</i> chart rows, and that is a third
///             category ADR-010 does not name.
///         </b> That ADR splits the chart into 26 API rows and 10
///         <c>@internal</c> rendering inputs and concludes the two files "overlap on 26 rows". They
///         also diverge the other way: <see cref="ClusterIdPointer" /> and <c>/location</c> are body
///         properties with no Helm value behind them — a chart is rendered <i>into</i> a cluster and
///         has no opinion about which one, and a region is a billing fact rather than a rendering
///         input. A generator that rewrote <c>values.yaml</c> from this schema has to skip them, and
///         nothing written down so far says so.
///     </para>
///     <para>
///         ⚠ <b>Something does now, and for a day it did the opposite.</b> The generator landed on
///         2026-08-12 with the rule "everything under <c>/properties</c> that is not
///         <see cref="SchemaProperty.ReadOnly" />", which <see cref="ClusterIdPointer" /> passes — so
///         the chart grew a <c>clusterId: ""</c> row under <c>## @required</c> and
///         <c>## @format uuid</c>, and the paragraph above was read as a prediction that had been
///         overtaken rather than a rule nobody had implemented. <c>""</c> is not a uuid;
///         <c>helm lint --strict</c> took it only because JSON Schema 2020-12 treats <c>format</c> as
///         an annotation rather than an assertion. <c>ChartAnnotationEmitter</c> is now told the
///         placement pointer — <see cref="ResourceTypeRegistration.ClusterIdPointer" />, which
///         <c>PostgresProvider</c>'s <c>RequiresCluster</c> call already records — and skips
///         it. The chart is 25 API rows and 11 <c>@internal</c> ones; <c>/location</c> is skipped by
///         being root-level, as it always was. charts/README.md § What a chart cannot say carries
///         the decision.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <c>/properties/bootstrap/password</c> is absent on purpose and the chart row moved to
///             <c>@internal</c> to match.
///         </b> Its own chart description already said the reconciler
///         supplies it —
///         <i>
///             "Provisioned into the tenant's Vault and read back by ISecretResolver at
///             render time"
///         </i> — so it was never a tenant setting. Declaring it as a body property would
///         have been worse than redundant: nothing in the write path replaces a
///         <see cref="SchemaProperty.Secret" /> value with a <c>SecretRef</c> before the grain writes
///         desired state, so the plaintext would have been persisted, which
///         docs/plan/00 § Non-negotiables forbids outright. See the remarks on
///         <see cref="ClusterJson" /> for where the value does reach the data plane.
///     </para>
/// </remarks>
public static class PostgresServers {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.DBforPostgreSQL";

    /// <summary>The one resource type.</summary>
    /// <remarks>
    ///     docs/plan/12 also names <c>servers/databases</c>, <c>servers/roles</c> and
    ///     <c>servers/firewallRules</c>. They are separate types with separate schemas and separate
    ///     reconcilers, and each is its own change — declaring them here with no reconciler would put
    ///     types in the registry that answer <c>202</c> and converge nothing.
    /// </remarks>
    public const string TypePath = "servers";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/postgres/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>
    ///     The chart this type is the configuration surface of — docs/plan/12 § The pattern, once,
    ///     piece 1.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A registry declaration, not a render path, and the two are different mechanisms with
    ///         the same word on them.
    ///     </b> <see cref="IResourceTypeBuilder.Chart" /> records which chart
    ///     describes this type; <c>IKubeCommandBuilder.Chart</c> asks
    ///     <c>CyberCloud.Kubernetes.Charts</c> to render one, and that assembly does not exist — the
    ///     builder answers with a named failure saying so. So the reconciler renders the same two
    ///     objects the chart's templates render, in C#, and this constant is what ties the two halves
    ///     together until the renderer lands.
    /// </remarks>
    public const string ChartName = "managed/postgres";

    /// <summary>How long a deleted server can be restored for. docs/plan/06 § Tags, locks.</summary>
    /// <remarks>
    ///     ⚠ <b>Seven days is a claim about this type's data, not a platform default</b> — nothing
    ///     supplies this number when a type declares no window. docs/plan/06 § Tags, locks names 7 days
    ///     for <i>"resources carrying data (Vault, Storage, databases)"</i>, and a managed PostgreSQL
    ///     cluster is the third of those. What the window preserves is what the teardown leaves: the
    ///     instances' <c>PersistentVolumeClaim</c>s, the stored body, and the committed quota.
    ///     <para>
    ///         ⚠ <b>The claims are left only because the teardown detaches them first.</b>
    ///         CloudNativePG stamps a controller reference on every claim it creates, and the
    ///         garbage collector removes them with the <c>Cluster</c>; until
    ///         <c>PostgresServerReconciler.DeleteAsync</c> cleared that reference ahead of the delete,
    ///         this constant advertised a window whose restore came back to an <c>initdb</c> —
    ///         issue #69, and <c>charts/managed/postgres/conformance.yaml § owed</c>.
    ///     </para>
    /// </remarks>
    public const int SoftDeleteDays = 7;

    /// <summary>
    ///     The label CloudNativePG stamps on every claim it creates for a cluster, carrying the
    ///     cluster's name — <c>utils.ClusterLabelName</c>, written by
    ///     <c>SetInheritedData</c>'s <c>LabelClusterName</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ This and not <c>cnpg.io/instanceName</c>, because the instance names are the thing the
    ///     provider cannot predict: a failover replaces instance 2 with instance 3, and the serial
    ///     only ever moves forward. The cluster label is the same on all of them and is what
    ///     <see cref="ClaimSelector" /> asks the API server for.
    /// </remarks>
    public const string ClaimLabel = "cnpg.io/cluster";

    /// <summary>
    ///     The annotation that stops CloudNativePG reconciling a <c>Cluster</c> while it carries
    ///     <see cref="PausedValue" /> — <c>utils.ReconciliationLoopAnnotationName</c>, checked first
    ///     thing in <c>ClusterReconciler.reconcile</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Applied around the two moments the operator must not be looking.</b> A restore
    ///     creates the <c>Cluster</c> and then hands it the retained claims; the operator's first
    ///     pass is milliseconds behind the create, sees no claims of its own yet, and would start a
    ///     fresh primary whose <c>initdb</c> moves the tenant's data directory aside. A teardown
    ///     detaches the claims and then deletes the <c>Cluster</c>; an operator pass in that gap
    ///     would see a cluster with no claims and register it unrecoverable. Neither window is a
    ///     place the operator has anything useful to do, so it is told to wait.
    /// </remarks>
    public const string PauseAnnotation = "cnpg.io/reconciliationLoop";

    /// <summary>The value of <see cref="PauseAnnotation" /> that pauses the operator.</summary>
    public const string PausedValue = "disabled";

    /// <summary>The field manager the apply runs under — ADR-013's stable per-provider name.</summary>
    public const string FieldManager = "cybercloud/cybercloud.dbforpostgresql";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the connection credentials.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions, Credentials:
    ///     <i>
    ///         "<c>listKeys</c> is an action with
    ///         its own permission, audited on every call"
    ///     </i>. ⚠ <c>regenerateKeys</c> is named in the same
    ///     paragraph and is <b>not</b> declared, because it is specified with
    ///     <i>
    ///         "a rolling grace
    ///         period so rotation is not an outage"
    ///     </i> and nothing in the platform can hold two live
    ///     credentials for one resource yet. An action whose contract cannot be honoured is worse than
    ///     an absent one.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     A key export is not a read: docs/plan/07 § Consistency puts it in the fully-consistent row
    ///     by name, and <c>ResourceManagerService</c> passes an action's <c>secret</c> flag into the
    ///     authorization call for exactly that reason. Sharing <c>read</c> would make every viewer of a
    ///     database a holder of its password.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>CloudNativePG's <c>Cluster</c> — the object a server <i>is</i>.</summary>
    /// <remarks>
    ///     ⚠ <see cref="GroupVersionKind.Plural" /> is carried rather than derived, for the reason that
    ///     type's own remarks give. It must match <c>charts/managed/postgres/SOURCE</c>'s
    ///     <c>upstream-api: postgresql.cnpg.io/v1</c>, which is the CRD contract these shapes render
    ///     against.
    /// </remarks>
    public static GroupVersionKind ClusterKind { get; } =
        new() { Group = "postgresql.cnpg.io", Version = "v1", Kind = "Cluster", Plural = "clusters" };

    /// <summary>CloudNativePG's <c>Pooler</c> — PgBouncer in front of the cluster.</summary>
    public static GroupVersionKind PoolerKind { get; } =
        new() { Group = "postgresql.cnpg.io", Version = "v1", Kind = "Pooler", Plural = "poolers" };

    /// <summary>The core <c>Secret</c> the backup key is rendered into.</summary>
    public static GroupVersionKind SecretKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    // ── Backups, to the platform's object store ───────────────────────────────────────────────

    /// <summary>The prefix of a server's bucket: <c>pg-{resourceId:N}</c>.</summary>
    public const string BucketPrefix = "pg";

    /// <summary>The <c>Secret</c> a server's backup key is rendered into: <c>{name}-backup-s3</c>.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Beside the server, in the tenant's namespace, and that is the one place the key is
    ///     readable by the tenant.</b> CloudNativePG's instance pods read <c>s3Credentials</c> from a
    ///     <c>Secret</c> in their own namespace and nowhere else, so the key has to be there; what keeps
    ///     that safe is <see cref="ObjectStoreCredentials" />'s scope — the key opens this server's
    ///     bucket and no other — rather than the Secret's visibility.
    /// </remarks>
    public static string BackupSecretName(string name) => name + "-backup-s3";

    /// <summary>The backup <c>Secret</c> a server owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef BackupSecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = BackupSecretName(name) };

    /// <summary>The key the <c>Secret</c> files the access key id under.</summary>
    public const string AccessKeyIdKey = "ACCESS_KEY_ID";

    /// <summary>The key the <c>Secret</c> files the secret access key under.</summary>
    public const string SecretAccessKeyKey = "ACCESS_SECRET_KEY";

    /// <summary>
    ///     The <c>Secret</c> document a server's backup key becomes.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="key">The key the vault holds. ⚠ A value for the length of the pass that renders it.</param>
    public static string BackupSecretJson(string name, ObjectStoreKey key) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(key);

        // ⚠ `data`, base64, and not `stringData`: the API server folds stringData into data on write,
        // so a server-side apply of stringData reads back as a field this manager never set, and every
        // pass would see drift. StorageAccounts.ConfigSecretJson renders its identities file the same way.
        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = BackupSecretName(name) },
            ["type"] = "Opaque",
            ["data"] = new JsonObject {
                [AccessKeyIdKey] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.AccessKeyId)),
                [SecretAccessKeyKey] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.SecretAccessKey))
            }
        }.ToJsonString();
    }

    /// <summary>Where a server's backups go: the platform's store, in the server's own bucket.</summary>
    /// <param name="DestinationPath">The <c>s3://{bucket}/</c> barman archives under.</param>
    /// <param name="EndpointUrl">The store's data-plane endpoint.</param>
    /// <param name="SecretName">The <c>Secret</c> holding the key — <see cref="BackupSecretName" />.</param>
    public sealed record BackupStore(string DestinationPath, string EndpointUrl, string SecretName) {
        /// <summary>The store a server with this id and name renders.</summary>
        /// <param name="resourceId">The server's GUID.</param>
        /// <param name="name">The server's name.</param>
        /// <param name="endpointUrl">The store's data-plane endpoint.</param>
        public static BackupStore For(Guid resourceId, string name, string endpointUrl) =>
            new(
                "s3://" + ObjectStoreCredentials.BucketFor(BucketPrefix, resourceId) + "/",
                endpointUrl,
                BackupSecretName(name)
            );
    }

    /// <summary>The labels every claim CloudNativePG created for a server carries, and the selector's pairs.</summary>
    /// <param name="name">The resource's own name, which is the <c>Cluster</c>'s.</param>
    public static ImmutableDictionary<string, string> ClaimOwnership(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ImmutableDictionary<string, string>.Empty.Add(ClaimLabel, name);
    }

    /// <summary>The selector that lists a server's claims: <c>cnpg.io/cluster={name}</c>.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ClaimSelector(string name) => RetainedVolume.Selector(ClaimOwnership(name));

    /// <summary>
    ///     The owner reference a claim carries when the <c>Cluster</c> it belongs to has this uid —
    ///     the shape <c>utils.SetAsOwnedBy</c> writes, with <c>controller: true</c> and nothing else.
    /// </summary>
    /// <param name="name">The <c>Cluster</c>'s name.</param>
    /// <param name="uid">Its <c>metadata.uid</c>, read back from the API server.</param>
    public static OwnerRef ClusterOwner(string name, string uid) =>
        new() { ApiVersion = ClusterKind.ApiVersion, Kind = ClusterKind.Kind, Name = name, Uid = uid };

    /// <summary>Whether a <c>Cluster</c> read back from the API server is carrying the pause.</summary>
    /// <param name="objectJson">The object's JSON, as returned.</param>
    public static bool IsPaused(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        return (parsed as JsonObject)?["metadata"] is JsonObject metadata
            && metadata["annotations"] is JsonObject annotations
            && annotations[PauseAnnotation] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && string.Equals(text, PausedValue, StringComparison.Ordinal);
    }

    // ── The two constraint vocabularies the chart cannot spell ─────────────────────────────────
    //
    // ⚠ READ THIS BEFORE ADDING ANOTHER `Pattern` HERE. ADR-010 § Which end authors the schema lists
    // seven SchemaProperty facts "the annotation vocabulary has no syntax for", and two of them —
    // `Pattern` and `MinLength`/`MaxLength` — are load-bearing on this type. charts/README.md § The
    // annotation format has @param, @required, @secret, @immutable, @internal, @enum, @range and
    // @widget, and nothing else; a Kubernetes quantity and a Postgres identifier are expressible in
    // neither @enum (they are open sets) nor @range (they are strings).
    //
    // The constraints are declared HERE, where ResourceSchema.Validate enforces them and all four of
    // ADR-012's built emitters read them, and the chart's `@param` block simply does not carry them.
    // That is a real, named loss on exactly one surface — the fifth, the chart-annotation emitter,
    // which ADR-012's own table marks "Not built" — and the alternative is worse in both directions:
    // dropping the constraint means a body that validates and then produces a Cluster CR the API
    // server refuses AFTER the caller was told 202, and growing the vocabulary before the emitter
    // exists means editing build/Build.Charts.cs, charts/README.md and ADR-010 for a consumer that
    // has no code.

    // ⚠ THE QUANTITY GRAMMAR WAS A LOCAL COPY, AND THE COPY COST THE PLATFORM A SECOND QUANTITY
    // PARSER. Not here: the author who needed to turn one of these strings into a number was reading
    // ValkeyCaches' copy, and wrote a `double` parser beside it for a grammar the platform already
    // parsed exactly in `decimal`. The mechanism was the duplication rather than that provider —
    // whichever copy an author lands on, the parser is not next to it.
    //
    // So these two now alias KubeQuantity's, which is where the parser lives. Rule 2 of
    // docs/plan/03 § Assembly graph rules is untouched: KubeQuantity is in
    // CyberCloud.ResourceManager.Contracts, which this project already references and every provider
    // may — no Providers.* edge is created, and GlobalUsings.cs already imports the namespace, so
    // reaching the shared constant costs a provider author nothing but the name.

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>An unquoted PostgreSQL identifier, lower-case.</summary>
    /// <remarks>
    ///     Postgres folds an unquoted identifier to lower case, so accepting <c>App</c> would create a
    ///     database called <c>app</c> and report the name the caller did not get.
    /// </remarks>
    public const string IdentifierPattern = "[a-z_][a-z0-9_]*";

    /// <summary>An <c>s3://bucket/prefix</c> URL, or the empty string.</summary>
    /// <remarks>
    ///     ⚠ <c>s3</c> only, and not <see cref="SchemaFormat.Uri" />. ADR-008 makes object storage
    ///     speak S3 and nothing else, so a <c>gs://</c> destination is a backup that silently never
    ///     runs; and <see cref="SchemaFormat.Uri" /> would refuse this property's own <c>""</c>
    ///     default, which was meant to spell "fill it in from the tenant's default bucket".
    ///     ⚠ <b>Nothing fills it in, and CloudNativePG refuses the empty string</b> — the definition
    ///     puts <c>minLength: 1</c> on <c>spec.backup.barmanObjectStore.destinationPath</c>, found
    ///     by issue #91 the first time a conformance run validated against it. So a body that leaves
    ///     backups on and the path empty is refused by <see cref="BackupDestinationProblem" /> before
    ///     the apply, naming this property, rather than by the API server naming the operator's.
    ///     The default stays <c>""</c> because the api-version is published and a default is part of
    ///     its contract; what changed is what the empty string means.
    /// </remarks>
    public const string BackupDestinationPattern = @"(s3://[a-z0-9][a-z0-9.\-]*[a-z0-9](/[^\s]*)?)?";

    /// <summary>The longest a PostgreSQL identifier may be before the server truncates it.</summary>
    /// <remarks>
    ///     ⚠ Truncation rather than rejection is what makes this worth declaring: Postgres silently
    ///     cuts an identifier at <c>NAMEDATALEN - 1</c>, so a 70-character database name produces a
    ///     database whose name is not the one in the resource body and no error anywhere.
    /// </remarks>
    public const int MaxIdentifierLength = 63;

    /// <summary>
    ///     What a recovery point's name may be: a Kubernetes object name, because it names
    ///     CloudNativePG's <c>Backup</c> — or empty.
    /// </summary>
    public const string RecoveryPointPattern = "([a-z0-9]([-a-z0-9.]*[a-z0-9])?)?";

    /// <summary>The longest name <see cref="RecoveryPointPattern" /> admits — a DNS subdomain's.</summary>
    public const int MaxRecoveryPointLength = 253;

    /// <summary>The property a restore reads its recovery point from.</summary>
    public const string RecoveryPointPointer = "/properties/restore/recoveryPoint";

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Twenty-eight properties: the 25 API-facing <c>@param</c> rows of
    ///         <c>charts/managed/postgres/values.yaml</c> at the pointers that file already emits as
    ///         <c>x-cybercloud-pointer</c>, plus <c>/properties</c> itself and the two body-only
    ///         properties the type's remarks explain.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every default here is the chart's default, spelled as JSON.</b> There is no
    ///         <c>@default</c> directive because the chart's default <i>is</i> the YAML literal on the
    ///         annotated line — charts/README.md § The annotation format — so the two files agree by
    ///         being read side by side, and a mismatch is the first thing a chart-annotation emitter
    ///         would report.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the server is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The server's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    true,
                    Description: "The cluster whose namespace holds the CloudNativePG objects."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },

                // ── The chart's API surface, in the chart's own declaration order ───────────────
                new(
                    "/properties/version",
                    SchemaKind.Text,
                    true,
                    Description: "Major PostgreSQL version. Minor upgrades are applied automatically "
                    + "in the maintenance window."
                ) { AllowedValues = ["16", "17", "18"], DefaultJson = "\"17\"" },
                new(
                    "/properties/replicas",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "Number of instances, including the primary. One is a single point of "
                    + "failure and is offered for development only."
                ) { Minimum = 1, Maximum = 5, DefaultJson = "2" },
                new(
                    "/properties/synchronousReplication",
                    SchemaKind.Boolean,
                    Description: "Whether commits wait for a replica. Costs write latency and removes "
                    + "the window in which a failover loses transactions."
                ) { DefaultJson = "false" },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory, either by preset or explicitly."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Databases use the s1 family, "
                    + "which is 1 vCPU to 4 GiB."
                ) {
                    AllowedValues = [
                        "s1.nano",
                        "s1.micro",
                        "s1.small",
                        "s1.medium",
                        "s1.large",
                        "s1.xlarge",
                        "s1.2xlarge",
                        "s1.4xlarge"
                    ],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"s1.small\""
                },
                new(
                    "/properties/sizing/cpu",
                    SchemaKind.Text,
                    Description: "Explicit vCPU quantity in Kubernetes form, for example 500m or 2. "
                    + "Empty means take it from the preset."
                ) { Pattern = OptionalQuantityPattern, DefaultJson = "\"\"" },
                new(
                    "/properties/sizing/memory",
                    SchemaKind.Text,
                    Description: "Explicit memory quantity in Kubernetes form, for example 4Gi. Empty "
                    + "means take it from the preset."
                ) { Pattern = OptionalQuantityPattern, DefaultJson = "\"\"" },
                new("/properties/storage", SchemaKind.Nested, Description: "The data volume."),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    true,
                    Description: "Data volume size in Kubernetes quantity form. Grows online; never "
                    + "shrinks."
                ) { Pattern = QuantityPattern, DefaultJson = "\"20Gi\"", ExampleJson = "\"20Gi\"" },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name. Empty means the cluster default."
                ) { Widget = WidgetHint.StorageClass, Immutable = true, DefaultJson = "\"\"" },
                new(
                    "/properties/storage/walSize",
                    SchemaKind.Text,
                    Description: "Size of the separate write-ahead-log volume. Empty means the WAL "
                    + "shares the data volume."
                ) { Pattern = OptionalQuantityPattern, DefaultJson = "\"\"" },
                new(
                    "/properties/pooling",
                    SchemaKind.Nested,
                    Description: "PgBouncer in front of the cluster."
                ),
                new(
                    "/properties/pooling/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether to run a connection pooler. On by default — a managed "
                    + "Postgres without one fails at the first serverless workload, and adding it "
                    + "later changes the connection string."
                ) { DefaultJson = "true" },
                new(
                    "/properties/pooling/mode",
                    SchemaKind.Text,
                    Description: "PgBouncer pooling mode. Transaction pooling is the useful one and "
                    + "breaks session-scoped features such as prepared statements and advisory locks. "
                    + "statement is published in this api-version and refused while pooling.enabled is "
                    + "true: CloudNativePG's Pooler admits only session and transaction."
                ) { AllowedValues = ["session", "transaction", "statement"], DefaultJson = "\"transaction\"" },
                new(
                    "/properties/pooling/instances",
                    SchemaKind.WholeNumber,
                    Description: "Number of pooler pods."
                ) { Minimum = 1, Maximum = 8, DefaultJson = "2" },
                new(
                    "/properties/extensions",
                    SchemaKind.Array,
                    Description: "Extensions to install, from the platform allow-list. An "
                    + "arbitrary-extension escape hatch is a code-execution surface and is not offered."
                ) {
                    ElementKind = SchemaKind.Text,
                    AllowedValues = ["pgvector", "postgis", "pg_stat_statements", "timescaledb"],
                    DefaultJson = "[]",
                    ExampleJson = """["pgvector"]"""
                },
                new(
                    "/properties/backup",
                    SchemaKind.Nested,
                    Description: "Backup to the tenant's object store, using CloudNativePG's "
                    + "barman-cloud."
                ),
                new(
                    "/properties/backup/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether continuous backup and WAL archiving run."
                ) { DefaultJson = "true" },
                new(
                    "/properties/backup/retentionDays",
                    SchemaKind.WholeNumber,
                    Description: "How long base backups and WAL are kept. The "
                    + "point-in-time-recovery window is this number of days."
                ) { Minimum = 1, Maximum = 365, DefaultJson = "14" },
                new(
                    "/properties/backup/destinationPath",
                    SchemaKind.Text,
                    Description: "Leave empty. Base backups and WAL go to the platform's object "
                    + "store, in a bucket of this server's own, with a key the platform issues and "
                    + "holds. A destination of your own is refused naming this property: this "
                    + "api-version has nowhere to carry the credentials it would need."
                ) {
                    Pattern = BackupDestinationPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"\""
                },
                new(
                    "/properties/restore",
                    SchemaKind.Nested,
                    Description: "Where the server's data comes from when it is created from a "
                    + "recovery point rather than empty."
                ),
                new(
                    "/properties/restore/recoveryPoint",
                    SchemaKind.Text,
                    Description: "The recovery point this server was restored from. Set only by a backup "
                    + "vault's recover action, which creates the server: a write may send back the "
                    + "value the server holds and nothing else. Empty means the server started as a "
                    + "new, empty database."
                ) {
                    Pattern = RecoveryPointPattern,
                    MaxLength = MaxRecoveryPointLength,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/bootstrap",
                    SchemaKind.Nested,
                    Description: "What exists in the database the moment it comes up."
                ),
                new(
                    "/properties/bootstrap/database",
                    SchemaKind.Text,
                    Description: "Name of the application database created on first start."
                ) {
                    Pattern = IdentifierPattern, MinLength = 1, MaxLength = MaxIdentifierLength, DefaultJson = "\"app\""
                },
                new(
                    "/properties/bootstrap/owner",
                    SchemaKind.Text,
                    Description: "Role that owns the application database."
                ) {
                    Pattern = IdentifierPattern, MinLength = 1, MaxLength = MaxIdentifierLength, DefaultJson = "\"app\""
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether CloudNativePG emits a PodMonitor for the platform's metrics "
                    + "stack."
                ) { DefaultJson = "true" }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/listKeys</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Declared even though no handler serves it, because an undeclared response is the one
    ///         part of the API surface with no contract
    ///     </b> — the reason
    ///     <c>CyberCloud.Providers.Sample</c> gives for declaring <c>ping</c>'s shapes. What leaves the
    ///     platform through a <c>secret: true</c> action is exactly the thing that should be written
    ///     down before it leaves.
    ///     <para>
    ///         ⚠ There is no request shape: an action that takes no body and one whose body the
    ///         registry does not know are different states, and <c>ActionRegistration</c> renders the
    ///         second honestly rather than inventing an empty object for it.
    ///     </para>
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/host",
                    SchemaKind.Text,
                    true,
                    Description: "The in-cluster DNS name to connect to. ⚠ The pooler's when pooling "
                    + "is on, so turning pooling off later is a visible change to this value rather "
                    + "than a silent one."
                ),
                new("/port", SchemaKind.WholeNumber, true, Description: "The TCP port.") {
                    Minimum = 1, Maximum = 65535
                },
                new("/database", SchemaKind.Text, true, Description: "The application database."),
                new("/username", SchemaKind.Text, true, Description: "The owning role."),
                new(
                    "/password",
                    SchemaKind.Text,
                    true,
                    Secret: true,
                    Description: "The owning role's password, read from the tenant's Vault for this "
                    + "call only."
                )
            ]
        );

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, s1 family.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         This table is a second copy of <c>charts/managed/postgres/templates/_helpers.tpl</c>'s
    ///         <c>postgres.resources</c>, and the duplication is the cost of having no chart renderer.
    ///     </b>
    ///     <c>CyberCloud.Kubernetes.Charts</c> does not exist (docs/plan/03 § src), so the objects are
    ///     built here; the moment it does, this table and the reconciler's use of it should go and the
    ///     chart's should stay, because the chart is the file a support engineer reads. Until then both
    ///     exist and <c>ChartRegistryPairTests.TheSizingTableIsTheChartsSizingTable</c> asserts they
    ///     agree value for value. ⚠ Named unlike its four siblings, which all spell the same assertion
    ///     <c>TheSizingTableAgreesWithTheChartsValueForValue</c>. This citation used to name a
    ///     "PostgresSizingTests", following those siblings' file naming — a class that has never
    ///     existed here, which is what a name guessed from the pattern rather than read off the tree
    ///     looks like. The Code citations gate in build/Build.Architecture.cs now refuses that.
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["s1.nano"] = ("100m", "512Mi"),
            ["s1.micro"] = ("250m", "1Gi"),
            ["s1.small"] = ("500m", "2Gi"),
            ["s1.medium"] = ("1", "4Gi"),
            ["s1.large"] = ("2", "8Gi"),
            ["s1.xlarge"] = ("4", "16Gi"),
            ["s1.2xlarge"] = ("8", "32Gi"),
            ["s1.4xlarge"] = ("16", "64Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     Each allowed value of <c>/properties/extensions</c>, and the two names PostgreSQL knows it
    ///     by: the extension <c>CREATE EXTENSION</c> installs, and the library the postmaster must
    ///     preload — <c>""</c> when there is none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ALLOW-LIST IS ONE LIST AND POSTGRESQL READS IT AS TWO VOCABULARIES.</b>
    ///         <c>/properties/extensions</c> reaches <c>bootstrap.initdb.postInitApplicationSQL</c> as
    ///         an extension name and <c>spec.postgresql.shared_preload_libraries</c> as a library name,
    ///         and for two of the four values those are different strings — or, for two others, there
    ///         is no library at all. Rendering the raw value into both produced a
    ///         <c>
    /// CREATE
    ///         EXTENSION pgvector
    ///         </c> that fails, and asked the postmaster to preload <c>pgvector</c>
    ///         and <c>postgis</c>, neither of which is a library. <c>conformance.yaml § owed</c>,
    ///         <c>extension-names-are-not-library-names</c>.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A failed preload is a cluster that never starts, not an extension that is
    ///             missing.
    ///         </b> <c>shared_preload_libraries</c> is read by the postmaster before it accepts
    ///         a connection, so a name with no library behind it fails startup for every database on
    ///         the instance rather than for the one feature that wanted it.
    ///     </para>
    ///     <para>
    ///         Each row was read at the version this chart's <c>/properties/version</c> offers, on
    ///         2026-08-18:
    ///         <list type="bullet">
    ///             <item>
    ///                 <c>pgvector</c> installs as <c>vector</c> — the project's own README says
    ///                 <c>CREATE EXTENSION vector</c> — and names no preload requirement.
    ///             </item>
    ///             <item><c>postgis</c> installs as <c>postgis</c> and names no preload requirement.</item>
    ///             <item>
    ///                 <c>pg_stat_statements</c> is a contrib module whose documentation says it
    ///                 <i>
    ///                     "must be loaded by adding pg_stat_statements to shared_preload_libraries …
    ///                     because it requires additional shared memory"
    ///                 </i>, and the library is spelled
    ///                 the same as the extension.
    ///             </item>
    ///             <item>
    ///                 <c>timescaledb</c> refuses to install unpreloaded:
    ///                 <c>extension_load_without_preload</c> in <c>src/extension_utils.c</c> raises
    ///                 <i>"extension \"timescaledb\" must be preloaded"</i> with the hint
    ///                 <i>"Please preload the timescaledb library via shared_preload_libraries"</i>.
    ///             </item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the same shape as <see cref="Presets" /> and it has the same second copy:</b>
    ///         <c>charts/managed/postgres/templates/cluster.yaml</c> carries the table too, because no
    ///         emitter reads a Helm template. <c>ChartRegistryPairTests.TheExtensionCatalogueIsTheChartsExtensionCatalogue</c>
    ///         diffs them row for row, which is the check the placement defect did not have.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string ExtensionName, string PreloadLibrary)> ExtensionCatalogue { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal) {
            ["pgvector"] = ("vector", ""),
            ["postgis"] = ("postgis", ""),
            ["pg_stat_statements"] = ("pg_stat_statements", "pg_stat_statements"),
            ["timescaledb"] = ("timescaledb", "timescaledb")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    // ── Addressing ────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>Cluster</c> a server owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ClusterRef(string ns, string name) =>
        new() { Kind = ClusterKind, Namespace = ns, Name = name };

    /// <summary>The <c>Pooler</c> a server owns while pooling is on.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ The suffix matches <c>templates/pooler.yaml</c>'s <c>{{ include "postgres.name" . }}-pooler</c>.
    ///     Two spellings of one object's name is an object the reconciler creates and never finds again.
    /// </remarks>
    public static ObjectRef PoolerRef(string ns, string name) =>
        new() { Kind = PoolerKind, Namespace = ns, Name = PoolerName(name) };

    /// <summary>The pooler object's name.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string PoolerName(string name) => name + "-pooler";

    /// <summary>The name of the basic-auth <c>Secret</c> CloudNativePG writes the owner's password into.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ CloudNativePG's own default — <c>Cluster.GetApplicationSecretName()</c> is
    ///     <c>{cluster}-app</c> when <c>initdb.secret</c> is unset — and <c>templates/cluster.yaml</c>'s
    ///     <c>{{ include "postgres.name" . }}-app</c>. <see cref="ClusterJson" /> deliberately does
    ///     NOT name it: a named secret is one the operator expects to find, an unnamed one is one it
    ///     writes. <c>PostgresServerListKeysHandler</c> reads it by this name.
    /// </remarks>
    public static string CredentialSecretName(string name) => name + "-app";

    /// <summary>The keys CloudNativePG files the owner's credential under.</summary>
    /// <remarks>
    ///     ⚠ The <c>Secret</c> is <c>kubernetes.io/basic-auth</c>, so the two names are the type's
    ///     rather than this platform's choice. It also carries <c>uri</c>, <c>jdbc-uri</c>,
    ///     <c>host</c>, <c>port</c> and <c>dbname</c>; <c>listKeys</c> returns none of those, because
    ///     the ones this platform can compute from the resource's own address are better computed —
    ///     see <see cref="Host" />, whose answer depends on the pooler and the secret's does not.
    /// </remarks>
    public const string UsernameKey = "username";

    /// <inheritdoc cref="UsernameKey" />
    public const string PasswordKey = "password";

    /// <summary>The TCP port every PostgreSQL endpoint this platform hands out listens on.</summary>
    /// <remarks>
    ///     ⚠ The same number for the pooler and for the read-write service. PgBouncer is a
    ///     transparent front end and the <c>Pooler</c>'s service publishes the wire protocol's own
    ///     port, so a client that turned pooling off would change host and not port.
    /// </remarks>
    public const int Port = 5432;

    /// <summary>The in-cluster DNS name a client connects to.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body, for whether pooling is on.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The pooler's when pooling is on, and that is a visible consequence rather than an
    ///         implementation detail.
    ///     </b> <see cref="ListKeysResponse" />'s <c>/host</c> says so in the
    ///     document a tenant reads: turning pooling off later changes this value, so a connection
    ///     string built once and stored is one that stops working — which is worth knowing before
    ///     rather than after. The read-write service is CloudNativePG's <c>{cluster}-rw</c>, which
    ///     always exists; the pooler's service is <see cref="PoolerName" />, which exists only while
    ///     the <c>Pooler</c> does.
    /// </remarks>
    public static string Host(string name, JsonElement desired) =>
        PoolingEnabled(desired) ? PoolerName(name) : name + "-rw";

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>Whether the desired body asks for a pooler.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The declared value, or the schema default (<c>true</c>) when absent. ⚠ The default is
    ///     applied <i>here</i> rather than by the write path, which stores the body as sent —
    ///     <see cref="SchemaProperty.DefaultJson" />'s own remarks say the validator does not
    ///     substitute, so a reconciler that read a missing property as <c>false</c> would turn every
    ///     body that omitted it into a server with no pooler.
    /// </returns>
    public static bool PoolingEnabled(JsonElement desired) => Flag(desired, "pooling", "enabled", true);

    /// <summary>Whether the desired body asks for backups.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool BackupEnabled(JsonElement desired) => Flag(desired, "backup", "enabled", true);

    /// <summary>The object-store URL backups go to, or the empty string when the body names none.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string BackupDestination(JsonElement desired) =>
        Text(desired, "backup", "destinationPath", string.Empty);

    /// <summary>
    ///     Why the desired body cannot be rendered into a <c>Cluster</c> the operator's definition
    ///     admits, or <see langword="null" /> when it can.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The sentence the operation fails with when backups are on and no destination is named —
    ///     the reconciler reports it as <c>InvalidRequestBody</c> at
    ///     <see cref="BackupDestinationPointer" /> — or <see langword="null" /> when the body renders.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Found by issue #91, and it was every default-bodied server.</b> <c>backup.enabled</c>
    ///     defaults to <c>true</c> and <c>destinationPath</c> to <c>""</c>, and
    ///     <see cref="ClusterJson" /> rendered the pair as
    ///     <c>spec.backup.barmanObjectStore.destinationPath: ""</c> — which
    ///     <c>charts/bundle/cloudnative-pg/crds/clusters.postgresql.cnpg.io.yaml</c> refuses with
    ///     <c>minLength: 1</c>. FakeKubeCluster echoed it, so twelve conformance assertions were
    ///     green over a Cluster no API server would have stored. The check lives here, before the
    ///     apply, so the refusal names the tenant's own property and not the operator's field, and
    ///     so the message says what to do: name a bucket, or turn backups off. Terminal rather than
    ///     retryable, because the body will say the same thing on every pass.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             #30 turned the refusal round: the empty destination is now the platform's store and
    ///             a named one is what is refused.
    ///         </b> The half #91 left — <c>the-default-bucket-is-not-filled-in</c> — was that a
    ///         destination alone never rescued the body: CloudNativePG 1.30.0's webhook answers
    ///         <i>"missing credentials. One and only one of azureCredentials, s3Credentials and
    ///         googleCredentials are required"</i>, and this api-version has no property that could
    ///         carry a credential without putting it in the body. The platform's store has one — a key
    ///         <see cref="ObjectStoreCredentials" /> issues and the vault holds — so an empty
    ///         destination renders a complete <c>barmanObjectStore</c>, and a named one, which could
    ///         only ever have been refused by the operator after the caller was told 202, is refused
    ///         here, before anything is applied.
    ///     </para>
    /// </remarks>
    public static string? BackupDestinationProblem(JsonElement desired) =>
        BackupEnabled(desired) && BackupDestination(desired).Length > 0
            ? "backup.destinationPath names a destination of its own, and this api-version has nowhere "
            + "to carry the credentials CloudNativePG needs to write there — its admission webhook "
            + "refuses a barmanObjectStore with none. Leave "
            + BackupDestinationPointer
            + " empty: backups then go to the platform's object store, in a bucket of this server's own, "
            + "with a key the platform issues and holds. Or set /properties/backup/enabled to false."
            : null;

    /// <summary>The property <see cref="BackupDestinationProblem" /> is reported against.</summary>
    public const string BackupDestinationPointer = "/properties/backup/destinationPath";

    /// <summary>
    ///     Why the desired body cannot be rendered into a <c>Pooler</c> the operator's definition
    ///     admits, or <see langword="null" /> when it can.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The sentence the operation fails with when pooling is on and the mode is
    ///     <c>statement</c> — the reconciler reports it as <c>InvalidRequestBody</c> at
    ///     <see cref="PoolingModePointer" /> — or <see langword="null" /> when the body renders.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Found by the review of issue #91, on the first run that varied a property.</b> The
    ///     2026-08-01 schema publishes three pooling modes — PgBouncer's three — and
    ///     <c>charts/bundle/cloudnative-pg/crds/poolers.postgresql.cnpg.io.yaml</c> declares
    ///     <c>spec.pgbouncer.poolMode</c> as an enum of <c>session</c> and <c>transaction</c>. So
    ///     <c>statement</c> was a value the API accepted and no operator could honour: the Cluster
    ///     applied, the Pooler was refused with the operator's field named, and the operation failed
    ///     after the tenant was told 202. The value cannot leave the schema — an api-version is
    ///     immutable, and <c>OpenApiCompatibility.EnumValueRemoved</c> says so — so the check lives
    ///     here, before the apply, with the tenant's own property and the two values that work.
    ///     Terminal, because the body says the same thing on every pass. Only when pooling is on:
    ///     with it off no Pooler is rendered and the mode reaches nothing.
    /// </remarks>
    public static string? PoolingModeProblem(JsonElement desired) =>
        PoolingEnabled(desired) && string.Equals(PoolingMode(desired), "statement", StringComparison.Ordinal)
            ? """pooling.enabled is true and pooling.mode is "statement". CloudNativePG's Pooler accepts """
            + """only "session" and "transaction" for spec.pgbouncer.poolMode, so the operator's """
            + "definition refuses the object and PgBouncer's statement pooling is not reachable through "
            + "it. Set "
            + PoolingModePointer
            + """ to "transaction" or "session", or set """
            + "/properties/pooling/enabled to false."
            : null;

    /// <summary>The property <see cref="PoolingModeProblem" /> is reported against.</summary>
    public const string PoolingModePointer = "/properties/pooling/mode";

    /// <summary>The PgBouncer pool mode the body asks for, <c>transaction</c> when it names none.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PoolingMode(JsonElement desired) => Text(desired, "pooling", "mode", "transaction");

    /// <summary>The application database <c>bootstrap</c> creates.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ Public because two callers need the same answer and used to read it separately:
    ///     <see cref="ClusterJson" /> renders it into the <c>Cluster</c>, and <c>listKeys</c> returns
    ///     it. The schema default is applied here for the reason <see cref="PoolingEnabled" /> gives.
    /// </remarks>
    public static string Database(JsonElement desired) => Text(desired, "bootstrap", "database", "app");

    /// <summary>The role that owns <see cref="Database" />.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Owner(JsonElement desired) => Text(desired, "bootstrap", "owner", "app");

    /// <summary>
    ///     The recovery point a server is created from, or the empty string for a new, empty one.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RecoveryPoint(JsonElement desired) => Text(desired, "restore", "recoveryPoint", string.Empty);

    /// <summary>
    ///     The <c>Cluster</c> document a desired body becomes, ready for server-side apply.
    /// </summary>
    /// <param name="name">The object's <c>metadata.name</c> — the resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The JSON <c>templates/cluster.yaml</c> renders, for the same values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. Those are ADR-013's seven mandatory
    ///         labels and two annotations, and <c>KubeCommand</c> injects them non-overridably —
    ///         docs/plan/09 § The command builder. A provider that set them itself would be a provider
    ///         that could get them wrong.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The owner's password appears here neither as a value nor as a reference, and the
    ///             <c>Secret</c> that ends up holding it is written by the operator.
    ///         </b> CNPG's
    ///         <c>bootstrap.initdb.secret.name</c> is the seam a platform that minted the credential
    ///         would take: the operator reads the password out of a <c>Secret</c> in the namespace, so
    ///         the only component that ever holds the plaintext is whatever writes that
    ///         <c>Secret</c> — docs/plan/12 § The pattern, once, piece 5, "credential provisioning
    ///         into the tenant's Vault". ⚠ Taking the seam means WRITING the Secret as well as naming
    ///         it; this renderer named it and wrote nothing until 2026-09-17, and the paragraph inside
    ///         <see cref="ClusterJson" /> records what that cost.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Piece 5 is built and this row still declines to use it.</b> <c>ISecretWriter</c>
    ///         is the interface and <c>CyberCloud.Vault</c> ships <c>OpenBaoSecretWriter</c>, so the
    ///         "until it does" this paragraph used to end on has passed. Leaving
    ///         <c>bootstrap.initdb.secret</c> unrendered is now the deliberate choice: CloudNativePG
    ///         generates its own password into <c>{cluster}-app</c> before the cluster reports ready,
    ///         and a credential minted after that is one the server never accepted.
    ///         <c>PostgresServerListKeysHandler</c> reads the operator's Secret, so the gap this
    ///         paragraph recorded — a working database whose credentials <c>listKeys</c> cannot hand
    ///         out — is closed.
    ///     </para>
    /// </remarks>
    /// <param name="store">
    ///     Where backups go — the platform's store, with the key's <c>Secret</c> — or
    ///     <see langword="null" /> to render no backup section. ⚠ The reconciler always passes one
    ///     while backups are on; <see langword="null" /> is for a teardown's pause, where the object
    ///     is on its way out and nothing reads its backup section again.
    /// </param>
    public static string ClusterJson(string name, JsonElement desired, BackupStore? store = null) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (cpu, memory) = Resources(desired);
        var extensionNames = ExtensionNames(desired);
        var libraries = PreloadLibraries(desired);

        // ⚠ `shared_preload_libraries` is a SIBLING of `parameters`, not a key inside it, and it is a
        // LIST rather than a comma-joined string. api/v1/cluster_types.go declares
        // `AdditionalLibraries []string `json:"shared_preload_libraries,omitempty"`` on
        // PostgresConfiguration, beside `Parameters map[string]string `json:"parameters,omitempty"``.
        //
        // ⚠ The other spelling is not a style difference, it is a 422 on every create. The key is in
        // pkg/postgres/configuration.go's FixedConfigurationParameters, and
        // internal/webhook/v1/cluster_webhook.go walks spec.postgresql.parameters and answers a fixed
        // key with "Can't set fixed configuration parameter" unless the value equals CloudNativePG's
        // own sanitized one. The webhook builds its ConfigurationInfo without
        // IncludingSharedPreloadLibraries, so the sanitized value stays at the default settings'
        // empty string and every non-empty list this renderer could produce differs from it. See
        // charts/managed/postgres/conformance.yaml § owed, `shared-preload-libraries-is-not-a-parameter`.
        //
        // ⚠ AND THE LIST IS NOT THE ALLOW-LIST. A library name is not an extension name — see
        // `ExtensionCatalogue`. `pgvector` and `postgis` need no preload entry at all, so a body
        // asking only for those renders no `shared_preload_libraries` key; `timescaledb` and
        // `pg_stat_statements` do, under those exact names.
        var parameters = new JsonObject { ["max_connections"] = "200" };
        var postgresql = new JsonObject();

        if (libraries.Length > 0) {
            var declared = new JsonArray();
            foreach (var library in libraries) {
                declared.Add(library);
            }

            postgresql["shared_preload_libraries"] = declared;
        }

        postgresql["parameters"] = parameters;

        // ⚠ `spec.postgresql.synchronous`, INSIDE the postgresql block — not a `postgresql_synchronous`
        // sibling of it. api/v1/cluster_types.go declares `Synchronous *SynchronousReplicaConfiguration
        // `json:"synchronous,omitempty"`` on PostgresConfiguration, beside `parameters`, and the
        // committed definition (charts/bundle/cloudnative-pg/crds/clusters.postgresql.cnpg.io.yaml)
        // declares `method` and `number` there and nothing under spec by the other name. The other
        // spelling was a typed-patch failure — ".spec.postgresql_synchronous: field not declared in
        // schema" — on every server created with synchronous replication, and it stayed green because
        // every suite converged the default body, where the flag is off. The review of issue #91 found
        // it; ProviderConformanceTests.EveryPropertyVariantTheSchemaAdmitsRendersAShapeTheDefinitionAdmits
        // renders the flag's other value against the definition now.
        //
        // ⚠ Absent rather than a `false`-valued block when replication is asynchronous. The definition
        // has no "asynchronous" member — asynchronous IS the absence of `synchronous` — and an empty
        // block written anyway would be a field this field manager owns forever under server-side
        // apply, which is a field the tenant's own controller could then never set.
        if (Flag(desired, "synchronousReplication", false)) {
            postgresql["synchronous"] = new JsonObject { ["method"] = "any", ["number"] = 1 };
        }

        // ⚠ NO `secret` HERE, AND UNTIL 2026-09-17 THERE WAS ONE — `{ "name": CredentialSecretName(name) }`
        // — WHICH IS WHY NO MANAGED DATABASE HAD EVER STARTED. The remarks above already said the
        // seam is left unrendered so that CloudNativePG generates the password; the code named the
        // Secret anyway, and the two had never met an operator. Read against v1.30.0:
        // `Cluster.ShouldInitDBCreateApplicationSecret` is true only while `initdb.secret` is nil or
        // its name is empty (api/v1/cluster_funcs.go), so a NAMED secret is one the operator expects
        // somebody else to have written. Nothing here writes it. The initdb Job then mounts it, the
        // kubelet answers `CreateContainerConfigError: secret "<name>-app" not found`, and the
        // primary never runs — measured on the first run of test/CyberCloud.Bundle.Cluster.Conformance
        // § M1StoryOnAFreshCluster, the first test in this repository with the operator installed.
        // Left absent, the operator writes `{name}-app` itself with the same two keys
        // PostgresServerListKeysHandler reads. The name stays public because the handler still
        // needs it; the renderer must never mention it.
        var initdb = new JsonObject { ["database"] = Database(desired), ["owner"] = Owner(desired) };

        // ⚠ The EXTENSION name, which for pgvector is `vector`. `CREATE EXTENSION pgvector` fails —
        // there is no control file by that name — and the failure lands in the bootstrap job rather
        // than on the resource, so the server comes up without the feature the tenant asked for.
        if (extensionNames.Length > 0) {
            var statements = new JsonArray();
            foreach (var extension in extensionNames) {
                statements.Add("CREATE EXTENSION IF NOT EXISTS " + extension + ";");
            }

            initdb["postInitApplicationSQL"] = statements;
        }

        var storage = new JsonObject { ["size"] = Text(desired, "storage", "size", "20Gi") };
        var storageClass = Text(desired, "storage", "class", string.Empty);

        if (storageClass.Length > 0) {
            storage["storageClass"] = storageClass;
        }

        // ⚠ A RESTORE BOOTSTRAPS FROM A BACKUP OBJECT BESIDE IT, AND NOTHING ELSE ABOUT THE SERVER
        // CHANGES. `bootstrap.recovery.backup.name` names a CloudNativePG Backup in the same namespace;
        // the operator reads the destination, the endpoint and the credential Secret off that Backup's
        // status — the SOURCE server's — so the restore needs no store of its own to read from, and
        // this server's own backup section below points at its own, empty, bucket. `database` and
        // `owner` are what the operator writes the `{name}-app` Secret for, so listKeys answers for a
        // restored server exactly as for a new one. The extensions are not re-created: they came back
        // with the data.
        var restoreFrom = RecoveryPoint(desired);
        var bootstrap = restoreFrom.Length > 0
            ? new JsonObject {
                ["recovery"] = new JsonObject {
                    ["backup"] = new JsonObject { ["name"] = restoreFrom },
                    ["database"] = Database(desired),
                    ["owner"] = Owner(desired)
                }
            }
            : new JsonObject { ["initdb"] = initdb };

        var spec = new JsonObject {
            ["instances"] = Number(desired, "replicas", 2),
            ["imageName"] = "ghcr.io/cloudnative-pg/postgresql:" + Version(desired),
            ["postgresql"] = postgresql,
            ["bootstrap"] = bootstrap,
            ["storage"] = storage,
            ["monitoring"] = new JsonObject { ["enablePodMonitor"] = Flag(desired, "monitoring", "enabled", true) }
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            spec["resources"] = new JsonObject { ["requests"] = quantities.DeepClone(), ["limits"] = quantities };
        }

        var walSize = Text(desired, "storage", "walSize", string.Empty);
        if (walSize.Length > 0) {
            var wal = new JsonObject { ["size"] = walSize };
            if (storageClass.Length > 0) {
                wal["storageClass"] = storageClass;
            }

            spec["walStorage"] = wal;
        }

        // ⚠ `barmanObjectStore`, CloudNativePG 1.30.0's in-tree archiver, and not the Barman Cloud
        // plugin the operator's own warning points at. The bundle pins 1.30.0, which still serves the
        // in-tree path — the warning says it goes in 1.31.0 — and the plugin is a second component
        // (a Deployment, its own ObjectStore CRD, cert-manager for its TLS) that the bundle does not
        // install. RecoveryVaults.BackupMethod names the same method for the vault's schedules, and
        // the move is one change to both, owed in charts/managed/postgres/conformance.yaml § owed,
        // `the-in-tree-archiver-goes-in-1-31`.
        if (BackupEnabled(desired) && store is not null) {
            spec["backup"] = new JsonObject {
                ["retentionPolicy"] =
                    Number(desired, "backup", "retentionDays", 14).ToString(CultureInfo.InvariantCulture) + "d",
                ["barmanObjectStore"] = new JsonObject {
                    ["destinationPath"] = store.DestinationPath,
                    ["endpointURL"] = store.EndpointUrl,
                    ["s3Credentials"] = new JsonObject {
                        ["accessKeyId"] = new JsonObject { ["name"] = store.SecretName, ["key"] = AccessKeyIdKey },
                        ["secretAccessKey"] = new JsonObject { ["name"] = store.SecretName, ["key"] = SecretAccessKeyKey }
                    },
                    ["wal"] = new JsonObject { ["compression"] = "gzip" }
                }
            };
        }

        return new JsonObject { ["metadata"] = new JsonObject { ["name"] = name }, ["spec"] = spec }
            .ToJsonString();
    }

    /// <summary>The <c>Pooler</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name — the pooler is named after it.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string PoolerJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = PoolerName(name) },
            ["spec"] = new JsonObject {
                ["cluster"] = new JsonObject { ["name"] = name },
                ["instances"] = Number(desired, "pooling", "instances", 2),
                ["type"] = "rw",
                ["pgbouncer"] = new JsonObject { ["poolMode"] = PoolingMode(desired) }
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns>
    ///     <c>true</c> when the fields this provider owns hold the desired values. ⚠ Subset, not
    ///     equality: server-side apply leaves other managers' fields in place and CloudNativePG writes
    ///     a large <c>status</c> of its own, so demanding an exact match would make the operator's own
    ///     bookkeeping look like drift and re-apply on every pass.
    /// </returns>
    /// <remarks>
    ///     ⚠ Dispatches on the object's <c>kind</c> rather than taking it as a parameter, because a
    ///     conformance case supplies this as one function over every object the resource owns.
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document || document["spec"] is not JsonObject spec) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "Pooler" => MatchesPooler(spec, desired),
            _ => MatchesCluster(spec, desired)
        };
    }

    static bool MatchesCluster(JsonObject spec, JsonElement desired) =>
        spec["instances"]?.GetValue<int>() == Number(desired, "replicas", 2)
        && spec["imageName"]?.GetValue<string>()?.EndsWith(':' + Version(desired), StringComparison.Ordinal) == true
        && (spec["storage"] as JsonObject)?["size"]?.GetValue<string>() == Text(desired, "storage", "size", "20Gi")
        && (spec["monitoring"] as JsonObject)?["enablePodMonitor"]?.GetValue<bool>()
        == Flag(desired, "monitoring", "enabled", true)
        && MatchesBootstrap(spec["bootstrap"] as JsonObject, desired)
        // ⚠ A server with backups on is not converged until its Cluster archives somewhere: the store
        // is the reconciler's to choose, so the destination is checked for presence, not for value.
        && (!BackupEnabled(desired)
            || (((spec["backup"] as JsonObject)?["barmanObjectStore"] as JsonObject)?["destinationPath"]?.GetValue<string>())
            is { Length: > 0 });

    /// <summary>Whether the stored bootstrap is the one the body asks for — a restore's or an initdb's.</summary>
    static bool MatchesBootstrap(JsonObject? bootstrap, JsonElement desired) {
        var restoreFrom = RecoveryPoint(desired);

        return restoreFrom.Length > 0
            ? ((bootstrap?["recovery"] as JsonObject)?["backup"] as JsonObject)?["name"]?.GetValue<string>() == restoreFrom
            : (bootstrap?["initdb"] as JsonObject)?["database"]?.GetValue<string>() == Database(desired);
    }

    static bool MatchesPooler(JsonObject spec, JsonElement desired) =>
        spec["instances"]?.GetValue<int>() == Number(desired, "pooling", "instances", 2)
        && (spec["pgbouncer"] as JsonObject)?["poolMode"]?.GetValue<string>()
        == PoolingMode(desired);

    /// <summary>
    ///     The CPU and memory a body asks for: the explicit quantities when both are given, otherwise
    ///     the preset's.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     Both quantities, or both empty when neither the preset nor an override supplies them —
    ///     which renders no <c>resources</c> block at all rather than a half-specified one, matching
    ///     <c>_helpers.tpl</c>'s <c>if and $cpu $memory</c>.
    /// </returns>
    public static (string Cpu, string Memory) Resources(JsonElement desired) {
        var preset = Text(desired, "sizing", "preset", "s1.small");
        var fallback = Presets.TryGetValue(preset, out var found) ? found : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    /// <summary>How many instances a body asks for, including the primary.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The declared value, or the schema default (<c>2</c>) when absent.</returns>
    /// <remarks>
    ///     ⚠ <b>Public because quota multiplies by it, and that is not a detail.</b> CloudNativePG runs
    ///     <c>spec.instances</c> pods and each one carries the whole <c>spec.resources</c> block and its
    ///     own data PVC — <see cref="ClusterJson" /> writes both once because the CR is per-cluster, not
    ///     because the cost is. A meter that reserved the per-instance figure would under-reserve a
    ///     two-instance server, which is the default, by half.
    /// </remarks>
    public static int Replicas(JsonElement desired) => Number(desired, "replicas", 2);

    /// <summary>The data volume size a body asks for, <b>per instance</b>.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The declared quantity, or the schema default (<c>20Gi</c>) when absent.</returns>
    public static string StorageSize(JsonElement desired) => Text(desired, "storage", "size", "20Gi");

    /// <summary>
    ///     The separate write-ahead-log volume size, per instance, or <c>""</c> when the WAL shares the
    ///     data volume.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <c>""</c> is not zero storage — it means one volume rather than two, and the data volume's
    ///     size already counts. <see cref="ClusterJson" /> reads it the same way.
    /// </remarks>
    public static string WalSize(JsonElement desired) => Text(desired, "storage", "walSize", string.Empty);

    /// <summary>The extensions a body asks for, in the order it listed them.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> Extensions(JsonElement desired) {
        if (Root(desired, "extensions") is not { ValueKind: JsonValueKind.Array } array) {
            return [];
        }

        var found = ImmutableArray.CreateBuilder<string>();
        foreach (var element in array.EnumerateArray()) {
            if (element.ValueKind is JsonValueKind.String && element.GetString() is { Length: > 0 } text) {
                found.Add(text);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>
    ///     The extension names <c>CREATE EXTENSION</c> installs, one per value the body listed and in
    ///     that order.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ A value with no row in <see cref="ExtensionCatalogue" /> renders as itself. The schema's
    ///     <c>AllowedValues</c> makes that unreachable through the API — and a renderer that dropped
    ///     the value instead would turn a catalogue row somebody forgot to add into an extension that
    ///     silently never installs, which is the failure this whole table exists to end.
    /// </remarks>
    public static ImmutableArray<string> ExtensionNames(JsonElement desired) {
        var found = ImmutableArray.CreateBuilder<string>();
        foreach (var extension in Extensions(desired)) {
            found.Add(ExtensionCatalogue.TryGetValue(extension, out var row) ? row.ExtensionName : extension);
        }

        return found.ToImmutable();
    }

    /// <summary>
    ///     The libraries <c>spec.postgresql.shared_preload_libraries</c> must carry for the extensions
    ///     a body asks for, in the order the body listed them and without repeats.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     Empty when nothing the body asked for needs preloading — which is the case for a body that
    ///     names only <c>pgvector</c>, <c>postgis</c>, or neither. ⚠ Empty means the key is not
    ///     rendered at all rather than rendered empty, because <c>shared_preload_libraries</c> is a
    ///     field this provider owns under server-side apply for as long as it writes it.
    /// </returns>
    public static ImmutableArray<string> PreloadLibraries(JsonElement desired) {
        var found = ImmutableArray.CreateBuilder<string>();
        foreach (var extension in Extensions(desired)) {
            if (ExtensionCatalogue.TryGetValue(extension, out var row)
                && row.PreloadLibrary.Length > 0
                && !found.Contains(row.PreloadLibrary)) {
                found.Add(row.PreloadLibrary);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>The major version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? "17"
            : "17";

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the server in.</param>
    /// <param name="replicas">How many instances.</param>
    /// <param name="storageSize">The data volume size.</param>
    /// <param name="pooling">Whether to run a pooler.</param>
    /// <param name="location">The region.</param>
    /// <param name="backupDestination">
    ///     Where backups go. Empty — the default — is the platform's store; a named destination is
    ///     what <see cref="BackupDestinationProblem" /> refuses, the shape a test reaches for to see that
    ///     refusal.
    /// </param>
    /// <param name="recoveryPoint">
    ///     The recovery point to bootstrap from, or empty for a new database. Written only when
    ///     non-empty, so every body built before #30 reads back unchanged.
    /// </param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int replicas = 2,
        string storageSize = "20Gi",
        bool pooling = true,
        string location = "eu-central",
        string backupDestination = "",
        string recoveryPoint = ""
    ) {
        var properties = new JsonObject {
            ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
            ["version"] = "17",
            ["replicas"] = replicas,
            ["storage"] = new JsonObject { ["size"] = storageSize },
            ["pooling"] = new JsonObject { ["enabled"] = pooling, ["instances"] = 2 },
            ["bootstrap"] = new JsonObject { ["database"] = "app", ["owner"] = "app" },
            // ⚠ Written even when empty, so a test that names a destination and one that does not
            // build the same shape. Empty is the platform's store since #30.
            ["backup"] = new JsonObject { ["destinationPath"] = backupDestination }
        };

        if (recoveryPoint.Length > 0) {
            properties["restore"] = new JsonObject { ["recoveryPoint"] = recoveryPoint };
        }

        return new JsonObject { ["location"] = location, ["properties"] = properties }.ToJsonString();
    }

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
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var found)
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

    static bool Flag(JsonElement desired, string name, bool fallback) =>
        Root(desired, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
