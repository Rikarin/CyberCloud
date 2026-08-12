using CyberCloud.ResourceManager.Registry;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL.Tests;

/// <summary>
///     What only this provider can be wrong about in its declaration.
/// </summary>
/// <remarks>
///     The lifecycle — create, poll, read back, tag, lock, delete, drift, the parent ReBAC edge — is
///     <c>CyberCloud.Providers.DBforPostgreSQL.Conformance</c>'s, because those are the <i>shared</i>
///     suite's assertions and a per-provider copy is the drift docs/plan/03 § Providers warns about.
/// </remarks>
public sealed class PostgresDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheWayASiloBuildsOneAtStart() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a type with no api-version, on a reconciler naming a type nobody declared, and — since
        // ProviderBuilder.CheckClusterPlacement — on a RequiresCluster type whose schema does not
        // declare a required string at the cluster-id pointer. Running it is how a declaration mistake
        // becomes a startup failure rather than a 404 with nothing in the log.
        var registry = ProviderRegistry.Build([new PostgresProvider()]);

        registry.Namespaces.ShouldContain(PostgresServers.ProviderNamespace);
        registry.TryGetType(PostgresServers.Type, out var registration).ShouldBeTrue();

        registration.ReconcilerType.ShouldBe(typeof(PostgresServerReconciler));
        registration.RequiresCluster.ShouldBeTrue();
        registration.SupportsTags.ShouldBeTrue();
        registration.ApiVersions.Length.ShouldBe(1);
        registration.Chart.ShouldBe(PostgresServers.ChartName);
        registration.TryGetAction(PostgresServers.ListKeysAction, out var listKeys).ShouldBeTrue();

        // ⚠ A key export is not a read. ResourceManagerService passes an action's `secret` flag into
        // the authorization call precisely so docs/plan/07 § Consistency's fully-consistent path is
        // taken for it, and an action that declared `secret: false` would get the cached one.
        listKeys.Secret.ShouldBeTrue();
        listKeys.Permission.ShouldNotBe(registration.ReadPermission, "every viewer would hold the password");
    }

    [Fact]
    public void TheProviderNamespaceKeepsItsExactCasingThroughTheRegistryAndAResourcePath() {
        // ⚠ ONE CHARACTER OF CASING. A `resourcegroup`/`resourceGroup` mismatch was once failing every
        // create in the platform, and surfaced as a 404 whose reason was in a log line. This provider
        // namespace has three internal case changes — DBforPostgreSQL — so it is the worst case the
        // catalogue has: `dbforpostgresql`, `DbForPostgreSql` and `DBForPostgreSQL` are all plausible
        // typos and all of them route to nothing.
        var registry = ProviderRegistry.Build([new PostgresProvider()]);

        registry.Namespaces.ShouldContain("CyberCloud.DBforPostgreSQL");
        PostgresServers.Type.ToString().ShouldBe("CyberCloud.DBforPostgreSQL/servers");

        var address = Address("casing");
        ResourceId.TryParsePath(address.Path, out var parsed).ShouldBeTrue(address.Path);

        parsed.Type.Namespace.ShouldBe("CyberCloud.DBforPostgreSQL");
        parsed.Type.Type.ShouldBe("servers");
        registry.TryGetType(parsed.Type, out _).ShouldBeTrue(
            "a path that round-tripped through the gateway's own parser no longer finds the type"
        );
    }

    [Fact]
    public void TheResourceTypeLabelIsTheValueTheConformanceManifestDeclares() {
        // ⚠ charts/managed/postgres/conformance.yaml pins this literal:
        //   cybercloud.io/resource-type: cybercloud.dbforpostgresql_servers
        // A `/` is not a legal label VALUE character, so the value is lower-cased with `/` replaced by
        // `_` — and nothing in the build compares the manifest's literal to what KubeLabels derives.
        // If the two disagree, every object this provider applies is labelled with something the
        // conformance suite will not find, which breaks orphan detection and billing attribution
        // rather than failing anything.
        KubeLabels.ResourceTypeValue(PostgresServers.Type).ShouldBe("cybercloud.dbforpostgresql_servers");
    }

    [Fact]
    public void TheFieldManagerIsTheOneAdr013DerivesFromTheNamespace() {
        // ADR-013 wants a stable field manager per provider, and the builder derives
        // cybercloud/{namespace, lower-cased}. The constant in .Contracts documents it; this asserts
        // the derivation still produces it, so a namespace rename cannot silently change the manager
        // and hand every field we own to a "new" manager on the next apply.
        using var desired = JsonDocument.Parse(PostgresServers.Body(Guid.NewGuid()));

        var command = KubeCommand.For(new RecordingConnection())
            .WithTenantId(Guid.NewGuid())
            .WithResourceId(Address("field-manager"))
            .InNamespace("ns")
            .WithKind(PostgresServers.ClusterKind)
            .ObjectJson(PostgresServers.ClusterJson("field-manager", desired.RootElement))
            .Build();

        command.FieldManager.ShouldBe(PostgresServers.FieldManager);
    }

    /// <remarks>
    ///     ⚠ Each body is valid in every respect but the one under test. Every property carries a
    ///     format or a pattern now, so a fixture with a placeholder cluster id would fail on that
    ///     first and the assertion would pass for the wrong reason — <see cref="Error.Target" /> is
    ///     the FIRST problem found.
    /// </remarks>
    [Theory]
    [InlineData("""{"properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","version":"17","replicas":2,"storage":{"size":"20Gi"}}}""", "/location")]
    [InlineData("""{"location":"eu-central","properties":{"version":"17","replicas":2,"storage":{"size":"20Gi"}}}""", "/properties/clusterId")]
    [InlineData("""{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","replicas":2,"storage":{"size":"20Gi"}}}""", "/properties/version")]
    [InlineData("""{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","version":"17","storage":{"size":"20Gi"}}}""", "/properties/replicas")]
    [InlineData("""{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","version":"17","replicas":2}}""", "/properties/storage/size")]
    public void EveryRequiredPropertyIsActuallyRequired(string body, string expectedTarget) {
        using var document = JsonDocument.Parse(body);

        var validated = PostgresServers.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Target.ShouldBe(expectedTarget);
    }

    /// <summary>
    ///     The constraints the chart's annotation vocabulary cannot spell, enforced where they are
    ///     spelled.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>These are the ones worth a test rather than a declaration, because the alternative to
    ///     each is a body that validates and then produces a CR the API server refuses AFTER the
    ///     caller was told 202.</b> A tenant reads that as "the platform accepted my request and lost
    ///     it", and the reason is in an operator's event stream rather than in the operation's error.
    /// </remarks>
    [Theory]
    [InlineData("/properties/storage/size", "\"20 gigabytes\"")]
    [InlineData("/properties/storage/size", "\"-4Gi\"")]
    [InlineData("/properties/sizing/cpu", "\"half a core\"")]
    [InlineData("/properties/sizing/memory", "\"4 GB\"")]
    [InlineData("/properties/backup/destinationPath", "\"gs://tenant-bucket/postgres\"")]
    [InlineData("/properties/bootstrap/database", "\"Application\"")]
    [InlineData("/properties/bootstrap/owner", "\"app; DROP DATABASE app\"")]
    public void AValueTheChartCouldNotHaveRefusedIsRefusedHere(string jsonPointer, string literal) {
        using var document = JsonDocument.Parse(BodyWith(jsonPointer, literal));

        var validated = PostgresServers.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue($"'{jsonPointer}' accepted {literal}");
        validated.Error!.Target.ShouldBe(jsonPointer);
    }

    [Theory]
    [InlineData("/properties/storage/size", "\"20Gi\"")]
    [InlineData("/properties/sizing/cpu", "\"500m\"")]
    [InlineData("/properties/sizing/cpu", "\"2\"")]
    [InlineData("/properties/sizing/cpu", "\"\"")]
    [InlineData("/properties/sizing/memory", "\"4Gi\"")]
    [InlineData("/properties/storage/walSize", "\"\"")]
    [InlineData("/properties/backup/destinationPath", "\"s3://tenant-bucket/postgres\"")]
    [InlineData("/properties/backup/destinationPath", "\"\"")]
    [InlineData("/properties/bootstrap/database", "\"app_v2\"")]
    public void TheValuesTheChartWritesAsExamplesAreAccepted(string jsonPointer, string literal) {
        // ⚠ The other half, and the half that catches an over-tight pattern. Every literal here is one
        // charts/managed/postgres/values.yaml either defaults to or names in a description, so a
        // pattern that refused one would be a pattern refusing the chart's own documented values.
        using var document = JsonDocument.Parse(BodyWith(jsonPointer, literal));

        PostgresServers.Schema2026.Validate(document.RootElement).IsSuccess.ShouldBeTrue(
            $"'{jsonPointer}' refused {literal}"
        );
    }

    [Fact]
    public void EveryDeclaredDefaultSatisfiesItsOwnPropertyAndTheWholeBodyOfDefaultsValidates() {
        // SchemaProperty checks each default against its own constraints at construction — so the
        // schema loading at all is half of this. The other half is that the defaults are jointly
        // valid, which nothing checks: a portal form pre-filled with them must be submittable.
        var body = new JsonObject {
            ["location"] = "eu-central",
            ["properties"] = new JsonObject { ["clusterId"] = Guid.NewGuid().ToString("D") }
        };

        foreach (var property in PostgresServers.Schema2026.Properties) {
            if (property.DefaultJson.Length == 0) {
                continue;
            }

            Place(body, property.JsonPointer, JsonNode.Parse(property.DefaultJson));
        }

        using var document = JsonDocument.Parse(body.ToJsonString());
        var validated = PostgresServers.Schema2026.Validate(document.RootElement);

        validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
    }

    internal static ResourceId Address(string name) =>
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            PostgresServers.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    /// <summary>A valid body with one pointer set to a JSON literal.</summary>
    /// <remarks>
    ///     ⚠ Returns text rather than a <see cref="JsonElement" />. An element outlives its
    ///     <see cref="JsonDocument" /> only as long as the document is undisposed, and a helper that
    ///     handed one back would either leak the document or hand back a reader over pooled memory
    ///     somebody else owns.
    /// </remarks>
    static string BodyWith(string pointer, string literal) {
        var body = JsonNode.Parse(
            PostgresServers.Body(Guid.Parse("7b6a5c4d-0000-4000-8000-000000000001"))
        )!.AsObject();

        Place(body, pointer, JsonNode.Parse(literal));
        return body.ToJsonString();
    }

    static void Place(JsonObject root, string pointer, JsonNode? value) {
        var tokens = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (var i = 0; i < tokens.Length - 1; i++) {
            if (current[tokens[i]] is JsonObject existing) {
                current = existing;
                continue;
            }

            var created = new JsonObject();
            current[tokens[i]] = created;
            current = created;
        }

        current[tokens[^1]] = value;
    }
}
