using System.Text;
using System.Text.RegularExpressions;
using CyberCloud.Core.Security;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     The admission policy's copy of <see cref="SecretShapedText.Rules" /> is byte-identical to the
///     original, in the original's order.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This is the gate issue #15 names as part of the cost, and it is here because the
///             failure it prevents has shipped before.
///         </b> <c>charts/bundle/cybercloud-admission/policies.yaml</c> is a
///         <c>ValidatingAdmissionPolicy</c> whose eleven validations carry the eleven shapes of
///         <see cref="SecretShapedText" /> as RE2 — a second copy of a rule set in a language that
///         cannot see the first. Left ungated, the two drift the first time somebody tightens a
///         pattern in C# and does not open the YAML, and the drift is silent: the scrubber redacts a
///         shape the API server admits, or the reverse, and every test on either side stays green.
///     </para>
///     <para>
///         ⚠ <b>The YAML is read line by line, deliberately, and the shape it must have is narrow.</b>
///         One <c>expression:</c> block scalar per rule holding exactly
///         <c>!variables.strings.exists(s, s.matches('…'))</c>, followed by a <c>message:</c> naming
///         the rule in parentheses, in <see cref="SecretShapedText.Rules" />' order. A YAML library
///         would accept a file <c>install.sh</c>'s reader cannot, and — more to the point — the
///         narrow reader is what lets a failure print the whole block to paste, which is the only
///         way a gate like this stays cheap to obey.
///     </para>
/// </remarks>
public class SecretShapedAdmissionPolicyTests {
    /// <summary>The policy, relative to the repository root.</summary>
    const string PolicyFile = "charts/bundle/cybercloud-admission/policies.yaml";

    /// <summary>The name the policy must carry, so the lines below are read from the right document.</summary>
    const string PolicyName = "cybercloud-secret-shaped-text";

    static readonly Regex ExpressionLine = new(
        @"^        !variables\.strings\.exists\(s, s\.matches\('(?<pattern>.*)'\)\)$",
        RegexOptions.NonBacktracking
    );

    static readonly Regex MessageLine = new(
        @"^      message: ""a value in this object is credential-shaped \((?<rule>[A-Za-z]+)\)\.",
        RegexOptions.NonBacktracking
    );

    [Fact]
    public void EveryRuleInThePolicyIsByteIdenticalToItsRe2Form() {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), PolicyFile));

        lines.ShouldContain(
            "  name: " + PolicyName,
            $"{PolicyFile} no longer declares a policy named {PolicyName}, so the validations this "
            + "test reads would be somebody else's. Renaming the policy means renaming it here"
        );

        // Every (expression, message) pair, in file order. The message is the line after its
        // expression's block scalar, so a pattern with no rule name beside it — or a name with no
        // pattern — is a shape violation rather than a silent skip.
        var actual = new List<(string Rule, string Literal)>();

        for (var i = 0; i < lines.Length; i++) {
            var expression = ExpressionLine.Match(lines[i]);

            if (!expression.Success) {
                continue;
            }

            var message = i + 1 < lines.Length ? MessageLine.Match(lines[i + 1]) : Match.Empty;

            message.Success.ShouldBeTrue(
                $"{PolicyFile} line {i + 1} is a rule expression and line {i + 2} is not its "
                + "message. The message is what names the rule to whoever was refused, so the two "
                + "travel together:\n" + lines[i] + "\n" + (i + 1 < lines.Length ? lines[i + 1] : "(end)")
            );

            actual.Add((message.Groups["rule"].Value, expression.Groups["pattern"].Value));
        }

        var expected = SecretShapedText.Rules
            .Select(rule => (rule.Name, Literal: CelLiteral(rule.Re2Pattern)))
            .ToList();

        // ⚠ One assertion over the whole list rather than one per rule, so that an insertion, a
        // deletion or a reorder reads as what it is instead of as eleven cascading mismatches — and
        // so that the failure carries the entire block to paste.
        actual.ShouldBe(
            expected,
            $"{PolicyFile}'s validations do not match SecretShapedText.Rules, byte for byte and in "
            + "order. The C# is the source; paste this block over the policy's `validations:`:\n\n"
            + ExpectedBlock()
        );

        actual.Count.ShouldBe(
            SecretShapedText.Rules.Count,
            "the rule count moved without this test noticing, which should be impossible after the "
            + "assertion above"
        );
    }

    /// <summary>The literal spelling of a pattern inside a single-quoted CEL string.</summary>
    /// <param name="pattern">An RE2 pattern.</param>
    /// <remarks>
    ///     Two escapes and no others: a backslash is <c>\\</c> and a single quote is <c>\'</c>. The
    ///     expression sits in a YAML block scalar, which performs no escaping of its own, so what
    ///     this returns is what the file holds.
    /// </remarks>
    static string CelLiteral(string pattern) =>
        pattern.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    /// <summary>The <c>validations:</c> block the policy must carry, rendered from the rules.</summary>
    static string ExpectedBlock() {
        var block = new StringBuilder();

        foreach (var rule in SecretShapedText.Rules) {
            block.Append("    - expression: |-\n");
            block.Append("        !variables.strings.exists(s, s.matches('")
                .Append(CelLiteral(rule.Re2Pattern))
                .Append("'))\n");
            block.Append("      message: \"a value in this object is credential-shaped (")
                .Append(rule.Name)
                .Append("). Cyber Cloud refuses a secret in a ConfigMap, an env var, an arg or a ")
                .Append("command — docs/plan/18 § Platform security, row Secrets. Put it in a Secret ")
                .Append("and reference it.\"\n");
            block.Append("      reason: Invalid\n");
        }

        return block.ToString();
    }

    /// <summary>The repository root — the directory holding <c>CyberCloud.slnx</c>.</summary>
    /// <remarks>
    ///     Walked upward rather than counted in <c>..</c> segments, which is what every other
    ///     file-reading test in this repository does: the distance from a test assembly to the root
    ///     is a property of the artifacts layout, and it changes without anybody deciding to.
    /// </remarks>
    static string RepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "No CyberCloud.slnx above " + AppContext.BaseDirectory + ", so " + PolicyFile
                + " cannot be found."
            );
    }
}
