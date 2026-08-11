using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.ManagedIdentity;
using CyberCloud.Identity.Tests.Infrastructure;
using System.Net;
using System.Text;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     <see cref="HttpClusterOidcDiscovery" /> — the component that decides, <b>at binding time</b>,
///     whether a cluster can host a managed identity at all. docs/plan/11 § Managed identity.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything here is about the refusal.</b> docs/plan/11 § Managed identity: the flow
///     "requires the tenant's cluster to expose a <b>publicly reachable</b> OIDC discovery document
///     … for BYO clusters that is not automatic, and the portal must say so at binding time rather
///     than failing at token exchange." The success path is one test; the ways a cluster can fail to
///     be a trust anchor are the rest, and each of them has to produce a sentence an administrator
///     can act on.
/// </remarks>
public sealed class ClusterOidcDiscoveryTests {
    const string Issuer = "https://oidc.cluster.example";

    static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    const string KeySet = """{"keys":[{"kty":"EC","crv":"P-256","kid":"k","x":"AA","y":"BB"}]}""";

    static string Document(string? issuer = null, string? jwksUri = null) =>
        $$"""
          {"issuer":"{{issuer ?? Issuer}}","jwks_uri":"{{jwksUri ?? Issuer + "/openid/v1/jwks"}}"}
          """;

    static HttpClusterOidcDiscovery Discovery(params (string Url, HttpResponseMessage Response)[] routes) =>
        new(new(new StubHandler(routes)), new FixedClock(Now));

    static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task AReachableClusterIsRecordedWithItsIssuerAndKeySet() {
        var discovery = Discovery(
            (Issuer + "/.well-known/openid-configuration", Json(Document())),
            (Issuer + "/openid/v1/jwks", Json(KeySet))
        );

        var read = await discovery.DiscoverAsync(Issuer, TestContext.Current.CancellationToken);

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);

        var issuer = read.GetValueOrThrow();

