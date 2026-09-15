using System.Diagnostics.CodeAnalysis;
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
//  ⚠ THAT WARNING ONLY FIRES OVER SHAPES THIS FILE ACTUALLY MIRRORS, and issue #73 is the proof.
//  SdkEmitter made `{Type}Resource.Data` `required` and EmitterContract.cs § 1 grew the clause that
//  says a hand-written constructor assigning it owes `[SetsRequiredMembers]` — and this file, which
//  is that clause's only instance, went on compiling because `WidgetResource` still declared a
//  plain `{ get; }`. A stand-in that does not move when the contract moves checks nothing. So: a
//  change to EmitterContract.cs § 1 is a change here, in the same commit.
//
//  Read src/CyberCloud.Sdk/EmitterContract.cs first. It is the contract; this is one instance of it.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The body of a <c>CyberCloud.Sample/widgets</c> — openapi/2026-08-01.json § CyberCloud.Sample.widgets.</summary>
public sealed partial class WidgetData {
    public WidgetData(string location) {
        Location = location;
    }

    [JsonPropertyName("location")]
    public string Location { get; init; }

    [JsonPropertyName("properties")]
    public PropertiesData? Properties { get; init; }

    // ⚠ A NESTED CLASS PER CONTAINER, NAMED `{Name}Data`, AND THIS IS THE {Type}Data ROW OF
    // EmitterContract.cs § 1 SINCE ISSUE #79. The body is nested on the wire and so is the model:
    // a leaf is declared inside the class its parent declares, so `[JsonPropertyName]` is the
    // leaf's own name at every depth. Until 2026-09-15 this was a top-level `WidgetProperties`,
    // which SdkEmitter never emitted — it flattened every leaf onto one class, and the nested
    // leaves carried wire names that collided (fourteen of them, over eight types). The stand-in
    // mirrors the emitted shape rather than describing it, for the header's reason.
    /// <summary>The widget's own settings.</summary>
    public sealed partial class PropertiesData {
        [JsonPropertyName("clusterId")]
        public string ClusterId { get; init; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; init; }
    }
}

/// <summary>
///     The generated <c>JsonSerializerContext</c>. ⚠ Source-generated, never reflection — see
///     EmitterContract.cs § 1: the SDK sets <c>IsAotCompatible</c>, so a reflective
///     <c>JsonSerializer.Deserialize&lt;T&gt;</c> is IL2026 and a build failure.
/// </summary>
/// <remarks>
///     ⚠ <c>UseStringEnumConverter</c> and the <see cref="ResourceEnvelope{TProvisioningState}" />
///     entry are what the read envelope costs the context — the 2026-09-15 review of issue #85.
///     The envelope's <c>provisioningState</c> is a string on the wire and an enum on
///     <see cref="WidgetResource" />, and only this option makes the source generator read the
///     <c>[JsonStringEnumMemberName]</c> the emitter puts on every member; the closed instantiation
///     has to be declared here rather than in <c>SdkJsonContext</c> because it names this file's
///     enum. Drop either and every read fails to parse, which <c>EnvelopeTests</c> would report.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WidgetData))]
[JsonSerializable(typeof(WidgetListPage))]
[JsonSerializable(typeof(ResourceEnvelope<ProvisioningState>), TypeInfoPropertyName = "WidgetEnvelope")]
public partial class WidgetJsonContext : JsonSerializerContext;

/// <summary>A page of a list response.</summary>
/// <remarks>
///     ⚠ The elements are left as <see cref="JsonElement" /> rather than typed, because each one is
///     read twice — <see cref="WidgetResource.Read(CyberCloudClientContext, JsonElement)" /> says
///     why — and a page typed as <c>IReadOnlyList&lt;WidgetData&gt;</c> is a page that has already
///     dropped the envelope. It was that until the 2026-09-15 review of issue #85, and
///     <c>GetAll</c> yielded bodies with no id.
/// </remarks>
public sealed partial class WidgetListPage {
    [JsonPropertyName("value")]
    public IReadOnlyList<JsonElement> Value { get; init; } = [];

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; init; }
}

