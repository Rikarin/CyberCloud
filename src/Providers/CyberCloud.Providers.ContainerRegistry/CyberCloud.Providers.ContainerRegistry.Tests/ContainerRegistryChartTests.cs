using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     What the chart and the registry still have to be told about each other.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a row-by-row schema comparison, and the difference is ADR-012's fifth surface
///         existing.</b> <c>build/Build.Charts.cs</c> calls <c>RunGenerator(write: true, charts:
///         true)</c>, which drives <c>ChartSurfaces.Generate</c> and <c>ChartAnnotationEmitter</c>: the
///         chart's non-<c>@internal</c> <c>@param</c> block is <i>generated</i> from
///         <see cref="ContainerRegistries.Schema2026" /> and byte-diffed. Comparing the two here would
///         be comparing a file with the file it was generated from.
///     </para>
///     <para>
///         What generation does not reach is <c>templates/</c>, which <c>ChartSurfaces</c> filters out
///         on purpose — no emitter has ever read a Helm template — and this family puts <b>two</b>
///         tables in there that also exist in C#: the sizing presets and the pinned image patches.
///         ⚠ The second one is the dangerous one, and it is new to this row: every other family's
///         template duplication is a sizing table, whose drift shows up as a pod that is the wrong
///         size. A pinned-patch table that drifted would render an image tag that does not exist, per
///         pod, after the caller was told <c>202</c>.
///     </para>
/// </remarks>
public sealed partial class ContainerRegistryChartTests {
    [Fact]
    public void ThePresetTableInTheChartIsTheSameTableAsTheRegistrys() {
        // ⚠ THE SECOND COPY, DIFFED. It exists because CyberCloud.Kubernetes.Charts does not: the
        // reconciler builds its objects in C# and the chart builds the same objects in Helm, so a
        // sizing table lives in both. Reading the template as TEXT is the only way to compare them —
        // ChartSurfaces filters templates/ out of the chart tree on purpose.
        var chart = Presets(Helpers());

        chart.Count.ShouldBe(ContainerRegistries.Presets.Count);

        foreach (var (preset, (cpu, memory)) in ContainerRegistries.Presets) {
            chart.ShouldContainKey(preset);
            chart[preset].ShouldBe(
                (cpu, memory),
                $"charts/managed/harbor/templates/_helpers.tpl sizes '{preset}' differently from "
                + "ContainerRegistries.Presets. The reconciler and the chart would provision two "
                + "different registries from one body."
            );
        }
    }

    [Fact]
    public void ThePinnedPatchTableInTheChartIsTheSameTableAsTheRegistrys() {
        // ⚠ THE COPY THAT MATTERS MORE THAN THE PRESETS, AND NO EARLIER FAMILY HAS ONE. A preset that
        // drifted provisions a pod of the wrong size, which is visible and survivable. A pinned patch
        // that drifted renders an image tag Harbor does not publish — `goharbor/harbor-core:v2.15`
        // resolves to nothing — and the failure is an ImagePullBackOff per pod, after the caller was
        // told 202, with the resource stuck InProgress and nothing naming the tag.
        var chart = PinnedPatches(Helpers());

        chart.Count.ShouldBe(ContainerRegistries.PinnedPatch.Count);

        foreach (var (minor, tag) in ContainerRegistries.PinnedPatch) {
            chart.ShouldContainKey(minor);
            chart[minor].ShouldBe(
                tag,
                $"charts/managed/harbor/templates/_helpers.tpl pins '{minor}' to a different patch "
                + "than ContainerRegistries.PinnedPatch does"
            );
        }

        // ⚠ And the template's own fallback is the DEFAULT minor's tag, matching
        // ContainerRegistries.ImageTag. A fallback that differed would make an unrecognised minor
        // render one image through the reconciler and another through the chart.
        Helpers().ShouldContain(
            "| default \"" + ContainerRegistries.PinnedPatch[ContainerRegistries.DefaultVersion] + "\"",
            Case.Sensitive
        );
    }

    [Fact]
    public void EveryPatternInTheGeneratedSchemaIsAWholeValueMatch() {
        // ⚠ THE ONE WAY A CONSTRAINT CAN REACH THE CHART AND MEAN SOMETHING ELSE THERE, which is the
        // same failure as losing it and is harder to see. SchemaProperty.Pattern is a whole-value match
        // — ResourceSchema tests it as ^(?:…)$ — and JSON Schema's `pattern` keyword is a SEARCH. A
        // bare quantity pattern in values.schema.json accepts `xxx20Gixxx`, which the API refuses: the
        // chart would be strictly more permissive than the surface it is generated from.
        var patterns = Patterns(ChartSchema()).ToList();

        patterns.Count.ShouldBeGreaterThanOrEqualTo(
            3,
            "fewer `pattern` keywords reached charts/managed/harbor/values.schema.json than "
            + "CyberCloud.ContainerRegistry/registries declares — three quantities. Either the "
            + "@pattern directive stopped being emitted or this reader stopped finding them, and a "
            + "check that inspects nothing passes."
        );

        foreach (var (pointer, pattern) in patterns) {
            // ⚠ The non-capturing group is not decoration. Anchoring `a|b` as `^a|b$` means "starts
            // with a" OR "ends with b", which is a wider language than either.
            pattern.ShouldStartWith("^(?:", Case.Sensitive, pointer);
            pattern.ShouldEndWith(")$", Case.Sensitive, pointer);
        }
    }

