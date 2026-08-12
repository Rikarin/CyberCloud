using CyberCloud.ResourceManager.Registry;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The declaration, checked the way a silo checks it at start, plus the two facts that live in
///     two files and have to agree.
/// </summary>
public sealed class KafkaDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a type with no api-version, on a duplicate short name, and on a RequiresCluster type
        // whose schema does not declare the pointer as a required string. Running it is the cheapest
        // way to find all of that at compile-and-test time rather than at silo start.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);

        registry.TryGetType(KafkaClusters.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(KafkaClusters.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(KafkaClusters.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(KafkaClusterReconciler));
        registration.Actions.ShouldContain(x => x.Name == KafkaClusters.ListKeysAction && x.Secret);

        // ⚠ `listKeys` does NOT share the read permission, and this is the assertion that keeps it
        // that way. docs/plan/07 § Consistency puts a key export in the fully-consistent row by name;
        // sharing `read` would make every viewer of a broker a holder of its credentials, and the
        // change that would do it is one word.
        registration.Actions.Single(x => x.Name == KafkaClusters.ListKeysAction)
            .Permission.ShouldNotBe(registration.ReadPermission);
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // ⚠ SchemaProperty checks its own DefaultJson against its own constraints at construction, so
        // a default outside its @range cannot reach here — the failure would be a
        // TypeInitializationException at silo start naming a static constructor. What THAT check
        // cannot see is the whole body: this walks each default back into an otherwise-valid body and
        // validates the result, so a default that is individually legal and jointly refused is caught
        // here rather than by the first tenant who omits the property.
        //
        // ⚠ The body has to be a COMPLETE one with the property overridden, not a body carrying that
        // property alone. Three properties are required — /location, /properties/clusterId and
        // /properties/version — so a single-property body fails on the two it is missing, which is a
        // test that goes red for its own reason and says nothing about defaults. That is the shape
        // this test had on its first run.
        foreach (var property in KafkaClusters.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(KafkaClusters.Body(Guid.NewGuid()), property.JsonPointer, property.DefaultJson)
            );

            var validated = KafkaClusters.Schema2026.Validate(body.RootElement, allowTags: true);

            validated.IsSuccess.ShouldBeTrue(
                $"'{property.JsonPointer}' declares a default the API would refuse: {validated.Error?.Message}"
            );
        }
    }

    [Fact]
    public void TheBodyHelperSatisfiesTheSchemaItClaimsTo() {
        // Every test in this assembly and the conformance case's whole surface start from Body(). A
        // helper that drifted off the schema would make every one of them test a body the API would
        // refuse.
        using var body = JsonDocument.Parse(KafkaClusters.Body(Guid.NewGuid()));

        var validated = KafkaClusters.Schema2026.Validate(body.RootElement, allowTags: true);
        validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
    }

    [Fact]
    public void TheDeclaredPatternsAcceptWhatTheyDescribeAndRefuseWhatTheyDoNot() {
        // ⚠ A pattern is a whole-value match — ResourceSchema tests it as `^(?:…)$` — and this test
        // applies the same anchoring, because a bare pattern is a SEARCH and would accept
        // `xxx100Gixxx`. charts/README.md § The annotation format is the other half of that sentence.
        Anchored(KafkaClusters.QuantityPattern).IsMatch("100Gi").ShouldBeTrue();
        Anchored(KafkaClusters.QuantityPattern).IsMatch("500m").ShouldBeTrue();
        Anchored(KafkaClusters.QuantityPattern).IsMatch("").ShouldBeFalse();
        Anchored(KafkaClusters.QuantityPattern).IsMatch("-1Gi").ShouldBeFalse();
        Anchored(KafkaClusters.OptionalQuantityPattern).IsMatch("").ShouldBeTrue();

        // ⚠ The CIDR pattern is the one this provider adds to the vocabulary, and its prefix length
        // is bounded by an alternation rather than a range because a regex cannot say "0 to 32". A
        // `/33` entry matches nothing on a load balancer while looking exactly like a rule, so it has
        // to be refused at the API.
        Anchored(KafkaClusters.CidrPattern).IsMatch("203.0.113.0/24").ShouldBeTrue();
        Anchored(KafkaClusters.CidrPattern).IsMatch("10.0.0.0/8").ShouldBeTrue();
        Anchored(KafkaClusters.CidrPattern).IsMatch("0.0.0.0/0").ShouldBeTrue();
        Anchored(KafkaClusters.CidrPattern).IsMatch("203.0.113.0/33").ShouldBeFalse();
        Anchored(KafkaClusters.CidrPattern).IsMatch("256.0.113.0/24").ShouldBeFalse();
        Anchored(KafkaClusters.CidrPattern).IsMatch("203.0.113.0").ShouldBeFalse();
    }

    [Fact]
    public void TheListenerNameFitsStrimzisElevenCharacterLimit() {
        // ⚠ The CRD pins `^[a-z0-9]{1,11}$` on spec.kafka.listeners[].name, and Strimzi builds
        // Kubernetes object names out of it. A twelfth character is a manifest the API server refuses
        // AFTER the caller was told 202 — the class of failure this provider's whole render path is
        // arranged to avoid.
        foreach (var name in new[] { KafkaClusters.InternalListener, KafkaClusters.ExternalListener }) {
            name.Length.ShouldBeLessThanOrEqualTo(11, name);
            Regex.IsMatch(name, "^[a-z0-9]+$", RegexOptions.None, TimeSpan.FromSeconds(1))
                .ShouldBeTrue(name);
        }
    }

    [Fact]
    public void TheSizingTableAgreesWithTheChartsValueForValue() {
        // ⚠ THE HALF ADR-012's GENERATION DOES NOT REACH. The chart's @param block is generated from
        // KafkaClusters.Schema2026 and byte-diffed by ./build.sh Charts; a Helm TEMPLATE is not a
        // schema and nothing generates it. So `kafka.resources` in _helpers.tpl and
        // KafkaClusters.Presets are two hand-maintained copies of one table, and this is what stops
        // them drifting until CyberCloud.Kubernetes.Charts exists and one of them can be deleted.
        var helpers = Embedded("kafka.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in KafkaClusters.Presets) {
            var row = Regex.Match(
                helpers,
                // ⚠ `\s+` between every token, not a single space. The chart's table is column-aligned
                // for a reader, so `"cpu" "1"    "memory"` has four spaces where `"cpu" "250m"` has
                // one — a regex written against one row matches half the table and reports the other
                // half as missing.
                "\"" + Regex.Escape(preset) + "\"\\s+\\(dict\\s+\"cpu\"\\s+\"([^\"]+)\"\\s+\"memory\"\\s+\"([^\"]+)\"\\)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)
            );

            row.Success.ShouldBeTrue($"the chart's preset table has no row for '{preset}'");
            row.Groups[1].Value.ShouldBe(cpu, preset);
            row.Groups[2].Value.ShouldBe(memory, preset);
        }

        // ⚠ And the other direction: a preset the chart has and the schema does not is a value a
        // tenant can write into values.yaml and never into a resource body.
        foreach (Match row in Regex.Matches(
                     helpers,
                     "\"(c1\\.[a-z0-9]+)\"\\s+\\(dict\\s+\"cpu\"",
                     RegexOptions.None,
                     TimeSpan.FromSeconds(5)
                 )) {
            KafkaClusters.Presets.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void EveryPresetHoldsTheOneToTwoRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary defines c1 as "1:2, guaranteed". A table that drifted
        // off the ratio on one rung would make the family name a lie on that rung and nowhere else,
        // which is the kind of thing nobody notices because every other row reads correctly.
        foreach (var (preset, (cpu, memory)) in KafkaClusters.Presets) {
            (Gibibytes(memory) / Cores(cpu)).ShouldBe(2m, $"'{preset}' is {cpu} to {memory}");
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static Regex Anchored(string pattern) =>
        new("^(?:" + pattern + ")$", RegexOptions.None, TimeSpan.FromSeconds(1));

    static decimal Cores(string quantity) =>
        quantity.EndsWith('m')
            ? decimal.Parse(quantity[..^1], CultureInfo.InvariantCulture) / 1000m
            : decimal.Parse(quantity, CultureInfo.InvariantCulture);

    static decimal Gibibytes(string quantity) =>
        quantity.EndsWith("Mi", StringComparison.Ordinal)
            ? decimal.Parse(quantity[..^2], CultureInfo.InvariantCulture) / 1024m
            : decimal.Parse(quantity[..^2], CultureInfo.InvariantCulture);

    /// <summary>A valid body with one pointer replaced by the given JSON, creating parents as needed.</summary>
    static string Overridden(string body, string pointer, string json) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cursor = node;

        for (var i = 0; i < segments.Length - 1; i++) {
            if (cursor[segments[i]] is not System.Text.Json.Nodes.JsonObject next) {
                next = [];
                cursor[segments[i]] = next;
            }

            cursor = next;
        }

        cursor[segments[^1]] = System.Text.Json.Nodes.JsonNode.Parse(json);
        return node.ToJsonString();
    }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded in this assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
