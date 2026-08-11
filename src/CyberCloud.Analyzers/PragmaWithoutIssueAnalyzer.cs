using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace CyberCloud.Analyzers;

/// <summary>
///     CC1007 — <c>#pragma warning disable</c> with nothing linking it to a tracked issue.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Non-negotiables, the "Warnings are errors" row: <c>TreatWarningsAsErrors</c>,
///         nullable enabled, <i>"no <c>#pragma warning disable</c> without a linked issue"</i>.
///         <c>.editorconfig</c> records that no analyzer, built-in or otherwise, reports this and
///         that it needs a custom rule or a CI grep. This is the custom rule; it beats the grep
///         because it understands <c>restore</c>, string literals and comment placement.
///     </para>
///     <para>
///         <b>What counts as a link,</b> in a comment on the same line as the pragma or on the line
///         immediately above it:
///     </para>
///     <list type="bullet">
///         <item>a URL — <c>https://github.com/Rikarin/CyberCloud/issues/42</c>;</item>
///         <item>a hash reference — <c>#42</c>;</item>
///         <item>a tracker key — <c>CC-42</c>, <c>OPS-1234</c>.</item>
///     </list>
///     <para>
///         <b>What is deliberately not covered.</b> <c>#pragma warning restore</c> — restoring is the
///         good half. And <c>&lt;NoWarn&gt;</c> in a project file, which is MSBuild rather than C#:
///         this repository's existing suppressions are all of that kind, each argued in a comment
///         beside it (<c>CyberCloud.Core.csproj</c> says so in as many words:
///         <i>
///             "These are the ONLY
///             two suppressions in this assembly and neither is a <c>#pragma</c>"
///         </i>
///         ). Making the
///         project-file half a gate is a build-target job, not an analyzer's, and it is not claimed
///         here.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PragmaWithoutIssueAnalyzer : DiagnosticAnalyzer {
    /// <summary>A URL, a <c>#123</c>, or a <c>PROJ-123</c> tracker key.</summary>
    static readonly Regex IssueLink = new(
        @"https?://\S+|#\d+|\b[A-Z][A-Z0-9]+-\d+\b",
        RegexOptions.CultureInvariant
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.PragmaWithoutIssue);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        if (context is null) {
            return;
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // ⚠ RegisterSyntaxTreeAction, not RegisterSyntaxNodeAction. A pragma directive is
        // *structured trivia*, and whether the driver's node walk descends into trivia is an
        // implementation detail of the driver rather than a documented guarantee. Walking the tree
        // with descendIntoTrivia: true asks for it explicitly.
        context.RegisterSyntaxTreeAction(Analyze);
    }

    static void Analyze(SyntaxTreeAnalysisContext context) {
        var root = context.Tree.GetRoot(context.CancellationToken);

        foreach (var node in root.DescendantNodes(descendIntoTrivia: true)) {
            if (node is not PragmaWarningDirectiveTriviaSyntax directive
                || !directive.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)
                || HasIssueLink(directive)) {
                continue;
            }

            var codes = string.Join(", ", directive.ErrorCodes.Select(x => x.ToString()));

            context.ReportDiagnostic(
                Diagnostic.Create(
                    Rules.PragmaWithoutIssue,
                    directive.GetLocation(),
                    codes.Length == 0 ? "(everything)" : codes
                )
            );
        }
    }

    /// <summary>
    ///     Looks for a link in a comment trailing the directive, then in the comment above it.
    /// </summary>
    /// <remarks>
    ///     A pragma directive is itself trivia, so both neighbours are found through the token the
    ///     directive hangs off rather than through sibling nodes. The trailing half is the common
    ///     spelling — <c>#pragma warning disable CA1822 // https://…</c> — and the leading half
    ///     covers the case where the justification needs a line of its own.
    /// </remarks>
    static bool HasIssueLink(PragmaWarningDirectiveTriviaSyntax directive) {
        // Trailing: the comment on the directive's own line.
        //
        // ⚠ It is inside the directive node, NOT on EndOfDirectiveToken as one would expect —
        // verified against Roslyn 5.6.0, where for `#pragma warning disable CA1822 // link\n`:
        //     directive.ToFullString()                 = "#pragma warning disable CA1822 // link\n"
        //     directive.EndOfDirectiveToken.ToFullString() = "\n"
        // So the whole node's text is what has to be searched. Nothing a valid pragma directive can
        // contain matches IssueLink on its own: an error code is `CA1822` or a bare number, neither
        // of which has a hyphen-digit tail, and the leading `#` of `#pragma` is followed by a letter
        // rather than a digit.
        if (IssueLink.IsMatch(directive.ToFullString())) {
            return true;
        }

        // Leading: the trivia that precedes the directive on the lines above, up to the previous
        // non-comment line. Taking only comment trivia is what stops a URL in unrelated code from
        // counting.
        var parentTrivia = directive.ParentTrivia;
        var token = parentTrivia.Token;
        var directiveIndex = -1;
        var leading = token.LeadingTrivia;

        for (var i = 0; i < leading.Count; i++) {
            if (leading[i] == parentTrivia) {
                directiveIndex = i;
                break;
            }
        }

        for (var i = directiveIndex - 1; i >= 0; i--) {
            var trivia = leading[i];

            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia)) {
                continue;
            }

            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) {
                return IssueLink.IsMatch(trivia.ToFullString());
            }

            return false;
        }

        return false;
    }
}
