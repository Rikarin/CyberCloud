using CyberCloud.Core.Resources;

namespace CyberCloud.Core.Contracts.Serialization;

/// <summary>
///     The wire form of <see cref="ResourceId" />.
/// </summary>
/// <remarks>
///     <para>
///         The type is carried as a nested <see cref="ResourceTypeName" /> rather than as two loose
///         strings, so it goes through <see cref="ResourceTypeNameSurrogateConverter" /> and there
///         is exactly one place that decides what a valid type looks like on the wire.
///     </para>
///     <para>
///         ⚠ <b><c>default(ResourceId)</c> round-trips to <c>default(ResourceId)</c>.</b>
///         <c>ResourceId</c>'s constructor validates its <see cref="ResourceId.ResourceGroup" />
///         and <see cref="ResourceId.Name" /> and throws on a null one, so the converter has to
///         recognise the empty payload and short-circuit. This is not hypothetical: a failed
///         <c>Result&lt;ResourceId&gt;</c> — the shape <c>ResourceId.ParsePath</c> returns on every
///         malformed path — holds <c>default(ResourceId)</c> and writes it.
///     </para>
///     <para>
///         <see cref="ResourceId.Id" /> is <see cref="Guid.Empty" /> for an id that came from a path
///         and has not been resolved through the index yet (docs/plan/06 § Identifiers). That is a legitimate
///         value and is preserved as-is; it is not the same thing as the empty payload above, which
///         is why the null check is on <see cref="ResourceGroup" /> and <see cref="Name" />.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Core.ResourceId")]
public struct ResourceIdSurrogate {
    /// <summary>The owning tenant.</summary>
    [Id(0)]
    public Guid TenantId { get; set; }

    /// <summary>The billing and quota boundary.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; set; }

    /// <summary>The lifecycle boundary's name.</summary>
    [Id(2)]
    public string? ResourceGroup { get; set; }

    /// <summary>The provider namespace and resource type.</summary>
    [Id(3)]
    public ResourceTypeName Type { get; set; }

    /// <summary>The resource's name within its group.</summary>
    [Id(4)]
    public string? Name { get; set; }

    /// <summary>The resource's GUID, or <see cref="Guid.Empty" /> for an unresolved address.</summary>
    [Id(5)]
    public Guid Id { get; set; }

    /// <summary>
    ///     The ancestors' names, <c>/</c>-separated — empty for a top-level type.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A new member at a new <c>[Id]</c>, which is what makes an old payload still read.</b>
    ///     Orleans leaves an absent member at its default, and <see cref="string" />'s default is
    ///     <see langword="null" /> — which <see cref="ResourceId" /> normalises to <c>""</c>, the
    ///     correct value for every id written before this member existed, because every type in the
    ///     registry then was top-level. A depth-2 id sent to a silo that predates this member would
    ///     lose its parent rather than fail, so the two halves of a rolling upgrade must not straddle
    ///     the first nested type — docs/plan/12 § Child resources.
    /// </remarks>
    [Id(6)]
    public string? ParentNames { get; set; }
}

/// <summary>The <see cref="ResourceId" /> ↔ <see cref="ResourceIdSurrogate" /> converter.</summary>
[RegisterConverter]
public sealed class ResourceIdSurrogateConverter : IConverter<ResourceId, ResourceIdSurrogate> {
    /// <inheritdoc />
    public ResourceId ConvertFromSurrogate(in ResourceIdSurrogate surrogate) =>
        surrogate.ResourceGroup is null || surrogate.Name is null
            ? default
            : new ResourceId(
                surrogate.TenantId,
                surrogate.SubscriptionId,
                surrogate.ResourceGroup,
                surrogate.Type,
                surrogate.Name,
                surrogate.Id,
                surrogate.ParentNames ?? ""
            );

    /// <inheritdoc />
    public ResourceIdSurrogate ConvertToSurrogate(in ResourceId value) =>
        value.Name is null
            ? default
            : new ResourceIdSurrogate {
                TenantId = value.TenantId,
                SubscriptionId = value.SubscriptionId,
                ResourceGroup = value.ResourceGroup,
                Type = value.Type,
                Name = value.Name,
                Id = value.Id,
                ParentNames = value.ParentNames
            };
}
