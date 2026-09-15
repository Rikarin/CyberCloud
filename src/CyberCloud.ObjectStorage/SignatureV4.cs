using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.ObjectStorage;

/// <summary>
///     AWS Signature Version 4, for the S3 service, header-based. Pure functions over strings and
///     bytes, so a test can pin them against Amazon's published vectors.
/// </summary>
/// <remarks>
///     <para>
///         The algorithm, as the S3 documentation states it and in the order it states it: a
///         <i>canonical request</i> is hashed into a <i>string to sign</i>, which is HMACed with a
///         <i>signing key</i> derived from the secret through the date, the region, the service and
///         the literal <c>aws4_request</c>. Every step here is one of those sentences.
///     </para>
///     <para>
///         ⚠ <b>S3 encodes the path once, and this signer follows S3 rather than the general SigV4
///         rule.</b> The general algorithm URI-encodes each path segment twice; S3's own examples —
///         <c>/test$file.text</c> — encode it once, and a signer that double-encoded would compute a
///         different canonical request from the one the service computes for the same bytes on the
///         wire. <c>S3ObjectStoreSigningTests.ThePutExampleFromTheS3Documentation</c> is the vector
///         that pins which one this is.
///     </para>
///     <para>
///         ⚠ <b>The unreserved set is RFC 3986's and nothing more.</b> <c>Uri.EscapeDataString</c>
///         leaves <c>!</c>, <c>*</c>, <c>'</c>, <c>(</c> and <c>)</c> alone on some runtimes and
///         encodes them on others, and a key carrying any of them would then sign differently from
///         how the service reads it. <see cref="Encode" /> is written out so the set is exactly
///         <c>A–Z a–z 0–9 - _ . ~</c>, which is what every SigV4 implementation Amazon ships uses.
///     </para>
/// </remarks>
public static class SignatureV4 {
    /// <summary>The algorithm name every string to sign and every Authorization header opens with.</summary>
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The service name in the credential scope.</summary>
    public const string Service = "s3";

    /// <summary>The hex SHA-256 of zero bytes — what a body-less request declares as its payload hash.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>One request, in the form the canonicalisation reads.</summary>
    /// <param name="Method">The HTTP method, upper case.</param>
    /// <param name="Path">
    ///     The absolute path, decoded — <c>/bucket/some key</c>. Encoded once here, per segment.
    /// </param>
    /// <param name="Query">The query parameters, decoded. Empty values are legal (<c>?lifecycle</c>).</param>
    /// <param name="Headers">
    ///     Every header that is signed, with the value exactly as it goes on the wire. ⚠ Must
    ///     include <c>host</c>, <c>x-amz-date</c> and <c>x-amz-content-sha256</c>; the store adds all
    ///     three.
    /// </param>
    /// <param name="PayloadHash">The hex SHA-256 of the body, or <see cref="EmptyPayloadHash" />.</param>
    public sealed record Request(
        string Method,
        string Path,
        IReadOnlyList<KeyValuePair<string, string>> Query,
        IReadOnlyDictionary<string, string> Headers,
        string PayloadHash
    );

    /// <summary>The credential scope — <c>{yyyyMMdd}/{region}/s3/aws4_request</c>.</summary>
    /// <param name="date">The request's date, UTC.</param>
    /// <param name="region">The region.</param>
    public static string Scope(DateTimeOffset date, string region) =>
        $"{date.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{region}/{Service}/aws4_request";

