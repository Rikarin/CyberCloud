using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     The NuGet v3 API, driven with the requests <c>dotnet nuget push</c>, <c>dotnet restore</c>,
///     <c>dotnet add package</c> and <c>dotnet nuget delete</c> make.
/// </summary>
[Collection(FeedsHostSuite.Name)]
public sealed class NuGetProtocolTests(FeedsHostFixture host) {
    static string Base => FeedsHostFixture.Feed(FeedKind.NuGet, FeedsHostFixture.NuGetFeed);

    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheServiceIndexNamesEveryResourceAClientLooksFor() {
        using var client = host.Client(host.Alice);
        using var response = await client.GetAsync(Base + "/v3/index.json", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        using var index = JsonDocument.Parse(await response.BodyAsync());
        index.RootElement.GetProperty("version").GetString().ShouldBe("3.0.0");

        var resources = index.RootElement.GetProperty("resources")
            .EnumerateArray()
            .ToDictionary(
                static x => x.GetProperty("@type").GetString()!,
                static x => x.GetProperty("@id").GetString()!
            );

        var origin = host.BaseAddress.ToString().TrimEnd('/');
        resources["PackagePublish/2.0.0"].ShouldBe(origin + Base + "/v2/package");
        resources["PackageBaseAddress/3.0.0"].ShouldBe(origin + Base + "/v3/flatcontainer/");
        resources["RegistrationsBaseUrl/3.6.0"].ShouldBe(origin + Base + "/v3/registration/");
        resources["SearchQueryService/3.0.0-beta"].ShouldBe(origin + Base + "/v3/query");
    }

    [Fact]
    public async Task APushIsStoredListedRegisteredSearchableAndRestorable() {
        // ⚠ The whole client round trip in one test, because each step reads what the previous one
        // wrote and a version spelled `1.0` in the nuspec has to come back as `1.0.0` everywhere.
        var nupkg = TestPackages.NuGet("Cyber.Roundtrip", "1.0", "Round trip.", ("Newtonsoft.Json", "[13.0.1, )"));

        using var client = host.Client();

        // dotnet nuget push: multipart, X-NuGet-ApiKey, no Authorization header.
        using (var push = new HttpRequestMessage(HttpMethod.Put, Base + "/v2/package") {
                   Content = TestPackages.NuGetPush(nupkg)
               }.WithHeader("X-NuGet-ApiKey", host.Alice)) {
            using (var response = await client.SendAsync(push, Token)) {
                response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.BodyAsync());
            }
        }

        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Alice);

