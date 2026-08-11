using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CyberCloud.Analyzers;

/// <summary>
///     CC1003 — a <c>[GenerateSerializer]</c> type that carries no <c>[Alias]</c>.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Coding standards ("An analyzer fails a <c>[GenerateSerializer]</c> type
///         with no <c>[Alias]</c>") and docs/plan/04 § Failure and upgrade, whose own correction note
///         says the claim was <i>not</i> true and that the gate was a reflection test —
///         <c>CyberCloud.Core.Contracts.Tests.WireContractTests.EveryGenerateSerializerTypeHasAnAlias</c>
///         and its <c>CyberCloud.Tenancy.Contracts.Tests</c> sibling.
///     </para>
///     <para>
///         <b>What moving it into the compiler buys.</b> The reflection tests each cover exactly one
///         assembly, and only a <c>.Contracts</c> one — <c>CyberCloud.Tenancy</c>'s seven grain-state
///         types in <c>TenancyState.cs</c> carry <c>[GenerateSerializer]</c> and were covered by
///         neither. Every assembly that references this analyzer is covered, and the failure arrives
///         at the line rather than in a test log. The reflection tests stay: they also assert the
///         alias <i>strings</i>, which is a different claim (a rename must not change them) and one a
///         compile-time rule cannot make.
///     </para>
///     <para>
///         ⚠ Inert in an assembly with no Orleans reference. <c>CyberCloud.Core</c> has none by
///         design (docs/plan/03 § Assembly graph rules, rule 1), so
///         <c>GetTypeByMetadataName("Orleans.GenerateSerializerAttribute")</c> returns
///         <see langword="null" /> there and nothing is registered.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GenerateSerializerAliasAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.GenerateSerializerWithoutAlias);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var generateSerializer = context.Compilation.GetTypeByMetadataName(WellKnown.GenerateSerializerAttribute);
        var alias = context.Compilation.GetTypeByMetadataName(WellKnown.AliasAttribute);

        if (generateSerializer is null || alias is null)
        {
            return;
        }

        context.RegisterSymbolAction(
            symbolContext => AnalyzeType(symbolContext, generateSerializer, alias),
            SymbolKind.NamedType);
    }

    static void AnalyzeType(
        SymbolAnalysisContext context,
        INamedTypeSymbol generateSerializer,
        INamedTypeSymbol alias)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (!HasAttribute(type, generateSerializer) || HasAttribute(type, alias))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.GenerateSerializerWithoutAlias,
            type.Locations.Length > 0 ? type.Locations[0] : Location.None,
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
    {
        foreach (var applied in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(applied.AttributeClass, attribute))
            {
                return true;
            }
        }

        return false;
    }
}
