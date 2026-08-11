using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyberCloud.Sdk.Tests;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  ⚠ STAND-IN FOR GENERATED CODE. NOT A HAND-WRITTEN PART OF THE SDK.
//
//  Everything in this file is what the Roslyn emitter (ADR-012, docs/plan/21 § Generation) must
//  produce for one resource type. It is modelled on `CyberCloud.Sample/widgets`, the only resource
//  type in openapi/2026-08-01.json, and it is written by hand ONLY so that the hand-written half can
//  be tested end to end before the emitter exists.
//
//  ⚠ IT LIVES IN THE TEST PROJECT ON PURPOSE, and the placement is a claim rather than a
//  convenience. The emitter's output will land in CyberCloud.Sdk (or in an assembly beside it), so
//  putting the stand-in here proves that everything an emitter needs is PUBLIC on
//  CyberCloud.Sdk — nothing below uses an `internal`. If a future emitter change needs something
//  that is not public, this file stops compiling, which is the earliest possible warning.
//
//  Read src/CyberCloud.Sdk/EmitterContract.cs first. It is the contract; this is one instance of it.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The body of a <c>CyberCloud.Sample/widgets</c> — openapi/2026-08-01.json § CyberCloud.Sample.widgets.</summary>
public sealed partial class WidgetData {
    public WidgetData(string location) => Location = location;

    [JsonPropertyName("location")]
    public string Location { get; init; }

    [JsonPropertyName("properties")]
    public WidgetProperties? Properties { get; init; }
}

/// <summary>The widget's own settings.</summary>
public sealed partial class WidgetProperties {
    [JsonPropertyName("clusterId")]
    public string ClusterId { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>
///     The generated <c>JsonSerializerContext</c>. ⚠ Source-generated, never reflection — see
///     EmitterContract.cs § 1: the SDK sets <c>IsAotCompatible</c>, so a reflective
///     <c>JsonSerializer.Deserialize&lt;T&gt;</c> is IL2026 and a build failure.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetData))]
[JsonSerializable(typeof(WidgetListPage))]
public partial class WidgetJsonContext : JsonSerializerContext;

/// <summary>A page of a list response.</summary>
public sealed partial class WidgetListPage {
    [JsonPropertyName("value")]
    public IReadOnlyList<WidgetData> Value { get; init; } = [];

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; init; }
}

/// <summary>One widget.</summary>
public sealed partial class WidgetResource {
    public WidgetResource(CyberCloudClientContext context, Uri uri, WidgetData data) {
        Context = context;
        Uri = uri;
        Data = data;
    }

    public CyberCloudClientContext Context { get; }

    public Uri Uri { get; }

    public WidgetData Data { get; }
}

/// <summary>
///     Turns the resource <c>GET</c> that follows a successful operation into a
///     <see cref="WidgetResource" /> — EmitterContract.cs § 1's <c>{Type}OperationSource</c>.
/// </summary>
public sealed partial class WidgetOperationSource : IOperationSource<WidgetResource> {
    readonly CyberCloudClientContext context;
    readonly Uri uri;

    public WidgetOperationSource(CyberCloudClientContext context, Uri uri) {
        this.context = context;
        this.uri = uri;
    }

    public ValueTask<WidgetResource> CreateResultAsync(Response response, CancellationToken cancellationToken) {
        var data = JsonSerializer.Deserialize(response.Content.Span, WidgetJsonContext.Default.WidgetData)!;

        return ValueTask.FromResult(new WidgetResource(context, uri, data));
    }
}

/// <summary>The widgets of one resource group.</summary>
public sealed partial class WidgetCollection {
    readonly CyberCloudClientContext context;
    readonly string scope;

    public WidgetCollection(CyberCloudClientContext context, string scope) {
        this.context = context;
        this.scope = scope;
    }

    string Path(string name) => $"{scope}/providers/CyberCloud.Sample/widgets/{Uri.EscapeDataString(name)}";

    public async Task<Response<WidgetResource>> GetAsync(string name, CancellationToken cancellationToken = default) {
        using var request = context.CreateRequest(HttpMethod.Get, Path(name));
        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsError)
            throw CyberCloudClientContext.CreateFailure(response);

        var data = JsonSerializer.Deserialize(response.Content.Span, WidgetJsonContext.Default.WidgetData)!;

        return Response.FromValue(new WidgetResource(context, new Uri(context.Endpoint, Path(name)), data), response);
    }

    /// <summary>The <c>GetIfExists</c> shape — a <c>404</c> is an answer, not an exception.</summary>
    public async Task<NullableResponse<WidgetResource>> GetIfExistsAsync(string name, CancellationToken cancellationToken = default) {
        using var request = context.CreateRequest(HttpMethod.Get, Path(name));
        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Status == 404)
            return NullableResponse<WidgetResource>.FromNoValue(response);

        if (response.IsError)
            throw CyberCloudClientContext.CreateFailure(response);

        var data = JsonSerializer.Deserialize(response.Content.Span, WidgetJsonContext.Default.WidgetData)!;

        return Response.FromValue(new WidgetResource(context, new Uri(context.Endpoint, Path(name)), data), response);
    }

    public async Task<Operation<WidgetResource>> CreateOrUpdateAsync(
        WaitUntil waitUntil,
        string name,
        WidgetData data,
        CancellationToken cancellationToken = default) {
        var uri = new Uri(context.Endpoint, Path(name));

        using var request = context.CreateRequest(HttpMethod.Put, uri);
        CyberCloudClientContext.SetJsonBody(request, JsonSerializer.SerializeToUtf8Bytes(data, WidgetJsonContext.Default.WidgetData));

        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsError)
            throw CyberCloudClientContext.CreateFailure(response);

        var operation = new Operation<WidgetResource>(
            new WidgetOperationSource(context, uri),
            context,
            uri,
            response,
            "Widgets.CreateOrUpdate");

        if (waitUntil == WaitUntil.Completed)
            await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

        return operation;
    }

    public AsyncPageable<WidgetData> GetAll(CancellationToken cancellationToken = default)
        => AsyncPageable<WidgetData>.Create(
            async (continuationToken, pageSizeHint, token) => {
                using var request = continuationToken is null
                    ? context.CreateRequest(HttpMethod.Get, $"{scope}/providers/CyberCloud.Sample/widgets")
                    : context.CreateRequest(HttpMethod.Get, new Uri(continuationToken));

                var response = await context.Pipeline.SendAsync(request, token).ConfigureAwait(false);

                if (response.IsError)
                    throw CyberCloudClientContext.CreateFailure(response);

                var page = JsonSerializer.Deserialize(response.Content.Span, WidgetJsonContext.Default.WidgetListPage)!;

                return new Page<WidgetData>(page.Value, page.NextLink, response);
            },
            cancellationToken);
}