    [Fact]
    public void EveryApiPointerInTheShippedSchemaIsARegistryPointerWithTheSameCasing() {
        // ⚠ ONE CHARACTER OF CASING, PLACE THREE OF THREE — the registry, the OpenAPI document and the
        // chart. This reads the file that SHIPS rather than the emitter's output: `./build.sh Charts`
        // proves the generator agrees with the registry on the machine that runs it, and this proves
        // the bytes in the repository do.
        //
        // ⚠ It walks BOTH directions. Only the first would pass on a schema that had quietly lost half
        // its rows.
        var shipped = ApiPointers(ChartSchema()).ToList();

        var registry = ContainerRegistries.Schema2026.Properties
            .Where(x => x.JsonPointer.StartsWith("/properties/", StringComparison.Ordinal))
            .Where(x => x.JsonPointer != ContainerRegistries.ClusterIdPointer)
            .Select(x => x.JsonPointer)
            .ToList();

        foreach (var pointer in shipped) {
            registry.ShouldContain(
                pointer,
                $"charts/managed/harbor/values.schema.json carries the API pointer '{pointer}' and the "
                + "registry does not declare it"
            );
        }

        foreach (var pointer in registry) {
            shipped.ShouldContain(
                pointer,
                $"the registry declares '{pointer}' and charts/managed/harbor/values.schema.json does "
                + "not carry it, so the chart cannot render what the API accepts"
            );
        }
    }

    // ── Reading the two files ───────────────────────────────────────────────────────────────────

    static string Helpers() => Embedded("harbor.helpers.tpl");

    static JsonObject ChartSchema() => JsonNode.Parse(Embedded("harbor.values.schema.json"))!.AsObject();

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException(
                               $"'{name}' is not embedded in this assembly. The .csproj's "
                               + "EmbeddedResource list and this reader have to name the same file."
                           );

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    static Dictionary<string, (string Cpu, string Memory)> Presets(string helpers) {
        var found = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        foreach (Match match in PresetRow().Matches(helpers)) {
            found[match.Groups["preset"].Value] =
                (match.Groups["cpu"].Value, match.Groups["memory"].Value);
        }

        return found;
    }

    static Dictionary<string, string> PinnedPatches(string helpers) {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in PinnedRow().Matches(helpers)) {
            found[match.Groups["minor"].Value] = match.Groups["tag"].Value;
        }

        return found;
    }

    static IEnumerable<(string Pointer, string Pattern)> Patterns(JsonObject schema) {
        foreach (var (pointer, node) in Leaves(schema)) {
            if (node["pattern"]?.GetValue<string>() is { } pattern) {
                yield return (pointer, pattern);
            }
        }
    }

    /// <summary>
    ///     Every pointer the shipped schema marks as part of the API surface.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>x-cybercloud-api: false</c> is what an <c>@internal</c> row becomes in the generated
    ///     schema, and this row's <c>@internal</c> tail is eleven long — the platform identity block,
    ///     the image registry, the credentials-Secret name and Helm's plumbing. Walking every pointer
    ///     without that filter would compare the chart's whole value surface against the registry's API
    ///     surface, which are deliberately different sets.
    /// </remarks>
    static IEnumerable<string> ApiPointers(JsonObject schema) {
        foreach (var (pointer, node) in Leaves(schema)) {
            if (node["x-cybercloud-api"]?.GetValue<bool>() != false) {
                yield return pointer;
            }
        }
    }

    /// <summary>Every node carrying an <c>x-cybercloud-pointer</c>, by that pointer.</summary>
    static IEnumerable<(string Pointer, JsonObject Node)> Leaves(JsonNode? node) {
        if (node is JsonObject entry) {
            if (entry["x-cybercloud-pointer"]?.GetValue<string>() is { } pointer) {
                yield return (pointer, entry);
            }

            foreach (var member in entry) {
                foreach (var found in Leaves(member.Value)) {
                    yield return found;
                }
            }
        } else if (node is JsonArray array) {
            foreach (var member in array) {
                foreach (var found in Leaves(member)) {
                    yield return found;
                }
            }
        }
    }

    [GeneratedRegex("""^\s*"(?<preset>s1\.[a-z0-9]+)"\s*\(dict "cpu" "(?<cpu>[^"]+)"\s*"memory" "(?<memory>[^"]+)"\)""", RegexOptions.Multiline)]
    private static partial Regex PresetRow();

    [GeneratedRegex(@"^\s*""(?<minor>\d+\.\d+)"" ""(?<tag>v\d+\.\d+\.\d+)""", RegexOptions.Multiline)]
    private static partial Regex PinnedRow();
}
