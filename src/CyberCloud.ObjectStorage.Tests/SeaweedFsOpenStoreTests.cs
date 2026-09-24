using CyberCloud.Core.Time;
using System.Collections.Concurrent;
using System.Net;

namespace CyberCloud.ObjectStorage.Tests;

/// <summary>
///     <see cref="SeaweedFsObjectStoreGrants" /> against a store that answers everybody: no key is
///     issued, and nothing is created at the store.
/// </summary>
/// <remarks>
///     ⚠ A stub rather than a SeaweedFS, because a real store is open only until its first identity
///     reaches the S3 gateway, and a test that raced that window would be a test of the race.
/// </remarks>
public sealed class SeaweedFsOpenStoreTests {
    [Fact]
    public async Task AStoreThatAnswersAKeyNobodyIssuedGetsNoUserAndNoKey() {
        var wire = new AnsweringEverybody();
        var grants = new SeaweedFsObjectStoreGrants(
            new HttpClient(wire),
            new() {
                Endpoint = "http://objectstore.test:8333",
                IamEndpoint = "http://objectstore.test:8111",
                DataPlaneEndpoint = "http://objectstore.test:8333",
                Bucket = "platform",
                AccessKeyId = "ADMIN",
                SecretAccessKey = "admin-secret",
                AllowInsecureTransport = true
            },
            new SystemClock()
        );

        var issued = await grants.IssueKeyAsync("pg-one", "pg-one", TestContext.Current.CancellationToken);

        issued.IsFailure.ShouldBeTrue("a key was issued by a store that authenticates nobody, so its bucket scope means nothing");
        issued.Error!.Message.ShouldContain("never issued");
        wire.Requests.ShouldHaveSingleItem("the store was asked to create a user or a key before it was known to authenticate");
        wire.Requests.Single().ShouldStartWith("GET http://objectstore.test:8333/");
    }

    [Fact]
    public async Task AStoreThatRefusesAStrangerIsSaidToAuthenticate() {
        var closed = await SeaweedFsObjectStoreGrants.RefusesStrangersAsync(
            new HttpClient(new AnsweringEverybody(HttpStatusCode.Forbidden)),
            new Uri("http://objectstore.test:8333"),
            "us-east-1",
            new SystemClock(),
            TestContext.Current.CancellationToken
        );

        closed.GetValueOrThrow().ShouldBeTrue();
    }

    sealed class AnsweringEverybody(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler {
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Enqueue($"{request.Method} {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<ListAllMyBucketsResult/>") });
        }
    }
}
