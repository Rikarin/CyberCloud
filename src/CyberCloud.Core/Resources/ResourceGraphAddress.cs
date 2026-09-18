namespace CyberCloud.Core.Resources;

/// <summary>
///     The resource graph's one address —
///     <c>/tenants/{t}/providers/CyberCloud.ResourceGraph/resources</c>, where a tenant <c>POST</c>s
///     a query over its projected resources. docs/plan/08 § The resource-graph projection, the query
///     half of issue #54.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The second reserved namespace, reserved for the reason the first is.</b> The address
///         is a tenant scope followed by <c>/providers/{namespace}/{type}</c>, which is the shape a
///         provider's resource collection would have if a collection could hang off a tenant — and
///         a provider that registered <see cref="ProviderNamespace" /> would have every type it
///         declared shadowed by a route the gateway claims first. <c>ProviderRegistry.Build</c>
///         refuses such a provider, exactly as it refuses one that claims
///         <see cref="RoleAssignmentId.ProviderNamespace" />, and under this namespace the router asks
///         this one grammar and nothing else: a path that names the namespace and is not this address
///         is a <c>400</c> that says what the address is, never a fall-through into the scope or
///         resource grammars and their <c>404</c>.
///     </para>
///     <para>
///         ⚠ <b>Disjoint from every other grammar by construction, and the router's order is free.</b>
///         Five segments with <c>providers</c> third: a scope is two, four or six segments with
///         <c>subscriptions</c> third; a scope collection is three or five with <c>subscriptions</c>
///         third; a resource or a resource collection is at least nine and puts <c>providers</c>
///         seventh; a role assignment names its own namespace. <c>ResourceGraphAddressTests</c>
///         drives the overlap rather than asserting the claim.
///     </para>
///     <para>
///         <b>Why a <c>POST</c> and not a <c>GET</c> with a query string.</b> A KQL query is a
///         program, routinely longer than a URL should be and full of characters a URL would have to
///         escape; Azure Resource Graph's own endpoint is a <c>POST</c> for the same reason. It is
///         the one <c>POST</c> in this API that is not an action on an existing resource, and the
///         router sees it before the action grammar so that docs/plan/08's rule — <c>POST</c>
///         <i>"appears only for actions on an existing resource"</i> — stays true of every path that
///         reaches the resource manager.
///     </para>
/// </remarks>
/// <param name="TenantId">The tenant whose projection is queried.</param>
public readonly record struct ResourceGraphAddress(Guid TenantId) {
    /// <summary>
    ///     The provider namespace this address lives under. ⚠ Reserved: no provider may register it.
    /// </summary>
    /// <remarks>
    ///     Case-preserving on the way out and matched case-insensitively on the way in, as every
    ///     structural literal of a resource path is — <see cref="ResourceId.ParsePath" />. The
    ///     reservation is enforced the same way, so a provider spelling it
    ///     <c>cybercloud.resourcegraph</c> is refused too.
    /// </remarks>
    public const string ProviderNamespace = "CyberCloud.ResourceGraph";

    /// <summary>The type segment, <c>resources</c> — the one table the query language sees.</summary>
    public const string TypeSegment = "resources";

    /// <summary>
    ///     The two segments every address under the reserved namespace carries:
    ///     <c>/providers/CyberCloud.ResourceGraph/</c>.
    /// </summary>
    public const string NamespaceSegment = "/providers/" + ProviderNamespace + "/";

    /// <summary>
    ///     The three segments that follow the tenant: <c>/providers/CyberCloud.ResourceGraph/resources</c>.
    /// </summary>
    public const string Suffix = NamespaceSegment + TypeSegment;

    /// <summary>The address, spelled from the tenant.</summary>
    public string Path => ScopeId.Tenant(TenantId).Path + Suffix;

    /// <summary>
    ///     Whether a path is under the reserved namespace at all — whether or not it is the one
    ///     address served there.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     ⚠ What makes the reservation total rather than a precedence rule, as
    ///     <see cref="RoleAssignmentId.IsUnderNamespace" /> is for its namespace: the router asks
    ///     this first, and under the namespace only <see cref="ParsePath" />'s answer counts.
    /// </remarks>
    public static bool IsUnderNamespace(string? path) =>
        path is not null && path.Contains(NamespaceSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Parses the address. Returns <see langword="false" /> for anything that is not exactly it,
    ///     and never throws.
    /// </summary>
    /// <param name="path">The candidate path. May be <see langword="null" />.</param>
    /// <param name="address">The parsed address on success.</param>
    public static bool TryParsePath(string? path, out ResourceGraphAddress address) {
        address = default;
        var parsed = ParsePath(path);

        if (parsed.IsFailure) {
            return false;
        }

        address = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary>
    ///     <see cref="TryParsePath" /> with an explanation that names the offending value and the
    ///     one shape the namespace serves.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     The suffix must be the <b>end</b> of the path — a trailing <c>/</c>, a name after
    ///     <c>resources</c>, or another type under the namespace is refused — and it must follow a
    ///     tenant scope and no other: a subscription or a resource group before the namespace is a
    ///     refusal too, because the query is over a tenant's whole projection and the query language
    ///     is where a caller narrows it.
    /// </remarks>
    public static Result<ResourceGraphAddress> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A resource graph query is a POST to '/tenants/{t}" + Suffix + "' — docs/plan/08 § The "
                + "resource-graph projection."
            );
        }

        if (path.Length <= Suffix.Length || !path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) {
            return Invalid(
                $"'{path}' is not the resource graph's address. The only address under "
                + $"'{ProviderNamespace}' is '/tenants/{{t}}{Suffix}', and a query is a POST to it "
                + "with { \"query\": \"resources | …\" } as the body — docs/plan/08 § The resource-graph "
                + "projection."
            );
        }

        var scope = ScopeId.ParsePath(path[..^Suffix.Length]);

        if (scope.IsFailure || scope.GetValueOrThrow().Kind != ScopeKind.Tenant) {
            return Invalid(
                $"'{path}' is not the resource graph's address: the segments before '{Suffix}' must be a "
                + "tenant, '/tenants/{t}'. The query is over the tenant's whole projection; narrow it "
                + "in the query with 'where subscriptionId == …' or 'where resourceGroup == …' rather "
                + "than in the address — docs/plan/08 § The resource-graph projection."
            );
        }

        return Result<ResourceGraphAddress>.Success(new(scope.GetValueOrThrow().TenantId));
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    static Result<ResourceGraphAddress> Invalid(string message) =>
        Result<ResourceGraphAddress>.Failure(ErrorCode.InvalidResourceId, message);
}
