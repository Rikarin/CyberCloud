using CyberCloud.ResourceManager.Contracts.Generation;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     ADR-012's other three surfaces: the <c>cyc</c> verb tree, the .NET SDK and the portal forms.
/// </summary>
/// <remarks>
///     ⚠ <b>Each is generated from the OpenAPI document rather than from the registry</b> —
///     docs/plan/21 § Generation's one hop — so every assertion here starts from
///     <see cref="OpenApiEmitter.Emit" />'s output. That is also what these tests are for: the claim
///     is that a fact the registry gained reaches all four of these surfaces, and a fact that stopped
///     at the document would be a gap closed on paper.
///     <para>
///         ⚠ ADR-012's <b>fifth</b> surface — a managed chart's <c>@param</c> block — is not here and
///         is not generated from the document either. It reads the registry, for the reason on
///         <see cref="ChartAnnotationEmitter" />, and its own suite is
///         <c>ChartAnnotationTests</c>.
///     </para>
/// </remarks>
public sealed class DerivedSurfaceTests {
    static JsonObject Document =>
        OpenApiEmitter.Emit(Fixtures.Postgres(), ApiVersion.Parse(Fixtures.FirstVersion));

    static JsonObject Cli => CliEmitter.Emit(Document);

    static JsonObject Forms => FormsEmitter.Emit(Document);

    static string Sdk => SdkEmitter.Emit(Document);

    static JsonNode Command =>
        Cli["groups"]!["dbforpostgresql"]!["commands"]!["servers"]!;

    static JsonArray Flags(string verb) => Command["verbs"]![verb]!["flags"]!.AsArray();

    static JsonNode? Flag(string verb, string name) =>
        Flags(verb).FirstOrDefault(x => DocumentReader.Text(x?["name"]) == name);

    static JsonNode Form => Forms["forms"]!["CyberCloud.DBforPostgreSQL/servers"]!;

    static JsonNode? Field(string pointer) =>
        Form["fields"]!.AsArray().FirstOrDefault(x => DocumentReader.Text(x?["jsonPointer"]) == pointer);

    // ── The CLI verb tree ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryTypeGetsTheFourVerbsAndItsActions() {
        var verbs = Command["verbs"]!.AsObject();

