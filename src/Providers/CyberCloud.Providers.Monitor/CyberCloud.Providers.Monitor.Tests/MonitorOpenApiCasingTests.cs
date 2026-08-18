using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     Every property name of the workspace type, spelled the same in all three places a caller meets
///     it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One character of casing.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was
///         only in a log line.
///     </para>
///     <para>
///         ⚠ <b>EVERY EXPECTATION BELOW IS A LITERAL, AND A PREVIOUS PROVIDER'S CASING SABOTAGE
///         STAYED GREEN BECAUSE IT WAS NOT.</b> That version built the expected path from the same two
///         constants the emitter reads, so re-casing the constant left the whole suite green — two
///         things derived from one constant agree however that constant is spelled. The strings here
///         are typed out by hand, and they are the fourth independent copy after docs/plan/16,
///         <c>charts/managed/monitor-workspace/Chart.yaml</c>'s <c>cybercloud.io/resource-type</c> and
///         that chart's <c>conformance.yaml</c>.
///     </para>
///     <para>
///         ⚠ <b>This type's casing risk is concentrated in the <c>*GbPerDay</c> triple.</b> Three
///         property names differ by one word in the middle, all three carry a unit in the name, and
///         <c>GB</c>/<c>Gb</c> is exactly the pair a reader will normalise on the way past — the
///         platform's own <c>QuotaMeter.StorageGb</c> spells it <c>Gb</c> and the value is gibibytes.
///     </para>
///     <para>
///         The document is <b>emitted here, in-process</b>, rather than read off disk:
///         <c>openapi/2026-08-01.json</c> is written by a build step that runs <i>after</i>
///         compilation, so a test embedding it would compare this build's schema against the previous
///         build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class MonitorOpenApiCasingTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be.</b></summary>
    const string QualifiedType = "CyberCloud.Monitor/workspaces";

    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in MonitorWorkspaces.Schema2026.Properties) {
            names.ShouldContain(
                property.Name,
                $"'{property.JsonPointer}' does not appear in the emitted OpenAPI document under that "
                + "exact spelling. A property whose document name differs from its registry name by "
                + "one character is a property every generated client sends to the wrong key, and the "
                + "write path then refuses it as unknown."
            );
        }
    }

    [Fact]
    public void TheCamelCasedPropertyNamesAreSpelledOutHereRatherThanDerived() {
        // ⚠ THE LITERALS ARE THE TEST. Reading these off Schema2026 would compare the schema to
        // itself.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var expected in new[] {
                     "metricsGbPerDay", "logsGbPerDay", "tracesGbPerDay", "seriesCap",
                     "cardinalityCap", "overQuotaSampleRate", "purgeProtection", "clusterId",
                     "accountId", "otlpEndpoint", "remoteWriteEndpoint", "promqlEndpoint",
                     "sqlEndpoint", "ingestKey"
                 }) {
            names.ShouldContain(expected, $"'{expected}' is not in the document under that casing");
            names.ShouldNotContain(
                expected.ToLowerInvariant(),
                $"the document carries '{expected.ToLowerInvariant()}' as well as '{expected}'. Two "
                + "spellings of one property is a body half the generated clients send to the wrong "
                + "key."
            );
        }

        // ⚠ THE PLAUSIBLE TYPO, NAMED. `GB` is how a human writes the unit and `Gb` is how this
        // platform's own QuotaMeter spells it. A document carrying both is three properties a
        // generated client sends to keys the write path refuses as unknown.
        foreach (var wrong in new[] { "metricsGBPerDay", "logsGBPerDay", "tracesGBPerDay" }) {
            names.ShouldNotContain(wrong, $"the document carries '{wrong}'");
        }
    }

    [Fact]
    public void TheApiSpellsTheTierAndTheRenderedObjectSpellsTheDayCount() {
        // ⚠ TWO VOCABULARIES IN ONE PROVIDER, AND ONLY ONE OF THEM IS THE API. `short`, `standard`
        // and `extended` are what a tenant sets and what is priced; `retentionLogsDays` and the rest
        // are what the ingest map row carries, written by MonitorWorkspaces.RowJson. A document
        // carrying the row's spelling would be a body every generated client sends to a key the write
        // path refuses as unknown — and the mistake reads as correct, because the value it carries
        // genuinely is the retention.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        names.ShouldContain("retention");
        names.ShouldContain("metrics");

        foreach (var rowSpelling in new[] {
                     "retentionMetricsDays", "retentionLogsDays", "retentionTracesDays",
                     "ingestKeySecret", "overQuotaBehaviour", "target_path_suffix", "targetRefs"
                 }) {
            names.ShouldNotContain(
                rowSpelling,
                $"the rendered object's own field name '{rowSpelling}' reached the API document. It is "
                + "the data plane's spelling, or the VictoriaMetrics operator's, not this platform's "
                + "resource body — and the two must not be the same string in the same surface."
            );
        }
    }

    [Fact]
    public void TheTypePathSurvivesIntoThePathsExactly() {
        // ⚠ The path is what the gateway routes on, and `ResourceId.TryParsePath` compares the type
        // path through ResourceTypeName — which is case-INSENSITIVE. So a mis-cased document would
        // still route, and this test is not about routing: it is about what a generated client, a
        // portal breadcrumb and docs/plan/16's prose all copy.
        MonitorWorkspaces.Type.ToString().ShouldBe(
            QualifiedType,
            "the declared type is spelled differently from docs/plan/16 and from "
            + "charts/managed/monitor-workspace/Chart.yaml's cybercloud.io/resource-type annotation."
        );

        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/" + QualifiedType + "/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as docs/plan/16 spells them"
        );

        foreach (var path in paths.Where(
                     x => x.Contains("monitor/workspaces", StringComparison.OrdinalIgnoreCase)
                 )) {
            path.Contains(QualifiedType, StringComparison.Ordinal).ShouldBeTrue(
                $"'{path}' spells the type in a casing other than '{QualifiedType}'."
            );
        }

        // ⚠ THE PLAUSIBLE TYPO, NAMED. Every other type in the catalogue is a compound —
        // `clickhouseClusters`, `managedClusters`, `virtualNetworks` — so `monitorWorkspaces` is what
        // an author following the pattern rather than reading docs/plan/16 would write, and the
        // namespace would then say `Monitor` twice.
        paths.ShouldNotContain(
            x => x.Contains("monitorWorkspaces", StringComparison.Ordinal),
            "the type path repeats the provider namespace, which docs/plan/16 does not."
        );
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        Paths().ShouldContain(
            x => x.EndsWith("/" + MonitorWorkspaces.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{MonitorWorkspaces.ListKeysAction}'"
        );

        Paths().ShouldNotContain(
            x => x.EndsWith("/listkeys", StringComparison.Ordinal),
            "the action was lower-cased on the way into the document, so every generated client would "
            + "POST to a path the gateway does not serve."
        );
    }

    [Fact]
    public void TheKubeLabelValueIsTheLowerCasedTypeAndIsLegalLabelSyntax() {
        // ⚠ The one place the casing MUST change, and the one place it must change EXACTLY. A `/` is
        // not a legal Kubernetes label value character, so the type is lower-cased with `/` replaced
        // by `_`. charts/managed/monitor-workspace/conformance.yaml pins the same string
        // independently.
        var value = KubeLabels.ResourceTypeValue(MonitorWorkspaces.Type);

        value.ShouldBe("cybercloud.monitor_workspaces");
        LabelSyntax.ValidateValue(value, KubeLabels.ResourceType).IsSuccess.ShouldBeTrue(
            "the resource-type label value is not legal Kubernetes label syntax, so every object this "
            + "provider applies would be refused at admission rather than at build time."
        );
    }

    [Fact]
    public void TheEighthLabelThisProviderAddsIsAlsoLegalLabelSyntax() {
        // ⚠ THE FIRST PROVIDER-SUPPLIED LABEL IN THE TREE, so nothing above it validates one. The
        // seven ADR-013 labels are written by KubeCommandBuilder and checked by the Labels
        // architecture gate; this one is written by the render, and an illegal key or value would be
        // refused at apply time, per object, rather than at build time.
        LabelSyntax.ValidateValue(MonitorWorkspaces.RowLabelValue, MonitorWorkspaces.RowLabel)
            .IsSuccess.ShouldBeTrue();

        MonitorWorkspaces.RowLabel.StartsWith("cybercloud.io/", StringComparison.Ordinal).ShouldBeTrue(
            "a provider-supplied label outside the platform's own prefix is one nobody can attribute."
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        return OpenApiEmitter.Emit(registry, OpenApiEmitter.ApiVersionsOf(registry).Single());
    }

    static ImmutableArray<string> Paths() => [.. Document()["paths"]!.AsObject().Select(x => x.Key)];

    static void Collect(JsonNode? node, HashSet<string> names) {
        switch (node) {
            case JsonObject map:
                foreach (var (key, value) in map) {
                    names.Add(key);
                    Collect(value, names);
                }

                break;

            case JsonArray array:
                foreach (var item in array) {
                    Collect(item, names);
                }

                break;
        }
    }
}
