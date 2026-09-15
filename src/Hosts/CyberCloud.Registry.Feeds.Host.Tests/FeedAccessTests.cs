using System.Net;
using System.Text.Json;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     The four steps every endpoint shares, driven through the NuGet service index because it is
///     the smallest read there is.
/// </summary>
[Collection(FeedsHostSuite.Name)]
public sealed class FeedAccessTests(FeedsHostFixture host) {
    static string Index => FeedsHostFixture.Feed(FeedKind.NuGet, FeedsHostFixture.NuGetFeed) + "/v3/index.json";

    [Fact]
    public async Task AnAnonymousRequestIs401WithBothChallenges() {
        // ⚠ Both, because the clients differ: NuGet and Maven answer a Basic challenge by retrying
        // with their configured credential; npm sends Bearer unprompted.
        using var client = host.Client();
        using var response = await client.GetAsync(Index, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("Basic");
        challenge.ShouldContain("Bearer");

        var body = JsonDocument.Parse(await response.BodyAsync());
        body.RootElement.GetProperty("code").GetString().ShouldBe("AuthorizationFailed");
        body.RootElement.GetProperty("message").GetString()!.ShouldContain("not authenticated");
    }

    [Fact]
    public async Task ATokenThisPlatformDidNotIssueIs401() {
        using var client = host.Client("cc_" + Guid.NewGuid().ToString("N"));
        using var response = await client.GetAsync(Index, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheTokenIsReadFromAllThreePlacesAClientPutsIt() {
        var token = TestContext.Current.CancellationToken;

        // Bearer — npm's shape.
        using (var bearer = host.Client(host.Alice))
        using (var response = await bearer.GetAsync(Index, token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Basic with the token as the password — Maven's and `dotnet restore`'s shape. The username
        // is whatever the tenant wrote, and is ignored.
        using (var basic = host.BasicClient(host.Alice, username: "anything-at-all"))
        using (var response = await basic.GetAsync(Index, token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // X-NuGet-ApiKey — `dotnet nuget push`'s shape.
        using (var client = host.Client())
        using (var request = new HttpRequestMessage(HttpMethod.Get, Index).WithHeader("X-NuGet-ApiKey", host.Alice))
        using (var response = await client.SendAsync(request, token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task ABasicCredentialWithAnUnknownPasswordIs401AndAMalformedOneToo() {
        var token = TestContext.Current.CancellationToken;

        using (var basic = host.BasicClient("cc_" + Guid.NewGuid().ToString("N")))
        using (var response = await basic.GetAsync(Index, token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var client = host.Client())
        using (var request = new HttpRequestMessage(HttpMethod.Get, Index).WithHeader("Authorization", "Basic not-base64!"))
        using (var response = await client.SendAsync(request, token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task ACallerWithNoRoleOnTheGroupGets404NotFoundNever403() {
        // ⚠ docs/plan/07 § The enforcement seam: a 403 would confirm the feed exists. Carol is a
        // valid caller in the right tenant and holds nothing on the group.
        using var client = host.Client(host.Carol);
        using var response = await client.GetAsync(Index, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnotherTenantsOwnerGets404ForAFeedThatExistsInThisTenant() {
        // ⚠ Mallory owns a group with the same name in her own tenant and has a NuGet feed of the
        // same name in it. The URL she sends is exactly Alice's; the tenant comes from her token,
        // so she reaches her own feed and never Alice's — asserted here as a 404 on Alice's
        // subscription, which Mallory's tenant does not have.
        using var client = host.Client(host.Mallory);
        using var response = await client.GetAsync(Index, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var own = await client.GetAsync(
            FeedsHostFixture.Feed(FeedKind.NuGet, FeedsHostFixture.NuGetFeed, FeedsHostFixture.OtherSubscription) + "/v3/index.json",
            TestContext.Current.CancellationToken
        );

        own.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AReaderCanReadAndIsRefused403OnAPush() {
        var token = TestContext.Current.CancellationToken;
        using var client = host.Client(host.Bob);

        using (var read = await client.GetAsync(Index, token)) {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var push = await client.PutAsync(
            FeedsHostFixture.Feed(FeedKind.NuGet, FeedsHostFixture.NuGetFeed) + "/v2/package",
            new ByteArrayContent(TestPackages.NuGet("reader.push", "1.0.0")),
            token
        );

        // 403 and not 404: Bob can read the feed, so its existence is already known to him, and
        // what he lacks is `write`.
        push.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await push.BodyAsync()).ShouldContain("write");
    }

    [Fact]
    public async Task AFeedOfAnotherKindIs404OnThisProtocolsRoutes() {
        // The npm feed exists and Alice can read it; it serves no NuGet packages and never will.
        using var client = host.Client(host.Alice);
        using var response = await client.GetAsync(
            FeedsHostFixture.Feed(FeedKind.NuGet, FeedsHostFixture.NpmFeed) + "/v3/index.json",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.BodyAsync()).ShouldContain("npm feed");
    }

    [Fact]
    public async Task AFeedThatDoesNotExistIs404() {
        using var client = host.Client(host.Alice);
        using var response = await client.GetAsync(
            FeedsHostFixture.Feed(FeedKind.NuGet, "no-such-feed") + "/v3/index.json",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheHealthEndpointsNeedNoCredential() {
        using var client = host.Client();
        using var response = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }
}
