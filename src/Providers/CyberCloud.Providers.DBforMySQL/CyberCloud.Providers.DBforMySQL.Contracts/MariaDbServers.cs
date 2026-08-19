using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.DBforMySQL/servers</c>: the type, its api-version,
///     its body shape, the mariadb-operator <c>MariaDB</c> it becomes, and — because this row cannot
///     be described honestly without it — <see cref="SupportedSubset" />.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue, <i>"MariaDB — <c>CyberCloud.DBforMySQL/servers</c> · M3 ·
///         0.8 EM"</i>, on <b>mariadb-operator</b> (ADR-010 clause 1, which names the operator per
///         managed service).
///     </para>
///     <para>
///         ⚠ <b>THE COMPATIBILITY CLAIM, WHICH IS THIS ROW'S CENTRAL OBLIGATION AND NOT A
///         GARNISH.</b> docs/plan/12 line 310: <i>"Positioned as MySQL-compatible; the same honesty
///         rule as FerretDB applies to the compatibility claim."</i> That rule, at line 262, is:
///         <i>"⚠ This is a compatibility layer and the product page must say so, with a
///         supported-subset table. … Selling it as 'MongoDB' produces a churn event at the first
///         <c>$lookup</c>. Selling it as 'MongoDB-compatible document database, here is exactly what
///         works' produces a happy customer with a smaller use case."</i> ADR-011 makes the same
///         demand of the cache row in one line — <i>"say Valkey on the product page"</i>.
///     </para>
///     <para>
///         <b>So, in one sentence: <see cref="CompatibilityClaim" />.</b> The resource type is spelled
///         <c>DBforMySQL</c> and the thing that runs is <b>MariaDB</b>. The path is the Azure-parity
///         one, for the reason <c>CyberCloud.Cache/redis</c> gives about its own: a path is what a
///         tenant's existing scripts and ARM-shaped tooling address by string, and renaming it would
///         buy a truer noun at the price of the parity the row exists for. Everything a human reads —
///         <c>MariaDbProvider</c>'s <c>Display</c> summary, the chart's <c>description</c>,
///         the CLI's short name — says MariaDB and says <i>compatible</i>, and none of it says the
///         server is MySQL.
///     </para>
///     <para>
///         <b>The supported-subset table is <see cref="SupportedSubset" />, in this file, as data.</b>
///         Prose in a comment is a claim nothing can check; a table a test can walk is one that
///         cannot be quietly dropped when somebody shortens a summary.
///         <c>charts/managed/mariadb/conformance.yaml</c> § compatibility carries the same rows for
///         the product page and the conformance suite, and <c>MariaDbCompatibilityTests</c> asserts
///         that the claim reaches the API surface.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair and
///         <c>charts/managed/mariadb/values.yaml</c> is the generated one.</b> ADR-010 § Which end
///         authors the schema, DECIDED 2026-08-11: the C# <c>ResourceSchema</c> is authored, the
///         chart's <c>@param</c> block is generated from it by <c>ChartAnnotationEmitter</c> and
///         byte-diffed by <c>./build.sh Charts</c>. <c>/location</c> is root-level and
///         <see cref="ClusterIdPointer" /> is placement; both are excluded by the emitter rather than
///         by anyone remembering.
///     </para>
///     <para>
///         ⚠ <b>No property here is <see cref="SchemaProperty.Secret" />, and both passwords reach
///         the data plane as references by name.</b> Nothing on the write path swaps a secret value
///         for a <c>SecretRef</c> before the grain writes desired state
///         (<see cref="SchemaProperty" />'s own remarks say so), so a declared secret would be a
///         plaintext password in durable state. <c>spec.rootPasswordSecretKeyRef</c> and
///         <c>spec.passwordSecretKeyRef</c> are the seam. See <see cref="ServerJson" /> for the part
///         of that seam this operator does differently from both of its neighbours.
///     </para>
/// </remarks>
public static class MariaDbServers {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue and docs/plan/01 spell it.</summary>
    public const string ProviderNamespace = "CyberCloud.DBforMySQL";

