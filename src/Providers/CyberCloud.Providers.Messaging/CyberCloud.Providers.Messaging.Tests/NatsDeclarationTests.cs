using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The NATS declaration, checked the way a silo checks it at start, plus the facts that live in
///     more than one file and have to agree.
/// </summary>
public sealed class NatsDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a DUPLICATE TYPE, on a type with no api-version, on a duplicate SHORT NAME, and on a
        // RequiresCluster type whose schema does not declare the pointer as a required string.
        //
        // ⚠ THE TWO IN THE MIDDLE ARE WHY THIS TEST MATTERS MORE ON THE SECOND TYPE THAN IT DID ON
        // THE FIRST. `natsClusters` versus `kafkaClusters` and `nats` versus `kafka` are the two
        // collisions a second type in one namespace can produce, and both of them are silo-start
        // failures rather than compile ones.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);

        registry.TryGetType(NatsClusters.Type, out var registration).ShouldBeTrue();
        registry.TryGetType(KafkaClusters.Type, out _).ShouldBeTrue(
            "adding the second type removed the first one from the registry"
        );

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(NatsClusters.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(NatsClusters.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(NatsClusterReconciler));
        registration.Actions.ShouldContain(x => x.Name == NatsClusters.ListKeysAction && x.Secret);

        // ⚠ `listKeys` does NOT share the read permission. docs/plan/07 § Consistency puts a key
        // export in the fully-consistent row by name; sharing `read` would make every viewer of a
        // cluster a holder of its credentials, and the change that would do it is one word.
        registration.Actions.Single(x => x.Name == NatsClusters.ListKeysAction)
            .Permission.ShouldNotBe(registration.ReadPermission);
    }

    [Fact]
    public void TheFourMetersAreDeclaredAndEachSaysWhatItReads() {
        // ⚠ THE ASSERTION THE KAFKA TYPE CANNOT MAKE, AND THE REASON THIS FILE EXISTS SEPARATELY.
        // That registration declares QuotaMeter.Resources alone, with a long note saying the other
        // three are undeclarable because Meter(meter, pointer) reads a NUMBER. MeterDerivation closed
        // that; this asserts the closure rather than trusting it.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        registry.TryGetType(NatsClusters.Type, out var registration).ShouldBeTrue();

        foreach (var meter in new[] {
                     QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb, QuotaMeter.Resources
                 }) {
            registration.Meters.ShouldContain(x => x.Meter == meter, meter.ToString());
        }

        // Every derived meter publishes its formula and its read set — the price MeterDerivation
        // charges for putting a delegate on the quota path, and what OpenApiEmitter publishes.
        foreach (var meter in registration.Meters.Where(x => x.Derivation is not null)) {
            meter.Derivation!.Expression.ShouldNotBeNullOrWhiteSpace(meter.Meter.ToString());
            meter.Derivation.Reads.ShouldNotBeEmpty(meter.Meter.ToString());

            foreach (var pointer in meter.Derivation.Reads) {
                NatsClusters.Schema2026.Declares(pointer).ShouldBeTrue(
                    $"the {meter.Meter} derivation declares it reads '{pointer}', which this "
                    + "api-version's schema does not declare. A read set that names a property the "
                    + "schema dropped is what an api-version bump has to be diffed against."
                );
            }
        }
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // ⚠ SchemaProperty checks its own DefaultJson against its own constraints at construction, so
        // a default outside its @range cannot reach here. What THAT check cannot see is the whole
        // body: this walks each default back into an otherwise-valid body and validates the result,
        // so a default that is individually legal and jointly refused is caught here rather than by
        // the first tenant who omits the property.
        foreach (var property in NatsClusters.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(NatsClusters.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            NatsClusters.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue(
                $"the declared default for '{property.JsonPointer}' does not validate inside an "
                + "otherwise-valid body."
            );
        }
    }

    [Fact]
    public void TheQuantityGrammarIsThePlatformsRatherThanAFourthCopyOfIt() {
        // ⚠ KubeQuantity's remarks: "There is exactly one of these in the platform and there must
        // stay exactly one", and they record that the last provider to keep its own copy of the
        // grammar got a second PARSER written next to it — one that returned 8699999999999 for 8.7T.
        // KafkaClusters, PostgresServers and TestProvider each carried a byte-identical literal until
        // 2026-08-12 and now point at KubeQuantity too; QuantityParserTests fails if a fresh copy
        // appears. This asserts the identity is a REFERENCE rather than a matching string — a
        // matching string is what all four of these were, and it is what a copy would still be.
        NatsClusters.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
        NatsClusters.OptionalQuantityPattern.ShouldBeSameAs(KubeQuantity.OptionalPattern);
    }

    [Fact]
    public void TheFileStoreCeilingIsTheVolumeSizeInBytesForEverySuffixTheSchemaAccepts() {
        // ⚠ THE ONE PLACE THIS PROVIDER CROSSES TWO SIZE GRAMMARS, AND THEY AGREE ON THIRTEEN OF
        // FOURTEEN SUFFIXES. NATS' conf/parse.go lower-cases the suffix and maps `ki`→1024,
        // `gi`→2^30 — the same numbers Kubernetes means. THE FOURTEENTH IS `m`: Kubernetes reads it
        // as a MILLI and NATS as a MEGA, so `500m` is half a byte to the volume and five hundred
        // million to JetStream, AND NEITHER SIDE COMPLAINS. The schema's Pattern permits it, because
        // it is the same quantity grammar every property on every provider uses.
        //
        // Converting through KubeQuantity is what removes the question; this is what proves it was
        // removed.
        foreach (var (size, bytes) in new[] {
                     ("10Gi", "10737418240"),
                     ("512Mi", "536870912"),
                     ("1T", "1000000000000"),
                     ("2048", "2048"),
                     // ⚠ The trap, written out. Passing this through verbatim would cap JetStream at
                     // 500 MB on a half-byte volume; converting reads it as Kubernetes does.
                     ("500m", "0")
                 }) {
            using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId, storageSize: size));

            NatsClusters.ConfigJson("events", body.RootElement)
                .ShouldContain("max_file_store: " + bytes, Case.Sensitive, size);
        }
    }

    [Fact]
    public void AnAbsentMemoryCeilingIsAnExplicitZeroRatherThanAnOmittedKey() {
        // ⚠ NATS' OWN DEFAULT FOR max_memory_store IS A FRACTION OF SYSTEM MEMORY, which is not the
        // container's limit — the server does not read its own cgroup. So omitting the key gives a
        // memory-backed stream a budget nobody chose, on a machine whose size the tenant never saw.
        // Zero is "file storage only", which is what docs/plan/12 asks this service for.
        using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        NatsClusters.ConfigJson("events", body.RootElement).ShouldContain("max_memory_store: 0");
    }

    [Fact]
    public void TheRouteListNamesOnePeerPerServerAtTheHeadlessServicesOwnName() {
        // ⚠ THREE SEPARATE MISTAKES SHARE ONE SYMPTOM HERE — a cluster of N standalone servers, each
        // of which reports itself healthy. A route list that named the headless Service ONCE would
        // resolve to zero ready pods at first start; one that named the CLIENT Service would resolve
        // to a VIP rather than to peers; one short by a server leaves that server unreachable to the
        // rest. None of the three produces an error anywhere.
        using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId, servers: 5));

        var config = NatsClusters.ConfigJson("events", body.RootElement);

        for (var ordinal = 0; ordinal < 5; ordinal++) {
            config.ShouldContain($"nats://events-{ordinal}.events-headless:6222");
        }

        config.ShouldNotContain("events-5.");
        NatsClusters.HeadlessServiceName("events").ShouldBe("events-headless");
    }

    [Fact]
    public void TheLeafNodeListenerIsOffUnlessTheBodyAsksForIt() {
        // docs/plan/12 wants leaf-node connectivity available; a leaf node joins the cluster's
        // SUBJECT SPACE, so it is a topology change rather than a client and must not be a default.
        using var off = JsonDocument.Parse(NatsClusters.Body(ClusterId));
        using var on = JsonDocument.Parse(NatsClusters.Body(ClusterId, leafNodes: true));

        NatsClusters.ConfigJson("events", off.RootElement).ShouldNotContain("leafnodes");
        NatsClusters.ConfigJson("events", on.RootElement).ShouldContain("listen: 0.0.0.0:7422");
    }

    [Fact]
    public void EveryPresetHoldsTheOneToTwoRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary defines c1 as "1:2, guaranteed". A table that drifted
        // off the ratio on one rung would make the family name a lie on that rung and nowhere else,
        // which is the kind of thing nobody notices because every other row reads correctly.
        foreach (var (preset, (cpu, memory)) in NatsClusters.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(2m, $"'{preset}' is {cpu} to {memory}");
        }
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        // A preset the schema offers and the table does not is a body the API accepts and the meter
        // then refuses — a create that returns 500 for a value the schema advertised.
        var declared = NatsClusters.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues;

        declared.Order(StringComparer.Ordinal)
            .ShouldBe(NatsClusters.Presets.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>A body with one pointer replaced by a raw JSON value.</summary>
    static string Overridden(string body, string pointer, string valueJson) {
        var node = JsonNode.Parse(body)!.AsObject();
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var at = node;
        for (var i = 0; i < segments.Length - 1; i++) {
            at = at[segments[i]]?.AsObject() ?? Added(at, segments[i]);
        }

        at[segments[^1]] = JsonNode.Parse(valueJson);
        return node.ToJsonString();
    }

    static JsonObject Added(JsonObject parent, string name) {
        var child = new JsonObject();
        parent[name] = child;
        return child;
    }
}
