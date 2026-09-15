using CyberCloud.Core.Time;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using System.Net;
using System.Text;

namespace CyberCloud.ObjectStorage.Tests;

/// <summary>
///     The four verbs against a real SeaweedFS S3 endpoint in a container — the one that validates
///     the signature.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The identities file is the whole point of this suite, and a container without one
///             would prove nothing about signing.
///         </b> SeaweedFS with no <c>-s3.config</c> sets <c>iam.isAuthEnabled = false</c> and
///         answers every request as an administrator — the same fact docs/plan/12 § The pattern,
///         once records about <c>CyberCloud.Storage/accounts</c>. With one, every request is checked
///         against the credential named in <c>Authorization</c>, so a signer that disagreed with the
///         server about one byte of the canonical request gets <c>403</c> here and nowhere in the
///         Docker-free suites. <see cref="AWrongSecretIsRefusedByTheServer" /> is the control: it
///         proves the server is checking at all.
///     </para>
///     <para>
///         <c>chrislusf/seaweedfs:3.80</c>, pinned like <c>CyberCloud.Vault.Tests</c> pins OpenBao.
///         One process, <c>weed server -s3</c>, which is master, volume, filer and the S3 gateway in
///         one — the shape docs/plan/15's chart renders as separate objects, collapsed for a test.
///     </para>
/// </remarks>
public sealed class SeaweedFsRoundTripTests : IAsyncLifetime {
    public const string Image = "chrislusf/seaweedfs:3.80";
    const string AccessKeyId = "AKIACYBERCLOUDTEST";
    const string Secret = "cyber-cloud-test-secret-access-key";
    const string Bucket = "cybercloud-test";

    IContainer container = null!;
    ObjectStorageOptions options = null!;

    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        var identities =
            $$"""
              {"identities":[{"name":"cybercloud","credentials":[{"accessKey":"{{AccessKeyId}}","secretKey":"{{Secret}}"}],"actions":["Admin","Read","Write","List","Tagging"]}]}
              """;

        container = new ContainerBuilder(Image)
            .WithPortBinding(8333, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(identities), "/etc/seaweedfs/s3.json")
            .WithCommand("server", "-s3", "-s3.config=/etc/seaweedfs/s3.json", "-dir=/data", "-ip.bind=0.0.0.0")
            // ⚠ The S3 port answering, not a log line. The gateway prints its banner before the
            // filer it depends on is ready, and the first PUT against a half-started server fails
            // with a 500 that reads like a signing bug. A 403 to an unsigned GET / is the earliest
            // answer that means "the S3 gateway is up and checking credentials".
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(x => x
                        .ForPort(8333)
                        .ForPath("/")
                        .ForStatusCodeMatching(code => code is HttpStatusCode.Forbidden or HttpStatusCode.OK)
                    )
            )
            .Build();

        await container.StartAsync(token);

        options = new() {
            Endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(8333)}",
            Bucket = Bucket,
            AccessKeyId = AccessKeyId,
            SecretAccessKey = Secret,
            // ⚠ A container has no certificate anybody trusts; the flag exists for exactly this.
            AllowInsecureTransport = true,
            RequestTimeout = TimeSpan.FromSeconds(30)
        };

        await CreateBucketAsync(token);
    }

    public async ValueTask DisposeAsync() {
        if (container is not null) {
            await container.DisposeAsync();
        }
    }

    S3ObjectStore Store(string? secret = null) =>
        new(
            new HttpClient { Timeout = options.RequestTimeout },
            secret is null ? options : options.With(x => x.SecretAccessKey = secret),
            new SystemClock()
        );

    [Fact]
    public async Task PutGetListAndDeleteRoundTripAgainstARealServer() {
        var token = TestContext.Current.CancellationToken;
        var store = Store();
        var prefix = "aaaaaaaa00004000800000000000000a/feed-" + Guid.NewGuid().ToString("N") + "/";

        // Put — two keys under the prefix, one with a space and a plus in it, one outside it.
        (await store.PutAsync(prefix + "nuget/my package/1.0.0+build/a.nupkg", "first"u8.ToArray(), "application/octet-stream", token))
            .IsSuccess.ShouldBeTrue();
        (await store.PutAsync(prefix + "npm/@scope/pkg/-/pkg-1.0.0.tgz", "second"u8.ToArray(), "application/gzip", token))
            .IsSuccess.ShouldBeTrue();
        (await store.PutAsync("elsewhere/" + Guid.NewGuid().ToString("N"), "other"u8.ToArray(), "text/plain", token))
            .IsSuccess.ShouldBeTrue();

        // Get — the bytes and the media type come back.
        var read = await store.GetAsync(prefix + "nuget/my package/1.0.0+build/a.nupkg", token);
        using (var found = read.GetValueOrThrow()) {
            using var reader = new StreamReader(found.Content);
            (await reader.ReadToEndAsync(token)).ShouldBe("first");
            found.ContentType.ShouldBe("application/octet-stream");
        }

        // List — exactly the prefix's two, sorted, and not the third.
        (await store.ListAsync(prefix, token))
            .GetValueOrThrow()
            .ShouldBe([prefix + "npm/@scope/pkg/-/pkg-1.0.0.tgz", prefix + "nuget/my package/1.0.0+build/a.nupkg"]);

        // Delete — and the listing proves it, which is the read-back a teardown relies on.
        foreach (var key in (await store.ListAsync(prefix, token)).GetValueOrThrow()) {
            (await store.DeleteAsync(key, token)).IsSuccess.ShouldBeTrue();
        }

        (await store.ListAsync(prefix, token)).GetValueOrThrow().ShouldBeEmpty();
        (await store.GetAsync(prefix + "nuget/my package/1.0.0+build/a.nupkg", token)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AWrongSecretIsRefusedByTheServer() {
        // ⚠ The control. Without this, a server that ignored signatures would pass the test above.
        var refused = await Store("the-wrong-secret").PutAsync("probe/" + Guid.NewGuid().ToString("N"), "x"u8.ToArray(), "text/plain", TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("403");
        refused.Error.Message.ShouldNotContain("the-wrong-secret");
    }

    /// <summary>Creates the bucket with a signed <c>PUT /{bucket}</c> — the one call the store does not make.</summary>
    async Task CreateBucketAsync(CancellationToken token) {
        var endpoint = new Uri(options.Endpoint);
        var now = DateTimeOffset.UtcNow;
        var host = $"{endpoint.Host}:{endpoint.Port}";

        var headers = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["host"] = host,
            ["x-amz-content-sha256"] = SignatureV4.EmptyPayloadHash,
            ["x-amz-date"] = SignatureV4.AmzDate(now)
        };

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(endpoint, "/" + Bucket));
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", SignatureV4.EmptyPayloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            SignatureV4.Authorization(
                new("PUT", "/" + Bucket, [], headers, SignatureV4.EmptyPayloadHash),
                now,
                options.Region,
                AccessKeyId,
                Secret
            )
        );

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.SendAsync(request, token);

        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException(
                $"Creating the bucket answered {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync(token)
                + " — a fixture fault rather than a test failure."
            );
        }
    }
}
