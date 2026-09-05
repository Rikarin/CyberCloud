// Whether the checked-in .NET SDK in generated/sdk/ is valid C# — the reading half of the
// `Generated SDK compiles` gate in Build.Architecture.cs. Issue #73.
//
// Not a partial of Build, for the reason build/README.md gives about ArchitectureFacts.cs and
// CodeSurface.cs: what a gate reads is a separate concern from what it decides. This one is the
// first of the three that reads SOURCE rather than compiled metadata, and it reads it with the same
// compiler the rest of the tree is built by rather than with a regular expression — which is the
// entire point of the issue and is worth not undoing.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nuke.Common.IO;

/// <summary>
///     What one file of <c>generated/sdk/</c> looks like to a C# compiler.
/// </summary>
/// <param name="File">The file, named the way a failure message should name it.</param>
/// <param name="Types">
///     How many types it declares. ⚠ The vacuity guard: a file that parsed to nothing at all would
///     produce no errors and pass, which is the failure mode this whole gate exists to stop one
///     level up.
/// </param>
/// <param name="Declared">
///     How many members it declares <c>partial</c> with no implementing half. Reported rather than
///     asserted — see <see cref="GeneratedSdkSurface" />'s remarks on <c>CS8795</c>.
/// </param>
/// <param name="Errors">
///     One line per compiler error, already filtered to the ones that are defects. Empty is the
///     verdict the gate wants.
/// </param>
sealed record GeneratedSdkFile(string File, int Types, int Declared, IReadOnlyList<string> Errors);

