// Architecture — docs/plan/23 § The architecture gates, the enforcement half of
// docs/plan/00 § Non-negotiables.
//
// What this target reads is in ArchitectureFacts.cs; what it decides is here.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Serilog;

partial class Build
{
    /// <summary>
    ///     What a gate did, which is not the same question as whether it passed.
    ///     <para>
    ///         ⚠ <see cref="Vacuous" /> exists because a gate that inspected nothing prints the same
    ///         tick as a gate that inspected everything, and the two are not the same news. It was
    ///         added when half the rules in docs/plan/03 § Assembly graph rules were about assemblies
    ///         this repository did not contain yet; reporting those as "passed" would have been true
    ///         and misleading.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every assembly-graph rule has candidates now, and that is not the reassurance it
    ///         reads as.</b> Rule 2 spent its whole life green over three candidates in one provider
    ///         family — a set in which no violation was constructible — and the <c>const</c> hole
    ///         underneath it was found only once a second family existed. A candidate count answers
    ///         "did this rule look at anything", not "could this rule have failed"; the second
    ///         question is answered by constructing a violation, and only by that.
    ///     </para>
    /// </summary>
    enum GateStatus
    {
        /// <summary>Ran, inspected at least one candidate, found nothing.</summary>
        Enforced,

        /// <summary>
        ///     Ran and did not have a full set of candidates to inspect. Green, and worth nobody's
        ///     trust yet.
        ///     <para>
        ///         ⚠ Usually "zero candidates". <see cref="Build.WireCompatibilityGate" /> is the first
        ///         row to report it over a <i>partial</i> set — it compares every wire type in the tree
        ///         against the released baselines that exist, and docs/plan/23 asks for three. One
        ///         baseline is a real comparison and is not the property the row claims, so it is ○
        ///         with the shortfall spelled out in <see cref="GateOutcome.Detail" /> rather than ✔.
        ///     </para>
        /// </summary>
        Vacuous,

        /// <summary>Enforced by <c>src/CyberCloud.Analyzers</c> at compile time, not here.</summary>
        CompilerEnforced,

        /// <summary>Cannot be implemented yet, for a stated reason that is not "no time".</summary>
        Blocked,

        /// <summary>Ran and found violations. They are in <see cref="GateOutcome.Violations" />.</summary>
        Failed,
    }

    /// <summary>One row of the target's report.</summary>
    /// <param name="Gate">The gate name, matching docs/plan/23 § The architecture gates.</param>
    /// <param name="Status">See <see cref="GateStatus" />.</param>
    /// <param name="Detail">
    ///     What was inspected and how much of it, or why the gate could not run. Always says a
    ///     number when the gate ran, so "0 candidates" is visible in the log rather than inferable.
    /// </param>
    /// <param name="Violations">
    ///     One line per violation, each naming the offending type or file <i>and</i> the rule. The
    ///     failure is read at 2 a.m. by somebody who did not write the rule.
    /// </param>
    sealed record GateOutcome(string Gate, GateStatus Status, string Detail, IReadOnlyList<string> Violations)
    {
        public static GateOutcome From(string gate, int inspected, string what, List<string> violations)
            => new(
                gate,
                violations.Count > 0 ? GateStatus.Failed
                : inspected == 0 ? GateStatus.Vacuous
                : GateStatus.Enforced,
                $"{inspected} {what}",
                violations);

        public static GateOutcome Analyzer(string gate, string detail)
            => new(gate, GateStatus.CompilerEnforced, detail, []);

        public static GateOutcome Blocked(string gate, string why)
            => new(gate, GateStatus.Blocked, why, []);
    }

    /// <summary>
    ///     The ten gates from docs/plan/23 § The architecture gates, in the order the doc lists them,
    ///     plus one this build adds. Named here so the target's log output is the checklist, and so
    ///     adding a gate is a visible diff against the doc rather than a silent omission.
    ///     <para>
    ///         ⚠ <b>Three of the ten are enforced by the compiler instead, and this target must not
    ///         re-implement them.</b> <c>src/CyberCloud.Analyzers</c> ships CC1001–CC1007. It was four
    ///         until <b>Serializer discipline</b> stopped being a sentence and became a gate:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><b>Tenant keys</b> — the "no string literal containing '|' in a GetGrain argument" half is CC1004. The "every tenant-scoped grain interface is IGrainWithStringKey" half is cross-assembly and is enforced here.</item>
    ///         <item><b>Serializer discipline</b> — ⚠ <b>this row is no longer one of the four, and the sentence that used to sit here is why.</b> It read "the [Alias] on every [GenerateSerializer] half is CC1003; the [Id(n)] half is a reflection test, <c>CyberCloud.Core.Contracts.Tests.WireContractTests</c>" — a string constant that named a test covering <b>6 of the tree's 212</b> aliased wire types and checked neither that the test existed nor that it passed. <see cref="SerializerDisciplineGate" /> evaluates the row instead.</item>
    ///         <item><b>Secrets</b> — CC1005, in full.</item>
    ///         <item><b>No blocking</b> — CC1001 and CC1002. ⚠ Wider than the doc's wording: the doc says "in grain assemblies", the analyzers apply everywhere they are referenced, because a gateway that blocks is a stalled request even though it is not a stalled activation.</item>
    ///     </list>
    ///     <para>
    ///         A compile-time rule beats a build-target sweep for all three: it names the line, it runs
    ///         in the IDE, and it cannot be outrun by a file the sweep's glob missed. What it cannot
    ///         do is see across assemblies, which is why the rest stay here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The analyzer's reach is a project-file opt-in, and it is not universal.</b> A
    ///         project polices itself only if its <c>.csproj</c> carries the
    ///         <c>OutputItemType="Analyzer"</c> reference — and an opted-in project still runs nothing
    ///         if the rule's severity has been turned down. <see cref="AnalyzerCoverageGate" /> is the
    ///         gate that keeps the rows above honest on both counts; without it, "compiler-enforced" is
    ///         a claim about six projects wearing the clothes of a claim about the tree.
    ///     </para>
    /// </summary>
    static readonly (string Gate, string Checks)[] ArchitectureGates =
    [
        ("Assembly graph", "the seven rules in docs/plan/03"),
        ("Storage tier", "every [PersistentState] against durable-grains.txt; a Durable binding outside the list needs [DurableStateRationale]"),
        ("Tenant keys", "no string literal containing '|' in a GetGrain argument; every tenant-scoped grain interface is IGrainWithStringKey"),
        ("Serializer discipline", "every [GenerateSerializer] type has a stable [Alias]; [Id(n)] numbers never reused, checked against a committed manifest"),
        ("Wire compatibility", "round-trip every wire type through the last three released contract assemblies"),
        ("Secrets", "no [Id] member named *Password/*Secret/*Token/*Key outside CyberCloud.Vault"),
        ("No blocking", ".Result, .Wait(), async void banned in grain assemblies"),
        ("Generated surfaces", "OpenAPI/CLI/SDK/forms regenerate byte-identically from the registry"),
        ("OpenAPI compatibility", "published api-versions diffed; a breaking change fails"),
        ("Labels", "every reconciler's rendered output carries the seven cybercloud.io/* labels, asserted against real output"),
        ("Analyzer coverage", "every project under src/ references CyberCloud.Analyzers — not in docs/plan/23"),
        ("Plan citations", "no docs/plan/NN:LINE citation in a tracked file — docs/code-documentation-style.md § Citing the plan"),
        ("Code citations", "every <c>…Tests</c> and <c>…Tests.Method</c> in a tracked file names something this repository compiles — not in docs/plan/23"),
    ];

    // ── The assemblies the gates read ─────────────────────────────────────────────────────────

    /// <summary>
    ///     The solution's projects that ship, paired with the assembly <c>Compile</c> produced for
    ///     each.
    ///     <para>
    ///         ⚠ Test projects are excluded, and that is a decision rather than convenience.
    ///         docs/plan/03 § Assembly graph rules is about the shipped graph: a test assembly
    ///         referencing Orleans hosting, <c>KubernetesClient</c> or two providers at once is how
    ///         it tests them. <see cref="SuiteOwning" /> is the same classifier <c>Test</c> uses, so
    ///         the two targets cannot drift apart on what "a test project" means.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>CyberCloud.Analyzers</c> is <b>not</b> excluded even though it is the one
    ///         <c>netstandard2.0</c> project in the tree (docs/plan/02 § Platform baseline's
    ///         documented exception). Nothing here asserts a target framework, so it needs no
    ///         exemption — which is the argument for not writing a gate that asserts one.
    ///     </para>
    /// </summary>
    // List rather than IReadOnlyList: CA1859 is an error here and this is a private helper — the same
    // reason ShippingProjectFiles below returns a Dictionary.
    List<(AbsolutePath Project, AbsolutePath Assembly)> ShippingAssemblyPaths =>
        Solution.AllProjects
            .Select(x => (AbsolutePath)x.Path)
            .Where(project => SuiteOwning(project) is null)
            .OrderBy(project => project.NameWithoutExtension, StringComparer.Ordinal)
            .Select(project => (
                Project: project,
                Assembly: ArtifactsDirectory
                    / "bin"
                    / project.NameWithoutExtension
                    / Configuration.ToLowerInvariant()
                    / (project.NameWithoutExtension + ".dll")))
            .ToList();

    /// <summary>
    ///     Project file by assembly name. Safe because docs/plan/03 § src makes "folder name ==
    ///     assembly name == root namespace" a rule and <c>Directory.Build.props</c> § "Assembly and
    ///     namespace naming" implements it.
    /// </summary>
    // Dictionary, not IReadOnlyDictionary: CA1859 is an error here and this is a private helper.
    Dictionary<string, AbsolutePath> ShippingProjectFiles =>
        ShippingAssemblyPaths.ToDictionary(x => x.Project.NameWithoutExtension, x => x.Project, StringComparer.Ordinal);

    List<AssemblyFacts>? shippingAssemblies;

    /// <summary>
    ///     Every shipping assembly, read once. Fails rather than skipping when one is missing:
    ///     <c>Architecture</c> depends on <c>Compile</c>, so a missing assembly means the artifacts
    ///     directory was cleaned between the two, and quietly inspecting the remainder is how a gate
    ///     reports a green tick over half a tree.
    /// </summary>
    IReadOnlyList<AssemblyFacts> ShippingAssemblies
    {
        get
        {
            if (shippingAssemblies is not null)
                return shippingAssemblies;

            var missing = ShippingAssemblyPaths.Where(x => !x.Assembly.FileExists()).ToList();

            Assert.Empty(
                missing.Select(x => x.Project.NameWithoutExtension).ToList(),
                $"{missing.Count} shipping project(s) have no built assembly under {ArtifactsDirectory.Name}/bin — "
                + $"{string.Join(", ", missing.Select(x => x.Project.NameWithoutExtension))}. Run ./build.sh Compile "
                + $"first, in the same configuration ({Configuration}).");

            shippingAssemblies = ShippingAssemblyPaths.Select(x => AssemblyFacts.Read(x.Assembly)).ToList();

            return shippingAssemblies;
        }
    }

    // ── The target ────────────────────────────────────────────────────────────────────────────

    void CheckArchitecture()
    {
        // Logged before the gates run, not after: the assembly-graph gate narrates its seven rules
        // as it goes, and those lines are unreadable above the header that says what they belong to.
        Log.Information(
            "Architecture: {Count} gates — the ten in docs/plan/23 § The architecture gates, plus "
            + "Analyzer coverage, Plan citations and Code citations, which that table does not list",
            ArchitectureGates.Length);

        var outcomes = new List<GateOutcome>
        {
            AssemblyGraphGate(),
            StorageTierGate(),
            TenantKeyGate(),
            SerializerDisciplineGate(),
            WireCompatibilityGate(),
            GateOutcome.Analyzer("Secrets", "CC1005, in full"),
            GateOutcome.Analyzer("No blocking", "CC1001 and CC1002, wider than the doc's 'grain assemblies'"),
            GeneratedSurfacesGate(),
            OpenApiCompatibilityGate(),
            LabelsGate(),
            AnalyzerCoverageGate(),
            PlanCitationGate(),
            CodeCitationGate(),
        };

        Report(outcomes);
    }

