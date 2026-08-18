// ⚠ For SecretRef. CyberCloud.Storage/accounts is the first provider that named the type and this is
// the second; it lives in CyberCloud.Core.Contracts rather than in CyberCloud.ResourceManager.Contracts
// where it started — see its own remarks on why the [Alias] stayed put through the move.
using CyberCloud.Core.Contracts;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Monitor/workspaces</c>: the type, its api-version,
///     its body shape, and the <b>three</b> Kubernetes objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A WORKSPACE IS A TENANCY IN A STORE THE PLATFORM ALREADY RUNS. IT IS NOT A
///         DEPLOYMENT, AND GETTING THAT BACKWARDS IS THE EXPENSIVE MISTAKE ON THIS ROW.</b>
///         <c>CyberCloud.Analytics/clickhouseClusters</c> had to settle the mirror image of this
///         question and its answer is the precedent: the platform's own ClickHouse is not that
///         type. This row is the other half of the same sentence — the platform's own ClickHouse,
///         and the platform's own VictoriaMetrics, are reached through <b>this</b> type, and this
///         type provisions neither of them.
///     </para>
///     <para>
///         Five facts decide it, in the order that decides it:
///     </para>
///     <list type="number">
///         <item>
///             <b>docs/plan/16 § Ingest routes to coordinates INSIDE one store, not to stores.</b>
///             <i>"VictoriaMetrics (accountID) | ClickHouse (per-tenant database)"</i>. An
///             <c>accountID</c> is a tenancy coordinate inside one VictoriaMetrics cluster and a
///             database is one inside one ClickHouse. Neither is a deployment. If a workspace were a
///             deployment, the ingest router would have to discover and hold a connection per
///             workspace, and that document's opening claim — <i>"it is only safe because tenancy is
///             enforced at ingest, not at query"</i> — would have nothing left to enforce.
///         </item>
///         <item>
///             <b>docs/plan/05 § Every store gives the telemetry stores ONE ROW EACH.</b>
///             <i>"ClickHouse · Logs, traces, metering rollups, resource graph · Per region;
///             database-per-tenant; shard+replica"</i>, and <i>"VictoriaMetrics · Metrics · Per
///             region; native multi-tenant accountID"</i>. One row per region is one deployment per
///             region; database-per-tenant is what this type allocates.
///         </item>
///         <item>
///             <b>The deployment-shaped reading is a product this catalogue already sells under
///             another name.</b> <c>CyberCloud.Analytics/clickhouseClusters</c> is a single-tenant
///             cluster in a tenant namespace whose schema the tenant owns. A workspace that was also
///             a deployment would be that type with a worse name, and docs/plan/16's economic claim —
///             <i>"Building one pipeline for both is the decision that makes this affordable"</i> —
///             is a shared-engine argument that a per-workspace deployment refutes.
///         </item>
///         <item>
///             ⚠ <b>THE DEPENDENCY CYCLE, WHICH IS THE ONE THAT MAKES IT A CORRECTNESS QUESTION
///             RATHER THAN A MODELLING PREFERENCE.</b> docs/plan/16 says platform telemetry runs
///             <i>"under a platform workspace. No separate stack."</i> — so the platform workspace
///             <b>is</b> a resource of this type. If a resource of this type provisioned the store,
///             then reconciling the platform's own workspace would provision the store that every
///             reconcile — including that one — emits its telemetry into.
///             <c>CyberCloud.Ingest.Host</c> is deliberately not an Orleans client
///             (docs/plan/03 § Hosts) precisely so telemetry never runs through the control plane,
///             and a store the control plane creates puts it back. <b>Both halves have to be true at
///             once — the platform workspace is one of these, and the platform's stores are not —
///             and only the shared-store reading makes them compatible.</b>
///         </item>
///         <item>
///             <b>Soft delete decides the same way.</b> On the shared reading a soft-deleted
///             workspace is a tenancy whose partitions sit on disk until purge: cheap, reversible
///             and honest. On the deployment reading a recovery window either keeps a whole cluster
///             running for seven days or is a promise the platform cannot keep. See
///             <c>MonitorProvider</c>, which declares the window — and which records what declaring
///             one turned out to mean, because the first account of it here was wrong.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>WHAT THAT LEAVES A RECONCILER TO CONVERGE, AND WHY IT IS KUBERNETES.</b> The store
///         exists before any workspace does. What a workspace has to make true in the world is that
///         the <b>data plane</b> knows the tenancy: which key belongs to which (tenant, workspace),
///         which <c>accountID</c> its metrics carry, which database its logs land in, what its
///         retention and quota are. docs/plan/16 calls that <i>"a cached map"</i> and says nothing
///         about how it is filled — and the one constraint on filling it is decisive:
///         <b><c>CyberCloud.Ingest.Host</c> is not an Orleans client, so the control plane cannot
///         tell it anything by grain call.</b> The publication has to be through a store the ingest
///         host can read without the control plane, and the only such store the platform already
///         operates is Kubernetes. So the objects below are the workspace's <b>ingest map row</b>,
///         and the resource converges when that row is readable. That is a forced design rather
///         than a stylistic one, and it is the same argument as clause 4 read from the other end.
///     </para>
///     <para>
///         ⚠ <b>WHAT IS ENFORCED AND WHAT IS ONLY PUBLISHED, SPLIT RATHER THAN BLURRED.</b> The
///         <c>VMUser</c> is enforced the moment it is applied: vmauth reads it, and a workspace
///         cannot write to another workspace's metrics account because it never names one. Nothing
///         reads the <c>ConfigMap</c>, because <c>CyberCloud.Ingest.Host</c> does not exist yet —
///         docs/plan/03 § Hosts plans it and <c>src/Hosts</c> holds four hosts and not that one,
///         grepped rather than assumed. So the logs and traces half's retention, every volume
///         allowance, the cardinality cap and the over-quota sample rate reach the meter, the
///         published schema and the cluster, and are enforced by nobody.
///         <c>charts/managed/monitor-workspace/conformance.yaml § owed</c> says exactly what closes
///         each of them.
///     </para>
/// </remarks>
public static class MonitorWorkspaces {
    // ── Identity ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The provider namespace. docs/plan/01 § The catalogue and docs/plan/16.</summary>
    public const string ProviderNamespace = "CyberCloud.Monitor";

    /// <summary>The type path under <see cref="ProviderNamespace" />.</summary>
    public const string TypePath = "workspaces";

    /// <summary>The one api-version this type serves.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type renders.</summary>
    public const string ChartName = "managed/monitor-workspace";

