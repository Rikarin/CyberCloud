using CyberCloud.ResourceManager.Contracts.Generation;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     ADR-012's fifth surface: the non-<c>@internal</c> <c>@param</c> block of a managed chart's
///     <c>values.yaml</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here is one of the failure classes this repository has actually had</b>,
///         rather than a walk of the happy path: a gate that inspects nothing and passes; an emitter
///         whose output its own reader refuses; one character of casing; a hand-written region eaten
///         by a generator; a regeneration that is not stable; and a registry fact that reaches the
///         other four surfaces and stops here.
///     </para>
///     <para>
///         ⚠ <b>The <see cref="Subset" /> checker below is a checker and not a parser, and the
///         difference matters.</b> The real reader of this format is in <c>build/Build.Charts.cs</c>,
///         which is a Nuke project no test assembly can reference — <c>build/_build.csproj</c> is
///         deliberately outside the solution. So the round trip that matters happens in the pipeline
///         rather than here: <c>Build.Charts</c> writes the block and then parses the file it just
///         wrote, with line numbers, before generating <c>values.schema.json</c> from it. What is
///         asserted here is charts/README.md § The values subset, restated as predicates, so that a
///         construct outside the subset fails in a suite rather than only in a build.
///     </para>
/// </remarks>
public sealed class ChartAnnotationTests {
    // ── The fixture: charts/managed/postgres/values.yaml's 26 API rows, as a ResourceSchema ────

