using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Monitor/workspaces/collectors</c>: the type, its
///     body shape, the collector image it runs, and the <b>three</b> Kubernetes objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/16 § OTel Collector as a service · <b>M2 · 1.0 EM</b>:
///         <i>
///             "A tenant-owned
///             collector deployment in their cluster, configured declaratively"
///         </i>, with
///         <i>
///             "the
///             cybercloud exporter … pre-wired to the tenant's workspace"
///         </i>. Issue #32's second noun
///         of four. A tenant points their own workloads at the endpoint <c>listEndpoints</c> hands
///         back and the collector carries the telemetry into the workspace the collector hangs off.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A CHILD OF THE WORKSPACE, AND THE FIRST CHILD IN THIS FAMILY THAT RUNS A POD.
///         </b> The workspace is a tenancy and runs nothing; the alert rule applies nothing; this is
///         the object in the family that a kubelet schedules. It is a child rather than a top-level
///         type with a <c>workspace</c> property for the reason <c>AgentPools</c> gives — the
///         address names the workspace and a body property would be a second spelling of the same
///         fact — and because the one thing a collector is <i>for</i> is the workspace it feeds.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             THE WORKSPACE'S COORDINATES ARE NOT IN ANY RENDERED DOCUMENT, AND THAT IS THE
///             DESIGN RATHER THAN A GAP.
///         </b> The exporters address the workspace's stores exactly as the workspace's own
///         <c>listKeys</c> does — <see cref="MonitorWorkspaces.RemoteWriteEndpoint(string)" /> under
///         its <c>accountID</c>, <see cref="MonitorWorkspaces.SqlEndpoint(string)" /> under its
///         database, authenticated as its <c>VMUser</c> with its ingest key — but a child's
///         reconcile pass never learns its parent's GUID, and the accountID is a fold of that GUID.
///         So the rendered configuration writes <c>${env:CYBERCLOUD_ACCOUNT_ID}</c> where the
///         accountID goes and the <c>Deployment</c> hands the pod that variable from the workspace's
///         own ingest row, by <c>configMapKeyRef</c>, with the key from the workspace's own
///         <c>Secret</c> by <c>secretKeyRef</c> — <see cref="MonitorWorkspaces.WorkspaceEnv" />
///         carries the argument. The collector therefore has to run in the cluster its workspace
///         publishes into; <c>conformance.yaml § owed</c>,
///         <c>collector-runs-where-its-workspace-is-published</c>.
///     </para>
///     <para>
///         ⚠ <b>THE CONFIGURATION IS RENDERED, NOT ACCEPTED.</b> docs/plan/16 describes a tenant
///         declaring <c>receivers: [otlp, prometheus, filelog, kubeletstats]</c> and their own
///         exporters, validated against an allow-list, because
///         <i>
///             "an arbitrary collector config is a
///             data-exfiltration primitive and a code-execution surface"
///         </i>. This version offers the
///         allow-list's safest subset as typed properties — which OTLP protocols to listen on — and
///         renders the whole file itself, so there is no config to validate and no exporter a tenant
///         can point elsewhere. That is the smaller product and the honest one to ship first; the
///         declarative surface and tenant-owned exporters are <c>conformance.yaml § owed</c>,
///         <c>tenant-authored-config-is-not-accepted</c>.
///     </para>
///     <para>
///         ⚠ <b>THE IMAGE IS PINNED BY DIGEST IN THE BUNDLE'S SHAPE.</b> <c>repository:tag@sha256:…</c>,
///         resolved against Docker Hub on 2026-09-17 and recorded in
///         <c>charts/managed/monitor-collector/values.yaml</c> beside the same pin, which
///         <c>CollectorDeclarationTests</c> compares to this constant — the shape
///         <c>charts/bundle/*/component.yaml § images</c> uses, so <c>charts/bundle/images.sh</c>'s
///         drift question can one day be asked of it. A tag alone is a kubelet pulling whatever
///         upstream rebuilt last night into a tenant's namespace; <c>CyberCloud.Network/…/loadBalancers</c>
///         records that as owed and this type does not repeat it. ⚠ It is a workload image and not a
///         bundle component, so <c>Build.Licence</c> does not read it yet; the image's own
///         <c>org.opencontainers.image.licenses</c> label reads <c>Apache-2.0</c>, which is on
///         ADR-011's allow-list without an argument.
///     </para>
/// </remarks>
public static class MonitorCollectors {
    /// <summary>The type's path under <see cref="MonitorWorkspaces.ProviderNamespace" />.</summary>
    /// <remarks>
    ///     Interleaved with its parent: <c>…/workspaces/{workspace}/collectors/{name}</c>, the shape
    ///     <c>AgentPools</c> established.
    /// </remarks>
    public const string TypePath = "workspaces/collectors";