    /// <summary>Where the cluster id lives in the body.</summary>
    /// <remarks>
    ///     ⚠ <b>It names the regional cluster whose telemetry data plane serves this workspace, and
    ///     NOT a cluster the workspace deploys anything into.</b> Every other type in the catalogue
    ///     reads this pointer as <i>"put my workload here"</i>; this one reads it as <i>"publish my
    ///     tenancy where this region's ingest host is watching"</i>. The mechanism is identical and
    ///     the meaning is not, which is why the property's description says so in the tenant's own
    ///     words rather than leaving it to be inferred from the type.
    /// </remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>Where purge protection lives in the body.</summary>
    /// <remarks>
    ///     ⚠ Declared as a required boolean in <see cref="Schema2026" />, which
    ///     <c>IResourceTypeBuilder.SupportsSoftDelete</c> refuses the type without — a flag the
    ///     platform enforces against a property no schema declares is a protection that silently
    ///     never engages.
    /// </remarks>
    public const string PurgeProtectionPointer = "/properties/purgeProtection";

    /// <summary>The action that hands out the ingest key and the endpoints.</summary>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> needs.</summary>
    /// <remarks>
    ///     ⚠ Not <c>read</c>. An ingest key is a write credential for the tenant's whole telemetry
    ///     stream; sharing the read permission would make every viewer of a workspace a party that
    ///     can forge its logs.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The qualified type.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The objects a workspace's tenancy becomes ─────────────────────────────────────────────

    /// <summary>The core group, which two of this type's three objects are in.</summary>
    /// <remarks>
    ///     ⚠ <b>Two core kinds and one custom kind, which is a mix no earlier family has in this
    ///     proportion, and it is the architecture read off the object list.</b> A type that
    ///     provisions an engine writes its operator's CRD and nothing else;
    ///     <c>CyberCloud.Storage/accounts</c> writes one <c>Secret</c> beside its custom resource
    ///     because the engine needs a credential file. This type provisions no engine at all, so
    ///     what it writes is a credential, a routing rule and a row — and only the routing rule
    ///     belongs to somebody else's schema.
    /// </remarks>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <inheritdoc cref="ConfigMapKind" />
    public static GroupVersionKind SecretKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    /// <summary>
    ///     The VictoriaMetrics operator's <c>VMUser</c> — vmauth's routing-and-credential object.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ONLY OBJECT HERE THAT BELONGS TO AN OPERATOR, AND THE ONLY ONE THAT
    ///         ENFORCES ANYTHING TODAY.</b> Checked against
    ///         <c>api/operator/v1beta1/vmuser_types.go</c> rather than a README:
    ///         <c>VMUserSpec</c> carries <c>username</c>, <c>password</c>, <c>passwordRef</c>,
    ///         <c>bearerToken</c> and a required <c>targetRefs</c>, and each <c>TargetRef</c> carries
    ///         <c>crd</c>, <c>static</c>, <c>paths</c>, <c>hosts</c> and <c>target_path_suffix</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE CASING IS MIXED UPSTREAM AND IT IS NOT A TYPO HERE.</b> The spec's own keys
    ///         are camelCase (<c>targetRefs</c>, <c>bearerToken</c>, <c>passwordRef</c>) and a
    ///         target ref's are snake_case (<c>target_path_suffix</c>, <c>query_args</c>). The
    ///         operator's prose calls the last one <i>"targetPathSuffix"</i> and the JSON tag is
    ///         <c>target_path_suffix</c>; a document written from the prose is accepted by an API
    ///         server with a permissive schema and ignored by vmauth, which is a workspace whose
    ///         metrics land in whatever tenant the prefix defaulted to.
    ///         <c>MonitorReconcilerTests.TheTargetPathSuffixIsSpelledTheWayTheGoTagSpellsIt</c> pins
    ///         it.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind VmUserKind { get; } =
        new() {
            Group = "operator.victoriametrics.com", Version = "v1beta1", Kind = "VMUser", Plural = "vmusers"
        };

    /// <summary>The name of the <c>ConfigMap</c> carrying the ingest map row.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string RowName(string name) => "monitor-" + name;

    /// <summary>The name of the <c>Secret</c> carrying the ingest key.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string KeySecretName(string name) => "monitor-" + name + "-ingest";

    /// <summary>The ingest map row a workspace owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef RowRef(string ns, string name) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = RowName(name) };

    /// <summary>The ingest key a workspace owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef KeySecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = KeySecretName(name) };

    /// <summary>The name of the <c>VMUser</c> that routes and authorises this workspace.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string VmUserName(string name) => "monitor-" + name;

    /// <summary>The <c>VMUser</c> a workspace owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef VmUserRef(string ns, string name) =>
        new() { Kind = VmUserKind, Namespace = ns, Name = VmUserName(name) };

    /// <summary>The key the ingest key is filed under inside the <c>Secret</c>.</summary>
    public const string IngestKeyField = "ingestKey";

    /// <summary>
    ///     The label the ingest host would select the rows of every workspace in a region by.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not one of ADR-013's seven, and it is here because the seven cannot answer this
    ///     question.</b> <c>cybercloud.io/resource-type</c> is on every object this provider applies,
    ///     including the <c>Secret</c>; a watcher wants the <c>ConfigMap</c>s and only the
    ///     <c>ConfigMap</c>s, across every namespace in the region, and it must not have to encode
    ///     "the one whose kind is ConfigMap" as a rule about our label scheme. So the row carries a
    ///     label of its own that says what it <i>is</i>.
    /// </remarks>
    public const string RowLabel = "cybercloud.io/telemetry-row";

    /// <summary>The value <see cref="RowLabel" /> carries.</summary>
    public const string RowLabelValue = "workspace";

    // ── The tenancy coordinates, which are pure functions of the address ──────────────────────

    /// <summary>
    ///     The VictoriaMetrics <c>accountID</c> this workspace's metrics are written and read under.
    /// </summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>DERIVED, NOT ALLOCATED, AND THAT IS WHY THIS TYPE NEEDS NO GRAIN.</b> A counter
    ///         would be durable state, would need one authority per region, and would be the one
    ///         thing in the resource that a silo restart could get wrong. VictoriaMetrics' accountID
    ///         is a 32-bit unsigned integer in a URL path, so the resource's own GUID folded to 32
    ///         bits is an accountID that is stable, recomputable anywhere, and needs nothing
    ///         remembered.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A FOLD IS NOT A BIJECTION AND THE COLLISION IS REAL.</b> Two workspaces sharing
    ///         an accountID would read each other's metrics, which is the cross-tenant failure this
    ///         whole document exists to prevent. At 2^32 accounts the birthday bound is a coin-flip
    ///         at roughly 77 000 workspaces, which is inside this platform's target scale — so this
    ///         is a <b>known limit with a named closure</b> rather than a safe derivation:
    ///         <c>conformance.yaml § owed</c>, <c>accountid-is-folded-not-allocated</c>. What makes
    ///         it acceptable today is that nothing reads it yet; what makes it unacceptable to leave
    ///         is that the first thing to read it is the thing that enforces isolation.
    ///     </para>
    ///     <para>
    ///         ⚠ Zero is skipped. VictoriaMetrics treats <c>accountID=0</c> as a legal tenant, and a
    ///         fold that can produce it would give one workspace the account every misconfigured
    ///         client writes to by default.
    ///     </para>
    /// </remarks>
    public static uint AccountId(ResourceId id) {
        var bytes = id.Id.ToByteArray();
        uint folded = 2166136261;

        foreach (var octet in bytes) {
            folded = (folded ^ octet) * 16777619;
        }

        return folded == 0 ? 1 : folded;
    }

