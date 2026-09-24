using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     The telemetry the view tests emit, as OTLP/JSON, with every number an assertion reads chosen
///     so that it can only come out one way.
/// </summary>
/// <remarks>
///     <para>
///         <b>The <c>shop</c> application</b> — twenty traces, each four spans: <c>frontend</c> serves
///         <c>GET /cart</c> in <c>10·(i+1)</c> ms and fails for i &lt; 4 with an <c>exception</c> event;
///         inside it a client span <c>GET /items</c> to <c>cart</c> (50 ms); <c>cart</c> serves
///         <c>GET /items</c> in <c>5·(i+1)</c> ms and fails for i ∈ {10, 11}; and <c>cart</c> queries
///         PostgreSQL (<c>SELECT items</c>, 2 ms), failing for i ∈ {15, 16, 17}. Four log records:
///         trace 0 logs <c>reading cart</c> at Info, and traces 0‥2 each log an error carrying
///         <c>exception.type</c> <c>System.IO.IOException</c>.
///     </para>
///     <para>
///         <b>The <c>admin</c> application</b> in the same workspace: <c>backoffice</c> serves
///         <c>POST /login</c> five times in 30 ms. <b>The other tenant's</b> <c>shop</c>:
///         <c>b-frontend</c> serves <c>GET /b</c> seven times, once failing.
///     </para>
/// </remarks>
/// <param name="start">When the first trace starts. Ten minutes ago keeps every view's hour-long window over all of it.</param>
public sealed class TelemetryFeed(DateTimeOffset start) {
    readonly Dictionary<(string Service, string Namespace), JsonArray> spans = [];
    readonly Dictionary<(string Service, string Namespace), JsonArray> logs = [];

    /// <summary>How many spans the feed holds.</summary>
    public int SpanCount => spans.Values.Sum(static x => x.Count);

    /// <summary>How many log records the feed holds.</summary>
    public int LogCount => logs.Values.Sum(static x => x.Count);

    /// <summary>Adds the <c>shop</c> application and returns its trace ids, oldest first.</summary>
    public ImmutableArray<string> Shop() {
        var traces = ImmutableArray.CreateBuilder<string>();

        for (var i = 0; i < 20; i++) {
            var trace = Hex(16);
            traces.Add(trace);

            var at = start.AddSeconds(i);
            var server = Hex(8);
            var client = Hex(8);
            var cart = Hex(8);
            var database = Hex(8);

            Span(
                "frontend", "shop", trace, server, "", "GET /cart", 2, at, TimeSpan.FromMilliseconds(10 * (i + 1)),
                i < 4,
                exception: i < 4 ? ("System.InvalidOperationException", $"cart {i} gone") : null
            );

            Span(
                "frontend", "shop", trace, client, server, "GET /items", 3, at.AddMilliseconds(1), TimeSpan.FromMilliseconds(50),
                false,
                attributes: [("http.request.method", "GET"), ("server.address", "cart")]
            );

            Span(
                "cart", "shop", trace, cart, client, "GET /items", 2, at.AddMilliseconds(2), TimeSpan.FromMilliseconds(5 * (i + 1)),
                i is 10 or 11
            );

            Span(
                "cart", "shop", trace, database, cart, "SELECT items", 3, at.AddMilliseconds(3), TimeSpan.FromMilliseconds(2),
                i is 15 or 16 or 17,
                attributes: [("db.system", "postgresql"), ("server.address", "pg.local")]
            );

            if (i == 0) {
                Log("frontend", "shop", trace, server, at.AddMilliseconds(4), "Info", 9, "reading cart", []);
            }

            if (i < 3) {
                Log(
                    "frontend", "shop", trace, server, at.AddMilliseconds(5), "Error", 17, $"failed to read cart {i}",
                    [("exception.type", "System.IO.IOException"), ("exception.message", $"disk {i}")]
                );
            }
        }

        return traces.ToImmutable();
    }

    /// <summary>Adds the <c>admin</c> application.</summary>
    public void Admin() {
        for (var i = 0; i < 5; i++) {
            Span("backoffice", "admin", Hex(16), Hex(8), "", "POST /login", 2, start.AddSeconds(30 + i), TimeSpan.FromMilliseconds(30), false);
        }
    }

