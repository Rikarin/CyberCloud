namespace CyberCloud.Core.Resources;

/// <summary>
///     Strict GUID parsing for the two formats the identifiers use — <c>D</c> in a resource id path
///     (docs/plan/06 § Identifiers) and <c>N</c> in a grain key (docs/plan/06 § Grain keys).
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Guid.TryParseExact(string, string, out Guid)" /> is not exact.</b> It trims
///     leading and trailing whitespace before matching the format, so
///     <c>Guid.TryParseExact(" 2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", "D", out _)</c> returns
///     <see langword="true" />. In an identifier parser that is a round-trip break, not a nicety:
///     the path <c>/tenants/ 2b4a…/…</c> would parse, and the id it produced would re-emit a path
///     that is a different string — two spellings of one resource, and two entries in a
///     <c>sha256(path)</c> index. Verified against .NET 10 by
///     <c>ResourceIdTests.OnlyTheHyphenatedLowerCaseGuidFormIsAcceptedInAPath</c>, which fails
///     without the length guard below.
///     <para>
///         ⚠ <b>Public, and the reason is that the rule is needed <i>before</i> a path is parsed.</b>
///         <see cref="ResourceId.TryParsePath" /> is the enforcement point for a whole id, but the
///         gateway reads a tenant segment out of a URL that may be malformed further along and reads a
///         subscription segment without parsing at all — so it needs this rule on its own, one segment
///         at a time. It had a byte-for-byte copy of <c>TryParseD</c> for exactly that, which made the
///         strictness argument something two files had to keep agreeing about. One rule, one
///         implementation, and the assertion below pins the BCL behaviour that makes it necessary.
///     </para>
/// </remarks>
public static class GuidFormat {
    /// <summary>The length of the hyphenated <c>D</c> form: 32 hex digits and four hyphens.</summary>
    public const int DLength = 36;

    /// <summary>The length of the 32-digit <c>N</c> form.</summary>
    public const int NLength = 32;

    /// <summary>Parses the <c>D</c> form and nothing else — no whitespace, no braces, no <c>N</c>.</summary>
    /// <param name="value">The candidate. May be <see langword="null" />.</param>
    /// <param name="result">The GUID on success, <see cref="Guid.Empty" /> otherwise.</param>
    /// <returns><c>true</c> when the value is exactly 36 characters of hyphenated hex.</returns>
    /// <remarks>
    ///     ⚠ The length guard is not belt-and-braces. <see cref="Guid.TryParseExact(string, string, out Guid)" />
    ///     trims surrounding whitespace, so <c>" 2b4a…"</c> passes it — see this type's remarks, and
    ///     <c>ResourceIdTests.GuidTryParseExactIsNotActuallyExactAndThatIsWhyGuidFormatExists</c>,
    ///     which fails the day the BCL changes and the guard becomes redundant.
    /// </remarks>
    public static bool TryParseD(string? value, out Guid result) {
        result = Guid.Empty;

        return value is { Length: DLength } && Guid.TryParseExact(value, "D", out result);
    }

    /// <summary>Parses the <c>N</c> form and nothing else.</summary>
    /// <param name="value">The candidate. May be <see langword="null" />.</param>
    /// <param name="result">The GUID on success, <see cref="Guid.Empty" /> otherwise.</param>
    /// <returns><c>true</c> when the value is exactly 32 hex digits.</returns>
    public static bool TryParseN(string? value, out Guid result) {
        result = Guid.Empty;

        return value is { Length: NLength } && Guid.TryParseExact(value, "N", out result);
    }
}