        issuer.Issuer.ShouldBe(Issuer);
        issuer.KeySetUri.ShouldBe(Issuer + "/openid/v1/jwks");
        issuer.PublicKeySetJson.ShouldContain("\"kty\"");
        issuer.ReadAt.ShouldBe(Now);
        issuer.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task AClusterThatCannotBeReachedIsRefusedWithAMessageSayingWhatToPublish() {
        // ⚠ THE BYO CASE, WHICH IS THE COMMON ONE. A private cluster's control-plane endpoint simply
        // does not answer from here. The refusal has to be a sentence an administrator can act on,
        // because that is the entire argument for checking at binding time.
        var discovery = Discovery();

        var refused = await discovery.DiscoverAsync(Issuer, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();

        var message = refused.Error!.Message;

        message.ShouldContain("OIDC discovery document");
        message.ShouldContain("/.well-known/openid-configuration");
        message.ShouldContain("jwks_uri");
        message.ShouldContain("bring-your-own");
        message.ShouldContain("agent tunnel");
        message.ShouldContain("rather than at token exchange");

        // ⚠ And the specific reason, because "it did not work" sends somebody to a support queue.
        message.ShouldContain("could not be reached");
    }

    [Fact]
    public async Task ADiscoveryDocumentClaimingSomebodyElsesIssuerIsRefused() {
        // ⚠ OIDC Discovery requires the document's `issuer` to equal the URL it was read from, and
        // this is why: without the check, any cluster can publish a document claiming a well-known
        // issuer, and every token that issuer ever signed then validates against a key set this
        // cluster chose.
        var discovery = Discovery(
            (Issuer + "/.well-known/openid-configuration", Json(Document("https://oidc.someone-else.example"))),
            (Issuer + "/openid/v1/jwks", Json(KeySet))
        );

        var refused = await discovery.DiscoverAsync(Issuer, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("claims the issuer");
    }

    [Theory]
    [InlineData("http://oidc.cluster.example", "plaintext lets anyone on the path substitute the keys")]
    [InlineData("oidc.cluster.example", "not absolute")]
    [InlineData("", "empty")]
    [InlineData("file:///etc/passwd", "not https")]
    public async Task AnIssuerThatIsNotAnAbsoluteHttpsUrlIsRefusedWithoutAnyFetch(string issuer, string why) {
        // ⚠ Refused before a request is made. The key set decides who a workload is; fetched over
        // plaintext, anybody on the path substitutes their own and mints tokens for any service
        // account in the tenant — so http does not weaken the check, it removes it.
        var handler = new StubHandler([]);
        var discovery = new HttpClusterOidcDiscovery(new(handler), new FixedClock(Now));

        var refused = await discovery.DiscoverAsync(issuer, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue(why);
        refused.Error!.Message.ShouldContain("absolute https URL");
        handler.Requests.ShouldBeEmpty("nothing should have been fetched");
    }

    [Fact]
    public async Task AKeySetServedByADifferentHostIsRefused() {
        // OIDC permits a jwks_uri anywhere; a Kubernetes API server always serves both from the same
        // host. Requiring it costs nothing legitimate and removes "the discovery document can point
        // key fetching at an arbitrary host" from a path that ends in an authentication decision.
        var discovery = Discovery(
            (Issuer + "/.well-known/openid-configuration", Json(Document(jwksUri: "https://keys.elsewhere.example/jwks"))),
            ("https://keys.elsewhere.example/jwks", Json(KeySet))
        );

        var refused = await discovery.DiscoverAsync(Issuer, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("rather than by the issuer itself");
    }

    [Fact]
    public async Task AnEmptyKeySetIsRefused() {
        // A cluster that serves a syntactically fine but empty key set can verify nothing, so a
        // binding against it would be a binding that cannot ever exchange — the exact failure the
        // binding-time check exists to move forward in time.
        var discovery = Discovery(
            (Issuer + "/.well-known/openid-configuration", Json(Document())),
            (Issuer + "/openid/v1/jwks", Json("""{"keys":[]}"""))
        );

        var refused = await discovery.DiscoverAsync(Issuer, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("no keys");
    }

    [Fact]
    public async Task ADocumentThatIsNotJsonOrIsAnErrorIsRefused() {
        var notJson = Discovery(
            (Issuer + "/.well-known/openid-configuration",
                new(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") })
        );

        (await notJson.DiscoverAsync(Issuer, TestContext.Current.CancellationToken)).Error!.Message.ShouldContain("did not return JSON");

        var forbidden = Discovery(
            (Issuer + "/.well-known/openid-configuration", new HttpResponseMessage(HttpStatusCode.Forbidden))
        );

        // ⚠ A 403 is the shape of "the endpoint exists but is not anonymous", which is the second
        // most common BYO misconfiguration after "not routable". The number is in the message
        // because it is what the administrator will search their own logs for.
        (await forbidden.DiscoverAsync(Issuer, TestContext.Current.CancellationToken)).Error!.Message.ShouldContain("answered 403");
    }

    [Fact]
    public async Task AnEnormousDocumentIsRefusedRatherThanRead() {
        // ⚠ The cluster is a party we do not control, and an unbounded read from one is a memory
        // exhaustion a tenant can point at a silo by binding to a cluster they own.
        var discovery = Discovery(
            (Issuer + "/.well-known/openid-configuration",
                Json("{\"issuer\":\"" + Issuer + "\",\"pad\":\"" + new string('x', 200_000) + "\"}"))
        );

        (await discovery.DiscoverAsync(Issuer, TestContext.Current.CancellationToken)).Error!.Message.ShouldContain("more than");
    }

    /// <summary>Answers from a table and records what was asked for. Nothing leaves the process.</summary>
    sealed class StubHandler(IReadOnlyList<(string Url, HttpResponseMessage Response)> routes) : HttpMessageHandler {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            foreach (var (candidate, response) in routes) {
                if (string.Equals(candidate, url, StringComparison.Ordinal)) {
                    return Task.FromResult(response);
                }
            }

            // ⚠ What an unreachable cluster actually does: nothing answers. HttpRequestException is
            // what HttpClient surfaces for a connection that cannot be made.
            throw new HttpRequestException($"No route to {url}.");
        }
    }

    sealed class FixedClock(DateTimeOffset now) : IClock {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
