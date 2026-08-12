using System.Globalization;
using System.Text;

namespace CyberCloud.Core.Resources;

/// <summary>
///     The address of an addressable thing, in the Azure shape docs/plan/06 § Identifiers specifies:
///     <code>
///     /tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{rgName}
///       /providers/{providerNamespace}/{resourceType}/{resourceName}
///     </code>
///     A nested type <b>interleaves</b>, exactly as Azure does — see
///     <see cref="ParentNames" />:
///     <code>
///     …/providers/CyberCloud.DBforPostgreSQL/servers/{serverName}/databases/{databaseName}
///     </code>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06 § Identifiers — the GUID and the path answer different questions. The GUID is the
///         identity, stable across renames, used in tuples, metering records and grain keys. The
///         path is the address: human-readable, hierarchical, what appears in a URL, what a role
///         assignment scopes to, and what a support engineer pastes into a ticket.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="Path" /> does not contain <see cref="Id" />, so
///             <see cref="TryParsePath" /> cannot recover it and returns
///             <see cref="Guid.Empty" />.
///         </b>
///         This is not an omission here — it is what docs/plan/06 § Identifiers
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
///         necessary: the path grammar interleaves types and names, so a name containing <c>/</c>
///         would silently shift the whole alternation. See <c>ResourceIdTests</c> § separator
///         injection.
///     </para>
/// </remarks>
/// <param name="TenantId">The owning tenant. docs/plan/06 § The hierarchy.</param>
/// <param name="SubscriptionId">The billing and quota boundary. docs/plan/06 § The hierarchy.</param>
/// <param name="ResourceGroup">The lifecycle boundary's name. docs/plan/06 § The hierarchy.</param>
/// <param name="Type">The provider namespace and resource type.</param>
/// <param name="Name">The resource's name within its group.</param>
/// <param name="Id">
///     The resource's GUID, or <see cref="Guid.Empty" /> when this id came from a path.
/// </param>
/// <param name="ParentNames">
///     The ancestors' names, outermost first, <c>/</c>-separated — empty for a top-level type. See
///     the property.
/// </param>
public readonly record struct ResourceId(
    Guid TenantId,
    Guid SubscriptionId,
    string ResourceGroup,
    ResourceTypeName Type,
    string Name,
    Guid Id,
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

    /// <summary>The resource's name. Validated on construction and on <c>with</c>.</summary>
    public string Name {
        get;
        init => field = ResourceNaming.EnsureValid(value, nameof(Name), "resource name");
    } = ResourceNaming.EnsureValid(Name, nameof(Name), "resource name");

    /// <summary>The provider namespace and resource type. Must not be default.</summary>
    /// <remarks>
    ///     ⚠ The <c>init</c> re-checks the <see cref="ParentNames" /> invariant, and the property
    ///     initializer below cannot. <c>id with { Type = deeper }</c> runs only <i>this</i> accessor —
    ///     <see cref="ParentNames" /> keeps the value it was copied with — so without the check here
    ///     a depth-1 id could be re-typed to <c>servers/databases</c> and carry no server name, and
    ///     the first thing anyone would notice is a path with a segment missing. During construction
    ///     the initializer runs instead and <see cref="ParentNames" /> is not assigned yet, which is
    ///     what the null test distinguishes; <see cref="ParentNames" />' own initializer does the
    ///     check a moment later.
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

    /// <summary>
    ///     The ancestors' names, outermost first, joined by <c>/</c>. Empty for a top-level type;
    ///     <c>"pg-main"</c> for a <c>servers/databases</c>; <c>"a/b"</c> at depth 3.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is what makes a child address like Azure's.</b> A nested type is spelled
    ///         <c>…/servers/{serverName}/databases/{databaseName}</c> — the type segments and the
    ///         name segments alternate — rather than <c>…/servers/databases/{name}</c> with the type
    ///         path whole in the middle. docs/plan/12 § Child resources records why, and the short
    ///         version is that the alternative cannot express the parent at all: a
    ///         <see cref="ResourceId" /> is the <i>only</i> input
    ///         <c>IResourceRelationWriter.LinkToParentAsync</c> gets, so an address that does not
    ///         name its parent is an address whose ReBAC <c>parent</c> edge cannot point anywhere but
    ///         the resource group, and a child nobody can inherit permission to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A joined string rather than a collection, for the same reason
    ///         <see cref="ResourceTypeName" /> stores <c>servers/databases</c> as one.</b> A record
    ///         struct gets its equality from its members, and
    ///         <c>ImmutableArray&lt;string&gt;.Equals</c> is <i>reference</i> equality on the
    ///         underlying array — two ids with the same ancestors built separately would compare
    ///         unequal, and this type is a dictionary key in the index, the tuple store and the
    ///         registry. A string compares by value and serialises without a surrogate of its own.
    ///         The join is unambiguous because <see cref="ResourceNaming" /> forbids <c>/</c> in a
    ///         name.
    ///     </para>
    ///     <para>
    ///         <b>The invariant is <c>count == Type.Depth - 1</c>,</b> enforced on construction and
    ///         on <c>with</c>. It is what makes the grammar unambiguous without needing to count:
    ///         the segments after the namespace alternate type, name, type, name, so their number is
    ///         always even and the depth is half of it. Contrast the depth cap on
    ///         <see cref="ResourceTypeName" />, which exists because the <i>type path</i> read on its
    ///         own has no such structure.
    ///     </para>
    /// </remarks>
    public string ParentNames {
        get;
        init => field = EnsureParents(value, Type);
    } = EnsureParents(ParentNames, Type);

    /// <summary>
    ///     The address, exactly as docs/plan/06 § Identifiers defines it. Both GUIDs use the <c>D</c>
    ///     format (hyphenated, no braces).
    /// </summary>
    public string Path => Render(Type.Namespace, Type.Type);

    /// <summary>
    ///     The path with the provider namespace and type lower-cased, for hashing and indexing.
    /// </summary>
    /// <remarks>
    ///     docs/plan/06 § Grain keys keys <c>IResourceIndexGrain</c> on <c>idx/path/{sha256(path)[..16]}</c>.
    ///     It does not say <i>which</i> path, and it matters: the resource group and the resource
    ///     name are already forced lower-case by <see cref="ResourceNaming" />, but the provider
    ///     namespace and type are case-preserving, so <c>Path</c> alone is not a canonical form and
    ///     hashing it would let <c>CyberCloud.Cache/redis</c> and <c>cybercloud.cache/redis</c>
    ///     claim two index entries for one name. Hash this.
    /// </remarks>
    public string CanonicalPath =>
        Render(ResourceTypeName.AsciiLower(Type.Namespace), ResourceTypeName.AsciiLower(Type.Type));

    /// <summary>
    ///     The address of this resource's parent resource, or <see langword="null" /> when this is a
    ///     top-level type and its parent is the resource group.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the property the interleaved grammar exists to provide, and it is why
    ///         docs/plan/12 § Child resources chose that grammar.</b>
    ///         <c>IResourceRelationWriter.LinkToParentAsync</c> takes a <see cref="ResourceId" /> and
    ///         nothing else — no body, no registration, no grain call. Under a grammar that spelled a
    ///         child <c>…/servers/databases/{name}</c> the parent's <i>name</i> would simply not be
    ///         in the address, so the ReBAC <c>parent</c> edge could not point at it and a child
    ///         would inherit no permission from the thing it lives inside. Here it is a pure
    ///         function: drop the last type segment and the last name.
    ///     </para>
    ///     <para>
    ///         ⚠ The returned id carries <see cref="Guid.Empty" /> for its <see cref="Id" />, for the
    ///         same reason a parsed path does — the address is known, the identity is a lookup
    ///         through <c>IResourceIndexGrain</c>. Call <see cref="WithId" /> once it answers.
    ///     </para>
    /// </remarks>
    public ResourceId? Parent {
        get {
            if (ParentNames.Length == 0) {
                return null;
            }

            var lastType = Type.Type.LastIndexOf('/');
            var lastName = ParentNames.LastIndexOf('/');

            return new ResourceId(
                TenantId,
                SubscriptionId,
                ResourceGroup,
                new(Type.Namespace, Type.Type[..lastType]),
                lastName < 0 ? ParentNames : ParentNames[(lastName + 1)..],
                Guid.Empty,
                lastName < 0 ? "" : ParentNames[..lastName]
            );
        }
    }

    /// <summary>Returns this id with its GUID set — the step after the index resolves a path.</summary>
    public ResourceId WithId(Guid id) => this with { Id = id };

    /// <summary>
    ///     Returns this id re-typed, setting <see cref="Type" /> and <see cref="ParentNames" />
    ///     together.
    /// </summary>
    /// <param name="type">The new type.</param>
    /// <param name="parentNames">Its ancestors' names, <c>/</c>-separated. Empty for a top-level type.</param>
    /// <remarks>
    ///     ⚠ <b>Use this rather than <c>with { Type = …, ParentNames = … }</c>.</b> A <c>with</c>
    ///     expression runs one <c>init</c> accessor per member, in source order, so it is never
    ///     holding both new values at once: re-typing a top-level id to <c>servers/databases</c>
    ///     throws on the <see cref="Type" /> member because the parent name has not arrived yet, and
    ///     writing the two the other way round throws on <see cref="ParentNames" /> because the type
    ///     is still the shallow one. That is the invariant working, not a bug in it — an invariant
    ///     over two members cannot be checked by a setter that can only see one. This method sets
    ///     both in one construction, which is the only place the pair is ever consistent.
    /// </remarks>
    public ResourceId WithType(ResourceTypeName type, string parentNames = "") =>
        new(TenantId, SubscriptionId, ResourceGroup, type, Name, Id, parentNames);

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
    ///         because <c>PROD</c> is not a legal name (docs/plan/06 § Identifiers) and folding it to
    ///         <c>prod</c> would be exactly the mangling docs/plan/06 § Identifiers forbids. Use
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
    public static bool TryParsePath(string? path, out ResourceId id) {
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
    public static Result<ResourceId> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A resource id path is required. It looks like "
                + "'/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{rg}"
                + "/providers/{namespace}/{type}/{name}' — see docs/plan/06 § Identifiers."
            );
        }

        if (path[0] != '/') {
            return Invalid($"'{path}' is not a resource id path: it must start with '/'.");
        }

        // Split on '/' after dropping the leading one. An empty element means a doubled slash, a
        // trailing slash, or a genuinely empty segment — all malformed, none of them silently
        // absorbed.
        var segments = path[1..].Split('/');
        foreach (var segment in segments) {
            if (segment.Length == 0) {
                return Invalid(
                    $"'{path}' is not a resource id path: it contains an empty segment (a doubled "
                    + "or trailing '/')."
                );
            }
        }

        // 0:tenants 1:{guid} 2:subscriptions 3:{guid} 4:resourceGroups 5:{rg} 6:providers
        // 7:{namespace} then {type}/{name} repeated, one pair per level of nesting.
        //
        // ⚠ THE PAIRING IS WHAT MAKES THIS UNAMBIGUOUS, and it is the reason the grammar interleaves
        // rather than running the type path whole and putting one name at the end. Under the old
        // shape `/providers/{ns}/a/b/c` could be read as type `a/b` name `c` or type `a` name `b/c`,
        // and only ResourceNaming's ban on '/' in a name closed the second reading — docs/plan/06
        // § Identifiers calls that rule "load-bearing for identifier integrity". Here there is
        // nothing to close: type segments sit at even offsets from the namespace and names at odd
        // ones, so the tail length is always even and the depth is exactly half of it. An odd tail is
        // not a path read at the wrong depth, it is not a path.
        const int fixedPrefix = 8;
        var tail = segments.Length - fixedPrefix;

        if (tail < 2) {
            return Invalid(
                "'"
                + path
                + "' is not a resource id path: it has "
                + segments.Length.ToString(CultureInfo.InvariantCulture)
                + " segments and the shortest valid path has "
                + (fixedPrefix + 2).ToString(CultureInfo.InvariantCulture)
                + "."
            );
        }

        if (tail % 2 != 0) {
            return Invalid(
                "'"
                + path
                + "' is not a resource id path: after the provider namespace it must alternate "
                + "'{type}/{name}', one pair per level of nesting — for example "
                + "'/providers/CyberCloud.DBforPostgreSQL/servers/pg-main/databases/orders'. This "
                + "path has "
                + tail.ToString(CultureInfo.InvariantCulture)
                + " segments after the namespace, which is an odd number, so one type has no name or "
                + "one name has no type."
            );
        }

        if (!IsLiteral(segments[0], TenantsSegment)
            || !IsLiteral(segments[2], SubscriptionsSegment)
            || !IsLiteral(segments[4], ResourceGroupsSegment)
            || !IsLiteral(segments[6], ProvidersSegment)) {
            return Invalid(
                $"'{path}' is not a resource id path: the structural segments must be "
                + $"'/{TenantsSegment}/…/{SubscriptionsSegment}/…/{ResourceGroupsSegment}/…"
                + $"/{ProvidersSegment}/…' (matched case-insensitively)."
            );
        }

        if (!GuidFormat.TryParseD(segments[1], out var tenantId)) {
            return Invalid(
                $"'{segments[1]}' is not a tenant id: a resource id path spells GUIDs in the "
                + "hyphenated 'D' form, for example '2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3'. "
                + "Braced, parenthesised and bare-hex forms are rejected so that one resource has "
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
            return Result<ResourceId>.Failure(groupError);
        }

        // The type segments are the even offsets from the namespace, the names the odd ones.
        var typeSegments = new string[tail / 2];
        var nameSegments = new string[tail / 2 - 1];

        for (var i = 0; i < tail; i += 2) {
            typeSegments[i / 2] = segments[fixedPrefix + i];

            // Every name but the last, which is the resource's own.
            if (i + 2 < tail) {
                nameSegments[i / 2] = segments[fixedPrefix + i + 1];
            }
        }

        var type = ResourceTypeName.Create(segments[7], string.Join('/', typeSegments));
        if (type.TryGetError(out var typeError)) {
            return Result<ResourceId>.Failure(typeError);
        }

        // ⚠ Every ancestor's name is validated too, not just the resource's own. They are segments of
        // the same path and an unvalidated one is the same separator-injection hole the resource's
        // own name is checked for — see the remarks on the type.
        foreach (var ancestor in nameSegments) {
            var ancestorName = ResourceNaming.Validate(ancestor, "parent resource name");
            if (ancestorName.TryGetError(out var ancestorError)) {
                return Result<ResourceId>.Failure(ancestorError);
            }
        }

        var name = ResourceNaming.Validate(segments[^1]);
        if (name.TryGetError(out var nameError)) {
            return Result<ResourceId>.Failure(nameError);
        }

        return Result<ResourceId>.Success(
            new(
                tenantId,
                subscriptionId,
                segments[5],
                type.GetValueOrThrow(),
                segments[^1],
                Guid.Empty,
                string.Join('/', nameSegments)
            )
        );
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    /// <summary>
    ///     Renders the path, interleaving the type segments with <see cref="ParentNames" /> and
    ///     ending on <see cref="Name" />.
    /// </summary>
    /// <remarks>
    ///     One renderer for both <see cref="Path" /> and <see cref="CanonicalPath" />, differing only
    ///     in the casing of the two arguments. Two copies of the interleave would be two chances for
    ///     the address and the thing the index hashes to disagree about which server a database is
    ///     in, and that disagreement is silent.
    /// </remarks>
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
        var names = SplitParents(ParentNames);

        // The invariant makes names one shorter than typeSegments, so the last pass — and only the
        // last — falls through to this resource's own name.
        for (var i = 0; i < typeSegments.Length; i++) {
            built.Append('/').Append(typeSegments[i])
                .Append('/').Append(i < names.Length ? names[i] : Name);
        }

        return built.ToString();
    }

    static bool IsLiteral(string segment, string literal) =>
        string.Equals(segment, literal, StringComparison.OrdinalIgnoreCase);

    static Result<ResourceId> Invalid(string message) =>
        Result<ResourceId>.Failure(ErrorCode.InvalidResourceId, message);

    /// <summary>The <c>/</c>-separated ancestors, or an empty array for a top-level type.</summary>
    internal static string[] SplitParents(string? parentNames) =>
        string.IsNullOrEmpty(parentNames) ? [] : parentNames.Split('/');

    /// <summary>
    ///     Enforces <c>ParentNames.Count == Type.Depth - 1</c> and validates each ancestor.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Throws rather than returning a <see cref="Result" />, like the rest of this type's
    ///     construction-time validation.</b> A mismatched pair in code is a bug — the caller built an
    ///     address for a <c>servers/databases</c> without saying which server — and the failure has
    ///     to be loud, because the quiet version is a path that renders with a missing segment and
    ///     re-parses as something else. <see cref="ParsePath" /> is the <see cref="Result" />-shaped
    ///     door for input that came from a request, and it cannot produce a mismatch at all: it
    ///     derives both counts from the same alternation.
    /// </remarks>
    static string EnsureParents(string? parentNames, ResourceTypeName type) {
        var names = SplitParents(parentNames);
        var expected = type.IsEmpty ? 0 : type.Depth - 1;

        if (names.Length != expected) {
            throw new ArgumentException(
                "'"
                + type
                + "' nests "
                + (expected + 1).ToString(CultureInfo.InvariantCulture)
                + " levels deep, so it needs "
                + expected.ToString(CultureInfo.InvariantCulture)
                + " parent name(s) and was given "
                + names.Length.ToString(CultureInfo.InvariantCulture)
                + ". A 'servers/databases' is addressed "
                + "'…/servers/{serverName}/databases/{databaseName}', so it carries the server's "
                + "name — see the remarks on ResourceId.ParentNames.",
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
                "A resource id needs a resource type, for example "
                + "'CyberCloud.DBforPostgreSQL/servers'. default(ResourceTypeName) is not one.",
                nameof(type)
            )
            : type;
}
