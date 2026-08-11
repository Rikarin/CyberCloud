using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CyberCloud.Analyzers;

/// <summary>
///     CC1005 — an <c>[Id(n)]</c>-annotated member whose name says it holds a secret.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Non-negotiables, the "Secrets never reach grain state" row, verbatim:
///         <i>"Analyzer bans <c>[Id]</c>-annotated members named <c>*Password</c>, <c>*Secret</c>,
///         <c>*Token</c>, <c>*Key</c> outside <c>CyberCloud.Vault</c>; secrets are <c>SecretRef</c>
///         handles resolved at the data plane."</i> The suffix list is that sentence, not a
///         judgement call — widening or narrowing it is a change to the non-negotiable and belongs
///         in the document first.
///     </para>
///     <para>
///         <b>Why <c>[Id(n)]</c> is the trigger and not the type.</b> <c>[Id(n)]</c> is what puts a
///         member on the wire and in the durable tier. A plain field on a grain-state class without
///         one is not serialized and is not the hazard; a member with one is written to PostgreSQL,
///         replicated to every silo that deserializes the payload, and captured in every backup.
///     </para>
///     <para>
///         ⚠ <b><c>*Key</c> is the loose one and it is loose on purpose.</b> <c>HmacKey</c> and
///         <c>PartitionKey</c> are indistinguishable to a name-matching rule, and the plan chose the
///         name rule knowingly. The matching is <b>case-sensitive and ordinal</b>, which is what
///         keeps <c>Monkey</c> and <c>Turkey</c> out; <c>PascalCase</c> on anything a caller can name
///         is itself an error in <c>.editorconfig</c>, so the final word of a member name is always
///         capitalised and the suffix match is a word match. A legitimate <c>PartitionKey</c> is
///         answered with a <c>[SuppressMessage("CyberCloud.Security", "CC1005", Justification = …)]</c>
///         carrying the argument — which is the outcome the non-negotiable wants, because it makes
///         somebody write down why a thing called a key is not a secret.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SecretInGrainStateAnalyzer : DiagnosticAnalyzer
{
    /// <summary>docs/plan/00 § Non-negotiables, exactly as written there.</summary>
    static readonly string[] SecretSuffixes = ["Password", "Secret", "Token", "Key"];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.SecretInGrainState);

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
        // ⚠ The vault is the one assembly allowed to hold a secret value. It does not exist yet
        // (docs/plan/18); writing the exemption now means the rule does not have to be revisited
        // when it lands, and means an assembly cannot opt out by being named CyberCloud.VaultThing.
        var assembly = context.Compilation.AssemblyName;
        if (string.Equals(assembly, WellKnown.VaultAssembly, StringComparison.Ordinal)
            || (assembly is not null && assembly.StartsWith(WellKnown.VaultAssembly + ".", StringComparison.Ordinal)))
        {
            return;
        }

        var idAttribute = context.Compilation.GetTypeByMetadataName(WellKnown.IdAttribute);
        if (idAttribute is null)
        {
            return;
        }

        context.RegisterSymbolAction(
            symbolContext => AnalyzeMember(symbolContext, idAttribute),
            SymbolKind.Property,
            SymbolKind.Field);
    }

    static void AnalyzeMember(SymbolAnalysisContext context, INamedTypeSymbol idAttribute)
    {
        var member = context.Symbol;

        var suffix = SecretSuffix(member.Name);
        if (suffix is null || !HasIdAttribute(member, idAttribute))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.SecretInGrainState,
            member.Locations.Length > 0 ? member.Locations[0] : Location.None,
            member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            suffix));
    }

    /// <summary>
    ///     The banned suffix this name ends with, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     Ordinal and case-sensitive. A name that <i>is</i> the suffix ("Token") counts too — the
    ///     plan's <c>*Password</c> notation is a suffix glob, not a "must have a prefix" rule.
    /// </remarks>
    static string? SecretSuffix(string name)
    {
        foreach (var suffix in SecretSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return suffix;
            }
        }

        return null;
    }

    static bool HasIdAttribute(ISymbol symbol, INamedTypeSymbol idAttribute)
    {
        foreach (var applied in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(applied.AttributeClass, idAttribute))
            {
                return true;
            }
        }

        // A primary-constructor parameter or a positional record member carries the attribute on the
        // parameter, and the generated property inherits nothing. Orleans reads the attribute from
        // the field the property is backed by, so check that too.
        if (symbol is IPropertySymbol { GetMethod: not null } property
            && property.ContainingType is not null)
        {
            foreach (var candidate in property.ContainingType.GetMembers())
            {
                if (candidate is IFieldSymbol field
                    && SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property))
                {
                    foreach (var applied in field.GetAttributes())
                    {
                        if (SymbolEqualityComparer.Default.Equals(applied.AttributeClass, idAttribute))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }
}
