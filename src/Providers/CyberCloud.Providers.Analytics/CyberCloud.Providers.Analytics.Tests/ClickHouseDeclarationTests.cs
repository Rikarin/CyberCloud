using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Analytics.Tests;

/// <summary>
///     The ClickHouse declaration, checked the way a silo checks it at start, plus the facts that
///     live in more than one file and have to agree.
/// </summary>
public sealed class ClickHouseDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a duplicate type, on a type with no api-version, on a duplicate short name, and on a
        // RequiresCluster type whose schema does not declare the pointer as a required string.
        var registry = ProviderRegistry.Build([new AnalyticsProvider()]);

        registry.TryGetType(ClickHouseClusters.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(ClickHouseClusters.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(ClickHouseClusters.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(ClickHouseClusterReconciler));
        registration.Actions.ShouldContain(x => x.Name == ClickHouseClusters.ListKeysAction && x.Secret);

        // ⚠ `listKeys` does NOT share the read permission. docs/plan/07 § Consistency puts a key
        // export in the fully-consistent row by name; sharing `read` would make every viewer of a
        // cluster a holder of its database credentials.
        registration.Actions.Single(x => x.Name == ClickHouseClusters.ListKeysAction)
            .Permission.ShouldNotBe(registration.ReadPermission);
    }

    // ── Failure class (d): a shortName collision, derived rather than listed ─────────────

    [Fact]
    public void NoShortNameHereGivesACycTokenTwoMeanings() {
        // ⚠ DERIVED, AND IT REPLACES ARRAYS OF LITERALS THAT WENT STALE ON TWO CONSECUTIVE PASSES —
        // green by luck both times, because two of the short names they were missing are declared
        // through a `const string ShortName` and the maintenance grep was `grep 'shortName: "'`. The
        // list was maintained by a method that could not find everything it had to list.
        //
        // ⚠ AND THEY ASKED THE WRONG QUESTION. Measured against System.CommandLine 2.0.10, the token
        // dictionary is per PARENT command, so a short name equal to ANOTHER group's key cannot
        // collide — which is what most of those assertions spent themselves forbidding. CliTokens
        // carries the rule and the measurements.
        //
        // ⚠ ONE PROVIDER IS ALL THIS SEES, and src/Providers/README.md § Hard rule is why. The
        // whole-tree half is answered without a list by ProviderRegistry.Build at silo start,
        // CliEmitter.Emit at generation, and GeneratedSurfaceTests over the embedded verb tree.
        CliTokens.Collisions(
            ProviderRegistry.Build([new AnalyticsProvider()]).Types.Select(
                x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias)
            )
        ).ShouldBeEmpty();

        // ⚠ THE HALF THE DERIVED CHECK CANNOT MAKE, KEPT FROM THE TEST THAT HELD THE LISTS. Uniqueness
        // says the short name reaches this type; it does not say the short name is the word a person
        // would reach for, and only a literal can say that.
        ProviderRegistry.Build([new AnalyticsProvider()]).Types.Single().Display.Alias.ShouldBe("clickhouse");
    }

    // ── Failure class: the meters ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheFourMetersAreDeclaredAndEachSaysWhatItReads() {
        var registry = ProviderRegistry.Build([new AnalyticsProvider()]);
        registry.TryGetType(ClickHouseClusters.Type, out var registration).ShouldBeTrue();

        foreach (var meter in new[] {
                     QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb, QuotaMeter.Resources
                 }) {
            registration.Meters.ShouldContain(x => x.Meter == meter, meter.ToString());
        }

        foreach (var meter in registration.Meters.Where(x => x.Derivation is not null)) {
            meter.Derivation!.Expression.ShouldNotBeNullOrWhiteSpace(meter.Meter.ToString());
            meter.Derivation.Reads.ShouldNotBeEmpty(meter.Meter.ToString());

            foreach (var pointer in meter.Derivation.Reads) {
                ClickHouseClusters.Schema2026.Declares(pointer).ShouldBeTrue(
                    $"the {meter.Meter} derivation declares it reads '{pointer}', which this "
                    + "api-version's schema does not declare."
                );
            }
        }
    }

    [Fact]
    public void EveryDerivedMeterSaysItReadsBothFactorsOfTheProductAndTheKeeperCount() {
        // ⚠ THE READ SET IS THE ONLY REVIEWABLE PART OF A DERIVATION, because the amount itself is a
        // delegate nothing sandboxes. A meter on this type that named only `/properties/replicas`
        // would be declaring, in the one place a reviewer looks, that it is the natsClusters shape —
        // and it would reserve a third of what a three-shard cluster costs.
        var registry = ProviderRegistry.Build([new AnalyticsProvider()]);
        registry.TryGetType(ClickHouseClusters.Type, out var registration).ShouldBeTrue();

        foreach (var meter in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb }) {
            var reads = registration.Meters.Single(x => x.Meter == meter).Derivation!.Reads;

            reads.ShouldContain("/properties/shards", meter.ToString());
            reads.ShouldContain("/properties/replicas", meter.ToString());
            reads.ShouldContain("/properties/keeperNodes", meter.ToString());
        }
    }

    // ── Failure class: the declared defaults, and the schema's own coherence ─────────────────────

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // ⚠ SchemaProperty checks its own DefaultJson against its own constraints at construction, so
        // a default outside its @range cannot reach here. What THAT check cannot see is the whole
        // body: this walks each default back into an otherwise-valid body and validates the result.
        foreach (var property in ClickHouseClusters.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(ClickHouseClusters.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            ClickHouseClusters.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue(
                $"the declared default for '{property.JsonPointer}' does not validate inside an "
                + "otherwise-valid body."
            );
        }
    }

    [Fact]
    public void TheQuantityGrammarIsThePlatformsRatherThanAFifthCopyOfIt() {
        // ⚠ KubeQuantity's remarks: "There is exactly one of these in the platform and there must
        // stay exactly one", and they record that the last provider to keep its own copy of the
        // grammar got a second PARSER written next to it — one that returned 8699999999999 for 8.7T.
        // QuantityParserTests fails if a fresh copy appears. This asserts the identity is a REFERENCE
        // rather than a matching string.
        ClickHouseClusters.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
        ClickHouseClusters.OptionalQuantityPattern.ShouldBeSameAs(KubeQuantity.OptionalPattern);
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecret() {
        // ⚠ SchemaProperty.Secret's own remarks: a `Secret: true` body property masks the value on the
        // generated surfaces and DOES NOTHING ELSE — the write path stores it in plaintext, in grain
        // state, which docs/plan/05 forbids for a credential. The only secret this type has leaves
        // through the listKeys action.
        ClickHouseClusters.Schema2026.Properties.ShouldNotContain(
            x => x.Secret,
            "a credential in the resource body is a credential in grain state, whatever the schema "
            + "says about masking it."
        );

        ClickHouseClusters.ListKeysResponse.Properties.Count(x => x.Secret).ShouldBe(1);
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        // A preset the schema offers and the table does not is a body the API accepts and the meter
        // then refuses — a create that returns 500 for a value the schema advertised.
        ClickHouseClusters.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues
            .Order(StringComparer.Ordinal)
            .ShouldBe(ClickHouseClusters.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPresetAboveNanoHoldsTheOneToEightRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary defines m1 as "1:8 · Memory-bound — caches, analytics".
        // ⚠ `m1.nano` is deliberately off the ratio at 1:10, exactly as `s1.nano` is, and asserting it
        // separately is what stops somebody "fixing" it: the smallest box needs a floor rather than a
        // ratio, because a ClickHouse server plus its page cache does not fit in what 100m implies.
        foreach (var (preset, (cpu, memory)) in ClickHouseClusters.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(
                preset == "m1.nano" ? 10m : 8m,
                $"'{preset}' is {cpu} to {memory}"
            );
        }
    }

    // ── Failure class: the two objects, and the string that binds them ──────────────────────────

    [Fact]
    public void TheInstallationPointsItsCoordinationAtTheKeepersOwnServiceName() {
        // ⚠ THE SINGLE MOST LOAD-BEARING STRING ON THIS TYPE, AND THE ONE WITH NO NATURAL DEFENCES.
        // Nothing this provider applies creates `keeper-{name}` — the OPERATOR does, off the
        // ClickHouseKeeperInstallation — so a wrong prefix produces a cluster that applies cleanly,
        // reads back cleanly, converges, answers SELECT 1, and fails the first ReplicatedMergeTree a
        // tenant creates.
        //
        // ⚠ ASSERTED AGAINST A LITERAL rather than against KeeperServiceName, for the reason the
        // casing tests give: two things derived from one constant agree however that constant is
        // spelled.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var nodes = JsonNode.Parse(ClickHouseClusters.ClickHouseJson("events", body.RootElement))!
            ["spec"]!["configuration"]!["zookeeper"]!["nodes"]!.AsArray();

        nodes.Count.ShouldBe(1);
        nodes[0]!["host"]!.GetValue<string>().ShouldBe("keeper-events");
        nodes[0]!["port"]!.GetValue<int>().ShouldBe(2181);
    }

    [Fact]
    public void TheKeeperIsRenderedEvenForTheSmallestPossibleCluster() {
        // ⚠ THE DECISION, ASSERTED SO IT CANNOT BE UNDONE AS AN OPTIMISATION. A one-shard one-replica
        // ClickHouse does not need coordination to RUN, so "skip the Keeper when it is not needed"
        // reads as a saving. It is not: the schema is the tenant's problem (docs/plan/12), a
        // ReplicatedMergeTree is the ordinary thing to create, and a Keeper that appeared later would
        // arrive after the tables that needed it.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId, shards: 1, replicas: 1));

        var keeper = JsonNode.Parse(ClickHouseClusters.KeeperJson("events", body.RootElement))!["spec"]!;

        keeper["configuration"]!["clusters"]!.AsArray()[0]!["layout"]!["replicasCount"]!
            .GetValue<int>()
            .ShouldBe(3);
    }

    [Fact]
    public void TheKeeperLayoutHasNoShardAxisAtAll() {
        // ⚠ Raft replicates one log to every member; there is nothing to split, and the CHK layout has
        // no shardsCount. Writing one would be a field the CRD does not declare, which
        // x-kubernetes-preserve-unknown-fields would happily accept and the operator would ignore.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId, shards: 4));

        var layout = JsonNode.Parse(ClickHouseClusters.KeeperJson("events", body.RootElement))!["spec"]!
            ["configuration"]!["clusters"]!.AsArray()[0]!["layout"]!.AsObject();

        layout["shardsCount"].ShouldBeNull("the Keeper cluster was given a shard axis it does not have");
        layout["replicasCount"].ShouldNotBeNull();
    }

    [Fact]
    public void BothObjectsCarryTheSameVersionTag() {
        // ⚠ ONE PROPERTY, TWO IMAGES. Keeper and server share a release train and a wire protocol; a
        // cluster whose coordination is two majors ahead of its servers is a combination nobody tests.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        ClickHouseClusters.ClickHouseJson("events", body.RootElement)
            .ShouldContain("clickhouse/clickhouse-server:25.3", Case.Sensitive);

        ClickHouseClusters.KeeperJson("events", body.RootElement)
            .ShouldContain("clickhouse/clickhouse-keeper:25.3", Case.Sensitive);
    }

    [Fact]
    public void EveryPodTemplateNamedInDefaultsIsOneTheObjectAlsoDeclares() {
        // ⚠ A NAME IN `defaults.templates` THAT IS NOT IN `templates` IS NOT AN ERROR — the operator
        // runs the container it would have run anyway, with no image and no resources, and the CR is
        // accepted. So the pairing has nothing enforcing it but this.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        foreach (var json in new[] {
                     ClickHouseClusters.ClickHouseJson("events", body.RootElement),
                     ClickHouseClusters.KeeperJson("events", body.RootElement)
                 }) {
            var spec = JsonNode.Parse(json)!["spec"]!;
            var defaults = spec["defaults"]!["templates"]!;
            var templates = spec["templates"]!;

            templates["podTemplates"]!.AsArray()
                .Select(x => x!["name"]!.GetValue<string>())
                .ShouldContain(defaults["podTemplate"]!.GetValue<string>());

            templates["volumeClaimTemplates"]!.AsArray()
                .Select(x => x!["name"]!.GetValue<string>())
                .ShouldContain(defaults["dataVolumeClaimTemplate"]!.GetValue<string>());
        }
    }

    // ── Failure class: the scope boundary docs/plan/12 wrote for this row ───────────────────────

    [Fact]
    public void NeitherObjectSaysAnythingAboutTablesOrSchema() {
        // ⚠ docs/plan/12: "Schema is the tenant's problem and the resource does not manage tables. A
        // managed ClickHouse that tries to own DDL is a migration tool nobody asked for." The
        // non-obvious half is `schemaPolicy`, which is where the operator asks the platform how much
        // of a tenant's schema to copy onto a new replica — leaving it unset leaves that answer with
        // the operator rather than making it the platform's opinion about somebody else's tables.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId, shards: 3, replicas: 3));

        foreach (var json in new[] {
                     ClickHouseClusters.ClickHouseJson("events", body.RootElement),
                     ClickHouseClusters.KeeperJson("events", body.RootElement)
                 }) {
            foreach (var forbidden in new[] { "schemaPolicy", "CREATE ", "DATABASE", "TABLE", "files" }) {
                json.ShouldNotContain(
                    forbidden,
                    Case.Sensitive,
                    $"the rendered object carries '{forbidden}', and this resource does not manage "
                    + "tables — docs/plan/12 § The catalogue."
                );
            }
        }

        // And the schema offers no way to ask for it either.
        ClickHouseClusters.Schema2026.Properties.ShouldNotContain(
            x => x.JsonPointer.Contains("schema", StringComparison.OrdinalIgnoreCase)
                || x.JsonPointer.Contains("table", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void NoCredentialIsRenderedAndTheClusterIsThereforeUnreachableRatherThanOpen() {
        // ⚠ THE PIECE-5 ANSWER, AND ON THIS SERVICE IT IS THE OPPOSITE OF SeaweedFS'. The operator's
        // own hardening guide: a CHI with no `configuration.users` gets `default` with an empty
        // password behind a host_regexp AND an explicit pod-IP allow-list covering THIS CLUSTER'S OWN
        // PODS, and `clickhouse_operator` behind the operator pod's IP. So rendering no users block is
        // "secure and unreachable", not "anonymous administrator" — which is why this test asserts the
        // ABSENCE rather than a reference to a Secret nothing writes.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var configuration = JsonNode.Parse(ClickHouseClusters.ClickHouseJson("events", body.RootElement))!
            ["spec"]!["configuration"]!.AsObject();

        configuration["users"].ShouldBeNull(
            "a users block was rendered. Any password in it would have come from the resource body, "
            + "which is grain state — docs/plan/05 — and this reconciler mints nothing, so there is "
            + "no other source for one. Rendering a user means minting through ISecretWriter first."
        );

        foreach (var forbidden in new[] { "password", "secret", "stringData" }) {
            ClickHouseClusters.ClickHouseJson("events", body.RootElement)
                .ShouldNotContain(forbidden, Case.Insensitive, forbidden);
        }
    }

    [Fact]
    public void MonitoringOffRemovesBothTheSettingsAndThePort() {
        // ⚠ Piece 6 reaching an answer neither branch describes: the operator offers no
        // per-installation scrape switch, so what this flag controls is CLICKHOUSE'S OWN Prometheus
        // endpoint. Both halves have to move together — a container port with no endpoint behind it
        // is a scrape target that answers 404.
        using var on = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));
        using var off = JsonDocument.Parse(WithMonitoring(ClickHouseClusters.Body(ClusterId), false));

        var enabled = ClickHouseClusters.ClickHouseJson("events", on.RootElement);
        enabled.ShouldContain("prometheus/endpoint", Case.Sensitive);
        enabled.ShouldContain("\"containerPort\":9363", Case.Sensitive);

        var disabled = ClickHouseClusters.ClickHouseJson("events", off.RootElement);
        disabled.ShouldNotContain("prometheus", Case.Sensitive);
        disabled.ShouldNotContain("9363", Case.Sensitive);
    }

    [Fact]
    public void TheEndpointsNameTheOperatorsOwnServiceRatherThanTheResource() {
        // ⚠ The client Service is `clickhouse-{name}` and it is the OPERATOR's object. Confirmed from
        // the other side rather than from a convention: the operator's hardening guide prints the
        // users.xml it generates, and the default user's host_regexp contains
        // `clickhouse\-my-cluster\.test\.svc\.cluster\.local` for a CHI called `my-cluster` in `test`.
        // An endpoint naming the resource itself would resolve to nothing, and listKeys is the only
        // place a caller ever learns the address.
        ClickHouseClusters.HttpEndpoint("t-prod", "events")
            .ShouldBe("http://clickhouse-events.t-prod.svc:8123");

        ClickHouseClusters.NativeEndpoint("t-prod", "events")
            .ShouldBe("clickhouse-events.t-prod.svc:9000");
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

    static string WithMonitoring(string body, bool enabled) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["monitoring"] = new JsonObject { ["enabled"] = enabled };
        return node.ToJsonString();
    }
}
