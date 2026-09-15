using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     Maven's repository layout, driven with the request sequence <c>mvn deploy</c> and a
///     resolver make.
/// </summary>
[Collection(FeedsHostSuite.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5350:Do Not Use Weak Cryptographic Algorithms",
    Justification = "Maven's checksum files are .sha1; the test computes what a deploy would and compares it to what the host served."
)]
public sealed class MavenProtocolTests(FeedsHostFixture host) {
    static string Base => FeedsHostFixture.Feed(FeedKind.Maven, FeedsHostFixture.MavenFeed);

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static ByteArrayContent Bytes(byte[] bytes) => new(bytes);

    [Fact]
    public async Task TheDeploySequenceStoresEveryFileAndAResolverReadsThemBack() {
        // ⚠ The order mvn deploy uses: metadata first (and a 404 is fine), then each file with its
        // checksums, then the merged metadata with its checksums.
        using var client = host.BasicClient(host.Alice, username: "deployer");

        var jar = new byte[1024];
        RandomNumberGenerator.Fill(jar);
        var pom = "<project><groupId>io.cybercloud</groupId><artifactId>widget</artifactId><version>1.0.0</version></project>"u8.ToArray();
        var artifact = Base + "/io/cybercloud/widget";

        using (var response = await client.GetAsync(artifact + "/maven-metadata.xml", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        foreach (var (file, content) in new[] {
                     ("1.0.0/widget-1.0.0.jar", jar),
                     ("1.0.0/widget-1.0.0.jar.sha1", Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA1.HashData(jar)))),
                     ("1.0.0/widget-1.0.0.pom", pom),
                     ("1.0.0/widget-1.0.0.pom.sha1", Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA1.HashData(pom))))
                 }) {
            using var response = await client.PutAsync(artifact + "/" + file, Bytes(content), Token);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, file + ": " + await response.BodyAsync());
        }

        var metadata =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><metadata><groupId>io.cybercloud</groupId><artifactId>widget</artifactId>"
            + "<versioning><latest>1.0.0</latest><release>1.0.0</release><versions><version>1.0.0</version></versions><lastUpdated>20260915000000</lastUpdated></versioning></metadata>";

        using (var response = await client.PutAsync(artifact + "/maven-metadata.xml", Bytes(Encoding.UTF8.GetBytes(metadata)), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // A resolver: the metadata Maven deployed comes back verbatim, then the files.
        using (var response = await client.GetAsync(artifact + "/maven-metadata.xml", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
            (await response.BodyAsync()).ShouldBe(metadata);
        }

        using (var response = await client.GetAsync(artifact + "/1.0.0/widget-1.0.0.jar", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/java-archive");
            (await response.BytesAsync()).ShouldBe(jar);
        }

        using (var response = await client.GetAsync(artifact + "/1.0.0/widget-1.0.0.jar.sha1", Token)) {
            (await response.BodyAsync()).ShouldBe(Convert.ToHexStringLower(SHA1.HashData(jar)));
        }

        using (var head = new HttpRequestMessage(HttpMethod.Head, artifact + "/1.0.0/widget-1.0.0.pom"))
        using (var response = await client.SendAsync(head, Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AReleaseAndItsChecksumsAreImmutableAndMetadataAndSnapshotsAreNot() {
        using var client = host.Client(host.Alice);
        var artifact = Base + "/io/cybercloud/immutable";

        using (var response = await client.PutAsync(artifact + "/2.0.0/immutable-2.0.0.jar", Bytes("first"u8.ToArray()), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await client.PutAsync(artifact + "/2.0.0/immutable-2.0.0.jar", Bytes("second"u8.ToArray()), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await response.BodyAsync()).ShouldContain("immutable");
        }

        using (var response = await client.GetAsync(artifact + "/2.0.0/immutable-2.0.0.jar", Token)) {
            (await response.BodyAsync()).ShouldBe("first");
        }

        // ⚠ A release's checksum and signature are immutable WITH it — a rewritable .sha1 beside an
        // immutable jar is a release only as immutable as its checksum, which the review of #29
        // pointed out. The second PUT is 409 and the first checksum is what a resolver reads.
        foreach (var beside in new[] { "/2.0.0/immutable-2.0.0.jar.sha1", "/2.0.0/immutable-2.0.0.jar.md5", "/2.0.0/immutable-2.0.0.jar.asc" }) {
            using var first = await client.PutAsync(artifact + beside, Bytes("genuine"u8.ToArray()), Token);
            first.StatusCode.ShouldBe(HttpStatusCode.Created, beside);

            using var second = await client.PutAsync(artifact + beside, Bytes("substituted"u8.ToArray()), Token);
            second.StatusCode.ShouldBe(HttpStatusCode.Conflict, beside);

            using var read = await client.GetAsync(artifact + beside, Token);
            (await read.BodyAsync()).ShouldBe("genuine", beside);
        }

        // Metadata and its checksums are rewritten by every deploy.
        foreach (var replaceable in new[] { "/maven-metadata.xml", "/maven-metadata.xml.sha1" }) {
            using var first = await client.PutAsync(artifact + replaceable, Bytes("a"u8.ToArray()), Token);
            first.StatusCode.ShouldBe(HttpStatusCode.Created, replaceable);

            using var second = await client.PutAsync(artifact + replaceable, Bytes("b"u8.ToArray()), Token);
            second.StatusCode.ShouldBe(HttpStatusCode.Created, replaceable);

            using var read = await client.GetAsync(artifact + replaceable, Token);
            (await read.BodyAsync()).ShouldBe("b", replaceable);
        }

        // A snapshot is replaceable throughout, checksums included — that is what a snapshot is.
        foreach (var snapshot in new[] { "/3.0.0-SNAPSHOT/immutable-3.0.0-20260915.101010-1.jar", "/3.0.0-SNAPSHOT/immutable-3.0.0-20260915.101010-1.jar.sha1" }) {
            using var first = await client.PutAsync(artifact + snapshot, Bytes("snap-1"u8.ToArray()), Token);
            using var second = await client.PutAsync(artifact + snapshot, Bytes("snap-2"u8.ToArray()), Token);
            first.StatusCode.ShouldBe(HttpStatusCode.Created, snapshot);
            second.StatusCode.ShouldBe(HttpStatusCode.Created, snapshot);
        }
    }

    [Fact]
    public async Task MetadataNobodyDeployedIsGeneratedFromTheVersionsThatWere() {
        using var client = host.Client(host.Alice);
        var artifact = Base + "/io/cybercloud/generated";

        foreach (var version in new[] { "1.0.0", "1.1.0", "2.0.0-SNAPSHOT" }) {
            using var response = await client.PutAsync($"{artifact}/{version}/generated-{version}.pom", Bytes("<project/>"u8.ToArray()), Token);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        string body;

        using (var response = await client.GetAsync(artifact + "/maven-metadata.xml", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body = await response.BodyAsync();
        }

        var metadata = XDocument.Parse(body).Root!;
        metadata.Element("groupId")!.Value.ShouldBe("io.cybercloud");
        metadata.Element("artifactId")!.Value.ShouldBe("generated");

        var versioning = metadata.Element("versioning")!;
        versioning.Element("release")!.Value.ShouldBe("1.1.0", "the newest non-snapshot");
        versioning.Element("latest")!.Value.ShouldBe("2.0.0-SNAPSHOT");
        versioning.Element("versions")!.Elements("version").Select(x => x.Value).ShouldBe(["1.0.0", "1.1.0", "2.0.0-SNAPSHOT"]);

        // The checksum a resolver fetches beside it is of exactly the bytes it fetched.
        using (var response = await client.GetAsync(artifact + "/maven-metadata.xml.sha1", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.BodyAsync()).ShouldBe(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(body))));
        }
    }

    [Theory]
    [InlineData("io//cybercloud/x.jar")]
    [InlineData("io/cybercloud/x.jar/")]
    [InlineData("io/cyber cloud/x.jar")]
    public async Task APathThatIsNotARepositoryPathIs404BeforeAnythingIsRead(string path) {
        // ⚠ A `..` segment is not in this list because HttpClient collapses it before the request
        // leaves the process; the rule that refuses one is asserted directly in MavenPathTests.

        using var client = host.Client(host.Alice);

        using var put = await client.PutAsync(Base + "/" + path, Bytes("x"u8.ToArray()), Token);
        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var get = await client.GetAsync(Base + "/" + path, Token);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AFileNobodyDeployedIs404AndAReaderCannotDeploy() {
        using var alice = host.Client(host.Alice);
        using var bob = host.Client(host.Bob);

        using (var response = await alice.GetAsync(Base + "/io/cybercloud/absent/1.0.0/absent-1.0.0.jar", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await bob.PutAsync(Base + "/io/cybercloud/readonly/1.0.0/readonly-1.0.0.jar", Bytes("x"u8.ToArray()), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task ADeletedFeedsBytesAreGoneFromTheStoreAndItsRoutesAnswer404() {
        // ⚠ The whole contract between the host and the provider, end to end: the host stored under
        // the feed's prefix, the reconciler's teardown emptied it, and the routes stop answering.
        var feedId = await host.CreateFeedAsync(FeedsHostFixture.Tenant, FeedsHostFixture.Subscription, "alice", "short-lived", "maven");
        var prefix = ArtifactFeeds.StoragePrefix(FeedsHostFixture.Tenant, feedId);
        var feed = FeedsHostFixture.Feed(FeedKind.Maven, "short-lived");

        using var client = host.Client(host.Alice);

        using (var response = await client.PutAsync(feed + "/io/cybercloud/gone/1.0.0/gone-1.0.0.jar", Bytes("bytes"u8.ToArray()), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await host.Objects.ListAsync(prefix, Token)).GetValueOrThrow().ShouldNotBeEmpty("the push did not store under the feed's prefix");

        await host.DeleteFeedAsync(FeedsHostFixture.Tenant, FeedsHostFixture.Subscription, "alice", "short-lived");

        (await host.Objects.ListAsync(prefix, Token)).GetValueOrThrow().ShouldBeEmpty("the teardown left the feed's bytes behind");

        using (var response = await client.GetAsync(feed + "/io/cybercloud/gone/1.0.0/gone-1.0.0.jar", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
