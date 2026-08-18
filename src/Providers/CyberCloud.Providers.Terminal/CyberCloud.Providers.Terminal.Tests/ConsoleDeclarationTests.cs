using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal.Tests;

/// <summary>
///     What only this provider can be wrong about in its declaration.
/// </summary>
/// <remarks>
///     The lifecycle — create, poll, read back, tag, lock, delete, drift, and the parent ReBAC edge —
///     is <c>CyberCloud.Providers.Terminal.Conformance</c>'s, because those are the <i>shared</i>
///     suite's assertions and a per-provider copy is the drift docs/plan/03 § Providers warns about.
/// </remarks>
public sealed class ConsoleDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheWayASiloBuildsOneAtStart() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a type with no api-version, on a reconciler naming a type nobody declared, on a handler
        // type that does not implement IResourceActionHandler, and on a RequiresCluster type whose
        // schema does not declare a required string at the cluster-id pointer. Running it is how a
        // declaration mistake becomes a startup failure rather than a 404 with nothing in the log.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);

        registry.Namespaces.ShouldContain(CloudConsoles.ProviderNamespace);
        registry.TryGetType(CloudConsoles.Type, out var registration).ShouldBeTrue();

        registration.ReconcilerType.ShouldBe(typeof(CloudConsoleReconciler));
        registration.RequiresCluster.ShouldBeTrue();
        registration.SupportsTags.ShouldBeTrue();
        registration.ApiVersions.Length.ShouldBe(1);
        registration.Chart.ShouldBe(CloudConsoles.ChartName);
    }

    [Fact]
    public void BothActionsAreSynchronousWithAHandlerRatherThanLongRunningOrUnserved() {
        // ⚠ THE THREE KINDS AND WHY TWO OF THEM ARE WRONG HERE, PINNED SO THE CHOICE SURVIVES A
        // REFACTOR.
        //
        //   * LONG-RUNNING answers 202 and re-runs the TYPE'S RECONCILER through OperationGrain — which
        //     on this type converges three durable objects and never starts a pod, so a caller would
        //     poll an operation to success and still have no shell.
        //   * SYNCHRONOUS WITH NO HANDLER is refused by name by the dispatcher, which is right for
        //     something not built and wrong for something that is.
        //   * SYNCHRONOUS WITH A HANDLER answers 200 with its own body, which is what a person who
        //     just opened a terminal panel needs.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);
        registry.TryGetType(CloudConsoles.Type, out var registration).ShouldBeTrue();

        foreach (var name in new[] { CloudConsoles.ConnectAction, CloudConsoles.TerminateAction }) {
            registration.TryGetAction(name, out var action).ShouldBeTrue(name);
            action.LongRunning.ShouldBeFalse(name);
            action.HandlerType.ShouldBe(typeof(CloudConsoleSessionHandler), name);

            // ⚠ NOT `secret`. Nothing either action returns is a credential — a console's authority is
            // the pod's identity, which never reaches the caller — and declaring it secret would tell
            // every generated surface something untrue.
            action.Secret.ShouldBeFalse(name);
        }
    }

    [Fact]
    public void AttachingIsItsOwnPermissionAndNotRead() {
        // ⚠ A Reader on a resource group must NOT inherit a terminal inside it. Attaching to a shell
        // that holds a managed identity is not reading a resource, and the whole reason `connect` has
        // its own permission string is that docs/plan/07's roles compose by permission name.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);
        registry.TryGetType(CloudConsoles.Type, out var registration).ShouldBeTrue();

        registration.TryGetAction(CloudConsoles.ConnectAction, out var connect).ShouldBeTrue();

        connect.Permission.ShouldNotBe(registration.ReadPermission, "every viewer would get a shell");
        connect.Permission.ShouldNotBe(registration.WritePermission);

        // ⚠ terminate takes the SAME permission and not write or delete: ending your own session is
        // not a change to the resource and must not need a right that would also let you destroy the
        // home volume.
        registration.TryGetAction(CloudConsoles.TerminateAction, out var terminate).ShouldBeTrue();
        terminate.Permission.ShouldBe(connect.Permission);
        terminate.Permission.ShouldNotBe(registration.DeletePermission);
    }

    [Fact]
    public void OneHandlerServesBothActionsAndSaysSoByDeclaringNoActionName() {
        // IResourceActionHandler.Action: "empty when it serves every action on Type". Two classes
        // would be two places that have to agree about the pod's name, its identity and what "running"
        // means.
        new CloudConsoleSessionHandler().Action.ShouldBe(string.Empty);
        new CloudConsoleSessionHandler().Type.ShouldBe(CloudConsoles.Type);
    }

    [Fact]
    public void NoSoftDeleteIsDeclaredAndTheReasonIsThisTypesRatherThanThePlatforms() {
        // ⚠ THE ARGUMENT IS ON TerminalProvider AND THIS IS THE ASSERTION THAT KEEPS IT HONEST. Every
        // provider before docs/plan/08 § Soft delete landed declined for the platform's reason —
        // "nothing reads SoftDeleteDays" — and that reason has EXPIRED. This one declines for its own:
        // the home volume already has a retention policy in the body, chosen by the tenant and
        // starting from last use, and two retention mechanisms over one volume is two things that can
        // disagree about when the bytes go.
        //
        // ⚠ WHAT MAKES THAT WEAK IS THAT NEITHER MECHANISM RUNS —
        // conformance.yaml § owed, `delete-takes-the-home-directory` — so this assertion is a
        // reminder to revisit rather than a settled answer.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);
        registry.TryGetType(CloudConsoles.Type, out var registration).ShouldBeTrue();

        registration.SoftDeleteDays.ShouldBe(0);

        // The alternative it rests on is a real, tenant-visible number.
        var retention = CloudConsoles.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/home/retentionDays");

        retention.DefaultJson.ShouldBe("90", "docs/plan/19 § The pod: retained 90 days after last use");
    }

    // ── Failure class (d): a shortName collision ─────────────────────────────────────────────────

    [Fact]
    public void TheShortNameIsNoneOfTheTwelveCliGroupKeysInTheTree() {
        // ⚠ CliEmitter derives the CLI GROUP key from the provider namespace's LAST SEGMENT,
        // lower-cased — so this namespace is already the group `terminal`. System.CommandLine's
        // ValidTokens builds ONE dictionary of every command token and every alias in the whole tree,
        // so a group and an alias that share a string throw `ArgumentException: An item with the same
        // key has already been added` ON THE FIRST PARSE OF ANY COMMAND LINE — not only for the
        // colliding command. Several agents have hit that.
        //
        // ⚠ THE LIST IS TYPED OUT. Deriving it from the providers in the tree would compare a constant
        // to itself and would also require referencing another Providers.* assembly, which
        // src/Providers/README.md § Hard rule forbids.
        string[] groups = [
            "sample",
            "dbforpostgresql",
            "dbformysql",
            "cache",
            "messaging",
            "search",
            "storage",
            "documentdb",
            "analytics",
            "containerservice",
            "network",
            "terminal"
        ];

        groups.ShouldContain(
            "terminal",
            "this provider's own group key is missing from the list it is being checked against"
        );

        groups.ShouldNotContain(TerminalProvider.ShortName);
    }

    [Fact]
    public void TheShortNameIsNoneOfTheSixteenAlreadyDeclared() {
        // ⚠ ProviderRegistry.Build refuses a duplicate short name WITHIN one provider and never
        // compares one across providers or against a group name — see
        // charts/managed/seaweedfs/conformance.yaml § owed, `short-name-collides-with-the-group`. So
        // this is checked by hand, against literals, for the sixth time.
        //
        // ⚠ AND THE COUNT HAS MOVED: the brief for this row said "eleven existing", and there are
        // SIXTEEN. Four families and three child types have landed since that number was written.
        string[] declared = [
            "widget",
            "postgres",
            "mariadb",
            "valkey",
            "kafka",
            "nats",
            "rabbitmq",
            "objectstore",
            "bucket",
            "docdb",
            "opensearch",
            "clickhouse",
            "aks",
            "nodepool",
            "vnet",
            "subnet"
        ];

        declared.Length.ShouldBe(16);
        declared.ShouldNotContain(TerminalProvider.ShortName);
        declared.Distinct(StringComparer.Ordinal).Count().ShouldBe(declared.Length);
    }

    [Fact]
    public void TheShortNameIsNoneOfTheCliSReservedGroups() {
        // ⚠ A THIRD DICTIONARY. `CommandTree.ReservedGroups` takes down `cyc --help` entirely at
        // start-up when a generated group name collides — and an ALIAS lands in the same ValidTokens
        // dictionary a group name does, so the reserved list binds both.
        string[] reserved = [
            "login",
            "logout",
            "account",
            "rest",
            "config",
            "completion",
            "complete",
            "extension",
            "version"
        ];

        reserved.ShouldNotContain(TerminalProvider.ShortName);
    }

    [Fact]
    public void TheShortNameIsShellAndDeliberatelyNotTerminalOrConsole() {
        // `terminal` is the group key this namespace already produces — the same trap
        // CyberCloud.Storage/accounts hit and ships as `objectstore` to avoid. `console` was the other
        // candidate and `shell` won because it is what the image, the pod, the namespace and every
        // sentence in docs/plan/19 already call it.
        TerminalProvider.ShortName.ShouldBe("shell");
        TerminalProvider.ShortName.ShouldNotBe("terminal");
    }

    // ── The contract the portal is built against ──────────────────────────────────────────────

    [Fact]
    public void TheHubPathIsTheOneTheGatewayMaps() {
        // ⚠ A CONSTANT HERE AND A CONSTANT IN HubNames.Terminal, AND THEY MAY NOT BE ONE. Routing
        // /hubs/{name} is the gateway's job and nothing else in the platform decides what a hub is; a
        // provider assembly may not reference a host. So the string is duplicated on purpose and this
        // literal is what keeps the two in step — the same way every short-name check in this tree is
        // done.
        CloudConsoles.HubPath.ShouldBe("/hubs/terminal");
    }

    [Fact]
    public void TheConnectResponseCarriesTheFiveThingsATerminalPanelCannotProceedWithout() {
        // ⚠ THIS IS THE CONTRACT docs/plan/20 § The pages that are not generated IS BUILT AGAINST AND
        // NOBODY HAD WRITTEN IT DOWN. Each field is here because the panel cannot proceed without it,
        // and `recording` is here rather than only on the resource body because that document requires
        // the portal to be loud when recording is on — a panel that had to fetch the resource to find
        // out would render one frame of a terminal that lies.
        CloudConsoles.ConnectResponse.Properties.Select(x => x.JsonPointer).ShouldBe(
            [
                "/sessionId",
                "/hub",
                "/state",
                "/idleTimeoutSeconds",
                "/maxDurationSeconds",
                "/recording"
            ]
        );

        CloudConsoles.ConnectResponse.Properties.ShouldAllBe(x => x.Required);

        // ⚠ NO FIELD IS `Secret`. A connect response is not a credential export, and a `secret: true`
        // field here would take the fully-consistent authorization path for nothing.
        CloudConsoles.ConnectResponse.Properties.ShouldAllBe(x => !x.Secret);
    }

    // ── The image ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheImageDigestsAreVisiblyPlaceholders() {
        // ⚠ NOTHING IN THIS REPOSITORY BUILDS THE SHELL IMAGE — conformance.yaml § owed,
        // `no-image-pipeline` — so there is no digest to write. A plausible-looking 64 hex characters
        // would be a reference that fails to pull at the worst possible moment with nothing in the
        // tree to say why, so the two are all-zeroes and all-ones and this test is what keeps them
        // that way until the pipeline exists.
        //
        // When it does: this test is the one to delete, and deleting it is the moment somebody has to
        // read the owed item.
        foreach (var digest in CloudConsoles.ImageDigests.Values) {
            digest.ShouldStartWith("sha256:");
            digest.Length.ShouldBe("sha256:".Length + 64);
            digest["sha256:".Length..].Distinct().Count().ShouldBe(
                1,
                "a real-looking digest has been written for an image nothing builds"
            );
        }

        CloudConsoles.ImageDigests.Keys.Order(StringComparer.Ordinal).ShouldBe(["default", "minimal"]);
    }

    [Fact]
    public void AnUnknownImageVariantFallsBackRatherThanRenderingNothing() {
        // ⚠ The fallback is what keeps a body the schema would refuse from producing a pod with an
        // empty image reference. It also hides a MISSING variant, which is the second half of
        // conformance.yaml § owed, `the-minimal-variant-is-a-second-image-nobody-costed`.
        var body = JsonNode.Parse(CloudConsoles.Body(Cluster))!.AsObject();
        body["properties"]!["image"]!["variant"] = "does-not-exist";

        using var desired = JsonDocument.Parse(body.ToJsonString());

        CloudConsoles.Image(desired.RootElement).ShouldBe(
            CloudConsoles.ImageRepository + "@" + CloudConsoles.ImageDigests["default"]
        );
    }

    // ── The meters ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOnlyCapacityMeterIsStorageAndTheTwoThatAreMissingAreTheDecision() {
        // ⚠ A quota meter is reserved at WRITE time from a pure function of the body. That fits a home
        // volume exactly — the claim is allocated whether anybody is attached or not — and fits CPU
        // and memory exactly backwards: a console's pod exists only while somebody is typing into it,
        // so a state-based reservation would hold 2 vCPU and 4 GiB against a subscription for a
        // terminal that was closed a week ago. That is the exact failure docs/plan/19 § The pod calls
        // the design constraint, moved from the cluster into the quota grain where nobody would see
        // it.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);
        registry.TryGetType(CloudConsoles.Type, out var registration).ShouldBeTrue();

        var meters = registration.Meters.Select(x => x.Meter).ToList();

        meters.ShouldContain(QuotaMeter.StorageGb);
        meters.ShouldContain(QuotaMeter.Resources);
        meters.ShouldNotContain(QuotaMeter.Vcpu, "an idle console would reserve CPU it is not using");
        meters.ShouldNotContain(QuotaMeter.MemoryGb, "an idle console would reserve memory it is not using");
        meters.Count.ShouldBe(2);
    }

    [Fact]
    public void TheStorageMeterReadsTheHomeSizeAndNothingElse() {
        // ⚠ The container's ephemeral-storage limit is NODE DISK rather than provisioned storage — it
        // is reclaimed when the pod ends and no volume is ever cut for it — so reserving against it
        // would charge a tenant twice for one number and would make a subscription's storage allowance
        // depend on how many terminals happened to be open.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);
        registry.TryGetType(CloudConsoles.Type, out var registration).ShouldBeTrue();

        var storage = registration.Meters.Single(x => x.Meter == QuotaMeter.StorageGb);
        storage.Reads.ShouldBe(["/properties/home/size"]);

        using var five = JsonDocument.Parse(CloudConsoles.Body(Cluster));
        storage.Derivation!.Amount(five.RootElement).GetValueOrThrow().ShouldBe(5m);

        using var big = JsonDocument.Parse(CloudConsoles.Body(Cluster, homeSize: "50Gi"));
        storage.Derivation.Amount(big.RootElement).GetValueOrThrow().ShouldBe(50m);
    }

    [Fact]
    public void AMeterThatCannotResolveRefusesRatherThanReservingZero() {
        // A zero reservation is a volume a subscription is never held to and a delete that returns
        // nothing — the arithmetic asymmetry MeteredAmountTests exists to catch, seeded here.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);
        registry.TryGetType(CloudConsoles.Type, out var registration).ShouldBeTrue();

        var body = JsonNode.Parse(CloudConsoles.Body(Cluster))!.AsObject();
        body["properties"]!["home"]!["size"] = "not a quantity";

        using var broken = JsonDocument.Parse(body.ToJsonString());

        registration.Meters.Single(x => x.Meter == QuotaMeter.StorageGb)
            .Derivation!.Amount(broken.RootElement)
            .IsSuccess.ShouldBeFalse();
    }

    // ── The schema ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDeclaredDefaultIsTheOneTheReadersFallBackTo() {
        // ⚠ THE WRITE PATH STORES THE BODY AS SENT AND THE VALIDATOR SUBSTITUTES NOTHING, so a body
        // that omitted a property arrives at a reader with the property absent and the reader's own
        // fallback decides. Two copies of every default, and this is what keeps them equal — a
        // disagreement here is a console whose portal form says 20 minutes and whose pod says
        // something else.
        using var empty = JsonDocument.Parse("""{"location":"eu-central","properties":{}}""");
        var desired = empty.RootElement;

        Default("/properties/image/variant").ShouldBe("\"" + CloudConsoles.ImageVariant(desired) + "\"");
        Default("/properties/sizing/preset").ShouldBe("\"" + CloudConsoles.SizingPreset(desired) + "\"");
        Default("/properties/home/size").ShouldBe("\"" + CloudConsoles.HomeSize(desired) + "\"");
        Default("/properties/home/retentionDays").ShouldBe(
            CloudConsoles.HomeRetentionDays(desired).ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Default(CloudConsoles.IdleTimeoutMinutesPointer).ShouldBe(
            (CloudConsoles.IdleTimeoutSeconds(desired) / 60).ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Default(CloudConsoles.MaxDurationHoursPointer).ShouldBe(
            (CloudConsoles.MaxDurationSeconds(desired) / 3600).ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Default("/properties/network/egress").ShouldBe("\"" + CloudConsoles.EgressMode(desired) + "\"");
        Default("/properties/audit/sessionRecording").ShouldBe(
            CloudConsoles.SessionRecording(desired) ? "true" : "false"
        );
    }

    [Fact]
    public void EveryEnumInTheSchemaHasValuesInIt() {
        // ⚠ THIS TEST EXISTS BECAUSE THE BUG IT CATCHES SHIPPED FOR AN HOUR AND NOTHING IN C# WARNED.
        // Static field initialisers run in DECLARATION ORDER, and `EgressModes` was declared BELOW
        // Schema2026 — so the schema read an empty ImmutableArray and `/properties/network/egress`
        // accepted any string at all. A console could then be created asking for a posture nothing
        // renders, and NetworkPolicyJson's `Internet` comparison would fall through to the three-rule
        // form: a tenant asking for "internet" in lower case would silently get no public egress.
        //
        // ⚠ WHAT CAUGHT IT WAS `./build.sh Charts`, which emitted a @param block with no @enum line
        // and failed on the diff — a gate two steps removed from the mistake. This is the assertion
        // that names it, and it is written over EVERY property rather than over the one that broke,
        // because the hazard is the file's ordering rather than that member.
        // ⚠ THE THREE POINTERS ARE NAMED RATHER THAN DERIVED, and the first draft of this test derived
        // them — `Where(x => !x.AllowedValues.IsDefault)` — which is the mistake this test is about
        // wearing different clothes. `ResourceSchema` normalises an unset `AllowedValues` to an EMPTY
        // array rather than a default one, so that filter selected every property in the schema and
        // the test failed on `/location`. A list of three that a reviewer edits when a fourth enum
        // lands is the same trade `KubeLabels.Mandatory` and every short-name check in this tree make.
        string[] enums = [
            "/properties/image/variant",
            "/properties/sizing/preset",
            "/properties/network/egress"
        ];

        foreach (var pointer in enums) {
            CloudConsoles.Schema2026.Properties.Single(x => x.JsonPointer == pointer)
                .AllowedValues
                .ShouldNotBeEmpty(pointer);
        }

        CloudConsoles.EgressModes.ShouldBe(["Internet", "TenantOnly"]);

        CloudConsoles.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/network/egress")
            .AllowedValues
            .ShouldBe(CloudConsoles.EgressModes);
    }

    [Fact]
    public void NoBodyPropertyIsSecret() {
        // ⚠ A console has no credential in its body and must not grow one. ProviderBuilder refuses a
        // schema whose EVERY property is secret; nothing refuses one secret property, and a secret in
        // a resource body is a secret in grain state — docs/plan/00 § Non-negotiables.
        CloudConsoles.Schema2026.Properties.ShouldAllBe(x => !x.Secret);
    }

    [Fact]
    public void TheSizingLadderStopsWhereDocs19StopsAndNotAtARoundNumber() {
        // docs/plan/19 § The pod: "0.5–2 vCPU, 1–4 GB". Three presets spanning exactly that, and no
        // fourth: a shell is a place to type from, and a tenant who needs sixteen cores needs a cluster
        // and a job rather than a terminal that bills like one. Every other family exposes an eight-row
        // ladder; this row's short one is the decision.
        CloudConsoles.Presets.Count.ShouldBe(3);

        CloudConsoles.Presets["c1.small"].ShouldBe(("500m", "1Gi"));
        CloudConsoles.Presets["c1.medium"].ShouldBe(("1", "2Gi"));
        CloudConsoles.Presets["c1.large"].ShouldBe(("2", "4Gi"));

        // The enum and the table are one set, so a preset a tenant may name always resolves.
        CloudConsoles.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues
            .Order(StringComparer.Ordinal)
            .ShouldBe(CloudConsoles.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheProviderNamespaceAndTypeKeepTheirExactCasingThroughTheRegistry() {
        // ⚠ ONE CHARACTER OF CASING. This namespace is the easy-looking kind — one word, no internal
        // case changes — which is exactly the shape somebody normalises to lower case without noticing.
        var registry = ProviderRegistry.Build([new TerminalProvider()]);

        registry.Namespaces.ShouldContain("CyberCloud.Terminal");
        CloudConsoles.Type.ToString().ShouldBe("CyberCloud.Terminal/consoles");
        CloudConsoles.Type.Depth.ShouldBe(1);

        // ⚠ The label value is NOT the type verbatim: `/` is not legal in a label value. It is also
        // the string the console's own NetworkPolicy selects its pod on, so a change here silently
        // detaches the policy from the shell.
        CloudConsoles.ResourceTypeLabelValue.ShouldBe("cybercloud.terminal_consoles");
    }

    static readonly Guid Cluster = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    static string Default(string pointer) =>
        CloudConsoles.Schema2026.Properties.Single(x => x.JsonPointer == pointer).DefaultJson;
}