    /// <summary>Adds the other tenant's <c>shop</c>.</summary>
    public void OtherTenant() {
        for (var i = 0; i < 7; i++) {
            Span("b-frontend", "shop", Hex(16), Hex(8), "", "GET /b", 2, start.AddSeconds(i), TimeSpan.FromMilliseconds(40), i == 0);
        }
    }

    /// <summary>The spans, as one <c>/v1/traces</c> body.</summary>
    public string TracesJson() =>
        new JsonObject {
            ["resourceSpans"] = new JsonArray(
                [
                    .. spans.Select(static x => (JsonNode?)new JsonObject {
                            ["resource"] = Resource(x.Key.Service, x.Key.Namespace),
                            ["scopeSpans"] = new JsonArray(
                                new JsonObject { ["scope"] = new JsonObject { ["name"] = "views-test" }, ["spans"] = x.Value }
                            )
                        }
                    )
                ]
            )
        }.ToJsonString();

    /// <summary>The log records, as one <c>/v1/logs</c> body.</summary>
    public string LogsJson() =>
        new JsonObject {
            ["resourceLogs"] = new JsonArray(
                [
                    .. logs.Select(static x => (JsonNode?)new JsonObject {
                            ["resource"] = Resource(x.Key.Service, x.Key.Namespace),
                            ["scopeLogs"] = new JsonArray(
                                new JsonObject { ["scope"] = new JsonObject { ["name"] = "views-test" }, ["logRecords"] = x.Value }
                            )
                        }
                    )
                ]
            )
        }.ToJsonString();

    void Span(
        string service,
        string ns,
        string trace,
        string span,
        string parent,
        string name,
        int kind,
        DateTimeOffset at,
        TimeSpan duration,
        bool failed,
        (string Type, string Message)? exception = null,
        (string Key, string Value)[]? attributes = null
    ) {
        var body = new JsonObject {
            ["traceId"] = trace,
            ["spanId"] = span,
            ["name"] = name,
            ["kind"] = kind,
            ["startTimeUnixNano"] = Nanos(at),
            ["endTimeUnixNano"] = Nanos(at + duration),
            ["attributes"] = Attributes(attributes ?? []),
            ["status"] = failed ? new JsonObject { ["code"] = 2, ["message"] = "failed" } : new JsonObject()
        };

        if (parent.Length > 0) {
            body["parentSpanId"] = parent;
        }

        if (exception is { } thrown) {
            body["events"] = new JsonArray(
                new JsonObject {
                    ["timeUnixNano"] = Nanos(at + duration / 2),
                    ["name"] = "exception",
                    ["attributes"] = Attributes([("exception.type", thrown.Type), ("exception.message", thrown.Message)])
                }
            );
        }

        Bucket(spans, service, ns).Add(body);
    }

    void Log(
        string service,
        string ns,
        string trace,
        string span,
        DateTimeOffset at,
        string severity,
        int severityNumber,
        string message,
        (string Key, string Value)[] attributes
    ) =>
        Bucket(logs, service, ns)
            .Add(
                new JsonObject {
                    ["timeUnixNano"] = Nanos(at),
                    ["severityText"] = severity,
                    ["severityNumber"] = severityNumber,
                    ["body"] = new JsonObject { ["stringValue"] = message },
                    ["traceId"] = trace,
                    ["spanId"] = span,
                    ["attributes"] = Attributes(attributes)
                }
            );

    static JsonArray Bucket(Dictionary<(string, string), JsonArray> into, string service, string ns) {
        if (!into.TryGetValue((service, ns), out var bucket)) {
            bucket = [];
            into[(service, ns)] = bucket;
        }

        return bucket;
    }

    static JsonObject Resource(string service, string ns) =>
        new() { ["attributes"] = Attributes([("service.name", service), ("service.namespace", ns)]) };

    static JsonArray Attributes((string Key, string Value)[] pairs) =>
        new(
            [
                .. pairs.Select(static x => (JsonNode?)new JsonObject {
                        ["key"] = x.Key, ["value"] = new JsonObject { ["stringValue"] = x.Value }
                    }
                )
            ]
        );

    static string Nanos(DateTimeOffset at) =>
        (at.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture);

    static string Hex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}
