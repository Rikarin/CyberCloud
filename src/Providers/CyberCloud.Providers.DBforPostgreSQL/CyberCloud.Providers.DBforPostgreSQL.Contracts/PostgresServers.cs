using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
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
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair, and
///         <c>charts/managed/postgres/values.yaml</c> is the other half.</b> ADR-010 § Which end
///         authors the schema, DECIDED 2026-08-11: <i>"The C# <c>ResourceSchema</c> is authored. The
///         chart's <c>@param</c> annotations are generated from it and diffed."</i> Every property
///         below whose pointer begins <c>/properties/</c> and is not <see cref="ClusterIdPointer" />
///         has a <c>@param</c> row in that file at the same pointer — the chart already emits
///         <c>x-cybercloud-pointer</c>, so the correspondence is checkable by eye today and by a
///         generator when one exists.
///     </para>
///     <para>
///         ⚠ <b>Two properties here are deliberately <i>not</i> chart rows, and that is a third
///         category ADR-010 does not name.</b> That ADR splits the chart into 26 API rows and 10
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
///         ⚠ <b><c>/properties/bootstrap/password</c> is absent on purpose and the chart row moved to
///         <c>@internal</c> to match.</b> Its own chart description already said the reconciler
///         supplies it — <i>"Provisioned into the tenant's Vault and read back by ISecretResolver at
///         render time"</i> — so it was never a tenant setting. Declaring it as a body property would
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
    ///     ⚠ <b>A registry declaration, not a render path, and the two are different mechanisms with
    ///     the same word on them.</b> <see cref="IResourceTypeBuilder.Chart" /> records which chart
    ///     describes this type; <c>IKubeCommandBuilder.Chart</c> asks
    ///     <c>CyberCloud.Kubernetes.Charts</c> to render one, and that assembly does not exist — the
    ///     builder answers with a named failure saying so. So the reconciler renders the same two
    ///     objects the chart's templates render, in C#, and this constant is what ties the two halves
    ///     together until the renderer lands.
    /// </remarks>
    public const string ChartName = "managed/postgres";

    /// <summary>The field manager the apply runs under — ADR-013's stable per-provider name.</summary>
    public const string FieldManager = "cybercloud/cybercloud.dbforpostgresql";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the connection credentials.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions, Credentials: <i>"<c>listKeys</c> is an action with
    ///     its own permission, audited on every call"</i>. ⚠ <c>regenerateKeys</c> is named in the same
    ///     paragraph and is <b>not</b> declared, because it is specified with <i>"a rolling grace
    ///     period so rotation is not an outage"</i> and nothing in the platform can hold two live
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
    ///     default, which is how the platform spells "fill it in from the tenant's default bucket".
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
                    Required: true,
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
                    Required: true,
                    Description: "The cluster whose namespace holds the CloudNativePG objects."
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
                    Description: "Major PostgreSQL version. Minor upgrades are applied automatically "
                    + "in the maintenance window."
                ) {
                    AllowedValues = ["16", "17", "18"],
                    DefaultJson = "\"17\""
                },
                new(
                    "/properties/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of instances, including the primary. One is a single point of "
                    + "failure and is offered for development only."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "2"
                },
                new(
                    "/properties/synchronousReplication",
                    SchemaKind.Boolean,
                    Description: "Whether commits wait for a replica. Costs write latency and removes "
                    + "the window in which a failover loses transactions."
                ) {
                    DefaultJson = "false"
                },
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
                new("/properties/storage", SchemaKind.Nested, Description: "The data volume."),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Data volume size in Kubernetes quantity form. Grows online; never "
                    + "shrinks."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"20Gi\"",
                    ExampleJson = "\"20Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name. Empty means the cluster default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/storage/walSize",
                    SchemaKind.Text,
                    Description: "Size of the separate write-ahead-log volume. Empty means the WAL "
                    + "shares the data volume."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
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
                ) {
                    DefaultJson = "true"
                },
                new(
                    "/properties/pooling/mode",
                    SchemaKind.Text,
                    Description: "PgBouncer pooling mode. Transaction pooling is the useful one and "
                    + "breaks session-scoped features such as prepared statements and advisory locks."
                ) {
                    AllowedValues = ["session", "transaction", "statement"],
                    DefaultJson = "\"transaction\""
                },
                new(
                    "/properties/pooling/instances",
                    SchemaKind.WholeNumber,
                    Description: "Number of pooler pods."
                ) {
                    Minimum = 1,
                    Maximum = 8,
                    DefaultJson = "2"
                },
                new(
                    "/properties/extensions",
                    SchemaKind.Array,
                    Description: "Extensions to install, from the platform allow-list. An "
                    + "arbitrary-extension escape hatch is a code-execution surface and is not offered."
                ) {
                    ElementKind = SchemaKind.Text,
                    AllowedValues = ["pgvector", "postgis", "pg_stat_statements", "timescaledb"],
                    DefaultJson = "[]",
                    ExampleJson = "[\"pgvector\"]"
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
                ) {
                    DefaultJson = "true"
                },
                new(
                    "/properties/backup/retentionDays",
                    SchemaKind.WholeNumber,
                    Description: "How long base backups and WAL are kept. The "
                    + "point-in-time-recovery window is this number of days."
                ) {
                    Minimum = 1,
                    Maximum = 365,
                    DefaultJson = "14"
                },
                new(
                    "/properties/backup/destinationPath",
                    SchemaKind.Text,
                    Description: "Object-store URL for base backups and WAL, for example "
                    + "s3://tenant-bucket/postgres. Empty means the platform fills it in from the "
                    + "tenant's default bucket."
                ) {
                    Pattern = BackupDestinationPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"s3://tenant-bucket/postgres\""
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
                    Pattern = IdentifierPattern,
                    MinLength = 1,
                    MaxLength = MaxIdentifierLength,
                    DefaultJson = "\"app\""
                },
                new(
                    "/properties/bootstrap/owner",
                    SchemaKind.Text,
                    Description: "Role that owns the application database."
                ) {
                    Pattern = IdentifierPattern,
                    MinLength = 1,
                    MaxLength = MaxIdentifierLength,
                    DefaultJson = "\"app\""
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
    ///     part of the API surface with no contract</b> — the reason
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
                    Required: true,
                    Description: "The in-cluster DNS name to connect to. ⚠ The pooler's when pooling "
                    + "is on, so turning pooling off later is a visible change to this value rather "
                    + "than a silent one."
                ),
                new("/port", SchemaKind.WholeNumber, Required: true, Description: "The TCP port.") {
                    Minimum = 1,
                    Maximum = 65535
                },
                new("/database", SchemaKind.Text, Required: true, Description: "The application database."),
                new("/username", SchemaKind.Text, Required: true, Description: "The owning role."),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The owning role's password, read from the tenant's Vault for this "
                    + "call only."
                )
            ]
        );

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, s1 family.</summary>
    /// <remarks>
    ///     ⚠ <b>This table is a second copy of <c>charts/managed/postgres/templates/_helpers.tpl</c>'s
    ///     <c>postgres.resources</c>, and the duplication is the cost of having no chart renderer.</b>
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
    ///         is no library at all. Rendering the raw value into both produced a <c>CREATE
    ///         EXTENSION pgvector</c> that fails, and asked the postmaster to preload <c>pgvector</c>
    ///         and <c>postgis</c>, neither of which is a library. <c>conformance.yaml § owed</c>,
    ///         <c>extension-names-are-not-library-names</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A failed preload is a cluster that never starts, not an extension that is
    ///         missing.</b> <c>shared_preload_libraries</c> is read by the postmaster before it accepts
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
    ///                 <i>"must be loaded by adding pg_stat_statements to shared_preload_libraries …
    ///                 because it requires additional shared memory"</i>, and the library is spelled
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
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

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

    /// <summary>The name of the basic-auth <c>Secret</c> CloudNativePG reads the owner's password from.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ Matches <c>templates/cluster.yaml</c>'s <c>{{ include "postgres.name" . }}-app</c>. See
    ///     <see cref="ClusterJson" /> for why this reconciler references it and does not write it.
    /// </remarks>
    public static string CredentialSecretName(string name) => name + "-app";

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
    ///         ⚠ <b>The owner's password appears here as a <i>reference by name</i> and never as a
    ///         value, and the <c>Secret</c> it names is not written by this reconciler.</b> CNPG's
    ///         <c>bootstrap.initdb.secret.name</c> is the seam that makes that possible: the operator
    ///         reads the password out of a <c>Secret</c> in the namespace, so the only component that
    ///         ever holds the plaintext is whatever writes that <c>Secret</c> — docs/plan/12 § The
    ///         pattern, once, piece 5, "credential provisioning into the tenant's Vault", which needs
    ///         the OpenBao integration that does not exist. Until it does, the operator generates its
    ///         own password when the <c>Secret</c> is absent, which is a working database whose
    ///         credentials <c>listKeys</c> cannot yet hand out — a gap that is visible rather than a
    ///         plaintext password in grain state, which would not be.
    ///     </para>
    /// </remarks>
    public static string ClusterJson(string name, JsonElement desired) {
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

        var initdb = new JsonObject {
            ["database"] = Text(desired, "bootstrap", "database", "app"),
            ["owner"] = Text(desired, "bootstrap", "owner", "app"),
            ["secret"] = new JsonObject { ["name"] = CredentialSecretName(name) }
        };

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

        var spec = new JsonObject {
            ["instances"] = Number(desired, "replicas", 2),
            ["imageName"] = "ghcr.io/cloudnative-pg/postgresql:" + Version(desired),
            ["postgresql"] = postgresql,
            ["bootstrap"] = new JsonObject { ["initdb"] = initdb },
            ["storage"] = storage,
            ["monitoring"] = new JsonObject { ["enablePodMonitor"] = Flag(desired, "monitoring", "enabled", true) }
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            spec["resources"] = new JsonObject {
                ["requests"] = quantities.DeepClone(), ["limits"] = quantities
            };
        }

        // ⚠ Absent rather than a `false`-valued block when replication is asynchronous. The CRD has
        // no "asynchronous" member — asynchronous IS the absence of `postgresql_synchronous` — and an
        // empty block written anyway would be a field this field manager owns forever under
        // server-side apply, which is a field the tenant's own controller could then never set.
        if (Flag(desired, "synchronousReplication", false)) {
            spec["postgresql_synchronous"] = new JsonObject { ["method"] = "any", ["number"] = 1 };
        }

        var walSize = Text(desired, "storage", "walSize", string.Empty);
        if (walSize.Length > 0) {
            var wal = new JsonObject { ["size"] = walSize };
            if (storageClass.Length > 0) {
                wal["storageClass"] = storageClass;
            }

            spec["walStorage"] = wal;
        }

        if (BackupEnabled(desired)) {
            spec["backup"] = new JsonObject {
                ["retentionPolicy"] =
                    Number(desired, "backup", "retentionDays", 14).ToString(CultureInfo.InvariantCulture) + "d",
                ["barmanObjectStore"] = new JsonObject {
                    ["destinationPath"] = Text(desired, "backup", "destinationPath", string.Empty),
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
                ["pgbouncer"] = new JsonObject {
                    ["poolMode"] = Text(desired, "pooling", "mode", "transaction")
                }
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
        && ((spec["bootstrap"] as JsonObject)?["initdb"] as JsonObject)?["database"]?.GetValue<string>()
        == Text(desired, "bootstrap", "database", "app");

    static bool MatchesPooler(JsonObject spec, JsonElement desired) =>
        spec["instances"]?.GetValue<int>() == Number(desired, "pooling", "instances", 2)
        && (spec["pgbouncer"] as JsonObject)?["poolMode"]?.GetValue<string>()
        == Text(desired, "pooling", "mode", "transaction");

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
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = "17",
                ["replicas"] = replicas,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["pooling"] = new JsonObject { ["enabled"] = pooling, ["instances"] = 2 },
                ["bootstrap"] = new JsonObject { ["database"] = "app", ["owner"] = "app" }
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
