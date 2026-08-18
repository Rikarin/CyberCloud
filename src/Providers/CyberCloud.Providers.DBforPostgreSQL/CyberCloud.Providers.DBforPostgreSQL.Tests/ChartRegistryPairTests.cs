using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.DBforPostgreSQL.Tests;

/// <summary>
///     What the chart and the registry still have to be told about each other.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>CUT DOWN 2026-08-12, from six tests to three, and the four that went are the point of
///         this note.</b> This class was written as "a stand-in for a build gate that does not exist",
///         on the premise that <c>Build.Charts.cs</c> "never opens a provider registry — the only two
///         occurrences of <c>ResourceSchema</c> in that file are comments". <b>The premise was a grep
///         and the conclusion was wrong.</b> <c>build/Build.Charts.cs</c> calls
///         <c>RunGenerator(write: true, charts: true)</c>, which drives <c>ChartSurfaces.Generate</c>
///         and <c>ChartAnnotationEmitter</c>: the target owns the verdict and delegates the emission,
///         exactly as <c>Build.Generate</c> does, so the registry is reached without the word ever
///         appearing in that file. The gate existed the whole time.
///     </para>
///     <para>
///         So the row-by-row comparisons are gone. They compared two hand-maintained files, which can
///         agree and both be wrong; generation makes one file a function of the other, which cannot
///         disagree with its source.
///     </para>
///     <para>
///         ⚠ <b>CORRECTED 2026-08-12, later the same day. This note went on to say that three of the
///         deleted tests "had in fact become <i>false</i>" because they pinned
///         <c>/properties/clusterId</c> as a body-only property with no chart row, "and the emitter
///         gives it one, because it is under <c>/properties</c>, is not <c>ReadOnly</c>, and is
///         therefore something the chart's caller sets". The tests were right and the emitter was
///         incomplete.</b> Its rule had no word for placement, so the chart grew a
///         <c>clusterId: ""</c> row under <c>## @format uuid</c> — a default that is not a uuid, in a
///         chart whose own generated schema declares the format. <c>ChartAnnotationEmitter.Emit</c>
///         now takes <c>ResourceTypeRegistration.ClusterIdPointer</c> and skips it, so the deleted
///         assertions are true again — and they stay deleted, because
///         <c>ChartAnnotationTests.ThePlacementPointerReachesTheEmitterFromTheRegistrationRatherThanTheSchema</c>
///         asserts the same fact against the generator rather than against a second hand-maintained
///         file. ⚠ <b>The lesson is the direction of the inference</b>: "the generator does X" is not
///         evidence that X is right, and a test deleted for disagreeing with a generator should be
///         read as a report about the generator first.
///     </para>
///     <para>
///         ⚠ <b>What is left is what generation does not reach.</b> The preset table lives in
///         <c>templates/_helpers.tpl</c>, and <c>ChartSurfaces</c> filters <c>templates/</c> out on
///         purpose — no emitter has ever read a Helm template. And the <i>anchoring</i> of a
///         <c>@pattern</c> is a property of the shipped <c>values.schema.json</c> that neither half of
///         the round trip can assert on its own.
///     </para>
/// </remarks>
public sealed partial class ChartRegistryPairTests {
    [Fact]
    public void EveryPatternInTheGeneratedSchemaIsAWholeValueMatch() {
        // ⚠ THE ONE WAY A CONSTRAINT CAN REACH THE CHART AND MEAN SOMETHING ELSE THERE, which is the
        // same failure as losing it and is harder to see. SchemaProperty.Pattern is a whole-value
        // match — ResourceSchema tests it as ^(?:…)$ — and JSON Schema's `pattern` keyword is a
        // SEARCH. A bare `\d+(\.\d+)?(m|k|M|G|…)?` in values.schema.json accepts `xxx20Gixxx`, which
        // the API refuses. The chart would be strictly more permissive than the surface it is
        // generated from, and `helm lint` would pass values that come back 400.
        //
        // The annotation in values.yaml carries the pattern BARE, so that line and the C# `Pattern`
        // are the same string and drift between them is visible; build/Build.Charts.cs anchors on the
        // way into the schema. This asserts the end of that pipeline rather than either half.
        var patterns = Patterns(ChartSchema()).ToList();

        patterns.ShouldNotBeEmpty(
            "no `pattern` reached charts/managed/postgres/values.schema.json at all. "
            + "CyberCloud.DBforPostgreSQL/servers declares seven, so either the @pattern directive "
            + "stopped being emitted or this reader stopped finding them — and a check that inspects "
            + "nothing passes."
        );

        foreach (var (pointer, pattern) in patterns) {
            // ⚠ The non-capturing group is not decoration. Anchoring `a|b` as `^a|b$` means "starts
            // with a" OR "ends with b", which is a wider language than either — and the quantity
            // pattern this chart uses is thirteen alternations deep.
            pattern.ShouldStartWith("^(?:", Case.Sensitive, pointer);
            pattern.ShouldEndWith(")$", Case.Sensitive, pointer);
        }
    }

