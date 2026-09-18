using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using System.Collections.Immutable;

namespace CyberCloud.Analyzers.Tests;

/// <summary>
///     Runs one analyzer over one snippet and checks what it said, and where.
/// </summary>
/// <remarks>
///     <para>
///         <b>Locations are asserted through markup, not by eye.</b> A snippet wraps the offending
///         span in <c>{|CC1001:…|}</c>; the harness turns that into an expected diagnostic with an
///         exact span, and an analyzer that reports the right id at the wrong place fails. A snippet
///         with no markup asserts the opposite: <i>nothing</i> was reported anywhere in it.
///     </para>
///     <para>
///         <b>The negative tests are the important ones.</b> Each analyzer's file has more of them
///         than positive ones, on purpose: a rule that fires on correct code is suppressed
///         wholesale, and then it protects nothing. Every exemption written into an analyzer has a
///         test here that would fail if the exemption were removed.
///     </para>
///     <para>
///         <b>
///             References come from this process, and that is a deliberate departure from the
///             harness's default.
///         </b>
///         <c>Microsoft.CodeAnalysis.Testing</c> normally wants a
///         <see cref="ReferenceAssemblies" /> preset — <c>ReferenceAssemblies.Net.Net100</c> — and
///         resolves it by <b>restoring <c>Microsoft.NETCore.App.Ref</c> from NuGet at test time</b>.
///         That works, and it puts a network round trip inside a gate that docs/plan/23 § CI shape
///         runs on every PR under a three-minute budget, on a machine that is cold every time. A
///         test suite that can fail because nuget.org is slow is not a gate.
///     </para>
///     <para>
///         So the preset is replaced with an empty <see cref="ReferenceAssemblies" /> and the
///         references are the test host's own <c>TRUSTED_PLATFORM_ASSEMBLIES</c> — the exact list
///         this process was launched with. It is offline, deterministic, and strictly more faithful
///         than a reference pack: a snippet compiles against the real shared framework, the real
///         <c>Orleans</c>, the real <c>Orleans.Multitenant</c> and the real <c>CyberCloud.Core</c>,
///         at the versions <c>Directory.Packages.props</c> pins.
///     </para>
/// </remarks>
static class AnalyzerHarness {
    /// <summary>
    ///     An empty preset. Everything is supplied through <see cref="Platform" /> instead; see the
    ///     remarks on this class for why.
    /// </summary>
    /// <remarks>
    ///     The single-argument <see cref="ReferenceAssemblies" /> constructor names a target
    ///     framework and carries no reference-assembly package, so nothing is restored.
    /// </remarks>
    static readonly ReferenceAssemblies NoPresetPackages = new("net10.0");

    /// <summary>Every assembly this test host was launched with.</summary>
    static readonly ImmutableArray<MetadataReference> Platform = LoadPlatformAssemblies();

    /// <summary>Asserts that <paramref name="source" /> produces exactly the marked-up diagnostics.</summary>
    /// <typeparam name="TAnalyzer">The analyzer under test.</typeparam>
    /// <param name="source">C# with <c>{|CC1234:span|}</c> markup around each expected report.</param>
    /// <param name="assemblyName">
    ///     The compilation's assembly name. Only CC1005 cares — <c>CyberCloud.Vault</c> is exempt
    ///     from it, and that exemption needs a test.
    /// </param>
    /// <param name="skipSuppressionCheck">See the comment inside; only CC1007 sets it.</param>
    public static Task ReportsAsync<TAnalyzer>(
        string source,
        string? assemblyName = null,
        bool skipSuppressionCheck = false
    )
        where TAnalyzer : DiagnosticAnalyzer, new() {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> {
            TestCode = source, ReferenceAssemblies = NoPresetPackages
        };

        if (skipSuppressionCheck) {
            // ⚠ Only CC1007 needs this, and the reason is a genuine circularity rather than a
            // convenience. After checking the expected diagnostics, the harness re-runs the analyzer
            // over the same source with `#pragma warning disable <ID>` injected, to prove the rule
            // is suppressible. CC1007's subject IS `#pragma warning disable`, so it reports the
            // harness's own injected line — a diagnostic about a suppression the test never wrote.
            // Suppressibility is still real and still exercised: the injected pragma does silence
            // the diagnostics the test expected.
            test.TestBehaviors |= TestBehaviors.SkipSuppressionCheck;
        }

        test.TestState.AdditionalReferences.AddRange(Platform);

        if (assemblyName is not null) {
            test.SolutionTransforms.Add((solution, projectId) =>
                solution.WithProjectAssemblyName(projectId, assemblyName)
            );
        }

        return test.RunAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Asserts that <paramref name="source" /> produces <b>no</b> diagnostic from
    ///     <typeparamref name="TAnalyzer" />. Identical to <see cref="ReportsAsync{TAnalyzer}" />
    ///     with unmarked source; it exists so the intent is legible at the call site.
    /// </summary>
    /// <typeparam name="TAnalyzer">The analyzer under test.</typeparam>
    /// <param name="source">C# with no markup.</param>
    /// <param name="assemblyName">The compilation's assembly name, when it matters.</param>
    public static Task IsSilentAsync<TAnalyzer>(string source, string? assemblyName = null)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        ReportsAsync<TAnalyzer>(source, assemblyName);

    /// <summary>
    ///     <see cref="ReportsAsync{TAnalyzer}" /> with the harness's suppression re-run turned off —
    ///     see the comment inside it. Used only by CC1007's tests.
    /// </summary>
    /// <typeparam name="TAnalyzer">The analyzer under test.</typeparam>
    /// <param name="source">C# with <c>{|CC1234:span|}</c> markup around each expected report.</param>
    public static Task ReportsWithoutSuppressionCheckAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        ReportsAsync<TAnalyzer>(source, null, true);

    /// <summary>
    ///     The test host's <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, as metadata references.
    /// </summary>
    /// <remarks>
    ///     This is the assembly-closure the runtime resolved for this process: the shared framework
    ///     plus every package and project reference. It cannot contain a duplicate identity and
    ///     cannot contain an assembly that is not loadable, both of which a hand-rolled directory
    ///     sweep can.
    /// </remarks>
    static ImmutableArray<MetadataReference> LoadPlatformAssemblies() {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted) {
            throw new InvalidOperationException(
                "TRUSTED_PLATFORM_ASSEMBLIES is unset, so there is nothing for an analysed snippet "
                + "to compile against. This runs on the default (non-single-file) host; if that "
                + "changed, supply references another way rather than falling back to a NuGet "
                + "restore at test time."
            );
        }

        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var path in trusted.Split(Path.PathSeparator)) {
            if (path.Length > 0 && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return references.ToImmutable();
    }
}
