using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     The npm registry API, driven with the requests <c>npm publish</c>, <c>npm install</c>,
///     <c>npm dist-tag</c>, <c>npm search</c> and <c>npm ping</c> make.
/// </summary>
[Collection(FeedsHostSuite.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5350:Do Not Use Weak Cryptographic Algorithms",
    Justification = "npm's shasum IS SHA-1; the test computes what the protocol defines and compares it to what the host served."
)]
public sealed class NpmProtocolTests(FeedsHostFixture host) {
    static string Base => FeedsHostFixture.Feed(FeedKind.Npm, FeedsHostFixture.NpmFeed);

    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APublishIsInstallableThroughThePackumentAndTheTarballItNames() {
        var (document, tarball, fileName) = TestPackages.Npm("cyber-roundtrip", "1.2.3", "Round trip.");
        using var client = host.Client(host.Alice);

        // npm publish
        using (var response = await client.PutAsync(Base + "/cyber-roundtrip", HttpAssertions.Json(document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.BodyAsync());
        }

        // npm install reads the packument…
        string tarballUrl;

        using (var response = await client.GetAsync(Base + "/cyber-roundtrip", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var packument = JsonDocument.Parse(await response.BodyAsync());
            var root = packument.RootElement;

            root.GetProperty("name").GetString().ShouldBe("cyber-roundtrip");
            root.GetProperty("dist-tags").GetProperty("latest").GetString().ShouldBe("1.2.3");
            root.GetProperty("description").GetString().ShouldBe("Round trip.");
            root.GetProperty("time").GetProperty("1.2.3").GetString().ShouldNotBeNullOrEmpty();

            var version = root.GetProperty("versions").GetProperty("1.2.3");
            version.GetProperty("name").GetString().ShouldBe("cyber-roundtrip");

            var dist = version.GetProperty("dist");
            dist.GetProperty("shasum").GetString().ShouldBe(Convert.ToHexStringLower(SHA1.HashData(tarball)));
            dist.GetProperty("integrity").GetString().ShouldBe("sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)));
            tarballUrl = dist.GetProperty("tarball").GetString()!;
            tarballUrl.ShouldEndWith(Base + "/cyber-roundtrip/-/" + fileName);
        }

        // …and fetches the tarball at exactly the URL the packument named.
        using (var response = await client.GetAsync(new Uri(tarballUrl), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.BytesAsync()).ShouldBe(tarball);
        }

        // HEAD, which some clients send first.
        using (var head = new HttpRequestMessage(HttpMethod.Head, new Uri(tarballUrl)))
        using (var response = await client.SendAsync(head, Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AScopedPackageRoundTripsWithAndWithoutTheEscapedSlash() {
        // npm sends `@scope%2Fname` on some paths and `@scope/name` on others; both are one name.
        var (document, tarball, fileName) = TestPackages.Npm("@cyber/scoped", "0.1.0");
        using var client = host.Client(host.Alice);

        using (var response = await client.PutAsync(Base + "/@cyber%2Fscoped", HttpAssertions.Json(document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.BodyAsync());
        }

        using (var response = await client.GetAsync(Base + "/@cyber/scoped", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var packument = JsonDocument.Parse(await response.BodyAsync());
            packument.RootElement.GetProperty("versions").GetProperty("0.1.0").GetProperty("dist").GetProperty("tarball").GetString()
                .ShouldEndWith("/@cyber/scoped/-/" + fileName);
        }

        using (var response = await client.GetAsync(Base + "/@cyber/scoped/-/" + fileName, Token)) {
            (await response.BytesAsync()).ShouldBe(tarball);
        }
    }

    [Fact]
    public async Task APublishOverAnExistingVersionIs409AndANewVersionMovesLatest() {
        using var client = host.Client(host.Alice);

        using (var response = await client.PutAsync(Base + "/cyber-immutable", HttpAssertions.Json(TestPackages.Npm("cyber-immutable", "1.0.0").Document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await client.PutAsync(Base + "/cyber-immutable", HttpAssertions.Json(TestPackages.Npm("cyber-immutable", "1.0.0", "again").Document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var response = await client.PutAsync(Base + "/cyber-immutable", HttpAssertions.Json(TestPackages.Npm("cyber-immutable", "1.1.0").Document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await client.GetAsync(Base + "/cyber-immutable", Token)) {
            using var packument = JsonDocument.Parse(await response.BodyAsync());
            packument.RootElement.GetProperty("dist-tags").GetProperty("latest").GetString().ShouldBe("1.1.0");
            packument.RootElement.GetProperty("versions").EnumerateObject().Select(x => x.Name).ShouldBe(["1.0.0", "1.1.0"]);
        }
    }

    [Fact]
    public async Task DistTagsAreReadWrittenAndRemovedAndLatestCannotBeRemoved() {
        using var client = host.Client(host.Alice);

        using (var response = await client.PutAsync(Base + "/cyber-tagged", HttpAssertions.Json(TestPackages.Npm("cyber-tagged", "2.0.0-rc.1", "rc", ("next", "2.0.0-rc.1")).Document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // npm dist-tag ls
        using (var response = await client.GetAsync(Base + "/-/package/cyber-tagged/dist-tags", Token)) {
            using var tags = JsonDocument.Parse(await response.BodyAsync());
            tags.RootElement.GetProperty("next").GetString().ShouldBe("2.0.0-rc.1");
            tags.RootElement.GetProperty("latest").GetString().ShouldBe("2.0.0-rc.1");
        }

        // npm dist-tag add cyber-tagged@2.0.0-rc.1 beta
        using (var response = await client.PutAsync(Base + "/-/package/cyber-tagged/dist-tags/beta", HttpAssertions.Json("\"2.0.0-rc.1\""), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // A tag onto a version that does not exist is a 404.
        using (var response = await client.PutAsync(Base + "/-/package/cyber-tagged/dist-tags/broken", HttpAssertions.Json("\"9.9.9\""), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // npm dist-tag rm
        using (var response = await client.DeleteAsync(Base + "/-/package/cyber-tagged/dist-tags/next", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await client.DeleteAsync(Base + "/-/package/cyber-tagged/dist-tags/latest", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var response = await client.GetAsync(Base + "/-/package/cyber-tagged/dist-tags", Token)) {
            using var tags = JsonDocument.Parse(await response.BodyAsync());
            tags.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ShouldBe(["beta", "latest"]);
        }
    }

    [Fact]
    public async Task SearchAndPingAnswer() {
        using var client = host.Client(host.Alice);

        using (var response = await client.PutAsync(Base + "/cyber-findable", HttpAssertions.Json(TestPackages.Npm("cyber-findable", "1.0.0", "A needle in the haystack.").Document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await client.GetAsync(Base + "/-/v1/search?text=needle&size=5", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var results = JsonDocument.Parse(await response.BodyAsync());
            results.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
            results.RootElement.GetProperty("objects").EnumerateArray().Single().GetProperty("package").GetProperty("name").GetString().ShouldBe("cyber-findable");
        }

        using (var response = await client.GetAsync(Base + "/-/ping", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AReaderCanInstallAndCannotPublish() {
        using var alice = host.Client(host.Alice);
        using var bob = host.Client(host.Bob);

        using (var response = await alice.PutAsync(Base + "/cyber-readonly", HttpAssertions.Json(TestPackages.Npm("cyber-readonly", "1.0.0").Document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await bob.GetAsync(Base + "/cyber-readonly", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await bob.PutAsync(Base + "/cyber-readonly", HttpAssertions.Json(TestPackages.Npm("cyber-readonly", "1.0.1").Document), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await bob.PutAsync(Base + "/-/package/cyber-readonly/dist-tags/beta", HttpAssertions.Json("\"1.0.0\""), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task ADocumentThatIsNotAPublishIs400AndAnUnknownPackageIs404() {
        using var client = host.Client(host.Alice);

        using (var response = await client.PutAsync(Base + "/cyber-broken", HttpAssertions.Json("""{"name":"cyber-other","versions":{}}"""), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var response = await client.PutAsync(Base + "/cyber-broken", HttpAssertions.Json("not json"), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var response = await client.GetAsync(Base + "/cyber-nobody", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await client.GetAsync(Base + "/cyber-nobody/-/cyber-nobody-1.0.0.tgz", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // A name npm itself refuses.
        using (var response = await client.GetAsync(Base + "/Not%20A%20Name", Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task APublishWhoseAttachmentIsNamedForAnotherVersionIs400AndThatVersionsBytesStay() {
        // ⚠ THE REVIEW'S PROBE, KEPT. Publish 1.0.0; then publish 1.0.1 with its attachment keyed
        // `…-1.0.0.tgz`. Before the fix both were 201 and 1.0.0's tarball served 1.0.1's bytes
        // under 1.0.0's shasum — every install of 1.0.0 failing integrity, forever.
        var (first, firstTarball, firstFile) = TestPackages.Npm("cyber-overwrite", "1.0.0");
        var (second, secondTarball, _) = TestPackages.Npm("cyber-overwrite", "1.0.1", "Named after the first.");
        var misnamed = second.Replace("cyber-overwrite-1.0.1.tgz", firstFile, StringComparison.Ordinal);
        misnamed.ShouldNotBe(second);

        using var client = host.Client(host.Alice);

        using (var response = await client.PutAsync(Base + "/cyber-overwrite", HttpAssertions.Json(first), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.BodyAsync());
        }

        using (var response = await client.PutAsync(Base + "/cyber-overwrite", HttpAssertions.Json(misnamed), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await response.BodyAsync();
            body.ShouldContain(firstFile);
            body.ShouldContain("cyber-overwrite-1.0.1.tgz");
        }

        using (var response = await client.GetAsync(Base + "/cyber-overwrite/-/" + firstFile, Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var served = await response.BytesAsync();
            served.ShouldBe(firstTarball, "the first version's bytes were overwritten");
            served.ShouldNotBe(secondTarball);
        }

        // And 1.0.1 is not there at all: a refused publish leaves nothing behind.
        using (var response = await client.GetAsync(Base + "/cyber-overwrite", Token)) {
            using var packument = JsonDocument.Parse(await response.BodyAsync());
            packument.RootElement.GetProperty("versions").EnumerateObject().Select(x => x.Name).ShouldBe(["1.0.0"]);
        }

        // libnpmpublish keys the attachment with the scope still on the name; that spelling is the
        // same tarball and is accepted.
        var (scoped, scopedTarball, scopedFile) = TestPackages.Npm("@cyber/attached", "2.0.0");
        var asLibnpmpublish = scoped.Replace("\"" + scopedFile + "\"", "\"@cyber/attached-2.0.0.tgz\"", StringComparison.Ordinal);
        asLibnpmpublish.ShouldNotBe(scoped);

        using (var response = await client.PutAsync(Base + "/@cyber%2Fattached", HttpAssertions.Json(asLibnpmpublish), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.BodyAsync());
        }

        using (var response = await client.GetAsync(Base + "/@cyber/attached/-/" + scopedFile, Token)) {
            (await response.BytesAsync()).ShouldBe(scopedTarball);
        }
    }

    [Fact]
    public async Task AnNpmRouteOnTheNuGetFeedIs404() {
        using var client = host.Client(host.Alice);
        using var response = await client.GetAsync(FeedsHostFixture.Feed(FeedKind.Npm, FeedsHostFixture.NuGetFeed) + "/-/ping", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