    /// <summary>The ClickHouse database this workspace's logs, traces and events land in.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <remarks>
    ///     ⚠ <b>Keyed on the resource's GUID rather than on its name, and unlike
    ///     <see cref="AccountId" /> this one is injective.</b> A database name may be long, so the
    ///     whole GUID fits; a workspace renamed — which this platform does not offer — or two
    ///     workspaces called <c>prod</c> in two tenants cannot collide. The <c>ws_</c> prefix is
    ///     there because a ClickHouse identifier may not start with a digit.
    /// </remarks>
    public static string Database(ResourceId id) =>
        string.Create(CultureInfo.InvariantCulture, $"ws_{id.Id:N}");

    // ── The retention tiers ───────────────────────────────────────────────────────────────────

    /// <summary>The three retention tiers, in ascending order of what they cost.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>TIER NAMES RATHER THAN DAY COUNTS, AND THE REASON IS A LIMIT OF
    ///         <c>ResourceSchema</c> RATHER THAN A PREFERENCE.</b> docs/plan/16 spells retention as
    ///         nine numbers — <i>"metrics 15/90/400 days, logs 7/30/90, traces 3/14/30. Priced"</i> —
    ///         and each triple is a <b>discrete priced set</b>, not a range.
    ///         <c>SchemaProperty.AllowedValues</c> is legal on <c>SchemaKind.Text</c> and nowhere
    ///         else, and its own remarks say a numeric enumeration <i>"is expressible as
    ///         Minimum/Maximum or is a modelling mistake"</i> — but <c>Minimum</c>/<c>Maximum</c>
    ///         admits 399, which no price list has a row for and which this type's storage meter
    ///         would then reserve against. <b>So a discrete numeric tier set is inexpressible, and
    ///         this is the sixth family to record a rule <c>ResourceSchema</c> cannot state.</b>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What that forces turns out to be better than what it forbids.</b> A tier NAME is
    ///         enforceable at the API through <c>AllowedValues</c>, keeps the day count on the
    ///         platform's side of the contract where a price change is not an api-version break, and
    ///         is the same shape the whole catalogue already uses for sizing — a preset name over a
    ///         platform-owned table. The day counts are <see cref="RetentionDays" />, and
    ///         <c>MonitorRetentionTests</c> pins all nine against docs/plan/16 as literals.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>AND THE SECOND REASON IS THE ONE THAT REFUTES docs/plan/16: ON THE METRICS HALF
    ///         A PER-WORKSPACE RETENTION PERIOD IS NOT A SETTING VICTORIAMETRICS HAS.</b> Checked in
    ///         upstream's source and its own enterprise page on 2026-08-18, not in a blog post:
    ///         <c>app/vmstorage/main.go</c> declares <c>-retentionPeriod</c> once, per vmstorage
    ///         <i>node</i> — <i>"Data with timestamps outside the retentionPeriod is automatically
    ///         deleted"</i> — and the per-tenant form, <c>-retentionFilter</c> with its
    ///         <c>vm_account_id</c> pseudo-label selectors, is on
    ///         <c>docs.victoriametrics.com/enterprise/</c>'s feature list. So is
    ///         <c>-downsampling.period</c>. <b>An open-source VictoriaMetrics cluster cannot give two
    ///         accountIDs two retention periods.</b> docs/plan/16 § <c>workspaces</c> prices exactly
    ///         that, and ADR-016 chose the engine for <i>"native multi-tenancy"</i> — which is real
    ///         and is about isolation, not about retention.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The closure is upstream's own, and it is what makes a tier NAME the load-bearing
    ///         model rather than a workaround.</b> <c>docs.victoriametrics.com/guides/
    ///         guide-vmcluster-multiple-retention-setup/</c> answers the open-source case by running
    ///         <i>"separate logic groups of storages … with individual <c>-retentionPeriod</c>
    ///         settings, while still providing a single unified write and read path"</i>. That is
    ///         one vmstorage group per tier, and a workspace's tier therefore decides <b>which group
    ///         it is routed to</b> rather than a number written into a shared one — which is exactly
    ///         what <see cref="MetricsClusterName" /> spells and what the <c>VMUser</c> renders. A
    ///         retention expressed as a day count would have had nowhere to go; expressed as one of
    ///         three names it is a target.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The logs and traces half has no such limit and is the reverse case.</b>
    ///         ClickHouse's per-table <c>TTL expr + INTERVAL n DAY</c> is per database and therefore
    ///         genuinely per workspace, and <c>ALTER TABLE … MODIFY TTL</c> changes it in place. So
    ///         the two stores answer docs/plan/16's retention row differently, and the resource has
    ///         to be honest about both. What is owed on the ClickHouse half is the DDL itself —
    ///         nothing in the catalogue applies SQL — <c>conformance.yaml § owed</c>,
    ///         <c>clickhouse-ttl-is-published-not-applied</c>.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> Tiers { get; } = ["short", "standard", "extended"];

    /// <summary>The metrics signal.</summary>
    public const string Metrics = "metrics";

    /// <summary>The logs signal.</summary>
    public const string Logs = "logs";

    /// <summary>The traces signal.</summary>
    public const string Traces = "traces";

    /// <summary>The three signals, in the order docs/plan/16 § The stack lists them.</summary>
    public static ImmutableArray<string> Signals { get; } = [Metrics, Logs, Traces];

