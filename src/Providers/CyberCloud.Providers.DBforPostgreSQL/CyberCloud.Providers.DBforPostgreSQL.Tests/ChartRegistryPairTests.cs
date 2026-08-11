using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.DBforPostgreSQL.Tests;

/// <summary>
///     The chart and the registry, compared row by row.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a stand-in for a build gate that does not exist, and the difference matters
///         to anyone reading a green run.</b> ADR-012 lists five generated surfaces and marks the
///         fifth — the chart's <c>@param</c> block — <i>"Not built. The first four have emitters and
///         a drift gate; this one has a decision and neither."</i> ADR-010 § Which end authors the
///         schema then says the overlap <i>"becomes 26 rows wide the moment the Postgres provider
///         lands"</i>. It has landed. What has not appeared with it is any comparison inside
///         <c>./build.sh</c>: <c>Build.Charts.cs</c> never opens a provider registry — the only two
///         occurrences of <c>ResourceSchema</c> in that file are comments — so
///         <c>./build.sh Charts</c> reports "36 annotated value(s), 25 in the API surface" and has
///         no opinion about whether any C# declares them.
///     </para>
///     <para>
///         So the rows are compared here instead. It is strictly weaker than generation: two
///         hand-maintained files that agree can both be wrong together, whereas a derived file cannot
///         disagree with its source. It fails on exactly the diffs the emitter would fail on, which
///         is what makes it worth having until the emitter exists — and the emitter should delete it.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately <i>not</i> compared, because the chart cannot say it.</b>
///         <c>Pattern</c>, <c>MinLength</c> and <c>MaxLength</c> are three of the seven
///         <c>SchemaProperty</c> facts ADR-010 lists as having no annotation syntax, and this type
///         uses all three — see the constraint block in <c>PostgresServers</c> for the decision and
///         its cost. A comparison that included them would fail on every run and be switched off
///         within a week.
///     </para>
/// </remarks>
public sealed partial class ChartRegistryPairTests {
    [Fact]
    public void EveryApiFacingChartRowIsDeclaredAtTheSamePointerAndNothingElseIs() {
        // ⚠ ORDINAL, AND THAT IS THE WHOLE TEST. A `resourcegroup`/`resourceGroup` mismatch of exactly
        // one character was once failing every create in the platform and surfaced as a 404 whose
        // reason was in a log line. A pointer is a JSON Pointer into a request body, so a case
        // difference is a different property that silently never arrives — and both files spell these
        // names by hand today.
        var chart = ChartPointers();
        var registry = ApiPointers();

        registry.Except(chart, StringComparer.Ordinal).ShouldBeEmpty(
            "these pointers are declared in PostgresServers.Schema2026 and have no @param row at the "
            + "same pointer in charts/managed/postgres/values.yaml. A body property with no chart row "
            + "is a property the reconciler renders from nothing."
        );

        chart.Except(registry, StringComparer.Ordinal).ShouldBeEmpty(
            "these @param rows are in the chart's API surface and are not declared in "
            + "PostgresServers.Schema2026. An API-facing chart row nothing declares is configuration "
            + "the tenant cannot reach — ADR-010 § Which end authors the schema makes the C# the "
            + "authored side, so either declare the property or mark the row @internal with a reason."
        );
    }

