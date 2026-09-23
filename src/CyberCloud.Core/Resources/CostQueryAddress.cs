namespace CyberCloud.Core.Resources;

/// <summary>
///     The cost query's address — <c>{scope}/providers/CyberCloud.CostManagement/query</c> on a
///     subscription or a resource group, where a caller <c>POST</c>s a period and a grouping and gets
///     back what the usage in that scope cost. docs/plan/22 § Cost visibility, issue #38.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The third reserved namespace, reserved for the reason the other two are.</b>
///         <see cref="ResourceGraphAddress" /> and <see cref="RoleAssignmentId" /> each claim a
///         namespace a provider may not register, because the path they serve has the shape of a
///         provider's collection: on a resource group this address is nine segments with
///         <c>providers</c> seventh, which is exactly a resource collection of the type
///         <c>CyberCloud.CostManagement/query</c>. <c>ProviderRegistry.Build</c> refuses a provider
///         that claims <see cref="ProviderNamespace" />, and under the namespace the router asks this
///         one grammar and nothing else.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Not under <c>CyberCloud.Billing</c>, which is where docs/plan/01 files Cost Management,
///             because that namespace is a provider's.
///         </b> <c>CyberCloud.Billing/budgets</c> is a published type, and reserving its namespace
///         for a query would shadow it. Azure spells the same split
///         <c>Microsoft.CostManagement/query</c> beside <c>Microsoft.Consumption/budgets</c>; this is
///         that, with the platform's prefix.
///     </para>
///     <para>
///         <b>A subscription or a resource group, and no other scope.</b> Usage is recorded per
///         subscription (the usage ledger is keyed by one), so a tenant-wide query would be a fan-out
///         over every subscription the caller may or may not read — a portal page's job, not an
///         address. A management group is refused for the same reason.
///     </para>
/// </remarks>
/// <param name="Scope">The subscription or resource group whose usage is priced.</param>
public readonly record struct CostQueryAddress(ScopeId Scope) {
    /// <summary>The provider namespace. ⚠ Reserved: no provider may register it.</summary>
    public const string ProviderNamespace = "CyberCloud.CostManagement";

    /// <summary>The type segment, <c>query</c>.</summary>
    public const string TypeSegment = "query";

    /// <summary>The two segments every address under the namespace carries.</summary>
    public const string NamespaceSegment = "/providers/" + ProviderNamespace + "/";

    /// <summary>The three segments after the scope: <c>/providers/CyberCloud.CostManagement/query</c>.</summary>
    public const string Suffix = NamespaceSegment + TypeSegment;

    /// <summary>The address, spelled from the scope.</summary>
    public string Path => Scope.Path + Suffix;

    /// <summary>Whether a path is under the reserved namespace at all.</summary>
    /// <param name="path">The candidate path.</param>
    public static bool IsUnderNamespace(string? path) =>
        path is not null && path.Contains(NamespaceSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the address, or explains why a path under the namespace is not it.</summary>
    /// <param name="path">The candidate path.</param>
    /// <returns>
    ///     The address, or <see cref="ErrorCode.InvalidResourceId" /> naming the one shape the
    ///     namespace serves — a trailing segment, a tenant or management-group scope, and any other
    ///     type under the namespace are all refused.
    /// </returns>
    public static Result<CostQueryAddress> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)
            || path.Length <= Suffix.Length
            || !path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) {
            return Invalid(
                $"'{path}' is not the cost query's address. The only address under '{ProviderNamespace}' "
                + $"is '{{scope}}{Suffix}' on a subscription or a resource group, and a query is a POST "
                + "to it — docs/plan/22 § Cost visibility."
            );
        }

        var scope = ScopeId.ParsePath(path[..^Suffix.Length]);

        if (scope.IsFailure
            || scope.GetValueOrThrow().Kind is not (ScopeKind.Subscription or ScopeKind.ResourceGroup)) {
            return Invalid(
                $"'{path}' is not the cost query's address: the segments before '{Suffix}' must be a "
                + "subscription or a resource group. Usage is recorded per subscription, so a wider "
                + "scope is one query per subscription — docs/plan/22 § Cost visibility."
            );
        }

        return Result<CostQueryAddress>.Success(new(scope.GetValueOrThrow()));
    }

    /// <summary>The same address in another tenant — what the gateway rebuilds from the token.</summary>
    /// <param name="tenantId">The token's tenant.</param>
    public CostQueryAddress WithTenant(Guid tenantId) => new(Scope with { TenantId = tenantId });

    /// <inheritdoc />
    public override string ToString() => Path;

    static Result<CostQueryAddress> Invalid(string message) =>
        Result<CostQueryAddress>.Failure(ErrorCode.InvalidResourceId, message);
}
