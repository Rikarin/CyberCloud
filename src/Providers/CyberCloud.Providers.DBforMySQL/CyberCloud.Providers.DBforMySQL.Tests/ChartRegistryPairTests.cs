using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.DBforMySQL.Tests;

/// <summary>
///     What the chart and the registry still have to be told about each other.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Four tests, not the fourteen a row-by-row comparison would be, and the difference is
///         ADR-012's fifth surface existing.</b> <c>build/Build.Charts.cs</c> calls
///         <c>RunGenerator(write: true, charts: true)</c>, which drives <c>ChartSurfaces.Generate</c>
///         and <c>ChartAnnotationEmitter</c>: the chart's non-<c>@internal</c> <c>@param</c> block is
///         <i>generated</i> from <c>MariaDbServers.Schema2026</c> and byte-diffed. Comparing the two
///         here would be comparing a file with the file it was generated from.
///     </para>
///     <para>
///         What generation does not reach: <c>templates/</c>, which <c>ChartSurfaces</c> filters out on
///         purpose — no emitter has ever read a Helm template — and the <i>anchoring</i> of a
///         <c>@pattern</c>, which is a property of the shipped <c>values.schema.json</c> that neither
///         half of the round trip can assert on its own.
///     </para>
/// </remarks>
public sealed partial class ChartRegistryPairTests {
    [Fact]
    public void EveryPatternInTheGeneratedSchemaIsAWholeValueMatch() {
        // ⚠ THE ONE WAY A CONSTRAINT CAN REACH THE CHART AND MEAN SOMETHING ELSE THERE, which is the
        // same failure as losing it and is harder to see. SchemaProperty.Pattern is a whole-value match
        // — ResourceSchema tests it as ^(?:…)$ — and JSON Schema's `pattern` keyword is a SEARCH. A
        // bare `\d+(\.\d+)?(m|k|M|G|…)?` in values.schema.json accepts `xxx20Gixxx`, which the API
        // refuses. The chart would be strictly more permissive than the surface it is generated from,
        // and `helm lint` would pass values that come back 400.
        //
        // The annotation in values.yaml carries the pattern BARE, so that line and the C# `Pattern` are
        // the same string and drift between them is visible; build/Build.Charts.cs anchors on the way
        // into the schema. This asserts the end of that pipeline rather than either half.
        var patterns = Patterns(ChartSchema()).ToList();

        patterns.Count.ShouldBeGreaterThanOrEqualTo(
            5,
            "fewer `pattern` keywords reached charts/managed/mariadb/values.schema.json than "
            + "CyberCloud.DBforMySQL/servers declares — three quantities and two identifiers. Either "
            + "the @pattern directive stopped being emitted or this reader stopped finding them, and "
            + "a check that inspects nothing passes."
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
    public void EveryApiPointerInTheShippedSchemaIsARegistryPointerWithTheSameCasing() {
        // ⚠ ONE CHARACTER OF CASING, PLACE THREE OF THREE — the registry, the OpenAPI document and the
        // chart. This reads the file that SHIPS rather than the emitter's output: `./build.sh Charts`
        // proves the generator agrees with the registry on the machine that runs it, and this proves
        // the bytes in the repository do. The two differ exactly when somebody edits a generated file
        // by hand and does not run the target.
        //
        // ⚠ It walks BOTH directions. Only the first would pass on a schema that had quietly lost half
        // its rows.
        var shipped = ApiPointers(ChartSchema()).ToList();

        var registry = MariaDbServers.Schema2026.Properties
            .Where(x => x.JsonPointer.StartsWith("/properties/", StringComparison.Ordinal))
            .Where(x => x.JsonPointer != MariaDbServers.ClusterIdPointer)
            .Select(x => x.JsonPointer)
            .ToList();

        foreach (var pointer in shipped) {
            registry.ShouldContain(
                pointer,
                $"charts/managed/mariadb/values.schema.json carries the API pointer '{pointer}' and "
                + "MariaDbServers.Schema2026 does not declare it under that exact spelling."
            );
        }

        foreach (var pointer in registry) {
            shipped.ShouldContain(
                pointer,
                $"'{pointer}' is in the registry and not in the shipped values.schema.json. A property "
                + "the API validates and the chart cannot spell is a body that is accepted and then "
                + "rendered into nothing."
            );
        }

        // And placement is excluded from the chart, which is the emitter's second exclusion rather than
        // a quirk of one provider — charts/README.md § What a chart cannot say.
        shipped.ShouldNotContain(MariaDbServers.ClusterIdPointer);
    }

    [Fact]
    public void TheSizingTableIsTheChartsSizingTable() {
        // ⚠ MariaDbServers.Presets is a second copy of templates/_helpers.tpl's `mariadb.presets`
        // dictionary, and it exists only because CyberCloud.Kubernetes.Charts does not. Two copies of a
        // lookup table drift silently — a tenant asking for s1.large would get one memory figure from
        // the reconciler and another from anyone rendering the chart by hand, and the reconciler's
        // figure is also what quota reserves.
        //
        // ⚠ Not subsumed by ADR-012's fifth surface, and never will be: ChartSurfaces filters
        // `templates/` out of the chart tree on purpose, so no emitter reads this file.
        var chart = HelperPresets();

        chart.Count.ShouldBe(
            MariaDbServers.Presets.Count,
            "the s1 family in templates/_helpers.tpl and in MariaDbServers.Presets are different sizes"
        );

        foreach (var (preset, quantities) in chart) {
            MariaDbServers.Presets.TryGetValue(preset, out var mine).ShouldBeTrue(
                $"'{preset}' is in templates/_helpers.tpl and not in MariaDbServers.Presets"
            );

            mine.ShouldBe(quantities, preset);
        }
    }

    [Fact]
    public void EverySizingPresetTheSchemaAllowsHasARowInTheTable() {
        // The other direction: an allowed value with no row renders no `resources` block at all, so the
        // preset silently does nothing rather than failing — and the quota derivation, which reads the
        // same table, refuses the write. Two symptoms, one missing row.
        foreach (var preset in MariaDbServers.Schema2026.Properties
                     .Single(x => x.JsonPointer == "/properties/sizing/preset")
                     .AllowedValues) {
            MariaDbServers.Presets.ShouldContainKey(preset);
        }
    }

    // ── Reading the chart ─────────────────────────────────────────────────────────────────────

    /// <summary>Every <c>pattern</c> keyword in the generated schema, with the pointer carrying it.</summary>
    static IEnumerable<(string Pointer, string Pattern)> Patterns(JsonObject node) {
        foreach (var member in Members(node)) {
            if (member["pattern"]?.GetValue<string>() is { } pattern) {
                yield return (member["x-cybercloud-pointer"]!.GetValue<string>(), pattern);
            }
        }
    }

    /// <summary>Every pointer in the generated schema that is part of the resource body.</summary>
    /// <remarks>
    ///     ⚠ <c>x-cybercloud-api: false</c> is written on every descendant of an excluded key, not only
    ///     on the key itself — charts/README.md § The annotation format, rule 6 — so this filter needs
    ///     no walk back up the pointer. That matters more on this chart than on its siblings: the
    ///     <c>credentials</c> block is an <c>@internal</c> OBJECT with two <c>@secret</c> members
    ///     inside it, so a filter that only looked at the top key would let two password rows through.
    /// </remarks>
    static IEnumerable<string> ApiPointers(JsonObject node) =>
        Members(node)
            .Where(x => x["x-cybercloud-api"]?.GetValue<bool>() != false)
            .Select(x => x["x-cybercloud-pointer"]!.GetValue<string>());

    static IEnumerable<JsonObject> Members(JsonObject node) {
        if (node["properties"] is not JsonObject members) {
            yield break;
        }

        foreach (var (_, value) in members) {
            if (value is not JsonObject member) {
                continue;
            }

            yield return member;

            foreach (var nested in Members(member)) {
                yield return nested;
            }
        }
    }

    static JsonObject ChartSchema() => JsonNode.Parse(Embedded("mariadb.values.schema.json"))!.AsObject();

    /// <summary>The <c>mariadb.presets</c> table, read out of the Helm helper.</summary>
    /// <remarks>
    ///     ⚠ A regular expression over a template rather than a Helm render, because rendering needs
    ///     <c>helm</c> on <c>PATH</c> and a unit test that silently passes on a machine without it is
    ///     the vacuous pass <c>Build.Charts</c>' own warning exists to prevent. The shape it matches is
    ///     one line of a <c>dict</c> literal, and a rewrite of that block that this stops matching fails
    ///     the count assertion rather than passing quietly.
    /// </remarks>
    static ImmutableDictionary<string, (string Cpu, string Memory)> HelperPresets() {
        var found = ImmutableDictionary.CreateBuilder<string, (string, string)>(StringComparer.Ordinal);

        foreach (var line in Embedded("mariadb.helpers.tpl").Split('\n')) {
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
    ///     matched three of the eight rows on the first provider's chart and reported the other five as
    ///     absent. Caught by the count assertion in <c>TheSizingTableIsTheChartsSizingTable</c>, which
    ///     is exactly why that assertion is there.
    /// </remarks>
    [GeneratedRegex("""^\s*"(?<preset>[^"]+)"\s+\(dict\s+"cpu"\s+"(?<cpu>[^"]+)"\s+"memory"\s+"(?<memory>[^"]+)"\)""")]
    private static partial Regex PresetLine { get; }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. The .csproj embeds it from "
                + "charts/managed/mariadb/ — see its header for why the comparison lives here."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
