using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace CyberCloud.Analyzers;

/// <summary>
///     CC1002 — <c>async void</c>, in every form a C# file can spell it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Coding standards bans it outright. <c>.editorconfig</c> § "The docs/plan/00
///         bans" records that nothing in the SDK reports it — the rule people reach for is
///         <c>VSTHRD100</c>, in <c>Microsoft.VisualStudio.Threading.Analyzers</c>, a package this
///         repository does not reference because it arrives with twenty other opinions.
///     </para>
///     <para>
///         <b>Three declaration forms, all covered:</b> methods, local functions, and lambdas or
///         anonymous methods converted to a void-returning delegate. The lambda case matters most,
///         because it is the one that gets written by accident:
///         <c>
///             timer.Elapsed += async (s, e) =>
///             …
///         </c>
///         compiles to <c>async void</c> and nothing in the source says so.
///     </para>
///     <para>
///         <b>The negative case is the delegate's return type, not the lambda's body.</b>
///         <c>Task.Run(async () =&gt; …)</c> and
///         <c>
///             await Parallel.ForEachAsync(…, async (x, ct) =&gt;
///             …)
///         </c>
///         are correct and extremely common; they bind to <c>Func&lt;Task&gt;</c> and
///         <c>Func&lt;…, ValueTask&gt;</c>, not to <c>Action</c>. Asking the semantic model for the
///         converted delegate is what tells the two apart.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncVoidAnalyzer : DiagnosticAnalyzer {
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.AsyncVoid);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        if (context is null) {
            return;
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
        context.RegisterSyntaxNodeAction(
            AnalyzeAnonymousFunction,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.AnonymousMethodExpression
        );
    }

    static void AnalyzeMethod(SyntaxNodeAnalysisContext context) {
        var declaration = (MethodDeclarationSyntax)context.Node;
        if (!declaration.Modifiers.Any(SyntaxKind.AsyncKeyword)
            || !IsVoid(declaration.ReturnType, context)) {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rules.AsyncVoid,
                declaration.Identifier.GetLocation(),
                declaration.Identifier.ValueText
            )
        );
    }

    static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context) {
        var declaration = (LocalFunctionStatementSyntax)context.Node;
        if (!declaration.Modifiers.Any(SyntaxKind.AsyncKeyword)
            || !IsVoid(declaration.ReturnType, context)) {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rules.AsyncVoid,
                declaration.Identifier.GetLocation(),
                declaration.Identifier.ValueText
            )
        );
    }

    static void AnalyzeAnonymousFunction(SyntaxNodeAnalysisContext context) {
        var node = (AnonymousFunctionExpressionSyntax)context.Node;
        if (node.AsyncKeyword.IsKind(SyntaxKind.None)) {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol
                is not IMethodSymbol lambda
            || !lambda.ReturnsVoid) {
            return;
        }

        // The enclosing member's name is the only useful handle a lambda has; "a lambda in Foo"
        // beats an empty name in an error list.
        var owner = context.ContainingSymbol?.Name;
        context.ReportDiagnostic(
            Diagnostic.Create(
                Rules.AsyncVoid,
                node.AsyncKeyword.GetLocation(),
                string.IsNullOrEmpty(owner) ? "an async lambda" : "an async lambda in '" + owner + "'"
            )
        );
    }

    /// <summary>
    ///     Asks the semantic model rather than matching the token <c>void</c>, so an alias
    ///     (<c>using Nothing = System.Void;</c>) or a partial-method return type spelled differently
    ///     is still recognised.
    /// </summary>
    static bool IsVoid(TypeSyntax returnType, SyntaxNodeAnalysisContext context) =>
        context.SemanticModel.GetTypeInfo(returnType, context.CancellationToken).Type?.SpecialType
        == SpecialType.System_Void;
}