/// <summary>
///     Hands <c>generated/sdk/{api-version}.cs</c> to Roslyn, against the real
///     <c>CyberCloud.Sdk</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Issue #73 in one sentence: the <c>Generated surfaces</c> row compares BYTES, and
///         byte-identical is not valid.</b> <b>Four</b> defect families shipped in
///         <c>generated/sdk/2026-08-01.cs</c> — <c>CS0101</c> from a duplicated enum name,
///         <c>CS0246</c> from an action's enum that was referenced and never declared, seventeen
///         <c>CS0102</c>s from fourteen duplicated property names across eight declaring types, and
///         110 <c>CS9035</c>s from twenty-two <c>= new()</c> initialisers standing in for a body
///         whose members are <c>required</c> — every one of them green under every gate in this
///         repository, because no <c>.csproj</c> includes that file and nothing else handed it to a
///         compiler. The first two were found by running <c>tsc</c> over a DIFFERENT surface and then
///         reading the C# one by eye; the last two were found by this file, on the day it was
///         written.
///         ⚠ <b>This paragraph said "Three defects" until issue #81</b>, and so did
///         <c>Build.Architecture.cs</c> § <c>GeneratedSdkCompilesGate</c> and
///         <c>build/_build.csproj</c>. The <c>484bacf</c> review corrected the <c>CS0102</c> count in
///         all three sentences and left the family count at three, against
///         <c>DerivedSurfaces.cs</c>, <c>generated/README.md</c> and <c>portal/libs/api/README.md</c>,
///         which say four. Three files in <c>build/</c> — the machinery that gates citation honesty —
///         were the ones that disagreed.
///     </para>
///     <para>
///         ⚠ <b>HOW THE FOUR NUMBERS WERE COUNTED, because a count in a comment is the kind of claim
///         this gate exists to stop being taken on trust, and because reviewers had answered 110,
///         111 and 222 to the last of them.</b> Re-derived on 2026-09-05, not copied:
///         <c>git show 16fd0ca:generated/sdk/2026-08-01.cs</c> — the last blob of that file in which
///         all four families are present at once — handed to a probe that replicates
///         <see cref="Compile" /> exactly: the same <c>LanguageVersion.Latest</c> parse options, the
///         same <c>TRUSTED_PLATFORM_ASSEMBLIES</c> reference set plus the built
///         <c>CyberCloud.Sdk.dll</c>, the same pinned <c>Microsoft.CodeAnalysis.CSharp</c> 5.6.0, and
///         the error diagnostics grouped by id rather than read off a log:
///         <c>CS0101</c> 1, <c>CS0246</c> 1, <c>CS0102</c> 17, <c>CS9035</c> 110, plus the 170
///         <c>CS8795</c>s this gate accepts. The same probe over today's checked-in file gives the 170
///         and nothing else, which is the gate's own verdict arrived at a second way.
///     </para>
///     <para>
///         ⚠ <b>110, not the 111 you get by counting the word <c>required</c>.</b> <c>CS9035</c> is
///         one diagnostic per unset required member per creation site, so the arithmetic is
///         "required members of each <c>{Model}Data</c>, summed over the twenty-two
///         <c>public {Model}Data Data { get; init; } = new();</c> lines" — and that sum is 111 in the
///         source text. It is 110 to the compiler because <c>LoadBalancerData</c> declares
///         <c>Port</c> <c>required</c> TWICE: that pair is one of the seventeen <c>CS0102</c>s, and a
///         redeclared member is one member. The 222 in <c>e2005ed</c>'s commit message is the
///         syntactic 111 doubled. The other seven types with duplicated names leave the sum alone:
///         six of them never declared the same member <c>required</c> on both copies, and the seventh,
///         <c>SubnetResource.ListAddressUsageResult</c>, is not one of the twenty-two <c>= new()</c>
///         targets at all.
///         ⚠ <b>Fourteen names and seventeen diagnostics is not a rounding error either</b> — three of
///         the names (<c>Enabled</c>, on <c>KafkaClusterData</c>, <c>NATSClusterData</c> and
///         <c>PostgreSQLServerData</c>) were declared three times each, and <c>CS0102</c> is emitted
///         once per redeclaration.
///         ⚠ <b>What makes all of this stale:</b> nothing in the working tree — these are counts over
///         a historical blob, and <c>16fd0ca</c> is what pins them. They change only if that hash is
///         wrong.
///     </para>
///     <para>
///         ⚠ <b>WHY <c>16fd0ca</c> IS THAT BLOB, IN TWO COMMANDS, BECAUSE THE FIRST ANSWER GIVEN HERE
///         WAS WRONG AND THIS IS THE PARAGRAPH WRITTEN SO THAT NOTHING IS TAKEN ON TRUST.</b> The
///         sentence above read "since <c>d40d962</c> fixed <c>CS0101</c> and <c>CS0246</c> by hand ONE
///         COMMIT BEFORE <c>e2005ed</c> added this gate" until the review of this branch. The two
///         commits are not adjacent and the claim was never load-bearing:
///         <c>git rev-list --count d40d962..e2005ed</c> is <c>42</c>, and <c>e2005ed~1</c> is
///         <c>8548ee9</c>. What actually pins the blob is adjacency in the FILE's history, not in the
///         branch's, and that is the fact the derivation needs:
///         <list type="bullet">
///             <item>
///                 <c>git log --oneline -- generated/sdk/2026-08-01.cs</c> lists <c>e2005ed</c> then
///                 <c>d40d962</c>, so <c>d40d962</c> — the commit that fixed <c>CS0101</c> and
///                 <c>CS0246</c> by hand — is the previous commit to TOUCH this file.
///             </item>
///             <item>
///                 <c>git rev-parse --short d40d962^</c> is <c>16fd0ca</c>. Its blob is therefore the
///                 last one written before those two families were repaired, which is what makes it
///                 the last state of the file carrying all four at once.
///             </item>
///         </list>
///         Both commands were re-run on the tree that carries this comment. ⚠ The forty-two commits
///         between are irrelevant precisely BECAUSE none of them touched this file — which is the
///         first command's real content, and the reason the substantive claim survived a false
///         sentence. That is the failure mode worth naming: an incidental detail nobody needed,
///         asserted with the same confidence as the numbers, in the comment whose entire purpose is
///         that its numbers can be checked.
///     </para>
///     <para>
///         ⚠ <b>Why this is not a throwaway <c>.csproj</c>, which is where issue #73 starts.</b>
///         Two reasons, and the second is the one that settles it:
///     </para>
///     <list type="number">
///         <item>
///             <b>Today's output does not compile in ANY project, and that is by design.</b> Every
///             operation is emitted as <c>public partial Task&lt;…&gt; GetAsync(…);</c> — a
///             declaration whose implementing half is hand-written and does not exist yet
///             (docs/plan/21 § Generation, and <c>CyberCloud.Sdk/EmitterContract.cs</c> § 3, which
///             is the contract those signatures are owed). A partial member with an accessibility
///             modifier and no implementation is <c>CS8795</c>, an ERROR — <c>&lt;NoWarn&gt;</c>
///             cannot demote it — so a project including this file fails on 170 of them. Filtering
///             one diagnostic id here is a line of code with an argument beside it; supplying 170
///             hand-written stubs, and one more for every method the emitter ever gains, is a second
///             surface that drifts. ⚠ <b>170, not 140.</b> One <c>CS8795</c> is emitted per partial
///             MEMBER; 140 is the count of TYPES the file declares, and the two numbers are reported
///             side by side in this gate's own line. Counted on 2026-09-05 against
///             <c>generated/sdk/2026-08-01.cs</c>, which has 170 <c>public partial</c> members.
///         </item>
///         <item>
///             <b><c>generated/sdk/*.cs</c> is not one compilation and must never become one.</b>
///             <c>SdkEmitter.Namespace</c> is <c>CyberCloud.Sdk.Generated</c> with no api-version in
///             it, and <c>FileNameOf</c> is <c>{api-version}.cs</c> — so the day a second
///             api-version is published, two files declare <c>ClickHouseClusterData</c> in one
///             namespace. A <c>&lt;Compile Include="generated/sdk/*.cs" /&gt;</c> would be
///             <c>CS0101</c> on every type in the SDK, and the fix somebody would reach for is to
///             include only the newest file — which is the older api-versions going unchecked, one
///             release after the gate was added. ONE COMPILATION PER FILE is the correct shape and
///             is what this does.
///         </item>
///     </list>
///     <para>
///         ⚠ <b><c>CS8795</c> is the only diagnostic accepted, and accepting it is a claim about
///         this surface rather than a suppression.</b> The claim is "half of every partial member is
///         hand-written and is not here", which is exactly what docs/plan/21 § Generation says and
///         exactly what makes the file a DECLARATION surface — the same thing <c>tsc</c> checks over
///         the TypeScript client, which has no bodies to check either. Nothing else is accepted:
///         <c>CS0101</c>, <c>CS0102</c>, <c>CS0246</c> and <c>CS0542</c> — the four shapes a naming
///         bug in an emitter actually takes — all reach the gate. <see cref="GeneratedSdkFile.Declared" />
///         counts what was accepted so the log says how large the exemption is rather than hiding it.
///         ⚠ <b>What makes this stale:</b> the day <c>SdkEmitter</c> emits method bodies, the count
///         goes to zero and this exemption should be deleted rather than left standing over a file
///         that no longer needs it.
///     </para>
///     <para>
///         ⚠ <b>Errors only, not warnings, and the repository's "warnings are errors" rule does not
///         reach here.</b> That rule is about code this tree owns; the question this gate answers is
///         the one nothing answered — "would a consumer's compiler accept this file". Which analysers
///         a consuming project runs, and at what severity, is that project's business, and a gate
///         that failed on a style rule would be a gate the next person turns off.
///     </para>
///     <para>
///         ✔ <b>Verified by breaking it, on 2026-09-05, SDK 10.0.400 with Roslyn 5.6.0 — the pinned
///         <c>Microsoft.CodeAnalysis.CSharp</c> this class compiles with, which is NOT that SDK's own
///         <c>csc</c> (10.0.400 ships 5.9.0-1.26379.115; issue #80).</b> Two probes
///         appended to <c>generated/sdk/2026-08-01.cs</c> and reverted: a second
///         <c>public enum ValkeyCacheMode</c> gave
///         <c>✘ Generated SDK compiles Failed … line 4598: CS0101 The namespace
///         'CyberCloud.Sdk.Generated' already contains a definition for 'ValkeyCacheMode'</c>, and a
///         property typed <c>ListKeysResultSecurityProtocol</c> gave the matching <c>CS0246</c> —
///         the two defects issue #73 was filed about, both now red. A clean tree is
///         <c>✔ … 1 api-version file(s) declaring 140 type(s) … 170 partial member(s) accepted</c>.
///         ⚠ Both numbers are worth reading rather than skipping: <b>0 types</b> is a file that
///         parsed to nothing and a tick that means nothing (the gate fails on it), and
///         <b>0 accepted members</b> is the emitter having started to write method bodies, at which
///         point the <c>CS8795</c> exemption below is stale and should be deleted.
///     </para>
///     <para>
///         ⚠ <b>Reads <c>CyberCloud.Sdk.dll</c> as a metadata reference and never as a project
///         reference, and the distinction is a rule-7 one.</b> A <c>ProjectReference</c> from
///         anything under <c>src/</c> to <c>CyberCloud.Sdk</c> would be an edge from the module that
///         SERVES the API to the module that CALLS it, needing a line in <c>module-layering.txt</c>
///         that a reviewer should refuse. <c>build/_build.csproj</c> is deliberately outside
///         <c>CyberCloud.slnx</c> and outside the shipping graph, so what it reads off disk is not an
///         edge at all — the same reason <c>ArchitectureFacts.Read</c> reads assemblies rather than
///         loading them.
///     </para>
/// </remarks>
static class GeneratedSdkSurface
{
    /// <summary>
    ///     A partial member declared without its implementing half — the one accepted diagnostic.
    /// </summary>
    const string PartialWithoutImplementation = "CS8795";

