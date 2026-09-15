using CyberCloud.Core.Time;
using System.Net;
using System.Text;

namespace CyberCloud.ObjectStorage.Tests;

/// <summary>
///     The four verbs against a recording handler: what goes on the wire, and how each answer comes
///     back through the seam.
/// </summary>
public sealed class S3ObjectStoreTests {
    const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 30, 0, TimeSpan.Zero);

    static ObjectStorageOptions Options() =>
        new() {
            Endpoint = "https://s3.platform.internal:8333",
            Bucket = "cybercloud",
            Region = "eu-central",
            AccessKeyId = "AKIACYBERCLOUD",
            SecretAccessKey = Secret
        };

    static (S3ObjectStore Store, RecordingHandler Wire) Build(params (HttpStatusCode Status, string Body)[] answers) {
        var wire = new RecordingHandler(answers);
        return (new S3ObjectStore(new HttpClient(wire), Options(), new FixedClock(Now)), wire);
    }

    [Fact]
    public async Task APutIsASignedPathStyleRequestCarryingThePayloadHash() {
        var (store, wire) = Build((HttpStatusCode.OK, ""));

        var stored = await store.PutAsync(
            "tenant/feed/nuget/my package/1.0.0/my.package.1.0.0.nupkg",
            "hello"u8.ToArray(),
            "application/octet-stream",
            TestContext.Current.CancellationToken
        );

        stored.IsSuccess.ShouldBeTrue(stored.Error?.Message);

        var sent = wire.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Put);
        sent.Uri.ShouldBe("https://s3.platform.internal:8333/cybercloud/tenant/feed/nuget/my%20package/1.0.0/my.package.1.0.0.nupkg");
        sent.Headers["x-amz-date"].ShouldBe("20260915T103000Z");
        sent.Headers["x-amz-content-sha256"].ShouldBe("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
        sent.Headers["Authorization"].ShouldStartWith("AWS4-HMAC-SHA256 Credential=AKIACYBERCLOUD/20260915/eu-central/s3/aws4_request, ");
        sent.Headers["Authorization"].ShouldContain("SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=");
        sent.ContentType.ShouldBe("application/octet-stream");
        sent.Body.ShouldBe("hello");
    }

    [Fact]
    public async Task TheSignatureOnTheWireIsTheOneTheSignerComputesForTheSameRequest() {
        // The store and the signer have to agree on what was sent — host with its port, the encoded
        // path, the three headers — or a real server computes a different signature and refuses.
        var (store, wire) = Build((HttpStatusCode.OK, ""));

        await store.PutAsync("k", "x"u8.ToArray(), "text/plain", TestContext.Current.CancellationToken);

        var sent = wire.Requests.ShouldHaveSingleItem();
        var hash = sent.Headers["x-amz-content-sha256"];

        var expected = SignatureV4.Authorization(
            new(
                "PUT",
                "/cybercloud/k",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["host"] = "s3.platform.internal:8333",
                    ["x-amz-content-sha256"] = hash,
                    ["x-amz-date"] = "20260915T103000Z"
                },
                hash
            ),
            Now,
            "eu-central",
            "AKIACYBERCLOUD",
            Secret
        );

        sent.Headers["Authorization"].ShouldBe(expected);
    }

    [Fact]
    public async Task AGetStreamsTheBodyBackWithItsMediaType() {
        var (store, _) = Build((HttpStatusCode.OK, "the bytes"));

        var read = await store.GetAsync("tenant/feed/x", TestContext.Current.CancellationToken);

        using var found = read.GetValueOrThrow();
        found.ContentType.ShouldBe("application/x-test");
        found.Length.ShouldBe(9);

        using var reader = new StreamReader(found.Content);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldBe("the bytes");
    }

    [Fact]
    public async Task AGetOfAnAbsentKeyIsNotFoundAndADeleteOfOneSucceeds() {
        var (store, _) = Build((HttpStatusCode.NotFound, ""), (HttpStatusCode.NotFound, ""));

        var read = await store.GetAsync("tenant/feed/missing", TestContext.Current.CancellationToken);
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await store.DeleteAsync("tenant/feed/missing", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AListingFollowsItsContinuationTokenAndComesBackSorted() {
        const string page1 =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Name>cybercloud</Name><Prefix>t/f/</Prefix><IsTruncated>true</IsTruncated>
              <NextContinuationToken>tok-2</NextContinuationToken>
              <Contents><Key>t/f/b</Key><Size>1</Size></Contents>
              <Contents><Key>t/f/a</Key><Size>1</Size></Contents>
            </ListBucketResult>
            """;

        const string page2 =
            """
            <ListBucketResult>
              <IsTruncated>false</IsTruncated>
              <Contents><Key>t/f/c</Key></Contents>
            </ListBucketResult>
            """;

        var (store, wire) = Build((HttpStatusCode.OK, page1), (HttpStatusCode.OK, page2));

        var listed = await store.ListAsync("t/f/", TestContext.Current.CancellationToken);

        listed.GetValueOrThrow().ShouldBe(["t/f/a", "t/f/b", "t/f/c"]);

        wire.Requests.Count.ShouldBe(2);
        wire.Requests[0].Uri.ShouldBe("https://s3.platform.internal:8333/cybercloud?list-type=2&prefix=t%2Ff%2F&max-keys=1000");
        wire.Requests[1].Uri.ShouldContain("continuation-token=tok-2");
    }

    [Fact]
    public async Task AListingThatIsNotXmlIsARefusalRatherThanAnEmptyPrefix() {
        // ⚠ An empty answer here would let a feed's teardown converge over artefacts it never saw.
        var (store, _) = Build((HttpStatusCode.OK, "<html>login</html>"));

        var listed = await store.ListAsync("t/", TestContext.Current.CancellationToken);

        listed.IsFailure.ShouldBeTrue();
        listed.Error!.Message.ShouldContain("ListBucketResult");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NoRefusalCarriesTheCredential(HttpStatusCode status) {
        var (store, _) = Build((status, ""), (status, ""), (status, ""), (status, ""));

        var results = new List<Error?> {
            (await store.PutAsync("k", "x"u8.ToArray(), "text/plain", TestContext.Current.CancellationToken)).Error,
            (await store.GetAsync("k", TestContext.Current.CancellationToken)).Error,
            (await store.DeleteAsync("k", TestContext.Current.CancellationToken)).Error,
            (await store.ListAsync("k", TestContext.Current.CancellationToken)).Error
        };

        foreach (var error in results) {
            error.ShouldNotBeNull();
            error.Code.ShouldBe(ErrorCode.InternalError);
            error.Message.ShouldContain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            error.Message.ShouldContain("s3.platform.internal");
            error.Message.ShouldNotContain(Secret);
            error.Message.ShouldNotContain("AKIACYBERCLOUD");
        }
    }

    [Fact]
    public async Task AnUnreachableEndpointIsARefusalNamingTheSection() {
        var store = new S3ObjectStore(new HttpClient(new ThrowingHandler()), Options(), new FixedClock(Now));

        var refused = await store.GetAsync("k", TestContext.Current.CancellationToken);

        refused.Error!.Code.ShouldBe(ErrorCode.InternalError);
        refused.Error.Message.ShouldContain(ObjectStorageOptions.SectionName);
        refused.Error.Message.ShouldNotContain(Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/leading")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a\nb")]
    public async Task AKeyNoStoreAcceptsIsRefusedBeforeAnythingIsSent(string key) {
        var (store, wire) = Build();

        (await store.PutAsync(key, "x"u8.ToArray(), "text/plain", TestContext.Current.CancellationToken))
            .Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

        wire.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void TheEndpointIsValidatedUpFront() {
        Should.Throw<ArgumentException>(() => S3ObjectStore.ValidatedEndpoint(new() { Endpoint = "not a url" }))
            .Message.ShouldContain("Endpoint");

        Should.Throw<ArgumentException>(() => S3ObjectStore.ValidatedEndpoint(Options().With(x => x.Endpoint = "http://plain")))
            .Message.ShouldContain("AllowInsecureTransport");

        Should.Throw<ArgumentException>(() => S3ObjectStore.ValidatedEndpoint(Options().With(x => x.Bucket = "")))
            .Message.ShouldContain("Bucket");

        S3ObjectStore.ValidatedEndpoint(Options().With(x => { x.Endpoint = "http://dev"; x.AllowInsecureTransport = true; }))
            .Host.ShouldBe("dev");
    }

    sealed record Sent(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string? ContentType, string Body);

    sealed class RecordingHandler((HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler {
        int served;

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            var headers = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.Ordinal);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(
                new(
                    request.Method,
                    // AbsoluteUri, not ToString(): the latter unescapes, and the escaping IS the assertion.
                    request.RequestUri!.AbsoluteUri,
                    headers,
                    request.Content?.Headers.ContentType?.MediaType,
                    body
                )
            );

            var (status, answer) = answers[Math.Min(served++, answers.Length - 1)];

            return new(status) {
                Content = new StringContent(answer, Encoding.UTF8, "application/x-test")
            };
        }
    }

    sealed class ThrowingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    sealed class FixedClock(DateTimeOffset now) : IClock {
        public DateTimeOffset UtcNow => now;
    }
}

/// <summary>Lets a test derive one options object from another.</summary>
static class ObjectStorageOptionsExtensions {
    extension(ObjectStorageOptions options) {
        public ObjectStorageOptions With(Action<ObjectStorageOptions> change) {
            var copy = new ObjectStorageOptions {
                Endpoint = options.Endpoint,
                Bucket = options.Bucket,
                Region = options.Region,
                AccessKeyId = options.AccessKeyId,
                SecretAccessKey = options.SecretAccessKey,
                AllowInsecureTransport = options.AllowInsecureTransport,
                RequestTimeout = options.RequestTimeout
            };

            change(copy);
            return copy;
        }
    }
}
