using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CyberCloud.Gateway.Host.WellKnown;

/// <summary>
///     The RFC 9116 <c>security.txt</c> this gateway serves at <c>/.well-known/security.txt</c>, read
///     once from the embedded copy of <c>WellKnown/security.txt</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The file is a build input, not configuration, and that is what makes it gateable.</b>
///         RFC 9116 § 2.5.5 makes <c>Expires</c> mandatory, and docs/plan/18 § Disclosure is blunt
///         about the consequence:
///         <i>
///             "a <c>security.txt</c> with a stale <c>Expires</c> is worse than
///             none"
///         </i>. An embedded file has exactly one copy, the one the compiler saw, so
///         <c>SecurityTxtTests.ExpiresIsAtLeastThirtyDaysOut</c> reads the bytes that ship and turns
///         the build red thirty days before they lapse. A file read from disk or from a ConfigMap at
///         start-up would be a file nothing in the build could see expire.
///     </para>
///     <para>
///         ⚠ <b>Served from <c>api.cybercloud.io</c>, which is not where RFC 9116 § 3 says to look.</b>
///         The RFC puts the file on the organizational domain; no ingress or <c>HTTPRoute</c> in this
///         repository terminates one, and none of this host's routes is the apex. <c>Canonical:</c>
///         therefore names the gateway's own origin, so a scanner that finds the file here can verify
///         it is where it claims to be. Serving it at the apex is owed on the ingress that does not
///         exist yet — docs/plan/18 § Disclosure.
///     </para>
///     <para>
///         When <c>ExpiresIsAtLeastThirtyDaysOut</c> goes red: reread docs/security/disclosure-policy.md,
///         confirm the contact is still monitored, and move <c>Expires</c> forward by about eleven
///         months. The RFC recommends less than a year, and the sibling test refuses more.
///     </para>
/// </remarks>
static class SecurityTxt {
    /// <summary>The path RFC 9116 § 3 fixes. The only path under <c>/.well-known</c> this gateway routes.</summary>
    public const string Path = "/.well-known/security.txt";

    /// <summary>The embedded resource's logical name, as the project file declares it.</summary>
    const string ResourceName = "security.txt";

    /// <summary>The file, byte for byte as it was committed.</summary>
    public static string Content { get; } = Read();

    /// <summary>
    ///     Every <c>Name: value</c> line of <see cref="Content" />, in order. Comments and blank lines are
    ///     skipped; names keep their case.
    /// </summary>
    /// <remarks>
    ///     ⚠ A field may repeat — RFC 9116 § 2.5.3 allows several <c>Contact</c> lines and § 2.5.2
    ///     several <c>Canonical</c> lines — so this is a list of pairs and not a dictionary. The one
    ///     field the RFC forbids to repeat is <c>Expires</c>, which the gate checks by counting.
    /// </remarks>
    public static ImmutableArray<(string Name, string Value)> Fields { get; } = Parse(Content);

    /// <summary>Reads the parsed <c>Expires</c>, the field the build gate exists for.</summary>
    /// <returns>
    ///     The instant, when the file has exactly one <c>Expires</c> line and it is the RFC 3339
    ///     date-time RFC 9116 § 2.5.5 requires; otherwise <see langword="null" />.
    /// </returns>
    public static DateTimeOffset? Expires() {
        var lines = Fields.Where(static x => string.Equals(x.Name, "Expires", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count != 1) {
            return null;
        }

        // RFC 3339 § 5.6, which is what RFC 9116 § 2.5.5 names. Roundtrip-kind parsing accepts the
        // `Z` and `±hh:mm` offsets and refuses a date with no time, which the RFC also refuses.
        return DateTimeOffset.TryParseExact(
            lines[0].Value,
            ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var expires
        )
                ? expires
                : null;
    }

    static string Read() {
        using var stream = typeof(SecurityTxt).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{ResourceName}' is missing from {typeof(SecurityTxt).Assembly.GetName().Name}. "
                + "CyberCloud.Gateway.Host.csproj embeds WellKnown/security.txt under that logical name; "
                + "the file or the project line is gone."
            );

        using var reader = new StreamReader(stream, Encoding.UTF8, false);

        return reader.ReadToEnd();
    }

    static ImmutableArray<(string Name, string Value)> Parse(string content) {
        var fields = ImmutableArray.CreateBuilder<(string, string)>();

        // RFC 9116 § 4: a line ends in CRLF or LF, and either is valid on the wire.
        foreach (var raw in content.Split('\n')) {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0 || line[0] == '#') {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) {
                continue;
            }

            fields.Add((line[..colon], line[(colon + 1)..].Trim()));
        }

        return fields.ToImmutable();
    }
}