/// <summary>One widget.</summary>
public sealed partial class WidgetResource {
    // ⚠ `required Data` AND [SetsRequiredMembers], AND THIS PAIR IS THE POINT OF THE FILE — issue
    // #73. SdkEmitter now emits `public required {Model}Data Data { get; init; }` on every
    // {Type}Resource; it was `= new()` until 2026-09-05, which is CS9035 against a body whose
    // members are `required`, and 110 of those were checked into generated/sdk/2026-08-01.cs
    // unnoticed. EmitterContract.cs § 1's {Type}Resource row records what that asks of the
    // hand-written half, and a contract with no instance is a sentence rather than a check — so the
    // emitted shape is mirrored here rather than described.
    //
    // ⚠ WHAT BREAKS IF THE ATTRIBUTE GOES. Not this constructor: a constructor may leave a required
    // member unset. Every `new WidgetResource(...)` below becomes CS9035 instead — the call sites in
    // WidgetCollection and WidgetOperationSource, which are the shapes an emitted client is made of.
    // Three of them, and verified by deleting the attribute on 2026-09-05: three CS9035s. That is
    // the header's "this file stops compiling" promise reaching past accessibility, which used to be
    // the only thing it covered.
    [SetsRequiredMembers]
    public WidgetResource(CyberCloudClientContext context, Uri uri, WidgetData data) {
        Context = context;
        Uri = uri;
        Data = data;
    }

    public CyberCloudClientContext Context { get; }

    public Uri Uri { get; }

    // ⚠ THE READ ENVELOPE, FROM THE DOCUMENT — issue #85. SdkEmitter emits one member per leaf of
    // the Resource component the type's schema allOf's (openapi/2026-08-01.json § Resource): the
    // five the gateway serves beside the body, each with its wire name, initialised rather than
    // `required` because the hand-written half sets them from a response after construction —
    // `Read` below is that half, and until the 2026-09-15 review of the issue it did not exist:
    // the five were declared, every call site built the resource from the body alone, and Id was
    // string.Empty on every resource the stand-in ever produced. Until the issue itself the
    // emitted class declared `Id` alone, from a literal, and the document described none of the
    // five. Mirrored here rather than described, for the header's reason.
    [JsonPropertyName("etag")]
    public string Etag { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("provisioningState")]
    public ProvisioningState ProvisioningState { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    public required WidgetData Data { get; init; }

    /// <summary>
    ///     Reads one served resource — a <c>GET</c> <c>200</c>, a list element or the <c>GET</c>
    ///     that follows an operation — into the envelope and the body.
    /// </summary>
    /// <param name="context">The client context the resource keeps.</param>
    /// <param name="served">The resource object, exactly as <c>ResponseBodies.Resource</c> wrote it.</param>
    /// <remarks>
    ///     ⚠ <b>The hand-written half's one way to make a resource from a response, and the same
    ///     bytes are read twice.</b> The wire is flat and the SDK is not — the remarks on
    ///     <see cref="ResourceEnvelope{TProvisioningState}" /> carry the argument — so the element is
    ///     deserialised once as the envelope and once as <see cref="WidgetData" />, and each read
    ///     ignores the other's members. The URL is the envelope's <c>id</c>, which the document
    ///     describes as "also the URL it was read from", so a list element gets the same URL a
    ///     <c>GET</c> of it would.
    /// </remarks>
    public static WidgetResource Read(CyberCloudClientContext context, JsonElement served) {
        var envelope = JsonSerializer.Deserialize(served, WidgetJsonContext.Default.WidgetEnvelope)!;
        var data = JsonSerializer.Deserialize(served, WidgetJsonContext.Default.WidgetData)!;

        return new WidgetResource(context, new Uri(context.Endpoint, envelope.Id), data) {
            Id = envelope.Id,
            Name = envelope.Name,
            Type = envelope.Type,
            ProvisioningState = envelope.ProvisioningState,
            Etag = envelope.Etag
        };
    }

    /// <inheritdoc cref="Read(CyberCloudClientContext, JsonElement)" />
    /// <param name="content">A response body that is one resource object.</param>
    public static WidgetResource Read(CyberCloudClientContext context, ReadOnlyMemory<byte> content) {
        using var document = JsonDocument.Parse(content);

        return Read(context, document.RootElement);
    }
}

/// <summary>
///     The read envelope's closed set, declared once per generated file — the shape
///     <c>SdkEmitter.AppendEnvelopeEnums</c> emits from the document's <c>Resource</c> component.
/// </summary>
public enum ProvisioningState {
    Unknown = 0,