    /// <summary>
    ///     Logs one line per gate, then every violation, then fails if there were any.
    ///     <para>
    ///         All gates run before any of them fails the build. A first-failure exit would mean a
    ///         contributor fixes one rule, pushes, and discovers the second — the report is worth more
    ///         than the seconds saved.
    ///     </para>
    /// </summary>
    static void Report(List<GateOutcome> outcomes)
    {
        Assert.True(
            outcomes.Select(x => x.Gate).Distinct(StringComparer.Ordinal).Count() == outcomes.Count,
            "Two gates share a name — the report would be ambiguous.");

        var unreported = ArchitectureGates
            .Select(x => x.Gate)
            .Except(outcomes.Select(x => x.Gate), StringComparer.Ordinal)
            .ToList();

        Assert.Empty(
            unreported,
            $"{unreported.Count} gate(s) named in Build.Architecture.cs § ArchitectureGates produced no "
            + $"outcome: {string.Join(", ", unreported)}. The roster and the run must agree — "
            + "docs/plan/23 § The architecture gates.");

        foreach (var outcome in outcomes)
        {
            var marker = outcome.Status switch
            {
                GateStatus.Enforced => "✔",
                GateStatus.Vacuous => "○",
                GateStatus.CompilerEnforced => "⌨",
                GateStatus.Blocked => "▪",
                _ => "✘",
            };

            Log.Information(
                "  {Marker} {Gate,-22} {Status,-16} {Detail}",
                marker,
                outcome.Gate,
                outcome.Status,
                outcome.Detail);
        }

        var vacuous = outcomes.Where(x => x.Status == GateStatus.Vacuous).Select(x => x.Gate).ToList();

        if (vacuous.Count > 0)
        {
            // ⚠ Not "inspected zero candidates", which is what this said until Wire compatibility
            // became the first row to report ○ over a set it had partly inspected. That row reads
            // 212 wire types against 1 of the 3 released baselines its own rule names, so the old
            // wording was a warning that misdescribed the thing it was warning about — the same
            // defect, one level up, as a gate whose status line states a condition it never
            // evaluated. Each row's Detail says which of the two it is, in numbers.
            Log.Warning(
                "{Count} gate(s) did not have a full set of candidates to inspect and are green "
                + "because of what they did not find, not because the tree is clean: {Gates}. "
                + "○, not ✔ — each row's detail says how much it saw.",
                vacuous.Count,
                string.Join(", ", vacuous));
        }

        var failures = outcomes.Where(x => x.Status == GateStatus.Failed).ToList();

        if (failures.Count == 0)
            return;

        foreach (var failure in failures)
        {
            foreach (var violation in failure.Violations)
                Log.Error("{Gate}: {Violation}", failure.Gate, violation);
        }

        Assert.Fail(
            $"{failures.Sum(x => x.Violations.Count)} architecture violation(s) across "
            + $"{failures.Count} gate(s): {string.Join(", ", failures.Select(x => x.Gate))}. Listed above.");
    }

    // ── Gate: assembly graph — docs/plan/03 § Assembly graph rules ────────────────────────────

    /// <summary>
    ///     The seven rules, read off the compiled assemblies and — where metadata cannot see — off
    ///     the project files.
    ///     <para>
    ///         ⚠ <b>Rule 3 is the reason metadata is the primary instrument, and it is worth
    ///         understanding before changing it.</b> "No assembly above <c>CyberCloud.Kubernetes</c>
    ///         references <c>k8s.Models</c>" is a rule about <i>types</i>. docs/plan/02 § ADR-004
    ///         requires <c>UseKubeMembership()</c> and <c>UseKubernetesHosting()</c>, whose packages
    ///         both depend on <c>KubernetesClient</c>, so <c>dotnet list package
    ///         --include-transitive</c> on <c>CyberCloud.ServiceDefaults</c> reports
    ///         <c>KubernetesClient</c> and always will. A gate phrased over packages is therefore
    ///         <b>unsatisfiable</b>. The <c>AssemblyRef</c> table is the right instrument: Roslyn
    ///         writes a row only for an assembly a type was actually bound from, so it says "no code
    ///         here touches <c>k8s.*</c>" — which is the rule — while staying silent about the
    ///         restore closure, which is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is the reason rules 2, 4, 5 and 7 do not read metadata alone.</b>
    ///         <c>AssemblyRef</c> records <i>binding</i>, and binding is not the same as depending: a
    ///         <c>const</c> is inlined by the compiler, and so are <c>nameof</c>, a literal attribute
    ///         argument and — in most positions — the result of a <c>typeof</c> that is only compared.
    ///         A project could therefore take a <c>ProjectReference</c> on an assembly it is
    ///         forbidden to depend on, use nothing but consts from it, and leave no row for a gate to
    ///         find. That was verified rather than reasoned about, on a real cross-provider reference
    ///         — <c>CyberCloud.Providers.DBforPostgreSQL.Contracts.csproj</c> records the experiment.
    ///         The four rules that are about <i>our own</i> assemblies close it by reading the
    ///         declared <c>ProjectReference</c> set as well, through <see cref="InTreeEdges" />: for
    ///         an in-tree edge the reference itself is the violation, whether or not a type crossed
    ///         it. Rule 3 cannot do the same and must not try, for the reason above.
    ///     </para>
    ///     <para>
    ///         Every rule logs its candidate count, so a rule that inspected nothing says so rather
    ///         than leaving it to be inferred from a tick.
    ///     </para>
    /// </summary>
    GateOutcome AssemblyGraphGate()
    {
        var violations = new List<string>();
        var inspected = 0;

        void Rule(int number, string statement, int candidates, IEnumerable<string> found)
        {
            var list = found.ToList();

            inspected += candidates;
            violations.AddRange(list.Select(x => $"rule {number} — {x}. docs/plan/03 § Assembly graph rules: \"{statement}\""));

            Log.Information(
                "    rule {Number}: {Candidates} candidate(s){Note} — {Statement}",
                number,
                candidates,
                candidates == 0 ? ", VACUOUS" : list.Count > 0 ? $", {list.Count} violation(s)" : string.Empty,
                statement);
        }

        // Rule 1. Asserted in its strongest available form — not "no Orleans hosting, no
        // KubernetesClient, no ABP" but "nothing at all". CyberCloud.Core has zero package
        // references today (its .csproj says so in a comment); pinning that exactly means the gate
        // fires on the first dependency of any kind, before anyone has to argue about which ones the
        // doc's three examples were meant to stand for.
        var core = ShippingAssemblies.SingleOrDefault(x => string.Equals(x.Name, "CyberCloud.Core", StringComparison.Ordinal));

        Rule(
            1,
            "CyberCloud.Core references no Orleans hosting, no KubernetesClient, no ABP application layer.",
            core is null ? 0 : 1,
            core is null ? [] : CoreDependencyViolations(core));

        // Rule 2. Read as cross-provider: CyberCloud.Providers.Compute referencing its own
        // .Contracts is a provider's internal seam, and the doc's own next sentence — "Cross-provider
        // references go through CyberCloud.ResourceManager by resource id" — is what the rule is for.
        //
        // ⚠ Over InTreeEdges rather than ReferencedAssemblies, and that is this rule's const hole
        // closing. It stayed open long enough to be written up in two places — src/Providers/README.md
        // and CyberCloud.Providers.DBforPostgreSQL.Contracts.csproj — because a cross-provider
        // ProjectReference whose only use is `SampleWidgets.TypePath` emits no AssemblyRef row at all.
        // A ProjectReference from one provider family to another is never legitimate, so the
        // declaration is the violation and there is nothing to weigh.
        var providers = ShippingAssemblies.Where(x => ProviderFamily(x.Name) is not null).ToList();

        Rule(
            2,
            "No Providers.* assembly references another Providers.* assembly — not even .Contracts.",
            providers.Count,
            providers.SelectMany(provider => InTreeEdges(provider)
                .Where(edge => ProviderFamily(edge.To) is { } family
                    && !string.Equals(family, ProviderFamily(provider.Name), StringComparison.Ordinal))
                .Select(edge =>
                    $"{provider.Name} {edge.How} {edge.To}; go through CyberCloud.ResourceManager by resource id")));

        // Rule 3. "Above CyberCloud.Kubernetes" is everything outside its own family.
        var aboveKubernetes = ShippingAssemblies
            .Where(x => !x.Name.StartsWith("CyberCloud.Kubernetes", StringComparison.Ordinal))
            .ToList();

        Rule(
            3,
            "No assembly above CyberCloud.Kubernetes references k8s.Models.",
            aboveKubernetes.Count,
            aboveKubernetes.SelectMany(assembly => assembly.ReferencedAssemblies
                .Where(IsKubernetesClient)
                .Select(reference =>
                    $"{assembly.Name} binds types from {reference}. This is about types, not packages — "
                    + "docs/plan/02 § ADR-004 legitimately puts KubernetesClient in the restore closure")));

        // ── Rule 4 ────────────────────────────────────────────────────────────────────────────
        //
        // ⚠ THIS ROW USED TO REPORT A WEAKER RULE UNDER ITS OWN NAME — "nothing outside src/Hosts",
        // rather than the doc's "except its OWN host" — because nothing in the tree said which host
        // owns which application layer. `IsHost` reads a directory name, and an application
        // assembly's name (CyberCloud.Providers.Sample.Application) names its provider, not its host.
        //
        // The mapping now exists: [assembly: OwningHost("…")] in CyberCloud.Core.Contracts, read by
        // ArchitectureFacts exactly as [DurableStateRationale] already was. That was the first of the
        // three options the old comment here listed, and it won for the reason it gave — it travels
        // with the assembly, survives a project move, and is reviewable in the diff that creates the
        // coupling, which a manifest matches and a ProjectReference-closure inference cannot (that
        // one derives the rule from the very edge the rule is about).
        //
        // ⚠ AN APPLICATION ASSEMBLY THAT DECLARES NO OWNING HOST IS NOT EXEMPT, IT IS SEALED. It has
        // no own host, so rule 4 permits nothing at all to reference it. That is the doc's sentence
        // read literally, and it is why this rule is not vacuous on a tree where no host has wired an
        // application layer up yet: the count below is application assemblies, and every one of them
        // is a candidate whether or not it names a host.
        //
        // ⚠ THE DECLARED HOST MUST ITSELF BE A HOST. Without that check the attribute would launder
        // any reference at all — [assembly: OwningHost("CyberCloud.Identity")] would make the
        // identity module its own application layer's host.
        var applications = ShippingAssemblies
            .Where(x => x.Name.EndsWith(".Application", StringComparison.Ordinal))
            .ToList();

        var owningHosts = applications.ToDictionary(
            x => x.Name,
            x => x.OwningHosts.Where(host => !string.IsNullOrWhiteSpace(host)).Select(host => host!).ToList(),
            StringComparer.Ordinal);

        Rule(
            4,
            "Nothing references a *.Application assembly except its own host, named by "
            + "[assembly: OwningHost(\"…\")] on the application assembly.",
            applications.Count,
            OwningHostDeclarationViolations(applications).Concat(
                ShippingAssemblies.SelectMany(assembly => InTreeEdges(assembly)
                    .Where(edge => owningHosts.TryGetValue(edge.To, out var owners)
                        && !owners.Contains(assembly.Name, StringComparer.Ordinal))
                    .Select(edge => owningHosts[edge.To].Count == 0
                        ? $"{assembly.Name} {edge.How} {edge.To}, which declares no [OwningHost] and so "
                        + "has no own host for anything to be. docs/plan/03 § Assembly graph rules, "
                        + "rule 4 permits nothing to reference it until it names one"
                        : $"{assembly.Name} {edge.How} {edge.To}, whose own host is "
                        + $"{string.Join(" or ", owningHosts[edge.To])}"))));

        // Rule 5. Over InTreeEdges for the same reason as rule 2: a gateway that took a
        // ProjectReference on a provider implementation and used only its consts would leave no
        // AssemblyRef row, and the reference is the thing the rule forbids.
        var gateways = ShippingAssemblies
            .Where(x => x.Name.StartsWith("CyberCloud.Gateway", StringComparison.Ordinal))
            .ToList();

        Rule(
            5,
            "The gateway references no provider implementation assembly, only .Contracts and .Application.",
            gateways.Count,
            gateways.SelectMany(gateway => InTreeEdges(gateway)
                .Where(edge => IsProviderImplementation(edge.To))
                .Select(edge => $"{gateway.Name} {edge.How} the implementation assembly {edge.To}")));

        // Rule 6. Not an assembly rule — portal/libs/api is TypeScript.
        var portalApi = RootDirectory / "portal" / "libs" / "api";

        var portalFiles = portalApi.DirectoryExists()
            ? portalApi.GlobFiles("**/*").Where(x => x.FileExists()).ToList()
            : [];

        Rule(
            6,
            "portal/libs/api has no hand-written files; the generator owns the directory.",
            portalFiles.Count,
            portalFiles
                .Where(file => !LooksGenerated(file))
                .Select(file =>
                    $"{RootDirectory.GetRelativePathTo(file)} carries no generator banner "
                    + "(\"DO NOT EDIT\", \"@generated\" or \"auto-generated\" in its first 2 KB)"));

        // Rule 7 — see ModuleLayeringViolations.
        var moduleEdges = ModuleEdges();

        Rule(
            7,
            $"Every edge between two modules is declared in {ModuleLayeringFile.Name}, and the "
            + "declaration is acyclic.",
            moduleEdges.Count,
            ModuleLayeringViolations(moduleEdges));

        return GateOutcome.From("Assembly graph", inspected, "rule candidate(s) across 7 rules", violations);
    }

