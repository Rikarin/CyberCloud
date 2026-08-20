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
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

/// <summary>
///     Line coverage per assembly, out of a Cobertura report.
/// </summary>
/// <remarks>
///     ⚠ <b>Line coverage, not branch.</b> docs/plan/23 § Test layers says "coverage ≥ 70 %" without
///     saying of what, and the two answers differ by a lot on the same suite. Lines is the reading
///     that matches the number: a 70 % branch floor is a considerably stricter gate than most teams
///     mean by "70 % coverage", and choosing the stricter reading of an ambiguous requirement is how
///     a gate ends up disabled.
///     <para>
///         ⚠ <b>That second reason used to be "and branch coverage is not measured here anyway",
///         and it has stopped being true.</b> dotnet-coverage reported <c>branch-rate="1"</c> for
///         every assembly in this repository, so under it a branch floor would have gated on
///         nothing. coverlet reports real branch rates — measured on <c>CyberCloud.Identity</c>,
///         58.7 % branch against 64.4 % line — so a branch floor is now a decision somebody could
///         make rather than a thing that would silently pass. It is still not made here: the number
///         in docs/plan/23 is one number, and changing what it means is a change to that document
///         and not to this file.
///     </para>
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
        ///     <para>
        ///         ⚠ That guard cannot fire from a report, though, and the reason is worth knowing:
        ///         <see cref="Read" /> builds modules out of <c>&lt;line&gt;</c> elements, so an
        ///         assembly with no coverable line never reaches this record at all — it is simply
        ///         absent. <see cref="CoverableLines" /> is where the case is actually handled.
        ///     </para>
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
    ///         ⚠ <b>Several reports rather than one, merged here rather than by the collector's own
    ///         merge — <c>coverlet --merge-with</c>, or <c>dotnet-coverage merge</c> before
    ///         it.</b> Every suite produces its own report and most
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
    ///     <para>
    ///         ⚠ <b>The file a line belongs to comes from its nearest <c>&lt;class&gt;</c> ancestor,
    ///         not from its grandparent.</b> Cobertura lists most lines TWICE — once under
    ///         <c>&lt;class&gt;&lt;lines&gt;</c> and again under
    ///         <c>&lt;method&gt;&lt;lines&gt;</c> — and the grandparent of the second copy is the
    ///         <c>&lt;method&gt;</c>, which carries no <c>filename</c>. Every method-level line in
    ///         the report therefore keyed as (assembly, "", number), which did two wrong things at
    ///         once: it counted each line a second time, and it collapsed line 17 of one file into
    ///         line 17 of every other. Measured on <c>CyberCloud.Identity</c>: the old key reported
    ///         <b>1 837 of 2 775 lines, 66.2 %</b> for an assembly that is <b>1 392 of 2 163,
    ///         64.4 %</b> — a 1.8-point inflation of a 70 % floor, from a report that was correct.
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
                        line.Ancestors("class").FirstOrDefault()?.Attribute("filename")?.Value ?? string.Empty,
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
    ///     Counts the lines in a built assembly that a test could possibly cover, by reading the
    ///     sequence points out of its portable PDB.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the half that tells "fully covered, nothing to instrument" apart from
    ///         "nothing tests this".</b> Both produce the same thing in a Cobertura report — no
    ///         <c>&lt;package&gt;</c> element at all — and <see cref="Violations" /> has to call one
    ///         of them a pass and the other a failure.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sequence points, minus anything carrying
    ///         <c>[ExcludeFromCodeCoverage]</c>.</b> That subtraction is not a nicety, it is the
    ///         whole measurement. Measured on <c>CyberCloud.Providers.Sample.Application</c>, whose
    ///         only source file is a body-less class declaration: the PDB holds two sequence points,
    ///         both inside <c>Metadata_CyberCloudProvidersSampleApplication.ConfigureInner</c>, which
    ///         Orleans' source generator emits carrying <c>[GeneratedCode]</c>,
    ///         <c>[EditorBrowsable]</c> and <c>[ExcludeFromCodeCoverage]</c>. Count them and the
    ///         assembly looks coverable; discount them and it has nothing to cover — which is the
    ///         answer both collectors reach on their own. <c>dotnet-coverage instrument</c> said so
    ///         out loud, as <c>Module was not instrumented. Reason: optimized_or_instrumented</c>;
    ///         coverlet, which honours <c>[ExcludeFromCodeCoverage]</c> by the same rule this method
    ///         applies, simply emits no line for any of them and the four assemblies are absent from
    ///         the report.
    ///     </para>
    ///     <para>
    ///         ⚠ Reads the PDB rather than asking a collector to instrument the assembly and reading
    ///         what it says, which would also answer the question. Two reasons, and the first is why
    ///         this survived the change of collector unaltered: an answer that comes from parsing a
    ///         tool's stdout is an answer that changes when the tool changes its wording, and this
    ///         one is a count in a compiled artefact that no tool is involved in. The second is the
    ///         specific trap in the old route — <c>dotnet-coverage instrument</c> exits <b>0</b>
    ///         whether it instrumented or refused, and its refusal reason,
    ///         <c>optimized_or_instrumented</c>, also fires for an assembly compiled with
    ///         optimizations on. An excuse that widens whenever somebody passes
    ///         <c>--configuration Release</c> is a floor that stops gating without saying so.
    ///     </para>
    /// </remarks>
    /// <param name="assemblyPath">The built assembly. Its <c>.pdb</c> is expected beside it.</param>
    /// <returns>
    ///     The number of coverable lines, or <see langword="null" /> when the assembly or its PDB
    ///     could not be read. ⚠ <see langword="null" /> means "not known", and callers must not read
    ///     it as zero: "we could not tell" has to stay distinguishable from "there is nothing here".
    /// </returns>
    public static int? CoverableLines(string assemblyPath)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");

        if (!File.Exists(assemblyPath) || !File.Exists(pdbPath))
            return null;

        try
        {
            using var assemblyStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(assemblyStream);
            var metadata = peReader.GetMetadataReader();

            using var pdbStream = File.OpenRead(pdbPath);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var pdb = pdbProvider.GetMetadataReader();

            var coverable = 0;

            foreach (var handle in pdb.MethodDebugInformation)
            {
                var debugInformation = pdb.GetMethodDebugInformation(handle);

                if (debugInformation.SequencePointsBlob.IsNil)
                    continue;

                // Hidden sequence points (line 0xfeefee) mark compiler-emitted IL that maps to no
                // source line, so no report ever counts them either.
                var points = debugInformation.GetSequencePoints().Count(x => !x.IsHidden);

                if (points == 0)
                    continue;

                // MethodDebugInformation is a parallel table: row N describes MethodDef row N.
                var method = metadata.GetMethodDefinition(
                    (MethodDefinitionHandle)MetadataTokens.Handle(
                        TableIndex.MethodDef,
                        MetadataTokens.GetRowNumber(handle)));

                if (IsExcludedFromCoverage(metadata, method.GetCustomAttributes())
                    || IsExcludedFromCoverage(
                        metadata,
                        metadata.GetTypeDefinition(method.GetDeclaringType()).GetCustomAttributes()))
                {
                    continue;
                }

                coverable += points;
            }

            return coverable;
        }
        // Both answer "not known", which the caller turns into a violation with a message rather
        // than into a pass. An unreadable assembly is a worse thing to crash the floor over than to
        // report, and it stays reported either way.
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Whether any of these attributes is <c>[ExcludeFromCodeCoverage]</c>.</summary>
    /// <remarks>
    ///     Matches on the unqualified type name from the <c>TypeRef</c> table. The namespace is not
    ///     checked because there is one attribute with this name in the framework and a same-named
    ///     one somebody wrote deliberately means the same thing.
    /// </remarks>
    static bool IsExcludedFromCoverage(MetadataReader metadata, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);

            // A MemberReference constructor means the attribute type lives in another assembly,
            // which is true of every framework attribute. An attribute declared in this assembly
            // arrives as a MethodDefinition instead, and none of those is this one.
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;

            var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;

            if (parent.Kind != HandleKind.TypeReference)
                continue;

            if (metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name)
                is "ExcludeFromCodeCoverageAttribute")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every assembly name the report mentions.</summary>
    /// <remarks>
    ///     ⚠ Empty means the profiler never loaded, <b>not</b> that nothing is covered — see
    ///     Build.Test.cs § EnforceCoverageFloor, which refuses to enforce a floor against it.
    /// </remarks>
    public IReadOnlySet<string> MentionedAssemblies =>
        modules.Keys.ToHashSet(StringComparer.Ordinal);

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
    /// <param name="nothingToCover">
    ///     The projects whose assemblies hold no coverable line at all, from
    ///     <see cref="CoverableLines" />. ⚠ These are the <em>only</em> projects allowed to be
    ///     missing from the report — every other absence is a project no test loaded. Pass an empty
    ///     set to make every absence a violation; never pass a project here on the strength of a
    ///     name or a folder, because this set is the one place the floor can be switched off.
    /// </param>
    public IReadOnlyList<string> Violations(
        IEnumerable<string> projects,
        double floor,
        IReadOnlySet<string> nothingToCover)
    {
        var violations = new List<string>();

        foreach (var project in projects.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!modules.TryGetValue(project, out var module))
            {
                // Fully covered by definition: there is no line here for a test to reach. Counting
                // it as 0 % would fail a project for containing no executable code, which the same
                // reasoning as Module.Rate above says no amount of testing could fix.
                if (nothingToCover.Contains(project))
                    continue;

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
