using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DocumentDB.Tests;

/// <summary>
///     The document-database declaration, checked the way a silo checks it at start, plus the facts
///     that live in more than one file and have to agree.
/// </summary>
public sealed class DocumentDbDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("dddddddd-0000-4000-8000-000000000006");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a duplicate type, on a type with no api-version, on a duplicate short name, and on a
        // RequiresCluster type whose schema does not declare the pointer as a required string.
        var registry = ProviderRegistry.Build([new DocumentDbProvider()]);

        registry.TryGetType(DocumentDbAccounts.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(DocumentDbAccounts.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(DocumentDbAccounts.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(DocumentDbAccountReconciler));
        registration.Actions.ShouldContain(x => x.Name == DocumentDbAccounts.ListKeysAction && x.Secret);

        // ⚠ `listKeys` does NOT share the read permission. docs/plan/07 § Consistency puts a key
        // export in the fully-consistent row by name, and what this action returns is a PostgreSQL
        // role's password — the credential FerretDB forwards every client's login to. Sharing `read`
        // would make every viewer of an account a holder of its database password.
        registration.Actions.Single(x => x.Name == DocumentDbAccounts.ListKeysAction)
            .Permission.ShouldNotBe(registration.ReadPermission);
    }

    [Fact]
    public void TheShortNameIsNeitherTheGroupNameNorTheTrademark() {
        // ⚠ TWO INDEPENDENT REASONS THE OBVIOUS SHORT NAMES ARE WRONG, AND ONLY ONE OF THEM IS
        // MECHANICAL.
        //
        // `documentdb` is the CLI GROUP this namespace already produces — CliEmitter.GroupOf is the
        // provider namespace's last segment, lower-cased — and System.CommandLine's ValidTokens
        // builds ONE dictionary over every command token AND every alias in the whole tree, so a
        // group and an alias sharing a string throw `An item with the same key has already been
        // added. Key: documentdb` on the first parse of ANY command line. That is
        // CyberCloud.Storage/accounts' finding; this is the SECOND namespace it applies to, which is
        // what turns a near miss into a rule: whenever docs/plan/21 § Grammar's alias table would
        // spell the alias the way the namespace is spelled, it collides.
        //
        // `mongo` and `mongodb` are refused on ADR-011 grounds. This service is not MongoDB, a short
        // name is what a human types and what every example shows, and docs/plan/12 says selling it
        // as MongoDB "produces a churn event at the first $lookup".
        //
        // ⚠ NOTHING IN THE REGISTRY CHECKS EITHER. ProviderRegistry.Build refuses a DUPLICATE short
        // name and does not compare one against a group name; DerivedSurfaces.CliProblems does not
        // either. cyc.Tests' EveryVerbInTheTreeIsReachable catches the first as an ArgumentException
        // out of System.CommandLine naming neither the provider nor the string, and nothing catches
        // the second at all.
        var registry = ProviderRegistry.Build([new DocumentDbProvider()]);
        registry.TryGetType(DocumentDbAccounts.Type, out var registration).ShouldBeTrue();

        // ⚠ LITERALS, not `ProviderNamespace.Split('.')[^1].ToLowerInvariant()`. Deriving the group
        // name the same way the emitter does would compare the emitter to itself, which is the shape
        // that let a casing sabotage stay green on an earlier provider.
        registration.Display.Alias.ShouldNotBe(
            "documentdb",
            "the short name equals the group name CyberCloud.DocumentDB produces, so every `cyc` "
            + "invocation throws before it parses."
        );

        foreach (var trademark in new[] { "mongo", "mongodb" }) {
            registration.Display.Alias.ShouldNotBe(
                trademark,
                "ADR-011: real MongoDB is SSPL and is not what this service runs. The short name is "
                + "the string a human types most often, so it is the last place to imply otherwise."
            );
        }

        registration.Display.Alias.ShouldBe("docdb");
    }

    [Fact]
    public void TheCompatibilityStatementNamesTransactionsAndIsInTheSummary() {
        // ⚠ docs/plan/12 REQUIRES THIS ROW TO PUBLISH A SUPPORTED-SUBSET STATEMENT AND A PRODUCT PAGE
        // IS NOT A PLACE A BUILD CAN FAIL. "Selling it as 'MongoDB' produces a churn event at the
        // first $lookup. Selling it as 'MongoDB-compatible document database, here is exactly what
        // works' produces a happy customer with a smaller use case." The summary is what
        // `cyc documentdb accounts --help` prints and what the portal card shows, so that is where
        // the statement has to be.
        var registry = ProviderRegistry.Build([new DocumentDbProvider()]);
        registry.TryGetType(DocumentDbAccounts.Type, out var registration).ShouldBeTrue();

        // ⚠ Literals. Asserting `summary.ShouldContain(CompatibilityStatement)` would compare the
        // constant to itself and stay green if somebody emptied it.
        registration.Display.Summary.ShouldContain("not MongoDB", Case.Sensitive);
        registration.Display.Summary.ShouldContain("transactions are not supported", Case.Sensitive);

        // And the machine-readable half, which is what a portal would render as a table.
        DocumentDbAccounts.UnsupportedCommands.ShouldContain("commitTransaction");
        DocumentDbAccounts.UnsupportedCommands.ShouldContain("abortTransaction");

        // ⚠ Change streams are deliberately NOT here — upstream's compatibility page has no row for
        // them either way, so listing them would repeat a claim this provider could not check. See
        // charts/managed/ferretdb/conformance.yaml § owed, `change-streams-are-unverified`. This
        // assertion is what makes the omission a decision rather than a gap.
        DocumentDbAccounts.UnsupportedCommands.ShouldNotContain("watch");
    }

    [Fact]
    public void TheFourMetersAreDeclaredAndEachSaysWhatItReads() {
        var registry = ProviderRegistry.Build([new DocumentDbProvider()]);
        registry.TryGetType(DocumentDbAccounts.Type, out var registration).ShouldBeTrue();

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
                DocumentDbAccounts.Schema2026.Declares(pointer).ShouldBeTrue(
                    $"the {meter.Meter} derivation declares it reads '{pointer}', which this "
                    + "api-version's schema does not declare. A read set that names a property the "
                    + "schema dropped is what an api-version bump has to be diffed against."
                );
            }
        }

        // ⚠ AND THE GATEWAY POINTER IS IN THE READ SETS OF THE TWO METERS THAT NEED IT. This is the
        // half a `Reads` declaration exists for: the derivation is a delegate nothing sandboxes, so
        // the only thing that makes the claim checkable is a reviewer — or this — noting that a
        // formula summing the FerretDB pods must say it reads their count.
        foreach (var meter in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb }) {
            registration.Meters.Single(x => x.Meter == meter).Derivation!.Reads.ShouldContain(
                "/properties/gateway/replicas",
                meter.ToString()
            );
        }
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // ⚠ SchemaProperty checks its own DefaultJson against its own constraints at construction, so
        // a default outside its @range cannot reach here. What THAT check cannot see is the whole
        // body: this walks each default back into an otherwise-valid body and validates the result.
        foreach (var property in DocumentDbAccounts.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(DocumentDbAccounts.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            DocumentDbAccounts.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue(
                $"the declared default for '{property.JsonPointer}' does not validate inside an "
                + "otherwise-valid body."
            );
        }
    }

    [Fact]
    public void TheQuantityGrammarIsThePlatformsRatherThanAFreshCopyOfIt() {
        // ⚠ KubeQuantity's remarks: "There is exactly one of these in the platform and there must
        // stay exactly one", and they record that the last provider to keep its own copy of the
        // grammar got a second PARSER written next to it — one that returned 8699999999999 for 8.7T.
        // QuantityParserTests fails if a fresh copy appears. This asserts the identity is a REFERENCE
        // rather than a matching string — a matching string is what all four earlier copies were.
        DocumentDbAccounts.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
        DocumentDbAccounts.OptionalQuantityPattern.ShouldBeSameAs(KubeQuantity.OptionalPattern);
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecret() {
        // ⚠ docs/plan/12 § The pattern, once, piece 5 and SchemaProperty.Secret's own remarks: a
        // `Secret: true` body property masks the value on the generated surfaces and DOES NOTHING
        // ELSE — the write path stores it in plaintext, in grain state, which docs/plan/05 forbids for
        // a credential. The only secret this type has leaves through the listKeys action, whose
        // response schema is where the one `Secret: true` in this provider lives.
        DocumentDbAccounts.Schema2026.Properties.ShouldNotContain(
            x => x.Secret,
            "a credential in the resource body is a credential in grain state, whatever the schema "
            + "says about masking it."
        );

        DocumentDbAccounts.ListKeysResponse.Properties.Count(x => x.Secret).ShouldBe(1);
    }

    [Fact]
    public void EveryPresetHoldsTheOneToFourRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary defines s1 as "1:4". A table that drifted off the ratio
        // on one rung would make the family name a lie on that rung and nowhere else, which is the
        // kind of thing nobody notices because every other row reads correctly.
        //
        // ⚠ AND THAT IS NOT HYPOTHETICAL: PostgresServers.Presets["s1.nano"] is (100m, 512Mi), which
        // is 5 GiB per core. This provider's table is the third `s1` in the tree and the second that
        // holds the ratio on every rung — see conformance.yaml § owed, `sizing-table-is-not-shared`.
        foreach (var (preset, (cpu, memory)) in DocumentDbAccounts.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(4m, $"'{preset}' is {cpu} to {memory}");
        }
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        // A preset the schema offers and the table does not is a body the API accepts and the meter
        // then refuses — a create that returns 500 for a value the schema advertised.
        DocumentDbAccounts.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues
            .Order(StringComparer.Ordinal)
            .ShouldBe(DocumentDbAccounts.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheVersionEnumAndTheImagePairTableAreTheSameSet() {
        // ⚠ ONE PROPERTY, TWO IMAGES. A version the schema offers and the table does not would reach
        // DocumentDbAccounts.GatewayImage, which indexes the table — so the create would 500 for a
        // value the schema had just advertised.
        DocumentDbAccounts.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/version")
            .AllowedValues
            .Order(StringComparer.Ordinal)
            .ShouldBe(DocumentDbAccounts.Versions.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryVersionRendersItsOwnMATCHEDPairOfImages() {
        // ⚠ THE PAIRING IS THE WHOLE REASON THERE IS ONE PROPERTY AND NOT TWO. FerretDB and the
        // DocumentDB PostgreSQL extension are released together, and a pair that was never released
        // together is a proxy talking to an extension whose call signatures it does not know. The
        // upstream tag encodes the pairing — `{pgMajor}-{documentdb}-ferretdb-{ferretdb}` — so a
        // mismatched row is visible in the string itself.
        foreach (var (version, (gateway, postgres)) in DocumentDbAccounts.Versions) {
            postgres.ShouldEndWith(
                "-ferretdb-" + gateway,
                Case.Sensitive,
                $"'{version}' pairs FerretDB {gateway} with a PostgreSQL image built for a different "
                + "FerretDB. The tag says which one it was built for; this is the row that reads it."
            );

            using var body = JsonDocument.Parse(WithVersion(DocumentDbAccounts.Body(ClusterId), version));

            DocumentDbAccounts.GatewayImage(body.RootElement)
                .ShouldBe("ghcr.io/ferretdb/ferretdb:" + gateway, version);

            DocumentDbAccounts.PostgresImage(body.RootElement)
                .ShouldBe("ghcr.io/ferretdb/postgres-documentdb:" + postgres, version);
        }
    }

    [Fact]
    public void AnUnrecognisedVersionFallsBackToAWholePairRatherThanAnEmptyTag() {
        // ⚠ Unreachable from a validated body — AllowedValues closes it — and it is the fallback that
        // matters. An empty tag renders `ghcr.io/ferretdb/ferretdb:` and fails per pod at the API
        // server, after the caller was told 202; a fallback that moved only ONE of the two images
        // would be worse still, because the pods would start and the wire calls would fail.
        using var body = JsonDocument.Parse(WithVersion(DocumentDbAccounts.Body(ClusterId), "9.9"));

        DocumentDbAccounts.GatewayImage(body.RootElement).ShouldBe("ghcr.io/ferretdb/ferretdb:2.7.0");
        DocumentDbAccounts.PostgresImage(body.RootElement)
            .ShouldBe("ghcr.io/ferretdb/postgres-documentdb:17-0.107.0-ferretdb-2.7.0");
    }

    [Fact]
    public void ThePostgresImageIsNeverTheStockOne() {
        // ⚠ THE ONE-LINE MISTAKE THAT WOULD LOOK CORRECT IN REVIEW. charts/managed/postgres renders
        // ghcr.io/cloudnative-pg/postgresql, and a Cluster with that image is a perfectly healthy
        // PostgreSQL — it passes readiness, reports Succeeded, and every FerretDB query against it
        // fails, because the documentdb extension does not exist in it and the bootstrap's
        // CREATE EXTENSION failed hours earlier in a job log nobody reads.
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var image = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", body.RootElement))!["spec"]!
            ["imageName"]!.GetValue<string>();

        image.ShouldStartWith("ghcr.io/ferretdb/postgres-documentdb:");
        image.ShouldNotContain("cloudnative-pg/postgresql");

        // ⚠ And not the -dev variant, which is what FerretDB's own build/deps Dockerfile uses and
        // which that file's comment marks as a development image.
        image.ShouldNotContain("postgres-documentdb-dev");
    }

    [Fact]
    public void SharedPreloadLibrariesIsAListBesideParametersAndNeverAKeyInsideIt() {
        // ⚠ THE DEFECT THIS PROVIDER FOUND IN THE ROW ABOVE, ASSERTED ON THE HALF IT OWNS.
        // CloudNativePG declares `shared_preload_libraries` as a []string SIBLING of `parameters`
        // (api/v1/cluster_types.go, PostgresConfiguration.AdditionalLibraries), lists it in
        // FixedConfigurationParameters (pkg/postgres/configuration.go), and its validating webhook
        // answers any fixed key found under spec.postgresql.parameters with "Can't set fixed
        // configuration parameter" (internal/webhook/v1/cluster_webhook.go). So the other spelling is
        // not a style difference: it is a 422 on every create, after the caller was told 202.
        //
        // charts/managed/ferretdb/conformance.yaml § owed carries what it costs
        // CyberCloud.DBforPostgreSQL/servers, which is not this provider's to fix.
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var postgresql = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", body.RootElement))!
            ["spec"]!["postgresql"]!.AsObject();

        var libraries = postgresql["shared_preload_libraries"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();

        libraries.ShouldBe(["pg_cron", "pg_documentdb_core", "pg_documentdb"]);

        postgresql["parameters"]!.AsObject().ContainsKey("shared_preload_libraries").ShouldBeFalse(
            "shared_preload_libraries was written under spec.postgresql.parameters, where "
            + "CloudNativePG's validating webhook refuses it as a fixed configuration parameter. The "
            + "cluster is rejected at admission and the caller has already been told 202."
        );

        // ⚠ The LIBRARY names carry the pg_ prefix and the EXTENSION created from them does not. Two
        // vocabularies, three lines apart in the rendered object.
        libraries.ShouldNotContain("documentdb");
    }

    [Fact]
    public void TheExtensionIsInstalledInThePostgresDatabaseAndTheCronDatabaseAgrees() {
        // ⚠ TWO FACTS THAT HAVE TO NAME THE SAME DATABASE AND SIT IN DIFFERENT BLOCKS. postInitSQL
        // runs "as a superuser in the `postgres` database" (cluster_types.go) and pg_cron schedules
        // its jobs in exactly one database, which the DocumentDB extension registers background jobs
        // through. If the extension ever moves to the application database, BOTH have to move — and
        // the failure if only one does is a set of cron jobs that never fire, which nothing reports.
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var spec = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", body.RootElement))!["spec"]!
            .AsObject();

        spec["bootstrap"]!["initdb"]!["postInitSQL"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .ShouldContain("CREATE EXTENSION IF NOT EXISTS documentdb CASCADE;");

        // ⚠ postInitApplicationSQL would put the extension in the application database, which is NOT
        // where FERRETDB_POSTGRESQL_URL points. See conformance.yaml § owed,
        // `superuser-is-the-connection-role`, for the shape that would move both together.
        (spec["bootstrap"]!["initdb"]!.AsObject().ContainsKey("postInitApplicationSQL")).ShouldBeFalse();

        spec["postgresql"]!["parameters"]!["cron.database_name"]!.GetValue<string>().ShouldBe("postgres");
    }

    [Fact]
    public void TheClusterAsksForTheSuperuserSecretAndForTheRightUid() {
        // ⚠ TWO DEFAULTS THE CRD GETS WRONG FOR THIS IMAGE, BOTH WRITTEN OUT.
        //
        // enableSuperuserAccess defaults to FALSE, and without it CloudNativePG never creates the
        // Secret the gateway mounts — so every FerretDB pod stays in ContainerCreating while the
        // database is perfectly healthy.
        //
        // postgresUID/GID default to 26 (cluster_types.go, DefaultPostgresUID) and the
        // postgres-documentdb image runs as 999, so the data directory is mounted with an owner the
        // process cannot write and the first instance never initialises. The failure is a permission
        // error in an init container's log; nothing on the Cluster's status says "the UID is wrong".
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var spec = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", body.RootElement))!["spec"]!
            .AsObject();

        spec["enableSuperuserAccess"]!.GetValue<bool>().ShouldBeTrue();
        spec["postgresUID"]!.GetValue<int>().ShouldBe(999);
        spec["postgresGID"]!.GetValue<int>().ShouldBe(999);
    }

    [Fact]
    public void TheEndpointNamesThisProvidersServiceAndTheDsnNamesTheOperatorsOne() {
        // ⚠ TWO SERVICES, TWO OWNERS, AND SWAPPING THEM WOULD BE A CONNECTION STRING THAT RESOLVES TO
        // THE WRONG PROTOCOL. `orders` is the MongoDB endpoint this provider applies; `orders-pg-rw`
        // is the PostgreSQL primary CloudNativePG creates (GetServiceReadWriteName). Handing a driver
        // the second is a MongoDB client connecting to port 5432.
        DocumentDbAccounts.Endpoint("t-prod", "orders").ShouldBe("mongodb://orders.t-prod.svc:27017/");
        DocumentDbAccounts.PostgresServiceName("orders").ShouldBe("orders-pg-rw");
        DocumentDbAccounts.SuperuserSecretName("orders").ShouldBe("orders-pg-superuser");

        // ⚠ -rw and never -r. The read-only and any-instance Services would round-robin writes onto
        // hot standbys, and every insert would fail with "read-only transaction" on two thirds of the
        // connections — intermittently, which is the worst way for it to fail.
        DocumentDbAccounts.PostgresServiceName("orders").ShouldNotEndWith("-ro");
        DocumentDbAccounts.PostgresServiceName("orders").ShouldNotBe("orders-pg-r");
    }

    [Fact]
    public void MonitoringOffRemovesBothScrapeObjectsAndOnAsksForBoth() {
        // ⚠ ONE FLAG, TWO MECHANISMS, AND THIS IS THE ROW WHERE docs/plan/12 § piece 6 TAKES BOTH OF
        // ITS BRANCHES AT ONCE. CloudNativePG is ASKED (spec.monitoring.enablePodMonitor); FerretDB
        // has no operator to ask, so the platform WRITES a PodMonitor. A flag that moved one and not
        // the other would be a service half of whose health is invisible, with nothing saying which
        // half.
        using var on = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));
        using var off = JsonDocument.Parse(WithMonitoring(DocumentDbAccounts.Body(ClusterId), false));

        var clusterOn = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", on.RootElement))!;
        var clusterOff = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", off.RootElement))!;

        clusterOn["spec"]!["monitoring"]!["enablePodMonitor"]!.GetValue<bool>().ShouldBeTrue();
        clusterOff["spec"]!["monitoring"]!["enablePodMonitor"]!.GetValue<bool>().ShouldBeFalse();

        // ⚠ The path is /debug/metrics and not /metrics — internal/util/debug/debug.go registers the
        // handler there. A PodMonitor with the conventional path scrapes a 404 forever WITHOUT
        // FAILING, which is the quiet-scrape hazard the corrected piece 6 exists to avoid.
        DocumentDbAccounts.PodMonitorJson("orders").ShouldContain("\"path\":\"/debug/metrics\"");
        DocumentDbAccounts.PodMonitorJson("orders").ShouldNotContain("\"path\":\"/metrics\"");
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

    static string WithVersion(string body, string version) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["version"] = version;
        return node.ToJsonString();
    }

    static string WithMonitoring(string body, bool enabled) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["monitoring"] = new JsonObject { ["enabled"] = enabled };
        return node.ToJsonString();
    }
}