        verbs.ContainsKey("create").ShouldBeTrue();
        verbs.ContainsKey("show").ShouldBeTrue();
        verbs.ContainsKey("update").ShouldBeTrue();
        verbs.ContainsKey("delete").ShouldBeTrue();
        // Actions are verbs like any other — docs/plan/21 § Grammar's `cyc postgres server
        // connection-string show`.
        verbs.ContainsKey("restart").ShouldBeTrue();
        verbs.ContainsKey("list-keys").ShouldBeTrue();
    }

    [Fact]
    public void TheAliasTableIsGeneratedRatherThanHandMaintained() =>
        // docs/plan/21 § Grammar calls the alias table "the only hand-maintained part of the CLI's
        // surface". Declared on the type, it arrives here for free.
        DocumentReader.Text(Command["alias"]).ShouldBe("postgres");

    [Fact]
    public void AClosedSetBecomesAFlagsChoices() {
        // ⚠ THE ENUM GAP ON THIS SURFACE. Without it the CLI could only forward whatever was typed
        // and let the server answer 400 — no completion, no client-side refusal.
        var choices = Flag("create", "--sku-name");

        choices.ShouldNotBeNull();
        choices["choices"]!.AsArray().Select(x => DocumentReader.Text(x)).ShouldContain("s1.large");
    }

    [Fact]
    public void ABodyPropertyCalledNameDoesNotStealTheResourcesOwnNameFlag() {
        // ⚠ /properties/sku/name would be `--name`, which is the resource's own flag. The collision is
        // resolved by folding the path in, and the `properties` envelope is dropped because no user
        // thinks of it as part of the field's name.
        Flag("create", "--sku-name").ShouldNotBeNull();
        DocumentReader.Text(Flag("create", "--name")?["jsonPointer"]).ShouldBeEmpty();
    }

    [Fact]
    public void AnArrayBecomesARepeatedFlag() =>
        // ⚠ THE ARRAY GAP ON THIS SURFACE. `items: {}` gave a CLI nothing to type a repeated flag as.
        DocumentReader.Flag(Flag("create", "--allowed-ranges")?["repeated"]).ShouldBeTrue();

    [Fact]
    public void ABooleanBecomesASwitchRatherThanAValueFlag() =>
        // `--high-availability` on a command line means true. `--high-availability true` is a flag
        // whose value a user has to think about.
        DocumentReader.Text(Flag("create", "--high-availability")?["type"]).ShouldBe("switch");

    [Fact]
    public void TheClusterIdGetsAClusterAlias() =>
        // ⚠ THE CLUSTER-POINTER GAP ON THIS SURFACE: `requires-cluster: true` alone said a cluster was
        // needed and not which flag carries it.
        DocumentReader.Text(Flag("create", "--cluster-id")?["alias"]).ShouldBe("--cluster");

    [Fact]
    public void NothingIsRequiredOnUpdateBecauseAMergePatchOmitsWhatItIsNotChanging() =>
        Flags("update").ShouldAllBe(x =>
            !DocumentReader.Flag(x!["required"])
            || DocumentReader.Text(x["name"]) == "--name"
            || DocumentReader.Text(x["name"]) == "--resource-group"
        );

    [Fact]
    public void AReadOnlyPropertyIsNotAFlagOnAnyWriteVerb() =>
        // The server owns it and a body that sets it is refused, so offering a flag would offer a way
        // to fail.
        Flag("create", "--provisioning-state").ShouldBeNull();

    [Fact]
    public void ALongRunningVerbOffersWaitAndNoWait() {
        Command["verbs"]!["create"]!["waitFlags"].ShouldNotBeNull();
        // `show` is a GET and finishes when it returns.
        Command["verbs"]!["show"]!["waitFlags"].ShouldBeNull();
    }

    [Fact]
    public void ALongRunningActionIsMarkedAsOneAndAReadIsNot() {
        DocumentReader.Flag(Command["verbs"]!["restart"]!["longRunning"]).ShouldBeTrue();
        DocumentReader.Flag(Command["verbs"]!["list-keys"]!["longRunning"]).ShouldBeFalse();
    }

    [Fact]
    public void ASecretActionIsMarkedSoTheHostDoesNotEchoIt() =>
        DocumentReader.Flag(Command["verbs"]!["list-keys"]!["secret"]).ShouldBeTrue();

    [Fact]
    public void AnActionWithADeclaredRequestGetsFlagsRatherThanARawBody() {
        var listKeys = Command["verbs"]!["list-keys"]!;

        listKeys["rawBody"].ShouldBeNull();
        listKeys["flags"]!.AsArray()
            .Select(x => DocumentReader.Text(x?["name"]))
            .ShouldContain("--key-name");
    }

    [Fact]
    public void AnActionWithNoDeclaredRequestSaysSoRatherThanPretendingItHasNoParameters() {
        var registry = Fixtures.PostgresWithActions([new("noop", ActionKind.Post, "write", Secret: false)]);
        var tree = CliEmitter.Emit(OpenApiEmitter.Emit(registry, ApiVersion.Parse(Fixtures.FirstVersion)));

        DocumentReader.Flag(
            tree["groups"]!["dbforpostgresql"]!["commands"]!["servers"]!["verbs"]!["noop"]!["rawBody"]
        ).ShouldBeTrue();
    }

    // ── The portal forms ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeclaredWidgetPicksTheControl() =>
        // ⚠ ADR-012's promise, end to end: a hint the registry could not carry, through the document,
        // to the control the renderer draws.
        DocumentReader.Text(Field("/location")?["control"]).ShouldBe("xui-region");

    [Fact]
    public void AClosedSetOfEightOrFewerIsASelect() =>
        // docs/plan/20: "string + enum → @xui/select (≤ 8) or @xui/suggest (more)". /properties/tier
        // declares no widget, so the shape rule decides.
        DocumentReader.Text(Field("/properties/tier")?["control"]).ShouldBe("xui-select");

    [Fact]
    public void ADeclaredWidgetBeatsTheShapeRule() =>
        // ⚠ Both are closed sets of four. Only the registry knows that one of them is a sku picker and
        // the other is a dropdown, which is the whole reason a hint exists.
        DocumentReader.Text(Field("/properties/sku/name")?["control"]).ShouldBe("xui-sku");

    [Fact]
    public void ABoundedIntegerIsASlider() =>
        DocumentReader.Text(Field("/properties/storageGb")?["control"]).ShouldBe("xui-slider");

    [Fact]
    public void ASecretIsASecretRefPickerAndNeverAPlainValue() {
        // docs/plan/20: "never a plain value".
        DocumentReader.Text(Field("/properties/adminPassword")?["control"]).ShouldBe("xui-secret-ref");
        DocumentReader.Flag(Field("/properties/adminPassword")?["writeOnly"]).ShouldBeTrue();
    }

    [Fact]
    public void TheTagBagIsATagInput() =>
        // docs/plan/20: "object + additionalProperties: string → @xui/tag-input". It is here at all
        // because the emitted body now declares the bag.
        DocumentReader.Text(Field("/tags")?["control"]).ShouldBe("xui-tag-input");

    [Fact]
    public void AnImmutablePropertyIsDisabledAfterCreateWithAReason() {
        // docs/plan/20: "Disabled after create, with a tooltip naming why."
        DocumentReader.Flag(Field("/location")?["disabledAfterCreate"]).ShouldBeTrue();
        DocumentReader.Text(Field("/location")?["disabledReason"]).ShouldNotBeEmpty();
    }

    [Fact]
    public void ConstraintsAreCarriedThroughSoClientAndServerValidateTheSameNumbers() {
        var sku = Field("/properties/sku/vcpu")!;

        sku["minimum"]!.GetValue<double>().ShouldBe(1);
        sku["maximum"]!.GetValue<double>().ShouldBe(64);
    }

    [Fact]
    public void EveryFormNamesTheKeyAnOverrideIsRegisteredUnder() =>
        // docs/plan/20 § The escape hatch, and its limit: keyed by (resourceType, apiVersion), and
        // "every override must render the same schema".
        DocumentReader.Text(Form["overrideKey"])
            .ShouldBe("CyberCloud.DBforPostgreSQL/servers@" + Fixtures.FirstVersion);

    [Fact]
    public void EveryPropertyIsCoveredSoAnOverrideHasSomethingToBeCheckedAgainst() {
        var covered = Form["coveredPointers"]!.AsArray().Select(x => DocumentReader.Text(x)).ToList();

        covered.ShouldContain("/properties/sku/name");
        covered.ShouldContain("/tags");
    }

    [Fact]
    public void AnActionWithNoDeclaredRequestBecomesAConfirmRatherThanAnInventedDialog() {
        var restart = Form["actions"]!.AsArray()
            .Single(x => DocumentReader.Text(x?["name"]) == "restart");

        DocumentReader.Flag(restart!["confirmOnly"]).ShouldBeTrue();
    }

    // ── The .NET SDK ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheThreeAzureShapedTypesAreEmittedPerResourceType() {
        Sdk.ShouldContain("public sealed partial class PostgreSQLServerData");
        Sdk.ShouldContain("public sealed partial class PostgreSQLServerResource");
        Sdk.ShouldContain("public sealed partial class PostgreSQLServerCollection");
    }

    [Fact]
    public void AClosedSetBecomesACSharpEnumRatherThanAString() {
        // ⚠ THE ENUM GAP ON THIS SURFACE. Without it this member was a string, the compiler could not
        // catch a typo, and a caller learned the four values from a 400.
        Sdk.ShouldContain("public enum PostgreSQLServerName {");
        Sdk.ShouldContain("[JsonStringEnumMemberName(\"s1.large\")]");
        // ⚠ Unknown = 0, so a default(T) cannot name a real sku and silently send one.
        Sdk.ShouldContain("Unknown = 0");
    }

    [Fact]
    public void AnArrayHasARealElementTypeRatherThanObject() {
        // Before ElementKind the only honest rendering was IList<object>.
        Sdk.ShouldContain("public IList<string> AllowedRanges");
        Sdk.ShouldNotContain("IList<object>");
    }

    [Fact]
    public void AFormatBecomesTheClrTypeACallerExpects() {
        Sdk.ShouldContain("public DateTimeOffset? RetiredOn");
        Sdk.ShouldNotContain("public string? RetiredOn");
    }

    [Fact]
    public void ARequiredMemberTakesCSharpsOwnRequiredModifier() =>
        // A required property that is not set is a body the API refuses; `required` makes that a
        // compile error at the object initialiser rather than a 400 at run time.
        Sdk.ShouldContain("public required string Location");

    [Fact]
    public void AReadOnlyMemberIsNotRequiredBecauseACallerMayNotSetIt() =>
        Sdk.ShouldNotContain("required string? ProvisioningState");

    [Fact]
    public void ASecretActionsResponseIsATypeRatherThanAJsonElement() {
        // ⚠ THE listKeys CASE ON THIS SURFACE. "Secrets of unknown shape" was a JsonElement the
        // caller had to know the shape of from documentation.
        Sdk.ShouldContain("public sealed partial class ListKeysResult");
        Sdk.ShouldContain("Task<Response<ListKeysResult>> ListKeysAsync(");
    }

    [Fact]
    public void ALongRunningActionReturnsAnOperationAndAReadReturnsAResponse() {
        Sdk.ShouldContain("Task<Operation<System.Text.Json.JsonElement>> RestartAsync(");
        Sdk.ShouldContain("Task<Response<ListKeysResult>> ListKeysAsync(");
    }

    [Fact]
    public void EveryGeneratedTypeIsPartialSoTheHandWrittenHalfExtendsItInPlace() =>
        // docs/plan/21 § Generation: the credential types, pipeline policies and convenience methods
        // are hand-written on top. A wrapper would be a second surface with a second set of names.
        Sdk.Split('\n')
            .Where(x => x.Contains(" class ", StringComparison.Ordinal))
            .ShouldAllBe(x => x.Contains("partial", StringComparison.Ordinal)
                              || x.Contains("static class", StringComparison.Ordinal));

    [Fact]
    public void TheSourceIsBraceBalancedSoItIsNotObviouslyUncompilable() {
        var depth = 0;

        foreach (var current in Sdk) {
            depth += current switch { '{' => 1, '}' => -1, _ => 0 };
            depth.ShouldBeGreaterThanOrEqualTo(0);
        }

        depth.ShouldBe(0);
    }

    // ── Determinism, which is a correctness property for a file that is diffed ─────────────────

    [Fact]
    public void EverySurfaceIsStableAcrossRuns() {
        // ⚠ Two independent emissions, not one compared with itself: a generator that captured a
        // dictionary's iteration order would still be self-consistent within one run.
        DeterministicJson.ToText(CliEmitter.Emit(Document)).ShouldBe(DeterministicJson.ToText(Cli));
        DeterministicJson.ToText(FormsEmitter.Emit(Document)).ShouldBe(DeterministicJson.ToText(Forms));
        SdkEmitter.Emit(Document).ShouldBe(Sdk);
    }

    [Fact]
    public void NoSurfaceCarriesAPathATimestampOrAMachineName() {
        foreach (var text in new[] { DeterministicJson.ToText(Cli), DeterministicJson.ToText(Forms), Sdk }) {
            text.ShouldNotContain(Environment.MachineName, Case.Insensitive);
            text.ShouldNotContain(
                DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture) + "-"
                + DateTime.UtcNow.Month.ToString("00", CultureInfo.InvariantCulture) + "-"
                + DateTime.UtcNow.Day.ToString("00", CultureInfo.InvariantCulture)
            );
        }
    }

    [Fact]
    public void EveryEmittedNameIsInvariantlyCased() {
        // ⚠ The Turkish dotless ı. ToLower/ToUpper under tr-TR would emit --clusterıd and PostgreSQLİd,
        // and the file would differ from the one CI checked in. Every casing call is the Invariant one;
        // this asserts the outcome rather than the call.
        DeterministicJson.ToText(Cli).ShouldNotContain("ı");
        Sdk.ShouldNotContain("İ");
    }

    // ── Colliding names, on the two surfaces that derive one from a type path ──────────────────
    //
    // ⚠ Both of these were found by reading rather than by a red test, and both fail the same way:
    // the surface silently drops a resource type and nothing says so. A flat `kafkaClustersTopics`
    // and a nested `kafkaClusters/topics` are the pair that does it — they kebab and Pascal to the
    // same thing — and they are legal registrations that no gate refused.

    /// <summary>
    ///     A registry with two types whose CLI command names and SDK model names both collide.
    /// </summary>
    static FakeRegistry Colliding(DisplayMetadata? nestedDisplay = null) =>
        new FakeRegistry {
            Namespaces = ["CyberCloud.Streaming"],
            Types = [
                new ResourceTypeRegistration {
                    Type = new("CyberCloud.Streaming", "kafkaClustersTopics"),
                    ApiVersions = [new(ApiVersion.Parse(Fixtures.FirstVersion), ResourceSchema.Of([]))],
                    Display = new("Topic", "Topics", "topic", "A topic.")
                },
                new ResourceTypeRegistration {
                    Type = new("CyberCloud.Streaming", "kafkaClusters/topics"),
                    ApiVersions = [new(ApiVersion.Parse(Fixtures.FirstVersion), ResourceSchema.Of([]))],
                    Display = nestedDisplay ?? default
                }
            ]
        };

    [Fact]
    public void TwoTypesThatKebabToOneCommandNameFailRatherThanOneVanishing() {
        // ⚠ `commands[name] = …` is an indexer that REPLACES. Before this guard the second type
        // overwrote the first and the verb tree shipped one command where two belonged — a resource
        // type absent from the CLI, with a green build.
        var document = OpenApiEmitter.Emit(Colliding(), ApiVersion.Parse(Fixtures.FirstVersion));

        var thrown = Should.Throw<InvalidOperationException>(() => CliEmitter.Emit(document));

        thrown.Message.ShouldContain("kafka-clusters-topics");
        thrown.Message.ShouldContain("vanish");
    }

    [Fact]
    public void TwoTypesInOneNamespaceCannotShareAnSdkModelName() {
        // ⚠ The provider prefix cannot separate these: the namespace is the same, so both resolve to
        // `StreamingTopic` and the generated file declares one class name twice. The fallback is the
        // type path, which the registry already keys on.
        var document = OpenApiEmitter.Emit(
            Colliding(new DisplayMetadata("Topic", "Topics", "topic", "A topic.")),
            ApiVersion.Parse(Fixtures.FirstVersion)
        );

        var sdk = SdkEmitter.Emit(document);

        sdk.ShouldContain("StreamingKafkaClustersTopics");

        // …and the two are genuinely distinct rather than one having replaced the other.
        var classes = sdk.Split("public sealed partial class ", StringSplitOptions.None).Length - 1;
        classes.ShouldBeGreaterThan(1);
    }

    /// <summary>
    ///     ⚠ <b>The count check in <c>DerivedSurfaces.CliProblems</c> must not fire on a healthy
    ///     registry</b> — it runs on every <c>Generate</c>, so a miscounted check would fail every
    ///     build rather than none. It is the backstop for the collision above, catching a type lost
    ///     for any future reason; the emitter's throw is the primary guard, and a hand-edited tree is
    ///     already caught by the byte comparison.
    /// </summary>
    [Fact]
    public void TheVerbTreeHasExactlyOneCommandPerResourceTypeAndTheCheckAgrees() {
        var document = OpenApiEmitter.Emit(Fixtures.Postgres(), ApiVersion.Parse(Fixtures.FirstVersion));
        var tree = CliEmitter.Emit(document);

        var commands = tree["groups"]!.AsObject()
            .Sum(group => group.Value!["commands"]!.AsObject().Count);

        commands.ShouldBe(DocumentReader.TypesOf(document).Length);

        // …including the nested one, which is the type the collision would have eaten.
        tree["groups"]!["dbforpostgresql"]!["commands"]!.AsObject()
            .ContainsKey("servers-databases").ShouldBeTrue();

        DerivedSurfaces.Generate(
            new Dictionary<string, JsonObject> { [Fixtures.FirstVersion] = document },
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            write: false
        ).Documents.SelectMany(x => x.Problems).ShouldBeEmpty();
    }
}
