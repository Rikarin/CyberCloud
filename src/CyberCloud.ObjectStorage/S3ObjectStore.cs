using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace CyberCloud.ObjectStorage;

/// <summary>
///     The <see cref="IObjectStore" /> over an S3-compatible endpoint — SeaweedFS in this platform,
///     docs/plan/15 § Object storage — signed with <see cref="SignatureV4" />.
/// </summary>
/// <remarks>
///     <para>
///         Four verbs and nothing else: <c>PUT</c> an object, <c>GET</c> it, <c>DELETE</c> it, and
///         <c>ListObjectsV2</c> under a prefix. That is the whole of what a feed needs
///         (docs/plan/13 § Artifact feeds) and each is one request; multipart upload, versioning,
///         lifecycle and ACLs are S3 features this platform does not use and this class does not
///         speak.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The payload is hashed and the hash is signed, which is what makes a body that
///             changed in flight a refused request rather than a corrupt artefact.
///         </b> <c>UNSIGNED-PAYLOAD</c> would let the store skip the SHA-256 and stream; every caller
///         already holds the whole artefact — a nupkg is a zip that has to be opened for its nuspec,
///         an npm tarball arrives base64 inside a JSON document — so the hash is cheap and the
///         integrity is free.
///     </para>
///     <para>
///         ⚠ <b>A refusal names the endpoint and the status, never the credential.</b> The secret is
///         read into an HMAC key inside <see cref="SignatureV4" /> and appears in no message, no log
///         and no exception; <c>S3ObjectStoreTests.NoRefusalCarriesTheCredential</c> is the
///         assertion.
///     </para>
/// </remarks>
public sealed class S3ObjectStore : IObjectStore {
    readonly HttpClient http;
    readonly ObjectStorageOptions options;
    readonly IClock clock;
    readonly Uri endpoint;

