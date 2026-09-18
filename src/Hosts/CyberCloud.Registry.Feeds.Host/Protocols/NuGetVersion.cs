using System.Globalization;

namespace CyberCloud.Registry.Feeds.Host.Protocols;

/// <summary>
///     A NuGet package version, as the client normalises and orders it —
///     <c>Major.Minor.Patch[.Revision][-Prerelease][+Metadata]</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Normalisation is the whole point of the type.</b> A client asks the flat container
///         for <c>my.package/1.0.0/</c> when the nuspec said <c>1.0</c>, and for <c>1.0.0-beta.1</c>
///         when it said <c>1.0.0-BETA.1+build.7</c>: leading zeros go, a fourth part of zero goes,
///         build metadata goes, and the prerelease label keeps its case but compares without it. A
///         catalogue keyed on the nuspec's spelling would answer <c>404</c> to every restore.
///     </para>
///     <para>
///         Ordering follows SemVer 2 as NuGet applies it: numeric parts first, then a version with
///         a prerelease label sorts before the same version without one, then the label's
///         dot-separated identifiers — numeric ones numerically, others ordinally without case,
///         numeric before alphanumeric.
///     </para>
/// </remarks>
public sealed record NuGetVersion(int Major, int Minor, int Patch, int Revision, string Prerelease) :
    IComparable<NuGetVersion> {
    /// <summary>The normalised spelling — what the flat container and the registration use.</summary>
    public string Normalized =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}{(Revision > 0 ? "." + Revision.ToString(CultureInfo.InvariantCulture) : "")}{(Prerelease.Length > 0 ? "-" + Prerelease : "")}"
        );

    /// <summary>
    ///     The normalised spelling lower-cased — what the flat container's paths and the
    ///     catalogue's keys use. <c>1.0.0-Beta.2</c> and <c>1.0.0-beta.2</c> are one version, and
    ///     nuget.org's own flat container spells both <c>1.0.0-beta.2</c>.
    /// </summary>
    public string PathForm => Normalized.ToLowerInvariant();

    /// <summary>Whether the version carries a prerelease label.</summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>Parses a version the way the NuGet client does.</summary>
    /// <param name="text">The version, in any spelling the client accepts.</param>
    /// <returns>The version, or <see cref="ErrorCode.InvalidRequestBody" /> naming what was wrong.</returns>
    public static Result<NuGetVersion> Parse(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return Result<NuGetVersion>.Failure(ErrorCode.InvalidRequestBody, "A package version cannot be empty.");
        }

        var value = text.Trim();

        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) {
            value = value[..plus];
        }

        var prerelease = "";
        var dash = value.IndexOf('-', StringComparison.Ordinal);

        if (dash >= 0) {
            prerelease = value[(dash + 1)..];
            value = value[..dash];

            if (prerelease.Length == 0
                || prerelease.Split('.').Any(static x => x.Length == 0 || !x.All(IsIdentifierCharacter))) {
                return Result<NuGetVersion>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{text}' has a prerelease label that is not dot-separated identifiers."
                );
            }
        }

        var parts = value.Split('.');

        if (parts.Length is < 1 or > 4) {
            return Result<NuGetVersion>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{text}' does not have one to four numeric parts."
            );
        }

        var numbers = new int[4];

        for (var i = 0; i < parts.Length; i++) {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) {
                return Result<NuGetVersion>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{text}' has a part that is not a non-negative integer."
                );
            }
        }

        return Result<NuGetVersion>.Success(new(numbers[0], numbers[1], numbers[2], numbers[3], prerelease));
    }

    /// <inheritdoc />
    public int CompareTo(NuGetVersion? other) {
        if (other is null) {
            return 1;
        }

        var numeric = Major.CompareTo(other.Major);
        if (numeric != 0) {
            return numeric;
        }

        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0) {
            return numeric;
        }

        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) {
            return numeric;
        }

        numeric = Revision.CompareTo(other.Revision);
        if (numeric != 0) {
            return numeric;
        }

        if (IsPrerelease != other.IsPrerelease) {
            return IsPrerelease ? -1 : 1;
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    static int ComparePrerelease(string left, string right) {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++) {
            if (i >= a.Length) {
                return -1;
            }

            if (i >= b.Length) {
                return 1;
            }

            var leftNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            var order = (leftNumeric, rightNumeric) switch {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase)
            };

            if (order != 0) {
                return order;
            }
        }

        return 0;
    }

    static bool IsIdentifierCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';

    /// <summary>Less-than, in the order <see cref="CompareTo" /> defines.</summary>
    public static bool operator <(NuGetVersion left, NuGetVersion right) => Compare(left, right) < 0;

    /// <summary>Greater-than, in the order <see cref="CompareTo" /> defines.</summary>
    public static bool operator >(NuGetVersion left, NuGetVersion right) => Compare(left, right) > 0;

    /// <summary>Less-than-or-equal, in the order <see cref="CompareTo" /> defines.</summary>
    public static bool operator <=(NuGetVersion left, NuGetVersion right) => Compare(left, right) <= 0;

    /// <summary>Greater-than-or-equal, in the order <see cref="CompareTo" /> defines.</summary>
    public static bool operator >=(NuGetVersion left, NuGetVersion right) => Compare(left, right) >= 0;

    static int Compare(NuGetVersion? left, NuGetVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);
}
