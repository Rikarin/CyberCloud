using CyberCloud.ResourceManager.Registry;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Tests;

/// <summary>
///     What only this provider can be wrong about in its declaration.
/// </summary>
/// <remarks>
///     The lifecycle — create, poll, read back, tag, lock, delete, drift, <b>and the parent ReBAC
///     edge</b> — is <c>CyberCloud.Providers.Cache.Conformance</c>'s, because those are the
///     <i>shared</i> suite's assertions and a per-provider copy is the drift docs/plan/03 § Providers
///     warns about. ⚠ The ReBAC one is worth naming rather than leaving to "the lifecycle": created
///     resources were once invisible to their creator because the <c>parent</c> edge was never
///     written, and the assertion that catches it is
///     <c>ProviderConformanceTests.ACreateWritesTheParentEdgeBeforeItWritesDurableState</c>, which
///     this provider inherits by declaring a <c>ProviderConformanceCase</c> and nothing else.
/// </remarks>
public sealed class ValkeyDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheWayASiloBuildsOneAtStart() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a type with no api-version, on a reconciler naming a type nobody declared, and — since
        // ProviderBuilder.CheckClusterPlacement — on a RequiresCluster type whose schema does not
        // declare a required string at the cluster-id pointer. Running it is how a declaration mistake
        // becomes a startup failure rather than a 404 with nothing in the log.
        var registry = ProviderRegistry.Build([new ValkeyCacheProvider()]);

        registry.Namespaces.ShouldContain(ValkeyCaches.ProviderNamespace);
        registry.TryGetType(ValkeyCaches.Type, out var registration).ShouldBeTrue();

        registration.ReconcilerType.ShouldBe(typeof(ValkeyCacheReconciler));
        registration.RequiresCluster.ShouldBeTrue();
        registration.SupportsTags.ShouldBeTrue();
        registration.ApiVersions.Length.ShouldBe(1);
        registration.Chart.ShouldBe(ValkeyCaches.ChartName);
        registration.TryGetAction(ValkeyCaches.ListKeysAction, out var listKeys).ShouldBeTrue();

        // ⚠ A key export is not a read. ResourceManagerService passes an action's `secret` flag into
        // the authorization call precisely so docs/plan/07 § Consistency's fully-consistent path is
        // taken for it, and an action that declared `secret: false` would get the cached one.
        listKeys.Secret.ShouldBeTrue();
        listKeys.Permission.ShouldNotBe(registration.ReadPermission, "every viewer would hold the password");
    }

    [Fact]
    public void TheProviderNamespaceAndTypeKeepTheirExactCasingThroughTheRegistryAndAResourcePath() {
        // ⚠ ONE CHARACTER OF CASING, PLACE ONE OF THREE. A `resourcegroup`/`resourceGroup` mismatch
        // was once failing every create in the platform, and surfaced as a 404 whose reason was in a
        // log line. This namespace is the mirror image of the first provider's worst case: where
        // `DBforPostgreSQL` is hard because it has three internal case changes, `Cache` is hard
        // because it has none and therefore looks like a word nobody would get wrong — and the TYPE
        // is `redis`, an all-lower-case word next to a product called Valkey, which is the exact
        // shape of a rename somebody performs helpfully.
        var registry = ProviderRegistry.Build([new ValkeyCacheProvider()]);

        registry.Namespaces.ShouldContain("CyberCloud.Cache");
        ValkeyCaches.Type.ToString().ShouldBe("CyberCloud.Cache/redis");

        var address = Address("casing");
        ResourceId.TryParsePath(address.Path, out var parsed).ShouldBeTrue(address.Path);

        parsed.Type.Namespace.ShouldBe("CyberCloud.Cache");
        parsed.Type.Type.ShouldBe("redis");
        registry.TryGetType(parsed.Type, out _).ShouldBeTrue(
            "a path that round-tripped through the gateway's own parser no longer finds the type"
        );
    }

    [Fact]
    public void TheResourceTypeLabelIsTheValueTheConformanceManifestDeclares() {
        // ⚠ charts/managed/valkey/conformance.yaml pins this literal:
        //   cybercloud.io/resource-type: cybercloud.cache_redis
        // A `/` is not a legal label VALUE character, so the value is lower-cased with `/` replaced by
        // `_` — and nothing in the build compares the manifest's literal to what KubeLabels derives.
        // If the two disagree, every object this provider applies is labelled with something the
        // conformance suite will not find, which breaks orphan detection and billing attribution
        // rather than failing anything.
        KubeLabels.ResourceTypeValue(ValkeyCaches.Type).ShouldBe("cybercloud.cache_redis");
    }

    [Fact]
    public void TheFieldManagerIsTheOneAdr013DerivesFromTheNamespace() {
        // ADR-013 wants a stable field manager per provider, and the builder derives
        // cybercloud/{namespace, lower-cased}. The constant in .Contracts documents it; this asserts
        // the derivation still produces it, so a namespace rename cannot silently change the manager
        // and hand every field we own to a "new" manager on the next apply.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(Guid.NewGuid()));

        var command = KubeCommand.For(new RecordingConnection())
            .WithTenantId(Guid.NewGuid())
            .WithResourceId(Address("field-manager"))
            .InNamespace("ns")
            .WithKind(ValkeyCaches.FailoverKind)
            .ObjectJson(ValkeyCaches.RedisFailoverJson("field-manager", desired.RootElement))
            .Build();

        command.FieldManager.ShouldBe(ValkeyCaches.FieldManager);
    }

    [Fact]
    public void TheModeAxisIsVisibleAndOffersOnlyWhatTheOperatorImplements() {
        // ⚠ THE ONE-MEMBER ENUM, ASSERTED SO IT CANNOT BE "TIDIED" INTO THREE. docs/plan/12 names
        // Standalone, Sentinel and Cluster and warns that "these are not interchangeable and the API
        // must not pretend they are". spotahome/redis-operator ships one CRD with no sharding, and its
        // validate.go replaces a sentinel replica count of <= 0 with 3 — so Cluster has nothing to
        // render and Standalone is not a state the CRD can express. Either extra member would be a
        // value the API accepts and the cluster ignores.
        var mode = ValkeyCaches.Schema2026.Properties.Single(x => x.JsonPointer == "/properties/mode");

        mode.AllowedValues.ShouldBe(["Sentinel"]);
        mode.Immutable.ShouldBeTrue("a topology change is not something a running client survives");
        mode.DefaultJson.ShouldBe("\"Sentinel\"");
    }

    /// <remarks>
    ///     ⚠ Each body is valid in every respect but the one under test. Every property carries a
    ///     format, a pattern or a closed set, so a fixture with a placeholder cluster id would fail on
    ///     that first and the assertion would pass for the wrong reason — <see cref="Error.Target" />
    ///     is the FIRST problem found.
    /// </remarks>
    [Theory]
    [InlineData("""{"properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","version":"8","replicas":3}}""", "/location")]
    [InlineData("""{"location":"eu-central","properties":{"version":"8","replicas":3}}""", "/properties/clusterId")]
    [InlineData("""{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","replicas":3}}""", "/properties/version")]
    [InlineData("""{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","version":"8"}}""", "/properties/replicas")]
    public void EveryRequiredPropertyIsActuallyRequired(string body, string expectedTarget) {
        using var document = JsonDocument.Parse(body);

        var validated = ValkeyCaches.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Target.ShouldBe(expectedTarget);
    }

    /// <summary>
    ///     Values the API must refuse, at the pointer that must refuse them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The alternative to each of these is a body that validates and then produces a
    ///     RedisFailover the API server or the operator refuses AFTER the caller was told
    ///     <c>202</c>.</b> A tenant reads that as "the platform accepted my request and lost it", and
    ///     the reason is in an operator's event stream rather than in the operation's error.
    /// </remarks>
    [Theory]
    [InlineData("/properties/persistence/size", "\"8 gigabytes\"")]
    [InlineData("/properties/persistence/size", "\"-4Gi\"")]
    [InlineData("/properties/sizing/cpu", "\"half a core\"")]
    [InlineData("/properties/sizing/memory", "\"4 GB\"")]
    // ⚠ The three the FIRST provider had no equivalent of: a closed set whose members come from an
    // upstream project rather than from this platform. `allkeys-lfu` is real and `allkeys-mru` is not;
    // `Cluster` is a mode docs/plan/12 names and the operator cannot do; `9` is a Valkey major version
    // that does not exist yet. Each would reach a config file or a CRD and be ignored or rejected
    // there.
    [InlineData("/properties/maxmemoryPolicy", "\"allkeys-mru\"")]
    [InlineData("/properties/mode", "\"Cluster\"")]
    [InlineData("/properties/version", "\"9\"")]
    [InlineData("/properties/persistence/mode", "\"aof\"")]
    [InlineData("/properties/persistence/fsync", "\"everysecond\"")]
    [InlineData("/properties/replicas", "0")]
    [InlineData("/properties/replicas", "6")]
    public void AValueThatWouldReachTheClusterAndMeanNothingIsRefusedHere(string jsonPointer, string literal) {
        using var document = JsonDocument.Parse(BodyWith(jsonPointer, literal));

        var validated = ValkeyCaches.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue($"'{jsonPointer}' accepted {literal}");
        validated.Error!.Target.ShouldBe(jsonPointer);
    }

    [Theory]
    [InlineData("/properties/persistence/size", "\"8Gi\"")]
    [InlineData("/properties/sizing/cpu", "\"500m\"")]
    [InlineData("/properties/sizing/cpu", "\"2\"")]
    [InlineData("/properties/sizing/cpu", "\"\"")]
    [InlineData("/properties/sizing/memory", "\"4Gi\"")]
    [InlineData("/properties/sizing/memory", "\"\"")]
    [InlineData("/properties/persistence/mode", "\"None\"")]
    [InlineData("/properties/persistence/fsync", "\"no\"")]
    [InlineData("/properties/maxmemoryPolicy", "\"volatile-ttl\"")]
    public void TheValuesTheChartWritesAsDefaultsAndExamplesAreAccepted(string jsonPointer, string literal) {
        // ⚠ The other half, and the half that catches an over-tight constraint. Every literal here is
        // one charts/managed/valkey/values.yaml either defaults to or names in a description or an
        // enum, so a pattern or a closed set that refused one would be refusing the chart's own
        // documented values.
        using var document = JsonDocument.Parse(BodyWith(jsonPointer, literal));

        ValkeyCaches.Schema2026.Validate(document.RootElement).IsSuccess.ShouldBeTrue(
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

        foreach (var property in ValkeyCaches.Schema2026.Properties) {
            if (property.DefaultJson.Length == 0) {
                continue;
            }

            Place(body, property.JsonPointer, JsonNode.Parse(property.DefaultJson));
        }

        using var document = JsonDocument.Parse(body.ToJsonString());
        var validated = ValkeyCaches.Schema2026.Validate(document.RootElement);

        validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
    }

    internal static ResourceId Address(string name) =>
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            ValkeyCaches.Type,
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
            ValkeyCaches.Body(Guid.Parse("7b6a5c4d-0000-4000-8000-000000000001"))
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