        // The flat container, lower-cased and normalised.
        using (var versions = await client.GetAsync(Base + "/v3/flatcontainer/cyber.roundtrip/index.json", Token)) {
            versions.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await versions.BodyAsync());
            document.RootElement.GetProperty("versions")
                .EnumerateArray()
                .Select(static x => x.GetString())
                .ShouldBe(["1.0.0"]);
        }

        // The bytes, byte for byte, at the address the client computes from the flat container.
        using (var download = await client.GetAsync(
                   Base + "/v3/flatcontainer/cyber.roundtrip/1.0.0/cyber.roundtrip.1.0.0.nupkg",
                   Token
               )) {
            download.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await download.BytesAsync()).ShouldBe(nupkg);
        }

        // The nuspec on its own, which the client reads without downloading the package.
        using (var nuspec = await client.GetAsync(
                   Base + "/v3/flatcontainer/cyber.roundtrip/1.0.0/cyber.roundtrip.nuspec",
                   Token
               )) {
            nuspec.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await nuspec.BodyAsync()).ShouldContain("<id>Cyber.Roundtrip</id>");
        }

        // The registration: one page, one leaf, the dependency group rendered.
        using (var registration = await client.GetAsync(Base + "/v3/registration/cyber.roundtrip/index.json", Token)) {
            registration.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await registration.BodyAsync());

            var page = document.RootElement.GetProperty("items").EnumerateArray().Single();
            page.GetProperty("lower").GetString().ShouldBe("1.0.0");

            var leaf = page.GetProperty("items").EnumerateArray().Single();
            var entry = leaf.GetProperty("catalogEntry");
            entry.GetProperty("id").GetString().ShouldBe("Cyber.Roundtrip");
            entry.GetProperty("version").GetString().ShouldBe("1.0.0");
            entry.GetProperty("listed").GetBoolean().ShouldBeTrue();
            entry.GetProperty("packageContent")
                .GetString()
                .ShouldEndWith("/v3/flatcontainer/cyber.roundtrip/1.0.0/cyber.roundtrip.1.0.0.nupkg");

            var group = entry.GetProperty("dependencyGroups").EnumerateArray().Single();
            group.GetProperty("targetFramework").GetString().ShouldBe("net8.0");
            group.GetProperty("dependencies")
                .EnumerateArray()
                .Single()
                .GetProperty("id")
                .GetString()
                .ShouldBe("Newtonsoft.Json");
        }

        // Search finds it by id.
        using (var search = await client.GetAsync(Base + "/v3/query?q=roundtrip&prerelease=false", Token)) {
            search.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await search.BodyAsync());
            document.RootElement.GetProperty("totalHits").GetInt32().ShouldBe(1);

            var hit = document.RootElement.GetProperty("data").EnumerateArray().Single();
            hit.GetProperty("id").GetString().ShouldBe("Cyber.Roundtrip");
            hit.GetProperty("version").GetString().ShouldBe("1.0.0");
            hit.GetProperty("authors").EnumerateArray().Select(static x => x.GetString()).ShouldBe(["Cyber Cloud"]);
        }
    }

    [Fact]
    public async Task APushOfAVersionThatExistsIs409AndTheFirstBytesStay() {
        using var client = host.Client(host.Alice);

        var first = TestPackages.NuGet("Cyber.Immutable", "2.0.0", "first");
        var second = TestPackages.NuGet("cyber.immutable", "2.0.0.0", "second");

        using (var response = await client.PutAsync(Base + "/v2/package", TestPackages.NuGetPush(first), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // Same version in a different spelling — a different case and a trailing zero — is the
        // same version. A registry that let it through would hand two callers two packages.
        using (var response = await client.PutAsync(Base + "/v2/package", TestPackages.NuGetPush(second), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await response.BodyAsync()).ShouldContain("immutable");
        }

        using var download = await client.GetAsync(
            Base + "/v3/flatcontainer/cyber.immutable/2.0.0/cyber.immutable.2.0.0.nupkg",
            Token
        );
        (await download.BytesAsync()).ShouldBe(first);
    }

    [Fact]
    public async Task AnUnlistLeavesTheVersionRestorableAndOutOfSearchAndARelistBringsItBack() {
        using var client = host.Client(host.Alice);
        var nupkg = TestPackages.NuGet("Cyber.Unlisted", "3.1.0-beta.2", "Unlisted.");

        using (var response = await client.PutAsync(Base + "/v2/package", TestPackages.NuGetPush(nupkg), Token)) {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // dotnet nuget delete
        using (var unlist = await client.DeleteAsync(Base + "/v2/package/Cyber.Unlisted/3.1.0-beta.2", Token)) {
            unlist.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Still in the flat container — a lock file that pins it keeps restoring.
        using (var versions = await client.GetAsync(Base + "/v3/flatcontainer/cyber.unlisted/index.json", Token)) {
            using var document = JsonDocument.Parse(await versions.BodyAsync());
            document.RootElement.GetProperty("versions")
                .EnumerateArray()
                .Select(static x => x.GetString())
                .ShouldBe(["3.1.0-beta.2"]);
        }

        // Gone from search, and `listed` is false on the registration.
        using (var search = await client.GetAsync(Base + "/v3/query?q=unlisted&prerelease=true", Token)) {
            using var document = JsonDocument.Parse(await search.BodyAsync());
            document.RootElement.GetProperty("totalHits").GetInt32().ShouldBe(0);
        }

        using (var leaf = await client.GetAsync(Base + "/v3/registration/cyber.unlisted/3.1.0-beta.2.json", Token)) {
            using var document = JsonDocument.Parse(await leaf.BodyAsync());
            document.RootElement.GetProperty("catalogEntry").GetProperty("listed").GetBoolean().ShouldBeFalse();
        }

        // Relisted.
        using (var relist = await client.PostAsync(Base + "/v2/package/cyber.unlisted/3.1.0-BETA.2", null, Token)) {
            relist.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var search = await client.GetAsync(Base + "/v3/query?q=unlisted&prerelease=true", Token)) {
            using var document = JsonDocument.Parse(await search.BodyAsync());
            document.RootElement.GetProperty("totalHits").GetInt32().ShouldBe(1);
        }

        // And a prerelease stays out of a search that did not ask for one.
        using (var search = await client.GetAsync(Base + "/v3/query?q=unlisted", Token)) {
            using var document = JsonDocument.Parse(await search.BodyAsync());
            document.RootElement.GetProperty("totalHits").GetInt32().ShouldBe(0);
        }
    }

    [Fact]
    public async Task VersionsAreOrderedAsNuGetOrdersThem() {
        using var client = host.Client(host.Alice);

        foreach (var version in new[] { "1.0.0", "1.0.0-alpha", "1.0.0-alpha.10", "1.0.0-alpha.2", "0.9.0", "1.0.1" }) {
            using var response = await client.PutAsync(
                Base + "/v2/package",
                TestPackages.NuGetPush(TestPackages.NuGet("Cyber.Ordered", version)),
                Token
            );
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var versions = await client.GetAsync(Base + "/v3/flatcontainer/cyber.ordered/index.json", Token);
        using var document = JsonDocument.Parse(await versions.BodyAsync());

        document.RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(static x => x.GetString())
            .ShouldBe(["0.9.0", "1.0.0-alpha", "1.0.0-alpha.2", "1.0.0-alpha.10", "1.0.0", "1.0.1"]);
    }

    [Fact]
    public async Task APushThatIsNotAPackageIs400AndStoresNothing() {
        using var client = host.Client(host.Alice);
        var before = host.Objects.Count;

        using (var response = await client.PutAsync(
                   Base + "/v2/package",
                   TestPackages.NuGetPush(TestPackages.ZipWithoutNuspec()),
                   Token
               )) {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.BodyAsync()).ShouldContain("nuspec");
        }

        using (var response = await client.PutAsync(
                   Base + "/v2/package",
                   TestPackages.NuGetPush("not a zip"u8.ToArray()),
                   Token
               )) {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        host.Objects.Count.ShouldBe(before, "a refused push left bytes in the store");
    }

    [Fact]
    public async Task APushLargerThanTheCapIs400() {
        using var client = host.Client(host.Alice);
        var oversized = TestPackages.NuGet(
            "Cyber.Huge",
            "1.0.0",
            payloadBytes: (int)FeedsHostFixture.MaxArtifactBytes + 1
        );

        using var response = await client.PutAsync(Base + "/v2/package", TestPackages.NuGetPush(oversized), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.BodyAsync()).ShouldContain("MaxArtifactBytes");
    }

    [Fact]
    public async Task AnUnknownPackageIs404OnEveryReadRoute() {
        using var client = host.Client(host.Alice);

        foreach (var route in new[] {
                     "/v3/flatcontainer/no.such/index.json", "/v3/flatcontainer/no.such/1.0.0/no.such.1.0.0.nupkg",
                     "/v3/registration/no.such/index.json", "/v3/registration/no.such/1.0.0.json"
                 }) {
            using var response = await client.GetAsync(Base + route, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, route);
        }
    }

    [Fact]
    public async Task ARawBodyPushIsAcceptedToo() {
        // Not every client sends multipart; NuGet's own protocol allows the bytes as the body.
        using var client = host.Client(host.Alice);
        var nupkg = TestPackages.NuGet("Cyber.Raw", "1.0.0");

        using var response = await client.PutAsync(Base + "/v2/package", new ByteArrayContent(nupkg), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // ⚠ And the bytes are where the reconciler's teardown will look for them — under the
        // feed's storage prefix, which is the contract between the host and the provider — in a
        // directory named by their own hash, which is what keeps a racing second push off them.
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(nupkg));
        var stored = await host.Objects.ListAsync("", Token);
        stored.GetValueOrThrow()
            .ShouldContain(x => x.EndsWith(
                    $"/nuget/cyber.raw/1.0.0/{sha256}/cyber.raw.1.0.0.nupkg",
                    StringComparison.Ordinal
                )
            );
        stored.GetValueOrThrow()
            .ShouldContain(x => x.EndsWith(
                    $"/nuget/cyber.raw/1.0.0/{sha256}/cyber.raw.nuspec",
                    StringComparison.Ordinal
                )
            );
    }
}
