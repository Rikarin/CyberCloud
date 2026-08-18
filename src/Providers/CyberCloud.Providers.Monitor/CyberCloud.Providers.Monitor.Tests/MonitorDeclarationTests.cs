using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The workspace declaration, checked the way a silo checks it at start, plus the facts that live
///     in more than one file and have to agree.
/// </summary>
public sealed class MonitorDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a duplicate type, on a type with no api-version, on a duplicate short name, on a
        // RequiresCluster type whose schema does not declare the pointer as a required string, and on
        // a SupportsSoftDelete type whose schema does not declare its purge-protection pointer as a
        // boolean.
        var registry = ProviderRegistry.Build([new MonitorProvider()]);

        registry.TryGetType(MonitorWorkspaces.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(MonitorWorkspaces.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(MonitorWorkspaces.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(MonitorWorkspaceReconciler));
    }

    // ── The recovery window: argued, built, and withdrawn ───────────────────────────────────────

    [Fact]
    public void TheRecoveryWindowIsWithdrawnAndEverythingItNeedsIsStillHere() {
        // ⚠⚠ THIS TYPE WAS THE FIRST IN THE TREE TO DECLARE SupportsSoftDelete AND IT NO LONGER
        // DOES, WHICH IS A DECISION RATHER THAN A REVERSION. What a window means on this type was
        // measured rather than assumed: OperationGrain.DriveAsync runs NO pass for a soft delete, so
        // every object stays; ParkAsync keeps the quota committed in as many words; and the object
        // left standing is the VMUser, which is the one thing on this row that enforces anything —
        // vmauth resolves it the moment it is applied. So a soft-deleted workspace is an
        // authenticated, billed, open write path into a store the tenant believes is gone. A delete
        // that does not delete is worse than no recovery window. MonitorProvider carries the full
        // argument and CyberCloud.ContainerRegistry/registries reached the same conclusion from its
        // own measurement.
        //
        // ⚠ AND EVERY PART THE DECLARATION NEEDS IS DELIBERATELY KEPT, so re-declaring is ONE LINE.
        // That is what the rest of this test pins: a purge-protection property the builder would
        // refuse the type without, and a day count that is still the one docs/plan/06 asks for.
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorWorkspaces.Type, out var registration).ShouldBeTrue();

        registration.SoftDeleteDays.ShouldBe(
            0,
            "CyberCloud.Monitor/workspaces declares a recovery window again. That is welcome IF the "
            + "platform can now withdraw the write path when a resource is parked — check that "
            + "OperationGrain.ParkAsync takes the VMUser down (or that ISecretWriter can revoke the "
            + "ingest key) before believing it. If it still cannot, a soft-deleted workspace keeps "
            + "accepting authenticated writes and keeps accruing billed retention at an address that "
            + "answers 404. conformance.yaml § owed, `soft-delete-is-withdrawn-not-declined`."
        );

        // ⚠ THE NUMBER SURVIVES THE WITHDRAWAL, because the argument for it did. docs/plan/06
        // § Tags, locks gives 7 for a type carrying data, and a workspace holds the tenant's ONLY
        // copy of their logs — a database has a backup and an object store has versioning; telemetry
        // has neither, because the source of truth was a process that has since exited.
        MonitorProvider.SoftDeleteDays.ShouldBe(7);

        // ⚠ AND SO DOES THE PURGE-PROTECTION PROPERTY, which is the half that would otherwise have
        // to be rebuilt. IResourceTypeBuilder.SupportsSoftDelete refuses a type whose schema does
        // not declare its purge-protection pointer as a boolean — a flag the platform enforces
        // against a property no schema declares is a protection that silently never engages — so
        // keeping the property is what makes re-declaring one line rather than two edits and a
        // silo-start failure in between.
        MonitorWorkspaces.Schema2026.Declares(MonitorWorkspaces.PurgeProtectionPointer).ShouldBeTrue(
            "the purge-protection property was removed along with the declaration. Restoring the "
            + "window would then fail at ProviderRegistry.Build, at silo start, naming the pointer."
        );

        MonitorWorkspaces.Schema2026.Properties
            .Single(x => x.JsonPointer == MonitorWorkspaces.PurgeProtectionPointer)
            .Kind.ShouldBe(SchemaKind.Boolean);
    }

    // ── The action, and the shape only this type and one other exercise ─────────────────────────

    [Fact]
    public void ListKeysIsSynchronousSecretAndHasAHandler() {
        // ⚠ THE THREE-WAY DECLARATION THAT ONLY BECAME EXPRESSIBLE WHEN ACTIONS GOT HANDLERS.
        // Synchronous with a handler answers 200 with its own body; synchronous with NO handler is
        // refused by name at declaration time; long-running with a handler is refused by
        // ProviderBuilder.Action, because a long-running action goes through the operation grain and
        // re-runs the RECONCILER — which for a listKeys would answer 202 and hand back nothing. This
        // is the second handler in the tree.
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorWorkspaces.Type, out var registration).ShouldBeTrue();

        var action = registration.Actions.Single(x => x.Name == MonitorWorkspaces.ListKeysAction);

        action.Secret.ShouldBeTrue();
        action.LongRunning.ShouldBeFalse();
        action.HandlerType.ShouldBe(typeof(MonitorWorkspaceListKeysHandler));
        action.Response.ShouldNotBeNull();

        // ⚠ `listKeys` does NOT share the read permission. An ingest key is a WRITE credential for the
        // tenant's whole telemetry stream; sharing `read` would make every viewer of a workspace a
        // party that can forge its logs.
        action.Permission.ShouldNotBe(registration.ReadPermission);
    }

    [Fact]
    public void TheHandlerServesTheTypeAndTheActionItIsDeclaredOn() {
        // ⚠ A handler naming nothing is a handler that is never called, and it is silent unless
        // something looks. The dispatcher matches on both members.
        var handler = new MonitorWorkspaceListKeysHandler();

        handler.Type.ShouldBe(MonitorWorkspaces.Type);
        handler.Action.ShouldBe(MonitorWorkspaces.ListKeysAction);
    }

    // ── Failure class (d): the short name against every CLI group key, as literals ───────────────

    [Fact]
    public void TheShortNameIsNoneOfTheTwelveCliGroupKeysInTheTree() {
        // ⚠ THE CHECK CyberCloud.Storage/accounts DEMANDS OF EVERY PROVIDER AFTER IT, AND NOTHING IN
        // THE REGISTRY MAKES IT. CliEmitter derives the GROUP key from the provider namespace's last
        // segment, lower-cased, and System.CommandLine's ValidTokens builds ONE dictionary over every
        // command token AND every alias in the whole tree — so a short name equal to any group name
        // throws `An item with the same key has already been added` on the FIRST PARSE OF ANY COMMAND
        // LINE, before any verb runs, naming neither the provider nor the string.
        //
        // ⚠ EVERY NAME BELOW IS A TYPED-OUT LITERAL, INCLUDING THIS PROVIDER'S OWN. Deriving them the
        // way the emitter does — `ProviderNamespace.Split('.')[^1].ToLowerInvariant()` — would compare
        // the emitter to itself, which is the shape that let a casing sabotage stay green on an
        // earlier provider. The list is the twelve families in src/Providers today.
        var groups = new[] {
            "sample", "dbforpostgresql", "dbformysql", "cache", "messaging", "storage", "analytics",
            "search", "documentdb", "containerservice", "network", "monitor"
        };

        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorWorkspaces.Type, out var registration).ShouldBeTrue();

        registration.Display.Alias.ShouldBe("workspace");

        foreach (var group in groups) {
            registration.Display.Alias.ShouldNotBe(
                group,
                $"the short name equals the CLI group key '{group}', so every `cyc` invocation throws "
                + "before it parses."
            );
        }

        // ⚠ And this namespace's OWN group key is in that list, spelled out. `CyberCloud.Monitor`
        // becomes `monitor`, which is the obvious short name for this type and is precisely the one it
        // could not have — the same trap `CyberCloud.Storage` walked into with `storage` and
        // `CyberCloud.Analytics` with `analytics`.
        groups.ShouldContain("monitor");
    }

    [Fact]
    public void TheShortNameIsNoneOfTheSixteenShortNamesAlreadyDeclared() {
        // ⚠ ProviderRegistry.Build DOES refuse a duplicate short name — but only across the providers
        // it is handed, and this test builds a registry holding one. The sixteen below are the ones
        // shipping in src/Providers today, typed out for the reason above.
        var declared = new[] {
            "widget", "postgres", "mariadb", "valkey", "kafka", "nats", "rabbitmq", "objectstore",
            "bucket", "opensearch", "clickhouse", "docdb", "aks", "nodepool", "vnet", "subnet"
        };

        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorWorkspaces.Type, out var registration).ShouldBeTrue();

        declared.ShouldNotContain(
            registration.Display.Alias,
            "the short name is already taken by another provider, which ProviderRegistry.Build refuses "
            + "only when both providers are in the same registry — and a silo builds one registry."
        );
    }

    // ── The schema ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultBodyIsAcceptedAndTheDefaultsAreTheCheapOnes() {
        // docs/plan/16 § Cost and retention honesty asks for "per-signal retention that is a paid
        // property with a cheap default". A default of `extended` would bill every workspace at
        // twenty-six times the short tier before anybody chose anything.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        MonitorWorkspaces.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue();

        foreach (var signal in MonitorWorkspaces.Signals) {
            MonitorWorkspaces.Tier(body.RootElement, signal).ShouldBe(MonitorWorkspaces.Tiers[0]);
        }
    }

    [Fact]
    public void TheOverQuotaSampleRateCannotBeZero() {
        // ⚠ docs/plan/16's SECOND failure mode, made unrepresentable at the API. "Silently dropping" is
        // prevented "by making over-quota behaviour sampling with a visible rate rather than a drop",
        // and zero is a drop spelled as a rate. This Minimum is the one part of that promise the API
        // can keep on its own, with nothing downstream built.
        var property = MonitorWorkspaces.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/quota/overQuotaSampleRate");

        property.Minimum.ShouldBe(
            MonitorWorkspaces.MinimumOverQuotaSampleRate,
            "the over-quota sample rate may be set to zero, which is a silent drop under another name."
        );

        // ⚠ Compared as a value rather than through ShouldBeGreaterThan: `Minimum` is a `double?`, and
        // a nullable cannot satisfy Shouldly's `IComparable<T>` constraint. A property with NO minimum
        // reads as null here, which is exactly the regression this line has to catch.
        (property.Minimum > 0).ShouldBeTrue(
            "the over-quota sample rate has no positive minimum, so zero — a silent drop — is a legal "
            + "body."
        );
    }

    [Fact]
    public void EveryDeclaredPropertyIsCoherent() {
        // SchemaProperty.Incoherences runs a declared default through the property's OWN constraints
        // at class initialisation, so an incoherent one is a TypeInitializationException that takes
        // down the silo rather than a validation that never fires. Running it here means the failure
        // arrives in a test rather than at start-up.
        foreach (var property in MonitorWorkspaces.Schema2026.Properties) {
            property.Incoherences().ShouldBeEmpty(property.JsonPointer);
        }

        foreach (var property in MonitorWorkspaces.ListKeysResponse.Properties) {
            property.Incoherences().ShouldBeEmpty(property.JsonPointer);
        }
    }

    [Fact]
    public void TheIngestKeyIsTheOnlySecretFieldInTheResponse() {
        // ⚠ A secret: true action's response is "the description of what leaves the platform". Six of
        // the seven fields are addresses a tenant may paste into a Grafana datasource; one is a live
        // credential. Marking all seven secret would make the whole response uncacheable and
        // unloggable for no reason; marking none would put a credential in a body nothing audits.
        var secrets = MonitorWorkspaces.ListKeysResponse.Properties.Where(x => x.Secret).ToList();

        secrets.Count.ShouldBe(1);
        secrets[0].JsonPointer.ShouldBe("/ingestKey");
    }

    [Fact]
    public void TheTenancyCoordinatesAreNotBodyProperties() {
        // ⚠ AN ABSENCE ASSERTED, AND IT IS THE ONE THAT WOULD BE A CROSS-TENANT BUG. An accountID or a
        // database name a tenant could SET is another tenant's telemetry. Both are pure functions of
        // the resource's own id — MonitorWorkspaces.AccountId and .Database — and neither appears in
        // the schema at all, so there is nothing for the write path to have to refuse.
        foreach (var forbidden in new[] {
                     "/properties/accountId",
                     "/properties/database",
                     "/properties/ingestKey",
                     "/properties/dataSources"
                 }) {
            MonitorWorkspaces.Schema2026.Declares(forbidden).ShouldBeFalse(
                $"'{forbidden}' is a body property. A tenancy coordinate a tenant can choose is "
                + "another tenant's data; an endpoint a tenant can set is a datasource pointing "
                + "somewhere the platform does not serve."
            );
        }
    }
}
