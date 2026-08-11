using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CyberCloud.Sdk;

/// <summary>
///     The source-generated <see cref="JsonSerializerContext" /> every deserialisation in this
///     assembly goes through.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type is the mechanism behind docs/plan/21 § The .NET SDK's AOT claim.</b> That
///         section makes the CLI single-file AOT-published and makes the CLI depend on this SDK, and
///         concludes: <i>"Owning the stack means owning the serialization: source-generated
///         <c>System.Text.Json</c> throughout, and an AOT warning becomes a bug we can fix rather than
///         a dependency we must live with."</i> Reflection-based
///         <c>JsonSerializer.Deserialize&lt;T&gt;(json)</c> is exactly such a warning:
///         <c>IsAotCompatible</c> in the .csproj turns it into IL2026/IL3050 at the call site, so the
///         rule is enforced by the compiler rather than by review.
///     </para>
///     <para>
///         Adding a wire type means adding a <see cref="JsonSerializableAttribute" /> line here. There
///         is no other correct way to serialise one in this assembly.
///     </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenIdConfiguration))]
[JsonSerializable(typeof(TokenPayload))]
[JsonSerializable(typeof(TokenErrorPayload))]
[JsonSerializable(typeof(DeviceAuthorizationPayload))]
[JsonSerializable(typeof(JsonWebKeySet))]
[JsonSerializable(typeof(JsonWebKey))]
[JsonSerializable(typeof(CliTokenPayload))]
[JsonSerializable(typeof(TokenCacheRecord))]
// `string` is here for one caller: the JWT client assertion's `aud`, which must be JSON-escaped
// because a token endpoint URL can legitimately contain a character that would break the literal.
[JsonSerializable(typeof(string))]
internal partial class SdkJsonContext : JsonSerializerContext {
    /// <summary>
    ///     Deserialises with the generated metadata, turning a malformed body into an
    ///     <see cref="AuthenticationFailedException" /> that names what was being read.
    /// </summary>
    /// <remarks>
    ///     ⚠ The message names the document, never the body: an identity server's response can carry
    ///     an authorization code or a token, and an exception that quoted it would put credential
    ///     material in a log the first time somebody caught and logged this.
    /// </remarks>
    public static T Read<T>(ReadOnlyMemory<byte> content, JsonTypeInfo<T> typeInfo, string what) {
        try {
            var value = JsonSerializer.Deserialize(content.Span, typeInfo);

            return value ?? throw new AuthenticationFailedException($"The identity server returned an empty {what}.");
        } catch (JsonException e) {
            throw new AuthenticationFailedException($"The identity server's {what} could not be parsed.", e);
        }
    }
}
