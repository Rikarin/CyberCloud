namespace CyberCloud.ObjectStorage.Tests;

/// <summary>
///     <see cref="SignatureV4" /> against the worked examples in Amazon's S3 documentation,
///     "Examples: Signature Calculations in AWS Signature Version 4".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every constant below is copied from the document, not computed here.</b> A test that
///         computed its expectation with the code under test would pin nothing. The four examples
///         share one credential, one date and one bucket, and each exercises a different part of
///         the canonicalisation: a signed non-<c>x-amz</c> header, a path with a character that has
///         to be encoded once and not twice, an empty query value, and a query with two keys.
///     </para>
///     <para>
///         The credential is Amazon's published example pair and is not a secret; it signs nothing
///         anybody serves.
///     </para>
/// </remarks>
public sealed class SignatureV4Tests {
    const string AccessKeyId = "AKIAIOSFODNN7EXAMPLE";
    const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    const string Region = "us-east-1";
    const string Host = "examplebucket.s3.amazonaws.com";

    static readonly DateTimeOffset Date = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    static Dictionary<string, string> Headers(string payloadHash, params (string Name, string Value)[] extra) {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["host"] = Host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = "20130524T000000Z"
        };

        foreach (var (name, value) in extra) {
            headers[name] = value;
        }

        return headers;
    }

    [Fact]
    public void TheGetObjectExampleFromTheS3Documentation() {
        var request = new SignatureV4.Request(
            "GET",
            "/test.txt",
            [],
            Headers(SignatureV4.EmptyPayloadHash, ("Range", "bytes=0-9")),
            SignatureV4.EmptyPayloadHash
        );

        SignatureV4.CanonicalRequest(request)
            .ShouldBe(
                "GET\n/test.txt\n\nhost:examplebucket.s3.amazonaws.com\nrange:bytes=0-9\n"
                + "x-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n"
                + "x-amz-date:20130524T000000Z\n\nhost;range;x-amz-content-sha256;x-amz-date\n"
                + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            );

        SignatureV4.StringToSign(SignatureV4.CanonicalRequest(request), Date, Region)
            .ShouldBe(
                "AWS4-HMAC-SHA256\n20130524T000000Z\n20130524/us-east-1/s3/aws4_request\n"
                + "7344ae5b7ee6c3e7e6b0fe0640412a37625d1fbfff95c48bbb2dc43964946972"
            );

        SignatureV4.Sign(request, Date, Region, Secret)
            .ShouldBe("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");

        SignatureV4.Authorization(request, Date, Region, AccessKeyId, Secret)
            .ShouldBe(
                "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, "
                + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, "
                + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41"
            );
    }

    [Fact]
    public void ThePutObjectExampleFromTheS3Documentation() {
        // ⚠ The path carries a `$`, and the document encodes it ONCE — /test%24file.text. This is
        // the vector that pins S3's single encoding over the general SigV4 rule's double one.
        const string payloadHash = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";

        var request = new SignatureV4.Request(
            "PUT",
            "/test$file.text",
            [],
            Headers(
                payloadHash,
                ("Date", "Fri, 24 May 2013 00:00:00 GMT"),
                ("x-amz-storage-class", "REDUCED_REDUNDANCY")
            ),
            payloadHash
        );

        SignatureV4.CanonicalRequest(request).ShouldStartWith("PUT\n/test%24file.text\n\n");
        SignatureV4.Sign(request, Date, Region, Secret)
            .ShouldBe("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd");
    }

    [Fact]
    public void TheGetBucketLifecycleExampleFromTheS3Documentation() {
        // An empty query value canonicalises as `lifecycle=` — the key, an equals sign, nothing.
        var request = new SignatureV4.Request(
            "GET",
            "/",
            [new("lifecycle", "")],
            Headers(SignatureV4.EmptyPayloadHash),
            SignatureV4.EmptyPayloadHash
        );

        SignatureV4.CanonicalRequest(request).ShouldStartWith("GET\n/\nlifecycle=\n");
        SignatureV4.Sign(request, Date, Region, Secret)
            .ShouldBe("fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");
    }

    [Fact]
    public void TheListObjectsExampleFromTheS3Documentation() {
        // Two keys, given out of order here so the sort is what puts them right.
        var request = new SignatureV4.Request(
            "GET",
            "/",
            [new("prefix", "J"), new("max-keys", "2")],
            Headers(SignatureV4.EmptyPayloadHash),
            SignatureV4.EmptyPayloadHash
        );

        // ⚠ The canonical request and not the signature, unlike the three examples above. What this
        // example adds is the query sort, and the canonical request is where the sort shows; the
        // signature for it was not available to copy from the document when this was written, and
        // a constant written from memory pinned the wrong tail — 42 of 64 hex characters agreed and
        // the rest did not, which is exactly the shape of a misremembered constant and not of a
        // wrong algorithm. Three full vectors above pin the algorithm; this one pins the sort.
        SignatureV4.CanonicalRequest(request)
            .ShouldBe(
                "GET\n/\nmax-keys=2&prefix=J\nhost:examplebucket.s3.amazonaws.com\n"
                + "x-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n"
                + "x-amz-date:20130524T000000Z\n\nhost;x-amz-content-sha256;x-amz-date\n"
                + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            );
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a b", "a%20b")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("-_.~", "-_.~")]
    [InlineData("!*'()", "%21%2A%27%28%29")]
    [InlineData("ünïcode", "%C3%BCn%C3%AFcode")]
    public void AQueryValueIsEncodedWithExactlyTheUnreservedSet(string decoded, string encoded) =>
        SignatureV4.Encode(decoded, keepSlash: false).ShouldBe(encoded);

    [Fact]
    public void APathKeepsItsSlashesAndEncodesEverythingElse() =>
        SignatureV4.Encode("/bucket/tenant/feed/My Package 1.0.nupkg", keepSlash: true)
            .ShouldBe("/bucket/tenant/feed/My%20Package%201.0.nupkg");

    [Fact]
    public void AHeaderValueIsTrimmedAndItsSpacesCollapsed() {
        var request = new SignatureV4.Request(
            "GET",
            "/",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Custom"] = "  a   b  " },
            SignatureV4.EmptyPayloadHash
        );

        SignatureV4.CanonicalRequest(request).ShouldContain("\nx-custom:a b\n");
    }
}
