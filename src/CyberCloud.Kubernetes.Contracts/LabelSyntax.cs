using System.Globalization;
using System.Text;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     Kubernetes' own validation rules for label <i>keys</i> and label <i>values</i>, which are two
///     different rules and are routinely confused.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § The command builder states one half of this — "label values are limited to
///         63 characters and a restricted alphabet" — and the seven mandatory keys in ADR-013 are
///         governed by the <i>other</i> half, which the document does not state at all. Both are
///         transcribed here from <c>k8s.io/apimachinery/pkg/util/validation</c> so that a malformed
///         label is a local failure with a message, rather than a rejected admission six minutes into
///         a provision with an error that names neither the label nor the resource.
///     </para>
///     <para>
///         <b>Value</b> — <c>IsValidLabelValue</c>. At most <see cref="MaxValueLength" /> characters;
///         may be empty; if non-empty must begin and end with <c>[A-Za-z0-9]</c> and may contain
///         <c>-</c>, <c>_</c> and <c>.</c> in between. Regex:
///         <c>(([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9])?</c>
///     </para>
///     <para>
///         <b>Key</b> — <c>IsQualifiedName</c>. An optional DNS-subdomain prefix, a <c>/</c>, then a
///         name. The name part obeys the same alphabet as a value but may <b>not</b> be empty and is
///         capped at <see cref="MaxNameLength" />. The prefix is a DNS-1123 <i>subdomain</i>: at most
///         <see cref="MaxPrefixLength" /> characters, lower-case only, dot-separated labels of
///         <c>[a-z0-9]([-a-z0-9]*[a-z0-9])?</c>.
///     </para>
///     <para>
///         ⚠ <b>The two rules differ in ways that matter for exactly our key set.</b> A value may be
///         empty; a name part may not. A value may contain upper case; a <i>prefix</i> may not. Our
///         keys are all prefixed with <c>cybercloud.io</c>, so the prefix rule is live for every one
///         of the seven — and <c>CyberCloud.io</c> would be rejected by the API server while
///         <c>cybercloud.io</c> is accepted, which is a one-character difference between "works" and
///         "every object in the platform fails admission".
///     </para>
/// </remarks>
public static class LabelSyntax {
    /// <summary>The longest a label value may be — 63. docs/plan/09 § The command builder.</summary>
    public const int MaxValueLength = 63;

    /// <summary>The longest the name part of a label key may be — 63.</summary>
    public const int MaxNameLength = 63;

    /// <summary>The longest the prefix of a label key may be — 253, a DNS subdomain.</summary>
    public const int MaxPrefixLength = 253;

    /// <summary>The longest a single dot-separated segment of a DNS subdomain may be — 63.</summary>
    public const int MaxPrefixSegmentLength = 63;

    /// <summary>The value regex, as Kubernetes writes it. For messages and tests.</summary>
    public const string ValuePattern = "(([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9])?";

    /// <summary>The key name-part regex, as Kubernetes writes it.</summary>
    public const string NamePattern = "([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9]";

    /// <summary>The DNS-1123 subdomain regex the key prefix must match.</summary>
    public const string PrefixPattern = "[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*";

    /// <summary>Whether <paramref name="value" /> is a legal label value.</summary>
    /// <param name="value">The candidate. <see langword="null" /> is not legal; empty is.</param>
    public static bool IsValidValue(string? value) => DescribeValue(value) is null;

    /// <summary>Whether <paramref name="key" /> is a legal label key.</summary>
    /// <param name="key">The candidate, with or without a <c>prefix/</c>.</param>
    public static bool IsValidKey(string? key) => DescribeKey(key) is null;

