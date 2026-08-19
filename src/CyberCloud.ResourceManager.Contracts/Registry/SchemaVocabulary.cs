namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>
///     The wire spelling of every registry vocabulary that reaches a generated surface.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One place, because a wire string that appears twice is a wire string that will
///         eventually differ.</b> A <see cref="SchemaFormat" /> is written into the OpenAPI document
///         by the emitter, matched by the portal renderer's widget table, turned into a CLI flag's
///         help text and turned into an SDK member's type. Four <c>switch</c> statements over the
///         same enum is four chances to spell <c>date-time</c> as <c>datetime</c>, and the compiler
///         would catch none of them.
///     </para>
///     <para>
///         ⚠ <b>Changing a string here changes a published document</b>, and the compatibility gate
///         will say so for <see cref="Of(SchemaFormat)" /> (a <c>format</c> is contract) and will not
///         for <see cref="Of(WidgetHint)" /> (an <c>x-</c> extension is prose). That asymmetry is
///         intentional and is <c>OpenApiCompatibility</c>'s rule, not a special case here.
///     </para>
/// </remarks>
public static class SchemaVocabulary {
    /// <summary>
    ///     The <c>format</c> keyword's value, or <c>""</c> for <see cref="SchemaFormat.None" />.
    /// </summary>
    /// <param name="format">The declared format.</param>
    /// <remarks>
    ///     ⚠ The two platform formats carry a <c>cybercloud-</c> prefix. JSON Schema's <c>format</c>
    ///     vocabulary is open, so an unprefixed <c>region</c> would be a name a future registered
    ///     format could collide with — and a generic tool that guessed at it would guess wrong.
    /// </remarks>
    public static string Of(SchemaFormat format) =>
        format switch {
            SchemaFormat.Uuid => "uuid",
            SchemaFormat.DateTime => "date-time",
            SchemaFormat.Uri => "uri",
            SchemaFormat.Email => "email",
            SchemaFormat.Region => "cybercloud-region",
            SchemaFormat.ResourceId => "cybercloud-resource-id",
            _ => string.Empty
        };

    /// <summary>
    ///     The <c>x-cybercloud-widget</c> value, or <c>""</c> for <see cref="WidgetHint.None" />.
    /// </summary>
    /// <param name="widget">The declared hint.</param>
    /// <remarks>
    ///     The strings are docs/plan/20 § The shape that makes 100 resource types affordable's own —
    ///     that document's widgets directory is named <c>region, cluster, storageclass, subnet, sku,
    ///     secret-ref, cron, cidr, duration</c>, and the renderer keys on exactly these.
    /// </remarks>
    public static string Of(WidgetHint widget) =>
        widget switch {
            WidgetHint.Region => "region",
            WidgetHint.Cluster => "cluster",
            WidgetHint.StorageClass => "storageclass",
            WidgetHint.Subnet => "subnet",
            WidgetHint.Sku => "sku",
            WidgetHint.SecretRef => "secret-ref",
            WidgetHint.Cron => "cron",
            WidgetHint.Cidr => "cidr",
            WidgetHint.Duration => "duration",
            WidgetHint.CozyPreset => "cozy-preset",
            WidgetHint.TagInput => "tag-input",
            _ => string.Empty
        };

    /// <summary>The JSON type name of a kind — what an emitted <c>type</c> keyword carries.</summary>
    /// <param name="kind">The kind. ⚠ <see cref="SchemaKind.Unknown" /> has none and throws.</param>
    /// <exception cref="InvalidOperationException"><paramref name="kind" /> is the never-assigned member.</exception>
    public static string JsonTypeOf(SchemaKind kind) =>
        kind switch {
            SchemaKind.Text => "string",
            SchemaKind.Number => "number",
            SchemaKind.WholeNumber => "integer",
            SchemaKind.Boolean => "boolean",
            SchemaKind.Nested => "object",
            SchemaKind.Array => "array",
            _ => throw new InvalidOperationException(
                $"SchemaKind.{kind} has no JSON type. Every member but Unknown maps to one — see the "
                + "remarks on SchemaKind."
            )
        };
}

/// <summary>
///     The one root-level property that belongs to no provider: the tag bag of
///     docs/plan/06 § Tags, locks, and the small stuff that is not small.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type exists because <c>SupportsTags</c> was a flag with no schema consequence,
///         and that turned into the platform's first documented contract lie.</b>
///         <c>IResourceTypeBuilder.SupportsTags</c> made the write path accept a root-level
///         <c>tags</c> object; <see cref="ResourceSchema.Validate" /> grew an <c>allowTags</c>
///         parameter so it would stop refusing one; and the OpenAPI emitter declared neither, so the
///         published document said <c>additionalProperties: false</c> over a body that in fact
///         accepted <c>tags</c>. A generated SDK would have had no member for it, a generated CLI no
///         flag, and a generated form no control — for a feature every tagged resource type uses.
///     </para>
///     <para>
///         The repair is that the constants below are read by <i>all three</i>: the validator, the
///         grain that enforces the cap, and the emitters. A flag's consequence is now a shape, and the
///         shape has one definition.
///     </para>
/// </remarks>
public static class TagRules {
    /// <summary>The pointer the bag sits at. Root-level, and not under <c>/properties</c>.</summary>
    public const string JsonPointer = "/tags";

    /// <summary>The member name, for a body walk that has a name rather than a pointer.</summary>
    public const string Name = "tags";

