using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>
///     An api-version: a date, and immutable.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § The provider registry:
///         <i>
///             "<b>API versions are dates and they are immutable.</b> Adding a field is a new
///             version. The registry keeps every version and each has its own schema and its own
///             body↔state mapping; the grain's state is a <i>superset</i> and a read at an old
///             version projects down. Removing a version needs a 12-month notice window and a build
///             gate that fails on a version removed without one. Yes, this is heavy. It is the
///             difference between an SDK that keeps working and a platform nobody automates against."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>A date and nothing else — no <c>-preview</c>, no <c>v2</c>, no "latest".</b> Azure
///         permits <c>2026-08-01-preview</c> and it is left out on purpose: a preview suffix is a
///         promise that the version may change, which is the exact promise this type exists to
///         forbid. A preview API gets a date like everything else and a deprecation notice like
///         everything else. "Latest" is left out for the sharper reason — a client that says "latest"
///         has asked to be broken by the next release, and the whole point of the immutable date is
///         that nobody can ask for that by accident.
///     </para>
///     <para>
///         <b>Ordering is lexicographic and chronological at once</b>, because <c>yyyy-MM-dd</c> sorts
///         that way. That is why the format is fixed rather than parsed leniently: the registry sorts
///         versions to find the newest, and a format where sorting and chronology disagreed would
///         make "the newest schema" quietly wrong.
///     </para>
/// </remarks>
public readonly record struct ApiVersion : IComparable<ApiVersion> {
    /// <summary>The exact length of a well-formed api-version — <c>yyyy-MM-dd</c>.</summary>
    public const int Length = 10;

    readonly string? value;

    /// <summary>The wire form, <c>yyyy-MM-dd</c>. Empty for <c>default</c>.</summary>
    public string Value => value ?? string.Empty;

    /// <summary>Whether this is <c>default(ApiVersion)</c>.</summary>
    public bool IsEmpty => value is null;

    /// <summary>The date this version is.</summary>
    public DateOnly Date { get; }

    ApiVersion(string wire, DateOnly date) {
        value = wire;
        Date = date;
    }

    /// <summary>Creates an api-version, throwing on anything that is not a date.</summary>
    /// <param name="value">The wire form, <c>yyyy-MM-dd</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="value" /> is not a <c>yyyy-MM-dd</c> date.</exception>
    /// <remarks>
    ///     Throws rather than returning a <see cref="Result" /> because a malformed version in a
    ///     provider's <c>Describe</c> is a bug (docs/plan/00 § Coding standards). Use
    ///     <see cref="TryParse" /> for a version that came off a query string.
    /// </remarks>
    public static ApiVersion Parse(string value) {
        if (!TryParse(value, out var version)) {
            throw new ArgumentException(Explain(value), nameof(value));
        }

        return version;
    }

    /// <summary>Parses an api-version off a query string. Never throws.</summary>
    /// <param name="value">The candidate. May be <see langword="null" />.</param>
    /// <param name="version">The parsed version on success.</param>
    /// <returns><c>true</c> when <paramref name="value" /> is exactly a <c>yyyy-MM-dd</c> date.</returns>
    /// <remarks>
    ///     ⚠ Strict on purpose. <see cref="DateOnly.TryParse(string, out DateOnly)" /> would accept
    ///     <c>2026-8-1</c>, <c>08/01/2026</c> and a trailing space, which are three more spellings of
    ///     one version — and the registry keys on the string, so three spellings would be three
    ///     lookups, two of which miss.
    /// </remarks>
    public static bool TryParse(string? value, out ApiVersion version) {
        version = default;

        if (value is null || value.Length != Length) {
            return false;
        }

        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date
            )) {
            return false;
        }

        version = new(value, date);
        return true;
    }

    /// <summary>
    ///     <see cref="TryParse" /> with an explanation, for a request that named an unparseable
    ///     version.
    /// </summary>
    /// <param name="value">The candidate.</param>
    public static Result<ApiVersion> Create(string? value) =>
        TryParse(value, out var version)
            ? Result<ApiVersion>.Success(version)
            : Result<ApiVersion>.Failure(ErrorCode.InvalidApiVersion, Explain(value));

    /// <inheritdoc />
    public int CompareTo(ApiVersion other) => Date.CompareTo(other.Date);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Whether <paramref name="left" /> is older than <paramref name="right" />.</summary>
    public static bool operator <(ApiVersion left, ApiVersion right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left" /> is newer than <paramref name="right" />.</summary>
    public static bool operator >(ApiVersion left, ApiVersion right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left" /> is not newer than <paramref name="right" />.</summary>
    public static bool operator <=(ApiVersion left, ApiVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left" /> is not older than <paramref name="right" />.</summary>
    public static bool operator >=(ApiVersion left, ApiVersion right) => left.CompareTo(right) >= 0;

    static string Explain([NotNullWhen(false)] string? value) =>
        $"'{value}' is not an api-version. An api-version is a date in the form 'yyyy-MM-dd', for "
        + "example '2026-08-01'. There is no 'latest' and no '-preview' suffix: a version is "
        + "immutable once published, so adding a field is a new date — docs/plan/08 § The provider "
        + "registry.";
}