    /// <summary>
    ///     Validates a label value, naming the offending character and the rule when it fails.
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="key">The key it is being set under, so the message can name it.</param>
    public static Result ValidateValue(string? value, string key) {
        var problem = DescribeValue(value);
        return problem is null
            ? Result.Success
            : Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{Show(value)}' is not a legal value for the Kubernetes label '{key}': {problem}. "
                + "A label value is at most "
                + Int(MaxValueLength)
                + " characters, must begin and "
                + "end with a letter or a digit, and may contain '-', '_' and '.' in between "
                + "(regex: "
                + ValuePattern
                + "). An object carrying an illegal label is rejected by "
                + "the API server at admission, which surfaces as a provisioning failure whose "
                + "message names neither the label nor the resource — docs/plan/09 § The command "
                + "builder."
            );
    }

    /// <summary>Validates a label key, naming which half of the rule it broke.</summary>
    /// <param name="key">The candidate key.</param>
    public static Result ValidateKey(string? key) {
        var problem = DescribeKey(key);
        return problem is null
            ? Result.Success
            : Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{Show(key)}' is not a legal Kubernetes label key: {problem}. A key is an optional "
                + "DNS-subdomain prefix and a '/', then a name of at most "
                + Int(MaxNameLength)
                + " characters beginning and ending with a letter or a digit (regex: "
                + NamePattern
                + "). The prefix is lower-case only and at most "
                + Int(MaxPrefixLength)
                + " characters."
            );
    }

    static string? DescribeValue(string? value) {
        if (value is null) {
            return "it is null, and a label value must be a string (an empty one is legal, null is "
                + "not)";
        }

        if (value.Length > MaxValueLength) {
            return "it is "
                + Int(value.Length)
                + " characters long and the limit is "
                + Int(MaxValueLength);
        }

        // Empty is explicitly legal for a value. This is the single place the value rule and the
        // key-name rule diverge on emptiness, so it is written out rather than folded together.
        return value.Length == 0 ? null : DescribeAlphanumericBounded(value);
    }

    static string? DescribeKey(string? key) {
        if (string.IsNullOrEmpty(key)) {
            return "it is null or empty";
        }

        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) {
            return DescribeName(key, "the key");
        }

        if (key.IndexOf('/', slash + 1) >= 0) {
            return "it contains more than one '/', and a key is at most 'prefix/name'";
        }

        var prefix = key[..slash];
        var name = key[(slash + 1)..];

        return DescribePrefix(prefix) ?? DescribeName(name, "the name part after the '/'");
    }

    static string? DescribeName(string name, string what) {
        if (name.Length == 0) {
            return what + " is empty, and unlike a label value a key's name part may not be";
        }

        return name.Length > MaxNameLength
            ? what
            + " is "
            + Int(name.Length)
            + " characters long and the limit is "
            + Int(MaxNameLength)
            : DescribeAlphanumericBounded(name);
    }

    static string? DescribePrefix(string prefix) {
        if (prefix.Length == 0) {
            return "it begins with '/', so the prefix is empty — write the bare name instead";
        }

        if (prefix.Length > MaxPrefixLength) {
            return "its prefix is "
                + Int(prefix.Length)
                + " characters long and a DNS subdomain is "
                + "capped at "
                + Int(MaxPrefixLength);
        }

        var start = 0;
        for (var i = 0; i <= prefix.Length; i++) {
            if (i != prefix.Length && prefix[i] != '.') {
                continue;
            }

            var segment = prefix.AsSpan(start, i - start);
            var problem = DescribePrefixSegment(segment);
            if (problem is not null) {
                return "its prefix segment '"
                    + new string(segment)
                    + "' "
                    + problem
                    + ". A key prefix is a DNS-1123 subdomain (regex: "
                    + PrefixPattern
                    + "), which is lower-case only — 'CyberCloud.io' is rejected where "
                    + "'cybercloud.io' is accepted";
            }

            start = i + 1;
        }

        return null;
    }

    static string? DescribePrefixSegment(ReadOnlySpan<char> segment) {
        if (segment.Length == 0) {
            return "is empty (a doubled, leading or trailing '.')";
        }

        if (segment.Length > MaxPrefixSegmentLength) {
            return "is "
                + Int(segment.Length)
                + " characters and a subdomain segment is capped at "
                + Int(MaxPrefixSegmentLength);
        }

        foreach (var c in segment) {
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) {
                return "contains " + Describe(c) + ", which is not one of a-z, 0-9 or '-'";
            }
        }

        if (segment[0] == '-' || segment[^1] == '-') {
            return "begins or ends with '-'";
        }

        return null;
    }

    /// <summary>
    ///     The shared half of the value rule and the key-name rule: begins and ends
    ///     <c>[A-Za-z0-9]</c>, with <c>-</c>, <c>_</c> and <c>.</c> permitted in between.
    /// </summary>
    static string? DescribeAlphanumericBounded(string value) {
        for (var i = 0; i < value.Length; i++) {
            var c = value[i];
            if (c is not (>= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-'
                or '_'
                or '.')) {
                return Describe(c)
                    + " at position "
                    + Int(i)
                    + " is not one of A-Z, a-z, 0-9, '-', '_' or '.'";
            }
        }

        if (!char.IsAsciiLetterOrDigit(value[0])) {
            return "it begins with '" + value[0] + "', and it must begin with a letter or a digit";
        }

        return char.IsAsciiLetterOrDigit(value[^1])
            ? null
            : "it ends with '" + value[^1] + "', and it must end with a letter or a digit";
    }

    static string Describe(char c) =>
        char.IsControl(c) || char.IsWhiteSpace(c) || c > 0x7E
            ? "the character U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
            : "'" + c + "'";

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a candidate safely — a control character in a label is exactly the input this rejects.</summary>
    static string Show(string? value) {
        if (value is null) {
            return "null";
        }

        var needsEscaping = false;
        foreach (var c in value) {
            if (char.IsControl(c)) {
                needsEscaping = true;
                break;
            }
        }

        if (!needsEscaping) {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value) {
            if (char.IsControl(c)) {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            } else {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
