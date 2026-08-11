using CyberCloud.Core.Resources;

namespace CyberCloud.Core.Contracts.Serialization;

/// <summary>
///     The wire form of <see cref="ResourceTypeName" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This surrogate re-validates, and a malformed payload throws.</b> That is the one
///         place this assembly deliberately breaks the "substitute, do not throw" rule stated on
///         <see cref="WireErrors" />, and the reason is in
///         <c>CyberCloud.Core/Resources/ResourceTypeName.cs</c>: the type path is what tells
///         <c>ResourceId.TryParsePath</c> where the type ends and the resource <i>name</i> begins.
///         A <see cref="ResourceTypeName" /> smuggled in over the wire with a <c>/</c> in a segment
///         shifts that boundary, and the value the receiver then treats as an address is not the
///         one the sender wrote. An exception is the correct outcome for that; a substituted
///         "unknown type" would be an address.
///     </para>
///     <para>
///         <b><c>default(ResourceTypeName)</c> round-trips.</b> Both members are
///         <see langword="null" /> and the converter maps that back to <c>default</c> rather than
///         calling the validating constructor. This is not a nicety — <c>Result&lt;T&gt;</c> writes
///         <c>default(T)</c> for a failure (see <c>ResultSurrogate{T}</c>), so every failed
///         <c>Result&lt;ResourceTypeName&gt;</c> takes this path.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Core.ResourceTypeName")]
public struct ResourceTypeNameSurrogate {
    /// <summary>The provider namespace, for example <c>CyberCloud.DBforPostgreSQL</c>.</summary>
    [Id(0)]
    public string? Namespace { get; set; }

    /// <summary>The type path, for example <c>servers</c> or <c>servers/databases</c>.</summary>
    [Id(1)]
    public string? Type { get; set; }
}

/// <summary>The <see cref="ResourceTypeName" /> ↔ <see cref="ResourceTypeNameSurrogate" /> converter.</summary>
[RegisterConverter]
public sealed class ResourceTypeNameSurrogateConverter
    : IConverter<ResourceTypeName, ResourceTypeNameSurrogate> {
    /// <inheritdoc />
    public ResourceTypeName ConvertFromSurrogate(in ResourceTypeNameSurrogate surrogate) =>
        surrogate.Namespace is null && surrogate.Type is null
            ? default
            : new ResourceTypeName(surrogate.Namespace!, surrogate.Type!);

    /// <inheritdoc />
    public ResourceTypeNameSurrogate ConvertToSurrogate(in ResourceTypeName value) =>
        value.IsEmpty
            ? default
            : new ResourceTypeNameSurrogate { Namespace = value.Namespace, Type = value.Type };
}
