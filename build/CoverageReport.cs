// The Cobertura half of docs/plan/23 § Test layers, row Unit: "Coverage ≥ 70 % per project".
//
// Standalone rather than a `partial class Build` member, and pure BCL rather than Nuke, for the same
// reason ArchitectureFacts.cs is: what a report says and what the build decides about it are two
// jobs, and only the second one needs a build. It also makes the floor checkable against a report
// without running a test — which matters because the run and the check happen on different machines
// often enough (docs/plan/23 § CI shape parallelises `pr.yml`) that they cannot be one step.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

/// <summary>
///     Line coverage per assembly, out of a Cobertura report.
/// </summary>
/// <remarks>
///     ⚠ <b>Line coverage, not branch.</b> docs/plan/23 § Test layers says "coverage ≥ 70 %" without
///     saying of what, and the two answers differ by a lot on the same suite. Lines is the reading
///     that matches the number: a 70 % branch floor is a considerably stricter gate than most teams
///     mean by "70 % coverage", and choosing the stricter reading of an ambiguous requirement is how
///     a gate ends up disabled. ⚠ dotnet-coverage reports <c>branch-rate="1"</c> for every assembly
///     in this repository, so a branch floor would additionally be measuring nothing.
/// </remarks>
sealed class CoverageReport
{
    /// <summary>Covered and coverable line counts for one assembly.</summary>
    /// <param name="Assembly">The assembly name, which is also the project name — Directory.Build.props § "Assembly and namespace naming".</param>
    /// <param name="Covered">Lines with at least one hit.</param>
    /// <param name="Coverable">Lines the compiler emitted sequence points for.</param>
    public sealed record Module(string Assembly, int Covered, int Coverable)
    {
        /// <summary>Covered ÷ coverable, or 1 for an assembly with no coverable lines at all.</summary>
        /// <remarks>
        ///     ⚠ An assembly with zero coverable lines is 100 %, not 0 %. It happens — a project that
        ///     is nothing but records and interfaces emits almost no sequence points — and calling
        ///     that 0 % would fail a project for containing no executable code, which no amount of
        ///     testing could fix.
        /// </remarks>
        public double Rate => Coverable == 0 ? 1 : (double)Covered / Coverable;
    }

    readonly Dictionary<string, Module> modules;

    CoverageReport(Dictionary<string, Module> modules) => this.modules = modules;

    /// <summary>Every assembly the report mentions, ordered by name.</summary>
    public IReadOnlyList<Module> Modules =>
        modules.Values.OrderBy(x => x.Assembly, StringComparer.Ordinal).ToList();

    /// <summary>
    ///     Reads one or more Cobertura reports as a single answer.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Several reports rather than one, merged here rather than by
    ///         <c>dotnet-coverage merge</c>.</b> Every suite produces its own report and most
    ///         assemblies appear in several of them — <c>CyberCloud.Core</c> is loaded by nearly
    ///         every suite in the tree. Summing per-report totals would count the same line once per
    ///         suite that loaded it, which inflates both halves of the ratio and, worse, makes the
    ///         "3 of 412 lines" in a failure message a number that corresponds to nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ So the unit of merge is the <b>line</b>, keyed by (assembly, file, number) and
    ///         reduced with <c>max</c>: a line covered by any suite is covered. This is also what
    ///         makes reading several reports and reading one merged report give the same answer.
    ///     </para>
    ///     <para>
    ///         ⚠ Counts <c>&lt;line&gt;</c> elements rather than reading the <c>line-rate</c>
    ///         attribute. The attribute is a rounded ratio, so a report with one covered line out of
    ///         four hundred and a report with none are both "0.00" to two places — and the failure
    ///         message wants to say "3 of 412 lines", which is the sentence that tells somebody
    ///         whether the project has no tests or a broken one.
    ///     </para>
    /// </remarks>
    public static CoverageReport Read(params string[] paths)
    {
        var hits = new Dictionary<(string Assembly, string File, string Line), int>();

        foreach (var path in paths)
        {
            var root = XDocument.Load(path).Root
                       ?? throw new FormatException($"{path} is empty.");

            foreach (var package in root.Descendants("package"))
            {
                var assembly = package.Attribute("name")?.Value;

                if (string.IsNullOrEmpty(assembly))
                    continue;

                foreach (var line in package.Descendants("line"))
                {
                    var key = (
                        assembly,
                        line.Parent?.Parent?.Attribute("filename")?.Value ?? string.Empty,
                        line.Attribute("number")?.Value ?? string.Empty);

                    var count = int.TryParse(
                        line.Attribute("hits")?.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed)
                        ? parsed
                        : 0;

                    hits[key] = hits.TryGetValue(key, out var existing) ? Math.Max(existing, count) : count;
                }
            }
        }

        var modules = hits
            .GroupBy(x => x.Key.Assembly, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Module(group.Key, group.Count(x => x.Value > 0), group.Count()),
                StringComparer.Ordinal);

        return new(modules);
    }

    /// <summary>
    ///     One line per project below the floor, plus one per project the report never mentions.
    /// </summary>
    /// <param name="projects">
    ///     The assemblies the floor applies to. ⚠ Supplied by the caller rather than taken from the
    ///     report, because the two disagree in exactly the case that matters: a project with no tests
    ///     at all produces no package element, and a floor that only looked at what the report
    ///     mentions would give it a pass.
    /// </param>
    /// <param name="floor">The fraction from docs/plan/23 § Test layers — 0.70.</param>
    public IReadOnlyList<string> Violations(IEnumerable<string> projects, double floor)
    {
        var violations = new List<string>();

        foreach (var project in projects.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!modules.TryGetValue(project, out var module))
            {
                violations.Add(
                    $"{project} does not appear in the coverage report at all — no test loaded it, so "
                    + "its coverage is 0 %. docs/plan/23 § Test layers puts the floor at "
                    + $"{floor:P0} per project");

                continue;
            }

            if (module.Rate >= floor)
                continue;

            violations.Add(
                $"{project} is at {module.Rate:P1} ({module.Covered} of {module.Coverable} lines), "
                + $"below the {floor:P0} floor in docs/plan/23 § Test layers");
        }

        return violations;
    }
}
