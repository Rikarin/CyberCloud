using System.Globalization;
using System.Text;

namespace CyberCloud.Core.Resources;

/// <summary>
///     Which of docs/plan/06 § The hierarchy's scopes an address names.
/// </summary>
/// <remarks>
///     ⚠ <b>The management group is deliberately absent.</b> docs/plan/06 § The hierarchy makes that
///     tree optional and docs/plan/01 puts it at M2, so there is no grain, no key and no parent
///     pointer for one — the same reason <c>ILockResolver</c> walks three scopes and not four. A
///     member here would be an address nothing could resolve.
/// </remarks>
public enum ScopeKind {
    /// <summary>Not a scope address.</summary>
    Unknown = 0,

    /// <summary>A tenant — <c>/tenants/{tenantId}</c>.</summary>
    Tenant,

    /// <summary>A subscription — <c>/tenants/{tenantId}/subscriptions/{subscriptionId}</c>.</summary>
    Subscription,

    /// <summary>A resource group — the subscription's path plus <c>/resourceGroups/{name}</c>.</summary>
    ResourceGroup
}

/// <summary>
///     The address of a <i>scope</i> — a tenant, a subscription or a resource group.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A scope is not a resource, and this type exists because <see cref="ResourceId" />
///         cannot say so.</b> docs/plan/06 § Identifiers gives a resource id path a fixed eight-segment
///         prefix ending in <c>/providers/{namespace}</c> followed by an even number of
///         <c>{type}/{name}</c> pairs. A scope has no provider, no type and no name pair, so every
///         scope address fails <see cref="ResourceId.ParsePath" /> — which is exactly what it did:
///         until this type existed, <c>PUT /tenants/{t}/subscriptions/{s}/resourceGroups/{rg}</c> was
///         a <see cref="ErrorCode.InvalidResourceId" /> <c>400</c>, and
///         <c>ISubscriptionGrain.CreateResourceGroupAsync</c> had no caller outside tests.
///     </para>
///     <para>
///         ⚠ <b>The grammar is a strict prefix of a resource id's, and that is the whole design.</b>
///         The three forms are the first two, four and six segments of
///         docs/plan/06 § Identifiers' path, spelled with the same literals, the same
///         <c>D</c>-form GUID rule and the same DNS-1123 name rule. A resource path is at least ten
///         segments, so nothing parses as both — <c>ScopeIdTests</c> asserts the disjointness rather
///         than assuming it, because the router now tries both and an overlap would make one of them
///         unreachable.
///     </para>
///     <para>
///         ⚠ <b><see cref="SubscriptionId" /> being <see cref="Guid.Empty" /> does <i>not</i> mean
///         "no subscription", and <see cref="TenantId" /> being <see cref="Guid.Empty" /> does not
///         mean "no tenant".</b> docs/plan/06 § Platform administration makes <c>Guid.Empty</c> the
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
public readonly record struct ScopeId(
    ScopeKind Kind,
    Guid TenantId,
    Guid SubscriptionId,
    string ResourceGroup
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

    /// <summary>The address, exactly as docs/plan/06 § Identifiers spells its prefix.</summary>
    public string Path {
        get {
            if (Kind == ScopeKind.Unknown) {
                return "";
            }

            var built = new StringBuilder(96)
                .Append('/').Append(ResourceId.TenantsSegment)
                .Append('/').Append(TenantId.ToString("D", CultureInfo.InvariantCulture));

            if (Kind == ScopeKind.Tenant) {
                return built.ToString();
            }

            built.Append('/').Append(ResourceId.SubscriptionsSegment)
                .Append('/').Append(SubscriptionId.ToString("D", CultureInfo.InvariantCulture));

            if (Kind == ScopeKind.Subscription) {
                return built.ToString();
            }

            return built.Append('/').Append(ResourceId.ResourceGroupsSegment)
                .Append('/').Append(ResourceGroup)
                .ToString();
        }
    }

    /// <summary>
    ///     The scope one level up, or <see langword="null" /> for a tenant.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the address the ReBAC <c>parent</c> edge points at, and a tenant having none
    ///     is the whole of the authorization problem this type surfaces.</b> <c>CyberCloudSchema</c>
    ///     gives <c>subscription</c> and <c>resourceGroup</c> a <c>parent</c> relation and every role
    ///     a <c>From("parent", …)</c> rewrite, so a subscription's permissions resolve through its
    ///     tenant and a group's through its subscription. <c>tenant</c> has no <c>parent</c> relation
    ///     at all: nothing is above it, so a <c>Check</c> on a tenant has nothing to resolve through
    ///     and only a <i>direct</i> tuple can grant on one. See
    ///     <c>IScopeManager.CreateTenantAsync</c> for what follows from that.
    /// </remarks>
    public ScopeId? Parent =>
        Kind switch {
            ScopeKind.ResourceGroup => Subscription(TenantId, SubscriptionId),
            ScopeKind.Subscription => Tenant(TenantId),
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
    ///         <b>Case.</b> The three structural literals are matched case-insensitively and the
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
    /// </remarks>
    public static Result<ScopeId> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A scope path is required. It looks like '/tenants/{tenantId}', "
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
                + " segments and a scope address has 2 (a tenant), 4 (a subscription) or 6 (a "
                + "resource group)."
            );
        }

        if (!IsLiteral(segments[0], ResourceId.TenantsSegment)
            || (segments.Length > 2 && !IsLiteral(segments[2], ResourceId.SubscriptionsSegment))
            || (segments.Length > 4 && !IsLiteral(segments[4], ResourceId.ResourceGroupsSegment))) {
            return Invalid(
                $"'{path}' is not a scope path: the structural segments must be "
                + $"'/{ResourceId.TenantsSegment}/…/{ResourceId.SubscriptionsSegment}/…"
                + $"/{ResourceId.ResourceGroupsSegment}/…' (matched case-insensitively)."
            );
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

    /// <inheritdoc />
    public override string ToString() => Path;

    static bool IsLiteral(string segment, string literal) =>
        string.Equals(segment, literal, StringComparison.OrdinalIgnoreCase);

    static Result<ScopeId> Invalid(string message) =>
        Result<ScopeId>.Failure(ErrorCode.InvalidResourceId, message);
}