    /// <summary>The chart that renders the same three objects — docs/plan/12 § The pattern, once.</summary>
    public const string ChartName = "managed/monitor-collector";

    /// <summary>Where the body names the cluster the collector runs in.</summary>
    /// <remarks>
    ///     ⚠ <b>The workspace's cluster, in practice, and nothing checks that.</b> The
    ///     <c>Deployment</c> reads the workspace's row and ingest key by name from its own namespace,
    ///     so a collector placed in a cluster the workspace never published into is a pod the kubelet
    ///     holds in <c>CreateContainerConfigError</c> naming the missing <c>ConfigMap</c>. The
    ///     reconciler cannot see the workspace's <c>clusterId</c> — a child's pass reads its own body
    ///     — and the message the kubelet writes names the object, which is the diagnosis.
    /// </remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that reports where workloads send their telemetry.</summary>
    public const string ListEndpointsAction = "listEndpoints";

    /// <summary>The permission <see cref="ListEndpointsAction" /> needs — an endpoint is not a secret.</summary>
    public const string ListEndpointsPermission = "read";

    /// <summary>The type, as the registry names it.</summary>
    public static ResourceTypeName Type { get; } = new(MonitorWorkspaces.ProviderNamespace, TypePath);

    // ── The image ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The collector image's repository — upstream's contrib distribution, unmodified.</summary>
    /// <remarks>
    ///     <c>contrib</c> rather than the core distribution because the two exporters this renders —
    ///     <c>prometheusremotewrite</c> and <c>clickhouse</c> — and the <c>basicauth</c> extension are
    ///     contrib components; the core image carries neither exporter.
    /// </remarks>
    public const string ImageRepository = "otel/opentelemetry-collector-contrib";

    /// <summary>The release the digest was resolved for.</summary>
    public const string ImageTag = "0.161.0";

    /// <summary>
    ///     The manifest-list digest Docker Hub served for <see cref="ImageTag" /> on 2026-09-17.
    /// </summary>
    /// <remarks>
    ///     ⚠ The <b>index</b> digest and not one platform's manifest, so the same reference pulls on
    ///     amd64 and arm64 nodes. <c>docker buildx imagetools inspect</c> prints it as the first
    ///     <c>Digest:</c> line; the per-platform lines under it are what a kubelet resolves through it.
    /// </remarks>
    public const string ImageDigest = "sha256:fd328de2552466ad78385e1b1289c3f2402b1c45f265b252aab1955b42845ac1";

    /// <summary>The image reference the <c>Deployment</c> carries.</summary>
    public const string Image = ImageRepository + ":" + ImageTag + "@" + ImageDigest;

    /// <summary>The uid the image's <c>USER</c> instruction sets, read off its config on 2026-09-17.</summary>
    /// <remarks>
    ///     ⚠ Written explicitly for the reason <c>charts/managed/haproxy</c> records: the image
    ///     declares <c>10001:10001</c> numerically, so <c>runAsNonRoot</c> alone would work here, and
    ///     it is spelled anyway so a rebuilt image that switched to a named user cannot change who
    ///     the collector runs as without a diff in this file.
    /// </remarks>
    public const int CollectorUid = 10001;

    /// <summary>Where the image's own <c>CMD</c> reads its configuration from.</summary>
    /// <remarks>
    ///     <c>--config /etc/otelcol-contrib/config.yaml</c>, read off the image config. Mounting the
    ///     <c>ConfigMap</c> over that directory means no <c>args</c> override and no second spelling
    ///     of the path.
    /// </remarks>
    public const string ConfigDirectory = "/etc/otelcol-contrib";

    /// <summary>The configuration file's name inside <see cref="ConfigDirectory" />.</summary>
    public const string ConfigFile = "config.yaml";

    // ── Ports ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>OTLP over gRPC — the port the image exposes and upstream's default.</summary>
    public const int OtlpGrpcPort = 4317;

