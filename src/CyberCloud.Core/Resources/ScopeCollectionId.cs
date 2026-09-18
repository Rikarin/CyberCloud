using System.Globalization;

namespace CyberCloud.Core.Resources;

/// <summary>
///     The address of a <b>collection</b> of scopes — a tenant's subscriptions, a tenant's management
///     groups, or a subscription's resource groups:
///     <code>
///     /tenants/{tenantId}/subscriptions
///     /tenants/{tenantId}/managementGroups
///     /tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups
///     </code>
///     Each is its parent's <see cref="ScopeId.Path" /> plus the literal segment the children live
///     under, and nothing after it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The third scope grammar, and it partitions the scope-shaped paths with the other
///             two exactly as <see cref="ResourceCollectionId" /> partitions the resource-shaped ones.
///         </b>
///         A scope item is 2, 4 or 6 segments; this is 3 or 5. A resource address is at least ten
///         and carries <c>/providers/</c>; so does a role assignment. No path parses as two of
///         them, and the router can therefore try this grammar after the item grammar without a
///         precedence rule — <c>ScopeCollectionIdTests</c> sweeps the overlap rather than assuming
///         it, for the reason <c>ScopeIdTests</c> does.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Since issue #39 the parent no longer determines the members, and
///             <see cref="MemberKind" /> is a field rather than a derivation.
///         </b> A tenant has two
///         collections — its subscriptions and its management groups — and the address tells them
///         apart by the last segment alone. The first version of this type computed the member kind
///         from <c>Parent.Kind</c>, which was correct for as long as every parent had exactly one
///         kind of child; a caller that still only keeps the parent (the manager's
///         <c>ScopeListRequest.ParentPath</c>) has to carry the kind beside it now, and does. The
///         management-group collection is <i>flat</i>: it lists every group in the tenant, nested or
///         not, the way Azure's <c>GET /providers/Microsoft.Management/managementGroups</c> does,
///         and a group's place in the tree is a property of the group rather than a segment of its
///         address.
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
///     and management-group collections, a <see cref="ScopeKind.Subscription" /> for the
///     resource-group collection.
/// </param>
/// <param name="MemberKind">
///     What the members are. <see cref="ScopeKind.Unknown" /> means "the one kind this parent had
///     before issue #39" — <see cref="ScopeKind.Subscription" /> under a tenant and
///     <see cref="ScopeKind.ResourceGroup" /> under a subscription — kept so a caller written against
///     the one-parameter shape still means what it meant.
/// </param>
public readonly record struct ScopeCollectionId(ScopeId Parent, ScopeKind MemberKind = ScopeKind.Unknown) {
    /// <summary>The scope whose children are listed. Only a tenant or a subscription can be one.</summary>
    public ScopeId Parent {
        get;
        init => field = EnsureParent(value, MemberKind);
    } = EnsureParent(Parent, MemberKind);

    /// <summary>
    ///     What the members are — <see cref="ScopeKind.Subscription" /> or
    ///     <see cref="ScopeKind.ManagementGroup" /> under a tenant,
    ///     <see cref="ScopeKind.ResourceGroup" /> under a subscription, <see cref="ScopeKind.Unknown" />
    ///     for a default instance.
    /// </summary>
    public ScopeKind MemberKind {
        get;
        init => field = EnsureMembers(Parent, value);
    } = EnsureMembers(Parent, MemberKind);

    /// <summary>The tenant every member belongs to — the parent's.</summary>
    public Guid TenantId => Parent.TenantId;

    /// <summary>The address: the parent's path plus the children's literal segment.</summary>
    public string Path =>
        MemberKind switch {
            ScopeKind.Subscription => Parent.Path + "/" + ResourceId.SubscriptionsSegment,
            ScopeKind.ManagementGroup => Parent.Path + "/" + ResourceId.ManagementGroupsSegment,
            ScopeKind.ResourceGroup => Parent.Path + "/" + ResourceId.ResourceGroupsSegment,
            _ => ""
        };

    /// <summary>The subscription collection of a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static ScopeCollectionId SubscriptionsOf(Guid tenantId) =>
        new(ScopeId.Tenant(tenantId), ScopeKind.Subscription);

    /// <summary>The management-group collection of a tenant — every group in it, flat.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static ScopeCollectionId ManagementGroupsOf(Guid tenantId) =>
        new(ScopeId.Tenant(tenantId), ScopeKind.ManagementGroup);

    /// <summary>The resource-group collection of a subscription.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    public static ScopeCollectionId ResourceGroupsOf(Guid tenantId, Guid subscriptionId) =>
        new(ScopeId.Subscription(tenantId, subscriptionId), ScopeKind.ResourceGroup);

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
    ///         ⚠ <b>The last segment must be a literal the parent's kind allows</b> —
    ///         <c>subscriptions</c> or <c>managementGroups</c> under a tenant, <c>resourceGroups</c>
    ///         under a subscription — matched case-insensitively like every other structural literal. Anything else after a
    ///         well-formed parent is not "a collection of something unknown"; it is not a scope
    ///         address at all.
    ///     </para>
    /// </remarks>
    public static Result<ScopeCollectionId> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A scope collection path is required. It looks like '/tenants/{tenantId}/subscriptions', "
                + "'/tenants/{tenantId}/managementGroups' or "
                + "'/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups' — "
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
                + " segments and a scope collection has 3 (a tenant's subscriptions or management "
                + "groups) or 5 (a subscription's resource groups)."
            );
        }

        var members = segments.Length == 3
            ? Literal(segments[^1], ResourceId.SubscriptionsSegment) ? ScopeKind.Subscription
            : Literal(segments[^1], ResourceId.ManagementGroupsSegment) ? ScopeKind.ManagementGroup
            : ScopeKind.Unknown
            : Literal(segments[^1], ResourceId.ResourceGroupsSegment) ? ScopeKind.ResourceGroup
                : ScopeKind.Unknown;

        if (members == ScopeKind.Unknown) {
            var expected = segments.Length == 3
                ? $"'{ResourceId.SubscriptionsSegment}' or '{ResourceId.ManagementGroupsSegment}'"
                : $"'{ResourceId.ResourceGroupsSegment}'";

            return Invalid(
                $"'{path}' is not a scope collection path: the last segment is '{segments[^1]}' and a "
                + $"collection under this parent ends in {expected} (matched case-insensitively)."
            );
        }

        var parent = ScopeId.ParsePath(path[..path.LastIndexOf('/')]);

        return parent.TryGetError(out var parentError)
            ? Result<ScopeCollectionId>.Failure(parentError)
            : Result<ScopeCollectionId>.Success(new(parent.GetValueOrThrow(), members));
    }

    static bool Literal(string segment, string literal) =>
        string.Equals(segment, literal, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => Path;

    static ScopeId EnsureParent(ScopeId parent, ScopeKind members) {
        if (parent.Kind is not (ScopeKind.Tenant or ScopeKind.Subscription or ScopeKind.Unknown)) {
            // ⚠ A management group is refused here too, and that is the flat-listing decision
            // stated as a constructor rule: the tree is read off each group's own record, and a
            // '/managementGroups/{name}/managementGroups' address would be a second path to the same
            // members, which is the drift docs/plan/06 § Identifiers spends a paragraph refusing.
            throw new ArgumentException(
                $"'{parent.Path}' is a {parent.Kind} and a scope collection lives under a tenant or a "
                + "subscription. A resource group has no scope children — what is inside it is "
                + "resources, listed by ResourceCollectionId — and a management group's children are "
                + "listed flat under the tenant, each carrying its parent as a property.",
                nameof(parent)
            );
        }

        _ = EnsureMembers(parent, members);
        return parent;
    }

    static ScopeKind EnsureMembers(ScopeId parent, ScopeKind members) {
        var resolved = members == ScopeKind.Unknown
            ? parent.Kind switch {
                ScopeKind.Tenant => ScopeKind.Subscription,
                ScopeKind.Subscription => ScopeKind.ResourceGroup,
                _ => ScopeKind.Unknown
            }
            : members;

        var legal = (parent.Kind, resolved) switch {
            (ScopeKind.Unknown, ScopeKind.Unknown) => true,
            (ScopeKind.Tenant, ScopeKind.Subscription or ScopeKind.ManagementGroup) => true,
            (ScopeKind.Subscription, ScopeKind.ResourceGroup) => true,
            _ => false
        };

        return legal
            ? resolved
            : throw new ArgumentException(
                $"A {parent.Kind} has no {resolved} collection. A tenant lists its subscriptions and "
                + "its management groups; a subscription lists its resource groups — docs/plan/06 "
                + "§ The hierarchy.",
                nameof(members)
            );
    }

    static Result<ScopeCollectionId> Invalid(string message) =>
        Result<ScopeCollectionId>.Failure(ErrorCode.InvalidResourceId, message);
}