    // ── The edges the rules are about ─────────────────────────────────────────────────────────

    /// <summary>
    ///     One edge from a shipping assembly to another shipping assembly, and how it was seen.
    /// </summary>
    /// <param name="To">The referenced assembly's simple name.</param>
    /// <param name="How">
    ///     Verb phrase for a violation message — <c>"binds types from"</c> when the
    ///     <c>AssemblyRef</c> table has a row, and the longer <c>ProjectReference</c> wording when it
    ///     does not. The distinction is worth printing: the second case is a dependency the compiler
    ///     erased, and somebody reading the failure will otherwise go looking for the <c>using</c>
    ///     that is not there.
    /// </param>
    /// <param name="Bound">Whether the <c>AssemblyRef</c> table has a row for it.</param>
    sealed record AssemblyEdge(string To, string How, bool Bound);

    /// <summary>
    ///     Every edge from one shipping assembly to another: the union of what it binds and what its
    ///     project file declares.
    ///     <para>
    ///         ⚠ <b>Both halves are load-bearing and they catch different things.</b> The
    ///         <c>AssemblyRef</c> half sees a type bound through a <i>transitive</i>
    ///         <c>ProjectReference</c>, which the project file never mentions — SDK-style project
    ///         references flow, so a project can use a type from a project it does not name. The
    ///         project-file half sees a reference the compiler erased: a <c>const</c>, a
    ///         <c>nameof</c>, a literal attribute argument. Either half alone has a documented hole;
    ///         the union has neither.
    ///     </para>
    ///     <para>
    ///         ⚠ Restricted to assemblies this repository builds. A package reference is not an edge
    ///         any of these rules is about, and rule 3 — the one rule that <i>is</i> about a package
    ///         — deliberately does not come through here.
    ///     </para>
    /// </summary>
    IEnumerable<AssemblyEdge> InTreeEdges(AssemblyFacts assembly)
    {
        var bound = assembly.ReferencedAssemblies
            .Where(ShippingProjectFiles.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);

        var declared = DeclaredProjectReferences(ShippingProjectFiles[assembly.Name])
            .Where(ShippingProjectFiles.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);

        return bound.Union(declared, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(to => new AssemblyEdge(
                to,
                bound.Contains(to)
                    ? "binds types from"
                    : "declares a <ProjectReference> on, and binds nothing from,",
                bound.Contains(to)));
    }

    /// <summary>
    ///     The assembly name of every <c>ProjectReference</c> a project file declares, by the
    ///     "folder name == assembly name" rule of docs/plan/03 § src.
    ///     <para>
    ///         ⚠ Analyzer references are excluded. <c>CyberCloud.Analyzers</c> is referenced by every
    ///         project in the tree with <c>OutputItemType="Analyzer"
    ///         ReferenceOutputAssembly="false"</c> — it contributes no assembly to the graph, and
    ///         counting it would put an edge from everything to it in every rule below.
    ///     </para>
    /// </summary>
    static IEnumerable<string> DeclaredProjectReferences(AbsolutePath project)
        => XDocument.Load(project)
            .Descendants()
            .Where(x => string.Equals(x.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
            .Where(x => !string.Equals(x.Attribute("OutputItemType")?.Value, "Analyzer", StringComparison.Ordinal))
            .Select(x => x.Attribute("Include")?.Value)
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => Path.GetFileNameWithoutExtension(x!.Replace('\\', '/')));

    /// <summary>
    ///     Rule 1, both ways round: nothing in the <c>AssemblyRef</c> table that is not the shared
    ///     framework, and nothing in the project file that is a package.
    /// </summary>
    /// <remarks>
    ///     The project-file half is not redundant. A <c>PackageReference</c> whose types are unused
    ///     leaves no <c>AssemblyRef</c> row, so metadata alone would let a dependency sit in
    ///     <c>CyberCloud.Core</c> unnoticed until the first line of code used it — at which point the
    ///     gate fires on a commit that only added a call.
    /// </remarks>
    IEnumerable<string> CoreDependencyViolations(AssemblyFacts core)
    {
        foreach (var reference in core.ReferencedAssemblies.Where(x => !IsSharedFramework(x)))
            yield return $"{core.Name} binds types from {reference}";

        foreach (var package in DeclaredPackageReferences(ShippingProjectFiles[core.Name]))
            yield return $"{core.Name}.csproj declares <PackageReference Include=\"{package}\" />";
    }

    /// <summary>
    ///     The <c>Include</c> of every <c>PackageReference</c> in a project file.
    ///     <para>
    ///         Read as XML rather than through MSBuild evaluation on purpose: this asks what the file
    ///         <i>says</i>, and the answer a reviewer can check by opening it is the useful one.
    ///         <c>Directory.Packages.props</c> is a version pin, not a reference, and correctly does
    ///         not show up here.
    ///     </para>
    /// </summary>
    static IEnumerable<string> DeclaredPackageReferences(AbsolutePath project)
        => XDocument.Load(project)
            .Descendants()
            .Where(x => string.Equals(x.Name.LocalName, "PackageReference", StringComparison.Ordinal))
            .Select(x => x.Attribute("Include")?.Value)
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!);

    static bool IsSharedFramework(string assembly)
        => assembly.StartsWith("System.", StringComparison.Ordinal)
            || string.Equals(assembly, "System", StringComparison.Ordinal)
            || string.Equals(assembly, "netstandard", StringComparison.Ordinal)
            || string.Equals(assembly, "mscorlib", StringComparison.Ordinal);

    static bool IsKubernetesClient(string assembly)
        => assembly.StartsWith("k8s", StringComparison.OrdinalIgnoreCase)
            || assembly.Contains("KubernetesClient", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The provider a <c>CyberCloud.Providers.X[.Contracts|.Application]</c> assembly belongs to,
    ///     or <see langword="null" /> if it is not a provider assembly at all.
    /// </summary>
    static string? ProviderFamily(string assembly)
    {
        const string prefix = "CyberCloud.Providers.";

        if (!assembly.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var rest = assembly[prefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);

        return dot < 0 ? rest : rest[..dot];
    }

    static bool IsProviderImplementation(string assembly)
        => ProviderFamily(assembly) is not null
            && !assembly.EndsWith(".Contracts", StringComparison.Ordinal)
            && !assembly.EndsWith(".Application", StringComparison.Ordinal);

    /// <summary>
    ///     Whether an assembly is a host, by where its project lives. docs/plan/03 § src/Hosts is the
    ///     definition; a name test would miss <c>CyberCloud.AppHost</c>, which does not end in
    ///     <c>.Host</c>.
    /// </summary>
    bool IsHost(string assembly)
        => ShippingProjectFiles.TryGetValue(assembly, out var project)
            && string.Equals(project.Parent.Parent.Name, "Hosts", StringComparison.Ordinal);

    /// <summary>
    ///     Rule 4's other half: whether the declarations themselves are usable. An
    ///     <c>[OwningHost]</c> naming something that is not a host would otherwise launder any
    ///     reference at all into an allowed one, which is the failure mode that made the third
    ///     option in this rule's history — inferring ownership from the host's own references —
    ///     the wrong one.
    /// </summary>
    IEnumerable<string> OwningHostDeclarationViolations(IEnumerable<AssemblyFacts> applications)
    {
        foreach (var application in applications)
        {
            foreach (var host in application.OwningHosts)
            {
                if (string.IsNullOrWhiteSpace(host))
                {
                    yield return $"{application.Name} carries an [OwningHost] with no host name. The "
                        + "attribute names one host under src/Hosts — docs/plan/03 § Assembly graph rules, rule 4";

                    continue;
                }

                if (!IsHost(host))
                {
                    yield return $"{application.Name} declares [OwningHost(\"{host}\")] and {host} is not "
                        + "a project under src/Hosts. An application layer's own host is a host; naming "
                        + "anything else would make the attribute an exemption from rule 4 rather than an "
                        + "answer to it";
                }
            }
        }
    }

    static bool LooksGenerated(AbsolutePath file)
    {
        using var reader = new StreamReader(file);

        var head = new char[2048];
        var read = reader.ReadBlock(head, 0, head.Length);
        var text = new string(head, 0, read);

        return text.Contains("DO NOT EDIT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("@generated", StringComparison.OrdinalIgnoreCase)
            || text.Contains("auto-generated", StringComparison.OrdinalIgnoreCase);
    }

    // ── Rule 7: module layering — docs/plan/03 § Assembly graph rules ─────────────────────────

    /// <summary>
    ///     The reviewed list of edges between modules, at the repository root beside
    ///     <c>durable-grains.txt</c> and for the same reason: a change to it should land in a diff
    ///     nobody scrolls past.
    /// </summary>
    AbsolutePath ModuleLayeringFile => RootDirectory / "module-layering.txt";

    /// <summary>
    ///     The module an assembly belongs to, or <see langword="null" /> if it is out of scope for
    ///     rule 7.
    ///     <para>
    ///         A module is the assembly name truncated to its first two dotted segments —
    ///         <c>CyberCloud.Identity</c>, <c>CyberCloud.Identity.Contracts</c> and (were there one)
    ///         <c>CyberCloud.Identity.Application</c> are one module. That collapsing is the whole
    ///         point: the sibling coupling this rule exists to catch reaches across the seam rather
    ///         than through it. <c>CyberCloud.Communication</c> binding
    ///         <c>CyberCloud.Identity.Contracts</c> while <c>CyberCloud.Identity</c> binds
    ///         <c>CyberCloud.Communication.Contracts</c> is not a cycle between four assemblies; it
    ///         is a cycle between two modules, and only the module view can see it.
    ///     </para>
    ///     <para>
    ///         A provider's module is its family, so that a module reaching into a provider — and a
    ///         provider reaching into a module — are both edges this rule sees. Rule 2 stays the hard
    ///         rule for provider-to-provider, which no declaration may permit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>src/Hosts</c> and <c>cli/</c> are out of scope, deliberately.</b> A host is the
    ///         composition root — referencing many modules is its job, nothing references a host
    ///         except <c>CyberCloud.AppHost</c>, and so no host can be part of a cycle. A declaration
    ///         listing what each host composes would change on every wiring commit and prevent
    ///         nothing. What a host may reference is rules 4 and 5, not this one. (Hosts would also
    ///         collide under the naming above: <c>CyberCloud.Identity.Host</c>'s first two segments
    ///         are the identity module's.)
    ///     </para>
    /// </summary>
    string? ModuleOf(string assembly)
    {
        if (!ShippingProjectFiles.TryGetValue(assembly, out var project))
            return null;

        if (ProviderFamily(assembly) is { } family)
            return "CyberCloud.Providers." + family;

        if (!string.Equals(project.Parent.Parent.Name, "src", StringComparison.Ordinal))
            return null;

        var first = assembly.IndexOf('.', StringComparison.Ordinal);

        if (first < 0)
            return assembly;

        var second = assembly.IndexOf('.', first + 1);

        return second < 0 ? assembly : assembly[..second];
    }

    /// <summary>One edge between two modules, and the assembly-level edge that put it there.</summary>
    sealed record ModuleEdge(string From, string To, string Because);

    /// <summary>
    ///     Every edge between two different modules in the shipping graph, deduplicated: four
    ///     assembly references from one module into another are one thing to declare, not four.
    ///     One assembly edge is kept as the reason so a violation names something concrete to go and
    ///     look at, and a <i>bound</i> one is preferred — otherwise a module that both binds a
    ///     sibling's contracts and carries a spare <c>ProjectReference</c> on its implementation
    ///     would be reported as binding nothing, which is true of that one reference and false of
    ///     the edge.
    /// </summary>
    List<ModuleEdge> ModuleEdges()
    {
        var edges = new Dictionary<(string From, string To), (string Because, bool Bound)>();

        foreach (var assembly in ShippingAssemblies)
        {
            if (ModuleOf(assembly.Name) is not { } from)
                continue;

            foreach (var edge in InTreeEdges(assembly))
            {
                if (ModuleOf(edge.To) is not { } to || string.Equals(from, to, StringComparison.Ordinal))
                    continue;

                var because = ($"{assembly.Name} {edge.How} {edge.To}", edge.Bound);

                if (!edges.TryGetValue((from, to), out var existing) || (edge.Bound && !existing.Bound))
                    edges[(from, to)] = because;
            }
        }

        return edges
            .OrderBy(x => x.Key.From, StringComparer.Ordinal)
            .ThenBy(x => x.Key.To, StringComparer.Ordinal)
            .Select(x => new ModuleEdge(x.Key.From, x.Key.To, x.Value.Because))
            .ToList();
    }

    /// <summary>
    ///     Rule 7, in three directions, against <see cref="ModuleLayeringFile" />.
    ///     <para>
    ///         ⚠ <b>What this rule is, precisely, because it is easy to over-read.</b> Directions 1
    ///         and 2 make the file and the graph agree — they do not decide whether a coupling is a
    ///         good idea, they make sure it was <i>seen</i>. A new edge between two modules costs one
    ///         line in one reviewed file, which is exactly the cost <c>durable-grains.txt</c> puts on
    ///         a new durable grain. Direction 3 is the half nothing can talk its way past: the
    ///         declaration must be acyclic, so the line that would have made the coupling legal fails
    ///         the build instead when it closes a loop.
    ///     </para>
    ///     <para>
    ///         The alternative considered was direction 3 alone — a cycle rule over the real graph,
    ///         with no file to keep. It is cheaper and it would have caught the violation that
    ///         exposed this hole (<c>CyberCloud.Communication</c> binding
    ///         <c>CyberCloud.Identity.Contracts</c> closes a loop against
    ///         <c>CyberCloud.Identity</c> → <c>CyberCloud.Communication.Contracts</c>). It would not
    ///         have caught <c>CyberCloud.Communication</c> reaching into <c>CyberCloud.Metering</c>,
    ///         which is acyclic and which
    ///         <c>CyberCloud.Communication.csproj</c> spends two paragraphs forbidding — "metering
    ///         must not be able to make [the send path] slower or fail it". A rule that cannot see
    ///         the coupling that assembly's own author wrote down twice is the wrong rule.
    ///     </para>
    /// </summary>
    List<string> ModuleLayeringViolations(List<ModuleEdge> actual)
    {
        Assert.True(
            ModuleLayeringFile.FileExists(),
            $"{ModuleLayeringFile.Name} is missing. It is the reviewed list of edges between modules "
            + "— docs/plan/03 § Assembly graph rules, rule 7. Create it at the repository root, one "
            + "\"Module -> Module\" per line.");

        var modules = ShippingAssemblies
            .Select(x => ModuleOf(x.Name))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var declared = new List<(string From, string To)>();
        var violations = new List<string>();
        var lines = ModuleLayeringFile.ReadAllLines();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split("->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2)
            {
                violations.Add(
                    $"{ModuleLayeringFile.Name}:{i + 1} is not \"Module -> Module\" — \"{line}\"");

                continue;
            }

            var unknown = parts.Where(x => !modules.Contains(x)).ToList();

            foreach (var module in unknown)
            {
                violations.Add(
                    $"{ModuleLayeringFile.Name}:{i + 1} names {module}, which is not a module in this "
                    + "tree. A module is a shipping project directly under src/, or a provider family "
                    + "under src/Providers, named by its first two dotted segments");
            }

            // Not added to `declared`: an unknown name is already reported, and letting it through
            // would report the same line a second time as a stale edge.
            if (unknown.Count == 0)
                declared.Add((parts[0], parts[1]));
        }

        var declaredSet = declared.ToHashSet();

        // Direction 1: every edge in the graph is declared. This is the direction that would have
        // caught the sibling reference the gate used to let through.
        foreach (var edge in actual.Where(x => !declaredSet.Contains((x.From, x.To))))
        {
            violations.Add(
                $"{edge.From} references {edge.To} and {ModuleLayeringFile.Name} does not say it may "
                + $"({edge.Because}). Adding the line is a review request, not a build fix — "
                + "docs/plan/03 § Assembly graph rules, rule 7");
        }

        // Direction 2: every declared edge is real. A line for a coupling that no longer exists is
        // a permission nobody granted twice, sitting there for the next person to use.
        var actualSet = actual.Select(x => (x.From, x.To)).ToHashSet();

        foreach (var (from, to) in declared.Where(x => !actualSet.Contains(x)))
        {
            violations.Add(
                $"{ModuleLayeringFile.Name} declares {from} -> {to} and no assembly in {from} "
                + $"references one in {to}. Delete the line: a stale entry is standing permission for "
                + "a coupling nobody reviewed");
        }

        // Direction 3: the declaration is acyclic. Nothing may be written here that makes a cycle
        // legal — this is the half a reviewer cannot be talked past.
        violations.AddRange(DeclarationCycles(declared));

        return violations;
    }

    /// <summary>
    ///     Every cycle in the declared graph, as a path. Reported as a path rather than as "there is
    ///     a cycle" because a two-module cycle is obvious and a four-module one is not.
    /// </summary>
    static List<string> DeclarationCycles(List<(string From, string To)> declared)
    {
        var outgoing = declared
            .GroupBy(x => x.From, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(y => y.To).ToList(), StringComparer.Ordinal);

        // Once a module's own edges have been walked, every cycle running through it has been seen,
        // so a second arrival need not walk them again.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var cycles = new List<string>();

        foreach (var start in outgoing.Keys.OrderBy(x => x, StringComparer.Ordinal))
            Walk(start, []);

        return cycles;

        void Walk(string module, List<string> path)
        {
            if (path.Contains(module, StringComparer.Ordinal))
            {
                var loop = path[path.IndexOf(module)..];
                var key = string.Join(" -> ", loop.OrderBy(x => x, StringComparer.Ordinal));

                if (reported.Add(key))
                {
                    cycles.Add(
                        $"the declaration is cyclic: {string.Join(" -> ", loop)} -> {module}. Modules "
                        + "form a layering, not a mesh — an edge that closes a loop cannot be declared "
                        + "away, because a cycle is what makes two modules one");
                }

                return;
            }

            if (!visited.Add(module) || !outgoing.TryGetValue(module, out var next))
                return;

            foreach (var to in next.OrderBy(x => x, StringComparer.Ordinal))
                Walk(to, [.. path, module]);
        }
    }

    // ── Gate: storage tier — docs/plan/05 § Choosing a tier ───────────────────────────────────

    /// <summary>
    ///     The reviewed list of grain types whose state is durable. docs/plan/05 § Choosing a tier:
    ///     <i>"The list is reviewed like a schema migration, because that is what it is."</i>
    ///     <para>
    ///         ⚠ At the repository root rather than under <c>build/</c>. docs/plan/03 § Top level
    ///         does not place the file at all — that is a gap in the doc, not a decision recorded
    ///         there — and the root is where a change to it lands in a diff nobody scrolls past.
    ///     </para>
    /// </summary>
    AbsolutePath DurableGrainsFile => RootDirectory / "durable-grains.txt";

    /// <summary>
    ///     Every <c>[PersistentState]</c> in a shipping assembly, against
    ///     <c>durable-grains.txt</c> — docs/plan/05 § Choosing a tier, enforced in both directions.
    ///     <para>
    ///         Both directions matter and they catch different mistakes. A listed grain that no
    ///         longer binds <c>Durable</c> is state that silently became rebuildable — the loss is
    ///         discovered by a customer. An unlisted grain that binds <c>Durable</c> is a schema
    ///         change nobody reviewed. Only the second is what people picture when they hear
    ///         "storage-tier gate".
    ///     </para>
    ///     <para>
    ///         Test assemblies are out of scope, as they are for the assembly graph: several of them
    ///         legitimately bind <c>Durable</c> from a fixture grain
    ///         (<c>CyberCloud.ServiceDefaults.Tests.Storage.IDurableStateGrain</c> exists to prove
    ///         the tier is wired), and putting fixtures on a list reviewed like a schema migration
    ///         would teach reviewers to skim it.
    ///     </para>
    /// </summary>
    GateOutcome StorageTierGate()
    {
        Assert.True(
            DurableGrainsFile.FileExists(),
            $"{DurableGrainsFile.Name} is missing. It is the reviewed list of grain types whose state "
            + "is durable — docs/plan/05 § Choosing a tier. Create it at the repository root, one "
            + "fully-qualified grain type per line.");

        var listed = DurableGrainsFile.ReadAllLines()
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var bindings = ShippingAssemblies
            .SelectMany(assembly => assembly.PersistentStateBindings.Select(binding => (Assembly: assembly.Name, Binding: binding)))
            .ToList();

        var rationales = ShippingAssemblies
            .SelectMany(x => x.DurableStateRationales)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

        var violations = new List<string>();

        // Direction 1: everything on the list still binds Durable.
        foreach (var type in listed.OrderBy(x => x, StringComparer.Ordinal))
        {
            var forType = bindings.Where(x => string.Equals(x.Binding.DeclaringType, type, StringComparison.Ordinal)).ToList();

            if (forType.Any(x => string.Equals(x.Binding.Tier, DurableTier, StringComparison.Ordinal)))
                continue;

            violations.Add(forType.Count == 0
                ? $"{type} is listed in {DurableGrainsFile.Name} but no shipping assembly declares a grain "
                + "type of that name with any [PersistentState] — the list has gone stale, or the type was "
                + "renamed without renaming the entry"
                : $"{type} is listed in {DurableGrainsFile.Name} but binds no state to the Durable tier "
                + $"(it binds {string.Join(", ", forType.Select(x => $"\"{x.Binding.StateName}\" → {x.Binding.Tier ?? "the default provider"}"))}). "
                + "State on the list is state whose loss tolerance is zero — docs/plan/02 § ADR-003");
        }

        // Direction 2: everything binding Durable is either on the list or carries a rationale.
        foreach (var (assembly, binding) in bindings
            .Where(x => string.Equals(x.Binding.Tier, DurableTier, StringComparison.Ordinal))
            .OrderBy(x => x.Binding.DeclaringType, StringComparer.Ordinal))
        {
            if (listed.Contains(binding.DeclaringType))
                continue;

            if (!rationales.TryGetValue(binding.DeclaringType, out var reason))
            {
                violations.Add(
                    $"{binding.DeclaringType} ({assembly}) binds [PersistentState(\"{binding.StateName}\", "
                    + $"StorageTiers.Durable)] but is not in {DurableGrainsFile.Name} and carries no "
                    + "[DurableStateRationale]. Add it to the list — reviewed like a schema migration — or "
                    + "say in the attribute why it does not belong there. docs/plan/05 § Choosing a tier");

                continue;
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                violations.Add(
                    $"{binding.DeclaringType} ({assembly}) carries [DurableStateRationale] with an empty "
                    + "reason. The attribute is an argument, not a checkbox — docs/plan/05 § Choosing a tier");
            }
        }

        return GateOutcome.From(
            "Storage tier",
            bindings.Count,
            $"[PersistentState] binding(s), {listed.Count} listed in {DurableGrainsFile.Name}, "
            + $"{rationales.Count} with [DurableStateRationale]",
            violations);
    }

