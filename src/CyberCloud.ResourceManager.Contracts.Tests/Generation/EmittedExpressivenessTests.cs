using CyberCloud.ResourceManager.Contracts.Generation;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     The other half of <c>SchemaExpressivenessTests</c>: every fact the registry can now express
///     must <i>reach the emitted document</i>.
/// </summary>
/// <remarks>
///     ⚠ <b>A registry field nothing reads is not a closed gap.</b> These assertions are what stop a
///     member being added to <see cref="SchemaProperty" />, enforced by the validator, and then
///     silently dropped by the emitter — which would leave the generated CLI, SDK and portal form
///     unable to see it, since docs/plan/21 § Generation makes them read this document rather than
///     the registry.
/// </remarks>
public sealed class EmittedExpressivenessTests {
    static JsonNode Document => OpenApiEmitter.Emit(Fixtures.Postgres(), ApiVersion.Parse(Fixtures.FirstVersion));

    static JsonNode Server => Document["components"]!["schemas"]!["CyberCloud.DBforPostgreSQL.servers"]!;

    static JsonNode Property(string name) => Server["properties"]!["properties"]!["properties"]![name]!;

    static JsonNode PathItem =>
        Document["paths"]![
            "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/{resourceName}"
        ]!;

    // ── 1. Enumerations ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AClosedSetBecomesAnEnum() {
        var values = Server["properties"]!["properties"]!["properties"]!["sku"]!["properties"]!["name"]!["enum"]!
            .AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();

