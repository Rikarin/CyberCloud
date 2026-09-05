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
            // ⚠ The quantity pattern is here in full, pipes and all, because a pattern carrying `|` is
            // the case `@pattern` exists to survive: `@enum` splits on that character and `@pattern`
            // must not. CyberCloud.DBforPostgreSQL/servers declares this exact shape.
            new("/properties/sizing/cpu", SchemaKind.Text, Description: "Explicit vCPU quantity.") {
                Pattern = @"(\d+(\.\d+)?(m|k|M|G|Ki|Mi|Gi)?)?",
                DefaultJson = "\"\""
            },
            new("/properties/sizing/memory", SchemaKind.Text, Description: "Explicit memory quantity.") {
                Pattern = @"(\d+(\.\d+)?(m|k|M|G|Ki|Mi|Gi)?)?",
                DefaultJson = "\"\""
            },
            new("/properties/storage", SchemaKind.Nested, Description: "The data volume."),
            new("/properties/storage/size", SchemaKind.Text, Required: true, Description: "Data volume size.") {
                Pattern = @"\d+(\.\d+)?(m|k|M|G|Ki|Mi|Gi)?",
                DefaultJson = "\"20Gi\"",
                ExampleJson = "\"20Gi\""
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
            new("/properties/backup/destinationPath", SchemaKind.Text, Description: "Object-store URL for backups.") {
                DefaultJson = "\"\"",
                ExampleJson = "\"s3://tenant-bucket/postgres\""
            },
            new("/properties/clusterId", SchemaKind.Text, Description: "The cluster's namespace.") {
                Format = SchemaFormat.Uuid,
                Widget = WidgetHint.Cluster
            },
            new("/properties/bootstrap", SchemaKind.Nested, Description: "What exists on first start."),
            new("/properties/bootstrap/database", SchemaKind.Text, Description: "The application database.") {
                Pattern = "[a-z_][a-z0-9_]*",
                MinLength = 1,
                MaxLength = 63,
                DefaultJson = "\"app\""
            },
            // ⚠ A one-sided length, which `@length` spells and `@range` cannot. Kept in the fixture so
            // the open end is emitted and subset-checked on every run rather than only in the test that
            // names it.
            new("/properties/bootstrap/owner", SchemaKind.Text, Description: "Role that owns the database.") {
                MinLength = 1,
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
        var block = Emit(schema);
        block.Problems.ShouldBeEmpty();
        return block.Text;
    }

    /// <summary>
    ///     A schema on its own, with no cluster placement declared.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The empty <c>clusterIdPointer</c> is the point of this helper rather than a
    ///     convenience.</b> <see cref="ChartAnnotationEmitter.Emit" /> takes the pointer with no
    ///     default because a schema cannot say which of its properties is placement — the fixture
    ///     below declares a required uuid called <c>clusterId</c> and nothing about it is
    ///     distinguishable from a tenant-chosen one. These tests are about the schema-to-block
    ///     mapping, so they say "no placement" once, here; the registry fact reaching the emitter is
    ///     <see cref="TheClusterIdIsPlacementAndIsExcludedFromTheChartItPlacesInto" /> and
    ///     <see cref="ThePlacementPointerReachesTheEmitterFromTheRegistrationRatherThanTheSchema" />.
    /// </remarks>
    static ChartAnnotationBlock Emit(ResourceSchema schema) =>
        ChartAnnotationEmitter.Emit(schema, clusterIdPointer: string.Empty);

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
    public void ThePlacementPointerReachesTheEmitterFromTheRegistrationRatherThanTheSchema() {
        // ⚠ THE FAILURE CLASS THIS FILE'S HEADER CALLS "a registry fact that reaches the other four
        // surfaces and stops here". `RequiresCluster` is recorded on the registration, the OpenAPI
        // document and the CLI both carry the property, and the chart is the one surface that must
        // NOT — a chart is rendered into a cluster and has no opinion about which one. The emitter
        // cannot work that out from the schema, so the only thing that can go wrong is the pointer
        // not being handed over, and that is exactly what this asserts: the same fixture, the same
        // chart, one flag apart.
        using var tree = new ChartTree();
        tree.Write("managed/postgres", "CyberCloud.DBforPostgreSQL/servers", "2026-08-01", ValuesFile(Block));

        var report = ChartSurfaces.Generate(Placed("managed/postgres"), tree.Root, write: true);

        report.Documents[0].Problems.ShouldBeEmpty();
        report.Documents[0].Drifted.ShouldBeTrue();

        var rewritten = tree.Read("managed/postgres");

        rewritten.Contains("clusterId", StringComparison.Ordinal).ShouldBeFalse();

        // ⚠ And the hand-written region is still there. An exclusion is a smaller generated block, so
        // it exercises the merge's "the original has a key the registry no longer does" path — the
        // one that ate bootstrap.password when it walked root keys only.
        rewritten.ShouldEndWith(InternalTail);
        rewritten.ShouldStartWith(Header);
        rewritten.ShouldContain("version: \"17\"");
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

        // ⚠ Every directive's argument, not only `@param`'s. `@range 1..` is the one that got past an
        // earlier version of this checker and would have failed the build on generated output.
        Subset.Problems("## @param a {integer} A.\n## @range 1..\na: 2\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {integer} A.\n## @range ..5\na: 2\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\n## @widget Storage Class\na: \"\"\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\n## @enum one | | two\na: one\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\n## @required yes\na: \"\"\n").ShouldNotBeEmpty();
        Subset.Problems("## @param a {string} A.\n## @internal\na: \"\"\n").ShouldNotBeEmpty();

        // …and the well-formed forms are still accepted, so the checker is not simply saying no.
        Subset.Problems("## @param a {integer} A.\n## @range 1..5\na: 2\n").ShouldBeEmpty();
        Subset.Problems("## @param a {string} A.\n## @widget storageclass\na: \"\"\n").ShouldBeEmpty();
    }

    [Fact]
    public void AOneSidedBoundIsRefusedBecauseRangeTakesBothEnds() {
        // ⚠ THE BUG THIS CAUGHT IN THIS EMITTER. SchemaProperty lets a property declare a Minimum with
        // no Maximum — Fixtures' own /properties/adminPassword does the string equivalent — and
        // `@range`'s pattern requires both. The obvious emission, `## @range 1..`, is a malformed
        // directive against a file this emitter had just written: the build would fail on its own
        // output, with a line number, pointing at a generated file.
        var block = Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            new("/properties/replicas", SchemaKind.WholeNumber, Description: "Instances.") {
                Minimum = 1,
                DefaultJson = "2"
            }
        ]));

        block.Text.ShouldBeEmpty();
        block.Problems.ShouldContain(x => x.Contains("one-sided numeric bound", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectiveArgumentThatCannotBeSpelledIsRefused() {
        // `@enum` members are separated by `|` and trimmed, so a value carrying either would come back
        // as a different string — or as two.
        var piped = Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            new("/properties/mode", SchemaKind.Text, Description: "A mode.") {
                AllowedValues = ["a|b"],
                DefaultJson = "\"a|b\""
            }
        ]));

        piped.Problems.ShouldContain(x => x.Contains("`@enum` cannot spell", StringComparison.Ordinal));

        // `@widget` renders one scalar field, and build/Build.Charts.cs refuses it on an array — which
        // SchemaProperty.Incoherences permits, so Fixtures' own /properties/allowedRanges is one.
        var widget = Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            new("/properties/ranges", SchemaKind.Array, Description: "CIDR ranges.") {
                ElementKind = SchemaKind.Text,
                Widget = WidgetHint.Cidr,
                DefaultJson = "[]"
            }
        ]));

        widget.Problems.ShouldContain(x => x.Contains("renders one scalar field", StringComparison.Ordinal));
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
        var block = Emit(ResourceSchema.Of([
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

        // The 27 rows under /properties. `/properties` itself is the values file's root and is not a
        // key in it — see the remarks on ChartAnnotationEmitter.RootPointer.
        names.Count.ShouldBe(27);
        keys.ShouldBe(names);
    }

    [Fact]
    public void AMemberNameThatIsNotAValuesKeyIsRefusedRatherThanMangled() {
        var block = Emit(new ResourceSchema {
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
    public void AnInternalRowNESTEDInsideAGeneratedObjectSurvivesToo() {
        // ⚠ THE BUG THE FIRST REAL PAIR FOUND, ON 2026-08-12. Rewrite walked ROOT keys only. The one
        // managed chart has `bootstrap.password` — `@internal`, `@secret` — sitting INSIDE
        // `bootstrap:`, which is a generated key. So the whole `bootstrap` region was replaced and the
        // password row was deleted on every run, while build/Build.Charts.cs printed "The @internal
        // rows were not touched". A generator that eats a hand-written row is bad; one that eats it
        // and reports otherwise is the failure this file's own header calls a diff nobody reads.
        //
        // ⚠ The row must survive AS BYTES, comments and all — it carries the written reason it is not
        // an API property, which is the whole `@internal` discipline.
        const string password =
            "  ## @param password {string} Password for the owning role.\n"
            + "  ## @secret\n"
            + "  ## @internal The reconciler supplies it; a body property would persist plaintext.\n"
            + "  password: \"\"\n";

        var original = Header
            + "## @param bootstrap {object} What exists on first start.\n"
            + "bootstrap:\n"
            + "  ## @param database {string} The application database.\n"
            + "  database: app\n"
            + password
            + "\n"
            + InternalTail;

        var rewritten = ChartAnnotationEmitter.Rewrite(original, Block);

        rewritten.Problems.ShouldBeEmpty();
        rewritten.Text.ShouldContain(password);

        // …and it is still a member of `bootstrap`, not reparented onto whatever came before it. The
        // first version of the recursive merge dropped each nested slice's own `key:` line, which
        // `helm lint` reported as a nil pointer three templates away from the cause.
        var lines = rewritten.Text.Split('\n');
        var key = Array.FindIndex(lines, x => x == "  password: \"\"");

        key.ShouldBeGreaterThan(0);
        lines.Take(key).Last(x => x.Length > 0 && x[0] != ' ' && x[0] != '#').ShouldBe("bootstrap:");

        // The root-level @internal rows are still there as well — the fix did not trade one for the
        // other.
        rewritten.Text.ShouldEndWith(InternalTail);
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
        Emit(Postgres()).Text.ShouldBe(Emit(Postgres()).Text);

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
    [InlineData("nullable")]
    [InlineData("element")]
    public void AFactWithNoAnnotationSyntaxIsRefusedRatherThanDropped(string directive) {
        // ⚠ THE FAILURE THIS IS MODELLED ON, and DerivedSurfaceTests exists for it on the other four
        // surfaces. Dropping one silently would mean a chart rendering a cluster from values the API
        // would have refused — and the drop would be invisible in the file that claims to be the
        // configuration surface.
        //
        // ⚠ THIS THEORY HAD SIX CASES UNTIL 2026-08-12 and now has two. `@format`, `@pattern`,
        // `@length` and `@example` moved from here to
        // EveryFactTheGrownVocabularySpellsReachesTheBlock when CyberCloud.DBforPostgreSQL/servers
        // became the vocabulary's first real user and made thirteen of these refusals the only red
        // gate in the tree. Nullable and a non-text ElementKind stay refused: neither has a user, and
        // both are harder than the four that closed — see the remarks on CheckInexpressible.
        var block = Emit(WithGap(directive));

        block.Text.ShouldBeEmpty();
        block.Problems.ShouldHaveSingleItem();
        block.Problems[0].ShouldContain("/properties/gap");
        block.Problems[0].ShouldContain("@" + directive);
    }

    static ResourceSchema WithGap(string directive) {
        var root = new SchemaProperty("/properties", SchemaKind.Nested, Description: "The configuration.");

        SchemaProperty gap = directive switch {
            "nullable" => new("/properties/gap", SchemaKind.Text, Description: "A gap.") { Nullable = true },
            _ => new("/properties/gap", SchemaKind.Array, Description: "A gap.") {
                ElementKind = SchemaKind.WholeNumber,
                DefaultJson = "[]"
            }
        };

        return ResourceSchema.Of([root, gap]);
    }

    // ── (f2) The four directives that closed, and what they may not carry ──────────────────────

    [Fact]
    public void EveryFactTheGrownVocabularySpellsReachesTheBlock() {
        // The other half of the gap theory, for the four that closed on 2026-08-12. Each was one of
        // the thirteen refusals `./build.sh Charts` reported against
        // CyberCloud.DBforPostgreSQL/servers.
        Block.ShouldContain(@"## @pattern \d+(\.\d+)?(m|k|M|G|Ki|Mi|Gi)?");
        Block.ShouldContain("## @length 1..63");
        Block.ShouldContain("## @example \"20Gi\"");
        Block.ShouldContain("## @format uuid");

        // ⚠ An open end, which `@length` spells and `@range` refuses. The asymmetry is deliberate —
        // "at least one character" is the ordinary shape of a string constraint, and a @length
        // requiring both ends would refuse the commonest case.
        Block.ShouldContain("## @length 1..\n");
    }

    [Fact]
    public void APatternKeepsEveryCharacterThatMeansSomethingToSomethingElse() {
        // ⚠ THE POINT OF `@pattern`, AND THE REASON IT IS NOT SPLIT, QUOTED OR ESCAPED. A regular
        // expression is made of the characters other parts of this format reserve: `|` separates
        // `@enum` members, `#` opens a YAML comment, `:` opens a mapping, `{` opens a `@param` type,
        // and a quote opens a scalar. Every one of them is inert on a `## @pattern` line — the line is
        // a comment, and this is the one directive that takes the rest of the line verbatim. A
        // vocabulary that refused them could not spell the first pattern anybody wrote.
        var pattern = @"^[a-z]#(one|two):\{3\}""x""$";

        var block = Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            // ⚠ No DefaultJson: ResourceSchema.Of checks a default against its own anchored pattern,
            // and the point here is the pattern's transport rather than its defaulting.
            new("/properties/awkward", SchemaKind.Text, Description: "An awkward one.") {
                Pattern = pattern
            }
        ]));

        block.Problems.ShouldBeEmpty();
        block.Text.ShouldContain("## @pattern " + pattern);

        // …and the emitted line is still inside the values subset, so the real reader will take it.
        Subset.Problems(block.Text).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(" [a-z]+", "whitespace")]
    [InlineData("[a-z]+ ", "whitespace")]
    [InlineData("[a-z]+\n[0-9]+", "control character")]
    [InlineData("[a-z]+\t", "whitespace")]
    public void APatternTheDirectiveCannotCarryIsRefusedRatherThanMangled(string pattern, string reason) {
        // ⚠ THE OTHER HALF OF THE SAME DECISION, AND THE DANGEROUS HALF. build/Build.Charts.cs trims
        // the line, then the directive body, then the argument — three separate trims — so a pattern
        // with an edge space comes back as a DIFFERENT pattern and the chart then validates a set of
        // strings the API does not. That is the "constraint that reached the API and not the chart"
        // failure wearing a disguise: the constraint arrives, and means something else. A newline
        // would end the annotation block above the key it describes; a tab is a subset violation.
        var block = Emit(new ResourceSchema {
            Properties = [
                new("/properties", SchemaKind.Nested, Description: "The configuration."),
                new("/properties/gap", SchemaKind.Text, Description: "A gap.") { Pattern = pattern }
            ]
        });

        block.Text.ShouldBeEmpty();
        block.Problems.ShouldContain(x => x.Contains(reason, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A pattern the linear engine refuses is a named problem here too — and it is named even when
    ///     the property also declares a <c>DefaultJson</c>, which is the shape that used to throw (#78).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is #76's hole, one door over, and the door is the reason the test builds its
    ///         schema by object initialiser.</b> #76 made <c>SchemaProperty.Matcher</c>
    ///         <c>RegexOptions.NonBacktracking</c> with no match timeout, so a lookaround, a
    ///         backreference or an atomic group is refused <i>by name</i> when the matcher is built;
    ///         <c>SchemaProperty.Incoherences</c> catches that refusal, and — after #76's review —
    ///         clears <c>Pattern</c> before running the declared literal through the ordinary
    ///         request-path validation, because that path builds the same matcher a second time in
    ///         <c>ResourceSchema.PatternProblem</c>, where nothing is caught on purpose.
    ///         <c>ChartAnnotationEmitter</c> never calls <c>Incoherences</c> and reached that second
    ///         build through <c>CheckAgainstOwnConstraints</c>, so a schema that never passed through
    ///         <c>ResourceSchema.Of</c> — which is precisely what every belt check in this emitter
    ///         exists for, and what <c>Emit(new ResourceSchema { … })</c> is here — threw a bare
    ///         <c>NotSupportedException</c> out of a method whose contract is to collect problems.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>DefaultJson</c> has to be a well-formed string of the property's own kind or
    ///         this test cannot fail.</b> <c>ResourceSchema.ValueProblems</c> reports a kind mismatch
    ///         and stops, so a <c>42</c> would never reach the constraint checks and the matcher would
    ///         never be built a second time. <c>MinLength</c> is the independent problem, checked
    ///         <i>before</i> the pattern in <c>ConstraintProblems</c> — it is what the throw used to
    ///         discard, and asserting it here is what proves the other half of the collector survived.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the refusal is owed to the chart gate whether or not a literal is declared</b>
    ///         — <c>build/Build.Charts.cs</c> compiles a <c>@pattern</c> with the same engine and the
    ///         same anchoring, so writing one would fail the gate against a file this emitter had just
    ///         written. The <c>[Theory]</c> row with no <c>DefaultJson</c> pins that half.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("(?=[a-z])[a-z0-9]+")]
    [InlineData("(?<![a-z])[a-z0-9]+")]
    [InlineData("(?>[a-z]+)[0-9]")]
    [InlineData(@"([a-z])\1")]
    public void APatternTheLinearEngineCannotRunIsNamedRatherThanThrownOutOfTheEmitter(string pattern) {
        var bare = Emit(new ResourceSchema {
            Properties = [
                new("/properties", SchemaKind.Nested, Description: "The configuration."),
                new("/properties/gap", SchemaKind.Text, Description: "A gap.") { Pattern = pattern }
            ]
        });

        bare.Text.ShouldBeEmpty();
        bare.Problems.ShouldContain(x => x.Contains("non-backtracking", StringComparison.Ordinal));
        bare.Problems.ShouldContain(x => x.Contains("'/properties/gap'", StringComparison.Ordinal));

        var withDefault = Emit(new ResourceSchema {
            Properties = [
                new("/properties", SchemaKind.Nested, Description: "The configuration."),
                new("/properties/gap", SchemaKind.Text, Description: "A gap.") {
                    Pattern = pattern,
                    MinLength = 10,
                    DefaultJson = "\"abc\""
                }
            ]
        });

        withDefault.Text.ShouldBeEmpty();
        withDefault.Problems.ShouldContain(x => x.Contains("non-backtracking", StringComparison.Ordinal));
        withDefault.Problems.ShouldContain(
            x => x.Contains("the minimum is 10", StringComparison.Ordinal),
            "the unrunnable pattern swallowed the other problem the same declaration has"
        );
    }

    /// <summary>
    ///     The array case, which inherits the cleared pattern rather than needing its own guard (#78).
    /// </summary>
    /// <remarks>
    ///     <c>ResourceSchema.ValueProblems</c> recurses into an array's elements with
    ///     <c>property with { Kind = property.ElementKind, … }</c>, so the element carries whatever
    ///     <c>Pattern</c> the array was handed — an empty one, once the emitter has cleared it. Without
    ///     that inheritance the guard would hold for a <c>{string}</c> and not for a
    ///     <c>{array}</c> of strings, which is the per-site-fix failure this repository keeps finding.
    ///     ⚠ The default has to hold at least one element: an empty array never recurses, so
    ///     <c>"[]"</c> here would pass against the defect.
    /// </remarks>
    [Fact]
    public void AnArrayElementInheritsTheClearedPatternRatherThanRebuildingIt() {
        var block = Emit(new ResourceSchema {
            Properties = [
                new("/properties", SchemaKind.Nested, Description: "The configuration."),
                new("/properties/gaps", SchemaKind.Array, Description: "Gaps.") {
                    ElementKind = SchemaKind.Text,
                    Pattern = "(?=[a-z])[a-z0-9]+",
                    MinLength = 10,
                    DefaultJson = "[\"abc\"]"
                }
            ]
        });

        block.Text.ShouldBeEmpty();
        block.Problems.ShouldContain(x => x.Contains("non-backtracking", StringComparison.Ordinal));
        block.Problems.ShouldContain(
            x => x.Contains("the minimum is 10", StringComparison.Ordinal),
            "the array element rebuilt the matcher the array itself was excused from building"
        );
    }

    [Fact]
    public void ASecretThatAlsoDeclaresAFormatIsRefusedBecauseBothWriteTheSameKeyword() {
        // `@secret` already means `format: password` — the three keywords OpenApiEmitter puts on a
        // secret — so a property carrying both would emit two `format`s into one schema node and the
        // second would win without a word being said.
        var block = Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            new("/properties/token", SchemaKind.Text, Secret: true, Description: "A token.") {
                Format = SchemaFormat.Uuid
            }
        ]));

        block.Text.ShouldBeEmpty();
        block.Problems.ShouldContain(x => x.Contains("the same keyword", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeLengthIsRefusedBecauseTheDirectiveSpellsDigits() {
        var block = Emit(new ResourceSchema {
            Properties = [
                new("/properties", SchemaKind.Nested, Description: "The configuration."),
                new("/properties/gap", SchemaKind.Text, Description: "A gap.") { MaxLength = -1 }
            ]
        });

        block.Text.ShouldBeEmpty();
        block.Problems.ShouldContain(x => x.Contains("negative length", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExampleIsRewrittenAsCompactJsonRatherThanCopied() {
        // ⚠ What makes `@example` safe where `@pattern` needed a check. Nobody constrains the
        // whitespace in a SchemaProperty.ExampleJson, and a pretty-printed one carries newlines that
        // would end the annotation block above its key. Re-serialising makes it one line by
        // construction, and makes the bytes a function of the value rather than of how it was spelled
        // — which is what a byte-for-byte drift gate needs.
        var block = Emit(ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Description: "The configuration."),
            new("/properties/extensions", SchemaKind.Array, Description: "Extensions.") {
                ElementKind = SchemaKind.Text,
                DefaultJson = "[]",
                ExampleJson = "[\n  \"pgvector\",\n  \"postgis\"\n]"
            }
        ]));

        block.Problems.ShouldBeEmpty();
        block.Text.ShouldContain("## @example [\"pgvector\",\"postgis\"]");
        Subset.Problems(block.Text).ShouldBeEmpty();
    }

    // ── (a2) The reader and the emitter are two files that can disagree in silence ─────────────

    [Fact]
    public void TheReaderAndTheEmitterAgreeOnTheDirectiveTable() {
        // ⚠ THE FAILURE CLASS THIS WHOLE TEST EXISTS FOR. The vocabulary lives in four places: the
        // `Directives` allow-list in build/Build.Charts.cs, the emission in ChartAnnotationEmitter,
        // the table in charts/README.md, and the Subset checker below. build/_build.csproj is outside
        // the solution and references nothing in src/, so the two halves CANNOT be wired together —
        // they can only be compared. A verb the reader accepts and the emitter never writes is dead
        // vocabulary; a verb the emitter writes and the reader rejects fails the build on generated
        // output. Both compile perfectly.
        //
        // ⚠ And the quiet one, which is why the allow-list alone is not enough: a verb listed in
        // `Directives` with no case in `TakeAnnotation` parses, is read, and is thrown away. The build
        // stays green and the constraint never reaches values.schema.json.
        var reader = ReaderSource();

        Table(reader, "Directives").ShouldBe(Subset.Directives, ignoreOrder: false);
        Table(reader, "FormatNames").ShouldBe(Subset.Formats, ignoreOrder: false);

        foreach (var directive in Subset.Directives) {
            reader.ShouldContain(
                $"case \"{directive}\"",
                Case.Sensitive,
                $"`@{directive}` is in build/Build.Charts.cs's Directives table with no case in "
                + "TakeAnnotation, so the reader accepts the directive, reads its argument and drops "
                + "it. The annotation parses, the build is green, and the fact never reaches "
                + "values.schema.json."
            );
        }
    }

    [Fact]
    public void TheDirectiveTableCheckWouldNoticeAMissingVerb() {
        // ⚠ A comparison nobody has seen fail is a comparison that passes. Both halves of the test
        // above are exercised here against text that is deliberately wrong.
        Table("static readonly string[] Directives = [\n  \"enum\",\n];", "Directives")
            .ShouldBe(["enum"]);

        Should.Throw<Exception>(() =>
            Table("static readonly string[] Directives = [\"enum\"];", "Directives")
                .ShouldBe(Subset.Directives, ignoreOrder: false));
    }

    /// <summary>
    ///     build/Build.Charts.cs, embedded at build time — see this project's <c>.csproj</c>.
    /// </summary>
    static string ReaderSource() {
        using var stream = typeof(ChartAnnotationTests).Assembly
            .GetManifestResourceStream("build.Build.Charts.cs")
            .ShouldNotBeNull(
                "build/Build.Charts.cs is not embedded in this test assembly, so the reader and the "
                + "emitter are no longer compared and either may grow a directive the other has never "
                + "heard of."
            );

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>The string-array initialiser called <paramref name="name" />, in declaration order.</summary>
    static ImmutableArray<string> Table(string source, string name) {
        var declaration = new Regex(
            @"string\[\]\s+" + Regex.Escape(name) + @"\s*=\s*\[(?<body>[^\]]*)\]",
            RegexOptions.Singleline
        ).Match(source);

        declaration.Success.ShouldBeTrue(
            $"build/Build.Charts.cs no longer declares a `string[] {name}`, so the reader's "
            + "vocabulary cannot be read and compared against the emitter's."
        );

        return [
            .. Regex.Matches(declaration.Groups["body"].Value, "\"(?<member>[^\"]*)\"")
                .Select(x => x.Groups["member"].Value)
        ];
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
        var block = Emit(ResourceSchema.Of([
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
    public void TheClusterIdIsPlacementAndIsExcludedFromTheChartItPlacesInto() {
        // ⚠ THE SECOND EXCLUSION, AND THE ONE THAT COST A CHART A DEFAULT ITS OWN SCHEMA REJECTS.
        // Until this rule existed the emitter's whole test was "under /properties and not ReadOnly",
        // which /properties/clusterId passes — so the chart grew a required `## @format uuid` row and
        // the only literal a string has for "unset", `""`. That is not a uuid. `helm lint --strict`
        // took it because JSON Schema 2020-12 makes `format` an annotation rather than an assertion,
        // so the chart's own default failed its own schema in every validator that asserts formats.
        //
        // The value could not be invented either way: a real uuid is a cluster a tenant would be
        // placed into without anybody choosing it, which is what Undefaulted refuses for a number and
        // for the same reason. The row was never configuration — templates/ never reads
        // `.Values.clusterId`, because the cluster is which API server the apply runs against and is
        // settled before Helm is handed anything.
        var placed = ChartAnnotationEmitter.Emit(Postgres(), "/properties/clusterId");

        placed.Problems.ShouldBeEmpty();

        // Gone with its whole annotation block, not left as a key with no `@param` — which the reader
        // fails with a line number — and not left as a block with no key under it either.
        placed.Text.Contains("clusterId", StringComparison.Ordinal).ShouldBeFalse();
        placed.Text.ShouldNotContain("## @format uuid");
        placed.Text.ShouldNotContain("## @widget cluster");
        Subset.Problems(placed.Text).ShouldBeEmpty();

        // ⚠ And nothing else moved. An exclusion that also dropped a neighbour would be a chart
        // silently missing a knob, which is the failure this whole surface exists to prevent.
        var kept = Regex.Matches(Block, @"^\s*## @param (?<name>\S+) ", RegexOptions.Multiline)
            .Select(x => x.Groups["name"].Value)
            .Where(x => x != "clusterId")
            .ToList();

        Regex.Matches(placed.Text, @"^\s*## @param (?<name>\S+) ", RegexOptions.Multiline)
            .Select(x => x.Groups["name"].Value)
            .ToList()
            .ShouldBe(kept);
    }

    [Fact]
    public void APointerNoTypeDeclaresAsPlacementIsStillAnOrdinaryChartRow() {
        // The other direction, and the reason the pointer is passed rather than pattern-matched: a
        // property is placement because a registration says so, never because of what it is called or
        // what format it carries. A type that declares no RequiresCluster has no placement pointer,
        // and a `clusterId` in its body is a tenant-chosen value like any other.
        Block.ShouldContain("clusterId:");
        Block.ShouldContain("## @format uuid");
    }

    [Fact]
    public void ANumberWithNoDeclaredDefaultIsRefusedRatherThanGivenAnInventedZero() {
        // Every values key carries a value; a null is refused by the reader and by helm. There is no
        // empty spelling of a number, and an invented 0 is a value a tenant would get without anybody
        // having chosen it — and it may sit outside the property's own @range.
        var block = Emit(ResourceSchema.Of([
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
        var block = Emit(new ResourceSchema {
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

    /// <summary>
    ///     The same type, declaring that it is placed into a cluster — the real
    ///     <c>CyberCloud.DBforPostgreSQL/servers</c> shape.
    /// </summary>
    /// <remarks>
    ///     ⚠ Kept apart from <see cref="Registry" /> rather than folded into it, because the pair of
    ///     them <i>is</i> the assertion: the pointer is the only difference between a chart with a
    ///     <c>clusterId</c> row and a chart without one.
    /// </remarks>
    static FakeRegistry Placed(string chart) =>
        new FakeRegistry {
            Namespaces = ["CyberCloud.DBforPostgreSQL"],
            Types = [
                new ResourceTypeRegistration {
                    Type = new("CyberCloud.DBforPostgreSQL", "servers"),
                    ApiVersions = [new(ApiVersion.Parse("2026-08-01"), Postgres())],
                    Chart = chart,
                    RequiresCluster = true,
                    ClusterIdPointer = ClusterPlacement.DefaultPointer
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
        /// <summary>
        ///     ⚠ The fourth copy of this table. <c>build/Build.Charts.cs</c> has the real one,
        ///     <c>ChartAnnotationEmitter</c> decides which of them it writes, charts/README.md documents
        ///     them, and this restates them. <see cref="TheReaderAndTheEmitterAgreeOnTheDirectiveTable" />
        ///     is what stops the copies drifting.
        /// </summary>
        public static readonly ImmutableArray<string> Directives = [
            "enum",
            "example",
            "format",
            "immutable",
            "internal",
            "length",
            "param",
            "pattern",
            "range",
            "required",
            "secret",
            "widget",
        ];

        public static readonly ImmutableArray<string> Formats = [
            "cybercloud-region",
            "cybercloud-resource-id",
            "date-time",
            "email",
            "uri",
            "uuid",
        ];

        static readonly ImmutableArray<string> Types =
            ["array", "boolean", "integer", "number", "object", "string"];

        static readonly Regex Param = new(
            @"^(?<name>[A-Za-z_][A-Za-z0-9_]*) \{(?<type>[A-Za-z]+)\} (?<description>\S.*)$"
        );

        static readonly Regex Key = new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*):[ ]*(?<value>.*)$");

        /// <summary>⚠ Both bounds. `1..` is malformed, not an open range — build/Build.Charts.cs.</summary>
        static readonly Regex Range = new(@"^-?\d+(?:\.\d+)?\.\.-?\d+(?:\.\d+)?$");

        /// <summary>⚠ Either end may be empty, unlike <see cref="Range" />. Digits only.</summary>
        static readonly Regex Length = new(@"^\d*\.\.\d*$");

        static readonly Regex Widget = new("^[a-z][a-z0-9-]*$");

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

                    var argument = body.Length > verb.Length ? body[(verb.Length + 1)..].Trim() : string.Empty;

                    if (!Directives.Contains(verb, StringComparer.Ordinal)) {
                        problems.Add($"{line}: `@{verb}` is not a directive.");
                    }

                    // ⚠ Every directive's ARGUMENT is checked, not only its name. An earlier version of
                    // this checker validated `@param` and waved the rest through, and it passed an
                    // emitter that wrote `## @range 1..` for a one-sided bound — which the real reader
                    // refuses as a malformed directive. A checker that only reads the verbs is a
                    // checker that approves the arguments.
                    switch (verb) {
                        case "param":
                            var parsed = Param.Match(argument);

                            if (!parsed.Success) {
                                problems.Add($"{line}: malformed `@param`.");
                            } else if (!Types.Contains(parsed.Groups["type"].Value, StringComparer.Ordinal)) {
                                problems.Add($"{line}: `{{{parsed.Groups["type"].Value}}}` is not a type.");
                            }

                            break;

                        case "range" when !Range.IsMatch(argument):
                            problems.Add($"{line}: malformed `@range`. Got `{argument}`.");
                            break;

                        // ⚠ `@length` accepts an open end where `@range` does not — but not two of
                        // them, which would be a directive constraining nothing.
                        case "length" when !Length.IsMatch(argument) || argument == "..":
                            problems.Add($"{line}: malformed `@length`. Got `{argument}`.");
                            break;

                        case "format" when !Formats.Contains(argument, StringComparer.Ordinal):
                            problems.Add($"{line}: `@format {argument}` is not one of the six.");
                            break;

                        // ⚠ The argument is NOT parsed as anything but a run of printable text. A
                        // pattern is full of `|`, `#`, `:` and braces and every one of them is legal
                        // here; what it may not be is empty, or carry the whitespace three separate
                        // trims in the real reader would eat.
                        case "pattern" when argument.Length == 0:
                            problems.Add($"{line}: `@pattern` needs a regular expression.");
                            break;

                        case "pattern" when body[(verb.Length + 1)..] != argument:
                            problems.Add(
                                $"{line}: `@pattern` has leading or trailing whitespace, which the "
                                + "reader trims away. Got `{argument}`."
                            );

                            break;

                        case "example":
                            if (argument.Length == 0) {
                                problems.Add($"{line}: `@example` needs a JSON value.");
                                break;
                            }

                            try {
                                _ = System.Text.Json.Nodes.JsonNode.Parse(argument);
                            } catch (System.Text.Json.JsonException) {
                                problems.Add($"{line}: `@example` is not JSON. Got `{argument}`.");
                            }

                            break;

                        case "widget" when !Widget.IsMatch(argument):
                            problems.Add($"{line}: `@widget` takes one lower-case name. Got `{argument}`.");
                            break;

                        case "enum":
                            var members = argument.Split('|').Select(x => x.Trim()).ToList();

                            if (members.Any(x => x.Length == 0) || members.Distinct(StringComparer.Ordinal).Count() != members.Count) {
                                problems.Add($"{line}: `@enum` has an empty or repeated member.");
                            }

                            break;

                        case "required" or "secret" or "immutable" when argument.Length > 0:
                            problems.Add($"{line}: `@{verb}` takes no argument. Got `{argument}`.");
                            break;

                        case "internal" when argument.Length == 0:
                            problems.Add($"{line}: `@internal` needs a reason.");
                            break;

                        default:
                            break;
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
