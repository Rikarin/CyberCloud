// Definitions — the operator-owned CustomResourceDefinitions committed under charts/bundle/*/crds/,
// checked two ways. The OFFLINE half joins the Bundle row: every operator-owned kind a managed chart
// renders has a committed definition, and every committed definition is for a kind a chart renders,
// under the component whose `serves:` covers it. The ONLINE half is its own row, `Definitions`: the
// pinned release is fetched and each committed file is compared with it byte for byte, through the
// one script that also writes them — charts/bundle/crds.sh — and reports ○ rather than ✔ when the
// release cannot be fetched.
//
// ⚠ WHY THIS EXISTS — ISSUE #91. FakeKubeCluster validates every applied custom resource against these
// files (test/CyberCloud.Conformance § StructuralSchema), and the cluster-backed suite installs them
// into k3s instead of an open stub. Before them, the only schema a conformance run ever met accepted
// anything, and charts/managed/seaweedfs-bucket rendered three fields in the wrong shape for a month
// under a green suite. A committed copy that drifts from the release the bundle installs is the same
// hole reopened one layer down — the harness would be refusing what an OLD operator refuses — which is
// what the online row exists to close and the reason it compares bytes rather than parsing.
//
// ⚠ WHY THE ONLINE ROW SHELLS OUT RATHER THAN FETCHING IN C#. Seven of the fourteen components keep
// their definitions under a chart's templates/ behind a values switch, so "what the release installs"
// is a `helm template --include-crds` with the component's own values — a Go-template render this
// build cannot reproduce. Two implementations of the extraction would be two opinions about the
// bytes; one script writes and checks, and this row reads its exit code. What that costs is the row's
// honesty about its tools: no Git bash, no helm, no network, and it says ○ with the reason.
//
// WHAT WOULD HAVE TO BREAK FOR EACH CHECK TO GO RED:
//
//   * a chart renders an operator-owned kind and no charts/bundle/<x>/crds/<plural>.<group>.yaml
//     defines it                                                                      → Bundle
//   * a committed definition is for a kind no chart renders, sits under a component whose
//     `serves:` does not cover its group/version, or is misnamed                       → Bundle
//   * a committed definition no longer matches the release its component pins          → Definitions
//   * a chart renders a kind the pinned release does not define at all                 → Definitions

using Nuke.Common.IO;
using Nuke.Common.Tooling;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

partial class Build {
    /// <summary>The script that writes and checks the committed definitions.</summary>
    AbsolutePath DefinitionsScript => BundleDirectory / "crds.sh";

    /// <summary>One committed definition, read narrowly: what the Bundle row needs to place it.</summary>
    /// <param name="File">The file, relative to the root, for a message.</param>
    /// <param name="Component">The component directory it sits under.</param>
    /// <param name="Group">Its <c>spec.group</c>.</param>
    /// <param name="Kind">Its <c>spec.names.kind</c>.</param>
    /// <param name="Plural">Its <c>spec.names.plural</c>.</param>
    /// <param name="Versions">The names under <c>spec.versions</c>.</param>
    sealed record CommittedDefinition(
        string File,
        string Component,
        string Group,
        string Kind,
        string Plural,
        IReadOnlyList<string> Versions);

    /// <summary>
    ///     Every operator-owned kind a managed chart renders has a committed definition under the
    ///     component that serves it, and nothing else is committed.
    /// </summary>
    /// <param name="components">The components, for <c>serves:</c>.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both directions, for the reason <see cref="RosterViolations" /> checks both.</b>
    ///         A rendered kind with no definition is a kind the fake echoes — the state issue #91
    ///         found a live defect in — so it fails here rather than waiting for a conformance run to
    ///         notice. A committed definition nothing renders is a file the script would delete on
    ///         the next <c>--refresh</c> and nothing reads meanwhile; it fails so that a chart that
    ///         stopped rendering a kind leaves no schema behind to be mistaken for coverage.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reads no network and runs no helm</b>, like every other check the Bundle row
    ///         makes: the question here is whether the tree is consistent with itself. Whether the
    ///         files match the release is <see cref="DefinitionsGate" />'s question.
    ///     </para>
    /// </remarks>
    IEnumerable<string> DefinitionViolations(List<BundleComponent> components) {
        var servedBy = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var component in components) {
            foreach (var entry in component.Serves) {
                servedBy.TryAdd(entry, component.Name);
            }
        }

