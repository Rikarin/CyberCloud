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
/// </remarks>
static class GuidFormat {
    /// <summary>The length of the hyphenated <c>D</c> form.</summary>
    public const int DLength = 36;

    /// <summary>The length of the 32-digit <c>N</c> form.</summary>
    public const int NLength = 32;

    /// <summary>Parses the <c>D</c> form and nothing else — no whitespace, no braces, no <c>N</c>.</summary>
    public static bool TryParseD(string value, out Guid result) {
        if (value.Length != DLength) {
            result = default;
            return false;
        }

        return Guid.TryParseExact(value, "D", out result);
    }

    /// <summary>Parses the <c>N</c> form and nothing else.</summary>
    public static bool TryParseN(string value, out Guid result) {
        if (value.Length != NLength) {
            result = default;
            return false;
        }

        return Guid.TryParseExact(value, "N", out result);
    }
}
