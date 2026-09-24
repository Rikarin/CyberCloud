using CyberCloud.Core.Time;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace CyberCloud.ObjectStorage;

/// <summary>
///     <see cref="IObjectStoreGrants" /> over SeaweedFS: buckets through the S3 API, and users,
///     bucket policies and access keys through its IAM API.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The IAM API and not the identities file, and the choice was measured rather than
///             reasoned.
///         </b> The spike behind #30's backup work tried the three routes a SeaweedFS
///         offers. A <c>-s3.config</c> file is static: 3.80 and 4.41 both ignored an identity written
///         to the filer while one was set. Filer-held identities (<c>/etc/iam/identity.json</c> on 3.80,
///         per-user files under <c>/etc/iam/identities/</c> on 4.41) did not take effect from an HTTP
///         write, and 4.41 without <c>jwt.filer_signing.key</c> answered every request — a bogus key
///         included — as an administrator. <c>weed iam</c> on 3.80 did what this type needs: a user per
///         principal, a policy naming one bucket, a key whose own bucket it can write and whose
///         neighbour's it cannot list, and a withdrawal the S3 API honoured within seconds.
///         <c>SeaweedFsGrantsTests</c> pins all of that against the real image.
///     </para>
///     <para>
///         ⚠ <b>The store generates the key; the vault holds it.</b> <c>CreateAccessKey</c> takes no
///         caller-chosen value, so the mint-once of <see cref="ISecretWriter" /> runs after the issue
///         rather than before — <see cref="ObjectStoreCredentials.EnsureAsync" /> has the race and its
///         ending.
///     </para>
///     <para>
///         ⚠ <b>3.80's <c>CreateUser</c> appends</b> — <c>iamapi_management_handlers.go</c> adds an
///         identity without checking the name — so a second call for one principal would make a second
///         identity of the same name. <see cref="IssueKeyAsync" /> asks <c>GetUser</c> first.
///     </para>
/// </remarks>
public sealed class SeaweedFsObjectStoreGrants : IObjectStoreGrants {
    /// <summary>The IAM API version every request names.</summary>
    public const string IamVersion = "2010-05-08";

    /// <summary>The signing service for the IAM API.</summary>
    public const string IamService = "iam";

    /// <summary>The policy name a principal's bucket grant is filed under.</summary>
    public const string PolicyName = "cybercloud-bucket";

    readonly HttpClient http;
    readonly ObjectStorageOptions options;
    readonly IClock clock;
    readonly Uri s3;
    readonly Uri iam;

