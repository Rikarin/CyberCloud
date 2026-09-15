using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The served resource body is the published one: the envelope, then the projected document's
///     own members, with <c>location</c> once — issue #72.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every snapshot here comes from <see cref="ProjectedSnapshot" />, which runs the real
///         projection.</b> That is the whole point of the class. The body was served as
///         <c>properties.properties.*</c> with <c>location</c> twice for as long as this suite
///         rendered a hand-written snapshot, because the writer and the substitute agreed with each
///         other and neither agreed with the grain. <c>TenantOverHttpTests</c> found it by driving the
///         real one; this class is the cheap version of that proof, and it holds only while the
///         substitute's shape is derived rather than typed.
///     </para>
///     <para>
///         The assertions read the body the way a generated client does — <c>properties.sku</c>, a
///         top-level <c>location</c> — and count members rather than index them, because
///         <see cref="JsonDocument" /> resolves a duplicated name to one of its values and a lookup
///         would pass over a body that carried <c>location</c> twice.
///     </para>
/// </remarks>
public sealed class ResourceBodyShapeTests {
    static string CollectionPath(Guid tenantId) =>
        $"/tenants/{tenantId:D}/subscriptions/{GatewayHarness.Subscription:D}/resourceGroups/prod"
        + "/providers/CyberCloud.DBforPostgreSQL/servers";

    [Fact]
    public async Task TheBodyIsTheProjectedDocumentSplicedIntoTheEnvelope() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);

        using var document = JsonDocument.Parse(response.Body);
        var resource = document.RootElement;

        // The published shape: the type's own leaves directly under `properties`.
        resource.GetProperty("properties").GetProperty("sku").GetString().ShouldBe("gp1");

        // The two symptoms of #72, by name.
        resource.GetProperty("properties").TryGetProperty("properties", out _)
            .ShouldBeFalse("the projected document was nested under a second `properties` member");
        resource.GetProperty("properties").TryGetProperty("location", out _)
            .ShouldBeFalse("`location` reached the wire inside `properties` as well as beside it");

        Names(resource).Count(x => x == "location").ShouldBe(1, "`location` is served once, at the top level");
        resource.GetProperty("location").GetString().ShouldBe("eu-central");

        Names(resource).ShouldBe(
            ["id", "name", "type", "location", "provisioningState", "etag", "properties"],
            "the envelope comes first, in the order the Azure shape lists it, and the body follows"
        );
    }

    /// <summary>
    ///     ⚠ Proves the substitute projects rather than passes text through. The superset carries
    ///     <c>onlyInANewerVersion</c>, which no registered api-version declares, and it must not be
    ///     served — the same guarantee
    ///     <c>WritePathTests.AReadAtAnOldVersionKeepsGettingTheShapeItWasWrittenAgainst</c> makes
    ///     against the real grain.
    /// </summary>
    [Fact]
    public async Task AMemberNoVersionDeclaresDoesNotReachTheWire() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Body.ShouldNotContain("onlyInANewerVersion");
    }

    /// <summary>
    ///     <c>ResponseBodies.Collection</c>'s own remark — each element is the same object a
    ///     <c>GET</c> writes, member for member — asserted byte for byte.
    /// </summary>
    [Fact]
    public async Task AListElementIsByteForByteWhatAGetServes() {
        var gateway = new GatewayHarness();
        var path = GatewayHarness.ResourcePath(GatewayHarness.TenantA);

        gateway.Manager.OnList = _ => Result<ResourceListPage>.Success(
            new() { Resources = [ProjectedSnapshot.Of(path)] }
        );

        var single = await gateway.SendAsync("GET", path, gateway.Token(GatewayHarness.TenantA));
        var listed = await gateway.SendAsync(
            "GET",
            CollectionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        single.Status.ShouldBe(StatusCodes.Status200OK);
        listed.Status.ShouldBe(StatusCodes.Status200OK);

        using var page = JsonDocument.Parse(listed.Body);
        var element = page.RootElement.GetProperty("value").EnumerateArray().Single();

        element.GetRawText().ShouldBe(single.Body);
    }

    /// <summary>
    ///     The writer alone, against a snapshot built the way the grain builds one, pinned to the
    ///     exact text — so a reader sees the served shape in one line rather than reconstructing it
    ///     from assertions.
    /// </summary>
    [Fact]
    public void TheWriterRendersTheEnvelopeThenTheBodyThenTags() {
        var snapshot = ProjectedSnapshot.Of("/tenants/t/subscriptions/s/resourceGroups/prod/providers/N/t/main")
            with {
                Tags = ImmutableDictionary<string, string>.Empty.Add("env", "prod")
            };

        ResponseBodies.Resource(snapshot)
            .ShouldBe(
                "{\"id\":\"/tenants/t/subscriptions/s/resourceGroups/prod/providers/N/t/main\","
                + "\"name\":\"main\","
                + "\"type\":\"CyberCloud.DBforPostgreSQL/servers\","
                + "\"location\":\"eu-central\","
                + "\"provisioningState\":\"Succeeded\","
                + "\"etag\":\"etag-1\","
                + "\"properties\":{\"sku\":\"gp1\"},"
                + "\"tags\":{\"env\":\"prod\"}}"
            );
    }

    /// <summary>
    ///     ⚠ The envelope owns its names. A body that carried <c>etag</c> or <c>id</c> at the top
    ///     level — nothing declares one today, and nothing stops a schema from doing so — would
    ///     otherwise make the response carry two.
    /// </summary>
    [Fact]
    public void ABodyMemberNamedLikeAnEnvelopeMemberIsNotWrittenTwice() {
        var snapshot = ProjectedSnapshot.Of("/tenants/t/subscriptions/s/resourceGroups/prod/providers/N/t/main")
            with {
                Body = """{"id":"forged","etag":"forged","location":"forged","properties":{"sku":"gp1"}}"""
            };

        using var document = JsonDocument.Parse(ResponseBodies.Resource(snapshot));
        var names = Names(document.RootElement).ToList();

        names.Count(x => x == "id").ShouldBe(1);
        names.Count(x => x == "etag").ShouldBe(1);
        names.Count(x => x == "location").ShouldBe(1);
        document.RootElement.GetProperty("etag").GetString().ShouldBe("etag-1");
        document.RootElement.GetProperty("location").GetString().ShouldBe("eu-central");
    }

    static IEnumerable<string> Names(JsonElement element) => element.EnumerateObject().Select(x => x.Name);
}
