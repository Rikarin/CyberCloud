using Microsoft.CodeAnalysis;

namespace CyberCloud.Analyzers;

/// <summary>
///     What counts as "a grain factory", and what counts as "grain code", shared by CC1004 and
///     CC1006.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The single most useful fact in this file:</b> <c>Orleans.Multitenant</c>'s
///         <c>ForTenant(tenantId)</c> does not return an <c>IGrainFactory</c>. It returns a distinct
///         type, <c>Orleans.Multitenant.TenantGrainFactory</c>, which implements no interfaces at all
///         and declares its own <c>GetGrain</c> overloads. Verified against the shipped 4.0.0
///         assembly:
///     </para>
///     <code>
///     Orleans.Multitenant.GrainFactoryExtensions.ForTenant(IGrainFactory, string) -> TenantGrainFactory
///     Orleans.Multitenant.TenantGrainFactory.GetGrain&lt;T&gt;(string, string)
///     Orleans.Multitenant.TenantGrainFactory : (no interfaces)
///     </code>
///     <para>
///         So "did this call go through <c>ForTenant</c>?" is a question about the <i>type of the
///         receiver</i>, not about the shape of the expression. CC1006 does not have to chase
///         <c>ForTenant(…)</c> through locals, fields, parameters or helper methods: a
///         <c>TenantGrainFactory</c> can only have come from one place. That is what makes the rule
///         cheap and, more importantly, unfoolable in the direction that matters — you cannot
///         accidentally land on the tenant-qualified overload.
///     </para>
/// </remarks>
static class GrainFactories
{
    /// <summary>
    ///     True for a <c>GetGrain</c> declared by <c>Orleans.IGrainFactory</c> or by Orleans's own
    ///     extension class over it — i.e. the <b>un</b>qualified factory.
    /// </summary>
    /// <remarks>
    ///     Deliberately false for <c>Orleans.Multitenant.TenantGrainFactory.GetGrain</c>: that one
    ///     already carries a tenant.
    /// </remarks>
    public static bool IsUnqualifiedGetGrain(IMethodSymbol method) =>
        string.Equals(method.Name, WellKnown.GetGrainMethod, StringComparison.Ordinal)
        && IsOrleansType(method.ContainingType, "IGrainFactory", "GrainFactoryExtensions");

    /// <summary>
    ///     True for any <c>GetGrain</c> that hands out a grain reference, tenant-qualified or not.
    ///     CC1004 cares about the key regardless of which factory formatted the tenant part.
    /// </summary>
    public static bool IsGetGrain(IMethodSymbol method) =>
        string.Equals(method.Name, WellKnown.GetGrainMethod, StringComparison.Ordinal)
        && (IsOrleansType(method.ContainingType, "IGrainFactory", "GrainFactoryExtensions")
            || IsMultitenantType(method.ContainingType));

