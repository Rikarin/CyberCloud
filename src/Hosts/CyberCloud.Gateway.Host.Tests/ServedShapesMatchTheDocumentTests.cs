using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Contracts.Registry;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     Every body the gateway serves validates against the response schema the published document
///     declares for it — issue #85.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the check nothing made before.</b> <c>ResourceBodyShapeTests</c> pins the
///         served shape against the code that writes it; <c>OpenApiEmitterTests</c> pins the
///         document against the registry; the compatibility gate diffs the document against its
///         own predecessor. None of the three asked whether the document describes the wire, and
///         the document did not: every <c>GET</c> <c>200</c> referenced the write body,
///         <c>additionalProperties: false</c> over three members, and the gateway served eight. A
///         client that validated a response against the document rejected every resource it read
///         and every operation it polled.
///     </para>
///     <para>
///         The document is emitted here from the same <see cref="OneTypeRegistry" /> the harness
///         routes with, so the schema and the served body come from one type declaration. The
///         validator is the four keywords that decide the question — <c>properties</c>,
///         <c>additionalProperties</c>, <c>required</c>, <c>enum</c>, with <c>allOf</c> and
///         <c>$ref</c> followed — rather than a JSON Schema library, because the failure this test
///         exists to refuse is "a name the schema does not permit", and a validator that could be
///         configured to ignore that would be a test that could be configured to pass.
///     </para>
/// </remarks>
public sealed class ServedShapesMatchTheDocumentTests {
    const string ResourceTemplate =
        "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
        + "/providers/CyberCloud.DBforPostgreSQL/servers/{resourceName}";

    const string CollectionTemplate =
        "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
        + "/providers/CyberCloud.DBforPostgreSQL/servers";

    static readonly JsonObject Document =
        OpenApiEmitter.Emit(new OneTypeRegistry(), ApiVersion.Parse(OneTypeRegistry.TheVersion));

    static string CollectionPath(Guid tenantId) =>
        $"/tenants/{tenantId:D}/subscriptions/{GatewayHarness.Subscription:D}/resourceGroups/prod"
        + "/providers/CyberCloud.DBforPostgreSQL/servers";

