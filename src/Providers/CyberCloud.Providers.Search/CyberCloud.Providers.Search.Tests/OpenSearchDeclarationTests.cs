using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search.Tests;

/// <summary>
///     The search declaration, checked the way a silo checks it at start, plus the facts that live in
///     more than one file and have to agree.
/// </summary>
public sealed class OpenSearchDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a duplicate type, on a type with no api-version, on a duplicate short name, and on a
        // RequiresCluster type whose schema does not declare the pointer as a required string.
        var registry = ProviderRegistry.Build([new SearchProvider()]);

        registry.TryGetType(OpenSearchServices.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(OpenSearchServices.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(OpenSearchServices.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(OpenSearchServiceReconciler));
        registration.Actions.ShouldContain(x => x.Name == OpenSearchServices.ListKeysAction && x.Secret);

        // ⚠ `listKeys` does NOT share the read permission. The credential it returns is the OpenSearch
        // ADMIN — the operator generates exactly one and does not scope it — so sharing `read` would
        // make every viewer of a search service a cluster administrator of it.
        registration.Actions.Single(x => x.Name == OpenSearchServices.ListKeysAction)
            .Permission.ShouldNotBe(registration.ReadPermission);
    }

    // ── Failure class (e): a generator that generates nothing and exits 0 ────────────────────────

    [Fact]
    public void TheRegistryHasTypesInItRatherThanBeingAnEmptyRegistryThatBuildsFine() {
        // ⚠ THE ANTI-VACUITY CHECK, AND IT IS ABOUT A COUNT RATHER THAN ABOUT AN EXIT CODE.
        // `ProviderRegistry.Build([])` throws, but a provider whose Describe body was deleted, or
        // whose ResourceType chain was accidentally left dangling, builds a registry with a NAMESPACE
        // and no types — and every downstream generator then emits a document with no paths, writes
        // it, diffs it against itself and exits 0. `Build.Generate`'s own vacuous branch reports
        // "0 provider namespace(s)" for the whole tree; nothing reports zero types for ONE provider.
        //
        // This is deliberately not `ShouldNotBeEmpty` on a collection whose emptiness is impossible:
        // it names the number this provider declares, so that deleting a type is a red test rather
        // than a smaller green one.
        var registry = ProviderRegistry.Build([new SearchProvider()]);

        registry.Types.Length.ShouldBe(
            1,
            "CyberCloud.Search declares a different number of resource types than this test expects. "
            + "If a type was added, add its assertions here and to the conformance project; if one was "
            + "removed, every generated surface just lost a document section and nothing else would "
            + "have failed."
        );

        registry.Types.Select(x => x.Type.ToString())
            .ShouldContain("CyberCloud.Search/services");
    }

    [Fact]
    public void TheFourMetersAreDeclaredAndEachSaysWhatItReads() {
        var registry = ProviderRegistry.Build([new SearchProvider()]);
        registry.TryGetType(OpenSearchServices.Type, out var registration).ShouldBeTrue();

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
                OpenSearchServices.Schema2026.Declares(pointer).ShouldBeTrue(
                    $"the {meter.Meter} derivation declares it reads '{pointer}', which this "
                    + "api-version's schema does not declare. A read set that names a property the "
                    + "schema dropped is what an api-version bump has to be diffed against."
                );
            }
        }

        // ⚠ AND EVERY POD-COUNT POINTER IS IN THE READ SET OF EVERY METER THAT SUMS OVER IT. This is
        // the half a `Reads` declaration exists for: the derivation is a delegate nothing sandboxes,
        // so the only thing that makes the claim checkable is a reviewer — or this — noting that a
        // formula summing three node populations must say it reads all three counts.
        foreach (var meter in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.StorageGb }) {
            var reads = registration.Meters.Single(x => x.Meter == meter).Derivation!.Reads;

            foreach (var pointer in new[] {
                         "/properties/dataNodes",
                         "/properties/masterNodes",
                         "/properties/coordinatingNodes"
                     }) {
                reads.ShouldContain(pointer, meter + " does not declare that it reads " + pointer);
            }
        }
    }

    // ── Failure class (a), at the CLI: two short names, neither of them the group ────────────────

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // ⚠ SchemaProperty checks its own DefaultJson against its own constraints at construction, so
        // a default outside its @range cannot reach here. What THAT check cannot see is the whole
        // body: this walks each default back into an otherwise-valid body and validates the result.
        foreach (var property in OpenSearchServices.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(OpenSearchServices.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            OpenSearchServices.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue(
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
        // rather than a matching string — a matching string is what all four earlier copies were, and
        // it is what a fifth would still be.
        OpenSearchServices.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
        OpenSearchServices.OptionalQuantityPattern.ShouldBeSameAs(KubeQuantity.OptionalPattern);
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecret() {
        // ⚠ docs/plan/12 § The pattern, once, piece 5 and SchemaProperty.Secret's own remarks: a
        // `Secret: true` body property masks the value on the generated surfaces and DOES NOTHING
        // ELSE — the write path stores it in plaintext, in grain state, which docs/plan/05 forbids for
        // a credential. The only secret this type has leaves through the listKeys action, whose
        // response schema is where the one `Secret: true` in this provider lives.
        OpenSearchServices.Schema2026.Properties.ShouldNotContain(
            x => x.Secret,
            "a credential in the resource body is a credential in grain state, whatever the schema "
            + "says about masking it."
        );

        OpenSearchServices.ListKeysResponse.Properties.Count(x => x.Secret).ShouldBe(1);
    }

    [Fact]
    public void TheM1TableIsTheOneTheVocabularyAlreadyDefines() {
        // ⚠ docs/plan/12 § Sizing vocabulary defines m1 as "1:8 · Memory-bound — caches, analytics",
        // and CyberCloud.Cache/redis already carries an m1 table. Two m1 tables that disagreed would
        // make the family name mean two things, and the drift would be invisible on every rung that
        // happened to agree.
        //
        // ⚠ THE RATIO IS A LITERAL 8 RATHER THAN A COMPARISON AGAINST THAT PROVIDER'S TABLE.
        // src/Providers/README.md § Hard rule forbids a Providers.* assembly referencing another, so
        // the tables cannot be diffed directly — the vocabulary they both claim to implement is the
        // only thing both can be compared to, and it is the thing that would be wrong.
        foreach (var (preset, (cpu, memory)) in OpenSearchServices.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(8m, $"'{preset}' is {cpu} to {memory}, which is not 1:8");
        }

        // ⚠ AND THE TWO SMALLEST RUNGS ARE ABSENT ON PURPOSE. OpenSearch derives its JVM heap from the
        // container limit; a node under 4 GiB fails a bootstrap check after passing its readiness
        // probe, which is an outage that looks healthy. Offering a rung this engine cannot run is
        // worse than having no rung, and somebody completing the table "for consistency" is exactly
        // how it would come back.
        OpenSearchServices.Presets.Keys.ShouldNotContain("m1.nano");
        OpenSearchServices.Presets.Keys.ShouldNotContain("m1.micro");
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        // A preset the schema offers and the table does not is a body the API accepts and the meter
        // then refuses — a create that returns 500 for a value the schema advertised.
        OpenSearchServices.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues
            .Order(StringComparer.Ordinal)
            .ShouldBe(OpenSearchServices.Presets.Keys.Order(StringComparer.Ordinal));
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
            ProviderRegistry.Build([new SearchProvider()]).Types.Select(
                x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias)
            )
        ).ShouldBeEmpty();

        // ⚠ THE HALF THE DERIVED CHECK CANNOT MAKE, KEPT FROM THE TEST THAT HELD THE LISTS. Uniqueness
        // says the short name reaches this type; it does not say the short name is the word a person
        // would reach for, and only a literal can say that.
        ProviderRegistry.Build([new SearchProvider()]).Types.Single().Display.Alias.ShouldBe("opensearch");
    }

    // ── The node-pool projection ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultBodyRendersTwoPoolsAndNotAZeroReplicaThirdOne() {
        // ⚠ NodePool.Replicas has NO `omitempty`, so a coordinating pool declared at zero is a real
        // StatefulSet the operator creates, scales to nothing, and then waits on in every readiness
        // roll-up it does. The service would never report healthy and the reason would be a pod that
        // does not exist.
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var pools = JsonNode.Parse(OpenSearchServices.NodePoolsJson(body.RootElement))!.AsArray();

        pools.Count.ShouldBe(2);
        Components(pools).ShouldBe(["masters", "data"]);
    }

    [Fact]
    public void AskingForCoordinatingNodesAddsAThirdPoolAtTheEnd() {
        using var body = JsonDocument.Parse(
            OpenSearchServices.Body(ClusterId, coordinatingNodes: 2)
        );

        var pools = JsonNode.Parse(OpenSearchServices.NodePoolsJson(body.RootElement))!.AsArray();

        // ⚠ THE ORDER IS PART OF THE CONTRACT, NOT A COINCIDENCE. spec.nodePools has no listMapKey in
        // the operator's CRD, so server-side apply treats the whole array as one atomic field this
        // provider owns — a renderer that emitted the same three pools in a different order on two
        // passes would make every apply a write.
        Components(pools).ShouldBe(["masters", "data", "coordinators"]);
        pools[2]!["replicas"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void ACoordinatingPoolCarriesIngestRatherThanAnEmptyRoleList() {
        // ⚠ THE TEXTBOOK SPELLING OF A COORDINATING-ONLY NODE IS AN EMPTY ROLES LIST AND IT IS NOT
        // WHAT IS WRITTEN HERE, WHICH IS THE ONE PLACE THIS PROVIDER DEPARTS FROM THE ENGINE'S OWN
        // VOCABULARY. pkg/builders/cluster.go does `nodeRolesValue := strings.Join(selectedRoles,
        // ",")` with `if len(selectedRoles) == 0 { nodeRolesValue = "[]" }` — it renders the
        // two-character STRING `[]` into an environment variable OpenSearch parses as a roles list.
        // A node whose roles OpenSearch fails to parse joins as a DEFAULT node — data AND
        // cluster-manager — which is the exact opposite of coordinating, and it reports itself
        // healthy while holding shards nobody meant it to hold.
        using var body = JsonDocument.Parse(
            OpenSearchServices.Body(ClusterId, coordinatingNodes: 1)
        );

        var coordinators = JsonNode.Parse(OpenSearchServices.NodePoolsJson(body.RootElement))!
            .AsArray()
            .Single(x => x!["component"]!.GetValue<string>() == "coordinators")!;

        var roles = coordinators["roles"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

        roles.ShouldNotBeEmpty(
            "an empty roles list reaches the operator's `nodeRolesValue = \"[]\"` branch, and a node "
            + "OpenSearch cannot parse roles for joins as a default node."
        );

        roles.ShouldBe(["ingest"]);
        roles.ShouldNotContain("data", "a coordinating node that holds shards is not coordinating");
        roles.ShouldNotContain("cluster_manager");
    }

    [Fact]
    public void TheMasterPoolSaysClusterManagerAndNeverMaster() {
        // ⚠ Both spellings are in the operator's `availableRoles` and only one of them is current.
        // OpenSearch renamed the role in 2.0 and every version this schema offers is ≥ 2.19, so the
        // compatibility path exists and is deliberately not relied on — a provider that shipped the
        // deprecated spelling would work until the release that removes it.
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var masters = JsonNode.Parse(OpenSearchServices.NodePoolsJson(body.RootElement))!
            .AsArray()
            .Single(x => x!["component"]!.GetValue<string>() == "masters")!;

        masters["roles"]!.AsArray().Select(x => x!.GetValue<string>())
            .ShouldBe(["cluster_manager"]);

        // ⚠ And a cluster-manager node gets a volume, because the cluster metadata is the only copy of
        // what indices exist and where their shards live. DiskSize is `omitempty`, so a pool that
        // declared none would take whatever the operator falls back to rather than a stated size.
        masters["diskSize"]!.GetValue<string>().ShouldBe("10Gi");
    }

    [Fact]
    public void TheTlsBlockIsWrittenOnEveryPassAndIsNotConditionalOnAnything() {
        // ⚠ THE ONE FIELD SET WHOSE ABSENCE BREAKS THE SERVICE WITH NO ERROR ANYWHERE.
        // pkg/reconcilers/tls.go returns immediately when Spec.Security or Spec.Security.Tls is nil —
        // "No security specified. Not doing anything" — generating no certificates, creating no
        // secrets and mounting no volumes. OpenSearch's security plugin requires transport TLS to form
        // a cluster, so the symptom is a set of pods that all pass their readiness probes and never
        // discover each other.
        foreach (var coordinating in new[] { 0, 3 }) {
            using var body = JsonDocument.Parse(
                OpenSearchServices.Body(ClusterId, coordinatingNodes: coordinating)
            );

            var tls = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
                ["spec"]!["security"]!["tls"]!;

            tls["transport"]!["generate"]!.GetValue<bool>().ShouldBeTrue();
            tls["http"]!["generate"]!.GetValue<bool>().ShouldBeTrue();

            // ⚠ perNode is the more expensive of the two legal answers. One shared certificate would
            // satisfy the plugin; a per-node certificate is what makes the transport peer check
            // identify the NODE, so that one compromised pod cannot impersonate the cluster manager.
            tls["transport"]!["perNode"]!.GetValue<bool>().ShouldBeTrue();
        }
    }

    [Fact]
    public void NoCredentialReferenceIsRenderedBecauseTheOperatorGeneratesOne() {
        // ⚠ THE DECISION THIS TYPE IS MOST LIKELY TO BE SECOND-GUESSED ON, AND IT IS THE OPPOSITE OF
        // charts/managed/seaweedfs'. pkg/helpers/helpers.go's EnsureAdminCredentialsSecret returns the
        // tenant's secret when spec.security.config.adminCredentialsSecret.Name is set and otherwise
        // does `randomPassword := GenerateSecurePassword()` into a generated Secret. So omitting the
        // reference makes the service come up AUTHENTICATED with a credential the platform cannot hand
        // out; rendering a reference to a Secret nothing writes would stop a cluster that would
        // otherwise be fine.
        //
        // On SeaweedFS the same choice runs the other way — a gateway with no identities file answers
        // every anonymous request as an administrator — which is why the two providers must not be
        // made to look alike by anyone tidying them.
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var security = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            ["spec"]!["security"]!.AsObject();

        security["config"].ShouldBeNull(
            "a spec.security.config referencing an adminCredentialsSecret was rendered. Nothing in "
            + "this platform writes that Secret, and the operator would stop waiting for its own "
            + "generated credential and wait for ours instead."
        );
    }

    [Fact]
    public void TheServiceNameIsWrittenBecauseTheClusterCannotFormWithoutIt() {
        // ⚠ GeneralConfig.ServiceName is the ONE field in that struct with no `omitempty`. The
        // operator names the Service after it and every node's discovery.seed_hosts resolves through
        // it, so a cluster that left it unset would never form — and would report that as pods that
        // are running.
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var general = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            ["spec"]!["general"]!.AsObject();

        general["serviceName"]!.GetValue<string>().ShouldBe("logs");
        general["httpPort"]!.GetValue<int>().ShouldBe(9200);
        general["version"]!.GetValue<string>().ShouldBe("3.1.0");
    }

    [Fact]
    public void TheImageIsNotWrittenBecauseTheOperatorComposesItFromTheVersion() {
        // ⚠ THE EXACT REVERSE OF charts/managed/seaweedfs' FINDING, AND IT IS WORTH ASSERTING FOR
        // THAT REASON. There, api/v1/image.go returns an empty image unchanged and a CR with no
        // spec.image renders pods with NO IMAGE, so the reference is written on every apply. Here
        // GeneralConfig embeds an *ImageSpec AND carries Version, and the operator composes the
        // reference from the version — so writing both would be two spellings of one fact with one
        // silently winning, which is the mistake that provider's note warns about from the other side.
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!["spec"]!["general"]!
            .AsObject()["image"]
            .ShouldBeNull(
                "spec.general.image and spec.general.version are two spellings of the version, and "
                + "the operator lets the first override the second."
            );
    }

    [Fact]
    public void MonitoringOffRemovesTheRequestAndWithItTheOperatorsScrapeObject() {
        // docs/plan/12 § The pattern, once, piece 6's FIRST branch — "ask the operator for the scrape
        // object wherever the operator accepts the request". The operator accepts it as
        // spec.general.monitoring.enable, so this provider renders no monitoring object at all.
        using var on = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));
        using var off = JsonDocument.Parse(
            WithMonitoring(OpenSearchServices.Body(ClusterId), false)
        );

        OpenSearchServices.ClusterJson("logs", on.RootElement).ShouldContain("\"monitoring\"");
        OpenSearchServices.ClusterJson("logs", off.RootElement).ShouldNotContain("monitoring");
    }

    [Fact]
    public void TheEndpointIsHttpsBecauseTheOperatorWasAskedToGenerateAnHttpCertificate() {
        // ⚠ The first service in the catalogue whose endpoint is https and means it.
        // CyberCloud.Storage/accounts hands out http because nothing terminates TLS on its S3 port;
        // here ClusterJson asks for spec.security.tls.http.generate, so the listener genuinely speaks
        // TLS. ⚠ It is a SELF-SIGNED certificate from the operator's own CA and the API hands out no
        // CA bundle — conformance.yaml § owed, `ca-bundle-is-not-handed-out`.
        OpenSearchServices.Endpoint("t-prod", "logs").ShouldBe("https://logs.t-prod.svc:9200");
    }

    /// <summary>The <c>component</c> of each pool, in order.</summary>
    static string[] Components(JsonArray pools) =>
        [.. pools.Select(x => x!["component"]!.GetValue<string>())];

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