    [Fact]
    public void TheSizingTableIsTheChartsSizingTable() {
        // ⚠ PostgresServers.Presets is a second copy of templates/_helpers.tpl's `postgres.resources`
        // dictionary, and it exists only because CyberCloud.Kubernetes.Charts does not. Two copies of
        // a lookup table drift silently — a tenant asking for s1.large would get one CPU count from
        // the reconciler and another from anyone rendering the chart by hand — so they are diffed.
        //
        // ⚠ Not subsumed by ADR-012's fifth surface, and never will be: ChartSurfaces filters
        // `templates/` out of the chart tree on purpose, so no emitter reads this file.
        var chart = HelperPresets();

        chart.Count.ShouldBe(
            PostgresServers.Presets.Count,
            "the s1 family in templates/_helpers.tpl and in PostgresServers.Presets are different sizes"
        );

        foreach (var (preset, quantities) in chart) {
            PostgresServers.Presets.TryGetValue(preset, out var mine).ShouldBeTrue(
                $"'{preset}' is in templates/_helpers.tpl and not in PostgresServers.Presets"
            );

            mine.ShouldBe(quantities, preset);
        }
    }

    [Fact]
    public void EverySizingPresetTheSchemaAllowsHasARowInTheTable() {
        // The other direction: an allowed value with no row renders no `resources` block at all, so
        // the preset silently does nothing rather than failing. _helpers.tpl has the same hazard and
        // says so; this is the check that neither list can grow without the other.
        foreach (var preset in PostgresServers.Schema2026.Properties
                     .Single(x => x.JsonPointer == "/properties/sizing/preset")
                     .AllowedValues) {
            PostgresServers.Presets.ShouldContainKey(preset);
        }
    }