    /// <summary>
    ///     Compiles one generated file on its own, against the shared framework plus the assemblies
    ///     given.
    /// </summary>
    /// <param name="file">The file, which must exist.</param>
    /// <param name="references">
    ///     Assemblies the file's types resolve against — <c>CyberCloud.Sdk</c>, in practice. The
    ///     shared framework is added here rather than by the caller, because the caller getting it
    ///     wrong would look like a defect in the generated file.
    /// </param>
    public static GeneratedSdkFile Compile(AbsolutePath file, IEnumerable<AbsolutePath> references)
    {
        // ⚠ LanguageVersion.Latest, matching Directory.Build.props § "Target framework and
        // language". Roslyn's default here is not the SDK's default, and a gate that compiled the
        // generated file under an older language than the tree builds with would fail on a feature
        // the emitter is entitled to use.
        var tree = CSharpSyntaxTree.ParseText(
            file.ReadAllText(),
            new CSharpParseOptions(LanguageVersion.Latest),
            path: file);

        var metadata = ReferenceAssemblies()
            .Concat(references.Where(x => x.FileExists()).Select(x => x.ToString()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "CyberCloud.Sdk.Generated.Probe",
            [tree],
            metadata,
            // DynamicallyLinkedLibrary: there is no entry point in a generated SDK and asking for
            // one would be a CS5001 that says nothing about the file.
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics();

        var errors = diagnostics
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Where(x => !string.Equals(x.Id, PartialWithoutImplementation, StringComparison.Ordinal))
            .OrderBy(x => x.Location.SourceSpan.Start)
            .Select(Describe)
            .ToList();

        return new GeneratedSdkFile(
            file.Name,
            tree.GetRoot().DescendantNodes().Count(IsTypeDeclaration),
            diagnostics.Count(x => string.Equals(x.Id, PartialWithoutImplementation, StringComparison.Ordinal)),
            errors);
    }

    /// <summary>
    ///     A diagnostic as a line somebody can act on: the id, the 1-based line, and the message.
    /// </summary>
    /// <remarks>
    ///     ⚠ The line number is the whole value of reporting these individually. <c>generated/</c> is
    ///     read-only (its README says so), so the reader's next move is to find the emitter branch
    ///     that wrote that line — and "somewhere in a 250 KB generated file" is not a starting point.
    /// </remarks>
    static string Describe(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;

        return $"line {line}: {diagnostic.Id} {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    static bool IsTypeDeclaration(SyntaxNode node)
        => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax;

    /// <summary>
    ///     The shared framework, taken from the list this process was started with.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>TRUSTED_PLATFORM_ASSEMBLIES</c>, not <c>AppDomain.CurrentDomain.GetAssemblies()</c>,
    ///     and <c>CyberCloud.Kubernetes.Contracts.Tests/CompileFailureTests</c> paid for that
    ///     lesson.</b> The second is whatever the runtime happens to have loaded by the time it is
    ///     called, which depends on what ran before — and a missing framework assembly does not fail
    ///     loudly here, it turns every type in the generated file into <c>CS0246</c> and the gate
    ///     into a wall of noise about the wrong thing. The trusted-platform list is fixed at process
    ///     start.
    ///     <para>
    ///         The build host is <c>net10.0</c> and so is every project in the tree
    ///         (Directory.Build.props § "Target framework and language"), so its own framework is the
    ///         one the generated file should be checked against.
    ///     </para>
    /// </remarks>
    static ImmutableArray<string> ReferenceAssemblies()
        => AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted
            ? [.. trusted.Split(System.IO.Path.PathSeparator).Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))]
            : [];
}
