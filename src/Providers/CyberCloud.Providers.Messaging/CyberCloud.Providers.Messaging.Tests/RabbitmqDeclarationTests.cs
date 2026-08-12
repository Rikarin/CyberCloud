using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The RabbitMQ declaration, checked the way a silo checks it at start, plus the facts that live
///     in more than one file and have to agree.
/// </summary>
public sealed class RabbitmqDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000009");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a DUPLICATE TYPE, on a type with no api-version, on a duplicate SHORT NAME, and on a
        // RequiresCluster type whose schema does not declare the pointer as a required string.
        //
        // ⚠ THE TWO IN THE MIDDLE MATTER MORE ON THE THIRD TYPE THAN THEY DID ON THE SECOND. A third
        // type has TWO existing siblings to collide with on both axes rather than one, and both
        // collisions are silo-start failures rather than compile ones.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);

        registry.TryGetType(RabbitmqClusters.Type, out var registration).ShouldBeTrue();
        registry.TryGetType(KafkaClusters.Type, out _).ShouldBeTrue(
            "adding the third type removed the first one from the registry"
        );

        registry.TryGetType(NatsClusters.Type, out _).ShouldBeTrue(
            "adding the third type removed the second one from the registry"
        );

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(RabbitmqClusters.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(RabbitmqClusters.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(RabbitmqClusterReconciler));
        registration.Actions.ShouldContain(x => x.Name == RabbitmqClusters.ListKeysAction && x.Secret);

        // ⚠ `listKeys` does NOT share the read permission. docs/plan/07 § Consistency puts a key
        // export in the fully-consistent row by name; sharing `read` would make every viewer of a
        // cluster a holder of its credentials, and the change that would do it is one word.
        registration.Actions.Single(x => x.Name == RabbitmqClusters.ListKeysAction)
            .Permission.ShouldNotBe(registration.ReadPermission);
    }

    [Fact]
    public void TheShortNameIsNoneOfTheCliGroupNamesTheNamespacesAlreadyProduce() {
        // ⚠ FAILURE CLASS (a), AND THE ONE THE REGISTRY ITSELF CANNOT CATCH. ProviderRegistry.Build
        // refuses a duplicate SHORT NAME and never compares one to a GROUP name — and
        // CliEmitter.GroupOf derives a group from the provider namespace's last segment, lower-cased.
        // System.CommandLine's ValidTokens is ONE DICTIONARY OVER THE WHOLE TREE, so a short name
        // equal to any group would make EVERY `cyc` parse throw, not just this type's. A provider
        // nearly shipped as `storage` for exactly this reason.
        //
        // ⚠ EVERY EXPECTATION HERE IS A TYPED-OUT LITERAL. Deriving the group names from the same
        // ProviderNamespace constants the emitter reads is the mistake a previous provider's casing
        // sabotage survived: two things derived from one constant agree however that constant is
        // spelled. These five are the CLI groups in the tree today, written by hand.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        registry.TryGetType(RabbitmqClusters.Type, out var registration).ShouldBeTrue();

        var alias = registration.Display.Alias;
        alias.ShouldBe("rabbitmq");

        foreach (var group in new[] { "messaging", "dbforpostgresql", "cache", "storage", "sample" }) {
            alias.ShouldNotBe(
                group,
                $"the short name is '{group}', which CliEmitter.GroupOf already derives as a top-level "
                + "CLI group from a provider namespace. System.CommandLine keeps one ValidTokens "
                + "dictionary over the whole verb tree, so this would throw on every `cyc` invocation "
                + "rather than only on this type's."
            );
        }

        // ⚠ And the group this type's OWN namespace produces, read off the emitted tree rather than
        // out of the list above — because the list above is the thing that goes stale when a sibling
        // provider lands, and this half cannot.
        var cli = CliEmitter.Emit(Document());
        foreach (var group in cli["groups"]!.AsObject().Select(x => x.Key)) {
            alias.ShouldNotBe(group, $"the short name collides with the emitted CLI group '{group}'");
        }
    }

    [Fact]
    public void TheFourMetersAreDeclaredAndEachSaysWhatItReads() {
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        registry.TryGetType(RabbitmqClusters.Type, out var registration).ShouldBeTrue();

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
                RabbitmqClusters.Schema2026.Declares(pointer).ShouldBeTrue(
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
        // body: this walks each default back into an otherwise-valid body and validates the result.
        //
        // ⚠ IT MATTERS MORE ON THIS TYPE THAN ON ITS SIBLINGS, because one of the defaults is the
        // reason the row exists. `queues.defaultType` defaulting to anything other than `quorum`
        // would be a cluster that replicates nothing while every other file still says it does.
        foreach (var property in RabbitmqClusters.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(RabbitmqClusters.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            RabbitmqClusters.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue(
                $"the declared default for '{property.JsonPointer}' does not validate inside an "
                + "otherwise-valid body."
            );
        }
    }

    [Fact]
    public void TheDefaultQueueTypeIsQuorumInTheSchemaAndInTheReaderThatFallsBackToIt() {
        // ⚠ THE ONE ASSERTION THIS WHOLE ROW EXISTS FOR, MADE AGAINST BOTH HALVES OF THE DEFAULT.
        // SchemaProperty.DefaultJson's remarks say the write path stores a body AS SENT and the
        // validator does not substitute — so an absent property means whatever the READER's fallback
        // says, and the schema's declared default is only what the API tells a caller. Two places,
        // and they can disagree silently: a body that omits `queues` would render a classic-queue
        // cluster while the portal form showed `quorum`.
        //
        // ⚠ AND THE SCHEMA HALF IS A LITERAL. Reading it off RabbitmqClusters.QueueTypes or off a
        // constant the renderer also reads would compare the schema to itself.
        RabbitmqClusters.Schema2026.Properties
            .Single(x => x.JsonPointer == RabbitmqClusters.DefaultQueueTypePointer)
            .DefaultJson.ShouldBe("\"quorum\"");

        // The reader's fallback, exercised through a body that declares no `queues` block at all.
        using var bare = JsonDocument.Parse(
            new JsonObject {
                ["location"] = "eu-central",
                ["properties"] = new JsonObject {
                    ["clusterId"] = ClusterId.ToString("D"),
                    ["version"] = "4.1",
                    ["nodes"] = 3,
                    ["storage"] = new JsonObject { ["size"] = "20Gi" }
                }
            }.ToJsonString()
        );

        RabbitmqClusters.DefaultQueueType(bare.RootElement).ShouldBe("quorum");
        RabbitmqClusters.AdditionalConfig(bare.RootElement)
            .ShouldContain("default_queue_type = quorum", Case.Sensitive);
    }

    [Fact]
    public void TheConfigLineReachesTheRenderedSpecAndNotSomeOtherField() {
        // ⚠ `default_queue_type` HAS NO MEMBER ON THE CRD, so the row's headline setting travels as a
        // line inside spec.rabbitmq.additionalConfig. That is worth asserting at the rendered
        // document rather than only at the string builder: a refactor that moved the block to
        // `advancedConfig` (Erlang terms, a different file entirely) or to `envConfig` would compile,
        // apply cleanly, and start a broker that ignored it.
        foreach (var queueType in RabbitmqClusters.QueueTypes) {
            using var body = JsonDocument.Parse(
                RabbitmqClusters.Body(ClusterId, defaultQueueType: queueType)
            );

            var spec = JsonNode.Parse(RabbitmqClusters.ClusterJson("events", body.RootElement))!["spec"]!
                .AsObject();

            spec["rabbitmq"]!["additionalConfig"]!.GetValue<string>()
                .ShouldContain("default_queue_type = " + queueType, Case.Sensitive, queueType);

            spec["rabbitmq"]!.AsObject().ContainsKey("advancedConfig").ShouldBeFalse(
                "the fragment moved to advancedConfig, which RabbitMQ reads as Erlang terms from a "
                + "different file — the broker would start and ignore it."
            );
        }
    }

    [Fact]
    public void TheConfigFragmentIsValidIniAndCarriesNoCredential() {
        // ⚠ TWO SEPARATE FAILURES IN ONE BLOCK. The operator PARSES this string looking for
        // default_user, default_pass and auth_mechanisms; a block it cannot parse fails the reconcile
        // rather than being ignored, so every line has to be `key = value`. And writing a credential
        // here would put a plaintext password into the resource body and into grain state, which
        // docs/plan/05 forbids outright — the operator generates its own into <name>-default-user.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var config = RabbitmqClusters.AdditionalConfig(body.RootElement);

        foreach (var line in config.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            line.ShouldContain(" = ", Case.Sensitive, $"'{line}' is not an INI assignment");
            line.Split(" = ").Length.ShouldBe(2, line);
        }

        foreach (var forbidden in new[] { "default_user", "default_pass" }) {
            config.ShouldNotContain(
                forbidden,
                Case.Sensitive,
                $"'{forbidden}' is in the rendered config. That is a plaintext credential in the "
                + "resource's desired body and in grain state, and it takes the credential out of the "
                + "operator's hands for no gain."
            );
        }
    }

    [Fact]
    public void TheImageIsWrittenExplicitlyBecauseAWebhookWouldOtherwiseChooseIt() {
        // ⚠ THE FIELD THAT LOOKS REDUNDANT AND IS NOT. This operator's MUTATING WEBHOOK fills
        // spec.image from its own build-time default when the field is unset — it is not a CRD
        // `default:`, it is admission. So omitting the field is not "the tenant's version wins", it
        // is whatever RabbitMQ the operator was compiled against, and /properties/version would be a
        // property that controls nothing.
        //
        // ⚠ And the `-management` suffix is load-bearing rather than cosmetic: rabbitmq_management is
        // in the operator's requiredPlugins, and the bare rabbitmq:{version} image ships the plugin
        // unenabled, so first boot would enable a plugin on every node instead of starting.
        foreach (var version in new[] { "4.0", "4.1" }) {
            using var body = JsonDocument.Parse(Overridden(
                RabbitmqClusters.Body(ClusterId),
                "/properties/version",
                "\"" + version + "\""
            ));

            var spec = JsonNode.Parse(RabbitmqClusters.ClusterJson("events", body.RootElement))!["spec"]!
                .AsObject();

            spec["image"]!.GetValue<string>().ShouldBe("rabbitmq:" + version + "-management");
        }
    }

    [Fact]
    public void TheResourcesBlockIsAlwaysWrittenBecauseTheCrdDefaultsItToBurstable() {
        // ⚠ THE DIFFERENCE BETWEEN THIS ROW AND ITS TWO SIBLINGS, IN ONE ASSERTION. On the Kafka and
        // NATS types an absent `resources` block means the workload gets no requests and no limits —
        // BestEffort, and visible. This CRD DEFAULTS spec.resources to
        // {limits: {cpu: 2000m, memory: 2Gi}, requests: {cpu: 1000m, memory: 2Gi}}, which is
        // BURSTABLE at quantities nobody chose, while the preset name still says c1 — and
        // docs/plan/12 § Sizing vocabulary defines c1 as GUARANTEED.
        //
        // Guaranteed is a QoS class you get by setting requests equal to limits, so both halves are
        // asserted: that the block is there at all, and that the two sides match.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var resources = JsonNode.Parse(RabbitmqClusters.ClusterJson("events", body.RootElement))!["spec"]!
            ["resources"]!.AsObject();

        resources.ShouldNotBeNull(
            "no resources block was rendered, so the API server's own default applies — a Burstable "
            + "pod at 1000m/2000m that the preset name calls guaranteed."
        );

        foreach (var quantity in new[] { "cpu", "memory" }) {
            resources["requests"]![quantity]!.GetValue<string>()
                .ShouldBe(resources["limits"]![quantity]!.GetValue<string>(), quantity);
        }

        resources["requests"]!["cpu"]!.GetValue<string>().ShouldBe("1");
        resources["requests"]!["memory"]!.GetValue<string>().ShouldBe("2Gi");
    }

    [Fact]
    public void TheStorageSizeIsRenderedAsAStringBecauseTheCrdTakesEither() {
        // ⚠ spec.persistence.storage IS x-kubernetes-int-or-string. `20` and `"20Gi"` are both valid
        // and mean wildly different things — twenty BYTES against twenty gibibytes — and a number
        // does not round-trip as the string Matches compares, so a rendered number would be a
        // resource that never converges AND a volume four hundred million times too small.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId, storageSize: "50Gi"));

        var storage = JsonNode.Parse(RabbitmqClusters.ClusterJson("events", body.RootElement))!["spec"]!
            ["persistence"]!["storage"]!;

        storage.GetValueKind().ShouldBe(JsonValueKind.String);
        storage.GetValue<string>().ShouldBe("50Gi");
    }

    [Fact]
    public void TheQuantityGrammarIsThePlatformsRatherThanAFifthCopyOfIt() {
        // ⚠ KubeQuantity's remarks: "There is exactly one of these in the platform and there must
        // stay exactly one", and they record that the last provider to keep its own copy of the
        // grammar got a second PARSER written next to it — and that consolidating the four found a
        // live defect where a one-byte limit floored to `maxmemory 0`, which means UNLIMITED.
        // QuantityParserTests fails if a fresh copy or a second suffix table appears. This asserts
        // the identity is a REFERENCE rather than a matching string — a matching string is what all
        // four of those were, and it is what a copy would still be.
        RabbitmqClusters.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
        RabbitmqClusters.OptionalQuantityPattern.ShouldBeSameAs(KubeQuantity.OptionalPattern);
    }

    [Fact]
    public void EveryPresetHoldsTheOneToTwoRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary defines c1 as "1:2, guaranteed". A table that drifted
        // off the ratio on one rung would make the family name a lie on that rung and nowhere else.
        foreach (var (preset, (cpu, memory)) in RabbitmqClusters.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(2m, $"'{preset}' is {cpu} to {memory}");
        }
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        // A preset the schema offers and the table does not is a body the API accepts and the meter
        // then refuses — a create that returns 500 for a value the schema advertised. ⚠ And on THIS
        // type it is worse than that: an unresolved preset renders no resources block, which this
        // CRD replaces with a Burstable default rather than leaving empty.
        var declared = RabbitmqClusters.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues;

        declared.Order(StringComparer.Ordinal)
            .ShouldBe(RabbitmqClusters.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePluginEnumIsClosedAndTheRenderedListIsSortedAndDeduplicated() {
        // ⚠ TWO FACTS, AND THE SECOND IS CLAUSE 1. `spec.rabbitmq.additionalPlugins` takes any
        // string, and the operator adds a Service port per plugin it RECOGNISES — so a typo is
        // enabled-and-absent, a healthy cluster that does not speak the protocol asked for. The enum
        // is what closes that.
        //
        // Sorting is idempotency: two bodies asking for the same set in different orders must render
        // the same document, or a reconciler alternating between them writes on every pass.
        var declared = RabbitmqClusters.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/plugins/additional");

        declared.ElementKind.ShouldBe(SchemaKind.Text);
        declared.AllowedValues.Order(StringComparer.Ordinal)
            .ShouldBe(RabbitmqClusters.AdditionalPlugins.Order(StringComparer.Ordinal));

        using var forward = JsonDocument.Parse(WithPlugins("rabbitmq_stream", "rabbitmq_mqtt"));
        using var reverse = JsonDocument.Parse(WithPlugins("rabbitmq_mqtt", "rabbitmq_stream", "rabbitmq_mqtt"));

        RabbitmqClusters.Plugins(forward.RootElement)
            .ShouldBe(RabbitmqClusters.Plugins(reverse.RootElement));

        RabbitmqClusters.ClusterJson("events", forward.RootElement)
            .ShouldBe(RabbitmqClusters.ClusterJson("events", reverse.RootElement));
    }

    /// <summary>A body asking for a list of plugins.</summary>
    string WithPlugins(params string[] plugins) {
        var node = JsonNode.Parse(RabbitmqClusters.Body(ClusterId))!.AsObject();
        var listed = new JsonArray();
        foreach (var plugin in plugins) {
            listed.Add(plugin);
        }

        node["properties"]!.AsObject()["plugins"] = new JsonObject { ["additional"] = listed };
        return node.ToJsonString();
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        return OpenApiEmitter.Emit(registry, OpenApiEmitter.ApiVersionsOf(registry).Single());
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
