using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Monitor/workspaces/components</c>: an application
///     inside a workspace, the connection string its SDKs send through a collector, and the five views
///     read back out of the workspace's traces and logs.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/01 § The catalogue's <i>"Application Insights | ⊂ workspaces | M2 | OTLP ingest, a
///         per-tenant ClickHouse database, and the trace/exception views"</i>, and issue #32's fourth
///         noun. docs/plan/16 § Application views carries the design; this is its contract.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A COMPONENT IS A LENS, NOT A STORE, AND ITS BOUNDARY IS <c>service.namespace</c>.
///         </b> The workspace is the tenancy — one ClickHouse database, <c>ws_{guid:N}</c> — and a
///         component names the slice of it one application writes: every span and log record whose
///         resource carries <c>service.namespace</c> equal to the component's name. The connection
///         string sets exactly that attribute, so an SDK wired from it lands in its own component's
///         views. ⚠ The attribute is the client's assertion, so it separates applications and does
///         not separate tenants: two components in one workspace can each claim the other's namespace.
///         The tenant boundary is the database, which the view path derives from the workspace's GUID
///         in the platform's index and never from anything in a request or a cluster.
///     </para>
///     <para>
///         ⚠ <b>One <c>ConfigMap</c>, which is the connection string a pod can <c>envFrom</c>.</b>
///         The three standard <c>OTEL_*</c> variables — the collector's endpoint, its protocol and the
///         resource attribute — in the component's namespace, so a workload is wired by naming one
///         object rather than by copying an action's answer into a manifest.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The views are actions and not child types, for the reason <c>listInstances</c> is an
///             action.
///         </b> A view is a read of telemetry the tenant's workloads produced, not desired state
///         anyone declares; a type for it would have a <c>PUT</c> nothing could apply.
///     </para>
/// </remarks>
public static class MonitorComponents {
    /// <summary>The type's path under <see cref="MonitorWorkspaces.ProviderNamespace" />.</summary>
    public const string TypePath = "workspaces/components";

    /// <summary>The chart that renders the same object — docs/plan/12 § The pattern, once.</summary>
    public const string ChartName = "managed/monitor-component";

    /// <summary>Where the body names the cluster the connection string is published in.</summary>
    /// <remarks>
    ///     The cluster its collector runs in, in practice — the endpoint is the collector's in-cluster
    ///     <c>Service</c>, which resolves in that cluster and nowhere else.
    /// </remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that returns the connection string.</summary>
    public const string ListConnectionStringAction = "listConnectionString";

    /// <summary>Request rate, failure rate and duration percentiles by operation.</summary>
    public const string RequestsAction = "requests";

    /// <summary>Outgoing calls — databases, HTTP, messaging — by target.</summary>
    public const string DependenciesAction = "dependencies";

    /// <summary>Exceptions recorded on spans and in logs, by type.</summary>
    public const string ExceptionsAction = "exceptions";

    /// <summary>The service graph: which service calls which, from parent and child spans.</summary>
    public const string ApplicationMapAction = "applicationMap";

    /// <summary>One trace's spans and logs.</summary>
    public const string TransactionAction = "transaction";

    /// <summary>
    ///     The permission every action on this type needs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>read</c>, and the views read telemetry rather than configuration. A reader of the
    ///     component is a reader of its application's requests, exceptions and log lines — the same
    ///     trade Azure's Reader role makes on an Application Insights resource. A tenant that wants
    ///     the two apart grants on the workspace's other children, not on this one.
    /// </remarks>
    public const string Permission = "read";

    /// <summary>Every view, in the order docs/plan/16 lists them.</summary>
    public static ImmutableArray<string> Views { get; } =
        [RequestsAction, DependenciesAction, ExceptionsAction, ApplicationMapAction, TransactionAction];

    /// <summary>The type, as the registry names it.</summary>
    public static ResourceTypeName Type { get; } = new(MonitorWorkspaces.ProviderNamespace, TypePath);

    // ── The limits ────────────────────────────────────────────────────────────────────────────

    /// <summary>How far back a view reads by default, in minutes.</summary>
    public const int DefaultTimespanMinutes = 60;