    /// <summary>The one resource type. ⚠ The Azure-parity path — see the type's remarks.</summary>
    /// <remarks>
    ///     docs/plan/12 names <c>servers/databases</c>, <c>servers/roles</c> and
    ///     <c>servers/firewallRules</c> for the PostgreSQL row and the same shape applies here. They
    ///     are separate types with separate schemas and separate reconcilers, and declaring one with
    ///     no reconciler would put a type in the registry that answers <c>202</c> and converges
    ///     nothing.
    /// </remarks>
    public const string TypePath = "servers";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/mariadb/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>
    ///     The chart this type is the configuration surface of — docs/plan/12 § The pattern, once,
    ///     piece 1.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A registry declaration, not a render path.</b> <see cref="IResourceTypeBuilder.Chart" />
    ///     records which chart describes this type; <c>IKubeCommandBuilder.Chart</c> would ask
    ///     <c>CyberCloud.Kubernetes.Charts</c> to render one, and that assembly does not exist. So the
    ///     reconciler renders in C# the object the chart's template renders, and this constant ties
    ///     the two halves together until a renderer lands.
    /// </remarks>
    public const string ChartName = "managed/mariadb";

    /// <summary>How long a deleted server can be restored for. docs/plan/06 § Tags, locks.</summary>
    /// <remarks>
    ///     ⚠ <b>The same seven days <c>PostgresServers.SoftDeleteDays</c> claims, deliberately.</b>
    ///     docs/plan/06 § Tags, locks names 7 days for <i>"resources carrying data"</i>, and two managed
    ///     relational databases whose recovery windows differed would be a difference a tenant has to
    ///     look up rather than assume. What the window preserves is what the teardown leaves behind: the
    ///     Galera nodes' <c>PersistentVolumeClaim</c>s, the stored body, and the committed quota.
    /// </remarks>
    public const int SoftDeleteDays = 7;

    /// <summary>The field manager the apply runs under — ADR-013's stable per-provider name.</summary>
    public const string FieldManager = "cybercloud/cybercloud.dbformysql";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the connection credentials.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions, Credentials. ⚠ <c>regenerateKeys</c> is named in the
    ///     same paragraph and is <b>not</b> declared, for the reason
    ///     <c>CyberCloud.DBforPostgreSQL/servers</c> gives: it is specified with a rolling grace period
    ///     and nothing in the platform can hold two live credentials for one resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     A key export is not a read: docs/plan/07 § Consistency puts it in the fully-consistent row
    ///     by name, and <c>ResourceManagerService</c> passes an action's <c>secret</c> flag into the
    ///     authorization call for exactly that reason. Sharing <c>read</c> would make every viewer of a
    ///     database a holder of its root password.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    // ── The compatibility claim ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     What this row is, in the one sentence a product page has room for.
    /// </summary>
    /// <remarks>
    ///     ⚠ It names the engine <b>first</b>. "MySQL-compatible database" alone is the sentence that
    ///     produces the churn event docs/plan/12 § MongoDB-compatible describes: a reader takes
    ///     "compatible" as a synonym for "is", discovers otherwise at the first
    ///     <c>caching_sha2_password</c> handshake, and reads the whole page back as marketing. Naming
    ///     MariaDB first costs one word and makes every row of <see cref="SupportedSubset" /> read as
    ///     a detail rather than as a retraction.
    /// </remarks>
    public const string CompatibilityClaim = "MySQL wire-compatible — MariaDB, not MySQL.";

    /// <summary>One row of the supported-subset table.</summary>
    /// <param name="Id">A stable identifier, matching <c>conformance.yaml</c> § compatibility.</param>
    /// <param name="Supported">Whether the platform offers it.</param>
    /// <param name="Says">What a tenant needs to know, in the terms they would hit it in.</param>
    public readonly record struct CompatibilityNote(string Id, bool Supported, string Says);