    [JsonStringEnumMemberName("Canceled")]
    Canceled = 1,

    [JsonStringEnumMemberName("Creating")]
    Creating = 2,

    [JsonStringEnumMemberName("Deleting")]
    Deleting = 3,

    [JsonStringEnumMemberName("Failed")]
    Failed = 4,

    [JsonStringEnumMemberName("Succeeded")]
    Succeeded = 5,

    [JsonStringEnumMemberName("Updating")]
    Updating = 6
}

/// <summary>
///     Turns the resource <c>GET</c> that follows a successful operation into a
///     <see cref="WidgetResource" /> — EmitterContract.cs § 1's <c>{Type}OperationSource</c>.
/// </summary>
public sealed partial class WidgetOperationSource : IOperationSource<WidgetResource> {
    readonly CyberCloudClientContext context;

    public WidgetOperationSource(CyberCloudClientContext context) {
        this.context = context;
    }

    public ValueTask<WidgetResource> CreateResultAsync(Response response, CancellationToken cancellationToken) =>
        ValueTask.FromResult(WidgetResource.Read(context, response.Content));
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

        if (response.IsError) {
            throw CyberCloudClientContext.CreateFailure(response);
        }

        return Response.FromValue(WidgetResource.Read(context, response.Content), response);
    }

    /// <summary>The <c>GetIfExists</c> shape — a <c>404</c> is an answer, not an exception.</summary>
    public async Task<NullableResponse<WidgetResource>> GetIfExistsAsync(
        string name,
        CancellationToken cancellationToken = default
    ) {
        using var request = context.CreateRequest(HttpMethod.Get, Path(name));
        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Status == 404) {
            return NullableResponse<WidgetResource>.FromNoValue(response);
        }

        if (response.IsError) {
            throw CyberCloudClientContext.CreateFailure(response);
        }

        return Response.FromValue(WidgetResource.Read(context, response.Content), response);
    }

    public async Task<Operation<WidgetResource>> CreateOrUpdateAsync(
        WaitUntil waitUntil,
        string name,
        WidgetData data,
        CancellationToken cancellationToken = default
    ) {
        var uri = new Uri(context.Endpoint, Path(name));

        using var request = context.CreateRequest(HttpMethod.Put, uri);
        CyberCloudClientContext.SetJsonBody(
            request,
            JsonSerializer.SerializeToUtf8Bytes(data, WidgetJsonContext.Default.WidgetData)
        );

        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsError) {
            throw CyberCloudClientContext.CreateFailure(response);
        }

        var operation = new Operation<WidgetResource>(
            new WidgetOperationSource(context),
            context,
            uri,
            response,
            "Widgets.CreateOrUpdate"
        );

        if (waitUntil == WaitUntil.Completed) {
            await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
        }

        return operation;
    }

    public AsyncPageable<WidgetResource> GetAll(CancellationToken cancellationToken = default) =>
        AsyncPageable<WidgetResource>.Create(
            async (continuationToken, pageSizeHint, token) => {
                using var request = continuationToken is null
                    ? context.CreateRequest(HttpMethod.Get, $"{scope}/providers/CyberCloud.Sample/widgets")
                    : context.CreateRequest(HttpMethod.Get, new Uri(continuationToken));

                var response = await context.Pipeline.SendAsync(request, token).ConfigureAwait(false);

                if (response.IsError) {
                    throw CyberCloudClientContext.CreateFailure(response);
                }

                var page = JsonSerializer.Deserialize(response.Content.Span, WidgetJsonContext.Default.WidgetListPage)!;

                return new Page<WidgetResource>(
                    [.. page.Value.Select(element => WidgetResource.Read(context, element))],
                    page.NextLink,
                    response
                );
            },
            cancellationToken
        );
}
