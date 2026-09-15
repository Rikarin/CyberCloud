using System.Text.RegularExpressions;

namespace CyberCloud.Core.Security;

/// <summary>
///     One of <see cref="SecretShapedText" />'s rules: a name that reaches the operator, and the
///     shape it recognises — in the .NET spelling the log scrubber runs, and in the RE2 spelling the
///     admission policy runs.
/// </summary>
/// <remarks>
///     <para>
///         The name is the alert's dimension. "A secret reached a log" is not actionable; "an
///         <c>AwsAccessKey</c> reached a log" names the credential to rotate and the integration to
///         go and read. The same name is what a refused <c>kubectl apply</c> reports, for the same
///         reason.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="Re2Pattern" /> exists because docs/plan/18 § Platform security's Secrets
///             row has a third control, and it runs in a language that cannot see this one.
///         </b> <c>charts/bundle/cybercloud-admission/policies.yaml</c> is a
///         <c>ValidatingAdmissionPolicy</c> whose CEL carries this rule set a second time, as RE2
///         patterns Kubernetes evaluates with Go's engine. A second copy of a rule set in a language
///         that cannot see the first is the shape of defect this repository has shipped before — a
///         permission named by one assembly and defined by neither — so this property is the one the
///         copy is derived from and the one
///         <c>SecretShapedAdmissionPolicyTests.EveryRuleInThePolicyIsByteIdenticalToItsRe2Form</c>
///         holds the file to, byte for byte and in this order. Edit a pattern here and that test
///         prints the block to paste.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The RE2 form is the .NET form with exactly two rewrites, and both are chosen so the
///             two engines answer "does it match" identically over the same text.
///         </b> The named
///         <c>(?&lt;secret&gt;…)</c> group becomes a plain <c>(?:…)</c> group: the scrubber uses the
///         group to redact part of a match and keep the rest, whereas admission refuses the whole
///         object, so where the secret sits inside the match is information the policy has no use
///         for. And <see cref="IgnoreCase" /> becomes a leading <c>(?i)</c>, which is RE2's spelling
///         of <see cref="RegexOptions.IgnoreCase" />. Nothing else differs, and nothing else may:
///         every pattern is already restricted to the syntax
///         <see cref="RegexOptions.NonBacktracking" /> accepts — no lookaround, no backreference, no
///         atomic group — which is the same subset RE2 accepts, so a pattern this type constructs is
///         a pattern the API server compiles.
///         <c>SecretShapedTextTests.TheRe2FormOfEveryRuleMatchesWhatTheDotNetFormMatches</c> runs
///         the fixture set through both spellings.
///     </para>
/// </remarks>
public sealed class SecretShapedRule {
    /// <summary>The name of the group a rule may use to redact part of a match instead of all of it.</summary>
    internal const string SecretGroup = "secret";

    internal SecretShapedRule(string name, string pattern, bool ignoreCase) {
        Name = name;
        Pattern = pattern;
        IgnoreCase = ignoreCase;
        Regex = new(
            pattern,
            RegexOptions.NonBacktracking | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
        );
        Re2Pattern = (ignoreCase ? "(?i)" : string.Empty)
            + pattern.Replace("(?<" + SecretGroup + ">", "(?:", StringComparison.Ordinal);
    }

    /// <summary>The rule's name — the alert dimension, and the word in a refusal message.</summary>
    public string Name { get; }

    /// <summary>The shape, as the .NET engine reads it.</summary>
    public string Pattern { get; }

    /// <summary>Whether the shape is matched without regard to case.</summary>
    public bool IgnoreCase { get; }

    /// <summary>
    ///     The shape as Go's RE2 reads it — what the admission policy's CEL <c>matches()</c> carries.
    /// </summary>
    public string Re2Pattern { get; }

    /// <summary>The compiled .NET form, which is what <see cref="SecretShapedText.TryRedact" /> runs.</summary>
    internal Regex Regex { get; }
}
