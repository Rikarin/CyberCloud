using System.Text.Json.Serialization;

namespace CyberCloud.Sdk;

/// <summary>
///     The five members every read carries beside the body — <c>openapi/{version}.json</c>
///     § Resource — as the hand-written half reads them off a response before it constructs a
///     generated <c>{Type}Resource</c>.
/// </summary>
/// <typeparam name="TProvisioningState">
///     The generated file's <c>ProvisioningState</c> enum. A type parameter because that enum is
///     emitted per api-version from the document's own closed set, and this assembly cannot name a
///     type the generator has not written yet.
/// </typeparam>
/// <remarks>
///     <para>
///         ⚠ <b>The wire is flat and the SDK is not, so one response is read twice.</b> The gateway
///         serves <c>id</c>, <c>name</c>, <c>type</c>, <c>location</c>, <c>provisioningState</c>,
///         <c>etag</c>, <c>properties</c> and <c>tags</c> at one level; EmitterContract.cs § 1 puts
///         the five the server owns on <c>{Type}Resource</c> and the body on <c>{Type}Data</c>, and
///         no write surface carries the five. The hand-written half therefore deserialises the same
///         bytes once as this type and once as the body, then constructs the resource from both.
///         <c>System.Text.Json</c> skips what a type does not declare, which is what makes two reads
///         of one document correct rather than a trick: neither read sees the other's members. The
///         stand-in in <c>CyberCloud.Sdk.Tests/StandIn/WidgetStandIn.cs</c> is the instance, and
///         <c>EnvelopeTests</c> reads all five back off a <c>GET</c>, a list element and an
///         operation's value.
///     </para>
///     <para>
///         ⚠ <b>Registered in the generated file's <c>JsonSerializerContext</c>, not in
///         <see cref="SdkJsonContext" />.</b> The closed instantiation names the file's enum, so
///         only that file can declare it — and that context needs
///         <c>UseStringEnumConverter = true</c>, because the wire's <c>"Creating"</c> reaches the
///         enum through the <c>[JsonStringEnumMemberName]</c> the emitter puts on every member.
///         Without the option the property is read as a number and every response fails to parse.
///         A value the document's enum does not declare is a <c>JsonException</c> rather than a
///         silent <c>Unknown</c>: an api-version is immutable, so a word this file has no member
///         for is a server that is not speaking this version.
///     </para>
/// </remarks>
public sealed class ResourceEnvelope<TProvisioningState> where TProvisioningState : struct, Enum {
    /// <summary>The resource's own path — docs/plan/06 § Identifiers — which is also the URL it was read from.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The last segment of the path: the name the caller chose on the <c>PUT</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The fully qualified resource type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Where the resource is in its lifecycle. ⚠ <c>Deleting</c> is a state a listing still shows.</summary>
    [JsonPropertyName("provisioningState")]
    public TProvisioningState ProvisioningState { get; init; }

    /// <summary>The concurrency token — send it back as <c>If-Match</c> on a write to refuse a lost update.</summary>
    [JsonPropertyName("etag")]
    public string Etag { get; init; } = string.Empty;
}
