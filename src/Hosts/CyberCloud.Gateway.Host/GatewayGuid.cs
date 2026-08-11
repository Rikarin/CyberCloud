namespace CyberCloud.Gateway.Host;

/// <summary>
///     Parses a GUID in the hyphenated <c>D</c> form and nothing else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This duplicates <c>CyberCloud.Core.Resources.GuidFormat</c>, which is
///         <c>internal</c>.</b> The rule it implements is not the gateway's to invent — docs/plan/06
///         § Identifiers fixes the <c>D</c> form for every id in a path, and
///         <see cref="ResourceId.TryParsePath" /> enforces it. The gateway needs the same rule
///         <i>before</i> a path is parsed: the tenant check reads a tenant segment out of a URL that
///         may be malformed further along, and the rate limiter reads a subscription segment without
///         parsing at all.
///     </para>
///     <para>
///         <b>Why the strictness matters here specifically.</b>
///         <see cref="Guid.TryParse(string, out Guid)" /> accepts the braced, parenthesised, bare-hex
///         and hex-array forms, so <c>{9f3…}</c> and <c>9f3…</c> would be two spellings of one
///         tenant. A tenant check that accepted a spelling the path parser rejects is a check that
///         can be walked around — and this one has no second line behind it. Even
///         <see cref="Guid.TryParseExact(string, string, out Guid)" /> is not enough on its own: it
///         trims surrounding whitespace, so <c>" 9f3…"</c> would pass here and fail downstream.
///     </para>
///     <para>
///         Making <c>GuidFormat</c> public in <c>CyberCloud.Core</c> would remove this file and is
///         the better answer; it is a one-line change to an assembly three other branches are editing
///         right now, so it is named here rather than taken.
///     </para>
/// </remarks>
static class GatewayGuid {
    /// <summary>The length of the <c>D</c> form: 32 hex digits and four hyphens.</summary>
    public const int DLength = 36;

    /// <summary>Parses the <c>D</c> form. Rejects every other spelling, and never throws.</summary>
    /// <param name="value">The candidate. May be <see langword="null" />.</param>
    /// <param name="id">The GUID on success.</param>
    /// <returns><c>true</c> when the value is exactly 36 characters of hyphenated hex.</returns>
    public static bool TryParseD(string? value, out Guid id) {
        id = Guid.Empty;

        return value is { Length: DLength } && Guid.TryParseExact(value, "D", out id);
    }
}
