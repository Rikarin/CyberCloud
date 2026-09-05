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
///         byte-identical is not valid.</b> Three defects shipped in
///         <c>generated/sdk/2026-08-01.cs</c> — <c>CS0101</c> from a duplicated enum name,
///         <c>CS0246</c> from an action's enum that was referenced and never declared, and
///         <c>CS0102</c> from fourteen duplicated property names across six resource types — every
///         one of them green under every gate in this repository, because no <c>.csproj</c> includes
///         that file and nothing else handed it to a compiler. The first two were found by running
///         <c>tsc</c> over a DIFFERENT surface and then reading the C# one by eye; the third was
///         found by this file, on the day it was written.
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
///             cannot demote it — so a project including this file fails on 140 of them. Filtering
///             one diagnostic id here is a line of code with an argument beside it; supplying 140
///             hand-written stubs, and one more for every method the emitter ever gains, is a second
///             surface that drifts.
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
///         ✔ <b>Verified by breaking it, on 2026-09-05, SDK 10.0.400 / Roslyn 5.6.0.</b> Two probes
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