    /// <summary>
    ///     <c>CyberCloud.Core.Contracts.StorageTiers.Durable</c>, spelled out because <c>build/</c>
    ///     does not reference the product tree — <c>_build.csproj</c> is deliberately not a member of
    ///     <c>CyberCloud.slnx</c>. A rename there is a data migration
    ///     (<c>StorageTiers</c>' own remarks say so), so this is not a constant that drifts quietly.
    /// </summary>
    const string DurableTier = "Durable";

    // ── Gate: tenant keys, the half CC1004 cannot see ─────────────────────────────────────────

    /// <summary>
    ///     Every grain interface is <c>IGrainWithStringKey</c>. docs/plan/23 § The architecture
    ///     gates, row Tenant keys, second clause.
    ///     <para>
    ///         The first clause — "no string literal containing '|' in a <c>GetGrain</c> argument" —
    ///         is CC1004 and is not repeated here.
    ///     </para>
    ///     <para>
    ///         ⚠ The doc says "every <i>tenant-scoped</i> grain interface", and this checks every
    ///         grain interface, which is stricter. Nothing in metadata says whether an interface is
    ///         tenant-scoped, and the stricter rule is the one that is actually right:
    ///         <c>Orleans.Multitenant</c> encodes the tenant into the string key, so a grain with any
    ///         other key kind cannot be tenant-scoped later without a breaking change. A platform
    ///         grain that is genuinely null-tenant is still <c>IGrainWithStringKey</c> here —
    ///         <c>IShardMapGrain</c> and <c>ITenantDirectoryGrain</c> both are.
    ///     </para>
    /// </summary>
    GateOutcome TenantKeyGate()
    {
        var grainInterfaces = ShippingAssemblies
            .SelectMany(assembly => assembly.InterfaceBases.Select(x => (Assembly: assembly.Name, Interface: x.Key, Bases: x.Value)))
            .Where(x => x.Bases.Any(IsGrainKeyInterface))
            .OrderBy(x => x.Interface, StringComparer.Ordinal)
            .ToList();

        var violations = grainInterfaces
            .Where(x => !x.Bases.Contains("IGrainWithStringKey", StringComparer.Ordinal))
            .Select(x =>
                $"{x.Interface} ({x.Assembly}) extends {string.Join(", ", x.Bases.Where(IsGrainKeyInterface))}. "
                + "Orleans.Multitenant carries the tenant in the string key and nowhere else, so any other key "
                + "kind puts the grain permanently outside tenant separation — docs/plan/02 § ADR-002")
            .ToList();

        return GateOutcome.From(
            "Tenant keys",
            grainInterfaces.Count,
            "grain interface(s); the '|'-literal half of this row is CC1004, at compile time",
            violations);
    }

