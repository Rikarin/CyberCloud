namespace CyberCloud.Core.Resources;

/// <summary>
///     The naming rule for the three ReBAC vocabularies — object <b>types</b>, object <b>ids</b> and
///     <b>relation</b> names (docs/plan/07 § The model).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this lives in <c>CyberCloud.Core</c> rather than in <c>CyberCloud.Authorization</c>.</b>
///         <see cref="GrainKeys" /> is the only type allowed to build a grain key (ADR-002), and
///         three of its shapes — <c>rel/obj/…</c>, <c>rel/sub/…</c>, <c>rel/check/…</c> — carry an
///         object type and an object id. A key parser that could not re-validate its own components
///         would accept a second spelling of a key, which is a second activation of one entity. So
///         the rule has to be reachable from <see cref="GrainKeys" />, which means it has to be here.
///     </para>
///     <para>
///         <b>The rules.</b>
///     </para>
///     <list type="table">
///         <item>
///             <term>Type, relation, permission</term>
///             <description>
///                 <c>[a-z][a-zA-Z0-9]{0,63}</c> — a lower-camel identifier, 1–64 characters. That
///                 is exactly the spelling docs/plan/07 § The model uses (<c>resourceGroup</c>,
///                 <c>assignRole</c>, <c>contributor</c>) and nothing else. No <c>/</c>, no
///                 <c>:</c>, no <c>#</c>, no <c>@</c>, no <c>|</c>, no <c>-</c>, no <c>_</c>: the
///                 first four are the separators of the tuple grammar
///                 (<c>object#relation@subject</c>) and of the grain key, the fifth is
///                 <c>Orleans.Multitenant</c>'s, and the last two are excluded only so that one
///                 concept has one spelling.
///             </description>
///         </item>
///         <item>
///             <term>Id</term>
///             <description>
///                 <see cref="ResourceNaming" />'s rule — <c>[a-z0-9]([-a-z0-9]*[a-z0-9])?</c>,
///                 1–63 characters. Reused rather than restated, and it is the right rule for two
///                 reasons: the 32-digit lower-case <c>N</c> form of a GUID satisfies it (so
///                 docs/plan/07 § The model's "ids are GUIDs" is the normal case), and so does a
///                 named singleton like <c>platform:root</c>, which docs/plan/06 § Platform
///                 administration already requires and which docs/plan/07 does not account for.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The exclusions are load-bearing, not cosmetic.</b> A relation containing <c>@</c>, or
///         a type containing <c>:</c>, would make <c>object#relation@subject</c> ambiguous — and a
///         tuple that parses two ways is a tuple that grants something nobody wrote. An id
///         containing <c>/</c> would change the segment count of <c>rel/obj/{type}/{id}</c> and let
///         one key be re-cut into another shape. Both are asserted over the injection corpus in
///         <c>GrainKeysTests</c>.
///     </para>
/// </remarks>
public static class RelationNaming {
    /// <summary>The longest legal type, relation or permission name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The rule for a type, relation or permission name, as a regular expression.</summary>
    public const string NamePattern = "[a-z][a-zA-Z0-9]{0,63}";

    /// <summary>The rule for an object id — <see cref="ResourceNaming.Pattern" />.</summary>
    public const string IdPattern = ResourceNaming.Pattern;

    /// <summary>
    ///     Whether <paramref name="name" /> is a legal object type, relation or permission name.
    /// </summary>
    /// <param name="name">The candidate.</param>
    public static bool IsName(string? name) {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) {
            return false;
        }

        if (name[0] is not (>= 'a' and <= 'z')) {
            return false;
        }

        foreach (var c in name) {
            if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="id" /> is a legal object id.</summary>
    /// <param name="id">The candidate.</param>
    public static bool IsId(string? id) => ResourceNaming.IsValid(id);

    /// <summary>
    ///     Validates a type, relation or permission name, with a message that states the rule.
    /// </summary>
    /// <param name="name">The candidate.</param>
    /// <param name="kind">What is being named — woven into the message.</param>
    public static Result ValidateName(string? name, string kind) =>
        IsName(name)
            ? Result.Success
            : Result.Failure(
                ErrorCode.InvalidResourceName,
                $"'{Show(name)}' is not a {kind}. The rule is {NamePattern} — a lower-camel "
                + $"identifier of 1-{MaxNameLength} characters, starting with a lower-case letter "
                + "and containing only ASCII letters and digits. ':', '#', '@', '/' and '|' are "
                + "excluded because they are the separators of the tuple grammar and of the grain "
                + "key: a name carrying one makes 'object#relation@subject' parse two ways. "
                + "docs/plan/07 § The model."
            );

    /// <summary>Validates an object id, with a message that states the rule.</summary>
    /// <param name="id">The candidate.</param>
    public static Result ValidateId(string? id) =>
        IsId(id)
            ? Result.Success
            : Result.Failure(
                ErrorCode.InvalidResourceName,
                $"'{Show(id)}' is not an object id. The rule is {IdPattern} — 1-"
                + $"{ResourceNaming.MaxLength} characters of lower-case letters, digits and "
                + "hyphens, starting and ending alphanumeric. The 32-digit lower-case 'N' form of a "
                + "GUID satisfies it, which is the normal case (docs/plan/07 § The model), and so "
                + "does a named singleton such as 'root'."
            );

    static string Show(string? value) => value is null ? "<null>" : value;
}