    /// <summary>Creates a store over one endpoint and one bucket.</summary>
    /// <param name="http">The client. Long-lived; <c>AddS3ObjectStore</c> creates one per store.</param>
    /// <param name="options">Where and how to sign. Validated by <c>AddS3ObjectStore</c>, and again here.</param>
    /// <param name="clock">For <c>x-amz-date</c>. ⚠ A skewed clock is a refused request: S3 allows 15 minutes.</param>
    public S3ObjectStore(HttpClient http, ObjectStorageOptions options, IClock clock) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        this.http = http;
        this.options = options;
        this.clock = clock;
        endpoint = ValidatedEndpoint(options);
    }

    /// <summary>
    ///     Checks the options a store can be built from, and throws naming the key that is wrong.
    /// </summary>
    /// <param name="options">The section.</param>
    /// <returns>The endpoint, parsed.</returns>
    /// <exception cref="ArgumentException">A key is empty, the endpoint is not absolute, or it is plain HTTP without opting in.</exception>
    public static Uri ValidatedEndpoint(ObjectStorageOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")) {
            throw new ArgumentException(
                $"{ObjectStorageOptions.SectionName}:Endpoint must be an absolute http(s) URL, and "
                + $"'{options.Endpoint}' is not.",
                nameof(options)
            );
        }

        if (parsed.Scheme == "http" && !options.AllowInsecureTransport) {
            throw new ArgumentException(
                $"{ObjectStorageOptions.SectionName}:Endpoint is plain http and AllowInsecureTransport "
                + "is off. A signed credential over clear text is a credential; set the flag only for "
                + "a development endpoint.",
                nameof(options)
            );
        }

        foreach (var (name, value) in new[] {
                     ("Bucket", options.Bucket),
                     ("AccessKeyId", options.AccessKeyId),
                     ("SecretAccessKey", options.SecretAccessKey),
                     ("Region", options.Region)
                 }) {
            if (string.IsNullOrWhiteSpace(value)) {
                throw new ArgumentException($"{ObjectStorageOptions.SectionName}:{name} is empty.", nameof(options));
            }
        }

        return parsed;
    }

    /// <inheritdoc />
    public async Task<Result> PutAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken = default
    ) {
        if (ObjectKeys.Validate(key).TryGetError(out var invalid)) {
            return Result.Failure(invalid);
        }

        using var request = Build(HttpMethod.Put, key, [], content, contentType);

        try {
            using var response = await http.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode
                ? Result.Success
                : Result.Failure(Refused("store", key, response.StatusCode));
        } catch (Exception exception) when (IsTransport(exception)) {
            return Result.Failure(Unreachable("store", exception));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StoredObject>> GetAsync(string key, CancellationToken cancellationToken = default) {
        if (ObjectKeys.Validate(key).TryGetError(out var invalid)) {
            return Result<StoredObject>.Failure(invalid);
        }

        var request = Build(HttpMethod.Get, key, [], ReadOnlyMemory<byte>.Empty, null);
        HttpResponseMessage? response = null;

        try {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound) {
                response.Dispose();
                request.Dispose();
                return Result<StoredObject>.Failure(ErrorCode.ResourceNotFound, $"'{key}' holds nothing.");
            }

            if (!response.IsSuccessStatusCode) {
                var status = response.StatusCode;
                response.Dispose();
                request.Dispose();
                return Result<StoredObject>.Failure(Refused("read", key, status));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            return Result<StoredObject>.Success(
                new(
                    // ⚠ Disposing the stream disposes the response and the request with it, so a
                    // caller that follows the seam's contract — dispose what you were handed — leaks
                    // no connection.
                    new OwningStream(stream, response, request),
                    response.Content.Headers.ContentLength ?? -1,
                    response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
                )
            );
        } catch (Exception exception) when (IsTransport(exception)) {
            response?.Dispose();
            request.Dispose();
            return Result<StoredObject>.Failure(Unreachable("read", exception));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string key, CancellationToken cancellationToken = default) {
        if (ObjectKeys.Validate(key).TryGetError(out var invalid)) {
            return Result.Failure(invalid);
        }

        using var request = Build(HttpMethod.Delete, key, [], ReadOnlyMemory<byte>.Empty, null);

        try {
            using var response = await http.SendAsync(request, cancellationToken);

            // 204 for a delete, 404 for a key that held nothing: both leave the key empty, which is
            // what the seam promises.
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
                ? Result.Success
                : Result.Failure(Refused("remove", key, response.StatusCode));
        } catch (Exception exception) when (IsTransport(exception)) {
            return Result.Failure(Unreachable("remove", exception));
        }
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<string>>> ListAsync(string prefix, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(prefix);

        var keys = ImmutableArray.CreateBuilder<string>();
        string? continuation = null;

        do {
            var query = new List<KeyValuePair<string, string>> {
                new("list-type", "2"),
                new("prefix", prefix),
                new("max-keys", "1000")
            };

            if (continuation is not null) {
                query.Add(new("continuation-token", continuation));
            }

            using var request = Build(HttpMethod.Get, null, query, ReadOnlyMemory<byte>.Empty, null);

            try {
                using var response = await http.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode) {
                    return Result<ImmutableArray<string>>.Failure(Refused("list", prefix, response.StatusCode));
                }

                var page = ParseListing(await response.Content.ReadAsStringAsync(cancellationToken));
                if (page.TryGetError(out var malformed)) {
                    return Result<ImmutableArray<string>>.Failure(malformed);
                }

                var (found, next) = page.GetValueOrThrow();
                keys.AddRange(found);
                continuation = next;
            } catch (Exception exception) when (IsTransport(exception)) {
                return Result<ImmutableArray<string>>.Failure(Unreachable("list", exception));
            }
        } while (continuation is not null);

        return Result<ImmutableArray<string>>.Success(keys.ToImmutable().Sort(StringComparer.Ordinal));
    }

    /// <summary>Reads the keys and the continuation token out of a <c>ListBucketResult</c>.</summary>
    /// <param name="xml">The response body.</param>
    /// <returns>The keys in this page, and the token for the next page or <see langword="null" />.</returns>
    /// <remarks>
    ///     Namespace-agnostic on purpose: Amazon's document declares
    ///     <c>http://s3.amazonaws.com/doc/2006-03-01/</c>, SeaweedFS's declares the same, and a
    ///     third store could declare none. Element local names are the contract.
    /// </remarks>
    internal static Result<(ImmutableArray<string> Keys, string? Continuation)> ParseListing(string xml) {
        XDocument document;

        try {
            document = XDocument.Parse(xml);
        } catch (System.Xml.XmlException exception) {
            return Result<(ImmutableArray<string>, string?)>.Failure(
                ErrorCode.InternalError,
                "The object store answered a listing with something that is not XML: " + exception.Message
            );
        }

        if (document.Root is not { } root || root.Name.LocalName != "ListBucketResult") {
            return Result<(ImmutableArray<string>, string?)>.Failure(
                ErrorCode.InternalError,
                "The object store answered a listing with a document that is not a ListBucketResult."
            );
        }

        var keys = root.Elements()
            .Where(x => x.Name.LocalName == "Contents")
            .Select(x => x.Elements().FirstOrDefault(y => y.Name.LocalName == "Key")?.Value)
            .OfType<string>()
            .ToImmutableArray();

        var truncated = string.Equals(
            root.Elements().FirstOrDefault(x => x.Name.LocalName == "IsTruncated")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase
        );

        var next = root.Elements().FirstOrDefault(x => x.Name.LocalName == "NextContinuationToken")?.Value;

        return Result<(ImmutableArray<string>, string?)>.Success(
            (keys, truncated && !string.IsNullOrEmpty(next) ? next : null)
        );
    }

    HttpRequestMessage Build(
        HttpMethod method,
        string? key,
        List<KeyValuePair<string, string>> query,
        ReadOnlyMemory<byte> body,
        string? contentType
    ) {
        var now = clock.UtcNow;
        var path = "/" + options.Bucket + (key is null ? "" : "/" + key);
        var payloadHash = body.IsEmpty ? SignatureV4.EmptyPayloadHash : SignatureV4.Hex(SHA256.HashData(body.Span));

        var headers = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["host"] = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}",
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = SignatureV4.AmzDate(now)
        };

        var signed = new SignatureV4.Request(method.Method, path, query, headers, payloadHash);

        var uri = new UriBuilder(endpoint) {
            Path = SignatureV4.Encode(path, keepSlash: true),
            Query = string.Join(
                '&',
                query.Select(x => SignatureV4.Encode(x.Key, keepSlash: false) + "=" + SignatureV4.Encode(x.Value, keepSlash: false))
            )
        }.Uri;

        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            SignatureV4.Authorization(signed, now, options.Region, options.AccessKeyId, options.SecretAccessKey)
        );

        if (!body.IsEmpty || method == HttpMethod.Put) {
            request.Content = new ReadOnlyMemoryContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
            );
        }

        return request;
    }

    Error Refused(string verb, string key, HttpStatusCode status) =>
        new(
            ErrorCode.InternalError,
            $"The object store at {endpoint.Host} refused to {verb} '{key}' with HTTP "
            + $"{((int)status).ToString(CultureInfo.InvariantCulture)}. A 403 is a credential or clock "
            + "problem on this host; anything else is the store's own log to read."
        );

    Error Unreachable(string verb, Exception exception) =>
        new(
            ErrorCode.InternalError,
            $"The object store at {endpoint.Host} could not be reached to {verb}: {exception.GetType().Name}. "
            + $"{ObjectStorageOptions.SectionName}:Endpoint names it."
        );

    static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or IOException;

    /// <summary>A response stream that takes its response and request down with it.</summary>
    sealed class OwningStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request) : Stream {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) {
                inner.Dispose();
                response.Dispose();
                request.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
