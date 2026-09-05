using CyberCloud.Core.Resources;

namespace CyberCloud.Core.Contracts.Serialization;

/// <summary>
///     The wire form of <see cref="ResourceCollectionId" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a collection address travels at all.</b> It did not, until
///         <c>IParkedResourceRegistryGrain.ListOfTypeAsync</c> — docs/plan/08 § Soft delete's
///         per-resource-group registry of parked resources, whose question is <i>"what is recoverable
///         in this group, of this type"</i>. That question is a <see cref="ResourceCollectionId" />
///         and nothing else: passing the four components loose would let a caller assemble a pair the
///         type's own constructor refuses (a nested type with no ancestor name), and passing a
///         <c>string</c> path would put <c>ResourceCollectionId.ParsePath</c> on the far side of a
///         grain call, where its failure has nowhere to go.
///     </para>
///     <para>
///         The type is carried as a nested <see cref="ResourceTypeName" /> for the reason
///         <see cref="ResourceIdSurrogate" /> gives: one place decides what a valid type looks like on
///         the wire.
///     </para>
///     <para>
///         ⚠ <b><c>default(ResourceCollectionId)</c> round-trips to <c>default</c>.</b> The
///         constructor validates the resource group name and refuses an empty
///         <see cref="ResourceTypeName" />, so the converter has to recognise the empty payload and
///         short-circuit — exactly as <see cref="ResourceIdSurrogate" />'s does, and for the same
///         concrete reason: a failed <c>Result&lt;ResourceCollectionId&gt;</c>, which is what
///         <c>ResourceCollectionId.ParsePath</c> returns for every malformed path, holds
///         <c>default</c> and writes it.
///     </para>
///     <para>
///         ⚠ <b>There is no <c>Id</c> member and there never will be.</b>
///         <see cref="ResourceCollectionId" />'s own remarks: a collection is not an entity — no GUID,
///         no index entry, no ReBAC object, no grain. A wire member holding one would be the first
///         place the platform pretended otherwise.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Core.ResourceCollectionId")]
public struct ResourceCollectionIdSurrogate {
    /// <summary>The owning tenant.</summary>
    [Id(0)]
    public Guid TenantId { get; set; }

    /// <summary>The billing and quota boundary.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; set; }

    /// <summary>The lifecycle boundary's name — the scope the listing runs at.</summary>
    [Id(2)]
    public string? ResourceGroup { get; set; }

    /// <summary>The provider namespace and resource type being listed.</summary>
    [Id(3)]
    public ResourceTypeName Type { get; set; }

    /// <summary>
    ///     The ancestors' names, <c>/</c>-separated — empty for a top-level type, and one shorter
    ///     than the type's depth for a nested one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not optional in the way <see cref="ResourceIdSurrogate.ParentNames" /> was when it was
    ///     added: this surrogate is new, so there is no older payload that omits this member, and the
    ///     invariant tying it to <see cref="Type" />'s depth is checked by the constructor on the way
    ///     back in. A payload that lost it would throw rather than decode one level shallower.
    /// </remarks>
    [Id(4)]
    public string? ParentNames { get; set; }
}

/// <summary>
///     The <see cref="ResourceCollectionId" /> ↔ <see cref="ResourceCollectionIdSurrogate" />
///     converter.
/// </summary>
[RegisterConverter]
public sealed class ResourceCollectionIdSurrogateConverter
    : IConverter<ResourceCollectionId, ResourceCollectionIdSurrogate> {
    /// <inheritdoc />
    public ResourceCollectionId ConvertFromSurrogate(in ResourceCollectionIdSurrogate surrogate) =>
        surrogate.ResourceGroup is null || surrogate.Type.IsEmpty
            ? default
            : new ResourceCollectionId(
                surrogate.TenantId,
                surrogate.SubscriptionId,
                surrogate.ResourceGroup,
                surrogate.Type,
                surrogate.ParentNames ?? ""
            );

    /// <inheritdoc />
    public ResourceCollectionIdSurrogate ConvertToSurrogate(in ResourceCollectionId value) =>
        value.ResourceGroup is null || value.Type.IsEmpty
            ? default
            : new ResourceCollectionIdSurrogate {
                TenantId = value.TenantId,
                SubscriptionId = value.SubscriptionId,
                ResourceGroup = value.ResourceGroup,
                Type = value.Type,
                ParentNames = value.ParentNames
            };
}
