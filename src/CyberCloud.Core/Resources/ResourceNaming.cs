using System.Globalization;
using System.Text;

namespace CyberCloud.Core.Resources;

/// <summary>
///     The naming rule for resource groups and resources, decided once so that no provider
///     re-litigates it — docs/plan/06:87-90.
/// </summary>
/// <remarks>
///     <para>
///         <b>The rule.</b> 1–63 characters, matching <c>[a-z0-9]([-a-z0-9]*[a-z0-9])?</c>,
///         case-insensitive-unique within the parent. That is the Kubernetes DNS-1123 <i>label</i>
///         rule, chosen because these names become Kubernetes object names and the alternative is a
///         mangling function nobody can invert.
///     </para>
///     <para>
///         <b>Why 63 and not something rounder.</b> docs/plan/09 notes that a Kubernetes label value
///         caps at 63 characters, and docs/plan/08:84 puts the resource id in the
///         <c>cybercloud.io/resource-id</c> label that makes drift detection a hash join. A name
///         that passes here must therefore be usable as a label value unchanged.
///     </para>
///     <para>
///         <b>Case is not folded, it is rejected.</b> docs/plan/06:88 says names are
///         case-insensitive-unique; the character class in the same sentence already forbids
///         upper-case. Rejecting <c>PROD</c> rather than silently lowering it to <c>prod</c> is what
///         makes uniqueness trivially true (no two stored names can differ only by case) and keeps
///         the promise in docs/plan/06:92-94 that we do not mangle. The consequence for path
///         parsing is stated on <see cref="ResourceId.TryParsePath" />.
///     </para>
///     <para>
///         ⚠ <b>This rule is load-bearing for identifier integrity, not just for aesthetics.</b>
///         The resource id path (docs/plan/06:36-39) is <c>/</c>-delimited and resource types nest
///         (<c>servers/databases</c>), so its grammar is only unambiguous while names contain no
///         <c>/</c>. The grain key (ADR-002) is embedded in a <c>|</c>-delimited tenant-qualified
///         key. Excluding <c>/</c>, <c>|</c>, <c>~</c>, <c>\</c>, whitespace, control characters and
///         every non-ASCII code point is what makes both encodings injective. See
///         <c>ResourceNamingTests</c> § separator injection.
///     </para>
/// </remarks>
public static class ResourceNaming {
    /// <summary>The shortest legal name.</summary>
    public const int MinLength = 1;

    /// <summary>
    ///     The longest legal name. Equal to the Kubernetes label-value cap, deliberately.
    /// </summary>
    public const int MaxLength = 63;

    /// <summary>The rule, as the regular expression docs/plan/06:88 states it.</summary>
    public const string Pattern = "[a-z0-9]([-a-z0-9]*[a-z0-9])?";

    /// <summary>Whether <paramref name="name" /> satisfies the rule.</summary>
    public static bool IsValid(string? name) => Describe(name) is null;

    /// <summary>
    ///     Validates <paramref name="name" />, returning a <see cref="Result" /> whose message
    ///     explains the rule and names the offending value and character.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <param name="kind">
    ///     What is being named, woven into the message — for example <c>"resource group name"</c> or
    ///     <c>"resource name"</c>. Defaults to <c>"resource name"</c>.
    /// </param>
    /// <param name="target">
    ///     An optional RFC 6901 JSON Pointer at the offending field, so the portal can highlight it.
    /// </param>
    /// <remarks>
    ///     docs/plan/06:92-94 is explicit that the message <i>is</i> the mitigation: Azure accepts a
    ///     wide character set and mangles it at the fabric, and the mangling is where the "why is my
    ///     resource called <c>pg-a7f3</c>" support tickets come from. So the message states the
    ///     rule, points at the character that broke it, and says why we are stricter.
    /// </remarks>
    public static Result Validate(string? name, string kind = "resource name", string? target = null) {
        var problem = Describe(name);
        return problem is null
            ? Result.Success
            : Result.Failure(ErrorCode.InvalidResourceName, Explain(name, kind, problem), target);
    }

    /// <summary>
    ///     Returns a description of the first thing wrong with <paramref name="name" />, or
    ///     <see langword="null" /> if it is valid.
    /// </summary>
    static string? Describe(string? name) {
        if (name is null) {
            return "it is null";
        }

        if (name.Length < MinLength) {
            return "it is empty";
        }

        if (name.Length > MaxLength) {
            return "it is " + Int(name.Length) + " characters long and the limit is " + Int(MaxLength);
        }

        for (var i = 0; i < name.Length; i++) {
            var c = name[i];
            var allowed = c is >= 'a' and <= 'z' or >= '0' and <= '9' || c == '-';
            if (!allowed) {
                return DescribeCharacter(c)
                    + " at position "
                    + Int(i)
                    + " is not one of a-z, 0-9 or '-'";
            }
        }

        if (name[0] == '-') {
            return "it starts with '-', and a name must start with a letter or a digit";
        }

        if (name[^1] == '-') {
            return "it ends with '-', and a name must end with a letter or a digit";
        }

        return null;
    }

    /// <summary>Names the character without pasting a control code into a log line.</summary>
    static string DescribeCharacter(char c) {
        var codePoint = "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
        return char.IsControl(c) || char.IsWhiteSpace(c) || c > 0x7E
            ? "the character " + codePoint
            : "'" + c + "' (" + codePoint + ")";
    }

    static string Explain(string? name, string kind, string problem) {
        var shown = name is null ? "null" : "'" + Quote(name) + "'";

        return shown
            + " is not a valid "
            + kind
            + ": "
            + problem
            + ". A "
            + kind
            + " is "
            + Int(MinLength)
            + "-"
            + Int(MaxLength)
            + " characters and must match "
            + Pattern
            + " — lower-case a-z, digits 0-9 and hyphens, starting and ending with a letter or a "
            + "digit. That is the Kubernetes DNS-1123 label rule, and it applies because this name "
            + "becomes a Kubernetes object name and a label value, which caps at "
            + Int(MaxLength)
            + " characters. Cyber Cloud rejects names outside the rule instead of mangling them to "
            + "fit, on purpose: a mangled name is how you end up asking why your resource is called "
            + "'pg-a7f3'. See docs/plan/06 § Naming rules.";
    }

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Renders control characters visibly so the message is safe to put in a log, an HTTP body
    ///     and a terminal. A NUL or a newline in a name is exactly the input this rule exists to
    ///     reject, so it must be quotable.
    /// </summary>
    static string Quote(string name) {
        var needsEscaping = false;
        foreach (var c in name) {
            if (char.IsControl(c)) {
                needsEscaping = true;
                break;
            }
        }

        if (!needsEscaping) {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        foreach (var c in name) {
            if (char.IsControl(c)) {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            } else {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Throws if <paramref name="name" /> is invalid. For constructors of identifier types,
    ///     where an invalid component is a bug rather than a domain outcome (docs/plan/00:171).
    /// </summary>
    /// <exception cref="ArgumentException">The name breaks the rule.</exception>
    internal static string EnsureValid(string? name, string parameterName, string kind) {
        var problem = Describe(name);
        if (problem is not null) {
            throw new ArgumentException(Explain(name, kind, problem), parameterName);
        }

        return name!;
    }
}