    /// <summary>
    ///     The cap of docs/plan/06 § Tags, locks — 50 pairs.
    /// </summary>
    /// <remarks>
    ///     ⚠ Enforced by the resource grain rather than by <see cref="ResourceSchema.Validate" />,
    ///     and the emitted <c>maxProperties</c> is therefore a true statement about the API and not
    ///     about the validator. It applies to the <i>merged</i> tag set: a <c>PATCH</c> adding one tag
    ///     to forty-nine is the request that crosses it, and the schema sees only the patch.
    /// </remarks>
    public const int MaxTags = 50;
}

/// <summary>
///     Where a resource says which cluster it is placed into — the other flag that had no shape.
/// </summary>
/// <remarks>
///     ⚠ <b>Unlike <see cref="TagRules" />, this pointer belongs to the <i>provider</i> and only its
///     default lives here.</b> The tag bag is the platform's and is identical for every type; a
///     cluster id is a property of the type's own body, which is why it is declared in the schema and
///     merely <i>named</i> by <c>RequiresCluster</c>. The default is the pointer
///     <c>ResourceManagerService</c> hard-coded before this existed, so every type that already worked
///     keeps working without saying anything new.
/// </remarks>
public static class ClusterPlacement {
    /// <summary>The pointer a type gets when it declares <c>RequiresCluster()</c> with no argument.</summary>
    public const string DefaultPointer = "/properties/clusterId";
}

/// <summary>
///     The vocabulary of soft delete — docs/plan/08 § Soft delete.
/// </summary>
/// <remarks>
///     ⚠ <b>The purge permission has a default and the recovery window does not, which is the right way
///     round.</b> A window is a promise about a specific type's data and only its provider can say how
///     long — <c>SupportsSoftDelete(7)</c> is a claim, not a formality. Who may end that window early is
///     the same question for every type that has one, so a default here means the nine providers that
///     eventually declare a window do not each invent a permission name, and the one that genuinely
///     needs a different one still says so.
/// </remarks>
public static class SoftDeletePolicy {
    /// <summary>
    ///     The permission a purge needs when a type declares a window and names none.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <c>delete</c>.</b> docs/plan/08 § Soft delete: Azure keeps
    ///     <c>deletedVaults/purge/action</c> out of Key Vault Contributor, so a role can hold "may
    ///     delete" without "may destroy permanently". Defaulting this to the delete permission would
    ///     collapse the two rights and make the recovery window worthless against exactly the caller it
    ///     protects against — the one who could already delete.
    /// </remarks>
    public const string DefaultPurgePermission = "purge";

    /// <summary>The action a caller posts to bring a parked resource back.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An action on the resource's own address, which is the one address a parked
    ///         resource still has.</b> docs/plan/08 § Soft delete records that the alternative —
    ///         Key Vault's <c>deletedVaults</c> collection at subscription+location scope — cannot be
    ///         built here, because <c>ResourceId.ParsePath</c> has <c>const int fixedPrefix = 8</c> and
    ///         there is no subscription-scoped address for the collection to live at. A <c>POST</c> to
    ///         <c>{resource}/restore</c> needs no new address shape: the path parses today, and
    ///         <c>ResourceManagerService.RestoreAsync</c> already asks the index's soft-deleted side for
    ///         the GUID that <c>ResolveAsync</c> refuses to give it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Declared by the platform on every type that has a window, never by a provider.</b>
    ///         <c>ProviderBuilder</c> synthesises this and <see cref="PurgeAction" /> in <c>Build</c>
    ///         and refuses a provider that declares either name itself. Two consequences follow, and
    ///         both are the reason it is done there. The gateway's stage 6 answers <c>404</c> to a
    ///         <c>POST</c> whose action the registry does not declare, so the route exists exactly for
    ///         the types that have a window and for no others; and ADR-012's four generated surfaces
    ///         read actions off the registry, so the OpenAPI path, the <c>cyc</c> verb, the SDK method
    ///         and the portal's action button all appear without an emitter knowing what soft delete is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not dispatched as an ordinary action.</b>
    ///         <c>ResourceManagerService.ActionAsync</c> hands this name and <see cref="PurgeAction" />
    ///         to <c>RestoreAsync</c> and <c>PurgeAsync</c> before its own step 1, because an ordinary
    ///         action resolves the address first and a parked resource has no addressable binding to
    ///         resolve — the canonical <c>404</c> is what step 1 is for. The registration is therefore a
    ///         declaration of the surface; the behaviour is the two methods that already existed.
    ///     </para>
    /// </remarks>
    public const string RestoreAction = "restore";

    /// <summary>The action a caller posts to end a recovery window early.</summary>
    /// <remarks>
    ///     ⚠ Its permission is the type's <c>PurgePermission</c> rather than
    ///     <see cref="DefaultPurgePermission" /> directly, so a type that named its own is published
    ///     with the one it named.
    /// </remarks>
    public const string PurgeAction = "purge";

    /// <summary>Whether <paramref name="name" /> is one of the two the platform owns.</summary>
    /// <param name="name">An action name from a declaration or a URL segment.</param>
    /// <returns><c>true</c> for <see cref="RestoreAction" /> and <see cref="PurgeAction" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Case-insensitive, because an action is matched as a URL segment is.</b>
    ///     <c>ResourceTypeRegistration.TryGetAction</c> compares that way, so a check here that was
    ///     case-sensitive would let <c>POST …/Restore</c> resolve to the synthesised registration and
    ///     then miss this test — an ordinary action dispatch against a resource that is not there.
    /// </remarks>
    public static bool IsReserved(string? name) =>
        string.Equals(name, RestoreAction, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, PurgeAction, StringComparison.OrdinalIgnoreCase);
}
