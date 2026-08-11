// Architecture — docs/plan/23 § The architecture gates, the enforcement half of
// docs/plan/00 § Non-negotiables.
//
// What this target reads is in ArchitectureFacts.cs; what it decides is here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Git;
using Serilog;

partial class Build
{
    /// <summary>
    ///     What a gate did, which is not the same question as whether it passed.
    ///     <para>
    ///         ⚠ <see cref="Vacuous" /> exists because a gate that inspected nothing prints the same
    ///         tick as a gate that inspected everything, and the two are not the same news. Half the
    ///         rules in docs/plan/03 § Assembly graph rules are about assemblies this repository does
    ///         not contain yet; reporting them as "passed" would be true and misleading.
    ///     </para>
    /// </summary>
    enum GateStatus
    {
        /// <summary>Ran, inspected at least one candidate, found nothing.</summary>
        Enforced,

        /// <summary>Ran and found no candidate to inspect. Green, and worth nobody's trust yet.</summary>
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
    ///         ⚠ <b>Four of the ten are enforced by the compiler instead, and this target must not
    ///         re-implement them.</b> <c>src/CyberCloud.Analyzers</c> ships CC1001–CC1007:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><b>Tenant keys</b> — the "no string literal containing '|' in a GetGrain argument" half is CC1004. The "every tenant-scoped grain interface is IGrainWithStringKey" half is cross-assembly and is enforced here.</item>
    ///         <item><b>Serializer discipline</b> — the "[Alias] on every [GenerateSerializer]" half is CC1003. The "[Id(n)] numbers never reused, checked against a committed manifest" half is a reflection test, <c>CyberCloud.Core.Contracts.Tests.WireContractTests</c>.</item>
    ///         <item><b>Secrets</b> — CC1005, in full.</item>
    ///         <item><b>No blocking</b> — CC1001 and CC1002. ⚠ Wider than the doc's wording: the doc says "in grain assemblies", the analyzers apply everywhere they are referenced, because a gateway that blocks is a stalled request even though it is not a stalled activation.</item>
    ///     </list>
    ///     <para>
    ///         A compile-time rule beats a build-target sweep for all four: it names the line, it runs
    ///         in the IDE, and it cannot be outrun by a file the sweep's glob missed. What it cannot
    ///         do is see across assemblies, which is why the other six stay here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The analyzer's reach is a project-file opt-in, and it is not universal.</b> A
    ///         project polices itself only if its <c>.csproj</c> carries the
    ///         <c>OutputItemType="Analyzer"</c> reference. <see cref="AnalyzerCoverageGate" /> is the
    ///         gate that keeps the four rows above honest — without it, "compiler-enforced" is a
    ///         claim about six projects wearing the clothes of a claim about the tree.
    ///     </para>
    /// </summary>
    static readonly (string Gate, string Checks)[] ArchitectureGates =
    [
        ("Assembly graph", "the six rules in docs/plan/03"),
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
    IReadOnlyList<(AbsolutePath Project, AbsolutePath Assembly)> ShippingAssemblyPaths =>
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
        // Logged before the gates run, not after: the assembly-graph gate narrates its six rules as
        // it goes, and those lines are unreadable above the header that says what they belong to.
        Log.Information(
            "Architecture: {Count} gates — the ten in docs/plan/23 § The architecture gates, plus "
            + "Analyzer coverage and Plan citations, which that table does not list",
            ArchitectureGates.Length);

        var outcomes = new List<GateOutcome>
        {
            AssemblyGraphGate(),
            StorageTierGate(),
            TenantKeyGate(),
            GateOutcome.Analyzer(
                "Serializer discipline",
                "CC1003 for [Alias]; the [Id(n)] manifest is CyberCloud.Core.Contracts.Tests.WireContractTests"),
            GateOutcome.Blocked(
                "Wire compatibility",
                "the repository has no release tags, so there is no 'last three released contract "
                + "assemblies' to round-trip through. `git tag` is empty. Implement with the first tag"),
            GateOutcome.Analyzer("Secrets", "CC1005, in full"),
            GateOutcome.Analyzer("No blocking", "CC1001 and CC1002, wider than the doc's 'grain assemblies'"),
            GeneratedSurfacesGate(),
            OpenApiCompatibilityGate(),
            GateOutcome.Blocked(
                "Labels",
                "docs/plan/23 § The architecture gates says this one is asserted by the conformance "
                + "suite against real rendered output, not by inspection. No provider exists to render any"),
            AnalyzerCoverageGate(),
            PlanCitationGate(),
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
            Log.Warning(
                "{Count} gate(s) inspected zero candidates and are green because they found nothing, "
                + "not because the tree is clean: {Gates}. ○, not ✔.",
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
    ///     The six rules, read off the compiled assemblies rather than the project files.
    ///     <para>
    ///         ⚠ <b>Rule 3 is the reason for that choice, and it is worth understanding before
    ///         changing it.</b> "No assembly above <c>CyberCloud.Kubernetes</c> references
    ///         <c>k8s.Models</c>" is a rule about <i>types</i>. docs/plan/02 § ADR-004 requires
    ///         <c>UseKubeMembership()</c> and <c>UseKubernetesHosting()</c>, whose packages both
    ///         depend on <c>KubernetesClient</c>, so <c>dotnet list package --include-transitive</c>
    ///         on <c>CyberCloud.ServiceDefaults</c> reports <c>KubernetesClient</c> and always will.
    ///         A gate phrased over packages is therefore <b>unsatisfiable</b>. The <c>AssemblyRef</c>
    ///         table is the right instrument: Roslyn writes a row only for an assembly a type was
    ///         actually bound from, so it says "no code here touches <c>k8s.*</c>" — which is the
    ///         rule — while staying silent about the restore closure, which is not.
    ///     </para>
    ///     <para>
    ///         Rules 2, 4 and 5 are about assemblies this repository does not contain yet and pass
    ///         vacuously. Each logs its candidate count so that is visible rather than inferable.
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
        var providers = ShippingAssemblies.Where(x => ProviderFamily(x.Name) is not null).ToList();

        Rule(
            2,
            "No Providers.* assembly references another Providers.* assembly — not even .Contracts.",
            providers.Count,
            providers.SelectMany(provider => provider.ReferencedAssemblies
                .Where(reference => ProviderFamily(reference) is { } family
                    && !string.Equals(family, ProviderFamily(provider.Name), StringComparison.Ordinal))
                .Select(reference =>
                    $"{provider.Name} binds types from {reference}; go through CyberCloud.ResourceManager by resource id")));

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

        // Rule 4. The "except its own host" half needs a host↔application map that does not exist
        // while there are no .Application assemblies; what is checkable now is "except a host".
        var applications = ShippingAssemblies
            .Where(x => x.Name.EndsWith(".Application", StringComparison.Ordinal))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        Rule(
            4,
            "Nothing references a *.Application assembly except its own host.",
            applications.Count,
            ShippingAssemblies
                .Where(assembly => !IsHost(assembly.Name))
                .SelectMany(assembly => assembly.ReferencedAssemblies
                    .Where(applications.Contains)
                    .Select(reference => $"{assembly.Name} binds types from {reference} and is not a host under src/Hosts")));

        // Rule 5.
        var gateways = ShippingAssemblies
            .Where(x => x.Name.StartsWith("CyberCloud.Gateway", StringComparison.Ordinal))
            .ToList();

        Rule(
            5,
            "The gateway references no provider implementation assembly, only .Contracts and .Application.",
            gateways.Count,
            gateways.SelectMany(gateway => gateway.ReferencedAssemblies
                .Where(IsProviderImplementation)
                .Select(reference => $"{gateway.Name} binds types from the implementation assembly {reference}")));

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

        return GateOutcome.From("Assembly graph", inspected, "rule candidate(s) across 6 rules", violations);
    }

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
        // ⚠ One exemption, and it has to exist: the file that defines the rule is the one file that
        // must show the anti-pattern, and it does — marked ✗, in a fenced example, next to the ✓.
        var exempt = RootDirectory / "docs" / "code-documentation-style.md";

        var files = TrackedFiles
            .Where(file => file.FileExists() && file != exempt && !LooksBinary(file))
            .OrderBy(x => x.ToString(), StringComparer.Ordinal)
            .ToList();

        var violations = new List<string>();

        foreach (var file in files)
        {
            var lines = file.ReadAllLines();

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

    /// <summary>
    ///     Every file git tracks. <c>git ls-files</c> rather than a glob because the alternative
    ///     walks <c>artifacts/</c> and follows <c>references/survival</c>, which is a symlink into
    ///     another repository entirely (docs/plan/03 § Top level).
    /// </summary>
    IReadOnlyList<AbsolutePath> TrackedFiles =>
        GitTasks.Git("ls-files", RootDirectory, logOutput: false, logInvocation: false)
            .Where(x => x.Type == OutputType.Std && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => RootDirectory / x.Text.Trim())
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
