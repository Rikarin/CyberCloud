using System.Globalization;

namespace CyberCloud.Core.Resources;

/// <summary>
///     The address of an addressable thing, in the Azure shape docs/plan/06:36-39 specifies:
///     <code>
///     /tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{rgName}
///       /providers/{providerNamespace}/{resourceType}/{resourceName}
///     </code>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06:41-45 — the GUID and the path answer different questions. The GUID is the
///         identity, stable across renames, used in tuples, metering records and grain keys. The
///         path is the address: human-readable, hierarchical, what appears in a URL, what a role
///         assignment scopes to, and what a support engineer pastes into a ticket.
///     </para>
///     <para>
///         ⚠ <b><see cref="Path" /> does not contain <see cref="Id" />, so
///         <see cref="TryParsePath" /> cannot recover it and returns
///         <see cref="Guid.Empty" />.</b> This is not an omission here — it is what docs/plan/06:44
///         says the system does: <c>IResourceIndexGrain</c> maps path to GUID, the mapping changes
///         when a resource is renamed, and the GUID does not. A parsed <see cref="ResourceId" /> is
///         therefore an <i>address</i> awaiting resolution; call <see cref="WithId" /> once the
///         index has answered. The round-trip property is exact for every other component — see
///         <c>ResourceIdTests.PathRoundTrips</c>.
///     </para>
///     <para>
///         <b>Construction validates.</b> <see cref="ResourceGroup" /> and <see cref="Name" /> must
///         satisfy <see cref="ResourceNaming" />, and the constructor throws if they do not. That is
///         the first of two independent defences against separator injection: you cannot build a
///         <see cref="ResourceId" /> whose <see cref="Path" /> would re-parse as a different id,
///         because you cannot get a <c>/</c> into a name in the first place. The second defence is
///         that <see cref="TryParsePath" /> re-validates every component it parses. Both are
///         necessary: the path grammar nests resource types (<c>servers/databases</c>), so a name
///         containing <c>/</c> would silently shift the type/name boundary. See
///         <c>ResourceIdTests</c> § separator injection.
///     </para>
/// </remarks>
/// <param name="TenantId">The owning tenant. docs/plan/06:8.</param>
/// <param name="SubscriptionId">The billing and quota boundary. docs/plan/06:10.</param>
/// <param name="ResourceGroup">The lifecycle boundary's name. docs/plan/06:11.</param>
/// <param name="Type">The provider namespace and resource type.</param>
/// <param name="Name">The resource's name within its group.</param>
/// <param name="Id">
///     The resource's GUID, or <see cref="Guid.Empty" /> when this id came from a path.
/// </param>
public readonly record struct ResourceId(
    Guid TenantId,
    Guid SubscriptionId,
    string ResourceGroup,
    ResourceTypeName Type,
    string Name,
    Guid Id)
{
    const string TenantsSegment = "tenants";
    const string SubscriptionsSegment = "subscriptions";
    const string ResourceGroupsSegment = "resourceGroups";
    const string ProvidersSegment = "providers";

    /// <summary>The lifecycle boundary's name. Validated on construction and on <c>with</c>.</summary>
    public string ResourceGroup
    {
        get;
        init => field = ResourceNaming.EnsureValid(value, nameof(ResourceGroup), "resource group name");
    } = ResourceNaming.EnsureValid(ResourceGroup, nameof(ResourceGroup), "resource group name");

    /// <summary>The resource's name. Validated on construction and on <c>with</c>.</summary>
    public string Name
    {
        get;
        init => field = ResourceNaming.EnsureValid(value, nameof(Name), "resource name");
    } = ResourceNaming.EnsureValid(Name, nameof(Name), "resource name");

    /// <summary>The provider namespace and resource type. Must not be default.</summary>
    public ResourceTypeName Type
    {
        get;
        init => field = EnsureType(value);
    } = EnsureType(Type);

    /// <summary>
    ///     The address, exactly as docs/plan/06:52-53 defines it. Both GUIDs use the <c>D</c>
    ///     format (hyphenated, no braces).
    /// </summary>
    public string Path => string.Create(
        CultureInfo.InvariantCulture,
        $"/{TenantsSegment}/{TenantId:D}/{SubscriptionsSegment}/{SubscriptionId:D}"
        + $"/{ResourceGroupsSegment}/{ResourceGroup}/{ProvidersSegment}/{Type.Namespace}/{Type.Type}/{Name}");

    /// <summary>
    ///     The path with the provider namespace and type lower-cased, for hashing and indexing.
    /// </summary>
    /// <remarks>
    ///     docs/plan/06:77 keys <c>IResourceIndexGrain</c> on <c>idx/path/{sha256(path)[..16]}</c>.
    ///     It does not say <i>which</i> path, and it matters: the resource group and the resource
    ///     name are already forced lower-case by <see cref="ResourceNaming" />, but the provider
    ///     namespace and type are case-preserving, so <c>Path</c> alone is not a canonical form and
    ///     hashing it would let <c>CyberCloud.Cache/redis</c> and <c>cybercloud.cache/redis</c>
    ///     claim two index entries for one name. Hash this.
    /// </remarks>
    public string CanonicalPath => string.Create(
        CultureInfo.InvariantCulture,
        $"/{TenantsSegment}/{TenantId:D}/{SubscriptionsSegment}/{SubscriptionId:D}"
        + $"/{ResourceGroupsSegment}/{ResourceGroup}/{ProvidersSegment}"
        + $"/{ResourceTypeName.AsciiLower(Type.Namespace)}/{ResourceTypeName.AsciiLower(Type.Type)}/{Name}");

    /// <summary>Returns this id with its GUID set — the step after the index resolves a path.</summary>
    public ResourceId WithId(Guid id) => this with { Id = id };

    /// <summary>
    ///     Parses a resource id path. Returns <see langword="false" /> for anything that is not
    ///     exactly one, and never throws.
    /// </summary>
    /// <param name="path">The candidate path. May be <see langword="null" />.</param>
    /// <param name="id">
    ///     The parsed id on success, with <see cref="Id" /> set to <see cref="Guid.Empty" /> — see
    ///     the remarks on the type.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>Case.</b> The four structural literals — <c>tenants</c>, <c>subscriptions</c>,
    ///         <c>resourceGroups</c>, <c>providers</c> — are matched case-insensitively, because a
    ///         support engineer pasting <c>/ResourceGroups/</c> out of a document should not get a
    ///         parse error. The <i>values</i> are not folded: <c>/resourceGroups/PROD</c> fails,
    ///         because <c>PROD</c> is not a legal name (docs/plan/06:88) and folding it to
    ///         <c>prod</c> would be exactly the mangling docs/plan/06:92-94 forbids. Use
    ///         <see cref="ParsePath" /> to get a message that says so. Round-tripping is unaffected:
    ///         <see cref="Path" /> always emits the canonical literals and the values are already
    ///         lower-case.
    ///     </para>
    ///     <para>
    ///         <b>GUIDs are parsed by <see cref="GuidFormat.TryParseD" />.</b> The braced
    ///         (<c>{…}</c>), parenthesised (<c>(…)</c>), bare-hex (<c>N</c>) and hex-array
    ///         (<c>X</c>) forms are all rejected. <see cref="Guid.TryParse(string, out Guid)" />
    ///         accepts every one of them, which would make five spellings of one path — and five
    ///         index entries for one resource. <see cref="Guid.TryParseExact(string, string, out Guid)" />
    ///         is not enough on its own either: it trims surrounding whitespace. See the remarks on
    ///         <see cref="GuidFormat" />.
    ///     </para>
    ///     <para>
    ///         Every component is re-validated as it is parsed. An empty segment (a doubled or
    ///         trailing slash), a missing <c>/providers/</c>, a wrong segment count, an over-deep
    ///         type, or an illegal character in a name all fail here.
    ///     </para>
    /// </remarks>
    public static bool TryParsePath(string? path, out ResourceId id)
    {
        id = default;
        var parsed = ParsePath(path);
        if (parsed.IsFailure)
        {
            return false;
        }

        id = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary>
    ///     <see cref="TryParsePath" /> with an explanation. The message names the offending value,
    ///     per docs/plan/08:187.
    /// </summary>
    public static Result<ResourceId> ParsePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Invalid("A resource id path is required. It looks like "
                + "'/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{rg}"
                + "/providers/{namespace}/{type}/{name}' — see docs/plan/06 § Identifiers.");
        }

        if (path[0] != '/')
        {
            return Invalid($"'{path}' is not a resource id path: it must start with '/'.");
        }

        // Split on '/' after dropping the leading one. An empty element means a doubled slash, a
        // trailing slash, or a genuinely empty segment — all malformed, none of them silently
        // absorbed.
        var segments = path[1..].Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return Invalid(
                    $"'{path}' is not a resource id path: it contains an empty segment (a doubled "
                    + "or trailing '/').");
            }
        }

        // 0:tenants 1:{guid} 2:subscriptions 3:{guid} 4:resourceGroups 5:{rg} 6:providers
        // 7:{namespace} 8..^1:{type…} ^1:{name}
        const int fixedPrefix = 8;
        if (segments.Length < fixedPrefix + 2)
        {
            return Invalid(
                "'" + path + "' is not a resource id path: it has "
                + segments.Length.ToString(CultureInfo.InvariantCulture)
                + " segments and the shortest valid path has "
                + (fixedPrefix + 2).ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (!IsLiteral(segments[0], TenantsSegment)
            || !IsLiteral(segments[2], SubscriptionsSegment)
            || !IsLiteral(segments[4], ResourceGroupsSegment)
            || !IsLiteral(segments[6], ProvidersSegment))
        {
            return Invalid(
                $"'{path}' is not a resource id path: the structural segments must be "
                + $"'/{TenantsSegment}/…/{SubscriptionsSegment}/…/{ResourceGroupsSegment}/…"
                + $"/{ProvidersSegment}/…' (matched case-insensitively).");
        }

        if (!GuidFormat.TryParseD(segments[1], out var tenantId))
        {
            return Invalid(
                $"'{segments[1]}' is not a tenant id: a resource id path spells GUIDs in the "
                + "hyphenated 'D' form, for example '2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3'. "
                + "Braced, parenthesised and bare-hex forms are rejected so that one resource has "
                + "exactly one path.");
        }

        if (!GuidFormat.TryParseD(segments[3], out var subscriptionId))
        {
            return Invalid(
                $"'{segments[3]}' is not a subscription id: a resource id path spells GUIDs in the "
                + "hyphenated 'D' form.");
        }

        var resourceGroup = ResourceNaming.Validate(segments[5], "resource group name");
        if (resourceGroup.TryGetError(out var groupError))
        {
            return Result<ResourceId>.Failure(groupError);
        }

        var typePath = string.Join('/', segments, fixedPrefix, segments.Length - fixedPrefix - 1);
        var type = ResourceTypeName.Create(segments[7], typePath);
        if (type.TryGetError(out var typeError))
        {
            return Result<ResourceId>.Failure(typeError);
        }

        var name = ResourceNaming.Validate(segments[^1], "resource name");
        if (name.TryGetError(out var nameError))
        {
            return Result<ResourceId>.Failure(nameError);
        }

        return Result<ResourceId>.Success(
            new ResourceId(
                tenantId,
                subscriptionId,
                segments[5],
                type.GetValueOrThrow(),
                segments[^1],
                Guid.Empty));
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    static bool IsLiteral(string segment, string literal) =>
        string.Equals(segment, literal, StringComparison.OrdinalIgnoreCase);

    static Result<ResourceId> Invalid(string message) =>
        Result<ResourceId>.Failure(ErrorCode.InvalidResourceId, message);

    static ResourceTypeName EnsureType(ResourceTypeName type) =>
        type.IsEmpty
            ? throw new ArgumentException(
                "A resource id needs a resource type, for example "
                + "'CyberCloud.DBforPostgreSQL/servers'. default(ResourceTypeName) is not one.",
                nameof(type))
            : type;
}