    /// <summary>How far back an aggregate view may read, in minutes — a day.</summary>
    /// <remarks>
    ///     ⚠ The look-back is the view's cost: every view scans the window, and the percentiles hold
    ///     each group's durations in memory. The store's own budget (<c>max_rows_to_read</c>,
    ///     <c>max_memory_usage</c>, <c>max_execution_time</c>) is the backstop; this is what keeps an
    ///     ordinary request well inside it.
    /// </remarks>
    public const int MaxTimespanMinutes = 1440;

    /// <summary>How far back a transaction lookup may read, in minutes — a week.</summary>
    /// <remarks>
    ///     Longer than the aggregates', because a lookup by trace id reads one trace through the
    ///     table's bloom-filter index rather than every span in the window.
    /// </remarks>
    public const int MaxTransactionTimespanMinutes = 10_080;

    /// <summary>How many rows an aggregate view returns by default.</summary>
    public const int DefaultTop = 20;

    /// <summary>The most rows an aggregate view returns.</summary>
    public const int MaxTop = 100;

    /// <summary>The most spans a transaction returns; <c>/truncated</c> says when there were more.</summary>
    public const int MaxTransactionSpans = 500;

    /// <summary>The most log records a transaction returns.</summary>
    public const int MaxTransactionLogs = 200;

    /// <summary>The longest log body a transaction returns, in characters.</summary>
    public const int MaxLogBodyLength = 2048;

    // ── The protocols ─────────────────────────────────────────────────────────────────────────

    /// <summary>OTLP over HTTP with protobuf bodies — the SDKs' <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> spelling.</summary>
    public const string HttpProtobuf = "http/protobuf";

    /// <summary>OTLP over gRPC.</summary>
    public const string Grpc = "grpc";

    /// <summary>The collector name the chart renders with and the examples use.</summary>
    public const string DefaultCollector = "gateway";

    /// <summary>The protocols a connection string can name.</summary>
    public static ImmutableArray<string> Protocols { get; } = [Grpc, HttpProtobuf];

    // ── The kind and the names ────────────────────────────────────────────────────────────────

