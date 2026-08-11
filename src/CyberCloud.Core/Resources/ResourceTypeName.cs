using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CyberCloud.Core.Resources;

/// <summary>
///     A fully qualified resource type: a provider namespace plus a type path, for example
///     <c>CyberCloud.DBforPostgreSQL</c> / <c>servers</c>, or the nested
///     <c>CyberCloud.DBforPostgreSQL</c> / <c>servers/databases</c>.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00:76-80 — every capability is a resource type in a provider namespace.
///         docs/plan/08:116-137 shows both the flat and the nested form in one provider's
///         <c>Describe</c>.
///     </para>
///     <para>
///         <b>Case.</b> Provider namespaces and type names are mixed-case by design
///         (<c>DBforPostgreSQL</c>, <c>virtualMachines</c>) and Azure treats them as
///         case-insensitive. This type preserves the casing it was given — it is what the portal and
///         the CLI display — and compares case-insensitively, so
///         <c>cybercloud.dbforpostgresql/servers</c> and <c>CyberCloud.DBforPostgreSQL/servers</c>
///         are the same type. <see cref="Canonical" /> is the lower-cased form to hash or index on.
///     </para>
///     <para>
///         <b>Depth is capped at <see cref="MaxDepth" />.</b> An uncapped type path would make the
///         resource id grammar (docs/plan/06:36-39) unboundedly greedy — the parser cannot tell
///         where the type ends and the name begins except by counting, and an attacker who controls
///         a segment would like that ambiguity. Azure's own deepest types are two levels; three is
///         headroom.
///     </para>
/// </remarks>
public readonly record struct ResourceTypeName
{
    /// <summary>The deepest nesting a type path may have. <c>servers/databases</c> is 2.</summary>
    public const int MaxDepth = 3;

    /// <summary>The longest a single namespace or type segment may be.</summary>
    public const int MaxSegmentLength = 63;

    readonly string? providerNamespace;
    readonly string? typePath;

    /// <summary>Creates a resource type name, validating both parts.</summary>
    /// <param name="providerNamespace">
    ///     For example <c>CyberCloud.DBforPostgreSQL</c>. Two or more <c>.</c>-separated segments of
    ///     <c>[A-Za-z][A-Za-z0-9]*</c>.
    /// </param>
    /// <param name="typePath">
    ///     For example <c>servers</c> or <c>servers/databases</c>. One to <see cref="MaxDepth" />
    ///     <c>/</c>-separated segments of <c>[A-Za-z][A-Za-z0-9]*</c>.
    /// </param>
    /// <exception cref="ArgumentException">Either part is malformed.</exception>
    /// <remarks>
    ///     Throws rather than returning a <see cref="Result" /> because a malformed type in code is
    ///     a bug (docs/plan/00:171). <see cref="Create" /> is the <see cref="Result" />-returning
    ///     form for input that came from a request.
    /// </remarks>
    public ResourceTypeName(string providerNamespace, string typePath)
    {
        var problem = DescribeNamespace(providerNamespace) ?? DescribeTypePath(typePath);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(providerNamespace));
        }

        this.providerNamespace = providerNamespace;
        this.typePath = typePath;
    }

    /// <summary>The provider namespace, for example <c>CyberCloud.DBforPostgreSQL</c>.</summary>
    public string Namespace => providerNamespace ?? string.Empty;

    /// <summary>The type path, for example <c>servers</c> or <c>servers/databases</c>.</summary>
    public string Type => typePath ?? string.Empty;

    /// <summary>Whether this is <c>default(ResourceTypeName)</c>.</summary>
    public bool IsEmpty => providerNamespace is null;

    /// <summary>The nesting depth: 1 for <c>servers</c>, 2 for <c>servers/databases</c>.</summary>
    public int Depth
    {
        get
        {
            if (typePath is null)
            {
                return 0;
            }

            var depth = 1;
            foreach (var c in typePath)
            {
                if (c == '/')
                {
                    depth++;
                }
            }

            return depth;
        }
    }

    /// <summary>
    ///     The lower-cased form, for hashing and indexing. docs/plan/06:77 keys the path index on
    ///     <c>sha256(path)</c>, and a hash is only stable if what goes into it is canonical.
    /// </summary>
    public ResourceTypeName Canonical =>
        IsEmpty ? default : new ResourceTypeName(AsciiLower(Namespace), AsciiLower(Type));

    /// <summary>The <see cref="Result" />-returning constructor, for request-shaped input.</summary>
    public static Result<ResourceTypeName> Create(string? providerNamespace, string? typePath)
    {
        var problem = DescribeNamespace(providerNamespace) ?? DescribeTypePath(typePath);
        return problem is null
            ? Result<ResourceTypeName>.Success(new ResourceTypeName(providerNamespace!, typePath!))
            : Result<ResourceTypeName>.Failure(ErrorCode.InvalidResourceType, problem);
    }

    /// <summary>
    ///     Parses the <c>{namespace}/{type}</c> form — the <see cref="ToString" /> round trip.
    /// </summary>
    public static bool TryParse(string? value, out ResourceTypeName type)
    {
        type = default;
        if (value is null)
        {
            return false;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == value.Length - 1)
        {
            return false;
        }

        var result = Create(value[..slash], value[(slash + 1)..]);
        if (result.IsFailure)
        {
            return false;
        }

        type = result.GetValueOrThrow();
        return true;
    }

    /// <summary>Renders <c>{namespace}/{type}</c>.</summary>
    public override string ToString() => IsEmpty ? string.Empty : $"{Namespace}/{Type}";

    /// <summary>Case-insensitive equality — see the remarks on the type.</summary>
    public bool Equals(ResourceTypeName other) =>
        string.Equals(Namespace, other.Namespace, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Type, other.Type, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Namespace),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Type));

    /// <summary>
    ///     ASCII-only lower-casing. <see cref="string.ToLowerInvariant" /> would also fold code
    ///     points such as U+212A KELVIN SIGN and U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE, and
    ///     an identifier canonicaliser that maps two distinct code points onto one is how you get
    ///     two resources with the same index key. Segments are validated ASCII, so this loses
    ///     nothing and closes that door.
    /// </summary>
    internal static string AsciiLower(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        return new string(buffer);
    }

    /// <summary>
    ///     A single <c>[A-Za-z][A-Za-z0-9]*</c> segment of at most
    ///     <see cref="MaxSegmentLength" /> characters.
    /// </summary>
    internal static bool IsValidSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length is 0 || segment.Length > MaxSegmentLength)
        {
            return false;
        }

        if (!char.IsAsciiLetter(segment[0]))
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    static string? DescribeNamespace([NotNullWhen(false)] string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "A provider namespace is required, for example 'CyberCloud.DBforPostgreSQL'. "
                + "See docs/plan/08 § The provider registry.";
        }

        var segments = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i != value.Length && value[i] != '.')
            {
                continue;
            }

            if (!IsValidSegment(value.AsSpan(start, i - start)))
            {
                return "'" + value + "' is not a valid provider namespace: segment "
                    + Int(segments + 1) + " ('" + value[start..i] + "') must be 1-"
                    + Int(MaxSegmentLength) + " characters of [A-Za-z][A-Za-z0-9]*. A namespace "
                    + "looks like 'CyberCloud.DBforPostgreSQL' — see docs/plan/08 § The provider "
                    + "registry.";
            }

            segments++;
            start = i + 1;
        }

        return segments >= 2
            ? null
            : $"'{value}' is not a valid provider namespace: it needs at least two '.'-separated "
            + "segments, for example 'CyberCloud.DBforPostgreSQL'.";
    }

    static string? DescribeTypePath([NotNullWhen(false)] string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "A resource type is required, for example 'servers' or 'servers/databases'.";
        }

        var segments = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i != value.Length && value[i] != '/')
            {
                continue;
            }

            if (!IsValidSegment(value.AsSpan(start, i - start)))
            {
                return "'" + value + "' is not a valid resource type: segment " + Int(segments + 1)
                    + " ('" + value[start..i] + "') must be 1-" + Int(MaxSegmentLength)
                    + " characters of [A-Za-z][A-Za-z0-9]*. A type looks like 'servers' or "
                    + "'servers/databases'.";
            }

            segments++;
            start = i + 1;
        }

        return segments <= MaxDepth
            ? null
            : "'" + value + "' is not a valid resource type: it nests " + Int(segments)
            + " levels deep and the limit is " + Int(MaxDepth) + ". See the remarks on "
            + "ResourceTypeName for why the limit exists — an uncapped type path makes the "
            + "resource id grammar ambiguous.";
    }

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