    /// <summary>
    ///     How many days each signal is kept at each tier — docs/plan/16 § <c>workspaces</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nine numbers, and every one of them is a bill.</b> They are the second factor of
    ///     <c>MonitorProvider</c>'s storage meter, so an edit here moves what every existing
    ///     workspace draws against its subscription's quota on its next write. That is the reason
    ///     they are a table rather than nine constants scattered through a renderer.
    /// </remarks>
    public static FrozenDictionary<string, FrozenDictionary<string, int>> RetentionDays { get; } =
        new Dictionary<string, FrozenDictionary<string, int>>(StringComparer.Ordinal) {
            [Metrics] = new Dictionary<string, int>(StringComparer.Ordinal) {
                ["short"] = 15, ["standard"] = 90, ["extended"] = 400
            }.ToFrozenDictionary(StringComparer.Ordinal),
            [Logs] = new Dictionary<string, int>(StringComparer.Ordinal) {
                ["short"] = 7, ["standard"] = 30, ["extended"] = 90
            }.ToFrozenDictionary(StringComparer.Ordinal),
            [Traces] = new Dictionary<string, int>(StringComparer.Ordinal) {
                ["short"] = 3, ["standard"] = 14, ["extended"] = 30
            }.ToFrozenDictionary(StringComparer.Ordinal)
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The default tier, for every signal.</summary>
    /// <remarks>
    ///     docs/plan/16 § Cost and retention honesty: <i>"per-signal retention that is a paid
    ///     property with a cheap default"</i>. The cheap one is the short one.
    /// </remarks>
    public const string DefaultTier = "short";

    /// <summary>How many days one signal is kept at one tier.</summary>
    /// <param name="signal">One of <see cref="Signals" />.</param>
    /// <param name="tier">One of <see cref="Tiers" />.</param>
    /// <returns>The day count, or <c>0</c> for a pair the table does not carry.</returns>
    public static int DaysOf(string signal, string tier) =>
        RetentionDays.TryGetValue(signal, out var tiers) && tiers.TryGetValue(tier, out var days)
            ? days
            : 0;

    // ── Defaults, all of them also the chart's ───────────────────────────────────────────────

    /// <summary>The default daily volume allowance for metrics, in gibibytes.</summary>
    public const int DefaultMetricsGbPerDay = 5;

    /// <summary>The default daily volume allowance for logs, in gibibytes.</summary>
    public const int DefaultLogsGbPerDay = 10;

    /// <summary>The default daily volume allowance for traces, in gibibytes.</summary>
    public const int DefaultTracesGbPerDay = 5;

    /// <summary>The default active-series ceiling.</summary>
    public const int DefaultSeriesCap = 1_000_000;

    /// <summary>The default per-metric label-value ceiling.</summary>
    public const int DefaultCardinalityCap = 20_000;

    /// <summary>The default retained fraction, in percent, once a signal is over its allowance.</summary>
    public const int DefaultOverQuotaSampleRate = 10;

    /// <summary>The lowest legal retained fraction, in percent.</summary>
    /// <remarks>
    ///     ⚠ <b>ONE, AND THE REASON IS THE PRODUCT CLAIM RATHER THAN A ROUND NUMBER.</b> docs/plan/16
    ///     § Cost and retention honesty forbids the silent drop — <i>"over-quota behaviour is
    ///     sampling with a visible rate rather than a drop"</i> — and zero is a drop spelled as a
    ///     rate. A <c>Minimum</c> of 1 is the one part of that promise the API can enforce on its
    ///     own, without anything downstream having to be built, so it is enforced there.
    /// </remarks>
    public const int MinimumOverQuotaSampleRate = 1;

    // ── The body shape ───────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     ⚠ Every default here is the chart's default, spelled as JSON — charts/README.md § The
    ///     annotation format. There is no <c>@default</c> directive, because the chart's default
    ///     <i>is</i> the YAML literal on the annotated line and <c>ChartAnnotationEmitter</c> writes
    ///     that literal from <see cref="SchemaProperty.DefaultJson" />.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the workspace is billed in, and the region whose "
                    + "telemetry stores hold its data."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The workspace's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The regional cluster whose telemetry data plane serves this "
                    + "workspace. ⚠ Nothing is deployed into it: a workspace is a tenancy in stores "
                    + "the platform already runs, and this is where its ingest routing is published."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },

                // ── Retention ────────────────────────────────────────────────────────────────
                new(
                    "/properties/retention",
                    SchemaKind.Nested,
                    Description: "How long each signal is kept. Priced per tier — docs/plan/16 "
                    + "§ Cost and retention honesty."
                ),
                new(
                    "/properties/retention/metrics",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Metrics retention tier: short is 15 days, standard 90, extended "
                    + "400. ⚠ Shortening this under an existing workspace destroys the samples "
                    + "already outside the new window, and the reconciler refuses the change rather "
                    + "than applying it."
                ) {
                    AllowedValues = Tiers,
                    Widget = WidgetHint.Sku,
                    DefaultJson = "\"short\""
                },
                new(
                    "/properties/retention/logs",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Logs retention tier: short is 7 days, standard 30, extended 90. "
                    + "⚠ Shortening this under an existing workspace destroys the lines already "
                    + "outside the new window, and the reconciler refuses the change rather than "
                    + "applying it."
                ) {
                    AllowedValues = Tiers,
                    Widget = WidgetHint.Sku,
                    DefaultJson = "\"short\""
                },
                new(
                    "/properties/retention/traces",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Traces retention tier: short is 3 days, standard 14, extended 30. "
                    + "⚠ Shortening this under an existing workspace destroys the spans already "
                    + "outside the new window, and the reconciler refuses the change rather than "
                    + "applying it."
                ) {
                    AllowedValues = Tiers,
                    Widget = WidgetHint.Sku,
                    DefaultJson = "\"short\""
                },

                // ── Quota ────────────────────────────────────────────────────────────────────
                new(
                    "/properties/quota",
                    SchemaKind.Nested,
                    Description: "What the workspace may take in per day, and what happens when it "
                    + "takes in more."
                ),
                new(
                    "/properties/quota/metricsGbPerDay",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Metric samples accepted per day, in gibibytes. This is the first "
                    + "factor of what the workspace draws against the subscription's storage quota; "
                    + "the second is the retention tier."
                ) {
                    Minimum = 1,
                    Maximum = 10_000,
                    DefaultJson = "5"
                },
                new(
                    "/properties/quota/logsGbPerDay",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Log lines accepted per day, in gibibytes."
                ) {
                    Minimum = 1,
                    Maximum = 10_000,
                    DefaultJson = "10"
                },
                new(
                    "/properties/quota/tracesGbPerDay",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Spans accepted per day, in gibibytes."
                ) {
                    Minimum = 1,
                    Maximum = 10_000,
                    DefaultJson = "5"
                },
                new(
                    "/properties/quota/seriesCap",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Active metric series the workspace may hold at once. One tenant "
                    + "putting a request id in a metric label is how a shared time-series database "
                    + "dies, so this is a ceiling rather than a guideline."
                ) {
                    Minimum = 1_000,
                    Maximum = 50_000_000,
                    DefaultJson = "1000000"
                },
                new(
                    "/properties/quota/cardinalityCap",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Distinct values one metric label may take before the series "
                    + "carrying it are rejected. The rejection names the offending label — "
                    + "docs/plan/16 § Ingest — because a rejection nobody can diagnose is one the "
                    + "client just retries."
                ) {
                    Minimum = 100,
                    Maximum = 1_000_000,
                    DefaultJson = "20000"
                },
                new(
                    "/properties/quota/overQuotaSampleRate",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "The percentage of data still accepted once a signal is over its "
                    + "daily allowance. ⚠ It cannot be zero: docs/plan/16 requires that going over "
                    + "quota samples at a visible rate rather than dropping silently, and zero is a "
                    + "silent drop spelled as a rate."
                ) {
                    Minimum = MinimumOverQuotaSampleRate,
                    Maximum = 100,
                    DefaultJson = "10"
                },

