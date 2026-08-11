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
