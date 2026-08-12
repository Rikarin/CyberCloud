using CyberCloud.ResourceManager.Registry;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Tests;

/// <summary>
///     What only this provider can be wrong about in its declaration.
/// </summary>
/// <remarks>
///     The lifecycle — create, poll, read back, tag, lock, delete, drift, <b>and the parent ReBAC
///     edge</b> — is <c>CyberCloud.Providers.DBforMySQL.Conformance</c>'s, because those are the
///     <i>shared</i> suite's assertions and a per-provider copy is the drift docs/plan/03 § Providers
///     warns about.
/// </remarks>
public sealed class MariaDbDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheWayASiloBuildsOneAtStart() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a type with no api-version, on a reconciler naming a type nobody declared, and — since
        // ProviderBuilder.CheckClusterPlacement — on a RequiresCluster type whose schema does not
        // declare a required string at the cluster-id pointer. Running it is how a declaration mistake
        // becomes a startup failure rather than a 404 with nothing in the log.
        var registry = ProviderRegistry.Build([new MariaDbProvider()]);

        registry.Namespaces.ShouldContain(MariaDbServers.ProviderNamespace);
        registry.TryGetType(MariaDbServers.Type, out var registration).ShouldBeTrue();

        registration.ReconcilerType.ShouldBe(typeof(MariaDbServerReconciler));
        registration.RequiresCluster.ShouldBeTrue();
        registration.SupportsTags.ShouldBeTrue();
        registration.ApiVersions.Length.ShouldBe(1);
        registration.Chart.ShouldBe(MariaDbServers.ChartName);
        registration.TryGetAction(MariaDbServers.ListKeysAction, out var listKeys).ShouldBeTrue();

        // ⚠ A key export is not a read. ResourceManagerService passes an action's `secret` flag into
        // the authorization call precisely so docs/plan/07 § Consistency's fully-consistent path is
        // taken for it, and an action that declared `secret: false` would get the cached one.
        listKeys.Secret.ShouldBeTrue();
        listKeys.Permission.ShouldNotBe(registration.ReadPermission, "every viewer would hold the password");
    }

    [Fact]
    public void TheGeneratorFindsThisProviderAndItsOneResourceTypeRatherThanRunningCleanOverNothing() {
        // ⚠ A GENERATOR THAT GENERATES NOTHING EXITS 0. `./build.sh Generate` loads every assembly
        // under src/Providers/ through ProviderDiscovery and emits from what it finds; an assembly it
        // cannot see contributes no paths, no CLI verbs and no SDK models, and the build is green
        // because the four checked-in documents match the four it just produced from a smaller
        // registry. So the count is what is asserted, not the exit code.
        //
        // ⚠ Discovery reads THIS assembly's reference to the provider one, which is the same reflection
        // path the build step takes over a built .dll — so a provider class that lost its public
        // parameterless constructor, or stopped implementing IResourceProvider, fails here rather than
        // silently vanishing from four generated surfaces.
        var found = ProviderDiscovery.FromAssembly(typeof(MariaDbProvider).Assembly);

        found.Length.ShouldBe(1, "src/Providers discovery no longer finds exactly this one provider");
        found[0].ProviderNamespace.ShouldBe("CyberCloud.DBforMySQL");

        var registry = ProviderRegistry.Build(found);

        registry.Types.Length.ShouldBe(
            1,
            "the registry built from the discovered assembly carries a different number of resource "
            + "types than the one this provider declares"
        );
    }

    [Fact]
    public void TheProviderNamespaceAndTypeKeepTheirExactCasingThroughTheRegistryAndAResourcePath() {
        // ⚠ ONE CHARACTER OF CASING, PLACE ONE OF THREE. A `resourcegroup`/`resourceGroup` mismatch was
        // once failing every create in the platform and surfaced as a 404 whose reason was in a log
        // line.
        //
        // ⚠ THIS NAMESPACE IS THE WORST SHAPE IN THE TREE AND IT IS NOT CLOSE. `DBforMySQL` carries
        // FOUR case transitions in ten characters — D-B-f-o-r-M-y-S-Q-L — and every one of them is a
        // spelling somebody writes differently in good faith: `DbForMySql` is what an IDE's
        // "normalise" refactor produces, `DBForMySQL` is what a reader who capitalises acronyms
        // consistently writes, and `DBforMysql` is what somebody who knows MySQL's own branding
        // writes. The sibling `DBforPostgreSQL` has three. Every one of them routes to nothing.
        var registry = ProviderRegistry.Build([new MariaDbProvider()]);

        // ⚠ LITERALS ON BOTH SIDES. Asserting `registry.Namespaces.ShouldContain(
        // MariaDbServers.ProviderNamespace)` compares the constant with itself and stays green through
        // any rename — which is exactly how a casing sabotage survived on an earlier provider.
        registry.Namespaces.ShouldContain("CyberCloud.DBforMySQL");
        MariaDbServers.Type.ToString().ShouldBe("CyberCloud.DBforMySQL/servers");

        var address = Address("casing");
        ResourceId.TryParsePath(address.Path, out var parsed).ShouldBeTrue(address.Path);

        parsed.Type.Namespace.ShouldBe("CyberCloud.DBforMySQL");
        parsed.Type.Type.ShouldBe("servers");
        registry.TryGetType(parsed.Type, out _).ShouldBeTrue(
            "a path that round-tripped through the gateway's own parser no longer finds the type"
        );
    }

    [Fact]
    public void TheResourceTypeLabelIsTheValueTheConformanceManifestDeclares() {
        // ⚠ charts/managed/mariadb/conformance.yaml pins this literal:
        //   cybercloud.io/resource-type: cybercloud.dbformysql_servers
        // A `/` is not a legal label VALUE character, so the value is lower-cased with `/` replaced by
        // `_` — and nothing in the build compares the manifest's literal to what KubeLabels derives.
        // If the two disagree, every object this provider applies is labelled with something the
        // conformance suite will not find, which breaks orphan detection and billing attribution
        // rather than failing anything.
        KubeLabels.ResourceTypeValue(MariaDbServers.Type).ShouldBe("cybercloud.dbformysql_servers");
    }

    [Fact]
    public void TheFieldManagerIsTheOneAdr013DerivesFromTheNamespace() {
        // ADR-013 wants a stable field manager per provider, and the builder derives
        // cybercloud/{namespace, lower-cased}. The constant in .Contracts documents it; this asserts
        // the derivation still produces it, so a namespace rename cannot silently change the manager
        // and hand every field we own to a "new" manager on the next apply.
        using var desired = JsonDocument.Parse(MariaDbServers.Body(Guid.NewGuid()));

        var command = KubeCommand.For(new RecordingConnection())
            .WithTenantId(Guid.NewGuid())
            .WithResourceId(Address("field-manager"))
            .InNamespace("ns")
            .WithKind(MariaDbServers.ServerKind)
            .ObjectJson(MariaDbServers.ServerJson("field-manager", desired.RootElement))
            .Build();

        // ⚠ A LITERAL, for the reason the casing test gives. `MariaDbServers.FieldManager` on both
        // sides would be the constant compared with itself.
        command.FieldManager.ShouldBe("cybercloud/cybercloud.dbformysql");
    }

    [Theory]
    // ⚠ EVERY GROUP KEY IN THE TREE, AS A LITERAL — including this provider's own. CliEmitter.GroupOf
    // takes the provider namespace's last segment and lower-cases it, so `CyberCloud.DBforMySQL` is
    // already the group `dbformysql`. System.CommandLine's ValidTokens builds ONE dictionary over
    // every command token AND every alias in the tree, so a short name equal to any group key throws
    // `An item with the same key has already been added` on the first parse of ANY command line,
    // before any verb runs. CyberCloud.Storage nearly shipped exactly that.
    //
    // ⚠ Deriving these from the registry would compare the emitter with itself. They are typed out.
    [InlineData("dbformysql")]
    [InlineData("dbforpostgresql")]
    [InlineData("cache")]
    [InlineData("messaging")]
    [InlineData("storage")]
    [InlineData("sample")]
    public void TheShortNameIsNotAnyProvidersGroupKey(string groupKey) {
        MariaDbProvider.ShortName.ShouldNotBe(
            groupKey,
            $"the short name equals the CLI group key '{groupKey}', so every `cyc` invocation throws "
            + "before it parses."
        );
    }

    [Fact]
    public void TheShortNameNamesTheEngineRatherThanTheResourceTypesSpelling() {
        // ⚠ THE RENAME THAT WOULD LOOK LIKE A CONSISTENCY FIX. The namespace says DBforMySQL, so
        // `mysql` is the "obvious" alias — and `cyc mysql server create` is a sentence claiming this
        // platform runs MySQL, in the place a tenant reads most often. docs/plan/12 line 310 makes the
        // compatibility claim this row's obligation and the alias is part of the surface it applies
        // to. The long form `dbformysql server` still exists and keeps Azure parity; the alias is a
        // second name for it, not a replacement.
        MariaDbProvider.ShortName.ShouldBe("mariadb");
        MariaDbProvider.ShortName.ShouldNotBe("mysql");
    }

    /// <remarks>
    ///     ⚠ Each body is valid in every respect but the one under test. Every property carries a
    ///     format, a pattern or a closed set, so a fixture with a placeholder cluster id would fail on
    ///     that first and the assertion would pass for the wrong reason — <see cref="Error.Target" />
    ///     is the FIRST problem found.
    /// </remarks>
    [Theory]
    [InlineData("""{"properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","version":"11.4","storage":{"size":"20Gi"}}}""", "/location")]
    [InlineData("""{"location":"eu-central","properties":{"version":"11.4","storage":{"size":"20Gi"}}}""", "/properties/clusterId")]
    [InlineData("""{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","storage":{"size":"20Gi"}}}""", "/properties/version")]
    [InlineData("""{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","version":"11.4"}}""", "/properties/storage/size")]
    public void EveryRequiredPropertyIsActuallyRequired(string body, string expectedTarget) {
        using var document = JsonDocument.Parse(body);

        var validated = MariaDbServers.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Target.ShouldBe(expectedTarget);
    }

    /// <summary>
    ///     Values the API must refuse, at the pointer that must refuse them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The alternative to each of these is a body that validates and then produces a MariaDB
    ///     the API server or the operator refuses AFTER the caller was told <c>202</c>.</b> A tenant
    ///     reads that as "the platform accepted my request and lost it", and the reason is in an
    ///     operator's event stream rather than in the operation's error.
    /// </remarks>
    [Theory]
    [InlineData("/properties/storage/size", "\"20 gigabytes\"")]
    [InlineData("/properties/storage/size", "\"-4Gi\"")]
    [InlineData("/properties/sizing/cpu", "\"half a core\"")]
    [InlineData("/properties/sizing/memory", "\"4 GB\"")]
    // ⚠ `Replication` is the member docs/plan/12 line 309 names and the operator marks alpha. Accepting
    // it would be a topology the API takes and the reconciler renders nothing for.
    [InlineData("/properties/highAvailability", "\"Replication\"")]
    [InlineData("/properties/highAvailability", "\"galera\"")]
    // ⚠ `8.0` is a MySQL version, and it is the single most likely wrong value on this whole type:
    // somebody reads `DBforMySQL` and asks for the MySQL they think they are getting. Refusing it at
    // the API is the compatibility claim enforced rather than merely stated.
    [InlineData("/properties/version", "\"8.0\"")]
    [InlineData("/properties/version", "\"11.5\"")]
    // ⚠ On Linux a database is a directory; `App` and `app` would be two of them.
    [InlineData("/properties/bootstrap/database", "\"App\"")]
    [InlineData("/properties/bootstrap/database", "\"1st\"")]
    [InlineData("/properties/bootstrap/username", "\"App\"")]
    public void AValueThatWouldReachTheClusterAndMeanNothingIsRefusedHere(string jsonPointer, string literal) {
        using var document = JsonDocument.Parse(BodyWith(jsonPointer, literal));

        var validated = MariaDbServers.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue($"'{jsonPointer}' accepted {literal}");
        validated.Error!.Target.ShouldBe(jsonPointer);
    }

    [Fact]
    public void AnAccountNameLongerThanMySqlAcceptsIsRefusedEvenThoughMariaDbWouldTakeIt() {
        // ⚠ THE SUBSET TABLE APPLIED TO A NUMBER, AND IT IS A CONSTRAINT THIS PLATFORM CHOSE. MariaDB
        // accepts a longer account name than MySQL does. Taking the larger of the two would let a
        // tenant create an account whose name their own MySQL tooling cannot reproduce — a
        // compatibility break introduced HERE rather than inherited from the engine, which is the one
        // kind this row has no excuse for.
        using var document = JsonDocument.Parse(
            BodyWith("/properties/bootstrap/username", "\"" + new string('a', 33) + "\"")
        );

        var validated = MariaDbServers.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Target.ShouldBe("/properties/bootstrap/username");

        // And the boundary itself is accepted, so the constraint is a cap rather than an off-by-one.
        using var atTheLimit = JsonDocument.Parse(
            BodyWith("/properties/bootstrap/username", "\"" + new string('a', 32) + "\"")
        );

        MariaDbServers.Schema2026.Validate(atTheLimit.RootElement).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/properties/storage/size", "\"20Gi\"")]
    [InlineData("/properties/sizing/cpu", "\"500m\"")]
    [InlineData("/properties/sizing/cpu", "\"2\"")]
    [InlineData("/properties/sizing/cpu", "\"\"")]
    [InlineData("/properties/sizing/memory", "\"4Gi\"")]
    [InlineData("/properties/sizing/memory", "\"\"")]
    [InlineData("/properties/highAvailability", "\"None\"")]
    [InlineData("/properties/version", "\"10.11\"")]
    [InlineData("/properties/version", "\"11.8\"")]
    [InlineData("/properties/bootstrap/database", "\"orders_2026\"")]
    [InlineData("/properties/bootstrap/username", "\"_svc\"")]
    public void TheValuesTheChartWritesAsDefaultsAndExamplesAreAccepted(string jsonPointer, string literal) {
        // ⚠ The other half, and the half that catches an over-tight constraint. Every literal here is
        // one charts/managed/mariadb/values.yaml either defaults to or names in a description or an
        // enum, so a pattern or a closed set that refused one would be refusing the chart's own
        // documented values.
        using var document = JsonDocument.Parse(BodyWith(jsonPointer, literal));

        MariaDbServers.Schema2026.Validate(document.RootElement).IsSuccess.ShouldBeTrue(
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

        foreach (var property in MariaDbServers.Schema2026.Properties) {
            if (property.DefaultJson.Length == 0) {
                continue;
            }

            Place(body, property.JsonPointer, JsonNode.Parse(property.DefaultJson));
        }

        using var document = JsonDocument.Parse(body.ToJsonString());
        var validated = MariaDbServers.Schema2026.Validate(document.RootElement);

        validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
    }

    [Fact]
    public void TheTopologyAxisIsVisibleImmutableAndOffersOnlyWhatTheOperatorDoesWell() {
        // ⚠ TWO MEMBERS WHERE docs/plan/12 NAMES THREE, ASSERTED SO IT CANNOT BE "COMPLETED" INTO
        // THREE. Line 309 says "Galera for HA, or async replication"; mariadb-operator's own
        // documentation marks spec.replication alpha and recommends Galera for production. A
        // Replication member would be a topology the API accepts and this provider renders nothing
        // for. conformance.yaml § owed, async-replication-topology, carries it.
        var topology = MariaDbServers.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/highAvailability");

        topology.AllowedValues.ShouldBe(["None", "Galera"]);
        topology.Immutable.ShouldBeTrue("the connection string moves with the topology");
        topology.DefaultJson.ShouldBe("\"Galera\"");
    }

    [Fact]
    public void ThereIsNoReplicaCountProperty() {
        // ⚠ THE ABSENCE, PINNED, BECAUSE ADDING IT IS THE OBVIOUS IMPROVEMENT. The CRD refuses an even
        // replica count under Galera — "An odd number of MariaDB instances … is required to avoid
        // split brain" — and SchemaProperty can express Minimum and Maximum and not "odd". A
        // `/properties/replicas` of 1..5 would validate here and produce a CR the API server refuses
        // for 2 and 4, AFTER the caller was told 202. See MariaDbServers.GaleraReplicas.
        MariaDbServers.Schema2026.Properties
            .ShouldNotContain(x => x.JsonPointer.Contains("replica", StringComparison.OrdinalIgnoreCase));

        // And the count the topology implies is still the one quota and the CR agree on.
        using var ha = JsonDocument.Parse(MariaDbServers.Body(Guid.NewGuid()));
        using var single = JsonDocument.Parse(MariaDbServers.Body(Guid.NewGuid(), highAvailability: "None"));

        MariaDbServers.Replicas(ha.RootElement).ShouldBe(3);
        MariaDbServers.Replicas(single.RootElement).ShouldBe(1);
    }

    internal static ResourceId Address(string name) =>
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            MariaDbServers.Type,
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
            MariaDbServers.Body(Guid.Parse("7b6a5c4d-0000-4000-8000-000000000001"))
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