    [Fact]
    public void TheBodyOnlyPropertiesAreExactlyTheTwoThatCanHaveNoChartRow() {
        // ⚠ ADR-010 splits the chart into 26 API rows and 10 @internal rendering inputs and concludes
        // the two files "overlap on 26 rows". They diverge the other way too, and that third category
        // is not written down anywhere: a cluster id and a region are body properties with no Helm
        // value behind them. Pinned here so the set cannot grow quietly — a generator rewriting
        // values.yaml from this schema has to skip exactly these.
        var bodyOnly = PostgresServers.Schema2026.Properties
            .Where(x => x.Kind is not SchemaKind.Nested)
            .Select(x => x.JsonPointer)
            .Except(ChartPointers(), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        bodyOnly.ShouldBe(new[] { "/location", PostgresServers.ClusterIdPointer });
    }

    [Fact]
    public void TheChartAndTheRegistryAgreeOnTypeRequirednessEnumRangeDefaultAndWidget() {
        var chart = ChartRows();
        var disagreements = new List<string>();

        foreach (var property in PostgresServers.Schema2026.Properties) {
            if (!chart.TryGetValue(property.JsonPointer, out var row)) {
                continue;
            }

            Compare(disagreements, property.JsonPointer, "type", TypeName(property.Kind), row.Type);
            Compare(disagreements, property.JsonPointer, "description", property.Description, row.Description);
            Compare(disagreements, property.JsonPointer, "required", property.Required, row.Required);
            Compare(disagreements, property.JsonPointer, "enum", string.Join("|", property.AllowedValues), string.Join("|", row.Enum));
            Compare(disagreements, property.JsonPointer, "minimum", property.Minimum, row.Minimum);
            Compare(disagreements, property.JsonPointer, "maximum", property.Maximum, row.Maximum);
            Compare(disagreements, property.JsonPointer, "default", property.DefaultJson, row.DefaultJson);
            Compare(disagreements, property.JsonPointer, "widget", SchemaVocabulary.Of(property.Widget), row.Widget);
            Compare(disagreements, property.JsonPointer, "immutable", property.Immutable, row.Immutable);
        }

        disagreements.ShouldBeEmpty(
            "the C# schema is the authored side (ADR-010 § Which end authors the schema) and "
            + "charts/managed/postgres/values.yaml disagrees with it:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, disagreements)
        );
    }

    [Fact]
    public void TheChartsResourceTypeAndApiVersionAreTheOnesTheProviderDeclares() {
        // ⚠ The chart names its resource type in three files — Chart.yaml's annotation,
        // conformance.yaml, and the generated schema's x-cybercloud-resource-type. Build.Charts
        // compares the first two to each other and never to a registry, so this is the only place the
        // spelling in src/ and the spelling in charts/ meet.
        var document = ChartSchema();

        document["x-cybercloud-resource-type"]?.GetValue<string>().ShouldBe(PostgresServers.Type.ToString());
        document["x-cybercloud-api-version"]?.GetValue<string>().ShouldBe(PostgresServers.V2026);
    }

    [Fact]
    public void TheSizingTableIsTheChartsSizingTable() {
        // ⚠ PostgresServers.Presets is a second copy of templates/_helpers.tpl's `postgres.resources`
        // dictionary, and it exists only because CyberCloud.Kubernetes.Charts does not. Two copies of
        // a lookup table drift silently — a tenant asking for s1.large would get one CPU count from
        // the reconciler and another from anyone rendering the chart by hand — so they are diffed.
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

    // ── Reading the chart ─────────────────────────────────────────────────────────────────────

    /// <summary>One <c>@param</c> row, as the generated schema carries it.</summary>
    sealed record ChartRow(
        string Type,
        string Description,
        bool Required,
        ImmutableArray<string> Enum,
        double? Minimum,
        double? Maximum,
        string DefaultJson,
        string Widget,
        bool Immutable
    );

    /// <summary>The API-facing rows of <c>values.schema.json</c>, keyed by pointer.</summary>
    /// <remarks>
    ///     ⚠ <c>x-cybercloud-api: false</c> is emitted on every descendant of an <c>@internal</c> key,
    ///     not only on the key that declared it — charts/README.md § The annotation format, point 6 —
    ///     so a flat filter is correct and does not have to walk back up the pointer.
    /// </remarks>
    static ImmutableDictionary<string, ChartRow> ChartRows() {
        var rows = ImmutableDictionary.CreateBuilder<string, ChartRow>(StringComparer.Ordinal);
        Walk(ChartSchema(), rows);
        return rows.ToImmutable();
    }

    static void Walk(JsonObject node, ImmutableDictionary<string, ChartRow>.Builder rows) {
        if (node["properties"] is not JsonObject members) {
            return;
        }

        ImmutableHashSet<string> required = node["required"] is JsonArray names
            ? [.. names.Select(x => x!.GetValue<string>())]
            : [];

        foreach (var (name, value) in members) {
            if (value is not JsonObject member) {
                continue;
            }

            if (member["x-cybercloud-api"]?.GetValue<bool>() is false) {
                continue;
            }

            var pointer = member["x-cybercloud-pointer"]!.GetValue<string>();

            rows[pointer] = new(
                member["type"]!.GetValue<string>(),
                member["description"]?.GetValue<string>() ?? string.Empty,
                required.Contains(name),
                Choices(member),
                member["minimum"]?.GetValue<double>(),
                member["maximum"]?.GetValue<double>(),
                member["default"]?.ToJsonString() ?? string.Empty,
                member["x-cybercloud-widget"]?.GetValue<string>() ?? string.Empty,
                member["x-cybercloud-immutable"]?.GetValue<bool>() ?? false
            );

            Walk(member, rows);
        }
    }

    /// <summary>The allowed values of a row, from its own <c>enum</c> or its array's <c>items</c>.</summary>
    static ImmutableArray<string> Choices(JsonObject member) {
        var declared = member["enum"] as JsonArray ?? (member["items"] as JsonObject)?["enum"] as JsonArray;

        return declared is null ? [] : [.. declared.Select(x => x!.GetValue<string>())];
    }

    /// <summary>Every API-facing pointer the chart declares.</summary>
    static ImmutableHashSet<string> ChartPointers() => [.. ChartRows().Keys];

    /// <summary>Every pointer the registry declares that a chart row could correspond to.</summary>
    /// <remarks>
    ///     The two body-only properties are excluded here and pinned by their own test, so a failure
    ///     of the set comparison names a real disagreement rather than the known asymmetry.
    /// </remarks>
    static ImmutableHashSet<string> ApiPointers() =>
        [
            .. PostgresServers.Schema2026.Properties
                .Select(x => x.JsonPointer)
                .Where(x => x != "/location" && x != "/properties" && x != PostgresServers.ClusterIdPointer)
        ];

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

    static string TypeName(SchemaKind kind) =>
        kind switch {
            SchemaKind.Text => "string",
            SchemaKind.Number => "number",
            SchemaKind.WholeNumber => "integer",
            SchemaKind.Boolean => "boolean",
            SchemaKind.Nested => "object",
            SchemaKind.Array => "array",
            _ => kind.ToString()
        };

    static void Compare<T>(List<string> disagreements, string pointer, string what, T mine, T theirs) {
        if (!EqualityComparer<T>.Default.Equals(mine, theirs)) {
            disagreements.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {pointer} {what}: registry '{mine}', chart '{theirs}'"
                )
            );
        }
    }
}