    /// <summary>The connection string, as a <c>ConfigMap</c>.</summary>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>The workspace a component reads, which is its parent's own name.</summary>
    /// <param name="id">The component's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string WorkspaceNameOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no workspace for this component to read. A "
            + "component is a child type and its address always interleaves its workspace — see "
            + "MonitorComponents.TypePath.",
            nameof(id)
        );

    /// <summary>The <c>ConfigMap</c>'s name: the workspace's name and the component's own, joined.</summary>
    /// <param name="id">The component's address.</param>
    /// <remarks>
    ///     <c>MonitorCollectors.ObjectNameOf</c>'s argument: the namespace is the resource group's, so
    ///     two workspaces in one group may each hold a component called <c>shop</c>.
    /// </remarks>
    public static string ObjectNameOf(ResourceId id) => "component-" + WorkspaceNameOf(id) + "-" + id.Name;

    /// <summary>The <c>ConfigMap</c> a component owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The component's address.</param>
    public static ObjectRef ConfigMapRef(string ns, ResourceId id) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>Every object a component owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The component's address.</param>
    public static ImmutableArray<ObjectRef> Objects(string ns, ResourceId id) => [ConfigMapRef(ns, id)];

    /// <summary>The value the component's telemetry carries in <c>service.namespace</c>.</summary>
    /// <param name="id">The component's address.</param>
    /// <remarks>
    ///     The component's own name, which is a DNS label and so needs no escaping inside
    ///     <c>OTEL_RESOURCE_ATTRIBUTES</c>' comma-and-equals grammar.
    /// </remarks>
    public static string ServiceNamespace(ResourceId id) => id.Name;

    // ── The body ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="MonitorWorkspaces.V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the component is billed in — its workspace's."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The component's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the connection string is published in — the one its "
                    + "collector runs in, because the endpoint is that collector's in-cluster address."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/collector",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The name of the collector under the same workspace that the "
                    + "application's SDKs send to. The views read the workspace whichever collector "
                    + "carried the telemetry; this only decides the endpoint the connection string names."
                ) {
                    Pattern = ResourceNaming.Pattern,
                    MaxLength = ResourceNaming.MaxLength,
                    // The chart's value, which has to satisfy the pattern; LoadBalancers' subnet is
                    // the same shape — required, and a default a chart can render.
                    DefaultJson = "\"" + DefaultCollector + "\"",
                    ExampleJson = "\"" + DefaultCollector + "\""
                },
                new(
                    "/properties/protocol",
                    SchemaKind.Text,
                    Description: "Which OTLP protocol the connection string names. The collector "
                    + "must have that receiver on."
                ) { AllowedValues = Protocols, DefaultJson = "\"" + HttpProtobuf + "\"" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    /// <summary>The collector the body names.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Collector(JsonElement desired) => Text(desired, "collector", string.Empty);

    /// <summary>The protocol the body names, or the default.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Protocol(JsonElement desired) =>
        Text(desired, "protocol", HttpProtobuf) is var protocol && Protocols.Contains(protocol)
            ? protocol
            : HttpProtobuf;

    // ── The connection string ─────────────────────────────────────────────────────────────────

    /// <summary><c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.</summary>
    public const string EnvEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary><c>OTEL_EXPORTER_OTLP_PROTOCOL</c>.</summary>
    public const string EnvProtocol = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary><c>OTEL_RESOURCE_ATTRIBUTES</c>.</summary>
    public const string EnvResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";

    /// <summary>The collector's endpoint the connection string names.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The component's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Computed from the collector's NAME, and the collector's body is never read.
    ///         </b> The address is <c>MonitorCollectors.ServiceHost</c> of the sibling the body names
    ///         — a pure function of two names and the namespace, so the object stays a function of
    ///         the address and the body. A collector with that protocol's receiver off, or no collector
    ///         of that name at all, is an address that does not answer, which is what
    ///         <c>listEndpoints</c> decided for a collector that has not converged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A URL for both protocols</b>, because the SDKs' variable is one: gRPC takes
    ///         <c>http://host:4317</c> and HTTP takes <c>http://host:4318</c> and appends
    ///         <c>/v1/traces</c> itself.
    ///     </para>
    /// </remarks>
    public static string Endpoint(string ns, ResourceId id, JsonElement desired) {
        var collector = new ResourceId(
            id.TenantId,
            id.SubscriptionId,
            id.ResourceGroup,
            MonitorCollectors.Type,
            Collector(desired),
            Guid.Empty,
            WorkspaceNameOf(id)
        );

        var port = Protocol(desired) == Grpc ? MonitorCollectors.OtlpGrpcPort : MonitorCollectors.OtlpHttpPort;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"http://{MonitorCollectors.ServiceHost(ns, collector)}:{port}"
        );
    }

    /// <summary><c>OTEL_RESOURCE_ATTRIBUTES</c>' value.</summary>
    /// <param name="id">The component's address.</param>
    public static string ResourceAttributes(ResourceId id) => "service.namespace=" + ServiceNamespace(id);

    /// <summary>
    ///     The connection string, as one <c>Key=Value;…</c> line of the three variables.
    /// </summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The component's address.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string ConnectionString(string ns, ResourceId id, JsonElement desired) =>
        $"{EnvEndpoint}={Endpoint(ns, id, desired)};{EnvProtocol}={Protocol(desired)};"
        + $"{EnvResourceAttributes}={ResourceAttributes(id)}";

    /// <summary>The <c>ConfigMap</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The component's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     No labels and no namespace here — ADR-013's seven labels are injected by <c>KubeCommand</c>
    ///     non-overridably, and the namespace comes from <c>InNamespace</c>. The data keys are the
    ///     variables' own names so <c>envFrom</c> needs no mapping.
    /// </remarks>
    public static string ConfigMapJson(string ns, ResourceId id, JsonElement desired) =>
        new JsonObject {
            ["kind"] = ConfigMapKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) },
            ["data"] = Data(ns, id, desired)
        }.ToJsonString();

    static JsonObject Data(string ns, ResourceId id, JsonElement desired) =>
        new() {
            [EnvEndpoint] = Endpoint(ns, id, desired),
            [EnvProtocol] = Protocol(desired),
            [EnvResourceAttributes] = ResourceAttributes(id)
        };

    /// <summary>Whether an object read back from the cluster is what a desired body asks for.</summary>
    /// <param name="objectJson">The object, as the API server returned it.</param>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The component's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     Exact over the three data keys and blind to anything else: nothing rewrites a
    ///     <c>ConfigMap</c>'s data, and a fourth key a tenant added beside them is theirs.
    /// </remarks>
    public static bool Matches(string objectJson, string ns, ResourceId id, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(objectJson);

        if (JsonNode.Parse(objectJson) is not JsonObject document
            || document["kind"]?.GetValue<string>() != ConfigMapKind.Kind
            || document["data"] is not JsonObject data) {
            return false;
        }

        return Data(ns, id, desired).All(x => data[x.Key]?.GetValue<string>() == x.Value!.GetValue<string>());
    }

    /// <summary>What a <c>listConnectionString</c> returns.</summary>
    /// <remarks>
    ///     ⚠ Not secret, for <c>listEndpoints</c>' reason: the collector's ingress is unauthenticated
    ///     by record (<c>charts/managed/monitor-collector/conformance.yaml § owed</c>,
    ///     <c>collector-ingress-is-unauthenticated</c>), so there is no key to put here.
    /// </remarks>
    public static ResourceSchema ListConnectionStringResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/connectionString",
                    SchemaKind.Text,
                    true,
                    Description: "The three variables as one Key=Value;… line, for a configuration "
                    + "that takes a single string."
                ),
                new(
                    "/otlpEndpoint",
                    SchemaKind.Text,
                    true,
                    Description: "OTEL_EXPORTER_OTLP_ENDPOINT: the collector's in-cluster URL."
                ),
                new("/otlpProtocol", SchemaKind.Text, true, Description: "OTEL_EXPORTER_OTLP_PROTOCOL.") {
                    AllowedValues = Protocols
                },
                new(
                    "/resourceAttributes",
                    SchemaKind.Text,
                    true,
                    Description: "OTEL_RESOURCE_ATTRIBUTES: the service.namespace the views filter on."
                ),
                new(
                    "/configMap",
                    SchemaKind.Text,
                    true,
                    Description: "The ConfigMap in the component's namespace carrying the three "
                    + "variables, for a pod's envFrom."
                )
            ]
        );

    // ── The views' requests ───────────────────────────────────────────────────────────────────

    static SchemaProperty TimespanProperty(int maximum, int fallback) =>
        new(
            "/timespanMinutes",
            SchemaKind.WholeNumber,
            Description: "How far back to read, in minutes, ending now."
        ) { Minimum = 1, Maximum = maximum, DefaultJson = fallback.ToString(CultureInfo.InvariantCulture) };

    /// <summary>What <c>requests</c>, <c>dependencies</c>, <c>exceptions</c> and <c>applicationMap</c> take.</summary>
    public static ResourceSchema ViewRequest { get; } =
        ResourceSchema.Of(
            [
                TimespanProperty(MaxTimespanMinutes, DefaultTimespanMinutes),
                new(
                    "/top",
                    SchemaKind.WholeNumber,
                    Description: "The most rows to return, busiest first."
                ) { Minimum = 1, Maximum = MaxTop, DefaultJson = DefaultTop.ToString(CultureInfo.InvariantCulture) }
            ]
        );

    /// <summary>What <c>transaction</c> takes.</summary>
    public static ResourceSchema TransactionRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/traceId",
                    SchemaKind.Text,
                    true,
                    Description: "The W3C trace id: 32 lower-case hex digits, as the SDKs and every log "
                    + "line of the trace carry it."
                ) { Pattern = "[0-9a-f]{32}", ExampleJson = "\"4bf92f3577b34da6a3ce929d0e0e4736\"" },
                TimespanProperty(MaxTransactionTimespanMinutes, MaxTimespanMinutes)
            ]
        );

    /// <summary>The look-back a validated view request asks for.</summary>
    /// <param name="body">The validated request.</param>
    /// <param name="fallback">The default when the request names none.</param>
    public static int TimespanMinutes(JsonElement body, int fallback = DefaultTimespanMinutes) =>
        Whole(body, "timespanMinutes", fallback);

    /// <summary>The row limit a validated view request asks for.</summary>
    /// <param name="body">The validated request.</param>
    public static int Top(JsonElement body) => Whole(body, "top", DefaultTop);

    /// <summary>The trace a validated transaction request names.</summary>
    /// <param name="body">The validated request.</param>
    public static string TraceId(JsonElement body) =>
        body.ValueKind is JsonValueKind.Object
        && body.TryGetProperty("traceId", out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // ── The views' responses ──────────────────────────────────────────────────────────────────
    //
    // ⚠ COLUMNS, NOT ROWS, BECAUSE THE SCHEMA HAS NO ARRAY OF OBJECTS. SchemaKind.Array's remarks say
    // why an array of objects is refused rather than half-modelled, so a view is a set of parallel
    // arrays — one per column, the same length, row i across all of them — which every generated
    // surface can type: the SDK gets number[] and string[], the CLI prints them, and a portal chart
    // takes a column as a series without reshaping. The alternative the alert rule chose, one text
    // line per row, would have made every number a substring.

    static SchemaProperty Column(string pointer, SchemaKind element, string description) =>
        new(pointer, SchemaKind.Array, true, Description: description) { ElementKind = element };

    static SchemaProperty Count(string pointer, string description) =>
        new(pointer, SchemaKind.WholeNumber, true, Description: description);

    static SchemaProperty Window { get; } =
        Count("/timespanMinutes", "The window the view read, in minutes, ending when it was asked.");

    /// <summary>What <c>requests</c> returns.</summary>
    public static ResourceSchema RequestsResponse { get; } =
        ResourceSchema.Of(
            [
                Window,
                Count("/total", "Every request in the window, across every operation, not only the rows below."),
                Count("/failed", "How many of them failed — a span whose status is Error."),
                Column("/services", SchemaKind.Text, "Per row: the service that served the operation."),
                Column("/operations", SchemaKind.Text, "Per row: the operation — the server span's name."),
                Column("/counts", SchemaKind.WholeNumber, "Per row: requests in the window."),
                Column("/failures", SchemaKind.WholeNumber, "Per row: failed requests."),
                Column("/ratePerMinute", SchemaKind.Number, "Per row: requests per minute over the window."),
                Column("/failureRate", SchemaKind.Number, "Per row: failures over requests, 0 to 1."),
                Column("/p50Ms", SchemaKind.Number, "Per row: the median duration, in milliseconds."),
                Column("/p95Ms", SchemaKind.Number, "Per row: the 95th percentile duration, in milliseconds."),
                Column("/p99Ms", SchemaKind.Number, "Per row: the 99th percentile duration, in milliseconds.")
            ]
        );

    /// <summary>What <c>dependencies</c> returns.</summary>
    public static ResourceSchema DependenciesResponse { get; } =
        ResourceSchema.Of(
            [
                Window,
                Count("/total", "Every outgoing call in the window, not only the rows below."),
                Count("/failed", "How many of them failed."),
                Column("/services", SchemaKind.Text, "Per row: the service that made the call."),
                Column("/types", SchemaKind.Text, "Per row: db, http, messaging, rpc or other, from the span's attributes."),
                Column(
                    "/targets",
                    SchemaKind.Text,
                    "Per row: what was called — peer.service, else server.address, else the database or "
                    + "messaging system; empty when the span names none."
                ),
                Column("/names", SchemaKind.Text, "Per row: the client span's name."),
                Column("/counts", SchemaKind.WholeNumber, "Per row: calls in the window."),
                Column("/failures", SchemaKind.WholeNumber, "Per row: failed calls."),
                Column("/failureRate", SchemaKind.Number, "Per row: failures over calls, 0 to 1."),
                Column("/p50Ms", SchemaKind.Number, "Per row: the median duration, in milliseconds."),
                Column("/p95Ms", SchemaKind.Number, "Per row: the 95th percentile duration, in milliseconds."),
                Column("/p99Ms", SchemaKind.Number, "Per row: the 99th percentile duration, in milliseconds.")
            ]
        );

    /// <summary>What <c>exceptions</c> returns.</summary>
    public static ResourceSchema ExceptionsResponse { get; } =
        ResourceSchema.Of(
            [
                Window,
                Count("/total", "Every exception in the window, from spans and from logs, not only the rows below."),
                Column("/services", SchemaKind.Text, "Per row: the service that raised it."),
                Column("/types", SchemaKind.Text, "Per row: exception.type."),
                Column("/messages", SchemaKind.Text, "Per row: the most recent exception.message of that type."),
                Column("/counts", SchemaKind.WholeNumber, "Per row: occurrences in the window."),
                Column(
                    "/fromSpans",
                    SchemaKind.WholeNumber,
                    "Per row: how many were an `exception` event on a span; the rest were log records "
                    + "carrying exception.type."
                ),
                new("/lastSeen", SchemaKind.Array, true, Description: "Per row: the latest occurrence.") {
                    ElementKind = SchemaKind.Text, Format = SchemaFormat.DateTime
                }
            ]
        );

    /// <summary>What <c>applicationMap</c> returns.</summary>
    public static ResourceSchema ApplicationMapResponse { get; } =
        ResourceSchema.Of(
            [
                Window,
                Column("/nodes", SchemaKind.Text, "Per node: a service in the component."),
                Column("/nodeRequests", SchemaKind.WholeNumber, "Per node: requests it served in the window."),
                Column("/nodeFailures", SchemaKind.WholeNumber, "Per node: requests it failed."),
                Column("/edgeSources", SchemaKind.Text, "Per edge: the calling service."),
                Column("/edgeTargets", SchemaKind.Text, "Per edge: the called service."),
                Column(
                    "/edgeCalls",
                    SchemaKind.WholeNumber,
                    "Per edge: spans in the target whose parent span is in the source, in the window."
                ),
                Column("/edgeFailures", SchemaKind.WholeNumber, "Per edge: those whose status is Error."),
                Column("/edgeP95Ms", SchemaKind.Number, "Per edge: the target span's 95th percentile duration, in milliseconds.")
            ]
        );

    /// <summary>What <c>transaction</c> returns.</summary>
    public static ResourceSchema TransactionResponse { get; } =
        ResourceSchema.Of(
            [
                new("/traceId", SchemaKind.Text, true, Description: "The trace that was read."),
                Count("/spanCount", "How many spans are returned."),
                new(
                    "/truncated",
                    SchemaKind.Boolean,
                    true,
                    Description: "Whether the trace has more spans or log records than a transaction returns."
                ),
                Column("/spanIds", SchemaKind.Text, "Per span: its id."),
                Column("/parentSpanIds", SchemaKind.Text, "Per span: its parent's id, empty for the root."),
                Column("/services", SchemaKind.Text, "Per span: the service that recorded it."),
                Column("/names", SchemaKind.Text, "Per span: its name."),
                Column("/kinds", SchemaKind.Text, "Per span: Server, Client, Internal, Producer or Consumer."),
                new("/starts", SchemaKind.Array, true, Description: "Per span: when it started, oldest first.") {
                    ElementKind = SchemaKind.Text, Format = SchemaFormat.DateTime
                },
                Column("/durationsMs", SchemaKind.Number, "Per span: how long it took, in milliseconds."),
                Column("/statuses", SchemaKind.Text, "Per span: Unset, Ok or Error."),
                Count("/logCount", "How many log records of the trace are returned."),
                new("/logTimes", SchemaKind.Array, true, Description: "Per log record: when, oldest first.") {
                    ElementKind = SchemaKind.Text, Format = SchemaFormat.DateTime
                },
                Column("/logSpanIds", SchemaKind.Text, "Per log record: the span it was written under."),
                Column("/logSeverities", SchemaKind.Text, "Per log record: its severity text."),
                Column("/logBodies", SchemaKind.Text, "Per log record: its body, cut at 2048 characters.")
            ]
        );

    // ── A body, for tests, the conformance case and the chart ────────────────────────────────

    /// <summary>A valid body at <see cref="MonitorWorkspaces.V2026" />.</summary>
    /// <param name="clusterId">The cluster the connection string is published in — its collector's.</param>
    /// <param name="collector">The collector under the same workspace the SDKs send to.</param>
    /// <param name="protocol">The OTLP protocol.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        string collector = DefaultCollector,
        string protocol = HttpProtobuf,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["collector"] = collector,
                ["protocol"] = protocol
            }
        }.ToJsonString();

    // ── Reading JSON ─────────────────────────────────────────────────────────────────────────

    static string Text(JsonElement desired, string name, string fallback) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    static int Whole(JsonElement body, string name, int fallback) =>
        body.ValueKind is JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;
}
