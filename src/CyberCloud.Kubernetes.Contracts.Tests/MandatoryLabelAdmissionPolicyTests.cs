using CyberCloud.Kubernetes.Contracts;
using Shouldly;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     The admission policy's copy of <see cref="KubeLabels.Mandatory" /> and
///     <see cref="KubeLabels.LifetimeStable" /> is byte-identical to the original, in the original's
///     order, and its bindings select on <see cref="KubeLabels.ManagedBySelector" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The same gate <c>SecretShapedAdmissionPolicyTests</c> is, over the other table in
///             the same file.
///         </b> <c>charts/bundle/cybercloud-admission/policies.yaml</c>'s
///         <c>cybercloud-mandatory-labels</c> policy is ADR-013's "validating admission policy on
///         every managed cluster" — the half of the seven-label rule that holds a writer who is not
///         <c>KubeCommandBuilder</c>. Its CEL names the seven keys as string literals, because CEL
///         cannot see this assembly. An eighth label added to <see cref="KubeLabels.Mandatory" />
///         without a matching edit here would be injected by every builder and required by no
///         cluster; a key renamed on one side only would refuse every object the platform writes.
///         Both are silent without this test.
///     </para>
///     <para>
///         ⚠ <b>Read line by line, in a shape as narrow as the other gate's.</b> Each list is a CEL
///         list literal in a block scalar under <c>- name: mandatory</c> or
///         <c>- name: lifetimeStable</c>, one single-quoted key per line, and the bindings' selector
///         is one <c>matchLabels:</c> line each.
///     </para>
/// </remarks>
public class MandatoryLabelAdmissionPolicyTests {
    const string PolicyFile = "charts/bundle/cybercloud-admission/policies.yaml";

    [Fact]
    public void TheSevenMandatoryLabelsInThePolicyAreKubeLabelsMandatoryInOrder() {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), PolicyFile));

        lines.ShouldContain("  name: cybercloud-mandatory-labels");

        ListVariable(lines, "mandatory").ShouldBe(
            KubeLabels.Mandatory.ToList(),
            customMessage:
            $"{PolicyFile}'s `mandatory` variable is not KubeLabels.Mandatory, in ADR-013's order. "
            + "The C# is the source; the list to paste is:\n" + Block(KubeLabels.Mandatory)
        );

        ListVariable(lines, "lifetimeStable").ShouldBe(
            KubeLabels.LifetimeStable.ToList(),
            customMessage:
            $"{PolicyFile}'s `lifetimeStable` variable is not KubeLabels.LifetimeStable. The C# is "
            + "the source; the list to paste is:\n" + Block(KubeLabels.LifetimeStable)
        );
    }

    [Fact]
    public void EveryQuotedManagedByKeyAndValueInThePolicyIsTheOneKubeLabelsDeclares() {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), PolicyFile));

        // ⚠ The bindings, not the policies, decide which namespaces are held, and both must select
        // on the selector every informer already filters by — docs/plan/09 § Observing. Two
        // bindings, two identical lines; a third binding would need a third.
        lines.Count(line => line == "        " + KubeLabels.ManagedBy + ": " + KubeLabels.ManagedByValue)
            .ShouldBe(
                2,
                $"{PolicyFile}'s two ValidatingAdmissionPolicyBindings must each carry "
                + $"`matchLabels: {KubeLabels.ManagedBySelector}` and nothing else selects a namespace"
            );

        // The CEL spells the key and the value as literals in several rules. Every spelling is
        // KubeLabels', so a rename on one side cannot leave a rule reading a label nobody writes.
        lines.Where(line => !line.TrimStart().StartsWith('#'))
            .Where(line => line.Contains("managed-by", StringComparison.Ordinal) && line.Contains('\''))
            .ShouldAllBe(
                line => line.Contains("'" + KubeLabels.ManagedBy + "'", StringComparison.Ordinal),
                $"a CEL line in {PolicyFile} quotes a managed-by key that is not KubeLabels.ManagedBy"
            );

        lines.Count(line => line.Contains("== '" + KubeLabels.ManagedByValue + "'", StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(
                2,
                $"{PolicyFile} must compare the label's value to KubeLabels.ManagedByValue in the rule "
                + "over a claim and the rule over an old object"
            );
    }

    /// <summary>The single-quoted entries of the CEL list literal under <c>- name: {name}</c>.</summary>
    static List<string> ListVariable(string[] lines, string name) {
        var start = Array.IndexOf(lines, "    - name: " + name);

        start.ShouldBeGreaterThanOrEqualTo(0, $"{PolicyFile} declares no variable named `{name}`");

        var entries = new List<string>();

        for (var i = start + 3; i < lines.Length && lines[i] != "        ]"; i++) {
            var entry = lines[i].Trim().TrimEnd(',');

            entry.ShouldStartWith("'", customMessage: $"line {i + 1} of {PolicyFile} is not a quoted list entry");
            entry.ShouldEndWith("'", customMessage: $"line {i + 1} of {PolicyFile} is not a quoted list entry");

            entries.Add(entry[1..^1]);
        }

        return entries;
    }

    static string Block(IEnumerable<string> keys) =>
        "        [\n" + string.Join(",\n", keys.Select(key => "          '" + key + "'")) + "\n        ]";

    /// <summary>The repository root — the directory holding <c>CyberCloud.slnx</c>, walked up to.</summary>
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
