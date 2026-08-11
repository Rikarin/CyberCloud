using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace CyberCloud.Analyzers;

/// <summary>
///     CC1004 — a string literal containing <c>|</c> passed to <c>GetGrain</c>.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/02 § ADR-002, verbatim:
///         <i>
///             "Nothing else in the codebase may concatenate a grain
///             key, enforced by an analyzer that flags string literals containing <c>|</c> in
///             <c>GetGrain</c> arguments."
///         </i>
///     </para>
///     <para>
///         <b>Why <c>|</c> specifically.</b> It is <c>Orleans.Multitenant</c>'s tenant separator: the
///         physical key is <c>{tenantId}|{keyWithinTenant}</c>, and ADR-002 records that the inner
///         key is copied <i>verbatim</i> — the doubling that escapes a <c>|</c> applies to the tenant
///         id only. A literal key containing one is therefore either a hand-forged tenant
///         qualification or a key that the parser will read as one. <c>GrainKeys</c> is the single
///         type allowed to format a key, and
///         <c>GrainKeysTests.EveryGeneratedKeyIsSafeForTenantQualification</c> is what guarantees
///         nothing it produces contains a <c>|</c>.
///     </para>
///     <para>
///         <b>Interpolated strings count.</b> <c>$"{tenant}|{key}"</c> is the same defect with a
///         different spelling, and it is the one somebody actually writes. Only the literal text
///         between the holes is inspected, so a <c>|</c> that arrives at run time through an
///         interpolation hole is out of reach here — that is what <c>GrainKeys.TryParse</c> and the
///         tenant-qualification-safety test are for.
///     </para>
///     <para>
///         <b>The negative case.</b> A perfectly ordinary <c>GetGrain(GrainKeys.Resource(id))</c> or
///         <c>GetGrain("res/…")</c> is silent, and so is any method called <c>GetGrain</c> on a type
///         that is not a grain factory.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GrainKeyLiteralAnalyzer : DiagnosticAnalyzer {
    const char TenantSeparator = '|';

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.GrainKeyLiteral);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        if (context is null) {
            return;
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    static void OnCompilationStart(CompilationStartAnalysisContext context) {
        var grainFactory = context.Compilation.GetTypeByMetadataName(WellKnown.GrainFactory);
        if (grainFactory is null) {
            return;
        }

        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    static void AnalyzeInvocation(OperationAnalysisContext context) {
        var operation = (IInvocationOperation)context.Operation;

        if (!GrainFactories.IsGetGrain(operation.TargetMethod)) {
            return;
        }

        foreach (var argument in operation.Arguments) {
            var offending = OffendingLiteral(argument.Value);
            if (offending is null) {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    Rules.GrainKeyLiteral,
                    offending.Syntax.GetLocation(),
                    operation.TargetMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }
    }

    /// <summary>
    ///     The literal or interpolated string whose <i>text</i> carries a <c>|</c>, or
    ///     <see langword="null" />.
    /// </summary>
    static IOperation? OffendingLiteral(IOperation value) {
        // An implicit conversion (string -> object for the Type-based overloads) wraps the literal.
        while (value is IConversionOperation conversion && conversion.Operand is not null) {
            value = conversion.Operand;
        }

        switch (value) {
            case ILiteralOperation literal
                when literal.ConstantValue.HasValue
                && literal.ConstantValue.Value is string text
                && text.IndexOf(TenantSeparator) >= 0:
                return literal;

            case IInterpolatedStringOperation interpolated:
                foreach (var part in interpolated.Parts) {
                    if (part is IInterpolatedStringTextOperation { Text: ILiteralOperation piece }
                        && piece.ConstantValue.HasValue
                        && piece.ConstantValue.Value is string chunk
                        && chunk.IndexOf(TenantSeparator) >= 0) {
                        return interpolated;
                    }
                }

                return null;

            default:
                return null;
        }
    }
}
