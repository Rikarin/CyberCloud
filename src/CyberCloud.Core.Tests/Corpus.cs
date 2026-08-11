using System.Globalization;
using CyberCloud.Core.Resources;

namespace CyberCloud.Core.Tests;

/// <summary>
///     Deterministic generators for the property-style tests.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why hand-rolled and not FsCheck/CsCheck.</b> Central Package Management
///         (Directory.Packages.props) forbids a version that is not pinned there, and its own header
///         says a package outside docs/plan/02's register needs an ADR before it is added. Writing
///         an ADR to get shrinking is not the right trade for the first product assembly, so this
///         file is a seeded generator instead: same effect (thousands of inputs, not three
///         hand-picked ones), reproducible by seed, no dependency. Swap it for a real property
///         framework when something else in the repository already justifies the package.
///     </para>
///     <para>
///         Every generator is seeded from a constant, so a failure is reproducible from the test
///         name alone.
///     </para>
/// </remarks>
static class Corpus
{
    /// <summary>
    ///     The characters that make identifier code dangerous. Each one is a separator in one of
    ///     the two encodings this assembly owns, an escape in the other, or a thing that looks like
    ///     one to a human reading a log line.
    /// </summary>
    public static readonly (string Value, string Why)[] InjectionCharacters =
    [
        ("|", "the Orleans.Multitenant tenant/key separator (verified against 4.0.0)"),
        ("||", "the Orleans.Multitenant escaped separator"),
        ("/", "the resource id path separator AND the grain key separator"),
        ("~", "the Orleans.Multitenant leading-character escape"),
        ("\\", "a separator on the other operating system, and a JSON escape"),
        ("\n", "splits a log line in two; the second half is attacker-controlled"),
        ("\r", "the same, on the other line ending"),
        ("\0", "terminates a C string; everything after it disappears in the wrong consumer"),
        ("\t", "invisible in every UI that renders it"),
        (" ", "invisible at the end of a name"),
        (".", "path traversal's first ingredient"),
        ("..", "path traversal"),
        (":", "a separator in the Kubernetes label and the ReBAC tuple grammars"),
        ("%2F", "a URL-encoded '/', in case anything decodes twice"),
        ("İ", "LATIN CAPITAL LETTER I WITH DOT ABOVE — folds onto 'i' in a Turkish locale"),
        ("K", "KELVIN SIGN — a look-alike for 'K' that lower-cases to 'k' under some foldings"),
        ("／", "FULLWIDTH SOLIDUS — a look-alike for '/'"),
        ("｜", "FULLWIDTH VERTICAL LINE — a look-alike for '|'"),
        ("⁄", "FRACTION SLASH — another look-alike for '/'"),
        ("е", "CYRILLIC SMALL LETTER IE — a look-alike for 'e'"),
        ("​", "ZERO WIDTH SPACE — invisible everywhere"),
        ("﻿", "ZERO WIDTH NO-BREAK SPACE — invisible everywhere"),
        ("A", "upper case, which docs/plan/06:59 forbids"),
        ("_", "legal in a Kubernetes annotation, illegal in a DNS-1123 label")
    ];

    /// <summary>
    ///     Generates <paramref name="count" /> names that satisfy
    ///     <see cref="ResourceNaming" />, spread across the whole legal length range.
    /// </summary>
    public static IEnumerable<string> ValidNames(int count, int seed)
    {
        const string first = "abcdefghijklmnopqrstuvwxyz0123456789";
        const string middle = "abcdefghijklmnopqrstuvwxyz0123456789-";

        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            // Bias towards the boundaries: a generator that only ever produces 20-character names
            // never tests the thing that breaks.
            var length = (i % 8) switch
            {
                0 => ResourceNaming.MinLength,
                1 => 2,
                2 => ResourceNaming.MaxLength,
                3 => ResourceNaming.MaxLength - 1,
                _ => random.Next(ResourceNaming.MinLength, ResourceNaming.MaxLength + 1)
            };

            var chars = new char[length];
            chars[0] = first[random.Next(first.Length)];
            for (var j = 1; j < length; j++)
            {
                chars[j] = middle[random.Next(middle.Length)];
            }

            if (length > 1)
            {
                chars[^1] = first[random.Next(first.Length)];
            }

            yield return new string(chars);
        }
    }

    /// <summary>Generates valid provider namespaces, including mixed case.</summary>
    public static IEnumerable<string> ValidNamespaces(int count, int seed)
    {
        string[] heads = ["CyberCloud", "cybercloud", "Contoso", "a", "X9"];
        string[] tails =
        [
            "DBforPostgreSQL", "Cache", "Network", "Storage", "Compute", "Platform",
            "dbforpostgresql", "K8s", "A1"
        ];

        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var segments = random.Next(2, 4);
            var value = heads[random.Next(heads.Length)];
            for (var j = 1; j < segments; j++)
            {
                value += "." + tails[random.Next(tails.Length)];
            }

            yield return value;
        }
    }

    /// <summary>Generates valid type paths, at every legal nesting depth.</summary>
    public static IEnumerable<string> ValidTypePaths(int count, int seed)
    {
        string[] segments =
        [
            "servers", "databases", "redis", "virtualMachines", "agentPools", "zones",
            "recordSets", "a", "X1", "vaults", "secrets"
        ];

        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var depth = (i % 3) + 1;
            var value = segments[random.Next(segments.Length)];
            for (var j = 1; j < depth; j++)
            {
                value += "/" + segments[random.Next(segments.Length)];
            }

            yield return value;
        }
    }

    /// <summary>Generates <paramref name="count" /> fully populated resource ids.</summary>
    public static IEnumerable<ResourceId> ResourceIds(int count, int seed)
    {
        var random = new Random(seed);
        using var names = ValidNames(count * 2, seed + 1).GetEnumerator();
        using var namespaces = ValidNamespaces(count, seed + 2).GetEnumerator();
        using var typePaths = ValidTypePaths(count, seed + 3).GetEnumerator();

        for (var i = 0; i < count; i++)
        {
            names.MoveNext();
            var group = names.Current;
            names.MoveNext();
            var name = names.Current;
            namespaces.MoveNext();
            typePaths.MoveNext();

            yield return new ResourceId(
                NextGuid(random),
                NextGuid(random),
                group,
                new ResourceTypeName(namespaces.Current, typePaths.Current),
                name,
                NextGuid(random));
        }
    }

    /// <summary>A GUID from a seeded <see cref="Random" />, so the corpus is reproducible.</summary>
    public static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <summary>Renders a string with its control characters visible, for assertion messages.</summary>
    public static string Printable(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (char.IsControl(c) || c > 0x7E)
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