    /// <summary>
    ///     True when <paramref name="type" /> is a grain: it derives from <c>Orleans.Grain</c> or
    ///     implements <c>Orleans.IGrainBase</c> (which is what a POCO grain does, and what
    ///     <c>Orleans.Grain</c> itself implements).
    /// </summary>
    /// <remarks>
    ///     ⚠ Grain call filters, grain services and hosted services are deliberately <b>not</b>
    ///     grains here. That is not an oversight: <c>TenantSeparatingCallFilter</c> attributes an
    ///     incoming call to the <i>calling grain's</i> tenant, and none of those has one. They are
    ///     exactly the callers CC1006 exists to catch.
    /// </remarks>
    public static bool IsGrain(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        foreach (var contract in type.AllInterfaces)
        {
            if (IsOrleansType(contract, "IGrainBase"))
            {
                return true;
            }
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (IsOrleansType(current, "Grain"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The <c>CyberCloud.Core.Resources.GrainKeys</c> methods that produce a <b>null-tenant</b>
    ///     key.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/06 § Grain keys and <c>GrainKeyKind</c>'s own documentation: a platform
    ///         singleton (<c>platform/{name}</c>) and a cluster connection (<c>cluster/{id:N}</c>)
    ///         are null-tenant by construction. <c>ITenantDirectoryGrain</c>'s remarks are explicit —
    ///         <i>"reach it with a plain <c>IGrainFactory.GetGrain</c> — <b>not</b>
    ///         <c>ForTenant</c>"</i> — because there is no tenant to qualify with.
    ///     </para>
    ///     <para>
    ///         This set is what keeps CC1006 off the two correct call sites that exist today,
    ///         <c>TenantDirectoryCache.Grain</c> and <c>ShardMapRefresher.Grain</c>, both of which are
    ///         non-grain code reaching a platform singleton on purpose. Without it the rule would
    ///         fire on correct code on day one, and an analyzer that does that gets suppressed
    ///         wholesale.
    ///     </para>
    /// </remarks>
    public static bool IsNullTenantKeyBuilder(IMethodSymbol method)
    {
        if (!string.Equals(method.ContainingType?.ToDisplayString(), WellKnown.GrainKeys, StringComparison.Ordinal))
        {
            return false;
        }

        switch (method.Name)
        {
            case "PlatformSingleton":
            case "ShardMap":
            case "TenantDirectory":
            case "ClusterConnection":
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    ///     True when this <c>GetGrain</c> call hands back a reference to a <b>string-keyed</b> grain
    ///     — the only kind that can be tenant-scoped.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/00 § Coding standards: <i>"Grain interfaces are <c>IGrainWithStringKey</c>
    ///         for anything tenant-scoped, because that is the only key kind
    ///         <c>Orleans.Multitenant</c> can carry a tenant in."</i> Contrapositive: a
    ///         <c>Guid</c>-keyed or integer-keyed grain has nowhere to put a tenant id, so reaching
    ///         it without one is not a cross-tenant read and CC1006 must be silent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is not a convenience narrowing — it is what keeps the rule truthful.</b>
    ///         Orleans' own system grains are integer-keyed, and this repository already calls one:
    ///         <c>ClusterHealthCheck</c> and <c>SiloReadinessHealthCheck</c> both do
    ///         <c>grains.GetGrain&lt;IManagementGrain&gt;(0)</c> from an <c>IHealthCheck</c>, which is
    ///         not grain code. Verified against the shipped assembly:
    ///         <c>Orleans.Runtime.IManagementGrain : Orleans.IGrainWithIntegerKey</c>. Read
    ///         literally, docs/plan/00's "forbidding raw <c>IGrainFactory.GetGrain</c> outside grain
    ///         code" bans those two correct call sites; scoped by key kind, it bans exactly the
    ///         reads that can cross a tenant.
    ///     </para>
    ///     <para>
    ///         The generic overloads carry the grain interface as a type argument, which is exact.
    ///         The non-generic <c>GetGrain(Type, …)</c> family does not, so the key <i>parameter</i>
    ///         answers instead: a <c>Guid</c> or <c>long</c> in the signature means a non-string key.
    ///     </para>
    /// </remarks>
    public static bool TargetsAStringKeyedGrain(IMethodSymbol method, INamedTypeSymbol stringKeyed)
    {
        if (method.TypeArguments.Length == 1)
        {
            return ImplementsOrIs(method.TypeArguments[0], stringKeyed);
        }

        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.SpecialType == SpecialType.System_Int64
                || string.Equals(parameter.Type.Name, "Guid", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    static bool ImplementsOrIs(ITypeSymbol type, INamedTypeSymbol contract)
    {
        if (SymbolEqualityComparer.Default.Equals(type, contract))
        {
            return true;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implemented, contract))
            {
                return true;
            }
        }

        // An unconstrained type parameter — `GetGrain<T>(key)` inside a generic helper — could be
        // anything, so it is treated as string-keyed and reported. A helper that hands out grain
        // references from outside a grain is exactly the shape the rule is about.
        return type.TypeKind == TypeKind.TypeParameter;
    }

    static bool IsMultitenantType(INamedTypeSymbol? type) =>
        type is not null
        && string.Equals(
            type.ContainingNamespace?.ToDisplayString(),
            "Orleans.Multitenant",
            StringComparison.Ordinal);

    static bool IsOrleansType(INamedTypeSymbol? type, params string[] names)
    {
        if (type is null
            || !string.Equals(type.ContainingNamespace?.ToDisplayString(), "Orleans", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var name in names)
        {
            if (string.Equals(type.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