    [Fact]
    public void TheChartPutsThePreloadLibrariesBesideParametersAndNotInsideThem() {
        // ⚠ THE OTHER HALF OF PostgresReconcilerTests.ThePreloadLibrariesAreASiblingOfParametersInBothSpellings,
        // and it has to be a separate assertion because the two files are separate artefacts. A fix
        // applied to PostgresServers.ClusterJson alone would leave anyone rendering the chart by hand
        // — charts/README.md's whole reason for the chart existing — writing a Cluster that
        // CloudNativePG's validating webhook rejects, which is exactly the chart↔registry drift
        // ADR-012's fifth surface exists to prevent and which no generator here can catch.
        //
        // CloudNativePG declares `shared_preload_libraries` as PostgresConfiguration.AdditionalLibraries,
        // a []string SIBLING of `parameters` (api/v1/cluster_types.go); lists it in
        // FixedConfigurationParameters (pkg/postgres/configuration.go); and refuses any fixed key
        // found under spec.postgresql.parameters (internal/webhook/v1/cluster_webhook.go).
        //
        // ⚠ A regular expression over the TEMPLATE rather than a `helm template` render, for
        // TheSizingTableIsTheChartsSizingTable's reason: rendering needs `helm` on PATH and a unit
        // test that silently passes on a machine without it is the vacuous pass Build.Charts' own
        // warning exists to prevent.
        var cluster = Embedded("postgres.cluster.yaml");

        var block = Regex.Match(
            cluster,
            "\\n    shared_preload_libraries:\\n((?:\\s*\\{\\{.*\\}\\}\\n|      - .*\\n)+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue(
            "the chart declares no `shared_preload_libraries:` list two levels under spec — which is "
            + "where CloudNativePG's PostgresConfiguration.AdditionalLibraries sits, beside "
            + "`parameters` rather than inside it."
        );

        // ⚠ It ranges over `$libraries` — the list this template DERIVES from `.Values.extensions`
        // through the catalogue at the top of the file — and not over the allow-list itself. Ranging
        // over `.Values.extensions` here is what asked the postmaster to preload `pgvector` and
        // `postgis`, neither of which is a library. TheExtensionCatalogueIsTheChartsExtensionCatalogue
        // checks the table the derivation reads.
        block.Groups[1].Value.ShouldContain(
            "range $libraries",
            customMessage: "the chart's shared_preload_libraries list is built from something other "
            + "than the derived $libraries. If it ranges over .Values.extensions it names every "
            + "allowed value as a library, and pgvector and postgis are not libraries — an entry with "
            + "nothing behind it stops the postmaster from starting."
        );

        var parameters = Regex.Match(
            cluster,
            "\\n    parameters:\\n((?:      \\S.*\\n)+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        parameters.Success.ShouldBeTrue("the chart declares no `parameters:` block");

        parameters.Groups[1].Value.ShouldNotContain(
            "shared_preload_libraries",
            customMessage: "the chart writes shared_preload_libraries under spec.postgresql.parameters, "
            + "where CloudNativePG's validating webhook refuses it as a fixed configuration "
            + "parameter. Every server created with an extension is rejected at admission after the "
            + "caller has been told 202."
        );

        // ⚠ And the one key that IS a parameter is still there, so a fix that emptied the block
        // rather than moving one key out of it fails here.
        parameters.Groups[1].Value.ShouldContain("max_connections");
    }

    [Fact]
    public void TheExtensionCatalogueIsTheChartsExtensionCatalogue() {
        // ⚠ THE SECOND LOOKUP TABLE THIS PAIR CARRIES TWICE, and it is the one whose drift is
        // invisible from either side. PostgresServers.ExtensionCatalogue and the `$catalogue` dict at
        // the top of templates/cluster.yaml both map an allow-list value to the EXTENSION it installs
        // as and the LIBRARY it needs preloaded, and those are different strings — `pgvector` installs
        // as `vector` and preloads nothing.
        //
        // Getting one row wrong in one spelling produces either a CREATE EXTENSION that fails inside a
        // bootstrap job, or a shared_preload_libraries entry with no library behind it — which the
        // postmaster refuses at startup, for every database on the instance. Neither failure reaches
        // the resource, and no emitter reads a Helm template, so this comparison is what holds them
        // together. Same reason as TheSizingTableIsTheChartsSizingTable, higher price.
        var chart = HelperExtensions();

        chart.Count.ShouldBe(
            PostgresServers.ExtensionCatalogue.Count,
            "the `$catalogue` dict in templates/cluster.yaml and PostgresServers.ExtensionCatalogue "
            + "are different sizes"
        );

        foreach (var (value, names) in chart) {
            PostgresServers.ExtensionCatalogue.TryGetValue(value, out var mine).ShouldBeTrue(
                $"'{value}' is in templates/cluster.yaml's `$catalogue` and not in "
                + "PostgresServers.ExtensionCatalogue"
            );

            mine.ShouldBe(names, value);
        }
    }

    // ── Reading the chart ─────────────────────────────────────────────────────────────────────

    /// <summary>The <c>$catalogue</c> extension table, read out of <c>templates/cluster.yaml</c>.</summary>
    /// <remarks>
    ///     ⚠ In <c>cluster.yaml</c> rather than <c>_helpers.tpl</c> because a <c>define</c> returns a
    ///     string and not a <c>dict</c>, so splitting the table across two helpers — one per
    ///     vocabulary — would have been the same duplication one file lower down. A regular expression
    ///     over the template rather than a <c>helm template</c> render, for
    ///     <see cref="HelperPresets" />'s reason.
    /// </remarks>
    static ImmutableDictionary<string, (string ExtensionName, string PreloadLibrary)> HelperExtensions() {
        var found = ImmutableDictionary.CreateBuilder<string, (string, string)>(StringComparer.Ordinal);

        foreach (var line in Embedded("postgres.cluster.yaml").Split('\n')) {
            var match = CatalogueLine.Match(line);

            if (match.Success) {
                found[match.Groups["value"].Value] =
                    (match.Groups["extension"].Value, match.Groups["preload"].Value);
            }
        }

        return found.ToImmutable();
    }

    /// <remarks>
    ///     ⚠ <c>\s+</c> between every field, and <c>[^"]*</c> rather than <c>[^"]+</c> for the preload
    ///     name — <c>""</c> is a legal and load-bearing value there, meaning "this extension needs no
    ///     preload entry", and a pattern demanding one character would have silently skipped exactly
    ///     the two rows the defect was about. The count assertion in
    ///     <see cref="TheExtensionCatalogueIsTheChartsExtensionCatalogue" /> is what catches a rewrite
    ///     this stops matching.
    /// </remarks>
    [GeneratedRegex(
        """^\s*"(?<value>[^"]+)"\s+\(dict\s+"extension"\s+"(?<extension>[^"]+)"\s+"preload"\s+"(?<preload>[^"]*)"\)"""
    )]
    private static partial Regex CatalogueLine { get; }

    /// <summary>Every <c>pattern</c> keyword in the generated schema, with the pointer carrying it.</summary>
    static IEnumerable<(string Pointer, string Pattern)> Patterns(JsonObject node) {
        if (node["properties"] is not JsonObject members) {
            yield break;
        }

        foreach (var (_, value) in members) {
            if (value is not JsonObject member) {
                continue;
            }

            if (member["pattern"]?.GetValue<string>() is { } pattern) {
                yield return (member["x-cybercloud-pointer"]!.GetValue<string>(), pattern);
            }

            foreach (var nested in Patterns(member)) {
                yield return nested;
            }
        }
    }

    static JsonObject ChartSchema() => JsonNode.Parse(Embedded("postgres.values.schema.json"))!.AsObject();

    /// <summary>The <c>postgres.resources</c> preset table, read out of the Helm helper.</summary>
    /// <remarks>
    ///     ⚠ A regular expression over a template rather than a Helm render, because rendering needs
    ///     <c>helm</c> on <c>PATH</c> and a unit test that silently passes on a machine without it is
    ///     the vacuous pass <c>Build.Charts</c>' own warning exists to prevent. The shape it matches
    ///     is one line of a <c>dict</c> literal, and a rewrite of that block that this stops matching
    ///     fails the count assertion rather than passing quietly.
    /// </remarks>
    static ImmutableDictionary<string, (string Cpu, string Memory)> HelperPresets() {
        var found = ImmutableDictionary.CreateBuilder<string, (string, string)>(StringComparer.Ordinal);

        foreach (var line in Embedded("postgres.helpers.tpl").Split('\n')) {
            var match = PresetLine.Match(line);

            if (match.Success) {
                found[match.Groups["preset"].Value] =
                    (match.Groups["cpu"].Value, match.Groups["memory"].Value);
            }
        }

        return found.ToImmutable();
    }

    /// <remarks>
    ///     ⚠ <c>\s+</c> between every field, not one space. The template's <c>dict</c> literal is
    ///     column-aligned — <c>"cpu" "1"    "memory" "4Gi"</c> — and a pattern with single spaces in it
    ///     matched three of the eight rows and reported the other five as absent. Caught by the count
    ///     assertion in <c>TheSizingTableIsTheChartsSizingTable</c>, which is exactly why that
    ///     assertion is there: a reader that silently finds fewer rows would have made this
    ///     comparison pass over the presets it could not see.
    /// </remarks>
    [GeneratedRegex("""^\s*"(?<preset>[^"]+)"\s+\(dict\s+"cpu"\s+"(?<cpu>[^"]+)"\s+"memory"\s+"(?<memory>[^"]+)"\)""")]
    private static partial Regex PresetLine { get; }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. The .csproj embeds it from "
                + "charts/managed/postgres/ — see its header for why the comparison lives here."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