        var committed = ReadCommittedDefinitions(out var unreadable);

        foreach (var problem in unreadable) {
            yield return problem;
        }

        var rendered = ReadRenderedKinds()
            .Where(x => servedBy.ContainsKey(x.ApiVersion))
            .ToList();

        foreach (var (apiVersion, kind, sources) in rendered) {
            var component = servedBy[apiVersion];
            var group = apiVersion[..apiVersion.LastIndexOf('/')];
            var version = apiVersion[(apiVersion.LastIndexOf('/') + 1)..];

            var definition = committed.FirstOrDefault(x =>
                string.Equals(x.Group, group, StringComparison.Ordinal)
                && string.Equals(x.Kind, kind, StringComparison.Ordinal)
            );

            if (definition is null) {
                yield return
                    $"{string.Join(", ", sources)} renders {apiVersion} {kind}, which charts/bundle/{component} "
                    + $"serves, and charts/bundle/{component}/crds/ holds no definition for it. Without one the "
                    + "conformance harness echoes whatever the reconciler renders — issue #91 — so run "
                    + "`./charts/bundle/crds.sh --refresh --component " + component + "` and commit what it writes";

                continue;
            }

            if (!string.Equals(definition.Component, component, StringComparison.Ordinal)) {
                yield return
                    $"{definition.File} defines {group} {kind}, and the component whose `serves:` covers "
                    + $"{apiVersion} is charts/bundle/{component}. A definition lives beside the component "
                    + "that installs it; crds.sh --refresh writes it there";
            }

            if (!definition.Versions.Contains(version, StringComparer.Ordinal)) {
                yield return
                    $"{string.Join(", ", sources)} renders {apiVersion} {kind} and {definition.File} serves "
                    + $"{kind} only at {string.Join(", ", definition.Versions)}. A real API server answers 404 "
                    + "for the version the chart renders — the Strimzi-drops-v1beta2 finding, one level down "
                    + "from `serves:`";
            }
        }