                // ── Soft delete ──────────────────────────────────────────────────────────────
                new(
                    PurgeProtectionPointer,
                    SchemaKind.Boolean,
                    Required: true,
                    Description: "Whether this workspace may be destroyed before its seven-day "
                    + "recovery window is out. Once true it stays true for the rest of the "
                    + "workspace's life, and a purge is refused while it is set."
                ) {
                    DefaultJson = "false"
                }
            ]
        );

    /// <summary>What a <c>POST …/listKeys</c> returns.</summary>
    /// <remarks>
    ///     ⚠ <b>It carries the datasource endpoints as well as the credential, and that is
    ///     docs/plan/16's <c>dataSources</c> row rather than an extra.</b> That document lists
    ///     <c>dataSources</c> as a property of the workspace — <i>"Read-only endpoints for the
    ///     tenant's own Grafana or an external one"</i> — and an endpoint the platform computes is
    ///     an output rather than a setting. A body property nobody may write is one the write path
    ///     has to refuse and the portal has to grey out; the action already exists, is already
    ///     audited, and is already the one place a tenant is told how to reach their data.
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/accountId",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The VictoriaMetrics tenant this workspace's metrics live under. It "
                    + "appears in every metrics URL below and is returned rather than left to be "
                    + "guessed at."
                ),
                new(
                    "/database",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The ClickHouse database this workspace's logs, traces and events "
                    + "live in. It is the one platform-chosen string that reaches the tenant's SQL."
                ),
                new(
                    "/otlpEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Where OTLP over gRPC is accepted, host:port."
                ),
                new(
                    "/remoteWriteEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Where Prometheus remote-write is accepted."
                ),
                new(
                    "/promqlEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The read-only PromQL/MetricsQL datasource URL, for the tenant's own "
                    + "Grafana or an external one."
                ),
                new(
                    "/sqlEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The read-only SQL datasource URL for logs, traces and events."
                ),
                new(
                    "/ingestKey",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The ingest key, read from the tenant's vault for this call only. It "
                    + "authenticates writes on every endpoint above."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ───────────────────────────────────────────────────────────────

    /// <summary>The retention tier a body asks for, for one signal.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="signal">One of <see cref="Signals" />.</param>
    public static string Tier(JsonElement desired, string signal) =>
        Text(desired, "retention", signal, DefaultTier);

    /// <summary>How many days one signal is kept, for the tier a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="signal">One of <see cref="Signals" />.</param>
    public static int Days(JsonElement desired, string signal) => DaysOf(signal, Tier(desired, signal));

    /// <summary>The daily allowance a body asks for, for one signal, in gibibytes.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="signal">One of <see cref="Signals" />.</param>
    public static int GbPerDay(JsonElement desired, string signal) =>
        Number(
            desired,
            "quota",
            signal + "GbPerDay",
            signal switch {
                Metrics => DefaultMetricsGbPerDay,
                Logs => DefaultLogsGbPerDay,
                _ => DefaultTracesGbPerDay
            }
        );

    /// <summary>The active-series ceiling a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int SeriesCap(JsonElement desired) =>
        Number(desired, "quota", "seriesCap", DefaultSeriesCap);

    /// <summary>The label-cardinality ceiling a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int CardinalityCap(JsonElement desired) =>
        Number(desired, "quota", "cardinalityCap", DefaultCardinalityCap);

    /// <summary>The retained fraction a body asks for once a signal is over its allowance.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int OverQuotaSampleRate(JsonElement desired) =>
        Number(desired, "quota", "overQuotaSampleRate", DefaultOverQuotaSampleRate);

    /// <summary>Whether the body forbids a purge before the recovery window is out.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool PurgeProtection(JsonElement desired) =>
        Root(desired, "purgeProtection") is { ValueKind: JsonValueKind.True };

    /// <summary>
    ///     The total gibibytes at rest a body's retention and allowance together entitle it to.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>THIS IS WHERE THE RETENTION SETTING REACHES THE METER, AND IT IS THE WHOLE ANSWER TO
    ///     docs/plan/16 § Cost and retention honesty.</b> That section's first failure mode is
    ///     <i>"storing everything forever"</i>, prevented by making retention <i>"a paid
    ///     property"</i> — which is only true if the number a tenant sets moves the number the
    ///     platform reserves. It is a sum over three signals of a product of two things the tenant
    ///     sets separately, and both factors are in every derivation's declared read set.
    /// </remarks>
    public static long StorageCeilingGb(JsonElement desired) {
        long total = 0;

        foreach (var signal in Signals) {
            total += (long)Days(desired, signal) * GbPerDay(desired, signal);
        }

        return total;
    }

    // ── The objects a desired body becomes ───────────────────────────────────────────────────

    /// <summary>The alphabet an ingest key is drawn from.</summary>
    /// <remarks>
    ///     ⚠ Base58-ish: no <c>0</c>, <c>O</c>, <c>I</c> or <c>l</c>. An ingest key is pasted into a
    ///     collector config by hand more often than an S3 key is, and a key whose transcription
    ///     failures are invisible is one that produces a support ticket about "ingest is broken".
    /// </remarks>
    const string KeyAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz123456789";

    /// <summary>How many characters an ingest key has.</summary>
    const int KeyLength = 48;

    /// <summary>Mints one ingest key.</summary>
    /// <remarks>
    ///     ⚠ <b>A different value on every call, deliberately.</b> A key derivable from a resource id
    ///     is a key anybody who can read the resource can compute. What reaches a rendered object is
    ///     never this value — it is whatever <c>ISecretResolver</c> returns after the mint, which is
    ///     the value the <i>first</i> pass wrote. See <c>MonitorWorkspaceReconciler</c>.
    /// </remarks>
    public static string GenerateIngestKey() =>
        RandomNumberGenerator.GetString(KeyAlphabet, KeyLength);

    /// <summary>Where a workspace's secrets live in the tenant's vault.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static string SecretPath(ResourceId id) {
        ArgumentNullException.ThrowIfNull(id.Path);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tenants/{id.TenantId:D}/{ProviderNamespace}/{TypePath}/{id.Id:D}"
        );
    }

    /// <summary>The handle that reads a workspace's ingest key back.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static SecretRef IngestKeyRef(ResourceId id) =>
        new() { Path = SecretPath(id), Field = IngestKeyField };

    /// <summary>The <c>Secret</c> document a workspace's ingest key becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="ingestKey">The key, as read back from the vault.</param>
    /// <remarks>
    ///     ⚠ <c>data</c> and not <c>stringData</c>. The convenience field is write-only — the API
    ///     server folds it into <c>data</c> and never returns it — so a read-back comparison against
    ///     <c>stringData</c> would never match and the resource would never converge.
    /// </remarks>
    public static string KeySecretJson(string name, string ingestKey) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(ingestKey);

        return new JsonObject {
            ["kind"] = SecretKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = KeySecretName(name) },
            ["type"] = "Opaque",
            ["data"] = new JsonObject {
                [IngestKeyField] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ingestKey))
            }
        }.ToJsonString();
    }

    /// <summary>The namespace the region's telemetry stack runs in.</summary>
    /// <remarks>
    ///     ⚠ <b>A platform namespace, and it is the one string in this file that is an assumption
    ///     about a deployment nothing in this repository performs.</b> The stores are installed by
    ///     the platform bundle (docs/plan/09) rather than by any resource, so this names where they
    ///     are and cannot verify it. A wrong value here produces a <c>VMUser</c> that applies
    ///     cleanly, reads back cleanly, converges, and routes to a <c>VMCluster</c> that is not
    ///     there — which vmauth reports as a bad gateway to the tenant's collector and nothing
    ///     reports to us. <c>conformance.yaml § owed</c>, <c>vmcluster-is-named-not-resolved</c>.
    /// </remarks>
    public const string TelemetryNamespace = "cybercloud-telemetry";

    /// <summary>
    ///     The <c>VMCluster</c> whose vmstorage group holds one retention tier's metrics.
    /// </summary>
    /// <param name="tier">One of <see cref="Tiers" />.</param>
    /// <remarks>
    ///     ⚠ <b>ONE CLUSTER PER TIER, WHICH IS THE ONLY OPEN-SOURCE ANSWER TO A PRICED RETENTION —
    ///     see <see cref="Tiers" /> for the source.</b> A workspace's metrics retention is therefore
    ///     a <i>routing</i> decision taken at reconcile time and not a number sent to a store, and
    ///     that is why moving a workspace between tiers is not a field edit: it is a different
    ///     target, and the samples already written stay in the group that holds them.
    /// </remarks>
    public static string MetricsClusterName(string tier) => "telemetry-" + tier;

    /// <summary>The <c>VMUser</c> document a desired body becomes.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>TWO TARGET REFS, INSERT AND SELECT, AND THE SUFFIX IS WHAT ENFORCES TENANCY.</b>
    ///         Upstream's own <c>docs/auth.md</c> example is exactly this shape — a
    ///         <c>VMCluster/vminsert</c> ref with <c>target_path_suffix: "/insert/1"</c> beside a
    ///         <c>VMCluster/vmselect</c> ref with <c>"/select/1"</c> — and its doc comment says the
    ///         suffix exists to <i>"hide tenant configuration from user"</i>. The
    ///         <c>/prometheus</c> segment after the accountID is VictoriaMetrics' cluster URL
    ///         grammar: <c>/insert/&lt;accountID&gt;/prometheus/api/v1/write</c> and
    ///         <c>/select/&lt;accountID&gt;/prometheus/api/v1/query</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THIS OBJECT IS docs/plan/16's <i>"the tenant label is injected by us and
    ///         overwrites anything the client sent"</i> FOR THE METRICS HALF, AND IT IS THE ONE
    ///         PLACE THAT RULE IS ACTUALLY ENFORCED TODAY.</b> A client cannot reach another
    ///         workspace's accountID by sending one, because it never sends one: it writes to
    ///         <c>/api/v1/write</c> and vmauth prefixes the tenant from the credential it
    ///         authenticated. ⚠ That is a property of the <b>suffix</b> and not of the credential,
    ///         which is why the suffix's spelling has its own test.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>passwordRef</c>, not <c>password</c>.</b> The credential belongs in the
    ///         <c>Secret</c> this provider already writes; inlining it would put a live ingest key
    ///         into an object that is not a <c>Secret</c>, readable by anything with a
    ///         namespace-scoped role, and into the desired-state document the drift scanner keeps.
    ///     </para>
    /// </remarks>
    public static string VmUserJson(ResourceId id, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(id.Path);

        var account = AccountId(id).ToString(CultureInfo.InvariantCulture);
        var cluster = MetricsClusterName(Tier(desired, Metrics));

        return new JsonObject {
            ["kind"] = VmUserKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = VmUserName(id.Name) },
            ["spec"] = new JsonObject {
                ["name"] = id.Name,
                ["username"] = VmUserName(id.Name),
                ["passwordRef"] = new JsonObject {
                    ["name"] = KeySecretName(id.Name),
                    ["key"] = IngestKeyField
                },
                ["targetRefs"] = new JsonArray {
                    TargetRef(cluster, "vminsert", $"/insert/{account}/prometheus"),
                    TargetRef(cluster, "vmselect", $"/select/{account}/prometheus")
                }
            }
        }.ToJsonString();
    }

    static JsonObject TargetRef(string cluster, string component, string suffix) =>
        new() {
            ["crd"] = new JsonObject {
                ["kind"] = "VMCluster/" + component,
                ["name"] = cluster,
                ["namespace"] = TelemetryNamespace
            },
            ["paths"] = new JsonArray { "/" },
            // ⚠ SNAKE_CASE, AND IT IS THE GO TAG RATHER THAN THE PROSE. See VmUserKind's remarks.
            ["target_path_suffix"] = suffix
        };

    /// <summary>The <c>ConfigMap</c> document a workspace's tenancy becomes.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE INGEST MAP ROW. It is the product of this resource type.</b> Everything the
    ///         data plane needs to accept, label, cap and route one tenant's telemetry is here, in
    ///         one object, in a form something that is not an Orleans client can read — which is the
    ///         constraint the whole design turns on. Nothing reads it yet;
    ///         <c>conformance.yaml § owed</c>, <c>nothing-consumes-the-row</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The retention is written as DAYS and not as the tier name, and that matters.</b>
    ///         The tier is the priced thing and the day count is the operational thing; a data plane
    ///         that had to know the tier table would be a second copy of
    ///         <see cref="RetentionDays" />, and the two would disagree the first time either moved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It names its key's <c>Secret</c> and does not carry the key.</b> A
    ///         <c>ConfigMap</c> is world-readable to anything with a namespace-scoped role, so the
    ///         credential stays in the <c>Secret</c> and the row carries the name it is under. That
    ///         is also why the <c>Secret</c> is applied first — see the reconciler.
    ///     </para>
    /// </remarks>
    public static string RowJson(ResourceId id, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(id.Path);

        return new JsonObject {
            ["kind"] = ConfigMapKind.Kind,
            ["metadata"] = new JsonObject {
                ["name"] = RowName(id.Name),
                ["labels"] = new JsonObject { [RowLabel] = RowLabelValue }
            },
            ["data"] = new JsonObject {
                ["tenantId"] = id.TenantId.ToString("D", CultureInfo.InvariantCulture),
                ["workspaceId"] = id.Id.ToString("D", CultureInfo.InvariantCulture),
                ["workspace"] = id.Name,
                ["accountId"] = AccountId(id).ToString(CultureInfo.InvariantCulture),
                ["database"] = Database(id),
                ["ingestKeySecret"] = KeySecretName(id.Name),
                ["retentionMetricsDays"] = Text(Days(desired, Metrics)),
                ["retentionLogsDays"] = Text(Days(desired, Logs)),
                ["retentionTracesDays"] = Text(Days(desired, Traces)),
                ["quotaMetricsGbPerDay"] = Text(GbPerDay(desired, Metrics)),
                ["quotaLogsGbPerDay"] = Text(GbPerDay(desired, Logs)),
                ["quotaTracesGbPerDay"] = Text(GbPerDay(desired, Traces)),
                ["seriesCap"] = Text(SeriesCap(desired)),
                ["cardinalityCap"] = Text(CardinalityCap(desired)),
                ["overQuotaBehaviour"] = "sample",
                ["overQuotaSampleRate"] = Text(OverQuotaSampleRate(desired))
            }
        }.ToJsonString();
    }

    // ── Reading an object back ───────────────────────────────────────────────────────────────

    /// <summary>Whether an object read out of the cluster carries what a desired body asked for.</summary>
    /// <param name="objectJson">The object, as the API server returned it.</param>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CONTAINMENT, NOT EQUALITY — and for a reason none of the eleven families before
    ///         this one gives.</b> Theirs are structural defaulting by a CRD, rewriting by a mutating
    ///         webhook, and a controller writing back into <c>.spec</c>. There is no CRD here and no
    ///         operator at all: both kinds are core. What forces containment is the <b>API server
    ///         itself</b>. A <c>ConfigMap</c> and a <c>Secret</c> read back carry
    ///         <c>metadata.creationTimestamp</c>, <c>metadata.uid</c>, <c>metadata.resourceVersion</c>,
    ///         <c>metadata.managedFields</c> and the seven labels <c>KubeCommandBuilder</c> injects,
    ///         none of which the render writes — so an equality comparison never matches on the
    ///         first read-back and the resource never converges. ⚠ <b>And unlike the CRD-defaulting
    ///         sightings, the conformance harness is NOT blind to this one</b>: <c>FakeKubeCluster</c>
    ///         echoes an apply back, but <c>KubeCommandBuilder</c> has already added the labels by
    ///         then, so the mistake goes red in both halves of the suite.
    ///         <c>MonitorMatchesTests.AnObjectCarryingWhatAnApiServerAddsStillMatches</c> runs it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>ONE <c>Matches</c> OVER TWO KINDS, so a rendered document names its own kind</b>
    ///         — the shape <c>ClickHouseClusters</c> established. A <c>Matches</c> that defaulted to
    ///         <see langword="true" /> for a document it did not recognise would report a
    ///         <c>Secret</c> that was never applied as converged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>Secret</c> is checked for PRESENCE and not for VALUE.</b> The key is
    ///         minted once and read back from the vault on every pass, so its value is stable — but
    ///         comparing it here would mean this method taking a credential as an argument, and a
    ///         comparison is not a reason to move one. What a wrong <c>Secret</c> looks like from
    ///         here is an absent field, and that is what is checked.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, ResourceId id, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(objectJson);

        if (JsonNode.Parse(objectJson) is not JsonObject document) {
            return false;
        }

        // ⚠ `null` is the render's own shape before KubeCommandBuilder injects the kind — the
        // renders write it themselves, so this branch is for a body that has been neither.
        return document["kind"]?.GetValue<string>() switch {
            "ConfigMap" => MatchesRow(document, id, desired),
            "Secret" => MatchesKeySecret(document),
            "VMUser" => MatchesVmUser(document, id, desired),
            _ => false
        };
    }

    /// <summary>
    ///     Whether an object carries everything a desired body asked for that does <b>not</b> depend
    ///     on which workspace it is.
    /// </summary>
    /// <param name="objectJson">The object, as the API server returned it.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A DELIBERATELY WEAKER COMPARISON, AND IT EXISTS BECAUSE
    ///         <c>ProviderConformanceCase.ObjectMatchesDesired</c> IS HANDED NO ADDRESS.</b> That
    ///         member's signature is <c>(objectJson, desiredJson)</c> — the limit
    ///         <c>StorageBuckets</c> records and <c>AgentPools</c> demonstrated — and every identity
    ///         this type renders is keyed on the resource's own GUID: the <c>accountID</c> in the
    ///         <c>VMUser</c>'s path suffix, the database name, all three object names. So the shared
    ///         suite <i>cannot</i> check them, and the honest response is a second method that says
    ///         which half it checks rather than a first method quietly given a made-up address.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this does NOT catch, therefore, is the worst bug this type can have:</b> a
    ///         render that put every workspace on one <c>accountID</c>, which is every tenant reading
    ///         every other tenant's metrics. <c>MonitorReconcilerTests
    ///         .TwoWorkspacesInTwoTenantsGetTwoAccountIdsAndTwoDatabases</c> is what catches that, and
    ///         it is a hand-written test for exactly this reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The reconciler must never call this one.</b> <see cref="Matches" /> is what
    ///         decides convergence, and convergence on a comparison that ignores identity would let a
    ///         workspace converge onto another workspace's routing.
    ///     </para>
    /// </remarks>
    public static bool MatchesShape(string objectJson, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(objectJson);

        if (JsonNode.Parse(objectJson) is not JsonObject document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "ConfigMap" => MatchesRowShape(document, desired),
            "Secret" => MatchesKeySecret(document),
            "VMUser" => MatchesVmUserShape(document, desired),
            _ => false
        };
    }

    /// <summary>The row's address-independent half: retention, allowances, caps and the rate.</summary>
    static bool MatchesRowShape(JsonObject document, JsonElement desired) {
        if (document["data"] is not JsonObject data) {
            return false;
        }

        foreach (var (signal, days, allowance) in new[] {
                     (Metrics, "retentionMetricsDays", "quotaMetricsGbPerDay"),
                     (Logs, "retentionLogsDays", "quotaLogsGbPerDay"),
                     (Traces, "retentionTracesDays", "quotaTracesGbPerDay")
                 }) {
            if (data[days]?.GetValue<string>() != Text(Days(desired, signal))
                || data[allowance]?.GetValue<string>() != Text(GbPerDay(desired, signal))) {
                return false;
            }
        }

        return data["seriesCap"]?.GetValue<string>() == Text(SeriesCap(desired))
            && data["cardinalityCap"]?.GetValue<string>() == Text(CardinalityCap(desired))
            && data["overQuotaBehaviour"]?.GetValue<string>() == "sample"
            && data["overQuotaSampleRate"]?.GetValue<string>() == Text(OverQuotaSampleRate(desired));
    }

    /// <summary>
    ///     The two directions a target-path suffix may pin, in the order
    ///     <see cref="VmUserJson" /> renders them.
    /// </summary>
    /// <remarks>
    ///     ⚠ The order is load-bearing: the first target ref is the write path and the second is the
    ///     read path, so a rendering that swapped them would give a workspace read access where it
    ///     asked for write and vice versa. VictoriaMetrics' cluster grammar spells them
    ///     <c>/insert/&lt;accountID&gt;/prometheus</c> and <c>/select/&lt;accountID&gt;/prometheus</c>.
    /// </remarks>
    static readonly string[] SuffixDirections = ["/insert/", "/select/"];

    /// <summary>
    ///     The routing's address-independent half: the tier it targets, and that both suffixes are
    ///     the tenancy-pinning grammar rather than a bare path.
    /// </summary>
    static bool MatchesVmUserShape(JsonObject document, JsonElement desired) {
        if (document["spec"] is not JsonObject spec
            || spec["targetRefs"] is not JsonArray targets
            || targets.Count != 2) {
            return false;
        }

        var cluster = MetricsClusterName(Tier(desired, Metrics));

        foreach (var (target, prefix) in targets.Zip(SuffixDirections)) {
            if (target is not JsonObject reference
                || reference["crd"]?["name"]?.GetValue<string>() != cluster
                || reference["target_path_suffix"]?.GetValue<string>() is not { } suffix
                // ⚠ The GRAMMAR, not the value: the account is the half this comparison cannot see.
                // A suffix that had lost its `/prometheus` tail, or its direction, is a workspace
                // whose writes vmauth forwards to a path vminsert does not serve.
                || !suffix.StartsWith(prefix, StringComparison.Ordinal)
                || !suffix.EndsWith("/prometheus", StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    static bool MatchesRow(JsonObject document, ResourceId id, JsonElement desired) {
        if (document["data"] is not JsonObject data) {
            return false;
        }

        if (JsonNode.Parse(RowJson(id, desired)) is not JsonObject rendered
            || rendered["data"] is not JsonObject expected) {
            return false;
        }

        foreach (var (key, value) in expected) {
            if (data[key]?.GetValue<string>() != value?.GetValue<string>()) {
                return false;
            }
        }

        return true;
    }

    static bool MatchesKeySecret(JsonObject document) =>
        document["data"] is JsonObject data
        && data[IngestKeyField]?.GetValue<string>() is { Length: > 0 };

    /// <summary>Whether a <c>VMUser</c> routes this workspace's account to this workspace's tier.</summary>
    /// <remarks>
    ///     ⚠ <b>BOTH TARGET REFS, AND THE SUFFIX ON EACH.</b> A comparison that checked only the
    ///     cluster name would pass for a workspace whose select suffix still names the accountID it
    ///     had before a rename of anything — and reading somebody else's metrics is the failure this
    ///     object exists to prevent, so its read-back is the one that must not be lenient.
    /// </remarks>
    static bool MatchesVmUser(JsonObject document, ResourceId id, JsonElement desired) {
        if (JsonNode.Parse(VmUserJson(id, desired)) is not JsonObject rendered
            || rendered["spec"] is not JsonObject expected
            || document["spec"] is not JsonObject actual) {
            return false;
        }

        if (actual["username"]?.GetValue<string>() != expected["username"]?.GetValue<string>()) {
            return false;
        }

        if (actual["passwordRef"] is not JsonObject reference
            || reference["name"]?.GetValue<string>() != KeySecretName(id.Name)
            || reference["key"]?.GetValue<string>() != IngestKeyField) {
            return false;
        }

        if (actual["targetRefs"] is not JsonArray targets
            || expected["targetRefs"] is not JsonArray wanted
            || targets.Count != wanted.Count) {
            return false;
        }

        for (var index = 0; index < wanted.Count; index++) {
            if (targets[index] is not JsonObject target
                || wanted[index] is not JsonObject want
                || target["target_path_suffix"]?.GetValue<string>()
                != want["target_path_suffix"]?.GetValue<string>()
                || target["crd"] is not JsonObject targetCrd
                || want["crd"] is not JsonObject wantCrd
                || targetCrd["kind"]?.GetValue<string>() != wantCrd["kind"]?.GetValue<string>()
                || targetCrd["name"]?.GetValue<string>() != wantCrd["name"]?.GetValue<string>()) {
                return false;
            }
        }

        return true;
    }

    // ── Endpoints ────────────────────────────────────────────────────────────────────────────

    /// <summary>The in-cluster host the region's ingest gateway answers on.</summary>
    /// <remarks>
    ///     ⚠ <b>A platform address rather than a per-workspace one, which is the architecture read
    ///     off the endpoint.</b> Every workspace in a region shares this host and is separated by its
    ///     key and its <c>accountID</c>. A per-workspace hostname would mean a per-workspace
    ///     listener, which is the deployment-shaped reading this type refuses.
    /// </remarks>
    public const string IngestHost = "ingest.cybercloud.svc";

    /// <summary>The in-cluster host the region's query gateway answers on.</summary>
    public const string QueryHost = "telemetry.cybercloud.svc";

    /// <summary>Where OTLP over gRPC is accepted.</summary>
    public const string OtlpEndpoint = IngestHost + ":4317";

    /// <summary>Where Prometheus remote-write is accepted, for one workspace.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static string RemoteWriteEndpoint(ResourceId id) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"https://{IngestHost}/insert/{AccountId(id)}/prometheus/api/v1/write"
        );

    /// <summary>The read-only PromQL datasource, for one workspace.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static string PromqlEndpoint(ResourceId id) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"https://{QueryHost}/select/{AccountId(id)}/prometheus"
        );

    /// <summary>The read-only SQL datasource for logs, traces and events, for one workspace.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static string SqlEndpoint(ResourceId id) =>
        string.Create(CultureInfo.InvariantCulture, $"https://{QueryHost}/sql/{Database(id)}");

    // ── A body, for tests, the conformance case and the chart ────────────────────────────────

    /// <summary>A valid body at <see cref="V2026" />.</summary>
    /// <param name="clusterId">The regional cluster this workspace's routing is published in.</param>
    /// <param name="metricsTier">The metrics retention tier.</param>
    /// <param name="logsTier">The logs retention tier.</param>
    /// <param name="tracesTier">The traces retention tier.</param>
    /// <param name="logsGbPerDay">The daily log allowance, in gibibytes.</param>
    /// <param name="overQuotaSampleRate">The retained fraction once over quota, in percent.</param>
    /// <param name="purgeProtection">Whether a purge is refused for the whole recovery window.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        string metricsTier = DefaultTier,
        string logsTier = DefaultTier,
        string tracesTier = DefaultTier,
        int logsGbPerDay = DefaultLogsGbPerDay,
        int overQuotaSampleRate = DefaultOverQuotaSampleRate,
        bool purgeProtection = false,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["retention"] = new JsonObject {
                    ["metrics"] = metricsTier,
                    ["logs"] = logsTier,
                    ["traces"] = tracesTier
                },
                ["quota"] = new JsonObject {
                    ["metricsGbPerDay"] = DefaultMetricsGbPerDay,
                    ["logsGbPerDay"] = logsGbPerDay,
                    ["tracesGbPerDay"] = DefaultTracesGbPerDay,
                    ["seriesCap"] = DefaultSeriesCap,
                    ["cardinalityCap"] = DefaultCardinalityCap,
                    ["overQuotaSampleRate"] = overQuotaSampleRate
                },
                ["purgeProtection"] = purgeProtection
            }
        }.ToJsonString();

    // ── Reading JSON ─────────────────────────────────────────────────────────────────────────

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    static JsonElement? Root(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var member)
            ? member
            : null;

    static string Text(JsonElement desired, string parent, string name, string fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static int Number(JsonElement desired, string parent, string name, int fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var number)
            ? number
            : fallback;
}
