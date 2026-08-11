using System.Collections.Immutable;

namespace CyberCloud.Core.Contracts.Serialization;

/// <summary>
///     The wire form of <see cref="Error" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a surrogate and not a mirror type.</b> docs/plan/03 § Foundation offers this assembly for
///         "wire types shared by grains, gateway, SDK", and the obvious reading is a parallel
///         <c>ErrorDto</c> that grains return instead of <see cref="Error" />. That reading loses
///         the property docs/plan/00 § Coding standards depends on: <i>every</i> grain method returns
///         <c>Task&lt;Result&lt;T&gt;&gt;</c>, so a mirror would put a mapping call on both sides of
///         every grain boundary in the platform, and the two types would drift the first time
///         somebody added a member to one of them. An Orleans surrogate keeps <b>one</b> type on
///         both sides — the caller and the callee both hold <c>CyberCloud.Core.Error</c> — and
///         leaves <c>CyberCloud.Core</c> at zero package references, which assembly-graph rule 1
///         (docs/plan/03 § Assembly graph rules) requires.
///     </para>
///     <para>
///         ⚠ <b><see cref="Details" /> is recursive.</b> An <see cref="Error" /> nests
///         <see cref="Error" />s, so the array below is serialised through this same converter. That
///         is fine and it is also the one place a hostile payload could ask for unbounded work; the
///         depth bound is Orleans' own recursion limit, not ours.
///     </para>
///     <para>
///         ⚠ <b><see cref="Code" /> is a string, not the <see cref="ErrorCode" /> object.</b>
///         <see cref="ErrorCode" /> is a closed registry with a private constructor
///         (<c>CyberCloud.Core/ErrorCode.cs</c>), so there is nothing to reconstruct on the far
///         side but the token. <see cref="WireErrors.Resolve" /> handles a token this build does not
///         know.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Core.Error")]
public struct ErrorSurrogate {
    /// <summary>The wire form of <see cref="Error.Code" /> — <see cref="ErrorCode.Value" />.</summary>
    [Id(0)]
    public string? Code { get; set; }

    /// <summary>The wire form of <see cref="Error.Message" />.</summary>
    [Id(1)]
    public string? Message { get; set; }

    /// <summary>The wire form of <see cref="Error.Target" />.</summary>
    [Id(2)]
    public string? Target { get; set; }

    /// <summary>
    ///     The wire form of <see cref="Error.Details" />. <see langword="null" /> and empty mean the
    ///     same thing on the way in; empty is normalised to <see langword="null" /> on the way out
    ///     so the common case costs no array.
    /// </summary>
    [Id(3)]
    public Error[]? Details { get; set; }
}

/// <summary>The <see cref="Error" /> ↔ <see cref="ErrorSurrogate" /> converter.</summary>
[RegisterConverter]
public sealed class ErrorSurrogateConverter : IConverter<Error, ErrorSurrogate> {
    /// <inheritdoc />
    public Error ConvertFromSurrogate(in ErrorSurrogate surrogate) {
        var message = string.IsNullOrWhiteSpace(surrogate.Message)
            ? WireErrors.MissingMessage
            : surrogate.Message;

        var (code, resolved) = WireErrors.Resolve(surrogate.Code, message);

        // ⚠ Target is re-validated by Error's constructor (RFC 6901), and a malformed one throws.
        // That is deliberate: Target is a JSON Pointer the portal feeds straight into a form, and
        // accepting an arbitrary string here would push the malformed value one layer further out.
        return new(
            code,
            resolved,
            surrogate.Target,
            surrogate.Details is { Length: > 0 } details
                ? ImmutableArray.Create(details)
                : ImmutableArray<Error>.Empty
        );
    }

    /// <inheritdoc />
    public ErrorSurrogate ConvertToSurrogate(in Error value) =>
        new() {
            Code = value.Code.Value,
            Message = value.Message,
            Target = value.Target,
            Details = value.Details.IsDefaultOrEmpty ? null : [.. value.Details]
        };
}
