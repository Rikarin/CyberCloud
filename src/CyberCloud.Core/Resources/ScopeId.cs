using System.Globalization;
using System.Text;

namespace CyberCloud.Core.Resources;

/// <summary>
///     Which of docs/plan/06 § The hierarchy's scopes an address names.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The management group was deliberately absent until issue #39, and the sentence that
///             kept it out is worth keeping.
///         </b> It read: "docs/plan/06 § The hierarchy makes that tree
///         optional and docs/plan/01 puts it at M2, so there is no grain, no key and no parent
///         pointer for one … A member here would be an address nothing could resolve." All three
///         now exist — <c>IManagementGroupGrain</c>, <c>GrainKeys.ManagementGroup</c> and
///         <c>SubscriptionDescriptor.ManagementGroup</c> — and the member landed with them, in that
///         order, which is the order docs/plan/06 § Grain keys asks for.
///     </para>
///     <para>
///         ⚠ <b>The tree is optional and the tenant is its implicit root.</b> A subscription with no
///         management group hangs off the tenant exactly as it did before the kind existed; a group
///         with no parent group hangs off the tenant too. Nothing has to be created for a tenant that
///         never wants a group, and <c>ScopeId.Parent</c> reads <see cref="Tenant" /> for both — the
///         <i>address</i> cannot know a group's parent group or a subscription's group, because
///         neither is in the path. That is state, held by the grains, and
///         <c>IScopeRelationWriter</c> is told the real parent by the caller that read it.
///     </para>
/// </remarks>
public enum ScopeKind {
    /// <summary>Not a scope address.</summary>
    Unknown = 0,

    /// <summary>A tenant — <c>/tenants/{tenantId}</c>.</summary>
    Tenant,

    /// <summary>A subscription — <c>/tenants/{tenantId}/subscriptions/{subscriptionId}</c>.</summary>
    Subscription,

    /// <summary>A resource group — the subscription's path plus <c>/resourceGroups/{name}</c>.</summary>
    ResourceGroup,

    /// <summary>
    ///     A management group — <c>/tenants/{tenantId}/managementGroups/{name}</c>. The scope above
    ///     the subscription: an optional tree, one parent each, the tenant its implicit root.
    ///     docs/plan/06 § The hierarchy.
    /// </summary>
    ManagementGroup
}