        values.ShouldBe(["s1.small", "s1.large", "c1.large", "m1.large"]);
    }

    [Fact]
    public void AnEnumKeepsDeclarationOrderRatherThanBeingSorted() =>
        // `enum` is a set to the compatibility diff, so order carries no contract — but it is the
        // order a generated CLI's completion and a portal's select will show, and sorting it would
        // move "c1.large" above "s1.large" in every dropdown for no reason.
        Server["properties"]!["properties"]!["properties"]!["sku"]!["properties"]!["name"]!["enum"]!
            .AsArray()[0]!
            .GetValue<string>()
            .ShouldBe("s1.small");

    // ── 2. Array element shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AnArrayCarriesItsElementType() =>
        // ⚠ `items: {}` used to be here, which an SDK generator renders as object[].
        Property("allowedRanges")["items"]!["type"]!.GetValue<string>().ShouldBe("string");

    [Fact]
    public void AnArraysConstraintsLandOnTheElementRatherThanOnTheArray() {
        Property("allowedRanges")["items"]!["pattern"].ShouldNotBeNull();
        Property("allowedRanges")["pattern"].ShouldBeNull();
    }

    // ── 3. Nullability ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANullablePropertyBecomesATypeUnion() {
        // OpenAPI 3.1 is JSON Schema 2020-12: nullability is a union, not 3.0's `nullable: true`.
        var types = Property("retiredOn")["type"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();

        types.ShouldBe(["string", "null"]);
    }

    [Fact]
    public void ANonNullablePropertyKeepsAScalarType() =>
        Property("storageGb")["type"]!.GetValue<string>().ShouldBe("integer");

    // ── 4. Formats ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFormatReachesTheDocument() =>
        Property("retiredOn")["format"]!.GetValue<string>().ShouldBe("date-time");

    [Fact]
    public void APlatformFormatIsPrefixedSoAGenericToolDoesNotGuessAtIt() =>
        Server["properties"]!["location"]!["format"]!.GetValue<string>().ShouldBe("cybercloud-region");

    // ── 5. Bounds ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NumericBoundsReachTheDocument() {
        Property("storageGb")["minimum"]!.GetValue<double>().ShouldBe(32);
        Property("storageGb")["maximum"]!.GetValue<double>().ShouldBe(16384);
    }

    [Fact]
    public void AStringLengthReachesTheDocument() =>
        Property("adminPassword")["minLength"]!.GetValue<int>().ShouldBe(12);

    [Fact]
    public void APatternIsPublishedAnchoredBecauseItIsAppliedAnchored() =>
        // ⚠ Emitting the bare pattern would publish a looser rule than the API applies: JSON Schema's
        // `pattern` is a search and ours is a whole-value match.
        Property("allowedRanges")["items"]!["pattern"]!.GetValue<string>()
            .ShouldStartWith("^(?:");

    // ── 6. Action schemas ──────────────────────────────────────────────────────────────────────

    static JsonNode Action(string name) =>
        Document["paths"]![
            "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/{resourceName}/" + name
        ]!;

    [Fact]
    public void AnActionsRequestBecomesARequestBodyReferringToItsOwnComponent() =>
        Action("listKeys")["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!
            .GetValue<string>()
            .ShouldBe("#/components/schemas/CyberCloud.DBforPostgreSQL.servers.ListKeysRequest");

    [Fact]
    public void AnActionsResponseIsTypedRatherThanAnEmptySchema() =>
        // ⚠ THE listKeys CASE. "Secrets of unknown shape" is now a component with two members, so a
        // reviewer can see which values leave the platform without reading the handler.
        Action("listKeys")["post"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!
            .GetValue<string>()
            .ShouldBe("#/components/schemas/CyberCloud.DBforPostgreSQL.servers.ListKeysResponse");

    [Fact]
    public void ASecretActionsResponseMembersAreMarkedSecret() {
        var response = Document["components"]!["schemas"]!
            ["CyberCloud.DBforPostgreSQL.servers.ListKeysResponse"]!;

        response["properties"]!["primary"]!["x-cybercloud-secret"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void ALongRunningActionAnswers202AndNot200() {
        // There is one long-running shape in this platform, and an action that does work is not a
        // second one. Guessing this wrong is a generated client that returns before the work is done.
        var restart = Action("restart")["post"]!["responses"]!;

        restart["202"].ShouldNotBeNull();
        restart["200"].ShouldBeNull();
        Action("restart")["post"]!["x-cybercloud-long-running"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void AnActionThatDeclaresNoResponseStillSaysSoOutLoud() {
        var registry = Fixtures.PostgresWithActions([new("noop", ActionKind.Post, "write", Secret: false)]);
        var document = OpenApiEmitter.Emit(registry, ApiVersion.Parse(Fixtures.FirstVersion));

        var description = document["paths"]![
                "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
                + "/providers/CyberCloud.DBforPostgreSQL/servers/{resourceName}/noop"
            ]!["post"]!["responses"]!["200"]!["description"]!
            .GetValue<string>();

        // Reported rather than invented: the registry can now say, and this action has not.
        description.ShouldContain("Unconstrained");
    }

    // ── 7. Display metadata ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATypeCarriesTheNameACliVerbTreeAndAPortalBreadcrumbNeed() {
        var display = PathItem["x-cybercloud-display"]!;

        display["name"]!.GetValue<string>().ShouldBe("PostgreSQL server");
        display["plural"]!.GetValue<string>().ShouldBe("PostgreSQL servers");
        display["alias"]!.GetValue<string>().ShouldBe("postgres");
        display["declared"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void AnUndeclaredDisplayFallsBackToTheTypeSegmentAndSaysItWasNotDeclared() {
        // ⚠ The fallback is here rather than in three downstream emitters, so the three cannot invent
        // three different names — and `declared: false` is greppable in the published document.
        var databases = Document["paths"]![
            "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/{serversName}/databases/{resourceName}"
        ]!["x-cybercloud-display"]!;

        databases["name"]!.GetValue<string>().ShouldBe("databases");
        databases["declared"]!.GetValue<bool>().ShouldBeFalse();
    }

    // ── 8. Widget hints ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeclaredWidgetBecomesTheHintAdr012Promised() {
        // docs/plan/02 § ADR-012: "x-cybercloud-* hints for widgets (a storageclass picker, a region
        // picker)". SchemaProperty had no slot for one; now the promise and the registry agree.
        Server["properties"]!["location"]!["x-cybercloud-widget"]!.GetValue<string>().ShouldBe("region");
        Property("allowedRanges")["x-cybercloud-widget"]!.GetValue<string>().ShouldBe("cidr");
        Property("adminPassword")["x-cybercloud-widget"]!.GetValue<string>().ShouldBe("secret-ref");
    }

    [Fact]
    public void ImmutabilityIsAHintAndNotAConstraint() =>
        Server["properties"]!["location"]!["x-cybercloud-immutable"]!.GetValue<bool>().ShouldBeTrue();

    // ── 9. Defaults and examples ───────────────────────────────────────────────────────────────

    [Fact]
    public void ADefaultReachesTheDocumentAsAJsonValueRatherThanAString() =>
        Property("storageGb")["default"]!.GetValue<int>().ShouldBe(32);

    [Fact]
    public void AnExampleReachesTheDocument() =>
        Server["properties"]!["properties"]!["properties"]!["sku"]!["properties"]!["name"]!["example"]!
            .GetValue<string>()
            .ShouldBe("s1.large");

    // ── 10. HTTP status on ErrorCode ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryStatusAnErrorCodeMapsOntoHasAResponse() {
        var responses = Document["components"]!["responses"]!.AsObject();

        foreach (var status in ErrorCode.HttpStatuses) {
            responses.ContainsKey(OpenApiEmitter.ResponseNameOfPublic(status)).ShouldBeTrue(
                $"HTTP {status} is an ErrorCode's status and has no response component."
            );
        }
    }

    [Fact]
    public void AResponseNamesTheCodesThatProduceIt() {
        var codes = Document["components"]!["responses"]!["TooManyRequests"]!["x-cybercloud-error-codes"]!
            .AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();

        codes.ShouldContain("QuotaExceeded");
    }

    [Fact]
    public void TheCodeToStatusMappingIsPublishedRatherThanKeptInTheEmitter() {
        var statuses = Document["components"]!["schemas"]!["ErrorCode"]!["x-cybercloud-http-status"]!;

        statuses["ResourceNotFound"]!.GetValue<int>().ShouldBe(404);
        statuses["QuotaExceeded"]!.GetValue<int>().ShouldBe(429);
        // ⚠ Both 404, and deliberately: docs/plan/00 § Non-negotiables forbids disclosing existence,
        // so a broken authorization schema and an invisible resource look the same to a caller.
        statuses["SchemaInvalid"]!.GetValue<int>().ShouldBe(404);
    }

    [Fact]
    public void EveryOperationDeclaresEveryErrorStatus() {
        var get = PathItem["get"]!["responses"]!.AsObject();

        foreach (var status in ErrorCode.HttpStatuses) {
            get.ContainsKey(status.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ShouldBeTrue($"HTTP {status} is missing from an operation's responses.");
        }

        get.ContainsKey("default").ShouldBeTrue();
    }

    // ── The tag bag ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATypeThatSupportsTagsDeclaresTheBagInItsBody() {
        // ⚠ THE PUBLISHED CONTRACT LIE, CLOSED. The write path accepted `tags` and the document said
        // additionalProperties: false and named no `tags` — a caller reading the contract would have
        // concluded tags were not supported.
        var tags = Server["properties"]!["tags"]!;

        tags["type"]!.GetValue<string>().ShouldBe("object");
        tags["additionalProperties"]!["type"]!.GetValue<string>().ShouldBe("string");
        tags["maxProperties"]!.GetValue<int>().ShouldBe(TagRules.MaxTags);
        tags["x-cybercloud-widget"]!.GetValue<string>().ShouldBe("tag-input");
    }

    [Fact]
    public void ATypeThatDoesNotSupportTagsDeclaresNoBag() =>
        // The other branch is real: IResourceTypeBuilder.SupportsTags' remarks require that a type
        // which does not declare it refuses a body with tags.
        Document["components"]!["schemas"]!["CyberCloud.DBforPostgreSQL.servers.databases"]!
            ["properties"]!["tags"]
            .ShouldBeNull();

    [Fact]
    public void TheTagBagIsOptionalRatherThanRequired() =>
        Server["required"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldNotContain("tags");

    // ── The cluster pointer ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATypeThatRequiresAClusterSaysWhichFieldCarriesTheId() =>
        // `requires-cluster: true` alone told a generated surface that a cluster was needed and not
        // which field carries it, so a CLI could not offer a --cluster flag.
        PathItem["x-cybercloud-cluster-id-pointer"]!.GetValue<string>()
            .ShouldBe(ClusterPlacement.DefaultPointer);

    // ── Soft delete ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The published window is one the platform delivers, and the disclaimer that said
    ///     otherwise is gone with the defect it described.</b>
    /// </summary>
    /// <remarks>
    ///     This test used to be called
    ///     <c>ASoftDeleteWindowReachesTheDeleteOperationAndSaysItIsNotHonouredYet</c> and asserted the
    ///     description contained <i>"not a window it currently honours"</i>. That was honest while
    ///     nothing in the resource manager read <c>SoftDeleteDays</c> — the platform was advertising a
    ///     recovery window it could not deliver, and naming it as a promise was the least bad thing the
    ///     emitter could do. docs/plan/08 § Soft delete is built now, so the document states the
    ///     behaviour; leaving the disclaimer would understate a guarantee callers can rely on, which is
    ///     the same drift with the sign flipped.
    /// </remarks>
    [Fact]
    public void ASoftDeleteWindowReachesTheDeleteOperationWithThePurgePermissionBesideIt() {
        var delete = PathItem["delete"]!;

        delete["x-cybercloud-soft-delete-days"]!.GetValue<int>().ShouldBe(7);

        // ⚠ The purge permission is published beside the delete permission, and they differ. A
        // generated SDK or a role designer reading this document has to be able to see that "may
        // delete" and "may destroy permanently" are two rights — docs/plan/08 § Soft delete.
        delete["x-cybercloud-purge-permission"]!.GetValue<string>().ShouldBe("purge");
        delete["x-cybercloud-purge-permission"]!.GetValue<string>()
            .ShouldNotBe(delete["x-cybercloud-permission"]!.GetValue<string>());

        delete["description"]!.GetValue<string>().ShouldNotContain("not a window it currently honours");
        delete["description"]!.GetValue<string>().ShouldContain("its old address answers 404");

        // And the pointer a portal needs in order to render the purge-protection toggle at all.
        PathItem["x-cybercloud-purge-protection-pointer"]!.GetValue<string>()
            .ShouldBe("/properties/enablePurgeProtection");
    }
}