    /// <summary>OTLP over HTTP — the port the image exposes and upstream's default.</summary>
    public const int OtlpHttpPort = 4318;

    /// <summary>The <c>health_check</c> extension's port, which the readiness probe asks.</summary>
    public const int HealthPort = 13133;

    // ── Sizing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The preset a body gets when it names none.</summary>
    public const string DefaultPreset = "c1.small";

    /// <summary>What each preset gives the collector pod.</summary>
    /// <remarks>
    ///     ⚠ <b>The same table is in the chart</b>, and <c>CollectorDeclarationTests</c> compares it
    ///     row for row — two spellings of a sizing table is a resource that reserves one quantity and
    ///     runs another. The <c>memory_limiter</c> processor is written as a percentage of the limit
    ///     rather than in mebibytes, so the table is the one place a size is spelled.
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal) {
            ["c1.small"] = ("250m", "256Mi"), ["c1.medium"] = ("500m", "512Mi"), ["c1.large"] = ("1", "1Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>What a body's preset costs.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static (string Cpu, string Memory) Resources(JsonElement desired) =>
        Presets.TryGetValue(Preset(desired), out var chosen) ? chosen : Presets[DefaultPreset];

    /// <summary>The most replicas a body may ask for.</summary>
    /// <remarks>
    ///     Three, because the collector is stateless behind a <c>ClusterIP</c> and a tenant's
    ///     workloads spread across it evenly; a larger fan-in is a bigger preset first. A tenant who
    ///     needs a collector per node needs docs/plan/16's <c>daemonset</c> mode, which is owed.
    /// </remarks>
    public const int MaxReplicas = 3;

    // ── The kinds ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The rendered collector configuration.</summary>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>The collector itself.</summary>
    public static GroupVersionKind DeploymentKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    /// <summary>The stable address workloads send to.</summary>
    public static GroupVersionKind ServiceKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Service", Plural = "services" };

    // ── Names ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The name all three objects take: the workspace's name and the collector's own, joined.
    /// </summary>
    /// <param name="id">The collector's address.</param>
    /// <remarks>
    ///     ⚠ <b>The workspace's name is in it because the namespace does not distinguish them</b> —
    ///     <c>AgentPools.ObjectNameOf</c>'s argument: two workspaces in one resource group may each
    ///     hold a collector called <c>gateway</c>. The <c>collector-</c> prefix keeps it clear of the
    ///     workspace's own <c>monitor-{name}</c> objects in the same namespace.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string ObjectNameOf(ResourceId id) => "collector-" + WorkspaceNameOf(id) + "-" + id.Name;

    /// <summary>The workspace a collector feeds, which is its parent's own name.</summary>
    /// <param name="id">The collector's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string WorkspaceNameOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no workspace for this collector to feed. A "
            + "collector is a child type and its address always interleaves its workspace — see "
            + "MonitorCollectors.TypePath.",
            nameof(id)
        );

    /// <summary>The <c>ConfigMap</c> a collector owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The collector's address.</param>
    public static ObjectRef ConfigMapRef(string ns, ResourceId id) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The <c>Deployment</c> a collector owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The collector's address.</param>
    public static ObjectRef DeploymentRef(string ns, ResourceId id) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The <c>Service</c> a collector owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The collector's address.</param>
    public static ObjectRef ServiceRef(string ns, ResourceId id) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>Every object a collector owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The collector's address.</param>
    /// <remarks>
    ///     Configuration first, so the pod never mounts a volume that is not there yet; the
    ///     <c>Service</c> last, so nothing is addressable before there is something to address.
    /// </remarks>
    public static ImmutableArray<ObjectRef> Objects(string ns, ResourceId id) =>
        [ConfigMapRef(ns, id), DeploymentRef(ns, id), ServiceRef(ns, id)];

    /// <summary>The pod-template annotation carrying the configuration's hash.</summary>
    /// <remarks>
    ///     A <c>ConfigMap</c> edit does not restart a pod on its own; the hash on the template is what
    ///     makes a configuration change a rollout — <c>LoadBalancers.ConfigChecksumAnnotation</c>'s
    ///     reason, verbatim.
    /// </remarks>
    public const string ConfigChecksumAnnotation = "cybercloud.io/collector-config";

    /// <summary>The label the <c>Deployment</c> selects its pods by, and the <c>Service</c> selects them by.</summary>
    public const string NameLabel = "app.kubernetes.io/name";

    /// <summary>The label distinguishing one collector's pods from another's in the same namespace.</summary>
    public const string InstanceLabel = "app.kubernetes.io/instance";

    /// <summary>The value <see cref="NameLabel" /> carries.</summary>
    public const string NameLabelValue = "opentelemetry-collector";

    // ── The body ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="MonitorWorkspaces.V2026" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Two booleans and not an array of receiver names</b>, although docs/plan/16 sketches
    ///     the array. An array of text is representable; what a receiver <i>is</i> — which port,
    ///     which protocol, which processors it needs in front of it — is not a string, and a tenant
    ///     who can name <c>filelog</c> can name a path on the node. The two protocols OTLP has are
    ///     the surface; each is a switch.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the collector is billed in — its workspace's."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The collector's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the collector runs in. ⚠ It must be the cluster its "
                    + "workspace publishes into: the collector reads the workspace's accountID, "
                    + "database and ingest key from the workspace's own objects in the same namespace, "
                    + "and in any other cluster the pod waits on a ConfigMap that is not there."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/receivers",
                    SchemaKind.Nested,
                    Description: "Which OTLP protocols the collector listens on. At least one; both is the default."
                ),
                new(
                    "/properties/receivers/otlpGrpc",
                    SchemaKind.Boolean,
                    Description: "Accept OTLP over gRPC on port 4317 — what most SDKs send by default."
                ) { DefaultJson = "true" },
                new(
                    "/properties/receivers/otlpHttp",
                    SchemaKind.Boolean,
                    Description: "Accept OTLP over HTTP on port 4318 — protobuf or JSON, for browsers "
                    + "and anything that cannot speak gRPC."
                ) { DefaultJson = "true" },
                new(
                    "/properties/replicas",
                    SchemaKind.WholeNumber,
                    Description: "How many collector pods share the endpoint. The collector is "
                    + "stateless, so more replicas is more fan-in and nothing else."
                ) { Minimum = 1, Maximum = MaxReplicas, DefaultJson = "1" },
                new("/properties/sizing", SchemaKind.Nested, Description: "CPU and memory for each collector pod."),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "How much each pod gets. The small row carries a few thousand spans "
                    + "a second; the larger rows are for a whole cluster's telemetry through one "
                    + "gateway. The memory limiter is set from the preset, so an oversized burst is "
                    + "refused rather than killed."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"" + DefaultPreset + "\""
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    /// <summary>What a <c>listEndpoints</c> returns.</summary>
    /// <remarks>
    ///     ⚠ <b>Both endpoints are always present and a disabled one is the empty string</b>, so a
    ///     client can bind to the response shape once rather than probing for a property. Nothing
    ///     here is secret: the collector authenticates its <i>exports</i> to the workspace, and a
    ///     workload inside the cluster reaches the collector's <c>ClusterIP</c> without a key —
    ///     <c>conformance.yaml § owed</c>, <c>collector-ingress-is-unauthenticated</c>.
    /// </remarks>
    public static ResourceSchema ListEndpointsResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/otlpGrpcEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Where OTLP over gRPC is accepted inside the cluster, host:port — "
                    + "empty when the gRPC receiver is off."
                ),
                new(
                    "/otlpHttpEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Where OTLP over HTTP is accepted inside the cluster, as a URL — "
                    + "empty when the HTTP receiver is off. Signals go to /v1/traces, /v1/metrics and "
                    + "/v1/logs under it."
                ),
                new(
                    "/service",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The Service's in-cluster DNS name, for a workload that builds its own URL."
                ),
                new(
                    "/workspace",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The workspace everything sent here lands in."
                )
            ]
        );

    // ── Reading a body ────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the body asks for the gRPC receiver.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool OtlpGrpc(JsonElement desired) => Flag(desired, "receivers", "otlpGrpc", true);

    /// <summary>Whether the body asks for the HTTP receiver.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool OtlpHttp(JsonElement desired) => Flag(desired, "receivers", "otlpHttp", true);

    /// <summary>How many pods the body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Replicas(JsonElement desired) => Whole(desired, "replicas", 1);

    /// <summary>The sizing preset the body asks for, or the default.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Preset(JsonElement desired) =>
        Nested(desired, "sizing", "preset") is { Length: > 0 } preset && Presets.ContainsKey(preset)
            ? preset
            : DefaultPreset;

    /// <summary>
    ///     What is wrong with a body the schema accepted, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>The one thing the schema cannot say: at least one of two booleans.</b>
    ///     <c>ResourceSchema</c> validates properties one at a time, so a collector with both receivers
    ///     off is a body the API accepts and a pod with nothing to listen on. The reconciler refuses
    ///     it by name rather than starting a collector that exports nothing — the sixth sighting of
    ///     the cross-property limit <c>MonitorWorkspaceReconciler</c> counts, on the cheapest possible
    ///     shape.
    /// </remarks>
    public static string? ReceiverProblem(JsonElement desired) =>
        OtlpGrpc(desired) || OtlpHttp(desired)
            ? null
            : "Both receivers are off, so the collector would listen on nothing and export nothing. "
            + "Turn on receivers.otlpGrpc, receivers.otlpHttp, or both.";

    // ── The rendered configuration ────────────────────────────────────────────────────────────

    /// <summary>The collector's configuration file, as YAML.</summary>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Deterministic, byte for byte, or the pod rolls on every reminder</b> — the hash
    ///         of this text is on the pod template. Nothing here reads a clock, a counter or a map
    ///         in iteration order.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>${env:…}</c> is the collector's own substitution syntax and it happens at
    ///             start
    ///         </b>, so the accountID, the database and the key never appear in this text, in
    ///         the <c>ConfigMap</c>, or in the desired-state document the drift scanner keeps. The
    ///         basic-auth <i>username</i> is spelled in full because it is the workspace's
    ///         <c>VMUser</c> name, a pure function of the workspace's name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>memory_limiter</c> first, <c>batch</c> second, in every pipeline.</b> Upstream
    ///         is explicit that the limiter must be the first processor or it protects nothing, and
    ///         the batch is what makes an export failure asynchronous: a receiver in front of a
    ///         failing exporter with no batch answers the client's export with the exporter's
    ///         error, and a tenant's SDK then retries into a collector that is up.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The ClickHouse exporter is given the query host and the database as two settings,
    ///             not <see cref="MonitorWorkspaces.SqlEndpoint(string)" />'s one URL — measured against
    ///             the real image on 2026-09-17.
    ///         </b> clickhouse-go reads a DSN's <i>path</i> as the
    ///         database name, so <c>https://telemetry.cybercloud.svc/sql/ws_x</c> became
    ///         <c>database=sql%2Fws_x</c> in the first request the collector sent. The host is the
    ///         workspace's query host and the database is the workspace's; the <c>/sql/</c> path is
    ///         the gateway's grammar for Grafana's plugin, which has a <c>path</c> setting of its own.
    ///         The same run renamed <c>prometheusremotewrite</c> to <c>prometheus_remote_write</c>:
    ///         the old spelling is a deprecated alias at 0.161.0 and logs a warning per start.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>create_schema: false</c> on the ClickHouse exporter.</b> The database is the
    ///         workspace's tenancy and the platform owns its tables (docs/plan/16 § Ingest routes
    ///         to <i>"ClickHouse (per-tenant database)"</i>); a collector that ran <c>CREATE TABLE</c>
    ///         under a tenant's credential would decide the schema of a store it does not own. What
    ///         the exporter expects to find there is <c>conformance.yaml § owed</c>,
    ///         <c>collector-clickhouse-tables-are-the-exporters-shape</c>.
    ///     </para>
    /// </remarks>
    public static string CollectorConfig(ResourceId id, JsonElement desired) {
        var workspace = WorkspaceNameOf(id);
        var user = MonitorWorkspaces.VmUserName(workspace);
        var builder = new StringBuilder();

        builder.Append(
            CultureInfo.InvariantCulture,
            $"""
            # Generated by CyberCloud from CyberCloud.Monitor/workspaces/collectors.
            # Edits are overwritten on the next reconcile pass.
            receivers:
              otlp:
                protocols:

            """
        );

        if (OtlpGrpc(desired)) {
            builder.Append(CultureInfo.InvariantCulture, $"      grpc:\n        endpoint: 0.0.0.0:{OtlpGrpcPort}\n");
        }

        if (OtlpHttp(desired)) {
            builder.Append(CultureInfo.InvariantCulture, $"      http:\n        endpoint: 0.0.0.0:{OtlpHttpPort}\n");
        }

        builder.Append(
            CultureInfo.InvariantCulture,
            $$"""
            processors:
              memory_limiter:
                check_interval: 1s
                limit_percentage: 80
                spike_limit_percentage: 20
              batch:
                timeout: 5s
                send_batch_size: 8192
            exporters:
              prometheus_remote_write:
                endpoint: {{MonitorWorkspaces.RemoteWriteEndpoint("${env:" + MonitorWorkspaces.EnvAccountId + "}")}}
                auth:
                  authenticator: basicauth/workspace
              clickhouse:
                endpoint: https://{{MonitorWorkspaces.QueryHost}}
                database: ${env:{{MonitorWorkspaces.EnvDatabase}}}
                username: {{user}}
                password: ${env:{{MonitorWorkspaces.EnvIngestKey}}}
                create_schema: false
            extensions:
              health_check:
                endpoint: 0.0.0.0:{{HealthPort}}
              basicauth/workspace:
                client_auth:
                  username: {{user}}
                  password: ${env:{{MonitorWorkspaces.EnvIngestKey}}}
            service:
              extensions: [health_check, basicauth/workspace]
              pipelines:
                metrics:
                  receivers: [otlp]
                  processors: [memory_limiter, batch]
                  exporters: [prometheus_remote_write]
                logs:
                  receivers: [otlp]
                  processors: [memory_limiter, batch]
                  exporters: [clickhouse]
                traces:
                  receivers: [otlp]
                  processors: [memory_limiter, batch]
                  exporters: [clickhouse]

            """
        );

        return builder.ToString();
    }

    /// <summary>The hash of the rendered configuration, as the pod template carries it.</summary>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string ConfigHash(ResourceId id, JsonElement desired) =>
        KubeLabels.ReconcileHash(CollectorConfig(id, desired));

    // ── The three documents ───────────────────────────────────────────────────────────────────

    /// <summary>The <c>ConfigMap</c> document a desired body becomes.</summary>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     No labels, no annotations and no namespace here — ADR-013's seven labels are injected by
    ///     <c>KubeCommand</c> non-overridably, and the namespace comes from <c>InNamespace</c>.
    /// </remarks>
    public static string ConfigMapJson(ResourceId id, JsonElement desired) =>
        new JsonObject {
            ["kind"] = ConfigMapKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) },
            ["data"] = new JsonObject { [ConfigFile] = CollectorConfig(id, desired) }
        }.ToJsonString();

    /// <summary>The <c>Deployment</c> document a desired body becomes.</summary>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The selector is derived from the address and is immutable on a Deployment.</b>
    ///         <c>spec.selector</c> cannot change after create, so nothing from the body is in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three <c>env</c> entries are the whole tenancy wiring.</b> See
    ///         <see cref="MonitorWorkspaces.WorkspaceEnv" />; a pod whose workspace has not converged
    ///         is held by the kubelet, by name, until it has.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The readiness probe asks the <c>health_check</c> extension, not a receiver
    ///             port.
    ///         </b> A TCP probe on 4317 would report a collector ready while its exporters were
    ///         still starting; the extension answers <c>200</c> only once the whole service has.
    ///     </para>
    /// </remarks>
    public static string DeploymentJson(ResourceId id, JsonElement desired) {
        var name = ObjectNameOf(id);
        var (cpu, memory) = Resources(desired);
        var selector = new JsonObject { [NameLabel] = NameLabelValue, [InstanceLabel] = name };
        var ports = new JsonArray();

        if (OtlpGrpc(desired)) {
            ports.Add(
                new JsonObject { ["name"] = "otlp-grpc", ["containerPort"] = OtlpGrpcPort, ["protocol"] = "TCP" }
            );
        }

        if (OtlpHttp(desired)) {
            ports.Add(
                new JsonObject { ["name"] = "otlp-http", ["containerPort"] = OtlpHttpPort, ["protocol"] = "TCP" }
            );
        }

        return new JsonObject {
            ["kind"] = DeploymentKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["replicas"] = Replicas(desired),
                ["selector"] = new JsonObject { ["matchLabels"] = selector.DeepClone() },
                ["template"] = new JsonObject {
                    ["metadata"] = new JsonObject {
                        ["labels"] = selector.DeepClone(),
                        ["annotations"] = new JsonObject { [ConfigChecksumAnnotation] = ConfigHash(id, desired) }
                    },
                    ["spec"] = new JsonObject {
                        ["automountServiceAccountToken"] = false,
                        ["terminationGracePeriodSeconds"] = 30,
                        ["securityContext"] = new JsonObject {
                            ["runAsNonRoot"] = true,
                            ["runAsUser"] = CollectorUid,
                            ["runAsGroup"] = CollectorUid,
                            ["seccompProfile"] = new JsonObject { ["type"] = "RuntimeDefault" }
                        },
                        ["containers"] = new JsonArray {
                            new JsonObject {
                                ["name"] = "collector",
                                ["image"] = Image,
                                ["securityContext"] = new JsonObject {
                                    ["allowPrivilegeEscalation"] = false,
                                    ["readOnlyRootFilesystem"] = true,
                                    ["capabilities"] = new JsonObject { ["drop"] = new JsonArray { "ALL" } }
                                },
                                ["env"] = MonitorWorkspaces.WorkspaceEnv(WorkspaceNameOf(id)),
                                ["ports"] = ports,
                                ["readinessProbe"] = new JsonObject {
                                    ["httpGet"] = new JsonObject { ["path"] = "/", ["port"] = HealthPort },
                                    ["periodSeconds"] = 5
                                },
                                ["resources"] = new JsonObject {
                                    ["requests"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory },
                                    ["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory }
                                },
                                ["volumeMounts"] = new JsonArray {
                                    new JsonObject {
                                        ["name"] = "config", ["mountPath"] = ConfigDirectory, ["readOnly"] = true
                                    }
                                }
                            }
                        },
                        ["volumes"] = new JsonArray {
                            new JsonObject { ["name"] = "config", ["configMap"] = new JsonObject { ["name"] = name } }
                        }
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The <c>Service</c> document a desired body becomes.</summary>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <c>ClusterIP</c>, and only the ports the body turns on — a port on the <c>Service</c> with
    ///     no receiver behind it is a connection refused that reads as a broken collector.
    /// </remarks>
    public static string ServiceJson(ResourceId id, JsonElement desired) {
        var name = ObjectNameOf(id);
        var ports = new JsonArray();

        if (OtlpGrpc(desired)) {
            ports.Add(
                new JsonObject {
                    ["name"] = "otlp-grpc", ["port"] = OtlpGrpcPort, ["targetPort"] = OtlpGrpcPort, ["protocol"] = "TCP"
                }
            );
        }

        if (OtlpHttp(desired)) {
            ports.Add(
                new JsonObject {
                    ["name"] = "otlp-http", ["port"] = OtlpHttpPort, ["targetPort"] = OtlpHttpPort, ["protocol"] = "TCP"
                }
            );
        }

        return new JsonObject {
            ["kind"] = ServiceKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["type"] = "ClusterIP",
                ["selector"] = new JsonObject { [NameLabel] = NameLabelValue, [InstanceLabel] = name },
                ["ports"] = ports
            }
        }.ToJsonString();
    }

    // ── Reading an object back ───────────────────────────────────────────────────────────────

    /// <summary>Whether an object read out of the cluster carries what a desired body asked for.</summary>
    /// <param name="objectJson">The object, as the API server returned it.</param>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Dispatches on <c>kind</c>, and a document with no kind is <see langword="false" /></b>
    ///         — this family's rule since the workspace: a <c>Matches</c> that defaulted to
    ///         <see langword="true" /> for an unrecognised document would report a <c>Service</c> that
    ///         was never applied as converged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The configuration is compared exactly and the Deployment by containment</b>, for
    ///         <c>LoadBalancers.Matches</c>' reason: nothing rewrites a <c>ConfigMap</c>'s data, and
    ///         the API server defaults a <c>Deployment</c> heavily. The fields compared on the
    ///         Deployment are the ones that decide what runs and what is billed: the image, the replica
    ///         count, the container's resource limits — the preset is what the vCPU and memory meters
    ///         charge for, so a drift the observer cannot see is a tenant billed for a size the pod does
    ///         not have; compared as the strings <see cref="Presets" /> spells, which holds while every
    ///         entry is already in the API server's canonical form — and the config hash, the one an
    ///         obvious implementation leaves out, without which every receiver change converges
    ///         instantly and changes nothing. The Service is compared on its
    ///         port set, because a receiver switched off that left its port behind is a connection
    ///         refused a tenant would report as an outage.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, ResourceId id, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(objectJson);

        if (JsonNode.Parse(objectJson) is not JsonObject document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "ConfigMap" => document["data"]?[ConfigFile]?.GetValue<string>() == CollectorConfig(id, desired),
            "Deployment" => MatchesDeployment(document, id, desired),
            "Service" => MatchesService(document, desired),
            _ => false
        };
    }

    static bool MatchesDeployment(JsonObject document, ResourceId id, JsonElement desired) {
        if (document["spec"] is not JsonObject spec
            || spec["template"] is not JsonObject template
            || template["spec"]?["containers"] is not JsonArray containers
            || containers.Count != 1
            || containers[0] is not JsonObject container) {
            return false;
        }

        var hash = template["metadata"]?["annotations"]?[ConfigChecksumAnnotation]?.GetValue<string>();
        var (cpu, memory) = Resources(desired);

        return spec["replicas"]?.GetValue<int>() == Replicas(desired)
            && container["image"]?.GetValue<string>() == Image
            && hash == ConfigHash(id, desired)
            && container["resources"]?["limits"]?["cpu"]?.GetValue<string>() == cpu
            && container["resources"]?["limits"]?["memory"]?.GetValue<string>() == memory
            && container["env"] is JsonArray env
            && env.Count == 3;
    }

    static bool MatchesService(JsonObject document, JsonElement desired) {
        if (document["spec"]?["ports"] is not JsonArray ports) {
            return false;
        }

        var found = ports
            .Select(static x => x?["port"]?.GetValue<int>() ?? 0)
            .Order()
            .ToArray();

        var wanted = new List<int>();

        if (OtlpGrpc(desired)) {
            wanted.Add(OtlpGrpcPort);
        }

        if (OtlpHttp(desired)) {
            wanted.Add(OtlpHttpPort);
        }

        return found.SequenceEqual(wanted.Order());
    }

    // ── The endpoints ─────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>Service</c>'s in-cluster DNS name.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The collector's address.</param>
    /// <remarks>
    ///     The <c>.svc</c> form and not <c>.svc.cluster.local</c>: the cluster suffix is a kubelet
    ///     setting the platform does not control, and the search path every pod gets resolves the
    ///     shorter form on any of them.
    /// </remarks>
    public static string ServiceHost(string ns, ResourceId id) => ObjectNameOf(id) + "." + ns + ".svc";

    /// <summary>Where OTLP over gRPC is accepted, or empty when the receiver is off.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string OtlpGrpcEndpoint(string ns, ResourceId id, JsonElement desired) =>
        OtlpGrpc(desired)
            ? string.Create(CultureInfo.InvariantCulture, $"{ServiceHost(ns, id)}:{OtlpGrpcPort}")
            : string.Empty;

    /// <summary>Where OTLP over HTTP is accepted, or empty when the receiver is off.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The collector's address.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string OtlpHttpEndpoint(string ns, ResourceId id, JsonElement desired) =>
        OtlpHttp(desired)
            ? string.Create(CultureInfo.InvariantCulture, $"http://{ServiceHost(ns, id)}:{OtlpHttpPort}")
            : string.Empty;

    // ── A body, for tests, the conformance case and the chart ────────────────────────────────

    /// <summary>A valid body at <see cref="MonitorWorkspaces.V2026" />.</summary>
    /// <param name="clusterId">The cluster the collector runs in — its workspace's.</param>
    /// <param name="otlpGrpc">Whether to listen for OTLP over gRPC.</param>
    /// <param name="otlpHttp">Whether to listen for OTLP over HTTP.</param>
    /// <param name="replicas">How many pods.</param>
    /// <param name="preset">The sizing preset.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        bool otlpGrpc = true,
        bool otlpHttp = true,
        int replicas = 1,
        string preset = DefaultPreset,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["receivers"] = new JsonObject { ["otlpGrpc"] = otlpGrpc, ["otlpHttp"] = otlpHttp },
                ["replicas"] = replicas,
                ["sizing"] = new JsonObject { ["preset"] = preset }
            }
        }.ToJsonString();

    // ── Reading JSON ─────────────────────────────────────────────────────────────────────────

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    static int Whole(JsonElement desired, string name, int fallback) =>
        Property(desired, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : fallback;

    static string Nested(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
