using System.Text.Json;

namespace CyberCloud.Identity.Host.Api;

/// <summary>
///     The one thing this host reads out of a WebAuthn assertion before handing it to the library.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Only the credential id, and only to look the credential up.</b>
///         <c>IPasskeyService.CompleteAssertionAsync</c> needs the stored public key to verify
///         against, and finding it needs to know which of the user's credentials answered — a
///         chicken-and-egg the WebAuthn spec resolves by putting the id in the clear at the top of
///         the response. Everything else in that JSON is the library's to parse, and parsing more of
///         it here would be the "page rebuilds the options" mistake one layer down.
///     </para>
///     <para>
///         ⚠ <b>The id read here is untrusted and the code treats it that way.</b> It selects a
///         lookup and nothing else; the assertion is then verified against <i>that stored
///         credential's</i> public key and against the challenge this server issued. A caller who
///         names somebody else's credential id gets a signature check against a key they do not hold.
///     </para>
/// </remarks>
public static class PasskeyAssertion {
    /// <summary>
    ///     The base64url credential id an assertion names.
    /// </summary>
    /// <param name="assertionJson">The browser's <c>navigator.credentials.get()</c> result.</param>
    /// <returns>
    ///     The id, or <see langword="null" /> when the JSON does not parse or carries no usable
    ///     <c>id</c>. ⚠ Never an exception: this runs on an unauthenticated endpoint, so malformed
    ///     input is an expected value rather than an exceptional one.
    /// </returns>
    public static string? CredentialIdOf(string? assertionJson) {
        if (string.IsNullOrWhiteSpace(assertionJson)) {
            return null;
        }

        try {
            using var document = JsonDocument.Parse(assertionJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return null;
            }

            // ⚠ `id` and not `rawId`. Both name the same credential — `rawId` is the ArrayBuffer and
            // `id` its base64url text — but Fido2PasskeyService stores `PasskeyCredential.CredentialId`
            // base64url, so `id` is the one that compares ordinally without a re-encode. Reading
            // `rawId` would mean decoding a caller-supplied buffer to compare it, which is work on an
            // unauthenticated path for no gain.
            return document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } value
                    ? value
                    : null;
        } catch (JsonException) {
            return null;
        }
    }
}
