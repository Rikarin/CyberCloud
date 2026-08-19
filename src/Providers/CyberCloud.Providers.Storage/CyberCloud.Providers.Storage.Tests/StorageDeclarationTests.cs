using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The object-storage declaration, checked the way a silo checks it at start, plus the facts that
///     live in more than one file and have to agree.
/// </summary>
public sealed class StorageDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a duplicate type, on a type with no api-version, on a duplicate short name, and on a
        // RequiresCluster type whose schema does not declare the pointer as a required string.
        var registry = ProviderRegistry.Build([new StorageProvider()]);

        registry.TryGetType(StorageAccounts.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(StorageAccounts.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(StorageAccounts.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(StorageAccountReconciler));
        registration.Actions.ShouldContain(x => x.Name == StorageAccounts.ListKeysAction && x.Secret);

        // ⚠ `listKeys` does NOT share the read permission. docs/plan/07 § Consistency puts a key
        // export in the fully-consistent row by name, and on THIS type the exported pair is the only
        // access control the data plane has — docs/plan/15 keeps ReBAC off the object GET path
        // deliberately. Sharing `read` would make every viewer of an account an owner of its data.
        registration.Actions.Single(x => x.Name == StorageAccounts.ListKeysAction)
            .Permission.ShouldNotBe(registration.ReadPermission);
    }

    [Fact]
    public void TheFourMetersAreDeclaredAndEachSaysWhatItReads() {
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        registry.TryGetType(StorageAccounts.Type, out var registration).ShouldBeTrue();

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
                StorageAccounts.Schema2026.Declares(pointer).ShouldBeTrue(
                    $"the {meter.Meter} derivation declares it reads '{pointer}', which this "
                    + "api-version's schema does not declare. A read set that names a property the "
                    + "schema dropped is what an api-version bump has to be diffed against."
                );
            }
        }

        // ⚠ AND THE TWO POD-COUNT POINTERS ARE IN THE READ SETS OF THE TWO METERS THAT NEED THEM. This
        // is the half a `Reads` declaration exists for: the derivation is a delegate nothing
        // sandboxes, so the only thing that makes the claim checkable is a reviewer — or this — noting
        // that a formula summing masters and gateways must say it reads them.
        foreach (var meter in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb }) {
            var reads = registration.Meters.Single(x => x.Meter == meter).Derivation!.Reads;

            reads.ShouldContain("/properties/masters", meter.ToString());
            reads.ShouldContain("/properties/gateway/replicas", meter.ToString());
        }
    }

    [Fact]
    public void TheShortNameIsNotTheGroupNameTheNamespaceAlreadyProduces() {
        // ⚠ THE COLLISION THIS PROVIDER FOUND, AND THE ONE THE ALIAS TABLE WALKS STRAIGHT INTO.
        // docs/plan/21 § Grammar's table would spell this alias `storage`, exactly as it spells
        // `postgres` for `dbforpostgresql server`. CliEmitter derives the GROUP key from the provider
        // namespace, so `CyberCloud.Storage` is already the group `storage` — and System.CommandLine's
        // ValidTokens builds ONE dictionary over every command token AND every alias in the tree, so
        // the two throw `An item with the same key has already been added. Key: storage` on the first
        // parse of any command line, before any verb runs.
        //
        // ⚠ THE REGISTRY CHECKS THIS NOW, AND THIS TEST NO LONGER HAS TO. The paragraph here used to
        // read "nothing in the registry checks this … cyc.Tests' EveryVerbInTheTreeIsReachable
        // catches it and reports an ArgumentException naming neither the provider nor the string".
        // ProviderRegistry.Build refuses it at silo start with a message naming both ends, derived
        // from what is registered rather than from a list — CliTokens.
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        registry.TryGetType(StorageAccounts.Type, out var registration).ShouldBeTrue();

        CliTokens.Collisions(
            registry.Types.Select(x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias))
        ).ShouldBeEmpty();

        // ⚠ A LITERAL, and deliberately so. The derived check says the short name collides with
        // nothing; only this says it is not the one word this namespace could not have.
        registration.Display.Alias.ShouldNotBe(
            "storage",
            "the short name equals the group name CyberCloud.Storage produces, so every `cyc` "
            + "invocation throws before it parses."
        );
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // ⚠ SchemaProperty checks its own DefaultJson against its own constraints at construction, so
        // a default outside its @range cannot reach here. What THAT check cannot see is the whole
        // body: this walks each default back into an otherwise-valid body and validates the result.
        foreach (var property in StorageAccounts.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(StorageAccounts.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            StorageAccounts.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue(
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
        StorageAccounts.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
        StorageAccounts.OptionalQuantityPattern.ShouldBeSameAs(KubeQuantity.OptionalPattern);
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecret() {
        // ⚠ docs/plan/12 § The pattern, once, piece 5 and SchemaProperty.Secret's own remarks: a
        // `Secret: true` body property masks the value on the generated surfaces and DOES NOTHING
        // ELSE — the write path stores it in plaintext, in grain state, which docs/plan/05 forbids for
        // a credential. The only secret this type has leaves through the listKeys action, whose
        // response schema is where the one `Secret: true` in this provider lives.
        StorageAccounts.Schema2026.Properties.ShouldNotContain(
            x => x.Secret,
            "a credential in the resource body is a credential in grain state, whatever the schema "
            + "says about masking it."
        );

        StorageAccounts.ListKeysResponse.Properties.Count(x => x.Secret).ShouldBe(1);
    }

    [Fact]
    public void EveryPresetHoldsTheOneToFourRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary defines s1 as "1:4". A table that drifted off the ratio
        // on one rung would make the family name a lie on that rung and nowhere else, which is the
        // kind of thing nobody notices because every other row reads correctly.
        foreach (var (preset, (cpu, memory)) in StorageAccounts.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(4m, $"'{preset}' is {cpu} to {memory}");
        }
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        // A preset the schema offers and the table does not is a body the API accepts and the meter
        // then refuses — a create that returns 500 for a value the schema advertised.
        StorageAccounts.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues
            .Order(StringComparer.Ordinal)
            .ShouldBe(StorageAccounts.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheReplicationEnumAndTheCodeTableAreTheSameSet() {
        StorageAccounts.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/replication")
            .AllowedValues
            .Order(StringComparer.Ordinal)
            .ShouldBe(StorageAccounts.ReplicationCodes.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryReplicationMemberRendersItsOwnThreeDigitCode() {
        // ⚠ THE DIGITS ARE A PLACEMENT AND NOT A COUNT — `xyz` is x extra copies in other data
        // centres, y in other racks, z on other servers in the same rack. Two members that rendered
        // the same code would be an API offering a choice the cluster cannot tell apart, and the
        // symptom is a durability promise nobody can audit.
        var codes = StorageAccounts.ReplicationCodes.Values.ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);

        foreach (var (member, code) in StorageAccounts.ReplicationCodes) {
            using var body = JsonDocument.Parse(WithReplication(StorageAccounts.Body(ClusterId), member));

            StorageAccounts.ReplicationCode(body.RootElement).ShouldBe(code, member);
            StorageAccounts.SeaweedJson("assets", body.RootElement)
                .ShouldContain("\"defaultReplication\":\"" + code + "\"", Case.Sensitive, member);
        }
    }

    [Fact]
    public void AnUnrecognisedReplicationMemberFallsBackToTwoCopiesRatherThanOne() {
        // ⚠ Unreachable from a validated body — AllowedValues closes it — and it is the fallback that
        // matters rather than the path to it. A typo, a renamed member or a body written at a future
        // api-version must not quietly become "one copy of everything": the failure would be silent,
        // durable and only visible after a disk was lost.
        using var body = JsonDocument.Parse(WithReplication(StorageAccounts.Body(ClusterId), "Nonsense"));

        StorageAccounts.ReplicationCode(body.RootElement).ShouldBe("001");
        StorageAccounts.ReplicationCode(body.RootElement).ShouldNotBe("000");
    }

    [Fact]
    public void TheImageIsAlwaysWrittenBecauseTheOperatorHasNoDefault() {
        // ⚠ api/v1/image.go: applyVersion(image, version) returns `image` unchanged when it is empty,
        // and every component's Image() goes through it. A Seaweed with no spec.image renders pods
        // with NO IMAGE, which fails per pod at the API server after the caller was told 202. This is
        // charts/managed/valkey's finding reached from the other side: there the operator's default
        // was the wrong licence, here there is no default at all.
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var spec = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", body.RootElement))!["spec"]!
            .AsObject();

        spec["image"]!.GetValue<string>().ShouldBe("chrislusf/seaweedfs:4.41");

        // ⚠ And spec.version stays UNSET. applyVersion rewrites the tag from spec.version when both
        // are present, so setting both would be two spellings of one fact with one silently winning.
        spec["version"].ShouldBeNull(
            "spec.version and a tagged spec.image are two spellings of the version, and applyVersion "
            + "lets the first overwrite the second."
        );
    }

    [Fact]
    public void ExactlyOneFilerIsRenderedAndItHasAPersistentStore() {
        // ⚠ controller_filer_configmap.go: with no spec.filer.config the operator mounts no filer.toml
        // at all, so the filer runs its EMBEDDED, PER-POD leveldb2 store. Two replicas would be two
        // divergent object namespaces behind one Service, with no error anywhere.
        //
        // ⚠ And persistence.enabled defaults to FALSE in the CRD, so an omitted block would put the
        // whole object namespace in the pod's writable layer — lost on the first restart.
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId, volumeServers: 16));

        var filer = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", body.RootElement))!["spec"]!
            ["filer"]!.AsObject();

        filer["replicas"]!.GetValue<int>().ShouldBe(1);
        filer["persistence"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

        // ⚠ Written out rather than inherited. The store lands in the container's working directory
        // (weed/command/filer.go defaults -defaultStoreDir to "."), the image says WORKDIR /data, and
        // PersistenceSpec.MountPath happens to default to "/data" too. Three facts in two repositories
        // that agree by coincidence; this is the one of them this platform controls.
        filer["persistence"]!["mountPath"]!.GetValue<string>().ShouldBe("/data");
    }

    [Fact]
    public void TheGatewayIsTheStandaloneOneAndNeverTheFilersEmbeddedS3() {
        // ⚠ controller_s3.go: the standalone gateway "is the preferred way to expose S3", the embedded
        // spec.filer.s3 path "is retained for backward compatibility but is deprecated", and "when
        // both are set the webhook rejects the CR". Rendering both would be a 422 on every create.
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var spec = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", body.RootElement))!["spec"]!
            .AsObject();

        spec["s3"].ShouldNotBeNull();
        (spec["filer"]!.AsObject()["s3"]).ShouldBeNull(
            "spec.filer.s3 and spec.s3 are both set, which the operator's validating webhook rejects "
            + "outright."
        );
    }

    [Fact]
    public void MonitoringOffRemovesTheMetricsPortAndWithItTheOperatorsScrapeObject() {
        // ⚠ docs/plan/12 § The pattern, once, piece 6's FIRST branch. controller_s3.go:
        // `if m.Spec.S3.MetricsPort != nil { ensureS3ServiceMonitor(m) }` with an else branch that
        // DELETES the ServiceMonitor. So the switch this provider renders is a port, and the object it
        // controls is somebody else's — which is the whole point of the corrected piece 6.
        using var on = JsonDocument.Parse(StorageAccounts.Body(ClusterId));
        using var off = JsonDocument.Parse(WithMonitoring(StorageAccounts.Body(ClusterId), false));

        StorageAccounts.SeaweedJson("assets", on.RootElement).ShouldContain("\"metricsPort\":9327");
        StorageAccounts.SeaweedJson("assets", off.RootElement).ShouldNotContain("metricsPort");

        // ⚠ And it is never spec.metricsAddress, which is the other thing in this CRD with "metrics"
        // in its name and is a PUSH gateway address the master forwards to the rest of the cluster.
        // Setting that one produces metrics nothing scrapes and no ServiceMonitor anywhere.
        StorageAccounts.SeaweedJson("assets", on.RootElement).ShouldNotContain("metricsAddress");
    }

    [Fact]
    public void TheEndpointNamesTheOperatorsOwnServiceRatherThanTheResource() {
        // ⚠ The gateway Service is `{name}-s3` — controller_s3.go's buildS3Service — and it is the
        // operator's object. An endpoint naming the resource itself would resolve to nothing, and
        // listKeys is the only place a caller ever learns the address.
        StorageAccounts.Endpoint("t-prod", "assets").ShouldBe("http://assets-s3.t-prod.svc:8333");
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

    static string WithReplication(string body, string replication) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["replication"] = replication;
        return node.ToJsonString();
    }

    static string WithMonitoring(string body, bool enabled) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["monitoring"] = new JsonObject { ["enabled"] = enabled };
        return node.ToJsonString();
    }
}