    /// <summary>Creates the grants over one store.</summary>
    /// <param name="http">The client. Long-lived.</param>
    /// <param name="options">The section. ⚠ Its credential must be an administrator of the store.</param>
    /// <param name="clock">For <c>x-amz-date</c>.</param>
    public SeaweedFsObjectStoreGrants(HttpClient http, ObjectStorageOptions options, IClock clock) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        this.http = http;
        this.options = options;
        this.clock = clock;
        s3 = S3ObjectStore.ValidatedEndpoint(options);
        iam = Validated(options.IamEndpoint, nameof(ObjectStorageOptions.IamEndpoint), options);
        DataPlaneEndpoint = Validated(options.DataPlaneEndpoint, nameof(ObjectStorageOptions.DataPlaneEndpoint), options)
            .GetLeftPart(UriPartial.Authority);
    }

    /// <inheritdoc />
    public string DataPlaneEndpoint { get; }

    /// <summary>Checks one of the two endpoints this type adds, the way the store's own is checked.</summary>
    /// <param name="value">The configured value.</param>
    /// <param name="name">Its key, for the message.</param>
    /// <param name="options">The section, for the insecure-transport flag.</param>
    /// <returns>The parsed endpoint.</returns>
    /// <exception cref="ArgumentException">It is not an absolute http(s) URL, or is plain http without opting in.</exception>
    public static Uri Validated(string value, string name, ObjectStorageOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) {
            throw new ArgumentException(
                $"{ObjectStorageOptions.SectionName}:{name} must be an absolute http(s) URL, and '{value}' is not.",
                nameof(options)
            );
        }

        if (parsed.Scheme == "http" && !options.AllowInsecureTransport) {
            throw new ArgumentException(
                $"{ObjectStorageOptions.SectionName}:{name} is plain http and AllowInsecureTransport is off.",
                nameof(options)
            );
        }

        return parsed;
    }

    /// <inheritdoc />
    public async Task<Result> EnsureBucketAsync(string bucket, CancellationToken cancellationToken = default) {
        using var request = SignedS3(HttpMethod.Put, "/" + bucket);

        try {
            using var response = await http.SendAsync(request, cancellationToken);

            // ⚠ 409 is "already there", and for a bucket named by a resource's own GUID it is this
            // resource's bucket from an earlier pass. S3 says BucketAlreadyOwnedByYou; SeaweedFS says
            // BucketAlreadyExists for its own buckets too, and both are the second pass converging.
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict
                ? Result.Success
                : Result.Failure(Refused("create the bucket", bucket, response.StatusCode, await Body(response)));
        } catch (Exception exception) when (IsTransport(exception)) {
            return Result.Failure(Unreachable(s3, "create a bucket", exception));
        }
    }

    /// <inheritdoc />
    public async Task<Result<ObjectStoreKey>> IssueKeyAsync(
        string principal,
        string bucket,
        CancellationToken cancellationToken = default
    ) {
        try {
            var user = await IamAsync([("Action", "GetUser"), ("UserName", principal)], cancellationToken);

            if (!user.Ok) {
                var created = await IamAsync([("Action", "CreateUser"), ("UserName", principal)], cancellationToken);

                if (!created.Ok) {
                    return Result<ObjectStoreKey>.Failure(Refused("create the user", principal, created.Status, created.Body));
                }
            }

            var policy = await IamAsync(
                [
                    ("Action", "PutUserPolicy"),
                    ("UserName", principal),
                    ("PolicyName", PolicyName),
                    ("PolicyDocument", PolicyFor(bucket))
                ],
                cancellationToken
            );

            if (!policy.Ok) {
                return Result<ObjectStoreKey>.Failure(Refused("scope the user to its bucket", principal, policy.Status, policy.Body));
            }

            var issued = await IamAsync([("Action", "CreateAccessKey"), ("UserName", principal)], cancellationToken);

            if (!issued.Ok) {
                return Result<ObjectStoreKey>.Failure(Refused("issue a key", principal, issued.Status, issued.Body));
            }

            var document = XDocument.Parse(issued.Body);
            var id = Element(document, "AccessKeyId");
            var secret = Element(document, "SecretAccessKey");

            return id.Length == 0 || secret.Length == 0
                ? Result<ObjectStoreKey>.Failure(
                    ErrorCode.InternalError,
                    $"The object store's IAM API answered CreateAccessKey for '{principal}' without a key pair."
                )
                : Result<ObjectStoreKey>.Success(new(id, secret));
        } catch (Exception exception) when (IsTransport(exception)) {
            return Result<ObjectStoreKey>.Failure(Unreachable(iam, "issue a key", exception));
        } catch (System.Xml.XmlException) {
            return Result<ObjectStoreKey>.Failure(
                ErrorCode.InternalError,
                $"The object store's IAM API answered CreateAccessKey for '{principal}' with something that is not XML."
            );
        }
    }

    /// <inheritdoc />
    public async Task<Result> RevokeKeyAsync(
        string principal,
        string accessKeyId,
        CancellationToken cancellationToken = default
    ) {
        try {
            var deleted = await IamAsync(
                [("Action", "DeleteAccessKey"), ("UserName", principal), ("AccessKeyId", accessKeyId)],
                cancellationToken
            );

            return deleted.Ok || deleted.Status == HttpStatusCode.NotFound
                ? Result.Success
                : Result.Failure(Refused("withdraw a key", principal, deleted.Status, deleted.Body));
        } catch (Exception exception) when (IsTransport(exception)) {
            return Result.Failure(Unreachable(iam, "withdraw a key", exception));
        }
    }

    /// <summary>
    ///     On a store that has no identities yet, creates the platform's own administrator and returns
    ///     its key — the install step that gives <see cref="ObjectStorageOptions.AccessKeyId" /> a value.
    /// </summary>
    /// <param name="http">A client.</param>
    /// <param name="iamEndpoint">The store's IAM origin.</param>
    /// <param name="principal">The administrator's name.</param>
    /// <param name="clock">For <c>x-amz-date</c>.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The administrator's key, or the store's refusal.</returns>
    /// <remarks>
    ///     ⚠ <b>It works only while the store is open</b>: SeaweedFS authenticates nothing until its
    ///     first identity exists, so the request is signed with a placeholder and the store accepts it.
    ///     Once this has run the store has an identity, and the key returned is the one every later
    ///     call signs with. An installer runs it once; the cluster-backed suites run it against the
    ///     store they start.
    /// </remarks>
    public static Task<Result<ObjectStoreKey>> BootstrapAdministratorAsync(
        HttpClient http,
        Uri iamEndpoint,
        string principal,
        IClock clock,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(iamEndpoint);

        var open = new ObjectStorageOptions {
            Endpoint = iamEndpoint.GetLeftPart(UriPartial.Authority),
            IamEndpoint = iamEndpoint.GetLeftPart(UriPartial.Authority),
            DataPlaneEndpoint = iamEndpoint.GetLeftPart(UriPartial.Authority),
            Bucket = "bootstrap",
            AccessKeyId = "BOOTSTRAP",
            SecretAccessKey = "bootstrap",
            AllowInsecureTransport = iamEndpoint.Scheme == "http"
        };

        // ⚠ "*" makes PolicyFor name every bucket and every object: the administrator's scope.
        return new SeaweedFsObjectStoreGrants(http, open, clock).IssueKeyAsync(principal, "*", cancellationToken);
    }

    /// <summary>The policy document a principal gets: everything on one bucket and its objects, nothing else.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <remarks>
    ///     ⚠ <c>s3:*</c> on the bucket, because barman-cloud lists, reads, writes and deletes (its own
    ///     retention) and SeaweedFS maps the IAM verbs onto its four coarse actions —
    ///     <c>Read</c>, <c>Write</c>, <c>List</c>, <c>Tagging</c> — scoped by bucket name. The scope is
    ///     what matters and it is one bucket.
    /// </remarks>
    public static string PolicyFor(string bucket) =>
        new JsonObject {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(
                new JsonObject {
                    ["Effect"] = "Allow",
                    ["Action"] = new JsonArray("s3:*"),
                    ["Resource"] = new JsonArray($"arn:aws:s3:::{bucket}", $"arn:aws:s3:::{bucket}/*")
                }
            )
        }.ToJsonString();

    async Task<(bool Ok, HttpStatusCode Status, string Body)> IamAsync(
        IReadOnlyList<(string Name, string Value)> parameters,
        CancellationToken cancellationToken
    ) {
        var form = string.Join(
            '&',
            parameters.Append((Name: "Version", Value: IamVersion))
                .Select(static x => SignatureV4.Encode(x.Name, false) + "=" + SignatureV4.Encode(x.Value, false))
        );

        var body = Encoding.UTF8.GetBytes(form);
        var now = clock.UtcNow;
        var payloadHash = SignatureV4.Hex(SHA256.HashData(body));

        var headers = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["content-type"] = "application/x-www-form-urlencoded; charset=utf-8",
            ["host"] = Host(iam),
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = SignatureV4.AmzDate(now)
        };

        var signed = new SignatureV4.Request("POST", "/", [], headers, payloadHash);

        using var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder(iam) { Path = "/" }.Uri);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            SignatureV4.Authorization(signed, now, options.Region, options.AccessKeyId, options.SecretAccessKey, IamService)
        );
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", headers["content-type"]);

        using var response = await http.SendAsync(request, cancellationToken);
        return (response.IsSuccessStatusCode, response.StatusCode, await Body(response));
    }

    HttpRequestMessage SignedS3(HttpMethod method, string path) {
        var now = clock.UtcNow;

        var headers = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["host"] = Host(s3),
            ["x-amz-content-sha256"] = SignatureV4.EmptyPayloadHash,
            ["x-amz-date"] = SignatureV4.AmzDate(now)
        };

        var signed = new SignatureV4.Request(method.Method, path, [], headers, SignatureV4.EmptyPayloadHash);

        var request = new HttpRequestMessage(method, new UriBuilder(s3) { Path = SignatureV4.Encode(path, true) }.Uri);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", SignatureV4.EmptyPayloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            SignatureV4.Authorization(signed, now, options.Region, options.AccessKeyId, options.SecretAccessKey)
        );
        request.Content = new ByteArrayContent([]);

        return request;
    }

    static string Host(Uri endpoint) => endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";

    static string Element(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? string.Empty;

    static async Task<string> Body(HttpResponseMessage response) {
        try {
            return await response.Content.ReadAsStringAsync();
        } catch (Exception exception) when (IsTransport(exception)) {
            return string.Empty;
        }
    }

    /// <summary>A refusal, naming what was asked and the store's own error code — never a key.</summary>
    static Error Refused(string what, string subject, HttpStatusCode status, string body) {
        var code = string.Empty;

        try {
            code = body.Length == 0
                ? string.Empty
                : XDocument.Parse(body).Descendants().FirstOrDefault(x => x.Name.LocalName == "Code")?.Value ?? string.Empty;
        } catch (System.Xml.XmlException) {
            // Not an S3 or IAM error document; the status alone is what there is to say.
        }

        return new(
            ErrorCode.InternalError,
            $"The platform's object store refused to {what} for '{subject}' with HTTP "
            + ((int)status).ToString(CultureInfo.InvariantCulture)
            + (code.Length > 0 ? $" ({code})" : string.Empty)
            + ". A 403 is the platform's own credential or clock; anything else is the store's log to read."
        );
    }

    static Error Unreachable(Uri endpoint, string what, Exception exception) =>
        new(
            ErrorCode.InternalError,
            $"The platform's object store at {endpoint.Host} could not be reached to {what}: "
            + $"{exception.GetType().Name}."
        );

    static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or IOException;
}