        foreach (var definition in committed) {
            var expectedName = definition.Plural + "." + definition.Group + ".yaml";

            if (!definition.File.EndsWith("/" + expectedName, StringComparison.Ordinal)) {
                yield return
                    $"{definition.File} defines {definition.Plural}.{definition.Group} and is not named "
                    + $"{expectedName}. crds.sh names a file after the definition's metadata.name so the "
                    + "check can find it; a file named otherwise is one the script would write a twin of";
            }

            if (!rendered.Any(x =>
                    string.Equals(x.ApiVersion[..x.ApiVersion.LastIndexOf('/')], definition.Group, StringComparison.Ordinal)
                    && string.Equals(x.Kind, definition.Kind, StringComparison.Ordinal)
                )) {
                yield return
                    $"{definition.File} defines {definition.Group} {definition.Kind}, and no template under "
                    + "charts/managed/ renders that kind. Only what a chart renders is committed — the rest "
                    + "of an operator's definitions would be megabytes nothing reads — so either the chart "
                    + "stopped rendering it, in which case `./charts/bundle/crds.sh --refresh` removes the "
                    + "file, or the template spells the kind in a way the scan cannot see";
            }

            var covers = definition.Versions.Any(version => servedBy.TryGetValue(definition.Group + "/" + version, out var owner)
                && string.Equals(owner, definition.Component, StringComparison.Ordinal));

            if (!covers) {
                yield return
                    $"{definition.File} sits under charts/bundle/{definition.Component}, whose `serves:` "
                    + $"covers no version of {definition.Group} the definition serves "
                    + $"({string.Join(", ", definition.Versions)}). A definition beside a component that "
                    + "does not install it is a schema the bundle never puts on a cluster";
            }
        }
    }

    /// <summary>
    ///     Every <c>charts/bundle/*/crds/*.yaml</c>, read as far as placing it needs.
    /// </summary>
    /// <param name="violations">Files that are not one definition, or cannot be read as one.</param>
    /// <remarks>
    ///     ⚠ <b>A line reader and a JSON reader, no YAML library</b>, for the reason
    ///     <see cref="ReadBundleSequence" /> gives: the build has no YAML parser and a definition's
    ///     identifiers sit at fixed indents in every file controller-gen writes. The one publisher
    ///     that renders through <c>toJson</c> (victoria-metrics-operator) puts <c>spec</c> on one
    ///     line as JSON, which <see cref="JsonDocument" /> reads exactly. Anything else is reported
    ///     rather than guessed at.
    /// </remarks>
    List<CommittedDefinition> ReadCommittedDefinitions(out List<string> violations) {
        violations = [];
        var found = new List<CommittedDefinition>();

        foreach (var directory in BundleDirectory.GlobDirectories("*").OrderBy(x => x.Name, StringComparer.Ordinal)) {
            var crds = directory / "crds";

            if (!crds.DirectoryExists()) {
                continue;
            }

            foreach (var file in crds.GlobFiles("*").OrderBy(x => x.Name, StringComparer.Ordinal)) {
                var relative = RootDirectory.GetRelativePathTo(file).ToString().Replace('\\', '/');

                if (!file.Name.EndsWith(".yaml", StringComparison.Ordinal)) {
                    violations.Add(
                        $"{relative} is under a crds/ directory and is not a .yaml file. crds.sh writes one "
                        + "definition per .yaml and the harness reads nothing else there"
                    );

                    continue;
                }

                if (ReadDefinition(file, directory.Name, relative) is { } definition) {
                    found.Add(definition);
                } else {
                    violations.Add(
                        $"{relative} could not be read as one apiextensions.k8s.io/v1 CustomResourceDefinition "
                        + "with a spec.group, spec.names.kind, spec.names.plural and at least one served "
                        + "version. Only what `./charts/bundle/crds.sh --refresh` writes belongs in a crds/ "
                        + "directory"
                    );
                }
            }
        }

        return found;
    }

    static CommittedDefinition? ReadDefinition(AbsolutePath file, string component, string relative) {
        var lines = file.ReadAllLines();
        var isDefinition = false;
        var group = string.Empty;
        var kind = string.Empty;
        var plural = string.Empty;
        var versions = new List<string>();
        var section = string.Empty;
        var inNames = false;
        var inVersions = false;

        foreach (var raw in lines) {
            var line = raw.TrimEnd('\r');

            if (DefinitionKindLine.IsMatch(line)) {
                isDefinition = true;
            }

            if (line.Length > 0 && char.IsLetter(line[0])) {
                section = line[..line.IndexOf(':')];
                inNames = false;
                inVersions = false;

                // The flow-style spelling: `spec: {"group":…}`, one JSON value on the line.
                if (section == "spec" && line.Length > 5 && line[5..].TrimStart().StartsWith('{')) {
                    try {
                        using var json = JsonDocument.Parse(line[5..].Trim());
                        var spec = json.RootElement;
                        group = spec.TryGetProperty("group", out var g) ? g.GetString() ?? string.Empty : string.Empty;

                        if (spec.TryGetProperty("names", out var names)) {
                            kind = names.TryGetProperty("kind", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                            plural = names.TryGetProperty("plural", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                        }

                        if (spec.TryGetProperty("versions", out var list)) {
                            versions.AddRange(list.EnumerateArray()
                                .Where(v => !v.TryGetProperty("served", out var served) || served.GetBoolean())
                                .Select(v => v.GetProperty("name").GetString() ?? string.Empty));
                        }
                    } catch (JsonException) {
                        return null;
                    }
                }

                continue;
            }

            if (section != "spec") {
                continue;
            }

            if (line.StartsWith("  group:", StringComparison.Ordinal)) {
                group = Unquote(line["  group:".Length..].Trim());
            } else if (line.StartsWith("  names:", StringComparison.Ordinal)) {
                inNames = true;
                inVersions = false;
            } else if (line.StartsWith("  versions:", StringComparison.Ordinal)) {
                inVersions = true;
                inNames = false;
            } else if (line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && char.IsLetter(line[2])) {
                inNames = false;
                inVersions = false;
            } else if (inNames && line.StartsWith("    kind:", StringComparison.Ordinal)) {
                kind = Unquote(line["    kind:".Length..].Trim());
            } else if (inNames && line.StartsWith("    plural:", StringComparison.Ordinal)) {
                plural = Unquote(line["    plural:".Length..].Trim());
            } else if (inVersions) {
                // `    name: v1` at the version entry's own indent, and not the `name:` of an
                // additionalPrinterColumns entry, which sits deeper.
                var match = VersionNameLine.Match(line);

                if (match.Success) {
                    versions.Add(match.Groups["name"].Value);
                }
            }
        }

        return isDefinition && group.Length > 0 && kind.Length > 0 && plural.Length > 0 && versions.Count > 0
            ? new(relative, component, group, kind, plural, versions)
            : null;
    }

    /// <summary><c>kind: CustomResourceDefinition</c>, quoted or not.</summary>
    static readonly Regex DefinitionKindLine = new(
        @"^kind:\s*""?CustomResourceDefinition""?\s*$",
        RegexOptions.Compiled
    );

    /// <summary>
    ///     A version entry's <c>name:</c> — at indent four or six, or after the entry's dash — whose
    ///     value is spelled like a version, which is what separates it from a printer column's name.
    /// </summary>
    static readonly Regex VersionNameLine = new(
        @"^ {2,6}(?:- )?name:\s*(?<name>v[0-9]+(?:(?:alpha|beta)[0-9]+)?)\s*$",
        RegexOptions.Compiled
    );

    /// <summary>
    ///     Every <c>apiVersion</c> + <c>kind</c> pair a managed template declares, with the templates
    ///     that declare it — <see cref="ReadRenderedApiGroups" /> with the kind kept.
    /// </summary>
    /// <remarks>
    ///     ⚠ The <c>kind:</c> taken is the first at the same indent or shallower after an
    ///     <c>apiVersion:</c> line, which is the shape every template in the tree uses; a
    ///     <c>kind:</c> inside a spec is indented deeper and skipped. The same rule
    ///     <c>charts/bundle/crds.sh</c> § <c>rendered_kinds</c> applies, so the two agree on what a
    ///     chart renders.
    /// </remarks>
    List<(string ApiVersion, string Kind, List<string> Sources)> ReadRenderedKinds() {
        var rendered = new Dictionary<(string, string), List<string>>();

        if (!ManagedChartsDirectory.DirectoryExists()) {
            return [];
        }

        foreach (var chart in ManagedChartsDirectory.GlobDirectories("*").OrderBy(x => x.Name, StringComparer.Ordinal)) {
            var templates = chart / "templates";

            if (!templates.DirectoryExists()) {
                continue;
            }

            foreach (var template in templates.GlobFiles("**/*.yaml", "**/*.yml", "**/*.tpl")
                         .OrderBy(x => x.ToString(), StringComparer.Ordinal)) {
                string? apiVersion = null;
                var indent = 0;

                foreach (var line in template.ReadAllLines()) {
                    var version = RenderedApiVersion.Match(line);

                    if (version.Success) {
                        var value = version.Groups["value"].Value;
                        apiVersion = value.Contains("{{", StringComparison.Ordinal) || !value.Contains('/') ? null : value;
                        indent = line.Length - line.TrimStart().Length;

                        continue;
                    }

                    if (apiVersion is null) {
                        continue;
                    }

                    var kind = RenderedKind.Match(line);

                    if (!kind.Success || line.Length - line.TrimStart().Length > indent) {
                        continue;
                    }

                    var group = apiVersion[..apiVersion.LastIndexOf('/')];

                    if (!BuiltInApiGroups.Contains(group)) {
                        if (!rendered.TryGetValue((apiVersion, kind.Groups["value"].Value), out var sources)) {
                            rendered[(apiVersion, kind.Groups["value"].Value)] = sources = [];
                        }

                        var source = RootDirectory.GetRelativePathTo(template).ToString().Replace('\\', '/');

                        if (!sources.Contains(source, StringComparer.Ordinal)) {
                            sources.Add(source);
                        }
                    }

                    apiVersion = null;
                }
            }
        }

        return rendered
            .OrderBy(x => x.Key.Item1, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Item2, StringComparer.Ordinal)
            .Select(x => (x.Key.Item1, x.Key.Item2, x.Value))
            .ToList();
    }

    /// <summary>A <c>kind:</c> line at any indent, with its value.</summary>
    static readonly Regex RenderedKind = new(
        @"^\s*-?\s*kind:\s*(?<value>[^\s#{]+)\s*$",
        RegexOptions.Compiled
    );

    // ── The online row ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every committed definition still matches the release its component pins, byte for byte,
    ///     as <c>charts/bundle/crds.sh</c> reports it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>○ rather than ✔ whenever the comparison did not happen</b>, and the detail says
    ///         why: no Git bash (Windows' <c>PATH</c> finds the WSL launcher first, so the bash is
    ///         found beside <c>git.exe</c> as <c>BundleInstaller.Bash</c> finds it), no <c>helm</c>,
    ///         no network (the script exits 3 when a release cannot be fetched), or a run that did
    ///         not finish inside its budget. A row that said ✔ on an offline runner would be a copy
    ///         nobody compared wearing a tick — the Python and Go rows' rule, one artefact over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exit 1 is a failure with the script's own lines as the violations.</b> Each names
    ///         the file and what happened to it — DIFFERS, MISSING, STALE, ABSENT — and what to run.
    ///         The gate adds nothing, because the script is the one reader of those bytes and a
    ///         paraphrase here would be a second one.
    ///     </para>
    /// </remarks>
    GateOutcome DefinitionsGate() {
        const string Gate = "Definitions";
        var committed = ReadCommittedDefinitions(out _).Count;

        if (committed == 0) {
            return GateOutcome.From(Gate, 0, "definition(s) under charts/bundle/*/crds/, so there was nothing to compare with a release", []);
        }

        if (!DefinitionsScript.FileExists()) {
            return GateOutcome.From(Gate, 0, $"of {committed} definition(s) compared — {RootDirectory.GetRelativePathTo(DefinitionsScript)} is missing", []);
        }

        var bash = GitBash(out var absent);

        if (bash is null) {
            return GateOutcome.From(Gate, 0, $"of {committed} definition(s) compared — {absent}, so crds.sh could not run. Install Git for Windows or bash and run again", []);
        }

        if (GeneratedPackageSurface.Resolve("helm", out var noHelm) is null) {
            return GateOutcome.From(Gate, 0, $"of {committed} definition(s) compared — {noHelm}, and seven components render their definitions through `helm template`. Install Helm and run again", []);
        }

        var run = RunScript(bash, DefinitionsScript, TimeSpan.FromMinutes(5), out var timedOut);

        if (timedOut) {
            return GateOutcome.From(Gate, 0, $"of {committed} definition(s) compared — crds.sh did not finish within 5 minutes, so the releases were not compared", []);
        }

        var compared = run.Output.Count(x => x.EndsWith(" matches", StringComparison.Ordinal));

        if (run.ExitCode == 3) {
            var why = run.Output.FirstOrDefault(x => x.Contains("could not be fetched", StringComparison.Ordinal)) ?? "a release could not be fetched";

            return GateOutcome.From(Gate, 0, $"of {committed} definition(s) compared — {why.Replace("crds.sh: ", string.Empty)}. Offline, or the registry is down; the comparison was not made", []);
        }

        var violations = run.ExitCode == 0
            ? []
            : run.Output
                .Where(x => x.StartsWith("crds.sh: ", StringComparison.Ordinal)
                    && (x.Contains(" DIFFERS", StringComparison.Ordinal)
                        || x.Contains(" MISSING", StringComparison.Ordinal)
                        || x.Contains(" STALE", StringComparison.Ordinal)
                        || x.Contains(" ABSENT", StringComparison.Ordinal)))
                .Select(x => x.Replace("crds.sh: ", string.Empty))
                .ToList();

        if (run.ExitCode != 0 && violations.Count == 0) {
            violations.Add(
                $"charts/bundle/crds.sh exited {run.ExitCode.ToString(CultureInfo.InvariantCulture)} and named no file: "
                + string.Join(" | ", run.Output.TakeLast(5))
            );
        }

        return GateOutcome.From(
            Gate,
            compared,
            $"definition(s) under charts/bundle/*/crds/ fetched from the release each component pins and compared byte for byte by charts/bundle/crds.sh",
            violations
        );
    }

    /// <summary>
    ///     Git for Windows' bash, found beside <c>git.exe</c> on <c>PATH</c>; <c>bash</c> elsewhere.
    /// </summary>
    /// <param name="absent">Why none was found, when none was.</param>
    /// <remarks>
    ///     ⚠ The same rule <c>BundleInstaller.Bash</c> follows, and for the reason its remarks give:
    ///     on Windows a <c>PATH</c> walk finds <c>C:\WINDOWS\system32\bash.exe</c>, the WSL launcher,
    ///     which runs a Linux distribution that has neither this checkout's helm nor its paths.
    /// </remarks>
    static string? GitBash(out string absent) {
        absent = string.Empty;
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (!OperatingSystem.IsWindows()) {
            if (directories.Any(directory => File.Exists(Path.Combine(directory, "bash")))) {
                return "bash";
            }

            absent = "`bash` is not on PATH";

            return null;
        }

        foreach (var directory in directories) {
            if (!File.Exists(Path.Combine(directory, "git.exe"))) {
                continue;
            }

            var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            var bash = root is null ? null : Path.Combine(root, "bin", "bash.exe");

            if (bash is not null && File.Exists(bash)) {
                return bash;
            }
        }

        absent = "no Git for Windows bash was found beside a git.exe on PATH";

        return null;
    }

    /// <summary>Runs a bash script from the repository root and captures both streams.</summary>
    /// <param name="bash">The bash to run it with.</param>
    /// <param name="script">The script, handed over with forward slashes so MSYS resolves it.</param>
    /// <param name="budget">How long it may take before the run is abandoned.</param>
    /// <param name="timedOut">Whether the budget ran out.</param>
    static ToolRun RunScript(string bash, AbsolutePath script, TimeSpan budget, out bool timedOut) {
        var start = new System.Diagnostics.ProcessStartInfo(bash) {
            WorkingDirectory = RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        start.ArgumentList.Add(script.ToString().Replace('\\', '/'));

        using var process = new System.Diagnostics.Process { StartInfo = start };
        var output = new List<string>();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.Add(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.Add(e.Data); } } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        timedOut = !process.WaitForExit(budget);

        if (timedOut) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) {
                // It exited between the wait and the kill, which is the outcome the kill wanted.
            }

            return new(-1, output);
        }

        process.WaitForExit();

        return new(process.ExitCode, output);
    }
}