    [Fact]
    public async Task AReadValidatesAgainstTheGet200() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        Conforms(response.Body, ResponseSchema(ResourceTemplate, "get", "200"));
    }

    [Fact]
    public async Task AListValidatesAgainstTheCollection200AndItsElementAgainstTheResource() {
        var gateway = new GatewayHarness();
        var path = GatewayHarness.ResourcePath(GatewayHarness.TenantA);

        gateway.Manager.OnList = _ => Result<ResourceListPage>.Success(
            new() { Resources = [ProjectedSnapshot.Of(path)], Continuation = "page-2" }
        );

        var response = await gateway.SendAsync(
            "GET",
            CollectionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        Conforms(response.Body, ResponseSchema(CollectionTemplate, "get", "200"));

        // The element is validated by the page's `items`; this asserts the page had one to validate.
        using var page = JsonDocument.Parse(response.Body);
        page.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
    }

    /// <summary>
    ///     ⚠ The state in each row is the one the real manager reports for that verb —
    ///     <c>ResourceGrain.BeginDeleteAsync</c> sets <c>Deleting</c> before it snapshots — and the
    ///     substitute is told to report it, so that the assertion is on a state the document's enum
    ///     has to admit rather than on the substitute's default. Until the 2026-09-15 review of
    ///     issue #85 all three rows asserted <c>Creating</c>, which was the double's, and a document
    ///     whose enum lacked <c>Deleting</c> would have passed.
    /// </summary>
    [Theory]
    [InlineData("PUT", "put", ProvisioningState.Creating)]
    [InlineData("PATCH", "patch", ProvisioningState.Updating)]
    [InlineData("DELETE", "delete", ProvisioningState.Deleting)]
    public async Task AWriteValidatesAgainstThe202(string method, string operation, ProvisioningState state) {
        var gateway = new GatewayHarness();

        gateway.Manager.OnWrite = request => Result<WriteAccepted>.Success(
            new() {
                OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                RetryAfterSeconds = 10,
                Resource = ProjectedSnapshot.Of(request.Path, provisioningState: state)
            }
        );

        var response = await gateway.SendAsync(
            method,
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: method == "DELETE" ? "" : """{"location":"eu-central","properties":{"sku":"gp1"}}"""
        );

        response.Status.ShouldBe(StatusCodes.Status202Accepted);

        // The 202 carries the resource as the write left it, and the document says so — it declared
        // no content at all until issue #85.
        Conforms(response.Body, ResponseSchema(ResourceTemplate, operation, "202"));
        response.Body.ShouldContain($"\"provisioningState\":\"{state}\"");
    }

    [Fact]
    public async Task ARunningOperationValidatesAgainstTheOperation200() {
        var gateway = new GatewayHarness();
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        gateway.Operations.OnRead = operationId => Result<OperationStatus>.Success(
            new() {
                OperationId = operationId,
                State = OperationState.Running,
                PercentComplete = 40,
                ResourcePath = GatewayHarness.ResourcePath(GatewayHarness.TenantA),
                StartedAt = gateway.Clock.UtcNow,
                Progress = [
                    new() { At = gateway.Clock.UtcNow, Step = "etcd", Detail = "etcd cluster ready", PercentComplete = 40 }
                ]
            }
        );

        var response = await gateway.SendAsync("GET", $"/operations/{id:D}", gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status200OK);
        Conforms(response.Body, ResponseSchema("/operations/{operationId}", "get", "200"));

        // id and startTime were served from the first day and declared by nobody.
        response.Body.ShouldContain("\"id\":\"" + id.ToString("D") + "\"");
        response.Body.ShouldContain("\"startTime\":");
    }

    [Fact]
    public async Task AFailedOperationValidatesAgainstTheOperation200() {
        var gateway = new GatewayHarness();
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        gateway.Operations.OnRead = operationId => Result<OperationStatus>.Success(
            new() {
                OperationId = operationId,
                State = OperationState.Failed,
                PercentComplete = 60,
                ResourcePath = GatewayHarness.ResourcePath(GatewayHarness.TenantA),
                StartedAt = gateway.Clock.UtcNow.AddMinutes(-3),
                EndedAt = gateway.Clock.UtcNow,
                Error = new(ErrorCode.QuotaExceeded, "vCPU: requested 8, remaining 2.")
            }
        );

        var response = await gateway.SendAsync("GET", $"/operations/{id:D}", gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status200OK);
        Conforms(response.Body, ResponseSchema("/operations/{operationId}", "get", "200"));
        response.Body.ShouldContain("\"endTime\":");
    }

    [Fact]
    public async Task AScopeValidatesAgainstTheScope200() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        Conforms(response.Body, ResponseSchema(OpenApiEmitter.ResourceGroupPathTemplate, "get", "200"));
    }

    /// <summary>
    ///     ⚠ Both scope collections validate against the one <c>Scope.List</c> page, and each
    ///     element against the one <c>Scope</c> — the same schema a by-id read validates against,
    ///     which is what makes "an element is what a GET renders" a documented promise.
    /// </summary>
    [Theory]
    [InlineData(OpenApiEmitter.SubscriptionCollectionPathTemplate, ScopeKind.Subscription)]
    [InlineData(OpenApiEmitter.ResourceGroupCollectionPathTemplate, ScopeKind.ResourceGroup)]
    public async Task AScopeCollectionValidatesAgainstTheScopeList200(string template, ScopeKind kind) {
        var gateway = new GatewayHarness();

        var path = template
            .Replace("{tenantId}", GatewayHarness.TenantA.ToString("D"), StringComparison.Ordinal)
            .Replace("{subscriptionId}", GatewayHarness.Subscription.ToString("D"), StringComparison.Ordinal);

        gateway.Scopes.OnList = request => Result<ScopeListPage>.Success(
            new() {
                Items = [
                    new() {
                        Path = kind == ScopeKind.Subscription
                            ? GatewayHarness.SubscriptionPath(GatewayHarness.TenantA)
                            : GatewayHarness.GroupPath(GatewayHarness.TenantA),
                        Kind = kind,
                        Name = kind == ScopeKind.Subscription ? "Default" : "prod",
                        Type = ScopeTypeNames.Of(kind),
                        Location = kind == ScopeKind.Subscription ? "" : "eu-central"
                    }
                ],
                Continuation = "page-2"
            }
        );

        var response = await gateway.SendAsync("GET", path, gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        Conforms(response.Body, ResponseSchema(template, "get", "200"));

        // The element is validated by the page's `items`; this asserts the page had one to validate,
        // and that the link the client follows was there to be followed.
        using var page = JsonDocument.Parse(response.Body);
        page.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
        page.RootElement.GetProperty("nextLink").GetString().ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    ///     ⚠ The validator refuses what it is meant to refuse. A body carrying a member no schema
    ///     names is the exact defect of issue #85, and a check that passed it would be this whole
    ///     class printing ticks over nothing.
    /// </summary>
    [Fact]
    public void TheValidatorRefusesAMemberTheSchemaDoesNotName() {
        var schema = ResponseSchema(ResourceTemplate, "get", "200");
        var served = ResponseBodies.Resource(ProjectedSnapshot.Of(GatewayHarness.ResourcePath(GatewayHarness.TenantA)));

        var forged = served[..^1] + ",\"lastModifiedBy\":\"nobody\"}";

        Should.Throw<ShouldAssertException>(() => Conforms(forged, schema))
            .Message.ShouldContain("lastModifiedBy");
    }

    // ── The validator ──────────────────────────────────────────────────────────────────────────

    static JsonNode ResponseSchema(string template, string operation, string status) =>
        Document["paths"]![template]![operation]!["responses"]![status]!["content"]!["application/json"]!["schema"]!;

    static void Conforms(string body, JsonNode schema) {
        using var document = JsonDocument.Parse(body);
        var problems = new List<string>();

        Check(schema, document.RootElement, "", problems);

        problems.ShouldBeEmpty(
            "the served body does not validate against the document's schema: " + string.Join("; ", problems)
        );
    }

    static void Check(JsonNode? node, JsonElement instance, string where, List<string> problems) {
        if (Resolve(node) is not JsonObject schema) {
            return;
        }

        // The members a schema admits: its own, and those of everything it allOfs. The keyword
        // additionalProperties sees only the former, which is why the emitter repeats the envelope
        // beside the body — and why this walk collects both, so a schema that only allOf'd would be
        // caught by the additionalProperties check below rather than excused by it.
        var declared = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var required = new List<string>();
        Collect(schema, declared, required);

        var own = schema["properties"] as JsonObject;
        var closed = schema["additionalProperties"] is JsonValue value
            && value.TryGetValue<bool>(out var allowed)
            && !allowed;

        switch (instance.ValueKind) {
            case JsonValueKind.Object:
                foreach (var member in instance.EnumerateObject()) {
                    var pointer = where + "/" + member.Name;

                    if (closed && (own is null || !own.ContainsKey(member.Name))) {
                        problems.Add($"{pointer} is served and the schema, which says additionalProperties: false, does not name it");
                        continue;
                    }

                    if (declared.TryGetValue(member.Name, out var child)) {
                        Check(child, member.Value, pointer, problems);
                    }
                }

                foreach (var name in required) {
                    if (!instance.TryGetProperty(name, out _)) {
                        problems.Add($"{where}/{name} is required by the schema and not served");
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var element in instance.EnumerateArray()) {
                    Check(schema["items"], element, where + "/" + index++, problems);
                }

                break;

            case JsonValueKind.String:
                if (schema["enum"] is JsonArray values
                    && !values.Any(x => string.Equals(DocumentReader.Text(x), instance.GetString(), StringComparison.Ordinal))) {
                    problems.Add($"{where} is \"{instance.GetString()}\", which the schema's enum does not list");
                }

                break;
        }

        var expected = DocumentReader.TypeOf(schema);

        if (expected.Length > 0 && !Matches(expected, instance.ValueKind)) {
            problems.Add($"{where} is a JSON {instance.ValueKind} and the schema says {expected}");
        }
    }

    static void Collect(JsonObject schema, Dictionary<string, JsonNode?> declared, List<string> required) {
        if (schema["properties"] is JsonObject properties) {
            foreach (var member in properties) {
                declared[member.Key] = member.Value;
            }
        }

        if (schema["required"] is JsonArray names) {
            required.AddRange(names.Select(DocumentReader.Text).Where(x => x.Length > 0));
        }

        if (schema["allOf"] is JsonArray inherits) {
            foreach (var entry in inherits) {
                if (Resolve(entry) is { } inherited) {
                    Collect(inherited, declared, required);
                }
            }
        }
    }

    static JsonObject? Resolve(JsonNode? node) {
        if (node is not JsonObject schema) {
            return null;
        }

        const string Prefix = "#/components/schemas/";
        var reference = DocumentReader.Text(schema["$ref"]);

        return reference.StartsWith(Prefix, StringComparison.Ordinal)
            ? Document["components"]?["schemas"]?[reference[Prefix.Length..]] as JsonObject
            : schema;
    }

    static bool Matches(string type, JsonValueKind kind) =>
        type switch {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "integer" or "number" => kind == JsonValueKind.Number,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            _ => true
        };
}
