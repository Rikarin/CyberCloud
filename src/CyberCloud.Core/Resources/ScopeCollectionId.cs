using System.Globalization;

namespace CyberCloud.Core.Resources;

/// <summary>
///     The address of a <b>collection</b> of scopes — a tenant's subscriptions, or a subscription's
///     resource groups:
///     <code>
///     /tenants/{tenantId}/subscriptions
///     /tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups
///     </code>
///     Each is its parent's <see cref="ScopeId.Path" /> plus the literal segment the children live
///     under, and nothing after it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The third scope grammar, and it partitions the scope-shaped paths with the other
///         two exactly as <see cref="ResourceCollectionId" /> partitions the resource-shaped ones.</b>
///         A scope item is 2, 4 or 6 segments; this is 3 or 5. A resource address is at least ten
///         and carries <c>/providers/</c>; so does a role assignment. No path parses as two of
///         them, and the router can therefore try this grammar after the item grammar without a
///         precedence rule — <c>ScopeCollectionIdTests</c> sweeps the overlap rather than assuming
///         it, for the reason <c>ScopeIdTests</c> does.
///     </para>
///     <para>
///         ⚠ <b>It carries no id of its own and never will.</b> A collection is not an entity: it
///         has no GUID, no ReBAC object and no grain. It is a query against the parent grain's own
///         listing — <c>ITenantGrain.ListSubscriptionsAsync</c> or
///         <c>ISubscriptionGrain.ListResourceGroupsAsync</c> — and every authorization decision
///         about it is a decision about the scopes it would return, plus one about the parent for
///         the resource-group case. <c>IScopeManager.ListAsync</c> carries that argument.
///     </para>
///     <para>
///         ⚠ <b>There is no tenant collection.</b> <c>/tenants</c> would list every tenant on the
///         platform to a caller holding a token for one of them; stage 3 of the gateway resolves the
///         request's tenant from the token and refuses every path naming another, so the only
///         tenant a request can address is its own and there is nothing to enumerate. A two-segment
///         parent with no children literal is therefore the item grammar's, never this one's.
///     </para>
/// </remarks>
/// <param name="Parent">
///     The scope whose children are listed: a <see cref="ScopeKind.Tenant" /> for the subscription
///     collection, a <see cref="ScopeKind.Subscription" /> for the resource-group collection.
/// </param>
public readonly record struct ScopeCollectionId(ScopeId Parent) {
    /// <summary>The scope whose children are listed. Only a tenant or a subscription can be one.</summary>
    public ScopeId Parent {
        get;
        init => field = EnsureParent(value);
    } = EnsureParent(Parent);

    /// <summary>
    ///     What the members are — <see cref="ScopeKind.Subscription" /> under a tenant,
    ///     <see cref="ScopeKind.ResourceGroup" /> under a subscription, <see cref="ScopeKind.Unknown" />
    ///     for a default instance.
    /// </summary>
    public ScopeKind MemberKind =>
        Parent.Kind switch {
            ScopeKind.Tenant => ScopeKind.Subscription,
            ScopeKind.Subscription => ScopeKind.ResourceGroup,
            _ => ScopeKind.Unknown
        };

    /// <summary>The tenant every member belongs to — the parent's.</summary>
    public Guid TenantId => Parent.TenantId;

    /// <summary>The address: the parent's path plus the children's literal segment.</summary>
    public string Path =>
        Parent.Kind switch {
            ScopeKind.Tenant => Parent.Path + "/" + ResourceId.SubscriptionsSegment,
            ScopeKind.Subscription => Parent.Path + "/" + ResourceId.ResourceGroupsSegment,
            _ => ""
        };

    /// <summary>The subscription collection of a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static ScopeCollectionId SubscriptionsOf(Guid tenantId) => new(ScopeId.Tenant(tenantId));

    /// <summary>The resource-group collection of a subscription.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    public static ScopeCollectionId ResourceGroupsOf(Guid tenantId, Guid subscriptionId) =>
        new(ScopeId.Subscription(tenantId, subscriptionId));

    /// <summary>
    ///     Parses a scope collection path. Returns <see langword="false" /> for anything that is not
    ///     exactly one, and never throws.
    /// </summary>
    /// <param name="path">The candidate path. May be <see langword="null" />.</param>
    /// <param name="id">The parsed address on success.</param>
    public static bool TryParsePath(string? path, out ScopeCollectionId id) {
        id = default;
        var parsed = ParsePath(path);

        if (parsed.IsFailure) {
            return false;
        }

        id = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary>
    ///     <see cref="TryParsePath" /> with an explanation, in the shape docs/plan/08 § Errors wants:
    ///     the message names the offending value.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     <para>
    ///         Built on <see cref="ScopeId.ParsePath" /> rather than beside it: the parent is the
    ///         path with its last segment removed, and that parser already applies the literal,
    ///         case and <c>D</c>-form rules this grammar shares. A second copy of those rules would
    ///         be a second place for them to drift, and a collection whose parent parsed under one
    ///         set of rules and not the other would be an address the gateway routes and the manager
    ///         refuses.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The last segment must be the literal the parent's kind implies</b> —
    ///         <c>subscriptions</c> under a tenant, <c>resourceGroups</c> under a subscription —
    ///         matched case-insensitively like every other structural literal. Anything else after a
    ///         well-formed parent is not "a collection of something unknown"; it is not a scope
    ///         address at all.
    ///     </para>
    /// </remarks>
    public static Result<ScopeCollectionId> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A scope collection path is required. It looks like '/tenants/{tenantId}/subscriptions' "
                + "or '/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups' — "
                + "docs/plan/06 § The hierarchy."
            );
        }

        if (path[0] != '/') {
            return Invalid($"'{path}' is not a scope collection path: it must start with '/'.");
        }

        var segments = path[1..].Split('/');

        foreach (var segment in segments) {
            if (segment.Length == 0) {
                return Invalid(
                    $"'{path}' is not a scope collection path: it contains an empty segment (a doubled "
                    + "or trailing '/')."
                );
            }
        }

        if (segments.Length is not (3 or 5)) {
            return Invalid(
                "'"
                + path
                + "' is not a scope collection path: it has "
                + segments.Length.ToString(CultureInfo.InvariantCulture)
                + " segments and a scope collection has 3 (a tenant's subscriptions) or 5 (a "
                + "subscription's resource groups)."
            );
        }

        var expected = segments.Length == 3 ? ResourceId.SubscriptionsSegment : ResourceId.ResourceGroupsSegment;

        if (!string.Equals(segments[^1], expected, StringComparison.OrdinalIgnoreCase)) {
            return Invalid(
                $"'{path}' is not a scope collection path: the last segment is '{segments[^1]}' and a "
                + $"collection under this parent ends in '{expected}' (matched case-insensitively)."
            );
        }

        var parent = ScopeId.ParsePath(path[..path.LastIndexOf('/')]);

        return parent.TryGetError(out var parentError)
            ? Result<ScopeCollectionId>.Failure(parentError)
            : Result<ScopeCollectionId>.Success(new(parent.GetValueOrThrow()));
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    static ScopeId EnsureParent(ScopeId parent) =>
        parent.Kind is ScopeKind.Tenant or ScopeKind.Subscription or ScopeKind.Unknown
            ? parent
            : throw new ArgumentException(
                $"'{parent.Path}' is a {parent.Kind} and a scope collection lives under a tenant or a "
                + "subscription. A resource group has no scope children — what is inside it is "
                + "resources, listed by ResourceCollectionId.",
                nameof(parent)
            );

    static Result<ScopeCollectionId> Invalid(string message) =>
        Result<ScopeCollectionId>.Failure(ErrorCode.InvalidResourceId, message);
}
