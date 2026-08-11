using CyberCloud.Core.Resources;

namespace CyberCloud.Core.Contracts.Serialization;

/// <summary>
///     The wire form of <see cref="ResourceKey" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>SCHEDULED FOR REMOVAL WITH THE TYPE IT SERIALISES.</b> docs/plan/06's grain-key
///         table has since won the disagreement <c>ResourceKey</c>'s own remarks record: an
///         <c>IResourceGrain</c> is keyed <c>res/{resourceId:N}</c>, not by the composite
///         <c>{sub:N}/{rg}/{type}/{res:N}</c> this type formats, because keying by GUID alone makes
///         a rename or a move a metadata update instead of a grain migration. <c>ResourceKey</c> is
///         to be replaced by a <c>GrainKeys</c> formatter covering all eight key shapes in that
///         table, most likely a static class with nothing to serialise. When that lands, <b>delete
///         this file and its baseline entries</b> rather than porting it — a grain key that travels
///         on the wire as a value is exactly the coupling the replacement removes. Nothing has
///         shipped, so the aliases and <c>[Id(n)]</c> numbers below are not yet load-bearing.
///     </para>
///     <para>
///         ⚠ <b>The key is carried decomposed, not as its formatted string.</b>
///         <see cref="ResourceKey.ToKeyWithinTenant" /> is the <i>only</i> place allowed to format a
///         grain key (ADR-002, docs/plan/02:147) and <see cref="ResourceKey.Parse" /> the only place
///         allowed to read one back. Putting the formatted string on the wire would make this
///         assembly a second implementation of that grammar the moment the format ever changed, and
///         a wire format that re-parses is a wire format that can be attacked by spelling. The parts
///         go over; the string is derived on arrival.
///     </para>
///     <para>
///         <c>default(ResourceKey)</c> round-trips, for the same reason as
///         <see cref="ResourceIdSurrogate" />.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Core.ResourceKey")]
public struct ResourceKeySurrogate
{
    /// <summary>The owning subscription.</summary>
    [Id(0)]
    public Guid SubscriptionId { get; set; }

    /// <summary>The owning resource group's name.</summary>
    [Id(1)]
    public string? ResourceGroup { get; set; }

    /// <summary>The resource type.</summary>
    [Id(2)]
    public ResourceTypeName Type { get; set; }

    /// <summary>The resource's GUID.</summary>
    [Id(3)]
    public Guid ResourceId { get; set; }
}

/// <summary>The <see cref="ResourceKey" /> ↔ <see cref="ResourceKeySurrogate" /> converter.</summary>
[RegisterConverter]
public sealed class ResourceKeySurrogateConverter : IConverter<ResourceKey, ResourceKeySurrogate>
{
    /// <inheritdoc />
    public ResourceKey ConvertFromSurrogate(in ResourceKeySurrogate surrogate) =>
        surrogate.ResourceGroup is null
            ? default
            : new ResourceKey(
                surrogate.SubscriptionId,
                surrogate.ResourceGroup,
                surrogate.Type,
                surrogate.ResourceId);

    /// <inheritdoc />
    public ResourceKeySurrogate ConvertToSurrogate(in ResourceKey value) =>
        value.ResourceGroup is null
            ? default
            : new ResourceKeySurrogate
            {
                SubscriptionId = value.SubscriptionId,
                ResourceGroup = value.ResourceGroup,
                Type = value.Type,
                ResourceId = value.ResourceId,
            };
}
