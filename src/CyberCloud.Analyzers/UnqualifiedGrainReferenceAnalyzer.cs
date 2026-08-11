using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace CyberCloud.Analyzers;

/// <summary>
///     CC1006 — a raw <c>IGrainFactory.GetGrain</c> from code that is not a grain.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the security rule, and it closes the hole the plan says is open.</b>
///         docs/plan/00 § Non-negotiables, in the corrected tenant-separation section:
///         <i>
///             "That
///             second row is currently <b>unenforced</b> — it is a discipline, not a gate. Making it one
///             is a named task: an analyzer or architecture test forbidding raw
///             <c>IGrainFactory.GetGrain</c> outside grain code."
///         </i>
///     </para>
///     <para>
///         <b>Why the library cannot do it.</b> <c>Orleans.Multitenant</c>'s
///         <c>TenantSeparatingCallFilter</c> is an <c>IIncomingGrainCallFilter</c> that attributes a
///         call to the <i>calling grain's</i> tenant. Decompiled, its first act is to take
///         <c>context.SourceId</c>, return immediately when there is none, and return again when the
///         source is a client or a system target — so <c>ICrossTenantAuthorizer</c> is consulted
///         <b>only</b> for grain-to-grain calls. The gateway is an Orleans <i>client</i> by design
///         (docs/plan/03, docs/plan/10) and is therefore permanently outside the filter, no matter
///         how it is configured.
///         <c>CyberCloud.Tenancy.Tests.CrossTenantReachabilityTests.Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN</c>
///         demonstrates the hole with separation fully wired and the authorizer registered: a raw
///         physical key from a client still reads another tenant's state, and the authorizer's
///         counter does not move.
///     </para>
///     <para>
///         <b>The discriminator is a type, not a syntax shape.</b>
///         <c>IGrainFactory.ForTenant(t)</c> returns <c>Orleans.Multitenant.TenantGrainFactory</c>,
///         a distinct type with its own <c>GetGrain</c> — see <see cref="GrainFactories" />. So this
///         rule does not have to trace <c>ForTenant</c> through locals, fields or helper methods, and
///         cannot be defeated by hiding the call behind one.
///     </para>
///     <para>
///         <b>Three exemptions, all of them real code in this repository, all narrow:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>A grain that is not string-keyed.</b> docs/plan/00 § Coding standards:
///             <c>IGrainWithStringKey</c> is
///             <i>
///                 "the only key kind <c>Orleans.Multitenant</c> can
///                 carry a tenant in"
///             </i>
///             , so a <c>Guid</c>- or integer-keyed grain cannot be
///             tenant-scoped and reaching it without a tenant cannot cross one.
///             <c>ClusterHealthCheck</c> and <c>SiloReadinessHealthCheck</c> call
///             <c>GetGrain&lt;IManagementGrain&gt;(0)</c> — Orleans' own integer-keyed system grain
///             — from an <c>IHealthCheck</c>, which is not grain code. Read literally the plan's
///             sentence bans them; scoped by key kind it does not. See
///             <see cref="GrainFactories.TargetsAStringKeyedGrain" />.
///         </item>
///         <item>
///             <b>Grain code.</b> Inside a grain the filter <i>does</i> run, which is what
///             <c>Route7a_FromInsideAGrainTheRawKeyIsNowClosed</c> asserts. A grain calling
///             <c>GetGrain</c> is covered by the library and is not this rule's business.
///         </item>
///         <item>
///             <b>Null-tenant platform grains.</b> <c>ITenantDirectoryGrain</c> and
///             <c>IShardMapGrain</c> have no tenant to qualify with, and their own documentation says
///             to reach them
///             <i>
///                 "with a plain <c>IGrainFactory.GetGrain</c> — <b>not</b>
///                 <c>ForTenant</c>"
///             </i>
///             . Recognised by the key: a call to one of
///             <c>GrainKeys</c>'s null-tenant builders. <c>TenantDirectoryCache.Grain</c> and
///             <c>ShardMapRefresher.Grain</c> are the two sites, and without this exemption the rule
///             would fire on correct code the day it was switched on.
///         </item>
///     </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnqualifiedGrainReferenceAnalyzer : DiagnosticAnalyzer {
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.UnqualifiedGrainReference);

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
        var stringKeyed = context.Compilation.GetTypeByMetadataName(WellKnown.GrainWithStringKey);

        if (context.Compilation.GetTypeByMetadataName(WellKnown.GrainFactory) is null || stringKeyed is null) {
            return;
        }

        context.RegisterOperationAction(
            operationContext => AnalyzeInvocation(operationContext, stringKeyed),
            OperationKind.Invocation
        );
    }

    static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol stringKeyed) {
        var operation = (IInvocationOperation)context.Operation;

        if (!GrainFactories.IsUnqualifiedGetGrain(operation.TargetMethod)) {
            return;
        }

        if (!GrainFactories.TargetsAStringKeyedGrain(operation.TargetMethod, stringKeyed)) {
            return;
        }

        if (GrainFactories.IsGrain(ContainingType(context.ContainingSymbol))) {
            return;
        }

        if (ReachesANullTenantGrain(operation)) {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rules.UnqualifiedGrainReference,
                operation.Syntax.GetLocation(),
                context.ContainingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            )
        );
    }

    static INamedTypeSymbol? ContainingType(ISymbol symbol) => symbol as INamedTypeSymbol ?? symbol.ContainingType;

    /// <summary>
    ///     True when the key handed to <c>GetGrain</c> came from one of <c>GrainKeys</c>'s
    ///     null-tenant builders.
    /// </summary>
    static bool ReachesANullTenantGrain(IInvocationOperation operation) {
        foreach (var argument in operation.Arguments) {
            var value = argument.Value;
            while (value is IConversionOperation conversion && conversion.Operand is not null) {
                value = conversion.Operand;
            }

            if (value is IInvocationOperation key && GrainFactories.IsNullTenantKeyBuilder(key.TargetMethod)) {
                return true;
            }
        }

        return false;
    }
}