    /// <summary>The <c>x-amz-date</c> value — <c>yyyyMMddTHHmmssZ</c>.</summary>
    /// <param name="date">The request's date, UTC.</param>
    public static string AmzDate(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The canonical request — the five lines plus the payload hash, newline-joined.</summary>
    /// <param name="request">The request.</param>
    public static string CanonicalRequest(Request request) {
        ArgumentNullException.ThrowIfNull(request);

        var headers = request.Headers
            .Select(x => (Name: x.Key.ToLowerInvariant(), Value: Collapse(x.Value)))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.Append(request.Method).Append('\n');
        builder.Append(CanonicalPath(request.Path)).Append('\n');
        builder.Append(CanonicalQuery(request.Query)).Append('\n');

        foreach (var (name, value) in headers) {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        builder.Append('\n');
        builder.Append(SignedHeaders(request.Headers)).Append('\n');
        builder.Append(request.PayloadHash);

        return builder.ToString();
    }

    /// <summary>The <c>SignedHeaders</c> list — lower-cased names, sorted, <c>;</c>-joined.</summary>
    /// <param name="headers">The signed headers.</param>
    public static string SignedHeaders(IReadOnlyDictionary<string, string> headers) {
        ArgumentNullException.ThrowIfNull(headers);

        return string.Join(';', headers.Keys.Select(x => x.ToLowerInvariant()).Order(StringComparer.Ordinal));
    }

    /// <summary>The string to sign — the algorithm, the date, the scope and the canonical request's hash.</summary>
    /// <param name="canonicalRequest">What <see cref="CanonicalRequest" /> produced.</param>
    /// <param name="date">The request's date.</param>
    /// <param name="region">The region.</param>
    public static string StringToSign(string canonicalRequest, DateTimeOffset date, string region) =>
        $"{Algorithm}\n{AmzDate(date)}\n{Scope(date, region)}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

    /// <summary>The signing key — four HMACs deep, from the secret through the scope's parts.</summary>
    /// <param name="secretAccessKey">The secret half of the credential.</param>
    /// <param name="date">The request's date.</param>
    /// <param name="region">The region.</param>
    public static byte[] SigningKey(string secretAccessKey, DateTimeOffset date, string region) {
        var kDate = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("AWS4" + secretAccessKey),
            Encoding.UTF8.GetBytes(date.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
        );
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(Service));

        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    /// <summary>The signature — the string to sign under the signing key, hex.</summary>
    /// <param name="request">The request.</param>
    /// <param name="date">The request's date.</param>
    /// <param name="region">The region.</param>
    /// <param name="secretAccessKey">The secret half of the credential.</param>
    public static string Sign(Request request, DateTimeOffset date, string region, string secretAccessKey) =>
        Hex(
            HMACSHA256.HashData(
                SigningKey(secretAccessKey, date, region),
                Encoding.UTF8.GetBytes(StringToSign(CanonicalRequest(request), date, region))
            )
        );

    /// <summary>The <c>Authorization</c> header value.</summary>
    /// <param name="request">The request.</param>
    /// <param name="date">The request's date.</param>
    /// <param name="region">The region.</param>
    /// <param name="accessKeyId">The public half of the credential.</param>
    /// <param name="secretAccessKey">The secret half.</param>
    public static string Authorization(
        Request request,
        DateTimeOffset date,
        string region,
        string accessKeyId,
        string secretAccessKey
    ) {
        ArgumentNullException.ThrowIfNull(request);

        return $"{Algorithm} Credential={accessKeyId}/{Scope(date, region)}, "
            + $"SignedHeaders={SignedHeaders(request.Headers)}, "
            + $"Signature={Sign(request, date, region, secretAccessKey)}";
    }

    /// <summary>Lower-case hex of a digest.</summary>
    /// <param name="bytes">The digest.</param>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>URI-encodes one value with RFC 3986's unreserved set and nothing more.</summary>
    /// <param name="value">The decoded value.</param>
    /// <param name="keepSlash">Whether <c>/</c> is left alone — true for a path, false for a query value.</param>
    public static string Encode(string value, bool keepSlash) {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);

        foreach (var b in Encoding.UTF8.GetBytes(value)) {
            var c = (char)b;

            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~'
                || (keepSlash && c == '/')) {
                builder.Append(c);
            } else {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    static string CanonicalPath(string path) => path.Length == 0 ? "/" : Encode(path, keepSlash: true);

    static string CanonicalQuery(IReadOnlyList<KeyValuePair<string, string>> query) =>
        string.Join(
            '&',
            query
                .Select(x => (Name: Encode(x.Key, keepSlash: false), Value: Encode(x.Value, keepSlash: false)))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Value, StringComparer.Ordinal)
                .Select(x => x.Name + "=" + x.Value)
        );

    /// <summary>Trims a header value and collapses runs of spaces, as the canonical form requires.</summary>
    static string Collapse(string value) {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var c in value.Trim()) {
            if (c == ' ') {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace) {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
