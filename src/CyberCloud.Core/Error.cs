using System.Collections.Immutable;

namespace CyberCloud.Core;

/// <summary>
///     The one error shape, per docs/plan/08 § Errors. Serialises to Azure's body:
///     <code>
///     { "error": { "code": "QuotaExceeded",
///                  "message": "Subscription quota for 'vcpu' in region 'eu-central' would be exceeded (requested 8, available 2).",
///                  "target": "properties.sku",
///                  "details": [ … ] } }
///     </code>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No exception details, ever</b> (docs/plan/08:190). This type has no field that could
///         carry a stack trace, and that absence is deliberate — a stack trace in an error body is
///         an information leak and a support-cost multiplier. The correlation id goes in a response
///         header and the detail goes to the trace.
///     </para>
///     <para>
///         The JSON serialisation itself lives in <c>CyberCloud.Core.Contracts</c> / the gateway,
///         not here: docs/plan/03:226 keeps Core free of wire concerns.
///     </para>
/// </remarks>
public sealed record Error
{
    /// <summary>Creates an error.</summary>
    /// <param name="code">A registered code. See <see cref="ErrorCode" />.</param>
    /// <param name="message">
    ///     A human-readable message that <b>names the actual values</b> — docs/plan/08:187:
    ///     <i>"Quota exceeded" without the meter, the request and the remainder is a support ticket
    ///     by construction.</i>
    /// </param>
    /// <param name="target">
    ///     An optional RFC 6901 JSON Pointer into the request body, so the portal can highlight the
    ///     field (docs/plan/08:188). Either <c>""</c> (the whole document) or a string of
    ///     <c>"/"</c>-prefixed reference tokens.
    /// </param>
    /// <param name="details">Optional nested errors. A default array is normalised to empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="message" /> is blank, or <paramref name="target" /> is not a JSON
    ///     Pointer. Both are programmer errors, not domain outcomes, so they throw rather than
    ///     returning a <see cref="Result" /> — docs/plan/00:171.
    /// </exception>
    public Error(
        ErrorCode code,
        string message,
        string? target = null,
        ImmutableArray<Error> details = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (target is not null && !IsJsonPointer(target))
        {
            throw new ArgumentException(
                $"'{target}' is not an RFC 6901 JSON Pointer. A target is either \"\" (the whole "
                + "document) or a sequence of \"/\"-prefixed reference tokens, for example "
                + "\"/properties/sku\". docs/plan/08:188.",
                nameof(target));
        }

        Code = code;
        Message = message;
        Target = target;
        Details = details.IsDefault ? [] : details;
    }

    /// <summary>The stable, greppable identifier. Part of the API contract.</summary>
    public ErrorCode Code { get; }

    /// <summary>The human-readable message. Names actual values.</summary>
    public string Message { get; }

    /// <summary>An RFC 6901 JSON Pointer into the request body, or <see langword="null" />.</summary>
    public string? Target { get; }

    /// <summary>Nested errors. Never default; possibly empty.</summary>
    public ImmutableArray<Error> Details { get; }

    /// <summary>Returns this error with <see cref="Target" /> replaced.</summary>
    public Error WithTarget(string? target) => new(Code, Message, target, Details);

    /// <summary>Returns this error with <paramref name="details" /> appended.</summary>
    public Error WithDetails(params ImmutableArray<Error> details) =>
        new(Code, Message, Target, Details.AddRange(details.IsDefault ? [] : details));

    /// <summary>
    ///     RFC 6901 § 3: a pointer is the empty string, or a sequence of parts each beginning with
    ///     <c>'/'</c>. Escaping (<c>~0</c>, <c>~1</c>) is validated too, because a lone <c>'~'</c>
    ///     is the one malformed pointer a hand-written target actually produces.
    /// </summary>
    static bool IsJsonPointer(string target)
    {
        if (target.Length == 0)
        {
            return true;
        }

        if (target[0] != '/')
        {
            return false;
        }

        for (var i = 0; i < target.Length; i++)
        {
            if (target[i] != '~')
            {
                continue;
            }

            if (i + 1 >= target.Length || (target[i + 1] != '0' && target[i + 1] != '1'))
            {
                return false;
            }

            i++;
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(Error? other) =>
        other is not null
        && Code == other.Code
        && string.Equals(Message, other.Message, StringComparison.Ordinal)
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && Details.SequenceEqual(other.Details);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Code);
        hash.Add(Message, StringComparer.Ordinal);
        hash.Add(Target, StringComparer.Ordinal);
        foreach (var detail in Details)
        {
            hash.Add(detail);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() =>
        Target is null ? $"{Code.Value}: {Message}" : $"{Code.Value} ({Target}): {Message}";
}
