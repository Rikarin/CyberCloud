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
    ///     <para>
    ///         ⚠ <b>And a <c>filename</c> is relative to that report's own
    ///         <c>&lt;sources&gt;</c> root, which is not the same root in every report.</b> coverlet
    ///         emits the deepest directory common to the files it instrumented, so a suite that
    ///         touched only <c>src/</c> projects writes <c>…/src/</c> and one that also touched a
    ///         host writes the repository root. Measured over the 71 reports of one run: <b>56</b>
    ///         said the repository root and <b>15</b> said <c>src/</c>. Keying on the raw string
    ///         therefore split one file into two — <c>src/CyberCloud.Communication/Providers/
    ///         ChannelProviders.cs</c> and <c>CyberCloud.Communication/Providers/
    ///         ChannelProviders.cs</c> counted as 382 lines rather than 191, and the two hit sets
    ///         were never unioned, so lines covered by one suite read as uncovered because another
    ///         suite spelled the path differently. The key is the resolved absolute path.
    ///     </para>
    /// </remarks>
    public static CoverageReport Read(params string[] paths)
    {
        var hits = new Dictionary<(string Assembly, string File, string Line), int>();

        foreach (var path in paths)
        {
            var root = XDocument.Load(path).Root
                       ?? throw new FormatException($"{path} is empty.");

            var sources = root.Element("sources")?.Elements("source").Select(x => x.Value).ToList() ?? [];

            // ⚠ Refused rather than guessed. With two roots a relative filename could belong to
            // either, and picking one would merge some files correctly and split others — which is
            // the failure this whole paragraph exists to remove, arriving silently a second time.
            // coverlet writes exactly one; a report with more came from something else.
            if (sources.Count > 1)
            {
                throw new FormatException(
                    $"{path} declares {sources.Count} <source> roots, so a <class filename=\"…\"> in "
                    + "it cannot be resolved to one path. Every line would key on a string that means "
                    + "different files in different reports, and the merge below would count some "
                    + "lines twice and union none of them. CoverageReport.cs § Read.");
            }

            var source = sources.Count == 1 ? sources[0] : string.Empty;

            foreach (var package in root.Descendants("package"))
            {
                var assembly = package.Attribute("name")?.Value;

                if (string.IsNullOrEmpty(assembly))
                    continue;

                foreach (var line in package.Descendants("line"))
                {
                    var file = line.Ancestors("class").FirstOrDefault()?.Attribute("filename")?.Value;

                    var key = (
                        assembly,
                        Resolve(source, file),
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
    ///     A <c>&lt;class filename&gt;</c> resolved against its report's <c>&lt;sources&gt;</c> root.
    /// </summary>
    /// <remarks>
    ///     ⚠ Normalised with <see cref="Path.GetFullPath(string)" /> rather than concatenated, so a
    ///     root ending in a separator and one that does not produce the same key — otherwise the
    ///     split this method exists to close would come back the moment a tool stopped writing the
    ///     trailing slash.
    /// </remarks>
    static string Resolve(string source, string? file)
    {
        if (string.IsNullOrEmpty(file))
            return string.Empty;

        if (string.IsNullOrEmpty(source))
            return file.Replace('\\', '/');

        try
        {
            return Path.GetFullPath(Path.Combine(source, file)).Replace('\\', '/');
        }
        // A filename or root this platform will not accept as a path. Falling back to the raw
        // string keeps the two spellings distinct, which is the pre-existing behaviour and is
        // visible as an inflated line count rather than as a wrong pass.
        catch (ArgumentException)
        {
            return file.Replace('\\', '/');
        }
        catch (PathTooLongException)
        {
            return file.Replace('\\', '/');
        }
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

    /// <summary>A pinned rate from the baseline file, and the line it is written on.</summary>
    /// <param name="Rate">The measured fraction, as recorded.</param>
    /// <param name="Line">The 1-based line, so a row can be reported by number.</param>
    public sealed record Pin(double Rate, int Line);

    /// <summary>
    ///     How far a measured rate may move from its pin before the pin is wrong.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Half a percentage point, and it is a noise band rather than a budget.</b> Exact
    ///         pinning is right for <c>actions-without-handlers.txt</c>, whose rows are a closed set
    ///         of names, and wrong here: a rate is a ratio over every line in the project, so
    ///         extracting a method or renaming a file moves it by hundredths and a build that went
    ///         red for that would be a build people learn to re-pin without reading.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The band cannot be spent, because it is symmetric.</b> A measurement more than
    ///         this far <em>below</em> the pin fails as a drop; a measurement more than this far
    ///         <em>above</em> it also fails, demanding the pin be raised. So the pin never moves down
    ///         and drift cannot accumulate a tenth at a time: whatever the project reaches becomes the
    ///         new floor under it as soon as the improvement is real. That symmetry is the whole
    ///         reason a tolerance is safe here — without it, twenty commits could walk a project ten
    ///         points down inside the rules.
    ///     </para>
    ///     <para>
    ///         Why this number: on the smallest project the file could plausibly list — 255 coverable
    ///         lines — half a point is 1.3 lines, so the pin is effectively exact and a small project
    ///         has nowhere to hide. On a 3 000-line project it is about 15 lines, which is a refactor
    ///         and not a policy change. The band therefore tightens exactly where a percentage is
    ///         least forgiving, which is the behaviour a fixed line count would not have.
    ///     </para>
    /// </remarks>
    public const double PinTolerance = 0.005;

    /// <summary>
    ///     One line per project below the floor, plus one per project the report never mentions, plus
    ///     one per row of the baseline file that no longer says what is true.
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
    /// <param name="baseline">
    ///     The reviewed rows of <c>coverage-below-floor.txt</c>, by project. ⚠ This is the <b>only</b>
    ///     thing that excuses a project from the floor, and it excuses it from the floor alone — a
    ///     listed project is still held to its own pinned rate, in both directions, by the rules
    ///     below.
    /// </param>
    /// <param name="baselineFile">The file's name, for every message that asks a reader to edit it.</param>
    /// <remarks>
    ///     ⚠ <b>Four rules, and three of them exist to keep the baseline from becoming a permission
    ///     slip.</b> The model is <c>actions-without-handlers.txt</c>, whose gate fails just as loudly
    ///     when a row outlives its debt as when a new gap appears unlisted. A debt list nobody is
    ///     forced to prune is a list in which no reader can tell the live rows from the dead ones,
    ///     and at that point it stops being evidence and becomes an exemption list — which is the
    ///     thing the floor's own failure message says never to build.
    /// </remarks>
    public IReadOnlyList<string> Violations(
        IEnumerable<string> projects,
        double floor,
        IReadOnlySet<string> nothingToCover,
        IReadOnlyDictionary<string, Pin> baseline,
        string baselineFile)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var violations = new List<string>();
        var unmatched = new HashSet<string>(baseline.Keys, StringComparer.Ordinal);

        foreach (var project in projects.OrderBy(x => x, StringComparer.Ordinal))
        {
            var listed = baseline.TryGetValue(project, out var pin) ? pin : null;
            unmatched.Remove(project);

            if (!modules.TryGetValue(project, out var module))
            {
                // Fully covered by definition: there is no line here for a test to reach. Counting
                // it as 0 % would fail a project for containing no executable code, which the same
                // reasoning as Module.Rate above says no amount of testing could fix.
                if (nothingToCover.Contains(project))
                {
                    if (listed is not null)
                    {
                        violations.Add(
                            $"{baselineFile} line {listed.Line} pins {project} at {listed.Rate:P1} and "
                            + "that project has no coverable line at all, so the floor never applied "
                            + "to it. Delete the row — a pin on a project that cannot be measured is "
                            + "a number nobody can check");
                    }

                    continue;
                }

                // No test loaded it, which is 0 %. A row pinning it at 0.0 is a legitimate thing to
                // write, so this falls through to the pin rules rather than short-circuiting.
                violations.AddRange(Judge(project, 0, 0, 0, floor, listed, baselineFile, absent: true));

                continue;
            }

            violations.AddRange(
                Judge(project, module.Rate, module.Covered, module.Coverable, floor, listed, baselineFile, absent: false));
        }

        // ⚠ The direction that keeps the file honest, and the one actions-without-handlers.txt's
        // gate calls out by name. A row naming a project that is not a shipping project any more is
        // standing permission for a gap that may not exist.
        foreach (var stale in unmatched.OrderBy(x => x, StringComparer.Ordinal))
        {
            violations.Add(
                $"{baselineFile} line {baseline[stale].Line} pins '{stale}' and no shipping project "
                + "has that name. Either it was renamed and the row did not follow, or it is gone and "
                + "so is the reason for the row");
        }

        return violations;
    }

    /// <summary>The four rules, for one project.</summary>
    static IEnumerable<string> Judge(
        string project,
        double rate,
        int covered,
        int coverable,
        double floor,
        Pin? listed,
        string baselineFile,
        bool absent)
    {
        var measured = absent
            ? "does not appear in the coverage report at all — no test loaded it, so its coverage is 0 %"
            : $"is at {rate:P1} ({covered} of {coverable} lines)";

        if (listed is null)
        {
            // Rule 1 — the floor, unchanged. An unlisted project is held to 70 %.
            if (rate >= floor)
                yield break;

            yield return
                $"{project} {measured}, below the {floor:P0} floor in docs/plan/23 § Test layers. "
                + $"The fix is a test. Adding a row to {baselineFile} is the other answer and it is a "
                + "review request rather than a build fix — read the header there for what it is "
                + "asking, and note that a row obliges you to a number as well as to a project";

            yield break;
        }

        // Rule 2 — a listed project that has reached the floor must lose its row, before anything
        // else is said about it. Reporting a pin drift on a project that now passes would be reading
        // out a number nobody should be looking at any more.
        if (rate >= floor)
        {
            yield return
                $"{project} is at {rate:P1} ({covered} of {coverable} lines), which meets the "
                + $"{floor:P0} floor — and {baselineFile} line {listed.Line} still lists it as debt. "
                + $"Delete that row and its comment. A debt list that outlives its debt is one nobody "
                + "can tell the live rows in, which is how it stops being evidence and starts being "
                + "an exemption list";

            yield break;
        }

        // Rule 3 — the ratchet. Below the pin by more than the noise band.
        if (rate < listed.Rate - PinTolerance)
        {
            yield return
                $"{project} {measured} and {baselineFile} line {listed.Line} pins it at "
                + $"{listed.Rate:P1}. It has DROPPED by {(listed.Rate - rate) * 100:F1} point(s), "
                + $"which is more than the {PinTolerance:P1} the pin tolerates. The pin only moves "
                + "up: cover what this change left uncovered, or — if the drop is real and intended "
                + $"— say so in the row's comment before touching the number. {baselineFile}";

            yield break;
        }

        // Rule 4 — the other half of the ratchet, and the reason the tolerance is not a budget.
        // Without this, a project could sit half a point below its pin forever and re-pin nothing,
        // and the band would be spendable a tenth at a time.
        if (rate > listed.Rate + PinTolerance)
        {
            yield return
                $"{project} is at {rate:P1} ({covered} of {coverable} lines) and {baselineFile} line "
                + $"{listed.Line} still pins it at {listed.Rate:P1}. It has IMPROVED by "
                + $"{(rate - listed.Rate) * 100:F1} point(s). Raise the pin to {rate * 100:F1} — the "
                + "pinned number is the measurement of record and a stale one lets the next change "
                + "give back ground this one won";
        }
    }
}
