namespace CyberCloud.Cli.Execution;

/// <summary>
///     What <c>--verbose</c> is allowed to print.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The strongest guarantee here is structural, not textual: <c>cyc</c> never holds the
///         token.</b> <c>BearerTokenHandler</c> attaches <c>Authorization</c> <i>inside</i> the SDK's
///         pipeline, below anything this CLI can see, so the request object the CLI traces has no
///         credential on it to leak. This type is the belt to that pair of braces — it exists so that
///         a future header the CLI does set, or a response header the platform starts sending, cannot
///         become a leak by being forgotten.
///     </para>
///     <para>
///         ⚠ <b>Deny by pattern, not by allow-list.</b> An allow-list of printable headers would be
///         the safer design and the wrong one here: the point of <c>--verbose</c> is to show a header
///         nobody anticipated, and a list would hide exactly the header somebody is debugging.
///     </para>
/// </remarks>
static class Redaction {
    /// <summary>The stand-in printed instead of a secret value.</summary>
    public const string Placeholder = "«redacted»";

    static readonly string[] Secretish = [
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "api-key",
        "x-amz-security-token",
        "www-authenticate",
    ];

    /// <summary>Renders one header for a trace line.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The value.</param>
    public static string Header(string name, string? value) {
        ArgumentNullException.ThrowIfNull(name);

        return IsSecret(name) ? $"{name}: {Placeholder}" : $"{name}: {value}";
    }

    /// <summary>Whether a header's value must never be printed.</summary>
    /// <param name="name">The header name, matched case-insensitively — HTTP header names are.</param>
    public static bool IsSecret(string name) {
        ArgumentNullException.ThrowIfNull(name);

        foreach (var candidate in Secretish) {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // ⚠ `www-authenticate` above is not a secret; it is redacted because a claims challenge
        // (docs/plan/11 § Protocol's `amr` step-up) carries an opaque blob a user will paste into a
        // support ticket, and there is no reading of it that helps at the terminal.
        return name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Renders a URL for a trace line, with any query-string credential removed.</summary>
    /// <param name="uri">The URL.</param>
    /// <remarks>
    ///     ⚠ Nothing in this API puts a credential in a query string, and this exists so that nothing
    ///     ever does by accident — a token in a URL is a token in every proxy log between here and the
    ///     gateway.
    /// </remarks>
    public static string Url(Uri uri) {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.Query.Length == 0)
            return uri.ToString();

        var parts = new List<string>();

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? pair : pair[..separator];

            parts.Add(IsSecret(name) ? $"{name}={Placeholder}" : pair);
        }

        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', parts);
    }
}