    static bool IsGrainKeyInterface(string name)
        => name.StartsWith("IGrainWith", StringComparison.Ordinal)
            && name.EndsWith("Key", StringComparison.Ordinal);

    // ── Gates: generated surfaces and OpenAPI compatibility — docs/plan/02 § ADR-012 ──────────

    GenerationReport? generation;

    /// <summary>
    ///     The generator's report, produced once and read by both rows below.
    ///     <para>
    ///         ⚠ <c>--check</c>, so the gate never writes into the tree it is inspecting. That is the
    ///         difference between this and <c>Generate</c>, which writes first and then reports —
    ///         a gate that repaired the thing it was checking would be permanently green.
    ///     </para>
    /// </summary>
    GenerationReport Generation => generation ??= RunGenerator(write: false);

    /// <summary>
    ///     docs/plan/23 § The architecture gates, row <b>Generated surfaces</b>: "OpenAPI/CLI/SDK/forms
    ///     regenerate byte-identically from the registry."
    ///     <para>
    ///         ⚠ <b>All four, at last, and the message says which.</b> docs/plan/21 § Generation makes
    ///         the OpenAPI document the surface the CLI, the SDK and the portal forms are generated
    ///         <i>from</i>, so the four are one chain rather than four independent generators — and a
    ///         drift anywhere in it fails here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Counted in resource types rather than in documents.</b> An empty registry still
    ///         produces <c>openapi/index.json</c>, so counting documents would report "1 inspected"
    ///         and a confident tick over a platform with no API at all.
    ///     </para>
    /// </summary>
    GateOutcome GeneratedSurfacesGate()
    {
        var violations = new List<string>();

        foreach (var document in Generation.Documents)
        {
            foreach (var problem in document.StructuralProblems)
            {
                violations.Add(
                    $"openapi/{document.File} is not a valid OpenAPI 3.1 document — {problem}. "
                    + "docs/plan/02 § ADR-012 specifies 3.1");
            }

            if (document.Drifted)
            {
                violations.Add(
                    $"openapi/{document.File} is not what the provider registry generates. Run "
                    + "./build.sh Generate and commit the result — docs/plan/23 § The architecture "
                    + "gates, row Generated surfaces");
            }
        }

        foreach (var stale in Generation.Stale)
        {
            violations.Add(
                $"openapi/{stale} is checked in and the registry no longer produces it. An api-version "
                + "is kept forever and removing one needs a 12-month notice window — "
                + "docs/plan/08 § The provider registry");
        }

        foreach (var surface in Generation.Derived)
        {
            foreach (var problem in surface.Problems)
                violations.Add($"generated/{surface.File} is not usable — {problem}");

            if (surface.Drifted)
            {
                violations.Add(
                    $"generated/{surface.File} is not what the OpenAPI document generates. Run "
                    + "./build.sh Generate and commit the result — docs/plan/23 § The architecture "
                    + "gates, row Generated surfaces");
            }
        }

        foreach (var stale in Generation.DerivedStale)
        {
            violations.Add(
                $"generated/{stale} is checked in and nothing produces it. A generated surface nobody "
                + "generates is one nobody can reproduce");
        }

        return GateOutcome.From(
            "Generated surfaces",
            Generation.ResourceTypes,
            $"resource type(s) over {Generation.Documents.Count} OpenAPI document(s) and "
            + $"{Generation.Derived.Count} derived file(s) — the cyc verb tree, the .NET SDK and the "
            + "portal forms — all regenerated and compared byte-for-byte",
            violations);
    }

    /// <summary>
    ///     docs/plan/23 § The architecture gates, row <b>OpenAPI compatibility</b>: "published
    ///     api-versions diffed; a breaking change fails."
    ///     <para>
    ///         Counted in documents that had a published predecessor, because a version that is new in
    ///         this commit has nothing to be incompatible with — so a run that only added versions is
    ///         <see cref="GateStatus.Vacuous" /> rather than <see cref="GateStatus.Enforced" />, which
    ///         is the honest answer to "did the compatibility rule hold".
    ///     </para>
    /// </summary>
    GateOutcome OpenApiCompatibilityGate()
    {
        var diffable = Generation.Documents
            .Where(x => x.Published && x.ApiVersion.Length > 0)
            .ToList();

        var violations = Generation.Documents
            .SelectMany(document => document.BreakingChanges.Select(breaking =>
                $"openapi/{document.File} breaks api-version {document.ApiVersion} — {breaking}. "
                + "docs/plan/21 § OpenAPI: adding an optional field is fine, removing anything or "
                + "narrowing a type is not"))
            .ToList();

        return GateOutcome.From(
            "OpenAPI compatibility",
            diffable.Count,
            "published api-version document(s) diffed against their checked-in predecessor",
            violations);
    }

    // ── Gate: wire compatibility — docs/plan/23 § The architecture gates, row Wire compatibility ─

    /// <summary>
    ///     How many released contract assemblies docs/plan/23 § The architecture gates wants the tree
    ///     round-tripped through.
    ///     <para>
    ///         ⚠ <b>Three rather than one, and the doc says why in a sentence worth not paraphrasing
    ///         away:</b> <i>"a hotfix branch will eventually be older than the previous tag, and
    ///         discovering that during an incident is the worst time."</i> That is the whole design.
    ///         Comparing against the newest release only would pass a tree that is compatible with
    ///         <c>v1.2.0</c> and unreadable by the <c>v1.0.x</c> silo the hotfix is being cut for.
    ///     </para>
    ///     <para>
    ///         It also sets the window in which a burned <c>[Id(n)]</c> number is actually enforced:
    ///         a number removed in one release and quietly reused four releases later is outside what
    ///         any diff of three manifests can see. The convention covers that case and this gate does
    ///         not; the convention is <c>CyberCloud.Core.Contracts.Tests.WireContractTests</c>'
    ///         append-only baseline, which is per-assembly and forever.
    ///     </para>
    /// </summary>
    const int ReleasesCompared = 3;

    [Parameter(
        "Record the wire baseline for any release tag that has no manifest under build/wire/. Builds "
        + "each such tagged commit from `git archive`, which is the one expensive thing in this "
        + "target and is why it is opt-in.")]
    readonly bool WireRecord;

    AbsolutePath WireDirectory => RootDirectory / "build" / WireContract.ManifestDirectoryName;

    /// <summary>
    ///     The aliases deliberately taken out of circulation. ⚠ Hand-written and reviewed, unlike the
    ///     manifests beside it, which are generated — see the file's own header.
    /// </summary>
    AbsolutePath BurnedAliasesFile => WireDirectory / "burned.txt";

    AbsolutePath WireManifest(string tag) => WireDirectory / (tag + ".txt");

    List<WireType>? currentWireTypes;

    /// <summary>
    ///     Every aliased wire type in the tree, read once and shared by the two rows that are about
    ///     the wire. Reading the assemblies twice would double the only expensive thing either row
    ///     does and could — if a build wrote between the two reads — let them disagree.
    /// </summary>
    IReadOnlyList<WireType> CurrentWireTypes =>
        currentWireTypes ??= WireContract.Read(ShippingAssemblyPaths.Select(x => x.Assembly));

    // ── Gate: serializer discipline — docs/plan/23 § The architecture gates ───────────────────