    /// <summary>
    ///     The supported-subset table docs/plan/12 line 310 requires, by way of line 262's rule.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every <c>false</c> row is a real, reachable failure rather than a hedge</b>, and
    ///         the first two are the ones that decide whether a migration lands: an application
    ///         configured for MySQL 8's default authentication plugin does not connect at all, and a
    ///         <c>mysqldump</c> from MySQL 8 does not import because the collations it names do not
    ///         exist here. Those two are this row's <c>$lookup</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>true</c> rows are not filler either.</b> A subset table listing only
    ///         absences reads as a warning notice; the point of the FerretDB paragraph is that the
    ///         honest version wins a customer with a smaller use case, which requires saying what
    ///         works.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This table describes the two ENGINES and not this platform's implementation of
    ///         them</b>, which is why nothing here is worded as a roadmap item. The rows marked
    ///         <c>false</c> do not close when the provider grows a property; they close if MariaDB
    ///         implements them, which is not a thing this row waits for.
    ///         <c>charts/managed/mariadb/conformance.yaml</c> § owed is where <i>this platform's</i>
    ///         debts are, and it is a separate list on purpose.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<CompatibilityNote> SupportedSubset { get; } = [
        new(
            "wire-protocol",
            true,
            "The MySQL client/server protocol on port 3306. Standard MySQL connectors, `mysql` and "
            + "`mariadb` command-line clients and ORMs connect unmodified."
        ),
        new(
            "sql-core",
            true,
            "SQL:2016 core, InnoDB transactions, foreign keys, prepared statements, stored routines, "
            + "triggers, views, window functions and common table expressions."
        ),
        new(
            "dump-restore",
            true,
            "Logical migration in and out via mysqldump or mariadb-dump, subject to the collation row."
        ),
        new(
            "auth-plugin",
            false,
            "MySQL 8's default `caching_sha2_password` authentication plugin. MariaDB does not "
            + "implement it, so a client left on MySQL 8 defaults must be told to use "
            + "`mysql_native_password` or `ed25519`. This is the first thing a MySQL 8 application "
            + "hits, and it is a connection failure rather than a query failure."
        ),
        new(
            "collations",
            false,
            "The MySQL 8.0 `utf8mb4_0900_*` collation family. It does not exist in MariaDB, so a "
            + "dump taken from MySQL 8 carrying COLLATE utf8mb4_0900_ai_ci does not import "
            + "unmodified."
        ),
        new(
            "replication-interop",
            false,
            "Replicating from or to a MySQL 8 server. The two projects use different GTID formats and "
            + "neither supports replication across the pair."
        ),
        new(
            "data-directory",
            false,
            "Opening a MySQL data directory in place. Migration between the two engines is logical — "
            + "dump and restore — and never a volume you move."
        ),
        new(
            "x-protocol",
            false,
            "The X Protocol on port 33060 and the MySQL Document Store."
        ),
        new(
            "group-replication",
            false,
            "MySQL Group Replication and InnoDB Cluster. High availability here is Galera, which is a "
            + "different mechanism with different semantics."
        ),
        new(
            "json-type",
            false,
            "MySQL 8's binary JSON column type. MariaDB's JSON is an alias for LONGTEXT with a check "
            + "constraint, so the functions largely match and SHOW CREATE TABLE does not."
        )
    ];

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>mariadb-operator's <c>MariaDB</c> — the object a server <i>is</i>.</summary>
    /// <remarks>
    ///     ⚠ <see cref="GroupVersionKind.Plural" /> is carried rather than derived, for the reason that
    ///     type's own remarks give. It must match <c>charts/managed/mariadb/SOURCE</c>'s
    ///     <c>upstream-api: k8s.mariadb.com/v1alpha1</c>.
    ///     <para>
    ///         ⚠ <b><c>v1alpha1</c> is the only served version, and this is the first type in the tree
    ///         whose CRD is not at a stable one.</b> It is not a reason to refuse the row — the
    ///         api-version this provider publishes is a promise about <i>our</i> body shape, which the
    ///         platform controls — but a conversion upstream is a reconciler change, and
    ///         <c>conformance.yaml</c> § owed, <c>crd-is-v1alpha1</c>, records that it was expected
    ///         rather than discovered.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind ServerKind { get; } =
        new() { Group = "k8s.mariadb.com", Version = "v1alpha1", Kind = "MariaDB", Plural = "mariadbs" };

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────
    //
    // ⚠ DO NOT WRITE A QUANTITY GRAMMAR HERE. Four local copies of it once existed and the fourth
    // produced a second, `double`-based parser for a grammar the platform already parsed exactly in
    // `decimal`; the grammar now lives beside that parser in KubeQuantity, and QuantityParserTests
    // fails if a fifth copy or a second suffix table appears. KubeQuantity is in
    // CyberCloud.ResourceManager.Contracts, which this project already references, so reaching the
    // shared constant costs a provider author nothing but the name.

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>An unquoted identifier this platform will create, lower-case.</summary>
    /// <remarks>
    ///     ⚠ <b>Lower case is not tidiness; on Linux a database is a directory.</b> MariaDB and MySQL
    ///     both derive a database's directory name from its identifier, and
    ///     <c>lower_case_table_names</c> defaults to <c>0</c> there — so <c>App</c> and <c>app</c> are
    ///     two databases on this platform and one database on a developer's macOS laptop. Accepting
    ///     both spellings is how a dump restores into a schema that is missing half its tables with no
    ///     error anywhere.
    /// </remarks>
    public const string IdentifierPattern = "[a-z_][a-z0-9_]*";

    /// <summary>The longest database name the server will accept.</summary>
    /// <remarks>Both engines agree on 64, so this one is not a compatibility choice.</remarks>
    public const int MaxDatabaseNameLength = 64;

    /// <summary>The longest account name this API accepts.</summary>
    /// <remarks>
    ///     ⚠ <b>MySQL's limit, not MariaDB's, and that is <see cref="SupportedSubset" /> applied to a
    ///     number.</b> MariaDB accepts a longer account name than MySQL does. Taking the larger of the
    ///     two would let a tenant create an account whose name their own MySQL tooling cannot
    ///     reproduce — a compatibility break introduced by this platform rather than inherited from
    ///     the engine, which is the one kind this row has no excuse for. The smaller limit is a subset
    ///     of both and costs nobody anything.
    /// </remarks>
    public const int MaxUserNameLength = 32;

    /// <summary>The port the server listens on. The protocol's own, not a choice.</summary>
    public const int Port = 3306;

    /// <summary>The replication topologies this type offers. ⚠ Two members, and docs/plan/12 names three.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/12 line 309 says <i>"Galera for HA, or async replication"</i>.
    ///         <c>Replication</c> is not offered: mariadb-operator's own documentation marks
    ///         <c>spec.replication</c> alpha and recommends Galera for production, and shipping an
    ///         alpha topology as a product mode is the compatibility pretence this type's remarks
    ///         reject pointed the other way. <c>conformance.yaml</c> § owed,
    ///         <c>async-replication-topology</c>, carries it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The property is <see cref="SchemaProperty.Immutable" />, and adding the third
    ///         member later is a new api-version either way.</b> <c>OpenApiCompatibility</c> refuses a
    ///         widened <see cref="SchemaProperty.AllowedValues" />, so a third topology is a new date;
    ///         what declaring the axis now buys is that it is a new date for <i>one enum member</i>
    ///         rather than for a property that did not exist, which leaves the meaning, the
    ///         immutability and the place in every generated client alone.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> Topologies { get; } = ["None", "Galera"];

    /// <summary>The instance count a Galera server runs.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant rather than a tenant setting, and the CRD is why rather than taste.</b> The
    ///     <c>MariaDB</c> CRD carries the rule <i>"An odd number of MariaDB instances
    ///     (mariadb.spec.replicas) is required to avoid split brain situations for Galera"</i>, with an
    ///     opt-out at <c>galera.replicasAllowEvenNumber</c> that this provider does not render.
    ///     <see cref="SchemaProperty" /> can express <see cref="SchemaProperty.Minimum" /> and
    ///     <see cref="SchemaProperty.Maximum" /> and has no way to say "odd" — so a <c>replicas</c>
    ///     property of 1..5 would be a body that validates here and then produces a CR the API server
    ///     refuses, <i>after</i> the caller was told <c>202</c>, for two of the five legal values.
    ///     <para>
    ///         Three rather than five for the reason <c>ValkeyCaches.SentinelReplicas</c> gives about
    ///         its own quorum: it is the smallest odd number that can hold a majority opinion, and the
    ///         size of a quorum is not something a tenant is placed to choose well.
    ///         <c>conformance.yaml</c> § owed, <c>replica-count-is-not-a-setting</c>, records what
    ///         closing it would take — a parity constraint in <c>SchemaProperty</c>, which is a
    ///         resource-manager change rather than a provider's.
    ///     </para>
    /// </remarks>
    public const int GaleraReplicas = 3;

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Seventeen properties: the fourteen API-facing <c>@param</c> rows of
    ///         <c>charts/managed/mariadb/values.yaml</c>, plus <c>/properties</c> itself and the two
    ///         body-only properties — <c>/location</c> and <see cref="ClusterIdPointer" /> — that the
    ///         chart-annotation emitter excludes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every default here is the chart's default, spelled as JSON.</b> There is no
    ///         <c>@default</c> directive because the chart's default <i>is</i> the YAML literal on the
    ///         annotated line — charts/README.md § The annotation format — and
    ///         <c>ChartAnnotationEmitter</c> writes that literal from
    ///         <see cref="SchemaProperty.DefaultJson" />.
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
                    Description: "The cluster whose namespace holds the MariaDB objects."
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
                    Description: "Major MariaDB version, by LTS series. Minor upgrades are applied "
                    + "automatically in the maintenance window."
                ) {
                    AllowedValues = ["10.11", "11.4", "11.8"],
                    DefaultJson = "\"11.4\""
                },
                new(
                    "/properties/highAvailability",
                    SchemaKind.Text,
                    Description: "Replication topology. Galera is three synchronous instances; None is "
                    + "a single instance and a single point of failure, offered for development only."
                ) {
                    AllowedValues = Topologies,
                    Immutable = true,
                    DefaultJson = "\"Galera\""
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
                new(
                    "/properties/storage",
                    SchemaKind.Nested,
                    Description: "The data volume. Every instance gets its own."
                ),
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
                    "/properties/bootstrap",
                    SchemaKind.Nested,
                    Description: "What exists in the database the moment it comes up."
                ),
                new(
                    "/properties/bootstrap/database",
                    SchemaKind.Text,
                    Description: "Name of the application database created on first start. Lower case "
                    + "only, because on Linux a database is a directory and its name is therefore "
                    + "case-sensitive."
                ) {
                    Pattern = IdentifierPattern,
                    MinLength = 1,
                    MaxLength = MaxDatabaseNameLength,
                    DefaultJson = "\"app\""
                },
                new(
                    "/properties/bootstrap/username",
                    SchemaKind.Text,
                    Description: "Account granted every privilege on the application database. Capped "
                    + "at MySQL's 32 characters rather than MariaDB's longer limit, because this row "
                    + "is sold as MySQL-compatible."
                ) {
                    Pattern = IdentifierPattern,
                    MinLength = 1,
                    MaxLength = MaxUserNameLength,
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
                    Description: "Whether the operator runs a mysqld-exporter beside the server."
                ) {
                    DefaultJson = "true"
                }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/listKeys</c> returns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Declared even though no handler serves it anywhere in the platform.</b> There is no
    ///         action dispatch in <c>CyberCloud.ResourceManager</c> — not for this type and not for any
    ///         other — so this shape is a contract and not a promise about today.
    ///         <c>conformance.yaml</c> § owed, <c>listkeys-has-no-handler</c>, says what the row loses:
    ///         a tenant with a running MariaDB has no supported way to learn its password, and the two
    ///         Secrets the operator generated are readable only with cluster access the tenant does not
    ///         have. Declaring the shape anyway is the reason <c>CyberCloud.Providers.Sample</c> gives
    ///         about <c>ping</c> — an undeclared response is the one part of an API surface with no
    ///         contract, and what leaves through a <c>secret: true</c> action is exactly the thing to
    ///         write down before it leaves.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The application account, not root.</b> Root exists — the operator generates its
    ///         password — and is deliberately not what this action hands out: a credential with
    ///         <c>GRANT OPTION</c> over every schema is not the one an application connects with, and
    ///         an API that returns it makes the safe choice the harder one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The host moves with the topology, visibly.</b> With Galera the endpoint is the
    ///         operator's <c>&lt;name&gt;-primary</c> Service; without it there is one instance and the
    ///         endpoint is <c>&lt;name&gt;</c> — which is why <c>/properties/highAvailability</c> is
    ///         immutable rather than merely awkward to change.
    ///     </para>
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/host",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster DNS name to connect to. ⚠ The primary Service's when "
                    + "high availability is on, so the topology is part of the connection string "
                    + "rather than hidden behind it."
                ),
                new("/port", SchemaKind.WholeNumber, Required: true, Description: "The TCP port.") {
                    Minimum = 1,
                    Maximum = 65535
                },
                new("/database", SchemaKind.Text, Required: true, Description: "The application database."),
                new("/username", SchemaKind.Text, Required: true, Description: "The application account."),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The application account's password, read from the tenant's Vault for "
                    + "this call only."
                ),
                new(
                    "/authenticationPlugin",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The authentication plugin a client must be configured for. ⚠ Not "
                    + "MySQL 8's caching_sha2_password default — see the supported-subset table. "
                    + "Returned rather than documented because a wrong default here is a connection "
                    + "that fails with a message about a plugin nobody chose."
                )
            ]
        );

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, s1 family.</summary>
    /// <remarks>
    ///     ⚠ <b>This table is a second copy of
    ///     <c>charts/managed/mariadb/templates/_helpers.tpl</c>'s <c>mariadb.presets</c>, and the
    ///     duplication is the cost of having no chart renderer.</b>
    ///     <c>CyberCloud.Kubernetes.Charts</c> does not exist (docs/plan/03 § src), so the object is
    ///     built here; the moment it does, this table and the reconciler's use of it should go and the
    ///     chart's should stay, because the chart is the file a support engineer reads. Until then both
    ///     exist and <c>ChartRegistryPairTests</c> asserts they agree value for value.
    ///     <para>
    ///         ⚠ <b>AND IT IS ALSO A SECOND COPY OF <c>PostgresServers.Presets</c>, WHICH NOTHING
    ///         COMPARES.</b> The s1 family is docs/plan/12 § Sizing vocabulary's, not this provider's,
    ///         and two managed relational databases now spell the same eight rows independently. Rule 2
    ///         of docs/plan/03 § Assembly graph rules forbids reaching the other provider's copy — even
    ///         for a <c>const</c>, and especially for one, since the compiler inlines it and the gate
    ///         reads binding references. The quantity grammar had the same problem and was lifted into
    ///         <c>KubeQuantity</c>, beside the parser that gave it a reason to live there; a preset
    ///         table is a docs/plan/12 fact with no such neighbour in
    ///         <c>CyberCloud.ResourceManager.Contracts</c>, so it is <b>recorded here rather than
    ///         fixed</b> — and the shape of the fix, when a third s1 consumer arrives, is a sizing
    ///         vocabulary in the resource manager rather than a fourth copy.
    ///     </para>
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

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── Addressing ────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>MariaDB</c> a server owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ServerRef(string ns, string name) =>
        new() { Kind = ServerKind, Namespace = ns, Name = name };

    /// <summary>The <c>Secret</c> the operator reads the root password from.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>The operator's own default name</b>, from <c>api/v1alpha1/mariadb_keys.go</c>'s
    ///     <c>RootPasswordSecretKeyRef()</c>. Choosing a different one would work identically today and
    ///     would mean that if <see cref="ServerJson" /> ever stopped rendering the reference, the
    ///     operator's fallback pointed at a <i>different</i> Secret and the server came up with a
    ///     credential nobody had recorded.
    /// </remarks>
    public static string RootSecretName(string name) => name + "-root";

    /// <summary>The <c>Secret</c> the operator reads the application account's password from.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>⚠ The operator's own default, for the reason on <see cref="RootSecretName" />.</remarks>
    public static string PasswordSecretName(string name) => name + "-password";

    /// <summary>The key inside <see cref="PasswordSecretName" /> and <see cref="RootSecretName" />.</summary>
    /// <remarks>
    ///     ⚠ <b>This platform's choice rather than the operator's, which is why it is one constant and
    ///     not two.</b> mariadb-operator takes the key name from the CR's <c>…SecretKeyRef</c> and
    ///     generates the Secret to match when <c>generate</c> is set — so the name in
    ///     <see cref="ServerJson" /> and the name <c>listKeys</c> reads by have to be the same string,
    ///     and a literal in both files is a mismatch waiting for the first edit that touches one.
    /// </remarks>
    public const string PasswordKey = "password";

    /// <summary>
    ///     The Service a client connects to.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="highlyAvailable">Whether the body asked for Galera.</param>
    /// <remarks>
    ///     ⚠ Both names are the operator's, read off <c>api/v1alpha1/mariadb_keys.go</c>
    ///     (<c>PrimaryServiceKey</c>) and <c>mariadb_types.go</c> (<c>GetHost</c>, which falls back to
    ///     the Service named after the resource when HA is off). They are not objects this provider
    ///     applies and not ones it may rename; they are here because <see cref="ListKeysResponse" />
    ///     advertises one, and a connection string built from a guess resolves to nothing.
    /// </remarks>
    public static string EndpointName(string name, bool highlyAvailable) =>
        highlyAvailable ? name + "-primary" : name;

    /// <summary>
    ///     The authentication plugin <see cref="ListKeysResponse" /> reports.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The <c>auth-plugin</c> row of <see cref="SupportedSubset" />, as a value rather than as
    ///     a sentence.</b> MySQL 8 defaults its clients to <c>caching_sha2_password</c>, which MariaDB
    ///     does not implement, so the compatibility claim's first real consequence is a handshake
    ///     failure. A connection string that carries the plugin turns that into a setting the caller
    ///     already has rather than an error they have to go and read about.
    /// </remarks>
    public const string AuthenticationPlugin = "mysql_native_password";

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The major version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? "11.4"
            : "11.4";

    /// <summary>The container image a body implies.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Written on every apply rather than left unset.</b> <c>MariaDB.GetImage</c> falls back to
    ///     the operator's <c>RelatedMariadbImage</c> environment value — an image chosen by whoever
    ///     installed the operator, at whatever version they shipped. <c>/properties/version</c> is a
    ///     promise about which MariaDB the tenant gets, and inheriting a cluster-wide default is how
    ///     that promise breaks without anybody editing anything.
    /// </remarks>
    public static string Image(JsonElement desired) => "mariadb:" + Version(desired);

    /// <summary>The topology a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string HighAvailability(JsonElement desired) =>
        Root(desired, "highAvailability") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? "Galera"
            : "Galera";

    /// <summary>Whether the body asked for Galera.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool IsHighlyAvailable(JsonElement desired) =>
        string.Equals(HighAvailability(desired), "Galera", StringComparison.Ordinal);

    /// <summary>How many instances a body implies, including the primary.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Public because quota multiplies by it, and derived because the CRD would not let it be
    ///     a number in the body.</b> Every instance carries the whole <c>spec.resources</c> block and
    ///     its own data PVC — <see cref="ServerJson" /> writes both once because the CR is per-server,
    ///     not because the cost is. A meter that reserved the per-instance figure would under-reserve
    ///     the default shape by two thirds. See <see cref="GaleraReplicas" /> for why the multiplier is
    ///     not a tenant-facing property, which is the sharper version of the same lesson: it is not
    ///     merely "not one value at one pointer", it is not a number in the body at all.
    /// </remarks>
    public static int Replicas(JsonElement desired) => IsHighlyAvailable(desired) ? GaleraReplicas : 1;

    /// <summary>The data volume size a body asks for, <b>per instance</b>.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) => Text(desired, "storage", "size", "20Gi");

    /// <summary>The application database a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Database(JsonElement desired) => Text(desired, "bootstrap", "database", "app");

    /// <summary>The application account a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Username(JsonElement desired) => Text(desired, "bootstrap", "username", "app");

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

    /// <summary>The <c>my.cnf</c> a server starts with.</summary>
    /// <remarks>
    ///     ⚠ One setting, and it is the one whose default is wrong for a managed database. MariaDB
    ///     ships <c>max_connections=151</c>; a pooled application tier reaches that on a bad afternoon
    ///     and the symptom is "Too many connections" rather than anything a tenant would attribute to a
    ///     server setting. The same number <c>PostgresServers.ClusterJson</c> writes, so that the two
    ///     managed relational databases answer the same question the same way.
    ///     <para>
    ///         ⚠ <c>[mariadb]</c> and not <c>[mysqld]</c>. Both are read by a MariaDB server, and the
    ///         engine is MariaDB — a group header claiming otherwise would be the pretence this type's
    ///         compatibility remarks reject.
    ///     </para>
    /// </remarks>
    public const string MyCnf = "[mariadb]\nmax_connections=200\n";

    /// <summary>
    ///     The <c>MariaDB</c> document a desired body becomes, ready for server-side apply.
    /// </summary>
    /// <param name="name">The object's <c>metadata.name</c> — the resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The JSON <c>templates/mariadb.yaml</c> renders, for the same values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. Those are ADR-013's seven mandatory
    ///         labels and two annotations, and <c>KubeCommand</c> injects them non-overridably —
    ///         docs/plan/09 § The command builder. A provider that set them itself would be a provider
    ///         that could get them wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both passwords appear as <i>references by name</i> and never as values, and neither
    ///         <c>Secret</c> is written by this reconciler</b> — docs/plan/12 § The pattern, once,
    ///         piece 5, which needs the OpenBao integration that does not exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>generate: true</c> is load-bearing rather than polite, and this operator is a
    ///         THIRD answer to the question the two neighbouring rows already asked.</b> CloudNativePG
    ///         generates its own password when the referenced <c>Secret</c> is absent, so
    ///         <c>CyberCloud.DBforPostgreSQL/servers</c> gets a working database whose credentials
    ///         <c>listKeys</c> cannot hand out. spotahome generates nothing, so
    ///         <c>CyberCloud.Cache/redis</c> does not come up at all. mariadb-operator does neither by
    ///         itself: <c>MariaDB.SetDefaults</c> fills in <c>spec.rootPasswordSecretKeyRef</c> —
    ///         carrying <c>Generate: true</c> — <b>only when the field is the zero value</b>, and
    ///         <c>GeneratedSecretKeyRef.Generate</c>'s own default is <c>false</c>. So rendering the
    ///         reference at all <i>replaces</i> the generous default, and a reference written without
    ///         the flag is a server that waits forever for a <c>Secret</c> nothing writes. The
    ///         generosity is a default, not a behaviour. Writing it explicitly puts this row in the
    ///         PostgreSQL position — running, authenticated, credentials unreachable — by an explicit
    ///         choice rather than by an operator's habit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>rootEmptyPassword</c> is written as <c>false</c> though its default already is.</b>
    ///         It is the one field on this CRD that turns a managed database into an open one, and
    ///         docs/plan/12's own <i>"a managed database on a public IP with a weak password is the
    ///         single most common cloud breach"</i> applies inside a namespace too. Stating it puts the
    ///         answer where a reader asking "is this thing password-protected" will look.
    ///     </para>
    /// </remarks>
    public static string ServerJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (cpu, memory) = Resources(desired);

        var storage = new JsonObject { ["size"] = StorageSize(desired) };
        var storageClass = Text(desired, "storage", "class", string.Empty);

        if (storageClass.Length > 0) {
            storage["storageClassName"] = storageClass;
        }

        var spec = new JsonObject {
            ["image"] = Image(desired),
            ["replicas"] = Replicas(desired),
            ["port"] = Port,
            ["storage"] = storage,
            ["rootEmptyPassword"] = false,
            ["rootPasswordSecretKeyRef"] = SecretRef(RootSecretName(name)),
            ["database"] = Database(desired),
            ["username"] = Username(desired),
            ["passwordSecretKeyRef"] = SecretRef(PasswordSecretName(name)),
            ["myCnf"] = MyCnf,
            ["metrics"] = new JsonObject { ["enabled"] = Flag(desired, "monitoring", "enabled", true) }
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            spec["resources"] = new JsonObject {
                ["requests"] = quantities.DeepClone(), ["limits"] = quantities
            };
        }

        // ⚠ Absent rather than an `enabled: false` block when HA is off. `IsGaleraEnabled()` reads
        // `spec.galera != nil && spec.galera.enabled` and `Galera.SetDefaults` runs only when it is
        // enabled, so a `false` block would be a field this field manager owns forever under
        // server-side apply, on a struct the operator would otherwise never touch — the same reasoning
        // PostgresServers.ClusterJson gives about `postgresql_synchronous`.
        if (IsHighlyAvailable(desired)) {
            spec["galera"] = new JsonObject { ["enabled"] = true };
        }

        return new JsonObject { ["metadata"] = new JsonObject { ["name"] = name }, ["spec"] = spec }
            .ToJsonString();
    }

    static JsonObject SecretRef(string secretName) =>
        new() { ["name"] = secretName, ["key"] = PasswordKey, ["generate"] = true };

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns>
    ///     <c>true</c> when the fields this provider owns hold the desired values.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, not equality, and the CRD is the evidence rather than the README.</b>
    ///         <c>MariaDB.SetDefaults</c> in <c>api/v1alpha1/mariadb_types.go</c> writes into the spec
    ///         it is given: <c>image</c>, <c>rootEmptyPassword</c>, <c>rootPasswordSecretKeyRef</c>,
    ///         <c>port</c>, <c>myCnfConfigMapKeyRef</c>, <c>passwordSecretKeyRef</c>, the metrics
    ///         exporter's image, port, username and password reference, <c>tls: {enabled: true}</c>,
    ///         <c>updateStrategy</c>, Galera's defaults, and — through <c>Storage.SetDefaults()</c> —
    ///         <c>storage.ephemeral</c>, <c>storage.resizeInUseVolumes</c>,
    ///         <c>storage.waitForVolumeResize</c> and a whole <c>volumeClaimTemplate</c>. An equality
    ///         test would report drift on the pass immediately after the operator first saw the object,
    ///         and on every pass after that, forever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>tls</c> default is worth naming on its own</b>, because it is the one place
    ///         this operator gives the platform something docs/plan/12 asked another row for and could
    ///         not have. It arrives as a field this provider never wrote, which is exactly the shape
    ///         that makes an equality test wrong.
    ///     </para>
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

        if (spec["replicas"]?.GetValue<int>() != Replicas(desired)
            || spec["image"]?.GetValue<string>() != Image(desired)
            || (spec["storage"] as JsonObject)?["size"]?.GetValue<string>() != StorageSize(desired)
            || spec["database"]?.GetValue<string>() != Database(desired)
            || spec["username"]?.GetValue<string>() != Username(desired)) {
            return false;
        }

        // ⚠ The credential seam, read back as a NAME. An operator that dropped the reference — or a
        // provider that stopped rendering it — would leave a server whose password nothing recorded,
        // and every other field here would still agree.
        if ((spec["rootPasswordSecretKeyRef"] as JsonObject)?["name"]?.GetValue<string>() is not { Length: > 0 }) {
            return false;
        }

        // ⚠ Galera read back as a POSITIVE fact in both directions. `galera` absent and
        // `galera.enabled: false` are the same thing to the operator, so HA-off tolerates either; but
        // HA-on demands `true`, because a Galera server whose block the operator dropped is a
        // single-instance database with a three-instance quota reservation.
        var galera = (spec["galera"] as JsonObject)?["enabled"]?.GetValue<bool>() == true;

        return galera == IsHighlyAvailable(desired);
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the server in.</param>
    /// <param name="highAvailability">The topology.</param>
    /// <param name="storageSize">The data volume size.</param>
    /// <param name="database">The application database.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string highAvailability = "Galera",
        string storageSize = "20Gi",
        string database = "app",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = "11.4",
                ["highAvailability"] = highAvailability,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["bootstrap"] = new JsonObject { ["database"] = database, ["username"] = "app" },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
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

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Member(desired, parent, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
