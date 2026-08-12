using CyberCloud.ResourceManager.Contracts.Generation;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     The first of ADR-012's four surfaces. docs/plan/02 § ADR-012 and docs/plan/21 § OpenAPI.
/// </summary>
public sealed class OpenApiEmitterTests {
    const string ServerPath =
        "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
        + "/providers/CyberCloud.DBforPostgreSQL/servers/{resourceName}";

    static JsonObject Emit(IProviderRegistry registry, string version = Fixtures.FirstVersion) =>
        OpenApiEmitter.Emit(registry, ApiVersion.Parse(version));

    // ── Structural validity ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePostgresDocumentIsStructurallyValid() {
        // The whole of "how did you validate it": every $ref resolves, every component key is legal,
        // every path template's placeholders are declared parameters, no operationId repeats, every
        // `required` names a declared property. See OpenApiStructure on what this is and is not.
        OpenApiStructure.Validate(Emit(Fixtures.Postgres())).ShouldBeEmpty();
    }

    [Fact]
    public void TheIndexIsStructurallyValid() =>
        OpenApiStructure.Validate(OpenApiEmitter.EmitIndex(Fixtures.Postgres())).ShouldBeEmpty();

    [Fact]
    public void AnEmptyRegistryProducesAValidEmptyDocumentRatherThanNothing() {
        // ⚠ The vacuous-pass problem, at the emitter. A generator that produces no document when it
        // finds no provider is indistinguishable from one that crashed before writing.
        var document = Emit(Fixtures.Empty);

        OpenApiStructure.Validate(document).ShouldBeEmpty();
        document["paths"]!.AsObject().ShouldContainKey("/operations/{operationId}");
        document["x-cybercloud-resource-type-count"]!.GetValue<int>().ShouldBe(0);

        var index = OpenApiEmitter.EmitIndex(Fixtures.Empty);

        OpenApiStructure.Validate(index).ShouldBeEmpty();
        index["x-cybercloud-providers"]!.AsArray().Count.ShouldBe(0);
        index["x-cybercloud-resource-types"]!.AsArray().Count.ShouldBe(0);
        index["x-cybercloud-api-versions"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void TheDocumentDeclaresOpenApi31() =>
        Emit(Fixtures.Postgres())["openapi"]!.GetValue<string>().ShouldStartWith("3.1.");

    // ── Determinism ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoRunsProduceIdenticalBytes() {
        var first = DeterministicJson.ToBytes(Emit(Fixtures.Postgres()));
        var second = DeterministicJson.ToBytes(Emit(Fixtures.Postgres()));

        second.ShouldBe(first);
    }

    [Fact]
    public void DeclarationOrderDoesNotChangeTheDocument() {
        // Sorting rather than preserving declaration order is what makes a provider reordering two
        // properties a no-op in the diff instead of a wall of moved lines.
        var forwards = Fixtures.PostgresWith(Fixtures.ServerSchema());

        var backwards = Fixtures.PostgresWith(
            ResourceSchema.Of([.. Fixtures.ServerSchema().Properties.Reverse()])
        );

        DeterministicJson.ToBytes(Emit(backwards)).ShouldBe(DeterministicJson.ToBytes(Emit(forwards)));
    }

    [Fact]
    public void TheDocumentCarriesNoPathOrMachineName() {
        var text = DeterministicJson.ToText(Emit(Fixtures.Postgres()));

        // Three of the ways a generated file goes red on somebody else's machine and green on yours.
        text.ShouldNotContain(Environment.MachineName, Case.Insensitive);
        text.ShouldNotContain(Environment.UserName, Case.Insensitive);
        text.ShouldNotContain(AppContext.BaseDirectory.TrimEnd('/'), Case.Insensitive);
    }

    [Fact]
    public void TheOnlyDatesInTheDocumentAreDeclaredApiVersions() {
        // The fourth way, and the one a string search cannot phrase directly: a generation timestamp
        // makes every run differ from the last. Every yyyy-MM-dd in the document is checked against
        // the versions the registry declared, so a clock reaching the output has nowhere to hide.
        var text = DeterministicJson.ToText(Emit(Fixtures.Postgres()));

        Regex.Matches(text, @"\d{4}-\d{2}-\d{2}")
            .Select(x => x.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe([Fixtures.FirstVersion]);
    }

    [Fact]
    public void TheBytesAreUtf8WithoutABomAndEndInOneNewline() {
        var bytes = DeterministicJson.ToBytes(Emit(Fixtures.Postgres()));

        bytes[0].ShouldBe((byte)'{');
        bytes[^1].ShouldBe((byte)'\n');
        bytes[^2].ShouldBe((byte)'}');
        DeterministicJson.ToText(Emit(Fixtures.Postgres())).ShouldNotContain("\r");
    }

    // ── Paths, from the registry and from the platform's id grammar ────────────────────────────

    [Fact]
    public void EveryResourceTypeGetsItsPath() {
        var paths = Emit(Fixtures.Postgres())["paths"]!.AsObject();

        paths.ShouldContainKey(ServerPath);
        paths.ShouldContainKey(ServerPath + "/listKeys");
        paths.ShouldContainKey(ServerPath + "/restart");
        paths.ShouldContainKey(
            "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/{serversName}/databases/{resourceName}"
        );
    }

    [Fact]
    public void ATypeThatDidNotExistAtAVersionHasNoPathInIt() {
        // servers/databases declares 2026-08-01 only. Its absence from the later document is the
        // honest rendering of "that type did not exist yet" — docs/plan/08 § The provider registry.
        var later = Emit(Fixtures.Postgres(), Fixtures.SecondVersion)["paths"]!.AsObject();

        later.ShouldContainKey(ServerPath);
        later.Count(x => x.Key.Contains("databases", StringComparison.Ordinal)).ShouldBe(0);
    }

    [Fact]
    public void TheFourVerbsCarryTheirRegistryPermission() {
        var item = Emit(Fixtures.Postgres())["paths"]![ServerPath]!.AsObject();

        item["get"]!["x-cybercloud-permission"]!.GetValue<string>().ShouldBe("read");
        item["put"]!["x-cybercloud-permission"]!.GetValue<string>().ShouldBe("write");
        item["patch"]!["x-cybercloud-permission"]!.GetValue<string>().ShouldBe("write");
        item["delete"]!["x-cybercloud-permission"]!.GetValue<string>().ShouldBe("delete");

        var action = Emit(Fixtures.Postgres())["paths"]![ServerPath + "/listKeys"]!["post"]!;

        // ⚠ An action's permission is often not `write` — docs/plan/08 § The provider registry.
        action["x-cybercloud-permission"]!.GetValue<string>().ShouldBe("listKeys");
        action["x-cybercloud-secret"]!.GetValue<bool>().ShouldBeTrue();
    }

    // ── The api-version parameter — docs/plan/10 § API versioning ──────────────────────────────

    [Fact]
    public void ApiVersionIsARequiredQueryParameterPinnedToThisDocument() {
        var parameter = Emit(Fixtures.Postgres())["components"]!["parameters"]!["ApiVersion"]!;

        parameter["name"]!.GetValue<string>().ShouldBe("api-version");
        parameter["in"]!.GetValue<string>().ShouldBe("query");
        parameter["required"]!.GetValue<bool>().ShouldBeTrue();

        // The enum is the one version this document describes: a caller sending a different date is
        // talking to a different document, which is the immutable-date rule in one keyword.
        var accepted = parameter["schema"]!["enum"]!.AsArray();
        accepted.Count.ShouldBe(1);
        accepted[0]!.GetValue<string>().ShouldBe(Fixtures.FirstVersion);
    }

    [Fact]
    public void EveryOperationTakesTheApiVersionParameter() {
        var paths = Emit(Fixtures.Postgres())["paths"]!.AsObject();

        foreach (var path in paths) {
            // ⚠ Not every entry is a $ref: a nested type's ancestors are declared inline, because
            // their names come from the type path — see OpenApiEmitter.ResourceParameters.
            var parameters = path.Value!["parameters"]!.AsArray()
                .Select(x => x!["$ref"]?.GetValue<string>())
                .ToList();

            parameters.Contains("#/components/parameters/ApiVersion", StringComparer.Ordinal)
                .ShouldBeTrue($"{path.Key} does not take the api-version parameter");
        }
    }

    // ── Long-running operations — docs/plan/10 § Long-running operations, over HTTP ────────────

    [Theory]
    [InlineData("put")]
    [InlineData("patch")]
    [InlineData("delete")]
    public void AWriteIs202WithTheTwoAzureHeaders(string verb) {
        var responses = Emit(Fixtures.Postgres())["paths"]![ServerPath]![verb]!["responses"]!;

        // 202 and nothing else: docs/plan/08 § The write path, end to end ends in a WriteAccepted for
        // every verb, so there is no synchronous success for an SDK to branch on.
        responses["200"].ShouldBeNull();

        var headers = responses["202"]!["headers"]!;
        headers["Azure-AsyncOperation"]!["required"]!.GetValue<bool>().ShouldBeTrue();
        headers["Azure-AsyncOperation"]!["schema"]!["format"]!.GetValue<string>().ShouldBe("uri");
        headers["Retry-After"]!["required"]!.GetValue<bool>().ShouldBeTrue();
        headers["Retry-After"]!["schema"]!["type"]!.GetValue<string>().ShouldBe("integer");
    }

    [Fact]
    public void TheOperationPollEndpointIsInEveryDocument() {
        var operation = Emit(Fixtures.Empty)["paths"]!["/operations/{operationId}"]!["get"]!;

        operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!
            .GetValue<string>()
            .ShouldBe("#/components/schemas/OperationStatus");
    }

    // ── The one error body — docs/plan/08 § Errors ─────────────────────────────────────────────

    [Fact]
    public void EveryOperationCanReturnTheOneErrorShape() {
        var paths = Emit(Fixtures.Postgres())["paths"]!.AsObject();
        var seen = 0;

        foreach (var path in paths) {
            foreach (var verb in new[] { "get", "put", "patch", "delete", "post" }) {
                if (path.Value![verb] is not JsonObject operation) {
                    continue;
                }

                seen++;
                var responses = operation["responses"]!.AsObject();

                foreach (var status in new[] { "400", "403", "404", "409", "429", "500", "default" }) {
                    responses[status]?["$ref"]?.GetValue<string>()
                        .StartsWith("#/components/responses/", StringComparison.Ordinal)
                        .ShouldBe(true, $"{path.Key} {verb} has no {status} response in the one error shape");
                }
            }
        }

        seen.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void TheErrorCodeEnumIsTheCheckedInRegistry() {
        var codes = Emit(Fixtures.Empty)["components"]!["schemas"]!["ErrorCode"]!["enum"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();

        // Read off ErrorCode.All rather than retyped, so a new error code reaches the published
        // contract without anybody editing the emitter — and shows up in the diff as an addition.
        codes.Count.ShouldBe(ErrorCode.All.Length);
        codes.ShouldContain("QuotaExceeded");
        codes.ShouldBe(codes.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void TheErrorBodyIsAzuresShape() {
        var schemas = Emit(Fixtures.Empty)["components"]!["schemas"]!;

        schemas["ErrorResponse"]!["properties"]!["error"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/components/schemas/Error");

        var error = schemas["Error"]!;
        var properties = error["properties"]!.AsObject();

        properties.ShouldContainKey("code");
        properties.ShouldContainKey("message");
        properties.ShouldContainKey("target");
        properties.ShouldContainKey("details");

        // details is recursive — docs/plan/08 § Errors reports every problem, not just the first.
        properties["details"]!["items"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/components/schemas/Error");
    }

    // ── Bodies, from the registry's schema ─────────────────────────────────────────────────────

    [Fact]
    public void TheFlatPointerListBecomesANestedSchema() {
        var body = Emit(Fixtures.Postgres())["components"]!["schemas"]!["CyberCloud.DBforPostgreSQL.servers"]!;

        var sku = body["properties"]!["properties"]!["properties"]!["sku"]!;
        sku["type"]!.GetValue<string>().ShouldBe("object");
        sku["properties"]!["name"]!["type"]!.GetValue<string>().ShouldBe("string");
        sku["properties"]!["vcpu"]!["type"]!.GetValue<string>().ShouldBe("integer");
        sku["required"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe(["name", "vcpu"]);
    }

    [Fact]
    public void ReadOnlyAndSecretBecomeReadOnlyAndWriteOnly() {
        var properties =
            Emit(Fixtures.Postgres())["components"]!["schemas"]!["CyberCloud.DBforPostgreSQL.servers"]!
                ["properties"]!["properties"]!["properties"]!;

        // The server owns it, so it never appears in a request.
        properties["provisioningState"]!["readOnly"]!.GetValue<bool>().ShouldBeTrue();

        // ⚠ writeOnly is what a Secret property is meant to be, not what the platform enforces — no
        // runtime read strips one, see SchemaProperty's remarks. This asserts the emitter, not the
        // guarantee. format=password is what a portal form masks on.
        properties["adminPassword"]!["writeOnly"]!.GetValue<bool>().ShouldBeTrue();
        properties["adminPassword"]!["format"]!.GetValue<string>().ShouldBe("password");
        properties["adminPassword"]!["x-cybercloud-secret"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void RejectingUnknownPropertiesBecomesAdditionalPropertiesFalse() {
        var body = Emit(Fixtures.Postgres())["components"]!["schemas"]!["CyberCloud.DBforPostgreSQL.servers"]!;

        body["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        body["properties"]!["properties"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void ATypeThatCarriesAFreeFormBagSaysSoOutLoud() {
        // Stated rather than omitted: true → false is a narrowing, and the compatibility diff can
        // only see that if the earlier document said `true`.
        var loose = Fixtures.PostgresWith(Fixtures.ServerSchema() with { RejectsUnknownProperties = false });

        Emit(loose)["components"]!["schemas"]!["CyberCloud.DBforPostgreSQL.servers"]!
            ["additionalProperties"]!
            .GetValue<bool>()
            .ShouldBeTrue();
    }

    [Fact]
    public void RegistryFactsWithNoBodyRepresentationBecomeExtensions() {
        // SupportsTags, RequiresCluster and SoftDeleteDays are registry facts that no request body
        // expresses. They are extensions rather than properties because none of them is a field a
        // caller sends — and they are here at all because the CLI and portal-form emitters need them.
        var item = Emit(Fixtures.Postgres())["paths"]![ServerPath]!;

        item["x-cybercloud-supports-tags"]!.GetValue<bool>().ShouldBeTrue();
        item["x-cybercloud-requires-cluster"]!.GetValue<bool>().ShouldBeTrue();
        item["x-cybercloud-soft-delete-days"]!.GetValue<int>().ShouldBe(7);
        item["x-cybercloud-resource-type"]!.GetValue<string>()
            .ShouldBe(Fixtures.Namespace + "/servers");
    }

    [Fact]
    public void TwoProvidersWithTheSameTypeNameDoNotCollide() {
        // ⚠ docs/plan/03 § Providers plans CyberCloud.DBforPostgreSQL and CyberCloud.DBforMySQL, and
        // both will have a type called `servers`. This document is one per api-version across every
        // provider, so an operationId of `servers_CreateOrUpdate` would be two operations with one
        // name — which is an invalid document and an SDK with one method where it needs two.
        var registry = new FakeRegistry {
            Namespaces = ["CyberCloud.DBforPostgreSQL", "CyberCloud.DBforMySQL"],
            Types = [
                new ResourceTypeRegistration {
                    Type = new("CyberCloud.DBforPostgreSQL", "servers"),
                    ApiVersions = [new(ApiVersion.Parse(Fixtures.FirstVersion), Fixtures.DatabaseSchema())]
                },
                new ResourceTypeRegistration {
                    Type = new("CyberCloud.DBforMySQL", "servers"),
                    ApiVersions = [new(ApiVersion.Parse(Fixtures.FirstVersion), Fixtures.DatabaseSchema())]
                }
            ]
        };

        var document = Emit(registry);

        OpenApiStructure.Validate(document).ShouldBeEmpty();

        document["paths"]!.AsObject().Count.ShouldBe(3);
        document["components"]!["schemas"]!.AsObject().ShouldContainKey("CyberCloud.DBforMySQL.servers");
        document["components"]!["schemas"]!.AsObject().ShouldContainKey("CyberCloud.DBforPostgreSQL.servers");
    }

    [Fact]
    public void QuotaMetersReachTheDocumentBecauseTheRegistryDeclaresThem() {
        // A caller about to be told QuotaExceeded can see which limit before sending, and the amount
        // is a JSON Pointer rather than a delegate precisely so it can be generated from.
        var meters = Emit(Fixtures.Postgres())["paths"]![ServerPath]!["x-cybercloud-meters"]!.AsArray();

        meters.Count.ShouldBe(2);
        meters[0]!["meter"]!.GetValue<string>().ShouldBe("StorageGb");
        meters[0]!["amountPointer"]!.GetValue<string>().ShouldBe("/properties/storageGb");
        meters[1]!["meter"]!.GetValue<string>().ShouldBe("Vcpu");
        meters[1]!["fallback"]!.GetValue<decimal>().ShouldBe(1m);
    }

    [Fact]
    public void AnApiVersionUnderNoticeIsMarkedDeprecated() {
        // ⚠ RetiredOn is the half of docs/plan/08 § The provider registry's 12-month notice window
        // the registry does carry. Without this, a version three months from being switched off is a
        // document indistinguishable from a live one.
        var retiring = Fixtures.RetiringPostgres(new DateOnly(2027, 8, 1));
        var item = Emit(retiring)["paths"]![ServerPath]!;

        item["get"]!["deprecated"]!.GetValue<bool>().ShouldBeTrue();
        item["put"]!["deprecated"]!.GetValue<bool>().ShouldBeTrue();
        item["x-cybercloud-retires-on"]!.GetValue<string>().ShouldBe("2027-08-01");

        // And a version that is not under notice says nothing, rather than saying `deprecated: false`
        // — a keyword that is absent is a keyword nobody has to diff.
        Emit(Fixtures.Postgres())["paths"]![ServerPath]!["get"]!["deprecated"].ShouldBeNull();
    }

    // ── Schemas the emitter refuses rather than emitting ───────────────────────────────────────

    [Fact]
    public void AnOrphanPropertyIsRefused() {
        // /properties/sku/name declared and /properties/sku not: ResourceSchema.Validate refuses the
        // undeclared parent, so every request fails whatever it sends. It fails here instead.
        var broken = Fixtures.PostgresWith(
            ResourceSchema.Of([new("/properties/sku/name", SchemaKind.Text)])
        );

        Should.Throw<InvalidOperationException>(() => Emit(broken))
            .Message.ShouldContain("but not its parent");
    }

    [Fact]
    public void APropertyInsideANonObjectIsRefused() {
        var broken = Fixtures.PostgresWith(
            ResourceSchema.Of([
                new("/properties", SchemaKind.Text),
                new("/properties/sku", SchemaKind.Text)
            ])
        );

        Should.Throw<InvalidOperationException>(() => Emit(broken)).Message.ShouldContain("not an object");
    }

    [Fact]
    public void ARequiredReadOnlyPropertyIsRefused() {
        // Required means required on a PUT; read-only means refused on a PUT. Every PUT fails twice.
        var broken = Fixtures.PostgresWith(
            ResourceSchema.Of([new("/location", SchemaKind.Text, Required: true, ReadOnly: true)])
        );

        Should.Throw<InvalidOperationException>(() => Emit(broken))
            .Message.ShouldContain("both required and read-only");
    }

    [Fact]
    public void APropertyWithNoKindIsRefused() {
        var broken = Fixtures.PostgresWith(ResourceSchema.Of([new("/location", SchemaKind.Unknown)]));

        Should.Throw<InvalidOperationException>(() => Emit(broken)).Message.ShouldContain("SchemaKind.Unknown");
    }

    [Fact]
    public void EmittingWithoutAnApiVersionIsARejectedArgument() =>
        Should.Throw<ArgumentException>(() => OpenApiEmitter.Emit(Fixtures.Postgres(), default));
}
