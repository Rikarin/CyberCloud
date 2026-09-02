using System.Globalization;
using System.Text;

namespace CyberCloud.Core.Resources;

/// <summary>
///     The address of a <b>collection</b> of resources of one type, inside one resource group:
///     <code>
///     /tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{rgName}
///       /providers/{providerNamespace}/{resourceType}
///     </code>
///     A nested type interleaves exactly as <see cref="ResourceId" /> does, and ends on the type
///     rather than on a name:
///     <code>
///     …/providers/CyberCloud.DBforPostgreSQL/servers/{serverName}/databases
///     </code>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a resource-group-scoped address and deliberately not a subscription-scoped
///         one.</b> <c>SoftDeletePolicy.RestoreAction</c>'s remarks record that Key Vault's
///         subscription+location-scoped <c>deletedVaults</c> collection "cannot be built here,
///         because <c>ResourceId.ParsePath</c> has <c>const int fixedPrefix = 8</c> and there is no
///         subscription-scoped address for the collection to live at". That is still true and this
///         type does not change it: every address below carries the same eight fixed segments a
///         resource does — <c>tenants</c>, <c>subscriptions</c>, <c>resourceGroups</c>,
///         <c>providers</c> and their four values — and differs from a resource only in what comes
///         after them. A subscription-scoped collection would be a change to the addressing grammar's
///         fixed prefix, which is a separate decision and is not taken here.
///     </para>
///     <para>
///         ⚠ <b>The tail is ODD, which is exactly the complement of <see cref="ResourceId" />'s.</b>
///         <see cref="ResourceId.ParsePath" /> refuses an odd tail with "one type has no name or one
///         name has no type"; that shape is not a malformed resource, it is a well-formed collection.
///         The two grammars therefore partition every path with the fixed prefix and neither can
///         accept the other's, so nothing has to decide between them by looking at the verb or at
///         anything else outside the path. That partition is the reason this is a second parser
///         rather than a flag on the first: a single parser that could return "resource or
///         collection" would put the choice at every call site, and the call sites that forgot would
///         address a collection as a resource named after its own type.
///     </para>
///     <para>
///         ⚠ <b>It carries no <c>Id</c> and never will.</b> A collection is not an entity: it has no
///         GUID, no index entry, no ReBAC object and no grain. It is a query against
///         <c>IResourceGroupGrain</c>'s membership, filtered to one type, and every authorization
///         decision about it is a decision about the resources it would return — see
///         <c>IResourceManager.ListAsync</c>.
///     </para>
/// </remarks>
/// <param name="TenantId">The owning tenant. docs/plan/06 § The hierarchy.</param>
/// <param name="SubscriptionId">The billing and quota boundary.</param>
/// <param name="ResourceGroup">The lifecycle boundary's name — the scope the listing runs at.</param>
/// <param name="Type">The provider namespace and resource type being listed.</param>
/// <param name="ParentNames">
///     The ancestors' names, outermost first, <c>/</c>-separated. Empty for a top-level type, and
///     one shorter than the type's depth for a nested one — the same invariant, and the same reason,
///     as <see cref="ResourceId.ParentNames" />.
/// </param>
public readonly record struct ResourceCollectionId(
    Guid TenantId,
    Guid SubscriptionId,
    string ResourceGroup,
    ResourceTypeName Type,
    string ParentNames = ""
) {
    const string TenantsSegment = "tenants";
    const string SubscriptionsSegment = "subscriptions";
    const string ResourceGroupsSegment = "resourceGroups";
    const string ProvidersSegment = "providers";

    /// <summary>The lifecycle boundary's name. Validated on construction and on <c>with</c>.</summary>
    public string ResourceGroup {
        get;
        init => field = ResourceNaming.EnsureValid(value, nameof(ResourceGroup), "resource group name");
    } = ResourceNaming.EnsureValid(ResourceGroup, nameof(ResourceGroup), "resource group name");

    /// <summary>The type being listed. Must not be default.</summary>
    /// <remarks>
    ///     ⚠ The <c>init</c> re-checks the <see cref="ParentNames" /> invariant for the same reason
    ///     <see cref="ResourceId.Type" />'s does: a <c>with</c> expression runs one accessor at a
    ///     time, so re-typing a depth-1 collection to <c>servers/databases</c> without supplying the
    ///     server's name would otherwise render a path with a segment missing.
    /// </remarks>
    public ResourceTypeName Type {
        get;
        init {
            field = EnsureType(value);

            if (ParentNames is not null) {
                EnsureParents(ParentNames, field);
            }
        }
    } = EnsureType(Type);

    /// <summary>The ancestors' names, outermost first, joined by <c>/</c>.</summary>
    public string ParentNames {
        get;
        init => field = EnsureParents(value, Type);
    } = EnsureParents(ParentNames, Type);

    /// <summary>The address, rendered. Both GUIDs use the <c>D</c> format.</summary>
    public string Path => Render(Type.Namespace, Type.Type);

    /// <summary>
    ///     The address of one resource in this collection.
    /// </summary>
    /// <param name="name">The resource's name within the group.</param>
    /// <remarks>
    ///     ⚠ The returned id carries <see cref="Guid.Empty" /> for its <c>Id</c>, like every other
    ///     address that came from a path — resolving it is a lookup through
    ///     <c>IResourceIndexGrain</c>.
    /// </remarks>
    public ResourceId Member(string name) =>
        new(TenantId, SubscriptionId, ResourceGroup, Type, name, Guid.Empty, ParentNames);

    /// <summary>
    ///     The collection a resource belongs to — this type's inverse.
    /// </summary>
    /// <param name="id">The resource.</param>
    public static ResourceCollectionId Of(ResourceId id) =>
        new(id.TenantId, id.SubscriptionId, id.ResourceGroup, id.Type, id.ParentNames);

    /// <summary>
    ///     Parses a collection path. Returns <see langword="false" /> for anything that is not
    ///     exactly one, and never throws.
    /// </summary>
    /// <param name="path">The candidate path. May be <see langword="null" />.</param>
    /// <param name="id">The parsed address on success.</param>
    public static bool TryParsePath(string? path, out ResourceCollectionId id) {
        id = default;
        var parsed = ParsePath(path);
        if (parsed.IsFailure) {
            return false;
        }

        id = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary>
    ///     <see cref="TryParsePath" /> with an explanation. The message names the offending value,
    ///     per docs/plan/08 § Errors.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     ⚠ <b>Every component is validated the way <see cref="ResourceId.ParsePath" /> validates
    ///     it</b>, including every ancestor name — they are segments of the same path and an
    ///     unvalidated one is the same separator-injection hole. What is <i>not</i> here is the
    ///     trailing resource name, because a collection has none.
    /// </remarks>
    public static Result<ResourceCollectionId> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A resource collection path is required. It looks like "
                + "'/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{rg}"
                + "/providers/{namespace}/{type}' — see docs/plan/06 § Identifiers."
            );
        }

        if (path[0] != '/') {
            return Invalid($"'{path}' is not a resource collection path: it must start with '/'.");
        }

        var segments = path[1..].Split('/');
        foreach (var segment in segments) {
            if (segment.Length == 0) {
                return Invalid(
                    $"'{path}' is not a resource collection path: it contains an empty segment (a "
                    + "doubled or trailing '/')."
                );
            }
        }

        // 0:tenants 1:{guid} 2:subscriptions 3:{guid} 4:resourceGroups 5:{rg} 6:providers
        // 7:{namespace} then {type} then {name}/{type} repeated, one pair per level of nesting.
        //
        // ⚠ THE ODD TAIL IS THE WHOLE OF THE GRAMMAR. ResourceId's tail is even because it ends on a
        // name; this one is odd because it ends on a type. The two are disjoint by construction, so
        // a path is a resource or a collection or neither, never both, and no caller has to choose.
        const int fixedPrefix = 8;
        var tail = segments.Length - fixedPrefix;

        if (tail < 1) {
            return Invalid(
                "'"
                + path
                + "' is not a resource collection path: it has "
                + segments.Length.ToString(CultureInfo.InvariantCulture)
                + " segments and the shortest valid path has "
                + (fixedPrefix + 1).ToString(CultureInfo.InvariantCulture)
                + "."
            );
        }

        if (tail % 2 == 0) {
            return Invalid(
                "'"
                + path
                + "' is not a resource collection path: after the provider namespace it is the type, "
                + "then '{name}/{type}' once per level of nesting — for example "
                + "'/providers/CyberCloud.DBforPostgreSQL/servers/pg-main/databases'. This path has "
                + tail.ToString(CultureInfo.InvariantCulture)
                + " segments after the namespace, which is an even number, so it ends on a name and "
                + "addresses one resource rather than a collection."
            );
        }

        if (!IsLiteral(segments[0], TenantsSegment)
            || !IsLiteral(segments[2], SubscriptionsSegment)
            || !IsLiteral(segments[4], ResourceGroupsSegment)
            || !IsLiteral(segments[6], ProvidersSegment)) {
            return Invalid(
                $"'{path}' is not a resource collection path: the structural segments must be "
                + $"'/{TenantsSegment}/…/{SubscriptionsSegment}/…/{ResourceGroupsSegment}/…"
                + $"/{ProvidersSegment}/…' (matched case-insensitively)."
            );
        }

        if (!GuidFormat.TryParseD(segments[1], out var tenantId)) {
            return Invalid(
                $"'{segments[1]}' is not a tenant id: a resource id path spells GUIDs in the "
                + "hyphenated 'D' form, for example '2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3'. "
                + "Braced, parenthesised and bare-hex forms are rejected so that one collection has "
                + "exactly one path."
            );
        }

        if (!GuidFormat.TryParseD(segments[3], out var subscriptionId)) {
            return Invalid(
                $"'{segments[3]}' is not a subscription id: a resource id path spells GUIDs in the "
                + "hyphenated 'D' form."
            );
        }

        var resourceGroup = ResourceNaming.Validate(segments[5], "resource group name");
        if (resourceGroup.TryGetError(out var groupError)) {
            return Result<ResourceCollectionId>.Failure(groupError);
        }

        // Type segments at the even offsets from the namespace, ancestor names at the odd ones. The
        // tail is odd, so there is one more type segment than there are names.
        var typeSegments = new string[(tail + 1) / 2];
        var nameSegments = new string[(tail - 1) / 2];

        for (var i = 0; i < tail; i += 2) {
            typeSegments[i / 2] = segments[fixedPrefix + i];

            if (i + 1 < tail) {
                nameSegments[i / 2] = segments[fixedPrefix + i + 1];
            }
        }

        var type = ResourceTypeName.Create(segments[7], string.Join('/', typeSegments));
        if (type.TryGetError(out var typeError)) {
            return Result<ResourceCollectionId>.Failure(typeError);
        }

        foreach (var ancestor in nameSegments) {
            var ancestorName = ResourceNaming.Validate(ancestor, "parent resource name");
            if (ancestorName.TryGetError(out var ancestorError)) {
                return Result<ResourceCollectionId>.Failure(ancestorError);
            }
        }

        return Result<ResourceCollectionId>.Success(
            new(
                tenantId,
                subscriptionId,
                segments[5],
                type.GetValueOrThrow(),
                string.Join('/', nameSegments)
            )
        );
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    /// <summary>
    ///     Renders the path, interleaving the type segments with <see cref="ParentNames" /> and
    ///     ending on a type segment.
    /// </summary>
    string Render(string providerNamespace, string typePath) {
        var built = new StringBuilder(128)
            .Append('/').Append(TenantsSegment)
            .Append('/').Append(TenantId.ToString("D", CultureInfo.InvariantCulture))
            .Append('/').Append(SubscriptionsSegment)
            .Append('/').Append(SubscriptionId.ToString("D", CultureInfo.InvariantCulture))
            .Append('/').Append(ResourceGroupsSegment)
            .Append('/').Append(ResourceGroup)
            .Append('/').Append(ProvidersSegment)
            .Append('/').Append(providerNamespace);

        var typeSegments = typePath.Split('/');
        var names = ResourceId.SplitParents(ParentNames);

        // The invariant makes names one shorter than typeSegments, so every pass but the last emits
        // a type and its ancestor's name, and the last emits the type alone.
        for (var i = 0; i < typeSegments.Length; i++) {
            built.Append('/').Append(typeSegments[i]);

            if (i < names.Length) {
                built.Append('/').Append(names[i]);
            }
        }

        return built.ToString();
    }

    static bool IsLiteral(string segment, string literal) =>
        string.Equals(segment, literal, StringComparison.OrdinalIgnoreCase);

    static Result<ResourceCollectionId> Invalid(string message) =>
        Result<ResourceCollectionId>.Failure(ErrorCode.InvalidResourceId, message);

    /// <summary>Enforces <c>ParentNames.Count == Type.Depth - 1</c> and validates each ancestor.</summary>
    /// <remarks>
    ///     ⚠ Throws rather than returning a <see cref="Result" />, for the reason
    ///     <see cref="ResourceId" />'s twin gives: a mismatched pair in code is a bug whose quiet
    ///     version is a path that renders with a segment missing and re-parses as something else.
    /// </remarks>
    static string EnsureParents(string? parentNames, ResourceTypeName type) {
        var names = ResourceId.SplitParents(parentNames);
        var expected = type.IsEmpty ? 0 : type.Depth - 1;

        if (names.Length != expected) {
            throw new ArgumentException(
                "'"
                + type
                + "' nests "
                + (expected + 1).ToString(CultureInfo.InvariantCulture)
                + " levels deep, so a collection of it needs "
                + expected.ToString(CultureInfo.InvariantCulture)
                + " parent name(s) and was given "
                + names.Length.ToString(CultureInfo.InvariantCulture)
                + ". A 'servers/databases' collection is addressed "
                + "'…/servers/{serverName}/databases', so it carries the server's name — see the "
                + "remarks on ResourceId.ParentNames.",
                nameof(parentNames)
            );
        }

        foreach (var name in names) {
            ResourceNaming.EnsureValid(name, nameof(parentNames), "parent resource name");
        }

        return parentNames ?? "";
    }

    static ResourceTypeName EnsureType(ResourceTypeName type) =>
        type.IsEmpty
            ? throw new ArgumentException(
                "A resource collection address needs a type. It is the provider namespace and the "
                + "type path — see ResourceTypeName.",
                nameof(type)
            )
            : type;
}
