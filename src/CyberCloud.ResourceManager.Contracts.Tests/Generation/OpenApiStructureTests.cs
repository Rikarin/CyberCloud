using CyberCloud.ResourceManager.Contracts.Generation;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     The validator that stands behind "the document is valid OpenAPI 3.1".
/// </summary>
/// <remarks>
///     ⚠ <b>These are the tests that make the emitter's clean bill of health mean anything.</b> A
///     validator that returned no problems for every input would pass every document ever written,
///     and <c>OpenApiEmitterTests</c> would go green over a broken surface. So every rule
///     <see cref="OpenApiStructure" /> claims to check is broken here on purpose and asserted to be
///     caught.
/// </remarks>
public sealed class OpenApiStructureTests {
    /// <summary>A minimal document that passes, for each case below to break in exactly one way.</summary>
    static JsonObject Sound() =>
        new() {
            ["openapi"] = "3.1.1",
            ["info"] = new JsonObject { ["title"] = "Cyber Cloud", ["version"] = "2026-08-01" },
            ["paths"] = new JsonObject {
                ["/things/{name}"] = new JsonObject {
                    ["parameters"] = new JsonArray { new JsonObject { ["$ref"] = "#/components/parameters/Name" } },
                    ["get"] = new JsonObject {
                        ["operationId"] = "Things_Get",
                        ["responses"] = new JsonObject {
                            ["200"] = new JsonObject { ["description"] = "The thing." }
                        }
                    }
                }
            },
            ["components"] = new JsonObject {
                ["parameters"] = new JsonObject {
                    ["Name"] = new JsonObject {
                        ["name"] = "name",
                        ["in"] = "path",
                        ["required"] = true,
                        ["schema"] = new JsonObject { ["type"] = "string" }
                    }
                },
                ["schemas"] = new JsonObject {
                    ["Thing"] = new JsonObject {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["size"] = new JsonObject { ["type"] = "integer" } },
                        ["required"] = new JsonArray { "size" }
                    }
                }
            }
        };

    [Fact]
    public void TheSoundDocumentPasses() => OpenApiStructure.Validate(Sound()).ShouldBeEmpty();

    [Fact]
    public void ADanglingLocalReferenceIsCaught() {
        var document = Sound();
        document["components"]!["schemas"]!["Thing"]!["properties"]!["size"] =
            new JsonObject { ["$ref"] = "#/components/schemas/NoSuchThing" };

        OpenApiStructure.Validate(document).ShouldContain(x => x.Contains("resolves to nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExternalReferenceIsRefused() {
        // docs/plan/21 § OpenAPI makes the document "the contract". A contract that is only complete
        // if a second file is fetched is not one — and a one-file diff cannot see a break in the other.
        var document = Sound();
        document["components"]!["schemas"]!["Thing"] = new JsonObject { ["$ref"] = "common.json#/Thing" };

        OpenApiStructure.Validate(document).ShouldContain(x => x.Contains("not a local reference", StringComparison.Ordinal));
    }

    [Fact]
    public void AComponentKeyWithASlashIsCaught() {
        // The rule that decides how a nested resource type's name is folded — CyberCloud.X/servers is
        // not a legal component key and CyberCloud.X.servers is.
        var document = Sound();
        document["components"]!["schemas"]!.AsObject()["CyberCloud.X/servers"] =
            new JsonObject { ["type"] = "object" };

        OpenApiStructure.Validate(document).ShouldContain(x => x.Contains("not a legal component key", StringComparison.Ordinal));
    }

    [Fact]
    public void APlaceholderWithNoParameterIsCaught() {
        // The rule the emitter is most likely to break: the template is string concatenation and the
        // parameter list is $refs, and nothing but this connects them.
        var document = Sound();
        var item = document["paths"]!["/things/{name}"]!.DeepClone();
        document["paths"]!.AsObject().Remove("/things/{name}");
        document["paths"]!.AsObject()["/things/{name}/{child}"] = item;

        OpenApiStructure.Validate(document).ShouldContain(x => x.Contains("{child}", StringComparison.Ordinal));
    }

    [Fact]
    public void AParameterThatIsNotInTheTemplateIsCaught() {
        var document = Sound();
        var item = document["paths"]!["/things/{name}"]!.DeepClone();
        document["paths"]!.AsObject().Remove("/things/{name}");
        document["paths"]!.AsObject()["/things"] = item;

        OpenApiStructure.Validate(document)
            .ShouldContain(x => x.Contains("declared and does not appear in the template", StringComparison.Ordinal));
    }

    [Fact]
    public void ADuplicateOperationIdIsCaught() {
        // Every SDK generator turns operationId into a method name.
        var document = Sound();
        document["paths"]!.AsObject()["/others/{name}"] = document["paths"]!["/things/{name}"]!.DeepClone();

        OpenApiStructure.Validate(document).ShouldContain(x => x.Contains("is already used by", StringComparison.Ordinal));
    }

    [Fact]
    public void ARequiredPropertyThatIsNotDeclaredIsCaught() {
        var document = Sound();
        document["components"]!["schemas"]!["Thing"]!["required"] = new JsonArray { "size", "colour" };

        OpenApiStructure.Validate(document)
            .ShouldContain(x => x.Contains("'colour', which is not in this schema's properties", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOperationWithNoResponseIsCaught() {
        var document = Sound();
        document["paths"]!["/things/{name}"]!["get"]!.AsObject().Remove("responses");

        OpenApiStructure.Validate(document)
            .ShouldContain(x => x.Contains("nothing a client can handle", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonStatusResponseKeyIsCaught() {
        var document = Sound();
        document["paths"]!["/things/{name}"]!["get"]!["responses"]!.AsObject()["ok"] =
            new JsonObject { ["description"] = "?" };

        OpenApiStructure.Validate(document)
            .ShouldContain(x => x.Contains("three-digit status code or 'default'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInventedJsonTypeIsCaught() {
        var document = Sound();
        document["components"]!["schemas"]!["Thing"]!["properties"]!["size"]!["type"] = "int";

        OpenApiStructure.Validate(document)
            .ShouldContain(x => x.Contains("'int' is not a JSON Schema type", StringComparison.Ordinal));
    }

    [Fact]
    public void A30DocumentIsRefused() {
        var document = Sound();
        document["openapi"] = "3.0.3";

        OpenApiStructure.Validate(document).ShouldContain(x => x.Contains("is not a 3.1 version", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingPathsObjectIsCaught() {
        // Empty is legal; absent is not.
        var document = Sound();
        document.Remove("paths");

        OpenApiStructure.Validate(document).ShouldContain(x => x.Contains("/paths — missing", StringComparison.Ordinal));
    }

    [Fact]
    public void AMalformedDocumentFromDiskIsReportedRatherThanThrown() {
        // The validator is pointed at files that came off disk as well as at documents this process
        // built, and one that threw on a malformed file would report nothing about it.
        var document = Sound();
        document["openapi"] = 31;
        document["info"]!["title"] = new JsonArray();

        OpenApiStructure.Validate(document).ShouldNotBeEmpty();
    }

    [Fact]
    public void EveryProblemIsReportedNotJustTheFirst() {
        var document = Sound();
        document["openapi"] = "3.0.3";
        document["components"]!["schemas"]!["Thing"]!["properties"]!["size"]!["type"] = "int";
        document["components"]!["schemas"]!["Thing"]!["required"] = new JsonArray { "colour" };

        OpenApiStructure.Validate(document).Length.ShouldBeGreaterThanOrEqualTo(3);
    }
}