    /// <summary>
    ///     docs/plan/23 § The architecture gates, row <b>Serializer discipline</b>: "every
    ///     <c>[GenerateSerializer]</c> type has a stable <c>[Alias]</c>; <c>[Id(n)]</c> numbers never
    ///     reused, checked against a committed manifest."
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This row was a string constant, and it is the third instance of the defect this
    ///         file has now removed twice before.</b> It read <c>GateOutcome.Analyzer("Serializer
    ///         discipline", "CC1003 for [Alias]; the [Id(n)] manifest is
    ///         CyberCloud.Core.Contracts.Tests.WireContractTests")</c>. Nothing checked that the named
    ///         test existed, that it passed, or that CC1003 was still switched on — and the named test
    ///         reflects over <c>typeof(ResultSurrogate).Assembly</c>, which is
    ///         <c>CyberCloud.Core.Contracts</c> and <b>6 of the tree's 212 aliased wire types</b>. A
    ///         singular claim over a plural subject, printed with a tick.
    ///         <see cref="LabelsGate" />'s remarks make the general argument: a status line that
    ///         asserts a condition it does not evaluate is worse than no status line.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this closes that <see cref="WireCompatibilityGate" /> does not, stated
    ///         narrowly, because the overlap is larger than it looks.</b> That gate compares the tree
    ///         against <i>released</i> manifests, and <c>build/wire/v0.1.0.txt</c> happens to contain
    ///         every wire type in the tree today — so "numbers never reused, never reordered" is in
    ///         fact enforced for all 212 right now, by a committed manifest, exactly as the doc's row
    ///         words it. The per-assembly baselines in the test suites are <b>not</b> what makes that
    ///         true, and a report claiming otherwise would be wrong in the tree's favour.
    ///     </para>
    ///     <para>
    ///         The gap is the <b>window between a type being written and a release being cut</b>. A
    ///         type added after the newest tag appears in no released manifest, so its numbers may be
    ///         renumbered freely until somebody tags — and the renumber then lands in the first
    ///         baseline as if it had always been that way. That count is what this row reports, and it
    ///         is <see cref="GateStatus.Vacuous" /> whenever it is not zero: an unpinned type is a
    ///         subject the row cannot see, which is ○ with the number rather than ✔.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The half that is not about baselines at all is the half nothing else could do.</b>
    ///         CC1003 sees one compilation. It cannot see two assemblies claiming one alias, which is
    ///         a coin flip at deserialization; it cannot see whether it is still error-severity in
    ///         some project; and it does not run over an assembly at all unless that project opted in
    ///         (<see cref="AnalyzerCoverageGate" />). Reading the metadata that shipped answers all
    ///         three from the artefact rather than from the configuration that produced it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cost is deliberately near zero, and that is a design constraint rather than a
    ///         happy result.</b> <c>Architecture</c> is about thirteen seconds and people run it
    ///         constantly. <see cref="LabelsGate" /> is the shape this row would otherwise copy — run
    ///         the suite, believe only the result — but there are five per-assembly baseline suites
    ///         and each <c>dotnet run</c> costs about 1.2s, which would be roughly a 50% increase for
    ///         a property the metadata already answers more completely. So this row reads the
    ///         assemblies <see cref="CurrentWireTypes" /> has already opened and the manifests
    ///         <see cref="WireCompatibilityGate" /> already parses, and claims nothing about any test.
    ///         <c>Test</c> runs those suites; this row does not pretend to.
    ///     </para>
    /// </remarks>
    GateOutcome SerializerDisciplineGate()
    {
        var current = CurrentWireTypes;
        var serializable = WireContract.Serializable(ShippingAssemblyPaths.Select(x => x.Assembly));
        var violations = new List<string>();

        // ⚠ THE EMPTY-INPUT VIOLATION, which is failure class (b) from the wire gate's own sabotage:
        // every check below is a loop over what was read, so a read that returned nothing produces no
        // violations and the row would print a confident count of zero. This tree cannot have zero
        // wire types — CyberCloud.Core.Contracts alone publishes six — so an empty read means the
        // assemblies were not the ones this gate thinks it opened.
        if (current.Count == 0 || serializable.Count == 0)
        {
            violations.Add(
                $"reading {ShippingAssemblyPaths.Count} shipping assembl(y/ies) found {current.Count} "
                + $"aliased type(s) and {serializable.Count} [GenerateSerializer] type(s). Neither can be "
                + "zero in this tree, so the metadata this row inspected is not the wire surface it "
                + "believes it read — every check in this gate loops over that set and would otherwise "
                + "report a clean sweep of nothing");
        }

        // The CC1003 property, evaluated against what shipped rather than asserted about the compiler.
        foreach (var (assembly, clrName, _) in serializable.Where(x => !x.Aliased))
        {
            violations.Add(
                $"{clrName} ({assembly}) carries [GenerateSerializer] and no [Alias]. CC1003 is supposed "
                + "to make this uncompilable, so if it is in the tree the analyzer did not run here — "
                + "check the project's OutputItemType=\"Analyzer\" reference and CC1003's severity. "
                + "docs/plan/04 § Failure and upgrade: the alias is what a peer looks the type up by");
        }

        // ⚠ Cross-assembly, which is the one thing no analyzer in this tree can be. CC1003 sees a
        // single compilation; two assemblies each declaring [Alias("CyberCloud.Core.Result")] compile
        // cleanly and separately, and Orleans then hands a peer's payload to whichever won the
        // registration race.
        foreach (var clash in current
            .GroupBy(x => x.Alias, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            violations.Add(
                $"the alias \"{clash.Key}\" is declared by {clash.Count()} types — "
                + $"{string.Join(", ", clash.Select(x => $"{x.ClrName} ({x.Assembly})"))}. An alias is the "
                + "primary key of the wire, so two claimants is a coin flip at deserialization rather "
                + "than an error anything reports");
        }

        // "Never reused", in the one form that needs no baseline at all: within a single type. A
        // number declared twice is the same slot written twice, and the far side reads whichever the
        // codec emitted last.
        foreach (var type in current)
        {
            foreach (var duplicate in type.Members
                .GroupBy(x => x.Id)
                .Where(x => x.Count() > 1)
                .OrderBy(x => x.Key))
            {
                violations.Add(
                    $"\"{type.Alias}\" ({type.ClrName}) declares [Id({duplicate.Key})] on "
                    + $"{string.Join(" and ", duplicate.Select(x => x.Name))}. docs/plan/05 § Serialization "
                    + "and schema evolution: numbers are never reused, and two members in one slot is the "
                    + "shortest way to break that");
            }
        }

        // ── Coverage: which of these types has a committed manifest pinning its numbers ──────────
        var pinned = PinnedAliases();

        var unpinned = current
            .Select(x => x.Alias)
            .Where(alias => !pinned.Contains(alias))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var detail =
            $"{serializable.Count} [GenerateSerializer] type(s), all checked for an [Alias]; "
            + $"{current.Count} aliased wire type(s), checked for a unique alias and for no [Id(n)] "
            + $"declared twice; {current.Count - unpinned.Count} of {current.Count} have their numbers "
            + $"pinned by a committed manifest under build/{WireContract.ManifestDirectoryName}";

        if (unpinned.Count > 0)
        {
            detail += $". {unpinned.Count} do not and can be renumbered until the next release tag "
                + $"records them — {string.Join(", ", unpinned.Take(5))}"
                + (unpinned.Count > 5 ? $" and {unpinned.Count - 5} more" : string.Empty);
        }

        return new GateOutcome(
            "Serializer discipline",
            violations.Count > 0 ? GateStatus.Failed
            : unpinned.Count > 0 ? GateStatus.Vacuous
            : GateStatus.Enforced,
            detail,
            violations);
    }

    /// <summary>
    ///     Every alias any committed manifest under <c>build/wire/</c> records.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every manifest, not only the three <see cref="ReleaseTags" /> returns. A number pinned by
    ///     a manifest that has since fallen out of the comparison window is still a number somebody
    ///     wrote down and reviewed, and this row is counting what is <i>pinned</i> rather than what is
    ///     <i>compared</i>. <see cref="WireCompatibilityGate" /> is the row about the window.
    /// </remarks>
    HashSet<string> PinnedAliases()
    {
        var pinned = new HashSet<string>(StringComparer.Ordinal);

        if (!WireDirectory.DirectoryExists())
            return pinned;

        foreach (var manifest in WireDirectory.GlobFiles("*.txt").Where(x => x != BurnedAliasesFile))
        {
            var (_, _, released) = WireContract.Parse(manifest.Name, manifest.ReadAllLines());

            pinned.UnionWith(released.Select(x => x.Alias));
        }

        return pinned;
    }

    /// <summary>
    ///     docs/plan/23 § The architecture gates, row <b>Wire compatibility</b>: "round-trip every
    ///     wire type through the last three released contract assemblies."
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This row reported <see cref="GateStatus.Blocked" /> with the reason "the
    ///         repository has no release tags … <c>git tag</c> is empty" — and it never ran
    ///         <c>git tag</c>.</b> The sentence was a string constant. When <c>v0.1.0</c> was created
    ///         precisely to unblock this row, the row went on printing that the repository had no
    ///         tags, because the only thing that could have noticed was a human reading the sentence.
    ///         A status line asserting a condition it does not evaluate is worse than no status line:
    ///         the roster is the artefact people trust instead of looking.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What "the released contract assemblies" turned out to mean here.</b> Nothing
    ///         publishes them: <c>src/CyberCloud.Sdk</c> is the only project in the tree with
    ///         <c>IsPackable=true</c> (<c>Directory.Build.props</c> § "Nothing is packable unless it
    ///         opts in"), and it is not a contracts assembly, so there is no NuGet package of
    ///         <c>CyberCloud.Core.Contracts</c> at <c>v0.1.0</c> or at any other tag to restore. The
    ///         only artefact a release leaves behind is the tagged commit. So a release's wire surface
    ///         is recorded by building that commit — <c>--wire-record</c>, about ten seconds a tag —
    ///         and the result is committed as <c>build/wire/&lt;tag&gt;.txt</c>, the same shape of
    ///         trust <c>openapi/2026-08-01.json</c> already carries.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cost, measured, because a target people run constantly is a target people
    ///         stop running.</b> <c>Architecture</c> was about twelve seconds before this row existed.
    ///         Building three tagged commits on every invocation would have added about thirty. What
    ///         this does on every invocation is read the assemblies <see cref="ShippingAssemblies" />
    ///         has already opened and three text files, and the whole row costs under a second. The
    ///         thirty seconds is paid once per release, by whoever cuts the tag, and lands in a
    ///         reviewable diff.
    ///     </para>
    /// </remarks>
    GateOutcome WireCompatibilityGate()
    {
        var current = CurrentWireTypes;
        var burned = BurnedAliases();
        var tags = ReleaseTags();
        var violations = WireContract.Revived(current, burned);

        // ⚠ The vacuity this row is most likely to be caught by. Not "the gate found nothing wrong"
        // but "there is nothing yet that this tree could be incompatible with" — the state the old
        // Blocked row was describing, said by code that actually looked.
        if (tags.Count == 0)
        {
            return new GateOutcome(
                "Wire compatibility",
                violations.Count > 0 ? GateStatus.Failed : GateStatus.Vacuous,
                $"{current.Count} aliased wire type(s) in the tree and 0 release tag(s) — `git tag` "
                + $"matched no {ReleaseTagPattern}, so nothing has been published for the tree to be "
                + "incompatible with",
                violations);
        }

        var compared = new List<string>();

        foreach (var tag in tags)
        {
            var manifest = WireManifest(tag);

            if (!manifest.FileExists() && WireRecord)
                RecordWireBaseline(tag, manifest);

            if (!manifest.FileExists())
            {
                violations.Add(
                    $"{tag} is a release tag and {RootDirectory.GetRelativePathTo(manifest)} does not "
                    + $"exist, so nothing in this repository knows what {tag} published on the wire. Run "
                    + "./build.sh Architecture --wire-record and commit the result — docs/plan/23 § The "
                    + "architecture gates, row Wire compatibility");

                continue;
            }

            var (_, recorded, released) = WireContract.Parse(manifest.Name, manifest.ReadAllLines());
            var actual = TagCommit(tag);

            // A manifest is only worth what its provenance is worth. This is what stops one being
            // regenerated from a moved tag, or copied from another tag's file and renamed.
            if (!string.Equals(recorded, actual, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{RootDirectory.GetRelativePathTo(manifest)} records commit {recorded} and {tag} "
                    + $"points at {actual}. The manifest was not generated from the tag it claims — "
                    + "regenerate it with ./build.sh Architecture --wire-record");

                continue;
            }

            // ⚠ A manifest that parses to nothing is the failure this whole gate exists to remove,
            // one turn of the screw further in. Every check below it is a loop over the released
            // types, so an emptied or truncated file produces zero violations and the row would then
            // report "1 of 3 released baseline(s) (v0.1.0)" — a comparison it did not make, named
            // after a release it did not read. Found by emptying the file and watching the row stay
            // green. No release this repository will ever cut publishes zero wire types.
            if (released.Count == 0)
            {
                violations.Add(
                    $"{RootDirectory.GetRelativePathTo(manifest)} parses to 0 wire types, so comparing "
                    + $"against it proves nothing about {tag}. Regenerate it with ./build.sh Architecture "
                    + "--wire-record");

                continue;
            }

            violations.AddRange(WireContract.Compare(tag, released, current, burned));
            compared.Add($"{tag}: {released.Count}");
        }

        var detail =
            $"{current.Count} aliased wire type(s) over {current.Sum(x => x.Members.Count)} [Id(n)] "
            + $"member(s) and {current.Sum(x => x.Values.Count)} enum constant(s), against "
            + $"{compared.Count} of {ReleasesCompared} released baseline(s)"
            // ⚠ The released type count per tag, not just the tag name. A row that names what it read
            // without saying how much of it read the same as one that read an empty file.
            + (compared.Count > 0 ? $" ({string.Join("; ", compared)} types)" : string.Empty);

        // ⚠ Enforced needs all three, and the shortfall is reported as ○ rather than ✔ on purpose.
        // One release is a real comparison over real types and it is not the property the row claims:
        // the hotfix case the doc names is exactly the one a single baseline cannot see. Saying
        // "1 of 3" under a tick would be the same defect this gate was written to remove, one row
        // further along.
        var status = violations.Count > 0 ? GateStatus.Failed
            : compared.Count >= ReleasesCompared ? GateStatus.Enforced
            : GateStatus.Vacuous;

        if (status == GateStatus.Vacuous)
        {
            detail += $". {ReleasesCompared - compared.Count} more release(s) needed before this row is "
                + "the guarantee docs/plan/23 words it as — a hotfix branch older than the previous tag "
                + "is what the other two baselines are for";
        }

        return new GateOutcome("Wire compatibility", status, detail, violations);
    }

    /// <summary>
    ///     ⚠ <c>v</c> and a dotted number, so that a moving pointer like <c>latest</c> or a topic tag
    ///     cannot silently become one of the three things the platform's wire compatibility is
    ///     measured against.
    /// </summary>
    const string ReleaseTagPattern = "v[0-9]*";

    /// <summary>
    ///     The last three release tags, newest first. <c>--sort=-v:refname</c> so <c>v0.10.0</c> sorts
    ///     above <c>v0.9.0</c>, which lexical order gets wrong the first time it matters.
    /// </summary>
    // List and HashSet rather than the interfaces: CA1859 is an error here and both are private
    // helpers — the same reason ShippingProjectFiles above returns a Dictionary.
    List<string> ReleaseTags() =>
        GitTasks.Git($"tag --list \"{ReleaseTagPattern}\" --sort=-v:refname", RootDirectory, logOutput: false, logInvocation: false)
            .Where(x => x.Type == OutputType.Std && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => x.Text.Trim())
            .Take(ReleasesCompared)
            .ToList();

    /// <summary>The commit a tag resolves to — <c>^{commit}</c> because these tags are annotated.</summary>
    string TagCommit(string tag) =>
        GitTasks.Git($"rev-list -n 1 {tag}", RootDirectory, logOutput: false, logInvocation: false)
            .Where(x => x.Type == OutputType.Std && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => x.Text.Trim())
            .FirstOrDefault() ?? string.Empty;

    /// <summary>
    ///     The declared burned aliases. Missing file means none, which is the honest reading — the
    ///     tree has never retired an alias after a release.
    /// </summary>
    HashSet<string> BurnedAliases()
        => !BurnedAliasesFile.FileExists()
            ? new HashSet<string>(StringComparer.Ordinal)
            : BurnedAliasesFile.ReadAllLines()
                .Select(line => line.Split('#')[0].Trim())
                .Where(line => line.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Builds a tagged commit and records what it published.
    ///     <para>
    ///         <c>git archive</c> into <c>artifacts/</c> rather than <c>git worktree add</c>: the
    ///         second writes into <c>.git</c> and leaves administrative state behind if the build
    ///         throws, and nothing here needs history. The tree is deleted afterwards — what is kept
    ///         is the manifest, which is the thing worth reviewing.
    ///     </para>
    ///     <para>
    ///         ⚠ Test assemblies are excluded through <see cref="SuiteOwning" />, the same classifier
    ///         <see cref="ShippingAssemblyPaths" /> uses, so that "what the release published" means
    ///         the same set on both sides of the comparison. A fixture in a test assembly carrying an
    ///         <c>[Alias]</c> would otherwise be recorded as released wire surface and then reported
    ///         as retired the moment the fixture was renamed.
    ///     </para>
    /// </summary>
    void RecordWireBaseline(string tag, AbsolutePath manifest)
    {
        var scratch = ArtifactsDirectory / "wire-record";
        var tree = scratch / tag;
        var tar = scratch / (tag + ".tar");

        Log.Information("Wire compatibility: recording {Tag} — building the tagged commit", tag);

        try
        {
            tree.CreateOrCleanDirectory();
            GitTasks.Git($"archive --format=tar --output \"{tar}\" {tag}", RootDirectory, logOutput: false);
            TarFile.ExtractToDirectory(tar, tree, overwriteFiles: true);

            DotNetTasks.DotNetBuild(s => s
                .SetProjectFile(tree / SolutionFile.Name)
                .SetConfiguration(Configuration)
                .SetProcessWorkingDirectory(tree));

            var released = (tree / "artifacts" / "bin")
                .GlobDirectories("*")
                .Select(x => x / Configuration.ToLowerInvariant() / (x.Name + ".dll"))
                .Where(x => x.FileExists() && SuiteOwning(x) is null)
                .OrderBy(x => x.NameWithoutExtension, StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(
                released,
                $"building {tag} produced no shipping assembly under artifacts/bin. A manifest written "
                + "from an empty set would record that the release published nothing, and every alias "
                + "in the tree would then look new rather than compatible.");

            manifest.Parent.CreateDirectory();
            manifest.WriteAllText(WireContract.Write(tag, TagCommit(tag), WireContract.Read(released)));

            Log.Information(
                "Wire compatibility: {Tag} → {File}, from {Count} shipping assembl{Suffix}. Commit it",
                tag,
                RootDirectory.GetRelativePathTo(manifest),
                released.Count,
                released.Count == 1 ? "y" : "ies");
        }
        finally
        {
            tar.DeleteFile();
            tree.DeleteDirectory();
        }
    }

    // ── Gate: Labels — docs/plan/23 § The architecture gates, row Labels ──────────────────────

    /// <summary>
    ///     The assertion this gate delegates to. docs/plan/23 § The architecture gates, row Labels:
    ///     "every reconciler's rendered output carries the seven <c>cybercloud.io/*</c> labels —
    ///     asserted by the conformance suite against real output, not by inspection."
    /// </summary>
    const string LabelsAssertion = "EveryAppliedObjectCarriesTheSevenMandatoryLabelsAndBothAnnotations";

    /// <summary>
    ///     Runs the labels assertion in every provider's conformance suite.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This gate was <see cref="GateStatus.Blocked" /> with the reason "no provider
    ///         exists to render any", and that reason expired the day
    ///         <c>src/Providers/CyberCloud.Providers.Sample</c> landed.</b> A blocked row whose stated
    ///         blocker is gone is worse than a failing one: the report keeps printing a sentence that
    ///         is no longer true, and nothing in the build can notice, because nothing in the build
    ///         reads a blocked row's prose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It runs the test rather than looking for it.</b> Doc 23 puts this row's assertion
    ///         in the conformance suite deliberately — the labels have to be read off objects a real
    ///         reconciler really applied, not off source — so the only thing this gate can honestly
    ///         report is the result of that run. Checking that the method exists would be inspection
    ///         wearing the suite's name, and a green tick over a test that has been failing since
    ///         Tuesday.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The duplicated work with <c>Test</c> is the point, not an oversight.</b>
    ///         <c>Test</c> runs the whole conformance suite and would fail on this assertion too, but
    ///         <c>Architecture</c> is the target docs/plan/23 § The architecture gates names, and a
    ///         gate that is green because a different target would have caught it is a gate that
    ///         reports on that target's configuration rather than on the tree. The cost is one filtered
    ///         test per provider, about a second each.
    ///     </para>
    ///     <para>
    ///         Counted in conformance suites: <see cref="GateStatus.Vacuous" /> until a provider ships
    ///         one, which is what the old Blocked row was reaching for and could not express.
    ///     </para>
    /// </remarks>
    GateOutcome LabelsGate()
    {
        // ⚠ Provider suites only — under src/Providers, not test/. test/CyberCloud.Conformance holds
        // the shared base class and a reference provider that exists to test the harness; counting it
        // would let the gate stay green on a tree whose real providers all stopped rendering labels.
        var suites = ClassifiedTestProjects
            .Where(x => x.Suite == TestSuite.PerPullRequest)
            .Select(x => x.Project)
            .Where(x => x.NameWithoutExtension.EndsWith(".Conformance", StringComparison.Ordinal))
            // ⚠ The Docker-free half only, and this is a correctness rule rather than a preference.
            // A provider's `.Cluster.Conformance` project asserts the same labels against a REAL API
            // server — strictly stronger, because it reads them off the object after admission — but
            // every test in it skips when no Docker daemon answers, and a filtered run whose only
            // match is a skipped test reports "Zero tests ran", which --minimum-expected-tests 1
            // treats as failure. Pointing this gate there would make an architecture gate pass or
            // fail on whether a daemon happened to be running. The stronger assertion is still made;
            // it is just not what this row reports on.
            .Where(x => !x.NameWithoutExtension.EndsWith(".Cluster.Conformance", StringComparison.Ordinal))
            .Where(x => x.ToString().Contains($"{Path.DirectorySeparatorChar}Providers{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(x => x.NameWithoutExtension, StringComparer.Ordinal)
            .ToList();

        var violations = suites
            .Where(suite => !SuiteTestPasses(suite, $"*.{LabelsAssertion}"))
            .Select(suite =>
                $"{suite.NameWithoutExtension} does not pass {LabelsAssertion} — either the assertion "
                + "failed (its output is above) or the suite has no test by that name, which "
                + "--minimum-expected-tests 1 reports the same way. docs/plan/23 § The architecture "
                + "gates, row Labels; the seven labels are CyberCloud.Kubernetes' KubeLabels.Mandatory")
            .ToList();

        return GateOutcome.From(
            "Labels",
            suites.Count,
            $"provider conformance suite(s), each running {LabelsAssertion} against objects a real "
            + "reconciler applied",
            violations);
    }

    // ── Gate: analyzer coverage — not in docs/plan/23, and that is the point ──────────────────

    /// <summary>
    ///     Every shipping project references <c>CyberCloud.Analyzers</c> as an analyzer asset.
    ///     <para>
    ///         ⚠ <b>This gate is what makes four rows of docs/plan/23 § The architecture gates
    ///         true.</b> Tenant keys, serializer discipline, secrets and no-blocking are all
    ///         "analyzer-enforced" — and an analyzer polices exactly the projects whose <c>.csproj</c>
    ///         opted in with <c>OutputItemType="Analyzer"</c>. A new assembly is not covered by
    ///         default, it is covered by somebody remembering, and "somebody remembered" is the
    ///         property a gate exists to replace.
    ///     </para>
    ///     <para>
    ///         Test projects are out of scope, matching <see cref="ShippingAssemblies" />. The
    ///         analyzer itself and its own test project are out of scope for the obvious reason.
    ///     </para>
    /// </summary>
    GateOutcome AnalyzerCoverageGate()
    {
        var candidates = ShippingProjectFiles
            .Where(x => !string.Equals(x.Key, "CyberCloud.Analyzers", StringComparison.Ordinal))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        var violations = candidates
            .Where(x => !ReferencesAnalyzer(x.Value))
            .Select(x =>
                $"{x.Key} does not reference CyberCloud.Analyzers. Add "
                + "<ProjectReference Include=\"…/CyberCloud.Analyzers/CyberCloud.Analyzers.csproj\" "
                + "OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" /> — without it CC1001–CC1007 "
                + "do not run on this project, and docs/plan/23 § The architecture gates' four "
                + "\"analyzer-enforced\" rows are not true of it")
            .ToList();

        return GateOutcome.From("Analyzer coverage", candidates.Count, "shipping project(s)", violations);
    }

    static bool ReferencesAnalyzer(AbsolutePath project)
        => XDocument.Load(project)
            .Descendants()
            .Where(x => string.Equals(x.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
            .Any(x => (x.Attribute("Include")?.Value ?? string.Empty)
                    .EndsWith("CyberCloud.Analyzers.csproj", StringComparison.Ordinal)
                && string.Equals(x.Attribute("OutputItemType")?.Value, "Analyzer", StringComparison.Ordinal));

    // ── Gates: citations — docs/code-documentation-style.md § Citing the plan, § Citing a test ─

    /// <summary>
    ///     The one file both citation gates exempt, and it has to exist: the file that defines the
    ///     rules is the one file that must show the anti-patterns, and it does — marked ✗, in fenced
    ///     examples, next to the ✓.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This was not reasoned about, it was demanded by the build.</b> Writing § Citing a test
    ///     with its two ✗ examples turned <see cref="CodeCitationGate" /> red on its own
    ///     documentation, in a Markdown file, which is the cheapest sabotage-test either gate will
    ///     ever get: the rule fires on real prose, in a non-<c>.cs</c> file, and names the line.
    /// </remarks>
    AbsolutePath CitationRuleFile => RootDirectory / "docs" / "code-documentation-style.md";

    // ── Gate: plan citations — docs/code-documentation-style.md § Citing the plan ─────────────

    /// <summary>
    ///     A citation of <c>docs/plan</c> by line number, anywhere in a tracked file.
    ///     <para>
    ///         Not one of the ten in docs/plan/23 § The architecture gates. It is here because the
    ///         convention has already rotted twice: converting the 209 line citations that existed
    ///         found that <b>61 would have resolved to a different section</b> against the
    ///         then-current docs, and <b>three were wrong when they were written</b>
    ///         (docs/code-documentation-style.md § Citing the plan). A line number is a citation that
    ///         decays silently, which is the worst kind — a reader who follows it lands somewhere
    ///         plausible and believes it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is a ratchet, not a cleanup.</b> The conversion is done and the count is
    ///         zero; the gate's whole job is keeping it there. If it ever reports a number, the fix
    ///         is to cite the section, never to widen the exemption.
    ///     </para>
    /// </summary>
    GateOutcome PlanCitationGate()
    {
        var files = TrackedText.Where(x => x.File != CitationRuleFile).ToList();
        var violations = new List<string>();

        foreach (var (file, lines) in files)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var match = LineNumberCitation.Match(lines[i]);

                if (!match.Success)
                    continue;

                violations.Add(
                    $"{RootDirectory.GetRelativePathTo(file)}:{i + 1} cites the plan by line number "
                    + $"(\"{match.Value}\"). Cite it by section — \"docs/plan/05 § The two tiers\" — "
                    + "docs/code-documentation-style.md § Citing the plan");
            }
        }

        return GateOutcome.From("Plan citations", files.Count, "tracked text file(s)", violations);
    }

    /// <summary>
    ///     <c>docs/plan/</c>, a document number, any filename tail, a colon and a line number. The
    ///     section form has a space and a <c>§</c> where this has a colon, so the two cannot be
    ///     confused.
    /// </summary>
    static readonly Regex LineNumberCitation = new(@"docs/plan/\d+[A-Za-z0-9._-]*:\d+", RegexOptions.Compiled);

    // ── Gate: code citations — the other half of docs/code-documentation-style.md § Citing ───────

    /// <summary>
    ///     A citation of a <b>test</b>, inside <c>&lt;c&gt;</c> markup: a name ending in
    ///     <c>Tests</c>, optionally followed by one member segment.
    /// </summary>
    static readonly Regex TestCitation = new(
        @"<c>([A-Z][A-Za-z0-9_]*Tests)(?:\.([A-Za-z0-9_]+))?</c>",
        RegexOptions.Compiled);

    /// <summary>
    ///     Every <c>&lt;c&gt;SomethingTests&lt;/c&gt;</c> and
    ///     <c>&lt;c&gt;SomethingTests.Method&lt;/c&gt;</c> in a tracked file names a test class, and a
    ///     member of it, that this repository actually compiles.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="PlanCitationGate" /> walks 1070 files to keep plan citations honest,
    ///         and a citation of a type or a test had no gate at all.</b> The decay is the same and it
    ///         is worse: a plan citation at least points at a file that exists, whereas a test
    ///         citation naming a class that never existed sends a reader looking for the assertion
    ///         that is supposed to be paying for the paragraph above it. Sixteen were found in this
    ///         tree when the gate was written, including three that named test classes copied from a
    ///         sibling provider's naming pattern rather than read off the tree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why the subject is tests and not every code citation, which is the whole design
    ///         and was decided by measurement rather than by taste.</b> The obvious rule — every
    ///         <c>&lt;c&gt;Identifier&lt;/c&gt;</c> must resolve — is unusable here: 8805
    ///         <c>&lt;c&gt;</c> spans in this tree, 3707 of them shaped like a dotted identifier, and
    ///         <b>1522</b> naming something this repository does not declare and never will —
    ///         <c>Orleans.Multitenant</c>, <c>PUT</c>, <c>ConfigMap</c>, <c>StatefulSet</c>,
    ///         <c>KubernetesClient</c>, assembly names, resource states. A rule with fifteen hundred
    ///         false positives is a rule somebody switches off in a week.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And why not the middle ground either.</b> The next narrowest rule — every
    ///         <c>&lt;c&gt;KnownType.Member&lt;/c&gt;</c> resolves — was measured too: 445
    ///         occurrences, and the residue after a metadata resolver is dominated by idioms that are
    ///         correct English and not code at all. A file cited without its extension
    ///         (<c>Build.Charts</c>, meaning <c>build/Build.Charts.cs</c>), a file cited with one
    ///         (<c>Program.cs</c>), and a foreign API's JSON field (<c>Status.reason</c> on a
    ///         Kubernetes status) all look exactly like a broken member citation and none of them is
    ///         one. That rule would need three exemptions to survive, and a rule with three exemptions
    ///         is a rule whose next false positive gets a fourth.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What that costs, said plainly rather than left to be discovered.</b> This gate
    ///         would <i>not</i> have caught <c>ProviderConformanceCase.RequiredCrds</c> — the citation
    ///         in <c>charts/managed/kafka/conformance.yaml</c> that named a member which lost a design
    ///         argument and never existed. That one was found by a person reading, and this gate does
    ///         not replace them. What it does is fence off the one shape where the convention is
    ///         unambiguous, the naming rule is exceptionless, and the measured false-positive count is
    ///         zero — which is a gate worth keeping precisely because nobody will ever want to
    ///         suppress it.
    ///     </para>
    ///     <para>
    ///         ⚠ Every tracked text file, not only <c>.cs</c>. Two of the sixteen were in YAML.
    ///     </para>
    /// </remarks>
    GateOutcome CodeCitationGate()
    {
        var assemblies = AllAssemblyPaths;
        var surface = CodeSurface.Read(assemblies);
        var violations = new List<string>();

        // ⚠ THE EMPTY-INPUT VIOLATION — the shape of failure the wire gate's own sabotage found, and
        // the one this gate is most exposed to. Every check below asks "is this name in `surface`",
        // so a surface read from nothing turns every citation into a violation rather than none —
        // loud, not silent. The dangerous direction is the other one: zero CITATIONS found, which
        // reports a confident tick over a regex that stopped matching. GateOutcome.From counts
        // citations, so that case is ○ with the number rather than ✔. What is left is a surface that
        // read no assemblies at all, which would make the loud failure look like a real one.
        Assert.NotEmpty(
            surface.Keys.ToList(),
            $"reading {assemblies.Count} assembl(y/ies) found no type at all. Architecture depends on "
            + "Compile, so this means the artifacts directory was cleaned between the two — every "
            + "citation below would be reported as unresolvable and every one of those reports would "
            + $"be wrong. Run ./build.sh Compile first, in the same configuration ({Configuration}).");

        var files = TrackedText.Where(x => x.File != CitationRuleFile).ToList();
        var citations = 0;

        foreach (var (file, lines) in files)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var match in TestCitation.Matches(lines[i]).Cast<Match>())
                {
                    var type = match.Groups[1].Value;
                    var member = match.Groups[2].Success ? match.Groups[2].Value : null;

                    citations++;

                    if (!surface.TryGetValue(type, out var members))
                    {
                        violations.Add(
                            $"{RootDirectory.GetRelativePathTo(file)}:{i + 1} cites {type} and no "
                            + "assembly this repository compiles declares a type of that name. The "
                            + "paragraph above it is claiming a test pays for it — "
                            + "docs/code-documentation-style.md § Citing a test");

                        continue;
                    }

                    // ⚠ A file extension is not a member. `<c>KafkaReconcilerTests.cs</c>` cites the
                    // FILE the shared harness lives in, which is a correct thing to write and which
                    // this tree writes about `Directory.Build.props` and `Program.cs` too. The carve-
                    // out is narrow on purpose: an extension is lowercase and known, so nothing is
                    // hidden behind it that a member citation could be mistaken for.
                    if (member is null || IsFileExtension(member) || members.Contains(member))
                        continue;

                    violations.Add(
                        $"{RootDirectory.GetRelativePathTo(file)}:{i + 1} cites {type}.{member} and "
                        + $"{type} declares no member of that name. A truncated method name is the "
                        + "usual cause — the numbers are the contract, and so are these — "
                        + "docs/code-documentation-style.md § Citing a test");
                }
            }
        }

        return GateOutcome.From(
            "Code citations",
            citations,
            $"<c>…Tests</c> citation(s) across {files.Count} tracked text file(s), resolved against "
            + $"{surface.Count} type(s) in {assemblies.Count} compiled assembl(y/ies)",
            violations);
    }

    /// <summary>The tails that make a citation a file reference rather than a member reference.</summary>
    static bool IsFileExtension(string segment)
        => segment is "cs" or "csproj" or "slnx" or "props" or "targets" or "json" or "yaml" or "yml"
            or "md" or "txt" or "tpl" or "sh" or "ps1" or "tsx" or "ts" or "razor" or "http";

    /// <summary>
    ///     Every project's built assembly, test projects included.
    /// </summary>
    /// <remarks>
    ///     ⚠ The one place in this file that deliberately does <b>not</b> filter through
    ///     <see cref="SuiteOwning" />. Every other gate here is about the shipped graph, and this one
    ///     is about what a doc comment may name — which is overwhelmingly a test.
    /// </remarks>
    List<AbsolutePath> AllAssemblyPaths =>
        Solution.AllProjects
            .Select(x => (AbsolutePath)x.Path)
            .Select(project => ArtifactsDirectory
                / "bin"
                / project.NameWithoutExtension
                / Configuration.ToLowerInvariant()
                / (project.NameWithoutExtension + ".dll"))
            .Where(x => x.FileExists())
            .OrderBy(x => x.NameWithoutExtension, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Every file git tracks. <c>git ls-files</c> rather than a glob because the alternative
    ///     walks <c>artifacts/</c> and follows <c>references/survival</c>, which is a symlink into
    ///     another repository entirely (docs/plan/03 § Top level).
    /// </summary>
    List<AbsolutePath> TrackedFiles =>
        GitTasks.Git("ls-files", RootDirectory, logOutput: false, logInvocation: false)
            .Where(x => x.Type == OutputType.Std && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => RootDirectory / x.Text.Trim())
            .ToList();

    List<(AbsolutePath File, string[] Lines)>? trackedText;

    /// <summary>
    ///     Every tracked text file with its lines, read once.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Shared because two gates walk the same 1070 files, and a target people run constantly
    ///     cannot pay for that twice.</b> <see cref="PlanCitationGate" /> and
    ///     <see cref="CodeCitationGate" /> ask different questions of identical input — and reading it
    ///     twice would also let them disagree about the file set, which is a worse failure than the
    ///     seconds: two rows reporting different counts of "tracked text file(s)" is a report nobody
    ///     can act on. <c>git ls-files</c> also runs once rather than twice.
    /// </remarks>
    IReadOnlyList<(AbsolutePath File, string[] Lines)> TrackedText =>
        trackedText ??= TrackedFiles
            .Where(file => file.FileExists() && !LooksBinary(file))
            .OrderBy(x => x.ToString(), StringComparer.Ordinal)
            .Select(file => (File: file, Lines: file.ReadAllLines()))
            .ToList();

    /// <summary>A NUL byte in the first 8 KB — the same heuristic git itself uses.</summary>
    static bool LooksBinary(AbsolutePath file)
    {
        using var stream = File.OpenRead(file);

        var head = new byte[8192];
        var read = stream.Read(head, 0, head.Length);

        return Array.IndexOf(head, (byte)0, 0, read) >= 0;
    }
}
