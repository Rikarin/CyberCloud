using System.Globalization;

namespace CyberCloud.Core.Resources;

/// <summary>
///     The <b>only</b> type in Cyber Cloud allowed to format or parse a grain key — ADR-002,
///     docs/plan/02:127-141 and docs/plan/06:69-70.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a string key at all.</b> The brief says "GUID as ID";
///         <c>Orleans.Multitenant</c> only supports string keys. Both requirements are satisfied:
///         identifiers in the API, the storage, the SDK and the URL are GUIDs, and the grain key is
///         a composed string that contains them. Nothing else in the codebase may concatenate one.
///     </para>
///     <para>
///         <b>Where this key ends up.</b> <see cref="ToKeyWithinTenant" /> produces the
///         <i>within-tenant</i> half. <c>Orleans.Multitenant</c> prepends the tenant and hands the
///         result to <c>IGrainFactory</c>. The physical key is
///         <c>{escapedTenantId}|{keyWithinTenant}</c> — <b>verified</b> against
///         <c>Orleans.Multitenant</c> 4.0.0's shipped assembly, internal type
///         <c>Orleans.Multitenant.Internal.TenantIdExtensions</c>, methods <c>AsTenantId</c> and
///         <c>GetTenantQualifiedKey</c>. Precisely:
///     </para>
///     <list type="bullet">
///         <item>
///             The tenant id string has each <c>'|'</c> doubled, then a single <c>'|'</c> is
///             appended as the terminator.
///         </item>
///         <item>
///             ⚠ The key within the tenant is <b>copied verbatim</b> — its <c>'|'</c> characters are
///             <b>not</b> escaped. Decoding still works, because the terminator is the first
///             <i>un</i>doubled <c>'|'</c> and the tenant id can no longer contain one.
///         </item>
///         <item>
///             A single <c>'~'</c> is prefixed when, and only when, the key within the tenant starts
///             with <c>'|'</c> or <c>'~'</c>.
///         </item>
///         <item>
///             For a <i>null-tenant</i> grain there is no prefix and no <c>'~'</c> rule; instead the
///             whole key has its <c>'|'</c> doubled.
///         </item>
///     </list>
///     <para>
///         The key this type produces starts with a hexadecimal digit and contains only
///         <c>[0-9a-f]</c>, <c>[A-Za-z]</c>, <c>-</c>, <c>.</c> and <c>/</c>, so it survives every
///         one of those branches untouched — see
///         <see cref="IsTenantQualificationSafe" /> and <c>ResourceKeyTests</c>.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/06:76 and docs/plan/02:138 disagree about this key's shape.</b> The grain
///         key table says <c>IResourceGrain</c> is keyed <c>res/{resourceId:N}</c>; ADR-002's code
///         block says <c>{subscriptionId:N}/{resourceGroup}/{type}/{resourceId:N}</c>. This type
///         implements ADR-002's form, because that is the form the ADR makes normative and the one
///         the deliverable names. The two must be reconciled before <c>IResourceGrain</c> lands —
///         they are different keys for the same grain, and picking the wrong one after grains exist
///         is a migration.
///     </para>
/// </remarks>
/// <param name="SubscriptionId">The owning subscription.</param>
/// <param name="ResourceGroup">The owning resource group's name.</param>
/// <param name="Type">The resource type.</param>
/// <param name="ResourceId">The resource's GUID — the identity, stable across renames.</param>
public readonly record struct ResourceKey(
    Guid SubscriptionId,
    string ResourceGroup,
    ResourceTypeName Type,
    Guid ResourceId)
{
    /// <summary>The owning resource group's name. Validated on construction and on <c>with</c>.</summary>
    public string ResourceGroup
    {
        get;
        init => field = ResourceNaming.EnsureValid(value, nameof(ResourceGroup), "resource group name");
    } = ResourceNaming.EnsureValid(ResourceGroup, nameof(ResourceGroup), "resource group name");

    /// <summary>The resource type. Must not be default.</summary>
    public ResourceTypeName Type
    {
        get;
        init => field = EnsureType(value);
    } = EnsureType(Type);

    /// <summary>
    ///     Formats the within-tenant grain key, exactly as ADR-002 (docs/plan/02:138) specifies:
    ///     <c>{SubscriptionId:N}/{ResourceGroup}/{Type}/{ResourceId:N}</c>, where <c>{Type}</c> is
    ///     itself <c>{namespace}/{typePath}</c> and may nest (<c>servers/databases</c>).
    /// </summary>
    /// <remarks>
    ///     GUIDs use the <c>N</c> format here and the <c>D</c> format in
    ///     <see cref="Resources.ResourceId.Path" />. That asymmetry is docs/plan/06's, not ours; both
    ///     are parsed with <c>TryParseExact</c> against the format they are written in, so a key
    ///     spelled with hyphens or braces is rejected rather than accepted as a second spelling of
    ///     the same grain.
    /// </remarks>
    public string ToKeyWithinTenant() => string.Create(
        CultureInfo.InvariantCulture,
        $"{SubscriptionId:N}/{ResourceGroup}/{Type.Namespace}/{Type.Type}/{ResourceId:N}");

    /// <summary>Builds the key for a resource id. Requires a resolved <see cref="Resources.ResourceId.Id" />.</summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="id" /> has no GUID — it came from a path and has not been resolved
    ///     through <c>IResourceIndexGrain</c> yet. Keying a grain on <see cref="Guid.Empty" /> would
    ///     collide every unresolved resource in the tenant onto one activation.
    /// </exception>
    public static ResourceKey From(ResourceId id) =>
        id.Id == Guid.Empty
            ? throw new ArgumentException(
                $"'{id.Path}' has no resource GUID, so it cannot be turned into a grain key. A "
                + "ResourceId parsed from a path carries Guid.Empty until IResourceIndexGrain "
                + "resolves it — see docs/plan/06 § Identifiers.",
                nameof(id))
            : new ResourceKey(id.SubscriptionId, id.ResourceGroup, id.Type, id.Id);

    /// <summary>
    ///     Parses a within-tenant grain key. Returns <see langword="false" /> for anything that is
    ///     not exactly one, and never throws.
    /// </summary>
    public static bool TryParse(string? keyWithinTenant, out ResourceKey key)
    {
        key = default;
        var parsed = Parse(keyWithinTenant);
        if (parsed.IsFailure)
        {
            return false;
        }

        key = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary><see cref="TryParse" /> with an explanation.</summary>
    public static Result<ResourceKey> Parse(string? keyWithinTenant)
    {
        if (string.IsNullOrEmpty(keyWithinTenant))
        {
            return Invalid("A grain key within a tenant is required. It looks like "
                + "'{subscriptionId:N}/{resourceGroup}/{namespace}/{type}/{resourceId:N}' — see "
                + "docs/plan/02 ADR-002.");
        }

        var segments = keyWithinTenant.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return Invalid(
                    $"'{keyWithinTenant}' is not a grain key: it contains an empty segment.");
            }
        }

        // 0:{sub:N} 1:{rg} 2:{namespace} 3..^1:{type…} ^1:{res:N}
        const int fixedPrefix = 3;
        if (segments.Length < fixedPrefix + 2)
        {
            return Invalid(
                "'" + keyWithinTenant + "' is not a grain key: it has "
                + segments.Length.ToString(CultureInfo.InvariantCulture)
                + " segments and the shortest valid key has "
                + (fixedPrefix + 2).ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (!GuidFormat.TryParseN(segments[0], out var subscriptionId))
        {
            return Invalid(
                $"'{segments[0]}' is not a subscription id: a grain key spells GUIDs in the "
                + "32-digit 'N' form, with no hyphens and no braces.");
        }

        if (!GuidFormat.TryParseN(segments[^1], out var resourceId))
        {
            return Invalid(
                $"'{segments[^1]}' is not a resource id: a grain key spells GUIDs in the 32-digit "
                + "'N' form, with no hyphens and no braces.");
        }

        var resourceGroup = ResourceNaming.Validate(segments[1], "resource group name");
        if (resourceGroup.TryGetError(out var groupError))
        {
            return Result<ResourceKey>.Failure(
                new Error(ErrorCode.InvalidGrainKey, groupError.Message));
        }

        var typePath = string.Join('/', segments, fixedPrefix, segments.Length - fixedPrefix - 1);
        var type = ResourceTypeName.Create(segments[2], typePath);
        if (type.TryGetError(out var typeError))
        {
            return Result<ResourceKey>.Failure(
                new Error(ErrorCode.InvalidGrainKey, typeError.Message));
        }

        return Result<ResourceKey>.Success(
            new ResourceKey(subscriptionId, segments[1], type.GetValueOrThrow(), resourceId));
    }

    /// <summary>
    ///     Whether <paramref name="keyWithinTenant" /> passes through
    ///     <c>Orleans.Multitenant</c>'s tenant qualification unchanged — no <c>'|'</c> anywhere and
    ///     no leading <c>'|'</c> or <c>'~'</c>.
    /// </summary>
    /// <remarks>
    ///     A key that fails this still round-trips (the encoding is lossless, verified against the
    ///     shipped assembly). What it loses is <i>legibility</i>: the physical key stored in Redis,
    ///     printed in a log and shown in a trace no longer reads as the key you constructed. Since
    ///     every key Cyber Cloud builds can trivially satisfy this, keys that do not are a bug worth
    ///     catching — which is why this predicate is public. It is the assertion any future
    ///     key-formatting type should be held to.
    /// </remarks>
    public static bool IsTenantQualificationSafe(string? keyWithinTenant)
    {
        if (string.IsNullOrEmpty(keyWithinTenant))
        {
            return false;
        }

        if (keyWithinTenant[0] is '|' or '~')
        {
            return false;
        }

        return !keyWithinTenant.Contains('|', StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString() => ToKeyWithinTenant();

    static Result<ResourceKey> Invalid(string message) =>
        Result<ResourceKey>.Failure(ErrorCode.InvalidGrainKey, message);

    static ResourceTypeName EnsureType(ResourceTypeName type) =>
        type.IsEmpty
            ? throw new ArgumentException(
                "A grain key needs a resource type, for example "
                + "'CyberCloud.DBforPostgreSQL/servers'. default(ResourceTypeName) is not one.",
                nameof(type))
            : type;
}
