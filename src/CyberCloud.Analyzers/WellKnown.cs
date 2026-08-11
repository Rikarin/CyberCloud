namespace CyberCloud.Analyzers;

/// <summary>
///     The metadata names these analyzers bind to, in one place.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ Every one of these is looked up with
///         <c>Compilation.GetTypeByMetadataName</c> and every analyzer <b>returns without
///         registering</b> when the lookup fails. That is deliberate: <c>CyberCloud.Core</c>
///         references no Orleans at all (docs/plan/03 § Assembly graph rules, rule 1), so an
///         Orleans-shaped rule must be inert there rather than guessing from a syntactic name
///         match. It also means this analyzer assembly needs no reference to Orleans, which it
///         could not have anyway — it targets netstandard2.0 and Orleans 10 targets net10.0.
///     </para>
/// </remarks>
static class WellKnown
{
    /// <summary><c>Orleans.GenerateSerializerAttribute</c>.</summary>
    public const string GenerateSerializerAttribute = "Orleans.GenerateSerializerAttribute";

    /// <summary><c>Orleans.AliasAttribute</c>.</summary>
    public const string AliasAttribute = "Orleans.AliasAttribute";

    /// <summary><c>Orleans.IdAttribute</c>.</summary>
    public const string IdAttribute = "Orleans.IdAttribute";

    /// <summary><c>Orleans.IGrainFactory</c> — the interface that hands out grain references.</summary>
    public const string GrainFactory = "Orleans.IGrainFactory";

    /// <summary><c>Orleans.IGrainBase</c> — implemented by <c>Orleans.Grain</c> and by POCO grains.</summary>
    public const string GrainBase = "Orleans.IGrainBase";

    /// <summary><c>Orleans.Grain</c> — the class most grains still derive from.</summary>
    public const string Grain = "Orleans.Grain";

    /// <summary><c>Orleans.IAddressable</c> — the root of every grain reference.</summary>
    public const string Addressable = "Orleans.IAddressable";

    /// <summary>
    ///     <c>Orleans.IGrainWithStringKey</c> — the only key kind that can carry a tenant.
    /// </summary>
    /// <remarks>
    ///     docs/plan/00 § Coding standards: <i>"Grain interfaces are <c>IGrainWithStringKey</c> for
    ///     anything tenant-scoped, because that is the only key kind <c>Orleans.Multitenant</c> can
    ///     carry a tenant in (verified against its source)."</i> CC1006 leans on the contrapositive:
    ///     a grain whose key is not a string cannot be tenant-scoped, so reaching it without a
    ///     tenant cannot be a cross-tenant read.
    /// </remarks>
    public const string GrainWithStringKey = "Orleans.IGrainWithStringKey";

    /// <summary><c>CyberCloud.Core.Resources.GrainKeys</c> — the one type allowed to format a key.</summary>
    public const string GrainKeys = "CyberCloud.Core.Resources.GrainKeys";

    /// <summary><c>System.Threading.Tasks.Task</c>.</summary>
    public const string Task = "System.Threading.Tasks.Task";

    /// <summary><c>System.Threading.Tasks.Task`1</c>.</summary>
    public const string TaskOfT = "System.Threading.Tasks.Task`1";

    /// <summary><c>System.Threading.Tasks.ValueTask</c>.</summary>
    public const string ValueTask = "System.Threading.Tasks.ValueTask";

    /// <summary><c>System.Threading.Tasks.ValueTask`1</c>.</summary>
    public const string ValueTaskOfT = "System.Threading.Tasks.ValueTask`1";

    /// <summary>The method name a grain reference is obtained through.</summary>
    public const string GetGrainMethod = "GetGrain";

    /// <summary><c>Orleans.Multitenant</c>'s tenant-qualifying factory selector.</summary>
    public const string ForTenantMethod = "ForTenant";

    /// <summary>
    ///     The assembly that is allowed to hold a secret value — docs/plan/00 § Non-negotiables and
    ///     docs/plan/18. It does not exist yet; the rule is written so that it does not have to.
    /// </summary>
    public const string VaultAssembly = "CyberCloud.Vault";
}
