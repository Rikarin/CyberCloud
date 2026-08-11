using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CyberCloud.Analyzers;

/// <summary>
///     CC1001 — <c>.Result</c>, <c>.Wait()</c> or <c>.GetAwaiter().GetResult()</c> reached from a
///     method that is not <c>async</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists when CA1849 is already on as an error.</b> CA1849 ("call async methods
///         when in an async method") fires only when the blocking call is inside a method that is
///         <i>already</i> <c>async</c> — where the fix is mechanical and the deadlock is unlikely,
///         because there is no synchronization context to deadlock against. The shape that stalls an
///         Orleans activation is the opposite one: a <b>synchronous</b> method that blocks on a task.
///         <c>.editorconfig</c> § "The docs/plan/00 bans" says so in as many words, and names this
///         analyzer as the missing half. This rule therefore fires <i>only</i> in a synchronous
///         context, so it never double-reports with CA1849.
///     </para>
///     <para>
///         <b>Two exemptions, both for shapes that provably do not block, both found in this
///         repository.</b> An analyzer that fires on correct code gets suppressed wholesale and then
///         protects nothing, so each is narrow and each has a negative test:
///     </para>
///     <list type="number">
///         <item>
///             <b>The <c>ContinueWith</c> antecedent.</b> Inside a continuation delegate the
///             antecedent task is complete by contract, and <c>t.GetAwaiter().GetResult()</c> is the
///             idiomatic way to re-raise its exception unwrapped rather than as an
///             <c>AggregateException</c>. Exempted only when the receiver is the continuation
///             delegate's own parameter.
///             <c>CyberCloud.ServiceDefaults.Storage.HotTierConfigurator</c>'s constructor is this.
///         </item>
///         <item>
///             <b>The guarded read.</b> <c>if (t.IsCompletedSuccessfully) { … t.Result … }</c> cannot
///             block, because the task has already completed. Exempted only when the guard tests the
///             syntactically same expression.
///             <c>HotTierConfigurator.Dispose</c> is this.
///         </item>
///     </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingWaitAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.BlockingWaitInSynchronousMethod);

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
        var compilation = context.Compilation;

        var taskTypes = new[]
            {
                compilation.GetTypeByMetadataName(WellKnown.Task),
                compilation.GetTypeByMetadataName(WellKnown.TaskOfT),
                compilation.GetTypeByMetadataName(WellKnown.ValueTask),
                compilation.GetTypeByMetadataName(WellKnown.ValueTaskOfT),
            }
            .Where(x => x is not null)
            .Select(x => x!)
            .ToImmutableArray();

        if (taskTypes.IsEmpty)
        {
            return;
        }

        context.RegisterOperationAction(
            operationContext => AnalyzeResultProperty(operationContext, taskTypes),
            OperationKind.PropertyReference);

        context.RegisterOperationAction(
            operationContext => AnalyzeInvocation(operationContext, taskTypes),
            OperationKind.Invocation);
    }

    // ── the three blocking shapes ────────────────────────────────────────────────────────────────

    static void AnalyzeResultProperty(
        OperationAnalysisContext context,
        ImmutableArray<INamedTypeSymbol> taskTypes)
    {
        var operation = (IPropertyReferenceOperation)context.Operation;

        // ⚠ The containing-type check is the whole of the negative case. `CyberCloud.Core.Result<T>`
        // exists in this repository, and anything with a property called `Result` — a parse result,
        // an HTTP response wrapper, a test double — must not be reported.
        if (!string.Equals(operation.Property.Name, "Result", StringComparison.Ordinal)
            || !IsTaskType(operation.Property.ContainingType, taskTypes))
        {
            return;
        }

        Report(context, operation, operation.Instance, "Task.Result");
    }

    static void AnalyzeInvocation(
        OperationAnalysisContext context,
        ImmutableArray<INamedTypeSymbol> taskTypes)
    {
        var operation = (IInvocationOperation)context.Operation;
        var method = operation.TargetMethod;

        // `task.Wait()` and its timeout overloads. ⚠ NOT SemaphoreSlim.Wait, ManualResetEventSlim.Wait
        // or Monitor.Wait — those are blocking primitives, not task-blocking, and the containing-type
        // check is what keeps them out.
        if (string.Equals(method.Name, "Wait", StringComparison.Ordinal)
            && IsTaskType(method.ContainingType, taskTypes))
        {
            Report(context, operation, operation.Instance, "Task.Wait()");
            return;
        }

        // `task.GetAwaiter().GetResult()`, in every spelling including
        // `.ConfigureAwait(false).GetAwaiter().GetResult()`. Matching on the awaiter type rather
        // than on the receiver chain is what makes the ConfigureAwait form impossible to miss.
        if (string.Equals(method.Name, "GetResult", StringComparison.Ordinal)
            && IsAwaiterType(method.ContainingType))
        {
            Report(context, operation, AwaitedExpression(operation.Instance), "GetAwaiter().GetResult()");
        }
    }

    // ── reporting, once the shape matches ────────────────────────────────────────────────────────

    static void Report(
        OperationAnalysisContext context,
        IOperation operation,
        IOperation? receiver,
        string what)
    {
        var enclosing = EnclosingFunction(operation, context.ContainingSymbol);
        if (enclosing.IsAsync)
        {
            // CA1849's territory, and it is already an error in .editorconfig. Reporting here too
            // would put two ids on one line and teach people to suppress both.
            return;
        }

        if (IsContinueWithAntecedent(receiver) || IsGuardedByCompletionCheck(operation, receiver))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.BlockingWaitInSynchronousMethod,
            operation.Syntax.GetLocation(),
            what,
            enclosing.Name));
    }

    // ── context ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The nearest enclosing function — lambda, local function, or the containing member.
    /// </summary>
    static (bool IsAsync, string Name) EnclosingFunction(IOperation operation, ISymbol containingSymbol)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case IAnonymousFunctionOperation anonymous:
                    return (anonymous.Symbol.IsAsync, containingSymbol.Name);

                case ILocalFunctionOperation local:
                    return (local.Symbol.IsAsync, local.Symbol.Name);
            }
        }

        return (containingSymbol is IMethodSymbol method && method.IsAsync, containingSymbol.Name);
    }

    /// <summary>
    ///     Exemption 1 — the receiver is the parameter of a <c>ContinueWith</c> continuation, so the
    ///     task it names is complete before the delegate runs.
    /// </summary>
    static bool IsContinueWithAntecedent(IOperation? receiver)
    {
        if (receiver is not IParameterReferenceOperation parameter)
        {
            return false;
        }

        // The parameter belongs to the lambda; the lambda is an argument to ContinueWith.
        for (var parent = receiver.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not IAnonymousFunctionOperation anonymous)
            {
                continue;
            }

            var owned = false;
            foreach (var candidate in anonymous.Symbol.Parameters)
            {
                owned |= SymbolEqualityComparer.Default.Equals(candidate, parameter.Parameter);
            }

            if (!owned)
            {
                return false;
            }

            for (var outer = anonymous.Parent; outer is not null; outer = outer.Parent)
            {
                if (outer is IInvocationOperation invocation)
                {
                    return string.Equals(invocation.TargetMethod.Name, "ContinueWith", StringComparison.Ordinal);
                }
            }

            return false;
        }

        return false;
    }

    /// <summary>
    ///     Exemption 2 — an enclosing <c>if</c> already established that this very expression's task
    ///     has completed, so reading it cannot block.
    /// </summary>
    static bool IsGuardedByCompletionCheck(IOperation operation, IOperation? receiver)
    {
        if (receiver?.Syntax is not ExpressionSyntax guarded)
        {
            return false;
        }

        for (var node = operation.Syntax.Parent; node is not null; node = node.Parent)
        {
            if (node is not IfStatementSyntax conditional)
            {
                continue;
            }

            foreach (var access in conditional.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            {
                if (IsCompletionProbe(access.Name.Identifier.ValueText)
                    && SyntaxFactory.AreEquivalent(access.Expression, guarded))
                {
                    return true;
                }
            }
        }

        return false;
    }

    static bool IsCompletionProbe(string name) =>
        string.Equals(name, "IsCompletedSuccessfully", StringComparison.Ordinal)
        || string.Equals(name, "IsCompleted", StringComparison.Ordinal);

    // ── type predicates ──────────────────────────────────────────────────────────────────────────

    static bool IsTaskType(INamedTypeSymbol? type, ImmutableArray<INamedTypeSymbol> taskTypes)
    {
        if (type is null)
        {
            return false;
        }

        var definition = type.OriginalDefinition;
        foreach (var candidate in taskTypes)
        {
            if (SymbolEqualityComparer.Default.Equals(definition, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     A <c>System.Runtime.CompilerServices.*Awaiter</c>. Matching the namespace and the suffix
    ///     covers <c>TaskAwaiter</c>, <c>TaskAwaiter&lt;T&gt;</c>, <c>ValueTaskAwaiter</c>,
    ///     <c>ConfiguredTaskAwaitable.ConfiguredTaskAwaiter</c> and the <c>ValueTask</c> equivalents
    ///     without naming eight types that Roslyn would then have to resolve one by one.
    /// </summary>
    static bool IsAwaiterType(INamedTypeSymbol? type) =>
        type is not null
        && type.Name.EndsWith("Awaiter", StringComparison.Ordinal)
        && string.Equals(
            type.ContainingNamespace?.ToDisplayString(),
            "System.Runtime.CompilerServices",
            StringComparison.Ordinal);

    /// <summary>
    ///     Unwraps <c>x.GetAwaiter()</c> and <c>x.ConfigureAwait(false)</c> back to <c>x</c>, so the
    ///     two exemptions can be tested against the task the caller actually named.
    /// </summary>
    static IOperation? AwaitedExpression(IOperation? receiver)
    {
        while (receiver is IInvocationOperation invocation
               && (string.Equals(invocation.TargetMethod.Name, "GetAwaiter", StringComparison.Ordinal)
                   || string.Equals(invocation.TargetMethod.Name, "ConfigureAwait", StringComparison.Ordinal)))
        {
            receiver = invocation.Instance;
        }

        return receiver;
    }
}