/// <summary>
///     The address of a <i>scope</i> — a tenant, a management group, a subscription or a resource
///     group.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A scope is not a resource, and this type exists because <see cref="ResourceId" />
///             cannot say so.
///         </b> docs/plan/06 § Identifiers gives a resource id path a fixed eight-segment
///         prefix ending in <c>/providers/{namespace}</c> followed by an even number of
///         <c>{type}/{name}</c> pairs. A scope has no provider, no type and no name pair, so every
///         scope address fails <see cref="ResourceId.ParsePath" /> — which is exactly what it did:
///         until this type existed, <c>PUT /tenants/{t}/subscriptions/{s}/resourceGroups/{rg}</c> was
///         a <see cref="ErrorCode.InvalidResourceId" /> <c>400</c>, and
///         <c>ISubscriptionGrain.CreateResourceGroupAsync</c> had no caller outside tests.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Three of the four forms are a strict prefix of a resource id's, and that is the whole
///             design.
///         </b> The tenant, subscription and resource-group forms are the first two, four and
///         six segments of docs/plan/06 § Identifiers' path, spelled with the same literals, the same
///         <c>D</c>-form GUID rule and the same DNS-1123 name rule. A resource path is at least ten
///         segments, so nothing parses as both — <c>ScopeIdTests</c> asserts the disjointness rather
///         than assuming it, because the router now tries both and an overlap would make one of them
///         unreachable.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The management group is the fourth form and the first that is NOT a prefix of a
///             resource path — issue #39's "addressing-grammar change".
///         </b> It is four segments like a
///         subscription, and the two are told apart by the literal in the third:
///         <c>/tenants/{t}/managementGroups/{name}</c> against
///         <c>/tenants/{t}/subscriptions/{s}</c>. <see cref="ResourceId.ParsePath" />'s
///         <c>const int fixedPrefix = 8</c> is untouched, because a resource is never addressed
///         <i>through</i> a group: the group is where a subscription hangs, not where a resource
///         lives, and a resource path still runs tenant → subscription → resource group → provider.
///         What the grammar gained is one more four-segment shape and one more collection under the
///         tenant; what it did not gain is a second address for anything that already had one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="SubscriptionId" /> being <see cref="Guid.Empty" /> does <i>not</i> mean
///             "no subscription", and <see cref="TenantId" /> being <see cref="Guid.Empty" /> does not
///             mean "no tenant".
///         </b> docs/plan/06 § Platform administration makes <c>Guid.Empty</c> the
///         <i>platform tenant</i> — an ordinary id that happens to be all zeroes. <see cref="Kind" />
///         is the only discriminator, and reading a GUID for one would make the platform tenant
///         unaddressable.
///     </para>
/// </remarks>
/// <param name="Kind">Which scope this address names.</param>
/// <param name="TenantId">The tenant. Always meaningful.</param>
/// <param name="SubscriptionId">
///     The subscription, for <see cref="ScopeKind.Subscription" /> and
///     <see cref="ScopeKind.ResourceGroup" />.
/// </param>
/// <param name="ResourceGroup">The group name, for <see cref="ScopeKind.ResourceGroup" />.</param>
/// <param name="ManagementGroup">
///     The management group's name, for <see cref="ScopeKind.ManagementGroup" />. ⚠ A DNS-1123 name
///     unique within the tenant, not a GUID: it is what a person types into a policy and reads in a
///     scope picker, and the tenant-qualified grain key makes it unique by construction.
/// </param>
public readonly record struct ScopeId(
    ScopeKind Kind,
    Guid TenantId,
    Guid SubscriptionId,
    string ResourceGroup,
    string ManagementGroup = ""
) {
    /// <summary>The address of a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static ScopeId Tenant(Guid tenantId) => new(ScopeKind.Tenant, tenantId, Guid.Empty, "");

    /// <summary>The address of a subscription.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    public static ScopeId Subscription(Guid tenantId, Guid subscriptionId) =>
        new(ScopeKind.Subscription, tenantId, subscriptionId, "");

    /// <summary>The address of a resource group.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="subscriptionId">The owning subscription.</param>
    /// <param name="name">The DNS-1123 group name.</param>
    public static ScopeId Group(Guid tenantId, Guid subscriptionId, string name) =>
        new(ScopeKind.ResourceGroup, tenantId, subscriptionId, name);

    /// <summary>The address of a management group.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="name">The DNS-1123 group name, unique within the tenant.</param>
    public static ScopeId ManagementGroupOf(Guid tenantId, string name) =>
        new(ScopeKind.ManagementGroup, tenantId, Guid.Empty, "", name);

    /// <summary>The address, exactly as docs/plan/06 § Identifiers spells its prefix.</summary>
    public string Path {
        get {
            if (Kind == ScopeKind.Unknown) {
                return "";
            }

            var built = new StringBuilder(96)
                .Append('/')
                .Append(ResourceId.TenantsSegment)
                .Append('/')
                .Append(TenantId.ToString("D", CultureInfo.InvariantCulture));

            if (Kind == ScopeKind.Tenant) {
                return built.ToString();
            }

            if (Kind == ScopeKind.ManagementGroup) {
                return built.Append('/')
                    .Append(ResourceId.ManagementGroupsSegment)
                    .Append('/')
                    .Append(ManagementGroup)
                    .ToString();
            }

            built.Append('/')
                .Append(ResourceId.SubscriptionsSegment)
                .Append('/')
                .Append(SubscriptionId.ToString("D", CultureInfo.InvariantCulture));

            if (Kind == ScopeKind.Subscription) {
                return built.ToString();
            }

            return built.Append('/')
                .Append(ResourceId.ResourceGroupsSegment)
                .Append('/')
                .Append(ResourceGroup)
                .ToString();
        }
    }

    /// <summary>
    ///     The scope one level up <i>as the address spells it</i>, or <see langword="null" /> for a
    ///     tenant.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This is the address the ReBAC <c>parent</c> edge points at, and a tenant having none
    ///             is the whole of the authorization problem this type surfaces.
    ///         </b> <c>CyberCloudSchema</c>
    ///         gives <c>managementGroup</c>, <c>subscription</c> and <c>resourceGroup</c> a
    ///         <c>parent</c> relation and every role a <c>From("parent", …)</c> rewrite, so a
    ///         subscription's permissions resolve through its parent and a group's through its
    ///         subscription. <c>tenant</c> has no <c>parent</c> relation at all: nothing is above it,
    ///         so a <c>Check</c> on a tenant has nothing to resolve through and only a <i>direct</i>
    ///         tuple can grant on one. See <c>IScopeManager.CreateTenantAsync</c> for what follows
    ///         from that.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             For a management group and for a subscription this is the tenant, and the tenant
    ///             is only the <i>default</i>.
    ///         </b> The address of a group does not carry its parent group
    ///         and the address of a subscription does not carry the group it is assigned to — both are
    ///         state, in <c>ManagementGroupDescriptor.Parent</c> and
    ///         <c>SubscriptionDescriptor.ManagementGroup</c>. The tenant is the implicit root of the
    ///         tree (docs/plan/06 § The hierarchy), so it is the right answer whenever nothing else is
    ///         recorded, and it is the wrong answer to write a tuple from without reading the record
    ///         first — <c>ScopeManagerService</c> reads it and hands
    ///         <c>IScopeRelationWriter.LinkToParentAsync</c> the real parent.
    ///     </para>
    /// </remarks>
    public ScopeId? Parent =>
        Kind switch {
            ScopeKind.ResourceGroup => Subscription(TenantId, SubscriptionId),
            ScopeKind.Subscription => Tenant(TenantId),
            ScopeKind.ManagementGroup => Tenant(TenantId),
            _ => null
        };

    /// <summary>
    ///     Parses a scope address. Returns <see langword="false" /> for anything that is not exactly
    ///     one, and never throws.
    /// </summary>
    /// <param name="path">The candidate path. May be <see langword="null" />.</param>
    /// <param name="id">The parsed address on success.</param>
    public static bool TryParsePath(string? path, out ScopeId id) {
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
    ///         <b>Case.</b> The four structural literals are matched case-insensitively and the
    ///         <i>values</i> are not folded — the same split <see cref="ResourceId.ParsePath" /> makes
    ///         and for the same reason: a support engineer pasting <c>/ResourceGroups/</c> should not
    ///         get a parse error, and folding <c>PROD</c> to <c>prod</c> would be the mangling
    ///         docs/plan/06 § Identifiers forbids.
    ///     </para>
    ///     <para>
    ///         <b>GUIDs are the hyphenated <c>D</c> form only</b>, through
    ///         <see cref="GuidFormat.TryParseD" />. Five spellings of one address are five cache
    ///         entries and five audit rows, which is the rule the resource path already applies.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Four segments is two shapes, and the third segment decides.</b>
    ///         <c>subscriptions</c> is followed by a GUID and <c>managementGroups</c> by a DNS-1123
    ///         name, so <c>/tenants/{t}/subscriptions/prod</c> and
    ///         <c>/tenants/{t}/managementGroups/{guid}</c> are each refused with a message about the
    ///         value rather than silently read as the other shape.
    ///     </para>
    /// </remarks>
    public static Result<ScopeId> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A scope path is required. It looks like '/tenants/{tenantId}', "
                + "'/tenants/{tenantId}/managementGroups/{name}', "
                + "'/tenants/{tenantId}/subscriptions/{subscriptionId}' or that followed by "
                + "'/resourceGroups/{name}' — see docs/plan/06 § The hierarchy."
            );
        }

        if (path[0] != '/') {
            return Invalid($"'{path}' is not a scope path: it must start with '/'.");
        }

        var segments = path[1..].Split('/');

        foreach (var segment in segments) {
            if (segment.Length == 0) {
                return Invalid(
                    $"'{path}' is not a scope path: it contains an empty segment (a doubled or "
                    + "trailing '/')."
                );
            }
        }

        if (segments.Length is not (2 or 4 or 6)) {
            return Invalid(
                "'"
                + path
                + "' is not a scope path: it has "
                + segments.Length.ToString(CultureInfo.InvariantCulture)
                + " segments and a scope address has 2 (a tenant), 4 (a subscription or a management "
                + "group) or 6 (a resource group)."
            );
        }

        if (!IsLiteral(segments[0], ResourceId.TenantsSegment)) {
            return StructureInvalid(path);
        }

        if (!GuidFormat.TryParseD(segments[1], out var tenantId)) {
            return Invalid(
                $"'{segments[1]}' is not a tenant id: a scope path spells GUIDs in the hyphenated "
                + "'D' form, for example '2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3'. Braced, "
                + "parenthesised and bare-hex forms are rejected so that one scope has exactly one "
                + "path."
            );
        }

        if (segments.Length == 2) {
            return Result<ScopeId>.Success(Tenant(tenantId));
        }

        if (segments.Length == 4 && IsLiteral(segments[2], ResourceId.ManagementGroupsSegment)) {
            var groupName = ResourceNaming.Validate(segments[3], "management group name");

            return groupName.TryGetError(out var groupNameError)
                ? Result<ScopeId>.Failure(groupNameError)
                : Result<ScopeId>.Success(ManagementGroupOf(tenantId, segments[3]));
        }

        if (!IsLiteral(segments[2], ResourceId.SubscriptionsSegment)
            || (segments.Length > 4 && !IsLiteral(segments[4], ResourceId.ResourceGroupsSegment))) {
            return StructureInvalid(path);
        }

        if (!GuidFormat.TryParseD(segments[3], out var subscriptionId)) {
            return Invalid(
                $"'{segments[3]}' is not a subscription id: a scope path spells GUIDs in the "
                + "hyphenated 'D' form."
            );
        }

        if (segments.Length == 4) {
            return Result<ScopeId>.Success(Subscription(tenantId, subscriptionId));
        }

        var name = ResourceNaming.Validate(segments[5], "resource group name");

        return name.TryGetError(out var nameError)
            ? Result<ScopeId>.Failure(nameError)
            : Result<ScopeId>.Success(Group(tenantId, subscriptionId, segments[5]));
    }

    /// <summary>
    ///     Parses a scope <i>collection</i> path — <c>/tenants/{t}/subscriptions</c>,
    ///     <c>/tenants/{t}/managementGroups</c> or
    ///     <c>/tenants/{t}/subscriptions/{s}/resourceGroups</c> — to the scope whose children it
    ///     lists. Returns <see langword="false" /> for anything else, and never throws.
    /// </summary>
    /// <param name="path">The candidate path. May be <see langword="null" />.</param>
    /// <param name="parent">The tenant or subscription the collection hangs off, on success.</param>
    /// <remarks>
    ///     <see cref="ScopeCollectionId.TryParsePath" /> with the wrapper removed, for a caller that
    ///     holds the parent's address and asks the parent's grain — which is every caller, because a
    ///     scope collection has no grain of its own. ⚠ Since issue #39 a tenant has <i>two</i>
    ///     collections, so the parent alone no longer says which one was asked for; a caller that
    ///     needs to know keeps the <see cref="ScopeCollectionId" /> and reads
    ///     <see cref="ScopeCollectionId.MemberKind" />.
    /// </remarks>
    public static bool TryParseCollectionParent(string? path, out ScopeId parent) {
        if (ScopeCollectionId.TryParsePath(path, out var collection)) {
            parent = collection.Parent;
            return true;
        }

        parent = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    static bool IsLiteral(string segment, string literal) =>
        string.Equals(segment, literal, StringComparison.OrdinalIgnoreCase);

    static Result<ScopeId> StructureInvalid(string path) =>
        Invalid(
            $"'{path}' is not a scope path: the structural segments must be "
            + $"'/{ResourceId.TenantsSegment}/…/{ResourceId.SubscriptionsSegment}/…"
            + $"/{ResourceId.ResourceGroupsSegment}/…' or "
            + $"'/{ResourceId.TenantsSegment}/…/{ResourceId.ManagementGroupsSegment}/…' (matched "
            + "case-insensitively)."
        );

    static Result<ScopeId> Invalid(string message) => Result<ScopeId>.Failure(ErrorCode.InvalidResourceId, message);
}