    /// <summary>
    ///     The authored schema a Postgres provider would declare, mirroring the 26 non-<c>@internal</c>
    ///     rows of charts/managed/postgres/values.yaml.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every row is one the hand-written chart already has, so this is the pair that does not
    ///     exist in the tree — see <see cref="AGateWithNoPairSaysSoRatherThanPassing" /> — made
    ///     explicit in a test where it can be asserted against.
    /// </remarks>
    static ResourceSchema Postgres() =>
        ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Required: true, Description: "The configuration."),
            new("/properties/version", SchemaKind.Text, Required: true, Description: "Major PostgreSQL version.") {
                AllowedValues = ["16", "17", "18"],
                DefaultJson = "\"17\""
            },
            new("/properties/replicas", SchemaKind.WholeNumber, Required: true, Description: "Instances, including the primary.") {
                Minimum = 1,
                Maximum = 5,
                DefaultJson = "2"
            },
            new("/properties/synchronousReplication", SchemaKind.Boolean, Description: "Whether commits wait for a replica.") {
                DefaultJson = "false"
            },
            new("/properties/sizing", SchemaKind.Nested, Description: "CPU and memory."),
            new("/properties/sizing/preset", SchemaKind.Text, Description: "A sizing preset.") {
                AllowedValues = ["s1.nano", "s1.micro", "s1.small", "s1.medium"],
                DefaultJson = "\"s1.small\""
            },
            new("/properties/sizing/cpu", SchemaKind.Text, Description: "Explicit vCPU quantity."),
            new("/properties/sizing/memory", SchemaKind.Text, Description: "Explicit memory quantity."),
            new("/properties/storage", SchemaKind.Nested, Description: "The data volume."),
            new("/properties/storage/size", SchemaKind.Text, Required: true, Description: "Data volume size.") {
                DefaultJson = "\"20Gi\""
            },
            new("/properties/storage/class", SchemaKind.Text, Description: "StorageClass name.") {
                Widget = WidgetHint.StorageClass,
                Immutable = true
            },
            new("/properties/storage/walSize", SchemaKind.Text, Description: "Size of the WAL volume."),
            new("/properties/pooling", SchemaKind.Nested, Description: "PgBouncer in front of the cluster."),
            new("/properties/pooling/enabled", SchemaKind.Boolean, Description: "Whether to run a pooler.") {
                DefaultJson = "true"
            },
            new("/properties/pooling/mode", SchemaKind.Text, Description: "PgBouncer pooling mode.") {
                AllowedValues = ["session", "transaction", "statement"],
                DefaultJson = "\"transaction\""
            },
            new("/properties/pooling/instances", SchemaKind.WholeNumber, Description: "Number of pooler pods.") {
                Minimum = 1,
                Maximum = 8,
                DefaultJson = "2"
            },
            new("/properties/extensions", SchemaKind.Array, Description: "Extensions to install.") {
                ElementKind = SchemaKind.Text,
                AllowedValues = ["pgvector", "postgis", "timescaledb"],
                DefaultJson = "[]"
            },
            new("/properties/backup", SchemaKind.Nested, Description: "Backup to the tenant's object store."),
            new("/properties/backup/enabled", SchemaKind.Boolean, Description: "Whether backup runs.") {
                DefaultJson = "true"
            },
            new("/properties/backup/retentionDays", SchemaKind.WholeNumber, Description: "How long backups are kept.") {
                Minimum = 1,
                Maximum = 365,
                DefaultJson = "14"
            },
            new("/properties/backup/destinationPath", SchemaKind.Text, Description: "Object-store URL for backups."),
            new("/properties/bootstrap", SchemaKind.Nested, Description: "What exists on first start."),
            new("/properties/bootstrap/database", SchemaKind.Text, Description: "The application database.") {
                DefaultJson = "\"app\""
            },
            new("/properties/bootstrap/owner", SchemaKind.Text, Description: "Role that owns the database.") {
                DefaultJson = "\"app\""
            },
            new("/properties/bootstrap/password", SchemaKind.Text, Secret: true, Description: "Password for the owning role."),
            new("/properties/monitoring", SchemaKind.Nested, Description: "What the platform scrapes."),
            new("/properties/monitoring/enabled", SchemaKind.Boolean, Description: "Whether a PodMonitor is emitted.") {
                DefaultJson = "true"
            }
        ]);

    static string Block => Emitted(Postgres());

    static string Emitted(ResourceSchema schema) {
        var block = ChartAnnotationEmitter.Emit(schema);
        block.Problems.ShouldBeEmpty();
        return block.Text;
    }

    /// <summary>
    ///     The hand-written region of charts/managed/postgres/values.yaml: the header comment and the
    ///     ten <c>@internal</c> rows, verbatim.
    /// </summary>
    const string Header =
        "# Managed PostgreSQL — the configuration surface, once.\n"
        + "#\n"
        + "# ⚠ values.schema.json next to this file is GENERATED from the annotations below.\n"
        + "\n";

    const string InternalTail =
        "## @param platform {object} Resource identity injected by the reconciler.\n"
        + "## @internal Written by the reconciler from the resource's own identity.\n"
        + "platform:\n"
        + "  ## @param tenantId {string} Tenant GUID.\n"
        + "  tenantId: \"\"\n"
        + "  ## @param resourceGroup {string} Resource group name.\n"
        + "  resourceGroup: \"\"\n"
        + "  ## @param managedBy {string} Value of the cybercloud.io/managed-by label.\n"
        + "  managedBy: cybercloud\n"
        + "\n"
        + "## @param imageName {string} Override the operator's default image.\n"
        + "## @internal A pinned image defeats automatic minor upgrades.\n"
        + "imageName: \"\"\n"
        + "\n"
        + "## @param nameOverride {string} Replace the generated object-name stem.\n"
        + "## @internal Helm plumbing. Nothing about it belongs in a resource body.\n"
        + "nameOverride: \"\"\n";

    static string ValuesFile(string block) => Header + block + "\n" + InternalTail;

    // ── (a) A gate that inspects nothing and passes ────────────────────────────────────────────

    [Fact]
    public void AGateWithNoPairSaysSoRatherThanPassing() {
        // ⚠ THE FAILURE THIS IS MODELLED ON. Build.Generate.cs's provider discovery once matched a
        // fixed nesting depth, found zero providers with one sitting in the solution, and reported
        // success. The chart surface starts in exactly that state: charts/managed/postgres declares
        // CyberCloud.DBforPostgreSQL/servers, no C# provider declares that type, and the only provider
        // in the tree renders no chart. So "clean" must not be the whole answer.
        using var tree = new ChartTree();
        tree.Write("managed/postgres", "CyberCloud.DBforPostgreSQL/servers", "2026-08-01", ValuesFile(Block));

        var report = ChartSurfaces.Generate(new FakeRegistry(), tree.Root, write: false);

        report.Pairs.ShouldBe(0);
        report.ManagedCharts.ShouldBe(1);
        report.TypesNamingAChart.ShouldBe(0);

        // Clean and vacuous at the same time — which is the whole point of having both.
        report.IsClean.ShouldBeTrue();
        report.IsVacuous.ShouldBeTrue();

        // And the chart that nothing claims is named, so "no pair" never arrives without which end is
        // missing. A count of zero with no names is the pass nobody reads.
        report.Unpaired.ShouldHaveSingleItem();
        report.Unpaired[0].ShouldContain("managed/postgres");
        report.Unpaired[0].ShouldContain(".Chart(\"managed/postgres\")");
    }

    [Fact]
    public void ATypeNamingAChartThatIsNotInTheTreeIsTheOtherHalfOfTheSameMismatch() {
        using var tree = new ChartTree();

        var report = ChartSurfaces.Generate(Registry("managed/postgres"), tree.Root, write: false);

        report.Pairs.ShouldBe(0);
        report.IsVacuous.ShouldBeTrue();
        report.TypesNamingAChart.ShouldBe(1);
        report.Unpaired.ShouldHaveSingleItem();
        report.Unpaired[0].ShouldContain("CyberCloud.DBforPostgreSQL/servers");
        report.Unpaired[0].ShouldContain("Chart.yaml does not exist");
    }

    [Fact]
    public void APairThatExistsIsActuallyCompared() {
        // The counterpart to the two above: with both ends present the run is no longer vacuous, and
        // an unchanged file is reported as unchanged rather than as new.
        using var tree = new ChartTree();
        tree.Write("managed/postgres", "CyberCloud.DBforPostgreSQL/servers", "2026-08-01", ValuesFile(Block));

        var report = ChartSurfaces.Generate(Registry("managed/postgres"), tree.Root, write: false);

        report.IsVacuous.ShouldBeFalse();
        report.Pairs.ShouldBe(1);
        report.Unpaired.ShouldBeEmpty();
        report.Documents[0].Published.ShouldBeTrue();
        report.Documents[0].Drifted.ShouldBeFalse();
        report.Documents[0].Problems.ShouldBeEmpty();
    }

    [Fact]
    public void AChartWhoseChartYamlNamesAnotherTypeIsRefusedRatherThanRewritten() {
        using var tree = new ChartTree();
        tree.Write("managed/postgres", "CyberCloud.Sample/widgets", "2026-08-01", ValuesFile(Block));

        var report = ChartSurfaces.Generate(Registry("managed/postgres"), tree.Root, write: true);

        report.IsClean.ShouldBeFalse();
        report.Documents[0].Problems.ShouldContain(x => x.Contains("disagree", StringComparison.Ordinal));
        // ⚠ And nothing was written: a pairing whose two ends disagree is not a pairing to generate from.
        tree.Read("managed/postgres").ShouldBe(ValuesFile(Block));
    }

    [Fact]
    public void AChartNamingAnApiVersionTheTypeDoesNotDeclareIsRefused() {
        using var tree = new ChartTree();
        tree.Write("managed/postgres", "CyberCloud.DBforPostgreSQL/servers", "2019-01-01", ValuesFile(Block));

        var report = ChartSurfaces.Generate(Registry("managed/postgres"), tree.Root, write: true);

        report.IsClean.ShouldBeFalse();
        report.Documents[0].Problems.ShouldContain(x => x.Contains("2019-01-01", StringComparison.Ordinal));
    }

    // ── (b) The emitter produces YAML the reader cannot read ───────────────────────────────────

    [Fact]
    public void EveryEmittedLineIsInsideTheValuesSubset() {
        // ⚠ build/Build.Charts.cs's reader refuses everything outside a deliberately narrow subset, and
        // every refusal is a build failure. An emitter that wrote a tab, a block sequence, a null or an
        // inline comment would fail the build on its own output.
        Subset.Problems(Block).ShouldBeEmpty();
    }

    [Fact]
    public void TheSubsetCheckerActuallyRefusesThingsRatherThanApprovingEverything() {
        // ⚠ A checker nobody has seen say no is a checker that says yes. Each of these is a construct
        // charts/README.md § The values subset names, and each must be caught.
        Subset.Problems("## @param a {string} A.\n\ta: \"\"\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {array} A.\na:\n  - one\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\na: null\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\na: value # why\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\n\na: \"\"\n").ShouldNotBeEmpty();
        Subset.Problems("## @colour a\n## @param a {string} A.\na: \"\"\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {widget} A.\na: \"\"\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\n   a: \"\"\n").ShouldNotBeEmpty();
    }

    [Fact]
    public void EveryAnnotationBlockSitsDirectlyAboveTheKeyItDescribes() {
        // The block is the run of `## @` lines immediately above the key — a blank line or an ordinary
        // comment between them is a build failure, so that moving a key moves its annotation with it.
        var lines = Block.Split('\n');

        for (var i = 0; i < lines.Length; i++) {
            if (!lines[i].TrimStart().StartsWith("## @param ", StringComparison.Ordinal)) {
                continue;
            }

            var name = lines[i].TrimStart()["## @param ".Length..].Split(' ')[0];
            var key = lines.Skip(i + 1).First(x => !x.TrimStart().StartsWith("## @", StringComparison.Ordinal));

            key.TrimStart().ShouldStartWith(name + ":");
        }
    }

    [Fact]
    public void ADescriptionThatWouldEndItsOwnBlockIsRefused() {
        // A newline in a Description would close the annotation block above the key, and the reader
        // would report "an annotation block with no key under it" against a generated file.
        var block = ChartAnnotationEmitter.Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested),
            new("/properties/a", SchemaKind.Text, Description: "One.\nTwo.")
        ]));

        block.Problems.ShouldContain(x => x.Contains("more than one line", StringComparison.Ordinal));
        block.Text.ShouldBeEmpty();
    }

    [Fact]
    public void AStringDefaultThatWouldReadAsANumberIsQuoted() =>
        // ⚠ `version: 17` unquoted is an integer to the reader, which would then fail the chart it had
        // just generated: "'version' is declared {string} and its value reads as an integer". A
        // generator failing the build on its own output is the worst kind of red.
        Block.ShouldContain("version: \"17\"");

    [Fact]
    public void AnEmptyStringABooleanAndASequenceAreSpelledTheWayTheReaderExpects() {
        Block.ShouldContain("walSize: \"\"");
        Block.ShouldContain("enabled: true");
        Block.ShouldContain("extensions: []");
        // No null anywhere: a key with no value has no default, and helm would reject it against the
        // type the same pipeline generates.
        Block.ShouldNotContain(": null");
    }

    // ── (c) One character of casing ────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPropertyNameRoundTripsWithItsExactCasing() {
        // ⚠ THE FAILURE THIS IS MODELLED ON. A resourcegroup/resourceGroup mismatch was failing every
        // create in this platform and surfaced as a 404 with the reason only in a log line. The key
        // written into values.yaml is what a rendered manifest reads, so one character is the whole
        // resource.
        foreach (var property in Postgres().Properties) {
            if (property.Kind is SchemaKind.Nested) {
                continue;
            }

            Block.ShouldContain(property.Name + ":");
        }

        Block.ShouldContain("synchronousReplication:");
        Block.ShouldContain("retentionDays:");
        Block.ShouldContain("destinationPath:");
        Block.ShouldContain("walSize:");

        // ⚠ The lower-cased spellings would be a different key in a different manifest. Compared
        // ordinally on purpose — Shouldly's ShouldNotContain is case-INSENSITIVE by default, which
        // would make this assertion fail against the correctly cased output and pass against nothing.
        Block.Contains("synchronousreplication", StringComparison.Ordinal).ShouldBeFalse();
        Block.Contains("retentiondays", StringComparison.Ordinal).ShouldBeFalse();
        Block.Contains("walsize", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void TheAnnotationNameAndTheKeyAreTheSameBytes() {
        // build/Build.Charts.cs checks the `@param` name against the key rather than using it, so the
        // two disagreeing is a build failure. Emitting them from one string is what makes that check
        // pass by construction rather than by luck.
        var names = Regex.Matches(Block, @"^\s*## @param (?<name>\S+) ", RegexOptions.Multiline)
            .Select(x => x.Groups["name"].Value)
            .ToList();

        var keys = Regex.Matches(Block, @"^\s*(?<key>[A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Multiline)
            .Select(x => x.Groups["key"].Value)
            .ToList();

        // The 26 rows under /properties. `/properties` itself is the values file's root and is not a
        // key in it — see the remarks on ChartAnnotationEmitter.RootPointer.
        names.Count.ShouldBe(26);
        keys.ShouldBe(names);
    }

    [Fact]
    public void AMemberNameThatIsNotAValuesKeyIsRefusedRatherThanMangled() {
        var block = ChartAnnotationEmitter.Emit(new ResourceSchema {
            Properties = [
                new("/properties", SchemaKind.Nested, Description: "The configuration."),
                new("/properties/storage-class", SchemaKind.Text, Description: "A hyphenated name.")
            ]
        });

        block.Problems.ShouldContain(x => x.Contains("storage-class", StringComparison.Ordinal));
    }

    // ── (d) The @internal rows get eaten ───────────────────────────────────────────────────────

    [Fact]
    public void TheInternalRowsSurviveRegenerationByteIdentically() {
        // ⚠ Ten of the 36 rows in charts/managed/postgres/values.yaml are @internal: Helm plumbing,
        // seven rows of reconciler-injected identity and an operator escape hatch. None is in any
        // ResourceSchema and none ever will be. A generator that rewrote the whole file would eat them.
        var rewritten = ChartAnnotationEmitter.Rewrite(ValuesFile("## @param stale {string} Gone.\nstale: \"\"\n"), Block);

        rewritten.Problems.ShouldBeEmpty();
        rewritten.Text.ShouldEndWith(InternalTail);
        rewritten.Text.ShouldStartWith(Header);
        rewritten.PreservedInternalLines.ShouldBeGreaterThan(0);

        // The key that is no longer in the registry is gone; the @internal ones are not.
        rewritten.Text.ShouldNotContain("stale:");
        rewritten.Text.ShouldContain("nameOverride: \"\"");
        rewritten.Text.ShouldContain("resourceGroup: \"\"");
    }

    [Fact]
    public void AnInternalKeyAboveAGeneratedOneIsRefusedRatherThanReordered() {
        // An in-place rewrite needs the generated region to be one contiguous run. Interleaved, "where
        // does a new key go" has no deterministic answer — and a generated file whose order depends on
        // the previous file's order cannot be regenerated from scratch.
        var interleaved =
            "## @param imageName {string} Override the image.\n"
            + "## @internal An escape hatch.\n"
            + "imageName: \"\"\n"
            + "\n"
            + "## @param version {string} Major version.\n"
            + "version: \"17\"\n";

        var rewritten = ChartAnnotationEmitter.Rewrite(interleaved, Block);

        rewritten.Problems.ShouldNotBeEmpty();
        rewritten.Problems[0].ShouldContain("imageName");
        rewritten.Text.ShouldBeEmpty();
    }

    [Fact]
    public void TheInternalRegionIsCopiedRatherThanReRendered() {
        // Byte-for-byte, including the quoting style and the wording of every comment. Re-rendering is
        // how `managedBy: cybercloud` quietly becomes `managedBy: "cybercloud"` and a diff appears in a
        // region nobody edited.
        var rewritten = ChartAnnotationEmitter.Rewrite(ValuesFile(Block), Block);

        rewritten.Text.ShouldBe(ValuesFile(Block));
    }

    // ── (e) Regeneration is not stable ─────────────────────────────────────────────────────────

    [Fact]
    public void TwoEmissionsFromOneSchemaAreTheSameBytes() =>
        // ⚠ Two independent emissions, not one compared with itself: a generator that captured a
        // dictionary's iteration order would still be self-consistent within one run. The Generated
        // surfaces gate compares byte-for-byte, so this is a correctness property.
        ChartAnnotationEmitter.Emit(Postgres()).Text.ShouldBe(ChartAnnotationEmitter.Emit(Postgres()).Text);

    [Fact]
    public void RewritingAnAlreadyRewrittenFileChangesNothing() {
        var once = ChartAnnotationEmitter.Rewrite(ValuesFile("## @param stale {string} Gone.\nstale: \"\"\n"), Block);
        var twice = ChartAnnotationEmitter.Rewrite(once.Text, Block);

        twice.Text.ShouldBe(once.Text);
    }

    [Fact]
    public void PropertiesKeepTheirDeclarationOrderRatherThanASortOne() {
        // ⚠ The one place this surface deliberately differs from the other four. They read the emitted
        // document, whose `properties` are sorted ordinally for determinism; a values.yaml sorted that
        // way would open with `backup` and close with `version`, which is a configuration file nobody
        // can read. Declaration order is available because this emitter reads the registry.
        var roots = Regex.Matches(Block, @"^(?<key>[A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Multiline)
            .Select(x => x.Groups["key"].Value)
            .ToList();

        roots[0].ShouldBe("version");
        roots[1].ShouldBe("replicas");
        roots[^1].ShouldBe("monitoring");
        roots.ShouldNotBe(roots.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void NothingEmittedCarriesAPathATimestampOrAMachineName() =>
        Block.ShouldNotContain(Environment.MachineName, Case.Insensitive);

    // ── (f) A fact the registry gained that the chart block silently drops ─────────────────────

    [Theory]
    [InlineData("format")]
    [InlineData("pattern")]
    [InlineData("length")]
    [InlineData("example")]
    [InlineData("nullable")]
    [InlineData("element")]
    public void AFactWithNoAnnotationSyntaxIsRefusedRatherThanDropped(string directive) {
        // ⚠ THE FAILURE THIS IS MODELLED ON, and DerivedSurfaceTests exists for it on the other four
        // surfaces. SchemaProperty carries seven facts the chart vocabulary cannot say. Dropping one
        // silently would mean a chart rendering a cluster from values the API would have refused —
        // and the drop would be invisible in the file that claims to be the configuration surface.
        var block = ChartAnnotationEmitter.Emit(WithGap(directive));

        block.Text.ShouldBeEmpty();
        block.Problems.ShouldHaveSingleItem();
        block.Problems[0].ShouldContain("/properties/gap");
        block.Problems[0].ShouldContain("@" + directive);
    }

    static ResourceSchema WithGap(string directive) {
        var root = new SchemaProperty("/properties", SchemaKind.Nested, Description: "The configuration.");

        SchemaProperty gap = directive switch {
            "format" => new("/properties/gap", SchemaKind.Text, Description: "A gap.") {
                Format = SchemaFormat.Uuid
            },
            "pattern" => new("/properties/gap", SchemaKind.Text, Description: "A gap.") {
                Pattern = "[a-z]+"
            },
            "length" => new("/properties/gap", SchemaKind.Text, Description: "A gap.") { MinLength = 12 },
            "example" => new("/properties/gap", SchemaKind.Text, Description: "A gap.") {
                ExampleJson = "\"eu-central\""
            },
            "nullable" => new("/properties/gap", SchemaKind.Text, Description: "A gap.") { Nullable = true },
            _ => new("/properties/gap", SchemaKind.Array, Description: "A gap.") {
                ElementKind = SchemaKind.WholeNumber,
                DefaultJson = "[]"
            }
        };

        return ResourceSchema.Of([root, gap]);
    }

    [Fact]
    public void ATextElementKindIsTheOneArrayShapeTheVocabularyReaches() =>
        // `@enum` on an array becomes `items: {type: string, enum: [...]}` and build/Build.Charts.cs
        // hard-codes that string, so a text element is expressible and nothing else is.
        Block.ShouldContain("## @enum pgvector | postgis | timescaledb");

    [Fact]
    public void AReadOnlyPropertyIsExcludedRatherThanRefused() {
        // Not a gap: a values key is by construction something the chart's caller sets, and
        // server-owned state has no home in a values file at all. The generated CLI drops
        // --provisioning-state for the same reason.
        var block = ChartAnnotationEmitter.Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            new("/properties/state", SchemaKind.Text, ReadOnly: true, Description: "Server-owned."),
            new("/properties/version", SchemaKind.Text, Description: "Tenant-owned.") {
                DefaultJson = "\"17\""
            }
        ]));

        block.Problems.ShouldBeEmpty();
        block.Text.ShouldNotContain("state:");
        block.Text.ShouldContain("version:");
    }

    [Fact]
    public void ANumberWithNoDeclaredDefaultIsRefusedRatherThanGivenAnInventedZero() {
        // Every values key carries a value; a null is refused by the reader and by helm. There is no
        // empty spelling of a number, and an invented 0 is a value a tenant would get without anybody
        // having chosen it — and it may sit outside the property's own @range.
        var block = ChartAnnotationEmitter.Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            new("/properties/replicas", SchemaKind.WholeNumber, Description: "Instances.") {
                Minimum = 1,
                Maximum = 5
            }
        ]));

        block.Problems.ShouldHaveSingleItem();
        block.Problems[0].ShouldContain("DefaultJson");
    }

    [Fact]
    public void ADefaultOutsideItsOwnConstraintsIsRefusedAgainstTheRegistryRatherThanTheChart() {
        // build/Build.Charts.cs would report this against values.yaml — which by then is a generated
        // file — and send the author to fix the wrong end.
        var block = ChartAnnotationEmitter.Emit(new ResourceSchema {
            Properties = [
                new("/properties", SchemaKind.Nested, Description: "The configuration."),
                new("/properties/replicas", SchemaKind.WholeNumber, Description: "Instances.") {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "9"
                }
            ]
        });

        block.Problems.ShouldContain(x => x.Contains("its own constraints reject", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryExpressibleFactActuallyReachesTheBlock() {
        // The other half of the gap tests: what the vocabulary does reach must be there. Required,
        // secret, immutable, the closed set, the bounds and the widget hint each become a directive.
        Block.ShouldContain("## @required");
        Block.ShouldContain("## @secret");
        Block.ShouldContain("## @immutable");
        Block.ShouldContain("## @enum 16 | 17 | 18");
        Block.ShouldContain("## @range 1..5");
        Block.ShouldContain("## @widget storageclass");
        Block.ShouldContain("## @param replicas {integer} Instances, including the primary.");
        // ⚠ `@internal` is never generated. It is the chart author's word for a row that is not API,
        // and a generator that emitted one would be claiming the registry had an opinion about Helm
        // plumbing.
        Block.ShouldNotContain("## @internal");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────

    static FakeRegistry Registry(string chart) =>
        new FakeRegistry {
            Namespaces = ["CyberCloud.DBforPostgreSQL"],
            Types = [
                new ResourceTypeRegistration {
                    Type = new("CyberCloud.DBforPostgreSQL", "servers"),
                    ApiVersions = [new(ApiVersion.Parse("2026-08-01"), Postgres())],
                    Chart = chart
                }
            ]
        };

    /// <summary>A throw-away <c>charts/</c> on disk.</summary>
    sealed class ChartTree : IDisposable {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "cybercloud-charts-" + Guid.NewGuid().ToString("N"));

        public void Write(string chart, string resourceType, string apiVersion, string values) {
            var directory = Path.Combine(Root, chart.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);

            File.WriteAllText(
                Path.Combine(directory, "Chart.yaml"),
                "name: " + chart.Split('/')[^1] + "\nversion: 0.1.0\nannotations:\n"
                + "  cybercloud.io/resource-type: " + resourceType + "\n"
                + "  cybercloud.io/api-version: \"" + apiVersion + "\"\n"
            );

            File.WriteAllText(Path.Combine(directory, ChartAnnotationEmitter.FileName), values);
        }

        public string Read(string chart) =>
            File.ReadAllText(Path.Combine(
                Root,
                chart.Replace('/', Path.DirectorySeparatorChar),
                ChartAnnotationEmitter.FileName
            ));

        public void Dispose() {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    /// <summary>
    ///     charts/README.md § The values subset and § The annotation format, restated as predicates.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A checker, not a parser, and not the reader.</b> See the remarks on the class: the real
    ///     reader is in <c>build/Build.Charts.cs</c>, which no test assembly can reference, and the
    ///     round trip through it happens in <c>Build.Charts</c> itself — the block is written and then
    ///     parsed before <c>values.schema.json</c> is generated from it. This catches the same
    ///     constructs in a suite so that a subset violation does not have to wait for a build.
    /// </remarks>
    static class Subset {
        static readonly ImmutableArray<string> Directives =
            ["enum", "immutable", "internal", "param", "range", "required", "secret", "widget"];

        static readonly ImmutableArray<string> Types =
            ["array", "boolean", "integer", "number", "object", "string"];

        static readonly Regex Param = new(
            @"^(?<name>[A-Za-z_][A-Za-z0-9_]*) \{(?<type>[A-Za-z]+)\} (?<description>\S.*)$"
        );

        static readonly Regex Key = new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*):[ ]*(?<value>.*)$");

        public static ImmutableArray<string> Problems(string block) {
            var problems = new List<string>();
            var lines = block.Split('\n');
            var pending = 0;

            for (var i = 0; i < lines.Length; i++) {
                var raw = lines[i];
                var line = i + 1;

                if (raw.Contains('\t', StringComparison.Ordinal)) {
                    problems.Add($"{line}: contains a tab.");
                    continue;
                }

                if (raw.Trim().Length == 0) {
                    if (pending > 0) {
                        problems.Add($"{line}: a blank line separates a block from its key.");
                    }

                    pending = 0;
                    continue;
                }

                var indent = raw.Length - raw.TrimStart(' ').Length;
                var trimmed = raw[indent..];

                if (indent % 2 != 0) {
                    problems.Add($"{line}: indented {indent} space(s).");
                    continue;
                }

                if (trimmed.StartsWith("## @", StringComparison.Ordinal)) {
                    var body = trimmed[4..];
                    var verb = body.Split(' ')[0];

                    if (!Directives.Contains(verb, StringComparer.Ordinal)) {
                        problems.Add($"{line}: `@{verb}` is not a directive.");
                    } else if (verb == "param") {
                        var parsed = Param.Match(body[(verb.Length + 1)..]);

                        if (!parsed.Success) {
                            problems.Add($"{line}: malformed `@param`.");
                        } else if (!Types.Contains(parsed.Groups["type"].Value, StringComparer.Ordinal)) {
                            problems.Add($"{line}: `{{{parsed.Groups["type"].Value}}}` is not a type.");
                        }
                    }

                    pending++;
                    continue;
                }

                if (trimmed[0] == '#') {
                    if (pending > 0) {
                        problems.Add($"{line}: an ordinary comment interrupts an annotation block.");
                    }

                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-") {
                    problems.Add($"{line}: a block sequence.");
                    pending = 0;
                    continue;
                }

                var key = Key.Match(trimmed);

                if (!key.Success) {
                    problems.Add($"{line}: not a `key: value` line, a comment or a blank.");
                    pending = 0;
                    continue;
                }

                if (pending == 0) {
                    problems.Add($"{line}: '{key.Groups["key"].Value}' has no `## @param` annotation.");
                }

                var value = key.Groups["value"].Value.TrimEnd();

                if (value is "null" or "~") {
                    problems.Add($"{line}: a null value.");
                }

                if (value.Length > 0 && value[0] is not ('"' or '\'')
                    && value.Contains(" #", StringComparison.Ordinal)) {
                    problems.Add($"{line}: an inline comment after a value.");
                }

                pending = 0;
            }

            if (pending > 0) {
                problems.Add("an annotation block with no key under it.");
            }

            return [.. problems];
        }
    }
}
