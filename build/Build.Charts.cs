// Charts — docs/plan/23 § Build, row `Charts`:
// "helm lint, generate values.schema.json from annotated values, fail on drift, package".
//
// The pipeline, once, for every chart under charts/:
//
//   provider registry ──emit──►  the non-@internal @param block   (ADR-012's FIFTH surface)
//                                            │
//                                            ▼  rewritten into, @internal rows untouched
//   values.yaml (annotated)  ──parse──►  a tree of typed, described values
//                                            │
//                                            └──emit──►  values.schema.json   (checked in, diffed)
//
// ⚠ THAT TOP ARROW LANDED 2026-08-11 AND REVERSED THE ONE THAT USED TO BE DRAWN HERE. This file's
// diagram ended "the shape a ResourceSchema would be built from" — chart to registry. ADR-010
// § Which end authors the schema decided the other direction: the C# ResourceSchema is authored and
// the chart's @param block is generated from it. The seam was seen and left open here, and it went
// unnoticed because the overlap is zero resource types wide — no type in the tree has both a chart
// and a registry declaration, so nothing had ever compared the two files.
//
// ⚠ THE ORDER OF THE TWO STEPS IS THE ROUND TRIP AND IS NOT AN ACCIDENT. The block is written first
// and then parsed by the reader below. An emitter that produced anything outside the values subset
// would fail this target on its own output, with a line number, on the very run that produced it —
// rather than a fortnight later when somebody edited the file by hand.
//
// Three things this file is deliberate about, each matching Build.Generate.cs rather than inventing
// a second convention:
//
//  * A drifted artifact is REWRITTEN IN PLACE and then fails. The reviewer gets a diff to read, not
//    an instruction to run something. Build.Generate.cs does the same, for the same reason.
//  * A run that inspected nothing says so loudly. Build.Architecture.cs calls that GateStatus.Vacuous
//    and prints ○ rather than ✔; there is one gate roster here, so the same distinction is a warning
//    with a count in it. "Zero charts" and "zero problems" are not the same news.
//  * Every problem is reported, not the first. A chart author fixing one annotation per build is a
//    chart author who stops annotating.
//
// ⚠ WHY THE YAML READER IS HAND-WRITTEN AND NARROW. There is no YAML library in
// Directory.Packages.props, and adding one needs an ADR (docs/plan/02 § Dependency register). It
// would also be the wrong tool: a general parser gives back a document with no line numbers attached
// to the values, and "a malformed annotation fails with the line number" is a requirement here. So
// this reads a deliberately small subset — block mappings, two-space indents, one-line scalars,
// flow sequences — and every construct outside that subset is a build failure naming its line rather
// than a silent misparse. The subset is documented in charts/README.md § The values subset, and it
// is a subset of YAML, so `helm` reads the same file with a real parser and would disagree loudly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    /// <summary>docs/plan/03 § charts/ — every chart the platform owns or has forked.</summary>
    AbsolutePath ChartsDirectory => RootDirectory / "charts";

    /// <summary>
    ///     Where <c>helm package</c> writes the tarballs.
    ///     <para>
    ///         ⚠ Under <c>artifacts/</c>, which is gitignored, and that is the opposite of where
    ///         <see cref="OpenApiDirectory" /> puts its output. The difference is what the artifact is
    ///         for: a chart package is a build product nobody diffs, whereas an OpenAPI document and a
    ///         <c>values.schema.json</c> are contracts, and a file git cannot see is a file no diff can
    ///         fail on.
    ///     </para>
    /// </summary>
    AbsolutePath ChartPackageDirectory => ArtifactsDirectory / "charts";

    /// <summary>
    ///     The subtree whose charts are managed services, and therefore carry the extra provenance and
    ///     conformance files docs/plan/12 § The pattern, once requires.
    /// </summary>
    AbsolutePath ManagedChartsDirectory => ChartsDirectory / "managed";

    void BuildCharts()
    {
        var charts = ChartsDirectory.DirectoryExists()
            ? ChartsDirectory.GlobFiles("**/Chart.yaml")
                .Where(x => !x.ToString().Contains($"{Path.DirectorySeparatorChar}templates{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(x => x.Parent)
                .OrderBy(x => x.ToString(), StringComparer.Ordinal)
                .ToList()
            : [];

        if (charts.Count == 0)
        {
            // The same distinction Build.Architecture.cs draws with GateStatus.Vacuous, and the same
            // one Build.Generate.cs prints when no provider is registered. A target that inspected
            // nothing is green because it found nothing, not because the tree is clean.
            Log.Warning(
                "Charts: inspected 0 chart(s). {Directory}/ contains no Chart.yaml, so nothing was "
                + "linted, no values.schema.json was generated and no drift could have been detected. "
                + "That is a pass and it is worth nobody's trust yet — docs/plan/03 § charts/ has "
                + "four chart families planned. ○, not ✔.",
                ChartsDirectory.Name);

            return;
        }

        var helm = ResolveHelm();
        var failures = new List<string>();
        var totalValues = 0;
        var apiValues = 0;
        var internalValues = 0;

        // First, because everything below reads values.yaml and this is what may have rewritten it.
        RegenerateChartAnnotations(failures);

        foreach (var chart in charts)
        {
            var relative = RootDirectory.GetRelativePathTo(chart);
            var metadata = ReadChartMetadata(chart, failures);

            // Order matters: the schema is regenerated BEFORE helm lint, because helm validates
            // values.yaml against values.schema.json. Linting first would validate the new values
            // against the old schema and report the previous build's opinion.
            var counted = GenerateValuesSchema(chart, metadata, failures);

            totalValues += counted.Total;
            apiValues += counted.Api;
            internalValues += counted.Internal;

            CheckManagedChartFiles(chart, metadata, failures);
            Lint(helm, chart, failures);

            Log.Information(
                "Charts: {Chart} — {Total} annotated value(s), {Api} in the API surface, "
                + "{Internal} marked @internal",
                relative,
                counted.Total,
                counted.Api,
                counted.Internal);
        }

        Log.Information(
            "Charts: {Count} chart(s), {Total} annotated value(s), {Api} in the API surface, "
            + "{Internal} marked @internal",
            charts.Count,
            totalValues,
            apiValues,
            internalValues);

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
                Log.Error("Charts: {Failure}", failure);

            Assert.Fail(
                $"{failures.Count} chart problem(s). Listed above. docs/plan/03 § charts/ makes "
                + "values.schema.json a generated file that is diffed in CI, and a diff that does not "
                + "fail is a diff nobody reads.");
        }

        Package(helm, charts);

        Log.Information(
            "Charts: {Count} chart(s) lint clean, {Total} value(s) annotated, every "
            + "values.schema.json regenerates byte-identically",
            charts.Count,
            totalValues);
    }

    // ── ADR-012's fifth surface ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Rewrites every paired chart's <c>@param</c> block from the provider registry and fails on
    ///     drift — ADR-010 § Which end authors the schema, DECIDED 2026-08-11.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This target owns the verdict and owns no emission logic</b>, exactly as
    ///         <c>Build.Generate.cs</c> does. The emitter is
    ///         <c>CyberCloud.ResourceManager.Contracts.Generation.ChartAnnotationEmitter</c> and it runs
    ///         inside <c>CyberCloud.ResourceManager.Generator</c>, because generating from the registry
    ///         means running a provider's <c>Describe</c>, which means loading Orleans into somebody's
    ///         process — and <c>build/_build.csproj</c> deliberately references nothing from
    ///         <c>src/</c> so that it is never this one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero pairs is a warning with the two counts in it, not a pass.</b> No resource type
    ///         in the tree currently has both a chart and a registry declaration: the one managed chart
    ///         names <c>CyberCloud.DBforPostgreSQL/servers</c>, which no provider declares, and
    ///         <c>CyberCloud.Providers.Sample</c> renders no chart on purpose. So the honest report is
    ///         "one chart, zero types naming one, zero pairs compared" — the <c>GateStatus.Vacuous</c>
    ///         distinction this file's own header draws, and the failure mode
    ///         <c>Build.Generate.cs</c>'s provider discovery has already had once. Both halves of every
    ///         mismatch are named individually, so "no pair" never arrives without which end is
    ///         missing.
    ///     </para>
    /// </remarks>
    void RegenerateChartAnnotations(List<string> failures)
    {
        if (!SolutionHasProjects)
        {
            Log.Warning(
                "Charts: {Solution} has no projects, so no provider registry could be built and no "
                + "chart's @param block was compared against one. Every chart below is hand-written "
                + "and nothing checked it against an API — ADR-010 § Which end authors the schema. "
                + "○, not ✔.",
                SolutionFile.Name);

            return;
        }

        var report = RunGenerator(write: true, charts: true);

        Log.Information(
            "Charts: {Managed} managed chart(s), {Types} registry type(s) naming a chart, "
            + "{Pairs} pair(s) compared",
            report.ManagedCharts,
            report.TypesNamingAChart,
            report.ChartAnnotations.Count);

        foreach (var unpaired in report.ChartUnpaired)
            Log.Warning("Charts: {Unpaired}", unpaired);

        if (report.ChartAnnotations.Count == 0)
        {
            Log.Warning(
                "Charts: 0 registry-to-chart pair(s), so no @param block was generated and no drift "
                + "could have been detected. That is a pass and it is worth nobody's trust yet — "
                + "ADR-012's fifth surface has an emitter and this gate, and nothing to point them "
                + "at until a provider declares .Chart(...) for a chart that is in the tree. "
                + "○, not ✔.");

            return;
        }

        foreach (var annotation in report.ChartAnnotations)
        {
            foreach (var problem in annotation.Problems)
            {
                failures.Add(
                    $"{ChartsDirectory.Name}/{annotation.File} cannot be generated from "
                    + $"'{annotation.ResourceType}' at {annotation.ApiVersion}: {problem}");
            }

            if (annotation.Drifted && annotation.Published)
            {
                // ⚠ The count of preserved rows is REPORTED, not asserted from memory. This message
                // used to end "the other 26" and to claim the @internal rows were untouched while the
                // rewrite was in fact eating bootstrap.password — a nested @internal key inside a
                // generated object. A gate that misreports what it did is worse than one that says
                // nothing, so the number comes from the rewrite that just ran.
                failures.Add(
                    $"{ChartsDirectory.Name}/{annotation.File}'s @param block was not what "
                    + $"'{annotation.ResourceType}' generates at {annotation.ApiVersion}. It has been "
                    + "rewritten in place — review the diff and commit it. "
                    + $"{annotation.PreservedInternalLines} line(s) of @internal region were carried "
                    + "through untouched; ADR-010 § Which end authors the schema makes the C# "
                    + "ResourceSchema the author of the rest.");
            }
        }
    }

    // ── helm ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>helm</c> off <c>PATH</c>, or a failure that says what to install.
    /// </summary>
    /// <remarks>
    ///     ⚠ Resolved once and asserted, rather than probed per chart. A missing <c>helm</c> is a
    ///     machine-configuration problem and must not be reported as a chart problem, and it certainly
    ///     must not be reported as a pass: a target that skips <c>helm lint</c> when <c>helm</c> is
    ///     absent is a target that is green on every CI runner that forgot to install it.
    /// </remarks>
    static Tool ResolveHelm()
    {
        try
        {
            return ToolResolver.GetPathTool("helm");
        }
        catch (Exception exception)
        {
            Assert.Fail(
                "`helm` is not on PATH, so `helm lint` cannot run and this target cannot honestly "
                + "report on a chart. Install Helm 4 (`brew install helm`, or the release tarball on "
                + $"CI) and run again. Underlying error: {exception.Message}");

            throw;
        }
    }

    /// <summary>
    ///     <c>helm lint --strict</c> over one chart.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>--strict</c>, so a lint <i>warning</i> fails the target too. Without it the only
    ///         failures are the ones Helm considers fatal, and a chart can accumulate warnings until
    ///         nobody reads the output.
    ///     </para>
    ///     <para>
    ///         ⚠ This also validates <c>values.yaml</c> against the <c>values.schema.json</c> that was
    ///         just generated, using Helm's own JSON Schema implementation rather than ours. That is
    ///         the cheapest possible check that the generated schema is a real schema and not merely a
    ///         file with the right name — a schema that its own chart's defaults fail is caught here.
    ///     </para>
    /// </remarks>
    void Lint(Tool helm, AbsolutePath chart, List<string> failures)
    {
        var exitCode = 0;

        helm(
            $"lint --strict {chart}",
            workingDirectory: RootDirectory,
            exitHandler: process => exitCode = process.ExitCode);

        if (exitCode != 0)
        {
            failures.Add(
                $"`helm lint --strict {RootDirectory.GetRelativePathTo(chart)}` exited {exitCode}. Its "
                + "output is above. docs/plan/23 § Build, row Charts.");
        }
    }

    /// <summary>Packages every chart into <see cref="ChartPackageDirectory" />.</summary>
    /// <remarks>
    ///     ⚠ Runs only after every chart has passed, so a red build never leaves a tarball behind that
    ///     somebody could pick up and install.
    /// </remarks>
    void Package(Tool helm, List<AbsolutePath> charts)
    {
        ChartPackageDirectory.CreateDirectory();

        foreach (var chart in charts)
            helm($"package {chart} --destination {ChartPackageDirectory}", workingDirectory: RootDirectory);

        Log.Information(
            "Charts: {Count} package(s) written to {Directory}/",
            charts.Count,
            RootDirectory.GetRelativePathTo(ChartPackageDirectory));
    }

    // ── Chart.yaml ────────────────────────────────────────────────────────────────────────────

    /// <summary>What this target needs out of a <c>Chart.yaml</c>.</summary>
    /// <param name="Name">The chart name. Must match its directory.</param>
    /// <param name="Version">The chart version.</param>
    /// <param name="ResourceType">
    ///     <c>cybercloud.io/resource-type</c> — the resource type this chart is the configuration
    ///     surface of. Required for a chart under <c>charts/managed/</c>, absent elsewhere.
    /// </param>
    /// <param name="ApiVersion">
    ///     <c>cybercloud.io/api-version</c> — which api-version's body the generated schema is.
    /// </param>
    sealed record ChartMetadata(string Name, string Version, string? ResourceType, string? ApiVersion);

    /// <summary>
    ///     Reads the four fields above out of a <c>Chart.yaml</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately not the values reader below. A <c>Chart.yaml</c> is Helm's file, not ours:
    ///     it carries block sequences (<c>keywords</c>, <c>maintainers</c>) that the annotated-values
    ///     subset refuses on purpose, and it carries no annotations at all. Pushing it through the
    ///     strict reader would mean either loosening that reader or forbidding legal Helm metadata.
    ///     This scan reads the keys it needs and ignores everything else, and every key it needs is
    ///     asserted present.
    /// </remarks>
    ChartMetadata ReadChartMetadata(AbsolutePath chart, List<string> failures)
    {
        var file = chart / "Chart.yaml";
        var relative = RootDirectory.GetRelativePathTo(file);
        var lines = file.ReadAllLines();

        string? name = null;
        string? version = null;
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = string.Empty;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.TrimStart().Length == 0 || line.TrimStart()[0] == '#')
                continue;

            var match = ChartYamlLine.Match(line);

            if (!match.Success)
                continue;

            var indent = match.Groups["indent"].Value.Length;
            var key = match.Groups["key"].Value;
            var value = Unquote(match.Groups["value"].Value.Trim());

            if (indent == 0)
            {
                section = key;

                if (string.Equals(key, "name", StringComparison.Ordinal))
                    name = value;

                if (string.Equals(key, "version", StringComparison.Ordinal))
                    version = value;

                continue;
            }

            if (indent == 2 && string.Equals(section, "annotations", StringComparison.Ordinal))
                annotations[key] = value;
        }

        if (string.IsNullOrEmpty(name))
            failures.Add($"{relative} declares no `name`.");
        else if (!string.Equals(name, chart.Name, StringComparison.Ordinal))
        {
            failures.Add(
                $"{relative} declares `name: {name}` and sits in a directory called '{chart.Name}'. "
                + "`helm package` names the tarball after the chart name, so the two disagreeing "
                + "means the file you edit and the artifact you ship have different names.");
        }

        if (string.IsNullOrEmpty(version))
            failures.Add($"{relative} declares no `version`.");

        annotations.TryGetValue(ResourceTypeAnnotation, out var resourceType);
        annotations.TryGetValue(ApiVersionAnnotation, out var apiVersion);

        return new(name ?? chart.Name, version ?? "0.0.0", resourceType, apiVersion);
    }

    const string ResourceTypeAnnotation = "cybercloud.io/resource-type";
    const string ApiVersionAnnotation = "cybercloud.io/api-version";

    /// <summary>A <c>key: value</c> line in a <c>Chart.yaml</c>, with its indent.</summary>
    static readonly Regex ChartYamlLine =
        new(@"^(?<indent>\s*)(?<key>[A-Za-z0-9._/-]+):[ \t]*(?<value>.*)$", RegexOptions.Compiled);

    // ── The managed-service extras — docs/plan/12 § The pattern, once ─────────────────────────

    /// <summary>
    ///     The files docs/plan/12 § The pattern, once and docs/plan/03 § charts/ require of a chart
    ///     under <c>charts/managed/</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>SOURCE</c> is required even when nothing is forked</b>, and docs/plan/03
    ///         § charts/ says "if forked". That is a deliberate tightening, with a reason: ADR-010's
    ///         rule is that a vendored chart carries its provenance, and the rule is unenforceable if
    ///         "there is no SOURCE file" is a legal state — it is then indistinguishable from
    ///         "somebody forked a chart and forgot". `vendored: none` is an answer to "where did this
    ///         come from"; a missing file is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>conformance.yaml</c> is checked for presence and for naming the same resource
    ///         type, and nothing more.</b> The suite that reads it lives in <c>test/</c> and does not
    ///         exist yet. Reporting that honestly is the point: a stronger-sounding check that did not
    ///         run would be the vacuous pass this file's own warning exists to prevent.
    ///     </para>
    /// </remarks>
    void CheckManagedChartFiles(AbsolutePath chart, ChartMetadata metadata, List<string> failures)
    {
        if (!chart.ToString().StartsWith(ManagedChartsDirectory + Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            return;

        var relative = RootDirectory.GetRelativePathTo(chart);

        if (string.IsNullOrEmpty(metadata.ResourceType))
        {
            failures.Add(
                $"{relative}/Chart.yaml carries no `{ResourceTypeAnnotation}` annotation. A managed "
                + "service's chart is the configuration surface of a resource type — docs/plan/12 "
                + "§ The pattern, once, piece 2 — and a schema that does not say which type it "
                + "describes cannot become that type's API schema.");
        }

        if (string.IsNullOrEmpty(metadata.ApiVersion))
        {
            failures.Add(
                $"{relative}/Chart.yaml carries no `{ApiVersionAnnotation}` annotation. An api-version "
                + "is immutable once published (docs/plan/08 § The provider registry), so a generated "
                + "body has to say which one it is the body of.");
        }

        var source = chart / "SOURCE";

        if (!source.FileExists())
        {
            failures.Add(
                $"{relative}/SOURCE is missing. ADR-010: \"a drifting vendored chart with no "
                + "provenance is how a platform ends up unable to upgrade Postgres\". Write one, with "
                + "`vendored:` and `upstream:`; `vendored: none` is a legitimate answer and a missing "
                + "file is not.");
        }
        else
        {
            var keys = ReadFlatKeys(source);

            foreach (var required in new[] { "vendored", "upstream" })
            {
                if (!keys.ContainsKey(required))
                    failures.Add($"{relative}/SOURCE declares no `{required}:`.");
            }
        }

        var conformance = chart / "conformance.yaml";

        if (!conformance.FileExists() || conformance.ReadAllText().Trim().Length == 0)
        {
            failures.Add(
                $"{relative}/conformance.yaml is missing or empty. docs/plan/12 § The pattern, once, "
                + "piece 8 makes a conformance manifest one of the eight things every managed service "
                + "is, and \"the suite does not exist yet\" is a reason to write the manifest, not a "
                + "reason to skip it.");

            return;
        }

        var declared = ReadFlatKeys(conformance);

        if (declared.TryGetValue("resourceType", out var conformanceType)
            && !string.IsNullOrEmpty(metadata.ResourceType)
            && !string.Equals(conformanceType, metadata.ResourceType, StringComparison.Ordinal))
        {
            failures.Add(
                $"{relative}/conformance.yaml says `resourceType: {conformanceType}` and Chart.yaml "
                + $"says `{metadata.ResourceType}`. The conformance suite would assert against a type "
                + "nobody generated.");
        }
    }

    /// <summary>
    ///     Top-level <c>key: value</c> pairs of a small YAML file, ignoring comments and everything
    ///     indented. Used for <c>SOURCE</c> and for the two scalars this target reads out of a
    ///     <c>conformance.yaml</c>.
    /// </summary>
    static Dictionary<string, string> ReadFlatKeys(AbsolutePath file)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in file.ReadAllLines())
        {
            if (line.Length == 0 || line[0] == '#' || char.IsWhiteSpace(line[0]))
                continue;

            var match = ChartYamlLine.Match(line);

            if (match.Success)
                keys[match.Groups["key"].Value] = Unquote(match.Groups["value"].Value.Trim());
        }

        return keys;
    }

    static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;

    // ── values.yaml → values.schema.json ──────────────────────────────────────────────────────

    /// <param name="Total">Annotated values found, at every depth.</param>
    /// <param name="Api">Of those, the ones that are part of the resource body.</param>
    /// <param name="Internal">Of those, the ones marked <c>@internal</c>.</param>
    readonly record struct ValueCounts(int Total, int Api, int Internal);

    /// <summary>
    ///     Parses one chart's annotated <c>values.yaml</c>, emits its <c>values.schema.json</c>, and
    ///     fails the build if the checked-in file was not what this run produced.
    /// </summary>
    ValueCounts GenerateValuesSchema(AbsolutePath chart, ChartMetadata metadata, List<string> failures)
    {
        var valuesFile = chart / "values.yaml";
        var relativeValues = RootDirectory.GetRelativePathTo(valuesFile);

        if (!valuesFile.FileExists())
        {
            failures.Add(
                $"{RootDirectory.GetRelativePathTo(chart)}/values.yaml is missing. docs/plan/03 "
                + "§ charts/ makes the annotated values.yaml \"the single description of a managed "
                + "service's configuration surface\", and there is nothing to generate from.");

            return default;
        }

        var problems = new List<string>();
        var roots = ReadAnnotatedValues(valuesFile, problems);

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                failures.Add($"{relativeValues}:{problem}");

            // No schema is written from a file that did not parse. Writing a partial one would put a
            // half-described API surface on disk and then diff it, which is worse than not writing it.
            return default;
        }

        var counts = Count(roots);
        var schema = SchemaDocument(metadata, roots);
        var rendered = Render(schema);

        var schemaFile = chart / "values.schema.json";
        var relativeSchema = RootDirectory.GetRelativePathTo(schemaFile);
        var existing = schemaFile.FileExists() ? LineFeeds(schemaFile.ReadAllText()) : null;

        if (string.Equals(existing, rendered, StringComparison.Ordinal))
            return counts;

        // Rewritten in place, exactly as Build.Generate.cs does with a drifted OpenAPI document: the
        // reviewer gets a diff to read rather than a command to run.
        schemaFile.WriteAllText(rendered);

        failures.Add(
            existing is null
                ? $"{relativeSchema} did not exist and has been generated from {relativeValues}. "
                + "Review it and commit it — docs/plan/03 § charts/ marks it GENERATED, checked in, "
                + "diffed in CI."
                : $"{relativeSchema} was not what {relativeValues} generates. It has been rewritten in "
                + "place — review the diff and commit it. A generated file that is edited by hand is "
                + "a file that disagrees with its source, and docs/plan/03 § charts/ makes this one "
                + "the thing CI diffs.");

        return counts;
    }

    static ValueCounts Count(IReadOnlyList<ChartValue> values)
    {
        var total = 0;
        var api = 0;
        var isInternal = 0;

        void Walk(IReadOnlyList<ChartValue> nodes, bool inheritedInternal)
        {
            foreach (var node in nodes)
            {
                var internalHere = inheritedInternal || node.Annotation?.InternalReason is not null;

                total++;

                if (internalHere)
                    isInternal++;
                else
                    api++;

                Walk(node.Children, internalHere);
            }
        }

        Walk(values, false);

        return new(total, api, isInternal);
    }

    // ── The emitted document ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The generated <c>values.schema.json</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The schema covers every key, including the <c>@internal</c> ones, and marks them
    ///         rather than dropping them.</b> This file is Helm's contract as well as ours: Helm
    ///         validates the whole of <c>values.yaml</c> against it, and with
    ///         <c>additionalProperties: false</c> a key the schema omitted would fail
    ///         <c>helm lint</c>. So the API surface is a <i>projection</i> of this document — the
    ///         members with no <c>x-cybercloud-api: false</c> — rather than the whole of it. A reader
    ///         that wants the resource body filters; a reader that wants the chart's values does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Keys are sorted ordinally and <c>x-cybercloud-order</c> carries declaration
    ///         order.</b> Sorting is what makes the file independent of any dictionary iteration
    ///         order, and it is what <c>OpenApiEmitter</c> already does. It also destroys the order a
    ///         generated form wants its fields in, so the order is written down as data instead of
    ///         being implied by position.
    ///     </para>
    /// </remarks>
    static JsonObject SchemaDocument(ChartMetadata metadata, IReadOnlyList<ChartValue> roots)
    {
        var root = ObjectNode(roots, freeform: false, inherited: null);

        root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        root["$id"] = $"https://schema.cybercloud.io/charts/{metadata.Name}/values.schema.json";
        root["title"] = $"{metadata.Name} chart values";
        root["description"] =
            $"The configuration surface of the {metadata.Name} chart. GENERATED from values.yaml by "
            + "`./build.sh Charts` and diffed in CI — docs/plan/03 § charts/. Edit values.yaml, never "
            + "this file. Members carrying `x-cybercloud-api: false` are chart or platform plumbing "
            + "and are not part of the resource body.";

        root["x-cybercloud-chart"] = metadata.Name;
        root["x-cybercloud-chart-version"] = metadata.Version;

        if (metadata.ResourceType is not null)
            root["x-cybercloud-resource-type"] = metadata.ResourceType;

        if (metadata.ApiVersion is not null)
            root["x-cybercloud-api-version"] = metadata.ApiVersion;

        return Sorted(root);
    }

    static JsonObject ObjectNode(IReadOnlyList<ChartValue> children, bool freeform, string? inherited)
    {
        var properties = new JsonObject();
        var required = new List<string>();

        foreach (var child in children.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            properties[child.Name] = PropertyNode(child, inherited);

            if (child.Annotation!.Required)
                required.Add(child.Name);
        }

        var node = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = freeform,
        };

        if (!freeform)
            node["properties"] = properties;

        if (required.Count > 0)
        {
            var array = new JsonArray();

            foreach (var name in required.OrderBy(x => x, StringComparer.Ordinal))
                array.Add(name);

            node["required"] = array;
        }

        return node;
    }

    /// <param name="value">The value to describe.</param>
    /// <param name="inherited">
    ///     The <c>@internal</c> reason of the nearest excluded ancestor, or null.
    ///     <para>
    ///         ⚠ <b>Exclusion is written on every descendant here and may be written only once in
    ///         values.yaml.</b> The two rules point opposite ways on purpose. In the source, a repeated
    ///         <c>@internal</c> is a second place to change and a second thing to disagree with the
    ///         first, so it is a build failure. In the generated file, a consumer projecting the API
    ///         surface reads one property at a time and would otherwise have to walk back up the
    ///         pointer to discover that its parent was excluded — and a filter that is easy to get
    ///         wrong is a filter that leaks platform plumbing into a public API.
    ///     </para>
    /// </param>
    static JsonObject PropertyNode(ChartValue value, string? inherited)
    {
        var annotation = value.Annotation!;
        var excluded = annotation.InternalReason ?? inherited;

        var node = annotation.Type is "object"
            ? ObjectNode(value.Children, freeform: value.Children.Count == 0, inherited: excluded)
            : new JsonObject { ["type"] = annotation.Type };

        node["description"] = annotation.Description;
        node["x-cybercloud-pointer"] = value.Pointer;
        node["x-cybercloud-order"] = value.Order;

        if (value.Default is not null)
            node["default"] = value.Default.DeepClone();

        if (annotation.Type is "array")
        {
            // ⚠ Element shape is modelled only as far as `@enum` states it, matching SchemaKind's own
            // position that "element shape is not modelled". An `items` this emitter invented would be
            // a claim nothing validates.
            node["items"] = annotation.Choices.Count > 0
                ? Sorted(new JsonObject { ["type"] = "string", ["enum"] = Choices(annotation.Choices) })
                : new JsonObject();
        }
        else if (annotation.Choices.Count > 0)
        {
            node["enum"] = Choices(annotation.Choices, annotation.Type);
        }

        if (annotation.Minimum is not null)
        {
            node["minimum"] = JsonNode.Parse(annotation.Minimum);
            node["maximum"] = JsonNode.Parse(annotation.Maximum!);
        }

        if (annotation.MinLength is not null)
            node["minLength"] = JsonNode.Parse(annotation.MinLength);

        if (annotation.MaxLength is not null)
            node["maxLength"] = JsonNode.Parse(annotation.MaxLength);

        // ⚠ ANCHORED HERE, and the annotation carries it bare. This is not tidying: JSON Schema's
        // `pattern` is a SEARCH, and SchemaProperty.Pattern is a whole-value match that ResourceSchema
        // tests as `^(?:…)$`. Emitting `\d+Gi` bare would give Helm a schema accepting `xxx20Gixxx`
        // while the API refuses it — the chart strictly more permissive than the surface it was
        // generated from, which is a cluster rendered from values the API would have rejected, arrived
        // at from the opposite direction. The same three characters, for the same stated reason, as
        // OpenApiEmitter. The non-capturing group matters: a bare `a|b` anchored as `^a|b$` is
        // `^a` or `b$`.
        if (annotation.Pattern is not null)
            node["pattern"] = Anchored(annotation.Pattern);

        // ⚠ `examples`, plural and an array — JSON Schema 2020-12's keyword, which is what this
        // document declares as its $schema. OpenApiEmitter writes `example`, singular, because OpenAPI
        // 3.0 is a different specification with a different keyword. Two spellings of one fact, each
        // correct in its own document.
        if (annotation.Example is not null)
            node["examples"] = new JsonArray(annotation.Example.DeepClone());

        if (annotation.Format is not null)
            node["format"] = annotation.Format;

        if (annotation.Secret)
        {
            // The same three keywords OpenApiEmitter puts on a secret property, so the chart-side and
            // registry-side descriptions of "secret" are the same three words rather than two dialects.
            node["writeOnly"] = true;
            node["format"] = "password";
            node["x-cybercloud-secret"] = true;
        }

        if (annotation.Immutable)
            node["x-cybercloud-immutable"] = true;

        if (annotation.Widget is not null)
            node["x-cybercloud-widget"] = annotation.Widget;

        if (excluded is not null)
        {
            node["x-cybercloud-api"] = false;
            node["x-cybercloud-internal-reason"] = excluded;
        }

        return Sorted(node);
    }

    static JsonArray Choices(IReadOnlyList<string> choices, string type = "string")
    {
        var array = new JsonArray();

        foreach (var choice in choices)
            array.Add(type is "string" ? choice : JsonNode.Parse(choice));

        return array;
    }

    /// <summary>An object with its keys in ordinal order — the same helper OpenApiEmitter uses.</summary>
    static JsonObject Sorted(JsonObject node)
    {
        var members = node.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        var sorted = new JsonObject();

        foreach (var member in members)
        {
            node.Remove(member.Key);
            sorted[member.Key] = member.Value;
        }

        return sorted;
    }

    /// <summary>
    ///     The document as bytes on disk.
    /// </summary>
    /// <remarks>
    ///     ⚠ Three things here are about determinism rather than taste, because this file is diffed:
    ///     the newline is <c>\n</c> on every platform (the repository's .editorconfig says
    ///     <c>end_of_line = lf</c>, and a CRLF checkout would otherwise drift against a CI runner),
    ///     there is a trailing newline (<c>insert_final_newline</c>), and the encoder is the relaxed
    ///     one so an em dash in a description stays an em dash instead of becoming <c>—</c>.
    ///     The relaxed encoder is safe here for the reason its name warns about: this file is never
    ///     interpolated into HTML.
    /// </remarks>
    static string Render(JsonNode node)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        return LineFeeds(node.ToJsonString(options)) + "\n";
    }

    /// <summary>
    ///     CRLF to LF, and nothing else.
    /// </summary>
    /// <remarks>
    ///     ⚠ It does <b>not</b> trim trailing newlines, and an earlier version did. That version
    ///     compared a trimmed reading of the file against an untrimmed rendering of the document, so
    ///     every chart drifted on every run — including the run immediately after the file was
    ///     written. A drift gate that fires unconditionally is indistinguishable from one that never
    ///     fires: both are ignored within a week. The comparison is byte-for-byte apart from the line
    ///     ending, which is a checkout artifact rather than content.
    /// </remarks>
    static string LineFeeds(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ── The annotated-values reader ───────────────────────────────────────────────────────────

    /// <summary>One value in a chart's <c>values.yaml</c>, with where it was written.</summary>
    /// <param name="Name">The YAML key.</param>
    /// <param name="Pointer">
    ///     The RFC 6901 pointer this value would have as a <c>SchemaProperty</c> — for example
    ///     <c>/properties/storage/size</c>. Emitted as <c>x-cybercloud-pointer</c>.
    /// </param>
    /// <param name="Line">The 1-based line of the key, for diagnostics.</param>
    /// <param name="Order">Declaration order within the parent object, 0-based.</param>
    /// <param name="Annotation">The parsed <c>##&#160;@</c> block. Null only while a failure is being reported.</param>
    /// <param name="Default">The YAML literal as JSON, or null for an object with members.</param>
    /// <param name="Children">Members, for an object.</param>
    sealed record ChartValue(
        string Name,
        string Pointer,
        int Line,
        int Order,
        ValueAnnotation? Annotation,
        JsonNode? Default,
        List<ChartValue> Children);

    /// <summary>The parsed annotation block above one key.</summary>
    /// <remarks>
    ///     ⚠ Every member here is a directive's argument that <see cref="PropertyNode" /> writes into
    ///     <c>values.schema.json</c>. A directive whose argument has no member is a directive the reader
    ///     accepts and drops — see the remarks on <see cref="Directives" />.
    /// </remarks>
    sealed record ValueAnnotation(
        string Name,
        string Type,
        string Description,
        bool Required,
        bool Secret,
        bool Immutable,
        string? InternalReason,
        IReadOnlyList<string> Choices,
        string? Minimum,
        string? Maximum,
        string? Widget,
        string? Pattern,
        string? MinLength,
        string? MaxLength,
        string? Format,
        JsonNode? Example);

    /// <summary>
    ///     The six type names, one per <c>SchemaKind</c> member that is not <c>Unknown</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Closed on purpose. Cozystack's <c>{type}</c> is prose that its generator largely ignores;
    ///     here the token is what the API validates on, so a seventh word is a build failure rather
    ///     than a documentation curiosity.
    /// </remarks>
    static readonly string[] ValueTypes = ["array", "boolean", "integer", "number", "object", "string"];

    /// <summary>
    ///     The twelve directives, ordinally sorted. Anything else fails with its line number.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This table decides only that a verb is SPELLED correctly.</b> A directive listed here
    ///     with no case in <see cref="TakeAnnotation" /> is accepted, read and thrown away — the
    ///     annotation block parses, the build is green, and the fact never reaches
    ///     <c>values.schema.json</c>. That is the "reader accepts what the emitter never writes"
    ///     failure in its quietest form, and it is why <c>ChartAnnotationTests</c> asserts this array
    ///     against the emitter's own list rather than trusting two hand-maintained copies to agree.
    /// </remarks>
    static readonly string[] Directives = [
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

    /// <summary>
    ///     The six <c>@format</c> names, one per <see cref="SchemaFormat" /> member that is not
    ///     <c>None</c> — <c>SchemaVocabulary.Of(SchemaFormat)</c> in
    ///     <c>src/CyberCloud.ResourceManager.Contracts</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Closed, for the same reason <see cref="ValueTypes" /> is: the token becomes the
    ///     <c>format</c> keyword the API validates on, so a seventh word is a typo that reaches a
    ///     published schema rather than a documentation curiosity. ⚠ It is a second copy of a list that
    ///     lives in <c>src/</c>, because this project deliberately references nothing there — see the
    ///     header. <c>ChartAnnotationTests</c> reads this file as text and fails when the two diverge.
    /// </remarks>
    static readonly string[] FormatNames = [
        "cybercloud-region",
        "cybercloud-resource-id",
        "date-time",
        "email",
        "uri",
        "uuid",
    ];

    static readonly Regex ParamDirective = new(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)[ ]+\{(?<type>[A-Za-z]+)\}[ ]+(?<description>\S.*)$",
        RegexOptions.Compiled);

    static readonly Regex KeyLine =
        new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*):[ ]*(?<value>.*)$", RegexOptions.Compiled);

    static readonly Regex RangeDirective =
        new(@"^(?<min>-?\d+(?:\.\d+)?)\.\.(?<max>-?\d+(?:\.\d+)?)$", RegexOptions.Compiled);

    /// <summary>
    ///     <c>## @length &lt;min&gt;..&lt;max&gt;</c>, with either end optional.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An open end is legal here and is NOT legal in <see cref="RangeDirective" /></b>, and the
    ///     asymmetry is deliberate rather than an oversight. <c>SchemaProperty</c> lets a property
    ///     declare a <c>MinLength</c> with no <c>MaxLength</c> — "at least one character" is the
    ///     ordinary shape of a string constraint — so a <c>@length</c> requiring both ends would refuse
    ///     the commonest case and buy nothing. <c>@range</c>'s pattern is older, is published in
    ///     charts/README.md, and widening it is a change to a surface that already has authors; that
    ///     gap stays named rather than quietly closed. ⚠ Digits only: a negative length is not a
    ///     length, and <c>ChartAnnotationEmitter</c> refuses one rather than writing a directive this
    ///     would then call malformed.
    /// </remarks>
    static readonly Regex LengthDirective =
        new(@"^(?<min>\d*)\.\.(?<max>\d*)$", RegexOptions.Compiled);

    static readonly Regex WidgetName = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

    static readonly Regex IntegerLiteral = new(@"^-?\d+$", RegexOptions.Compiled);

    static readonly Regex NumberLiteral = new(@"^-?\d+\.\d+(?:[eE][+-]?\d+)?$", RegexOptions.Compiled);

    /// <summary>
    ///     Reads an annotated <c>values.yaml</c> into a tree.
    /// </summary>
    /// <param name="file">The chart's <c>values.yaml</c>.</param>
    /// <param name="problems">
    ///     Appended to, one entry per problem, each starting <c>{line}: </c>. ⚠ Nothing here throws on
    ///     bad input — a malformed annotation is a message with a line number, not a stack trace, and a
    ///     stack trace is what the author gets if this method takes the lazy route.
    /// </param>
    static List<ChartValue> ReadAnnotatedValues(AbsolutePath file, List<string> problems)
    {
        var lines = file.ReadAllLines();
        var roots = new List<ChartValue>();
        var open = new List<(int Indent, ChartValue Node)>();
        var pending = new List<(int Line, int Indent, string Verb, string Argument)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var line = index + 1;

            if (raw.Trim().Length == 0)
            {
                if (pending.Count > 0)
                {
                    problems.Add(
                        $"{pending[0].Line}: a blank line separates this annotation block from the key "
                        + "it describes. The block is the run of `## @` lines directly above the key, "
                        + "so that moving a key moves its annotation with it.");

                    pending.Clear();
                }

                continue;
            }

            var indent = raw.Length - raw.TrimStart(' ').Length;

            if (raw.Contains('\t', StringComparison.Ordinal))
            {
                problems.Add($"{line}: contains a tab. YAML indentation is spaces, two per level.");
                continue;
            }

            var trimmed = raw[indent..].TrimEnd();

            if (trimmed[0] == '#')
            {
                if (trimmed.StartsWith("## @", StringComparison.Ordinal))
                {
                    ReadDirective(trimmed[4..].Trim(), line, indent, pending, problems);
                    continue;
                }

                if (pending.Count > 0)
                {
                    problems.Add(
                        $"{line}: an ordinary comment interrupts an annotation block. Put prose above "
                        + "the `## @param` line, not between it and the key.");
                }

                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                problems.Add(
                    $"{line}: a block sequence. Sequences are written in flow form on the key's own "
                    + "line — `extensions: [pgvector, postgis]` — so that every value's default sits "
                    + "on the line its annotation block is attached to. charts/README.md § The values "
                    + "subset.");

                pending.Clear();
                continue;
            }

            var key = KeyLine.Match(trimmed);

            if (!key.Success)
            {
                problems.Add(
                    $"{line}: not a `key: value` line, a comment or a blank. charts/README.md § The "
                    + "values subset lists what a values.yaml may contain.");

                pending.Clear();
                continue;
            }

            if (indent % 2 != 0)
            {
                problems.Add($"{line}: indented {indent} space(s). Indentation is two spaces per level.");
                pending.Clear();
                continue;
            }

            while (open.Count > 0 && indent <= open[^1].Indent)
                open.RemoveAt(open.Count - 1);

            var expected = open.Count == 0 ? 0 : open[^1].Indent + 2;

            if (indent != expected)
            {
                problems.Add(
                    $"{line}: indented {indent} space(s) where {expected} was expected. A key is either "
                    + "a sibling of the key above it or the first member of it.");

                pending.Clear();
                continue;
            }

            var name = key.Groups["key"].Value;
            var literal = key.Groups["value"].Value.TrimEnd();
            var parent = open.Count == 0 ? null : open[^1].Node;
            var siblings = parent?.Children ?? roots;

            var annotation = TakeAnnotation(pending, name, line, indent, problems);
            var inheritedInternal = InheritedInternalReason(open);

            if (annotation is not null && inheritedInternal is not null && annotation.InternalReason is not null)
            {
                problems.Add(
                    $"{line}: '{name}' is marked `@internal` inside an object that is already "
                    + "`@internal`. Exclusion is inherited, so repeating it is a second place to change "
                    + "and a second thing to disagree with the first.");
            }

            var value = ParseLiteral(literal, line, problems);

            var pointer = (parent?.Pointer ?? "/properties") + "/" + name;
            var node = new ChartValue(name, pointer, line, siblings.Count, annotation, value.Node, []);

            siblings.Add(node);
            open.Add((indent, node));
        }

        if (pending.Count > 0)
        {
            problems.Add(
                $"{pending[0].Line}: an annotation block with no key under it. The block describes the "
                + "key on the next line; there is no next line.");
        }

        Validate(roots, problems);

        return roots;
    }

    static string? InheritedInternalReason(List<(int Indent, ChartValue Node)> open)
    {
        for (var i = open.Count - 1; i >= 0; i--)
        {
            if (open[i].Node.Annotation?.InternalReason is { } reason)
                return reason;
        }

        return null;
    }

    // ── Directives ────────────────────────────────────────────────────────────────────────────

    static void ReadDirective(
        string body,
        int line,
        int indent,
        List<(int Line, int Indent, string Verb, string Argument)> pending,
        List<string> problems)
    {
        var space = body.IndexOf(' ', StringComparison.Ordinal);
        var verb = space < 0 ? body : body[..space];
        var argument = space < 0 ? string.Empty : body[(space + 1)..].Trim();

        if (!Directives.Contains(verb, StringComparer.Ordinal))
        {
            problems.Add(
                $"{line}: `@{verb}` is not a directive. The {Directives.Length} are "
                + $"{string.Join(", ", Directives.Select(x => "@" + x))} — charts/README.md § The "
                + "annotation format.");

            return;
        }

        pending.Add((line, indent, verb, argument));
    }

    /// <summary>
    ///     Turns the pending directive block into an annotation for one key, or reports why it cannot.
    /// </summary>
    static ValueAnnotation? TakeAnnotation(
        List<(int Line, int Indent, string Verb, string Argument)> pending,
        string key,
        int keyLine,
        int keyIndent,
        List<string> problems)
    {
        var block = pending.ToList();
        pending.Clear();

        if (block.Count == 0)
        {
            problems.Add(
                $"{keyLine}: '{key}' has no `## @param` annotation. Every key carries one, because a "
                + "schema that silently omits half the configuration surface is worse than no schema. "
                + "If the key is chart or platform plumbing rather than part of the resource body, "
                + "add `## @internal <reason>` as well — charts/README.md § The annotation format.");

            return null;
        }

        var misplaced = block.FirstOrDefault(x => x.Indent != keyIndent);

        if (misplaced.Line != 0)
        {
            problems.Add(
                $"{misplaced.Line}: this annotation is indented {misplaced.Indent} space(s) and "
                + $"'{key}' on line {keyLine} is indented {keyIndent}. An annotation sits at the "
                + "indentation of the key it describes.");
        }

        var duplicated = block
            .Where(x => x.Verb is not "enum")
            .GroupBy(x => x.Verb, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicated is not null)
        {
            problems.Add(
                $"{duplicated.Skip(1).First().Line}: `@{duplicated.Key}` appears twice on '{key}'.");
        }

        var param = block.FirstOrDefault(x => x.Verb is "param");

        if (param.Line == 0)
        {
            problems.Add(
                $"{block[0].Line}: '{key}' has directives but no `## @param`. The `@param` line is what "
                + "gives a value its type and its description; the rest modify it.");

            return null;
        }

        if (block[0].Verb is not "param")
        {
            problems.Add(
                $"{block[0].Line}: `@{block[0].Verb}` comes before `@param` on '{key}'. `@param` is "
                + "the first line of the block.");
        }

        var parsed = ParamDirective.Match(param.Argument);

        if (!parsed.Success)
        {
            problems.Add(
                $"{param.Line}: malformed `@param`. The form is "
                + "`## @param <name> {<type>} <description>` — for example "
                + "`## @param replicas {integer} Number of instances, including the primary.` "
                + $"Got: `@param {param.Argument}`");

            return null;
        }

        var name = parsed.Groups["name"].Value;
        var type = parsed.Groups["type"].Value;
        var description = parsed.Groups["description"].Value.Trim();

        if (!string.Equals(name, key, StringComparison.Ordinal))
        {
            problems.Add(
                $"{param.Line}: `@param {name}` describes a key called '{key}' on line {keyLine}. The "
                + "name in the annotation is checked against the key rather than used instead of it, "
                + "so the two can never quietly disagree.");

            return null;
        }

        if (!ValueTypes.Contains(type, StringComparer.Ordinal))
        {
            problems.Add(
                $"{param.Line}: `{{{type}}}` is not a type. The six are "
                + $"{string.Join(", ", ValueTypes)} — one per SchemaKind member.");

            return null;
        }

        var choices = new List<string>();
        string? minimum = null;
        string? maximum = null;
        string? widget = null;
        string? internalReason = null;
        string? pattern = null;
        string? minLength = null;
        string? maxLength = null;
        string? format = null;
        JsonNode? example = null;
        var required = false;
        var secret = false;
        var immutable = false;

        foreach (var (line, _, verb, argument) in block)
        {
            switch (verb)
            {
                case "param":
                    break;

                case "required":
                case "secret":
                case "immutable":
                    if (argument.Length > 0)
                        problems.Add($"{line}: `@{verb}` takes no argument. Got `{argument}`.");

                    required |= verb is "required";
                    secret |= verb is "secret";
                    immutable |= verb is "immutable";
                    break;

                case "internal":
                    if (argument.Length == 0)
                    {
                        problems.Add(
                            $"{line}: `@internal` needs a reason. Excluding a key from the API surface "
                            + "is a decision, and the next person has to be able to read why — the same "
                            + "discipline as [DurableStateRationale] in docs/plan/05 § The two tiers.");
                    }

                    internalReason = argument.Length == 0 ? "?" : argument;
                    break;

                case "enum":
                    foreach (var choice in argument.Split('|'))
                    {
                        var value = choice.Trim();

                        if (value.Length == 0)
                        {
                            problems.Add($"{line}: `@enum` has an empty member. Members are separated by `|`.");
                            continue;
                        }

                        if (choices.Contains(value, StringComparer.Ordinal))
                        {
                            problems.Add($"{line}: `@enum` lists `{value}` twice.");
                            continue;
                        }

                        choices.Add(value);
                    }

                    break;

                case "range":
                    var range = RangeDirective.Match(argument);

                    if (!range.Success)
                    {
                        problems.Add(
                            $"{line}: malformed `@range`. The form is `## @range <min>..<max>` — for "
                            + $"example `## @range 1..5`. Got `{argument}`.");

                        break;
                    }

                    minimum = range.Groups["min"].Value;
                    maximum = range.Groups["max"].Value;
                    break;

                case "widget":
                    if (!WidgetName.IsMatch(argument))
                    {
                        problems.Add(
                            $"{line}: `@widget` takes one lower-case name, for example `storageclass`. "
                            + $"Got `{argument}`. It becomes x-cybercloud-widget, which ADR-012 makes "
                            + "the portal's hint for a picker.");

                        break;
                    }

                    widget = argument;
                    break;

                case "length":
                    var lengths = LengthDirective.Match(argument);

                    if (!lengths.Success
                        || (lengths.Groups["min"].Value.Length == 0
                            && lengths.Groups["max"].Value.Length == 0))
                    {
                        problems.Add(
                            $"{line}: malformed `@length`. The form is `## @length <min>..<max>` and "
                            + "either end may be empty — `1..63`, `1..`, `..63` — but not both. Got "
                            + $"`{argument}`.");

                        break;
                    }

                    minLength = Empty(lengths.Groups["min"].Value);
                    maxLength = Empty(lengths.Groups["max"].Value);
                    break;

                case "pattern":
                    // ⚠ The argument is taken WHOLE and is not split, unquoted or unescaped. A pattern
                    // is full of characters that mean something to something — `|` to `@enum`, `#` to
                    // YAML, `:` to a mapping — and every one of them is inert here: the line is a
                    // comment, so no YAML parser reads it as structure, and this is the only directive
                    // that consumes the rest of the line verbatim. What a pattern CANNOT carry is
                    // leading or trailing whitespace, because ReadDirective has already trimmed it
                    // twice by the time it arrives; ChartAnnotationEmitter refuses to write one rather
                    // than let it come back as a different pattern.
                    if (argument.Length == 0)
                    {
                        problems.Add(
                            $"{line}: `@pattern` needs a regular expression. An empty one matches "
                            + "everything, which is a constraint that says nothing — omit the directive "
                            + "instead.");

                        break;
                    }

                    try
                    {
                        _ = Regex.Match(string.Empty, argument, RegexOptions.None, PatternTimeout);
                    }
                    catch (ArgumentException malformed)
                    {
                        problems.Add(
                            $"{line}: `@pattern` is not a regular expression that compiles: "
                            + $"{malformed.Message}");

                        break;
                    }

                    pattern = argument;
                    break;

                case "format":
                    if (!FormatNames.Contains(argument, StringComparer.Ordinal))
                    {
                        problems.Add(
                            $"{line}: `@format {argument}` is not a format. The "
                            + $"{FormatNames.Length} are {string.Join(", ", FormatNames)} — one per "
                            + "SchemaFormat member that is not None. The token becomes the `format` "
                            + "keyword the API validates on, so it is closed rather than free text.");

                        break;
                    }

                    format = argument;
                    break;

                case "example":
                    // ⚠ JSON, not a YAML scalar, and that is what makes this directive safe. An
                    // example is written by ChartAnnotationEmitter as compact JSON, so it is one line
                    // by construction and every control character it could carry is already escaped.
                    // Parsing it back with the same grammar is what makes the round trip an equality
                    // rather than a resemblance.
                    if (argument.Length == 0)
                    {
                        problems.Add($"{line}: `@example` needs a JSON value. Got nothing.");
                        break;
                    }

                    try
                    {
                        example = JsonNode.Parse(argument);
                    }
                    catch (JsonException malformed)
                    {
                        problems.Add(
                            $"{line}: `@example` takes one JSON value on one line — `\"20Gi\"`, `14`, "
                            + $"`[\"pgvector\"]`. Got `{argument}`, which is not JSON: "
                            + $"{malformed.Message}");
                    }

                    break;

                default:
                    break;
            }
        }

        if (description.Length == 0)
        {
            problems.Add(
                $"{param.Line}: '{key}' has an empty description. The description is the CLI help, the "
                + "SDK doc comment and the portal field label — ADR-012.");
        }

        return new(
            name,
            type,
            description,
            required,
            secret,
            immutable,
            internalReason,
            choices,
            minimum,
            maximum,
            widget,
            pattern,
            minLength,
            maxLength,
            format,
            example);
    }

    static string? Empty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    ///     How long a <c>@pattern</c> may run against the empty string before it is a bug rather than a
    ///     match — <c>SchemaProperty.PatternTimeout</c>'s value, restated here.
    /// </summary>
    static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    // ── Literals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>What a YAML literal turned out to be, so the declared type can be checked against it.</summary>
    enum LiteralKind
    {
        Missing,
        String,
        Integer,
        Number,
        Boolean,
        Array,
        EmptyObject,
    }

    readonly record struct Literal(LiteralKind Kind, JsonNode? Node);

    static Literal ParseLiteral(string text, int line, List<string> problems)
    {
        if (text.Length == 0)
            return new(LiteralKind.Missing, null);

        if (text is "{}")
            return new(LiteralKind.EmptyObject, new JsonObject());

        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
        {
            var array = new JsonArray();
            var body = text[1..^1].Trim();

            if (body.Length > 0)
            {
                foreach (var element in body.Split(','))
                {
                    var member = ParseLiteral(element.Trim(), line, problems);

                    if (member.Kind is LiteralKind.Array or LiteralKind.EmptyObject or LiteralKind.Missing)
                    {
                        problems.Add(
                            $"{line}: a sequence member is not a scalar. Nested sequences and maps are "
                            + "outside the values subset — charts/README.md § The values subset.");

                        continue;
                    }

                    array.Add(member.Node);
                }
            }

            return new(LiteralKind.Array, array);
        }

        if (text is "true" or "false")
            return new(LiteralKind.Boolean, JsonValue.Create(text is "true"));

        if (text[0] is '"' or '\'')
        {
            if (text.Length < 2 || text[^1] != text[0])
            {
                problems.Add(
                    $"{line}: a quoted scalar that does not end with the quote it opened with. An "
                    + "inline `#` comment after a value is the usual cause, and comments after a value "
                    + "are outside the values subset — put the prose in the description.");

                return new(LiteralKind.String, JsonValue.Create(text));
            }

            var inner = text[0] == '"'
                ? JsonNode.Parse(text)?.GetValue<string>() ?? string.Empty
                : text[1..^1].Replace("''", "'", StringComparison.Ordinal);

            return new(LiteralKind.String, JsonValue.Create(inner));
        }

        if (text.Contains(" #", StringComparison.Ordinal))
        {
            problems.Add(
                $"{line}: an inline comment after a value. A `#` after a scalar would silently become "
                + "part of it here; put the prose in the `@param` description instead, where it reaches "
                + "the CLI help and the portal.");

            return new(LiteralKind.String, JsonValue.Create(text));
        }

        if (IntegerLiteral.IsMatch(text))
            return new(LiteralKind.Integer, JsonNode.Parse(text));

        if (NumberLiteral.IsMatch(text))
            return new(LiteralKind.Number, JsonNode.Parse(text));

        if (text is "null" or "~")
        {
            problems.Add(
                $"{line}: a null value. A key with no value has no default, and `helm lint` would "
                + "reject `null` against the type its own generated schema declares. Give it a "
                + "default, or `\"\"` / `[]` / `{{}}`.");

            return new(LiteralKind.Missing, null);
        }

        return new(LiteralKind.String, JsonValue.Create(text));
    }

    // ── Cross-checks ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Checks each value's declared type against what was written, and each modifier against the
    ///     type it modifies.
    /// </summary>
    /// <remarks>
    ///     ⚠ The declared type is not inference and it is not decoration: it is a claim, and every one
    ///     of them is checked against the literal on the same line. That is the whole reason
    ///     <c>{type}</c> is mandatory here when Cozystack's is advisory — a `{string}` over a bare
    ///     <c>17</c> is the bug that produces an API taking a number where the SDK sends a string, and
    ///     it is invisible in any scheme that infers.
    /// </remarks>
    static void Validate(IReadOnlyList<ChartValue> values, List<string> problems)
    {
        foreach (var value in values)
        {
            if (value.Annotation is not { } annotation)
            {
                Validate(value.Children, problems);
                continue;
            }

            var literal = LiteralKindOf(value);

            switch (annotation.Type)
            {
                case "object" when value.Children.Count > 0 && literal is not LiteralKind.Missing:
                    problems.Add(
                        $"{value.Line}: '{value.Name}' is an object with members and also has a value "
                        + "on its own line.");

                    break;

                case "object" when value.Children.Count == 0 && literal is not LiteralKind.EmptyObject:
                    problems.Add(
                        $"{value.Line}: '{value.Name}' is declared `{{object}}` with no members. An "
                        + "object with no members is a free-form map and is written `{{}}`; anything "
                        + "else is a key whose members were forgotten.");

                    break;

                case "array" when literal is not LiteralKind.Array:
                    problems.Add(
                        $"{value.Line}: '{value.Name}' is declared `{{array}}` and its value is not a "
                        + "flow sequence. Write `[]` or `[a, b]`.");

                    break;

                case "boolean" when literal is not LiteralKind.Boolean:
                    problems.Add($"{value.Line}: '{value.Name}' is declared `{{boolean}}` and is not `true` or `false`.");
                    break;

                case "integer" when literal is not LiteralKind.Integer:
                    problems.Add($"{value.Line}: '{value.Name}' is declared `{{integer}}` and its value is not a whole number.");
                    break;

                case "number" when literal is not (LiteralKind.Integer or LiteralKind.Number):
                    problems.Add($"{value.Line}: '{value.Name}' is declared `{{number}}` and its value is not a number.");
                    break;

                case "string" when literal is not LiteralKind.String:
                    problems.Add(
                        $"{value.Line}: '{value.Name}' is declared `{{string}}` and its value reads as "
                        + $"{Article(literal)}. Quote it — `\"17\"`, not `17` — or correct the "
                        + "declared type.");

                    break;

                default:
                    break;
            }

            if (annotation.Choices.Count > 0 && annotation.Type is "object" or "boolean")
                problems.Add($"{value.Line}: `@enum` on a `{{{annotation.Type}}}`. It applies to strings, numbers and arrays.");

            if (annotation.Minimum is not null)
            {
                if (annotation.Type is not ("integer" or "number"))
                    problems.Add($"{value.Line}: `@range` on a `{{{annotation.Type}}}`. It applies to integers and numbers.");
                else
                    CheckRange(value, annotation, problems);
            }

            if (annotation.Secret && annotation.Type is not "string")
                problems.Add($"{value.Line}: `@secret` on a `{{{annotation.Type}}}`. A secret is a string.");

            // ⚠ The three string refinements. `pattern`, `minLength`/`maxLength` and `format` are
            // JSON Schema keywords that apply to a string and are SILENTLY IGNORED on anything else —
            // so a `@pattern` on an `{integer}` is not a stricter integer, it is a constraint the
            // author believes is enforced and that validates nothing at all. Refused rather than
            // emitted, exactly as `@secret` and `@range` are on the wrong type.
            foreach (var (directive, present) in new[]
                     {
                         ("@pattern", annotation.Pattern is not null),
                         ("@length", annotation.MinLength is not null || annotation.MaxLength is not null),
                         ("@format", annotation.Format is not null),
                     })
            {
                if (present && annotation.Type is not "string")
                {
                    problems.Add(
                        $"{value.Line}: `{directive}` on a `{{{annotation.Type}}}`. It refines a string, "
                        + "and JSON Schema ignores it on any other type — so it would read as a "
                        + "constraint and validate nothing.");
                }
            }

            if (annotation.Format is not null && annotation.Secret)
            {
                problems.Add(
                    $"{value.Line}: '{value.Name}' carries both `@secret` and `@format "
                    + $"{annotation.Format}`. `@secret` already emits `format: password`, so the two "
                    + "write the same keyword and one of them would be lost without a word being said.");
            }

            CheckLength(value, annotation, problems);
            CheckPattern(value, annotation, problems);

            if (annotation.Widget is not null && annotation.Type is "object" or "array")
                problems.Add($"{value.Line}: `@widget` on a `{{{annotation.Type}}}`. A widget renders one scalar field.");

            CheckChoices(value, annotation, problems);
            Validate(value.Children, problems);
        }
    }

    /// <summary>"an integer", "a boolean" — the diagnostic reads as a sentence or it does not read.</summary>
    static string Article(LiteralKind kind)
    {
        var name = kind switch
        {
            LiteralKind.EmptyObject => "empty map",
            LiteralKind.Missing => "key with no value",
            _ => kind.ToString().ToLowerInvariant(),
        };

        return (name[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an " : "a ") + name;
    }

    static LiteralKind LiteralKindOf(ChartValue value) =>
        value.Default switch
        {
            null => LiteralKind.Missing,
            JsonArray => LiteralKind.Array,
            JsonObject => LiteralKind.EmptyObject,
            _ => value.Default.GetValueKind() switch
            {
                JsonValueKind.String => LiteralKind.String,
                JsonValueKind.True or JsonValueKind.False => LiteralKind.Boolean,
                JsonValueKind.Number => value.Default.ToJsonString().Contains('.', StringComparison.Ordinal)
                    ? LiteralKind.Number
                    : LiteralKind.Integer,
                _ => LiteralKind.Missing,
            },
        };

    static void CheckRange(ChartValue value, ValueAnnotation annotation, List<string> problems)
    {
        var minimum = decimal.Parse(annotation.Minimum!, CultureInfo.InvariantCulture);
        var maximum = decimal.Parse(annotation.Maximum!, CultureInfo.InvariantCulture);

        if (minimum > maximum)
        {
            problems.Add($"{value.Line}: `@range {annotation.Minimum}..{annotation.Maximum}` is empty — no value satisfies it.");
            return;
        }

        if (value.Default is null || value.Default.GetValueKind() is not JsonValueKind.Number)
            return;

        var actual = decimal.Parse(value.Default.ToJsonString(), CultureInfo.InvariantCulture);

        if (actual < minimum || actual > maximum)
        {
            problems.Add(
                $"{value.Line}: '{value.Name}' defaults to {actual.ToString(CultureInfo.InvariantCulture)}, "
                + $"outside its own `@range {annotation.Minimum}..{annotation.Maximum}`. `helm lint` "
                + "would reject the chart's own values against the schema this generates.");
        }
    }

    /// <summary>
    ///     <c>@length</c> against itself and against the value on the annotated line.
    /// </summary>
    /// <remarks>
    ///     ⚠ The default is checked for the same reason <see cref="CheckRange" /> checks it: <c>helm
    ///     lint --strict</c> validates <c>values.yaml</c> against the <c>values.schema.json</c> this
    ///     file generates from it, so a default outside its own constraint fails the very next step of
    ///     the same target — with Helm's message rather than one naming the line.
    /// </remarks>
    static void CheckLength(ChartValue value, ValueAnnotation annotation, List<string> problems)
    {
        if (annotation.MinLength is null && annotation.MaxLength is null)
            return;

        var shortest = annotation.MinLength is null
            ? (int?)null
            : int.Parse(annotation.MinLength, CultureInfo.InvariantCulture);

        var longest = annotation.MaxLength is null
            ? (int?)null
            : int.Parse(annotation.MaxLength, CultureInfo.InvariantCulture);

        if (shortest is { } floor && longest is { } ceiling && floor > ceiling)
        {
            problems.Add(
                $"{value.Line}: `@length {annotation.MinLength}..{annotation.MaxLength}` is empty — no "
                + "string satisfies it.");

            return;
        }

        if (value.Default?.GetValueKind() is not JsonValueKind.String)
            return;

        var text = value.Default.GetValue<string>();

        if (shortest is { } atLeast && text.Length < atLeast)
        {
            problems.Add(
                $"{value.Line}: '{value.Name}' defaults to a string of {text.Length} character(s), "
                + $"below its own `@length {annotation.MinLength}..{annotation.MaxLength}`. `helm lint` "
                + "would reject the chart's own values against the schema this generates.");
        }

        if (longest is { } atMost && text.Length > atMost)
        {
            problems.Add(
                $"{value.Line}: '{value.Name}' defaults to a string of {text.Length} character(s), "
                + $"above its own `@length {annotation.MinLength}..{annotation.MaxLength}`.");
        }
    }

    /// <summary>
    ///     The value on the annotated line against its own <c>@pattern</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Anchored, because the emitted keyword is anchored.</b> <see cref="PropertyNode" />
    ///     writes <c>^(?:…)$</c> into <c>values.schema.json</c>, so checking the default with a bare
    ///     <see cref="Regex.IsMatch(string, string)" /> — a search — would approve a default that
    ///     <c>helm lint</c> then rejects two steps later against this file's own output. The anchoring
    ///     is written once, in <see cref="Anchored" />, so the two cannot drift apart.
    /// </remarks>
    static void CheckPattern(ChartValue value, ValueAnnotation annotation, List<string> problems)
    {
        if (annotation.Pattern is null || value.Default?.GetValueKind() is not JsonValueKind.String)
            return;

        var text = value.Default.GetValue<string>();

        try
        {
            if (Regex.IsMatch(text, Anchored(annotation.Pattern), RegexOptions.None, PatternTimeout))
                return;
        }
        catch (RegexMatchTimeoutException)
        {
            problems.Add(
                $"{value.Line}: '{value.Name}' has a `@pattern` that did not finish matching its own "
                + "default within 100ms. A pattern that backtracks is a pattern an attacker can hold "
                + "the API open with.");

            return;
        }

        problems.Add(
            $"{value.Line}: '{value.Name}' defaults to `{text}`, which its own `@pattern "
            + $"{annotation.Pattern}` does not match as a whole value. `helm lint` would reject the "
            + "chart's own values against the schema this generates.");
    }

    /// <summary>
    ///     A <c>@pattern</c> as the whole-value match it is meant to be — the one place the anchoring
    ///     is spelled, read by both <see cref="PropertyNode" /> and <see cref="CheckPattern" />.
    /// </summary>
    static string Anchored(string pattern) => "^(?:" + pattern + ")$";

    static void CheckChoices(ChartValue value, ValueAnnotation annotation, List<string> problems)
    {
        if (annotation.Choices.Count == 0 || value.Default is null)
            return;

        if (annotation.Type is "array")
        {
            foreach (var element in value.Default.AsArray())
            {
                var text = element?.GetValueKind() is JsonValueKind.String
                    ? element.GetValue<string>()
                    : element?.ToJsonString() ?? string.Empty;

                if (!annotation.Choices.Contains(text, StringComparer.Ordinal))
                {
                    problems.Add(
                        $"{value.Line}: '{value.Name}' defaults to a sequence containing `{text}`, which "
                        + $"its own `@enum` does not list ({string.Join(" | ", annotation.Choices)}).");
                }
            }

            return;
        }

        var literal = value.Default.GetValueKind() is JsonValueKind.String
            ? value.Default.GetValue<string>()
            : value.Default.ToJsonString();

        if (!annotation.Choices.Contains(literal, StringComparer.Ordinal))
        {
            problems.Add(
                $"{value.Line}: '{value.Name}' defaults to `{literal}`, which its own `@enum` does not "
                + $"list ({string.Join(" | ", annotation.Choices)}). `helm lint` would reject the "
                + "chart's own values against the schema this generates.");
        }
    }
}
