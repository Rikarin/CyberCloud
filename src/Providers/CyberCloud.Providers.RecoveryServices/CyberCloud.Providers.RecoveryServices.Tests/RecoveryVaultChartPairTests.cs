using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.Tests;

/// <summary>
///     The half generation does not reach: the Helm template renders the same ScheduledBackup the
///     reconciler does, by hand, and three literals in it can drift from the C#.
/// </summary>
/// <remarks>
///     ⚠ Nothing in <c>./build.sh</c> compares a Helm template to a registry —
///     <c>ChartRegistryPairTests</c> in the PostgreSQL family records the defect that made that worth
///     saying: both renderings wrote a field the operator refuses, for a month, because neither had a
///     reader. The template is embedded rather than read off disk so the test runs from any working
///     directory.
/// </remarks>
public sealed class RecoveryVaultChartPairTests {
    [Fact]
    public void TheTemplateSpellsTheSecondsFieldTheOwnerReferenceAndTheMethodAsTheRendererDoes() {
        var template = Template();

        using var desired = JsonDocument.Parse(RecoveryVaults.Body(Guid.NewGuid(), [], schedule: "0 2 * * *"));
        var spec = JsonNode.Parse(RecoveryVaults.ScheduledBackupJson("v", "i", "c", desired.RootElement))!["spec"]!.AsObject();

        // The seconds field: the template prepends "0 " to the five the tenant wrote, and so does the C#.
        template.ShouldContain("""schedule: {{ printf "0 %s" .Values.policy.schedule | quote }}""");
        spec["schedule"]!.GetValue<string>().ShouldStartWith("0 ");

        template.ShouldContain("backupOwnerReference: " + spec["backupOwnerReference"]!.GetValue<string>());
        template.ShouldContain("method: " + spec["method"]!.GetValue<string>());
        template.ShouldContain("immediate: " + (spec["immediate"]!.GetValue<bool>() ? "true" : "false"));

        template.ShouldContain("kind: " + RecoveryVaults.ScheduledBackupKind.Kind);
        template.ShouldContain("apiVersion: " + RecoveryVaults.ScheduledBackupKind.ApiVersion);
    }

    [Fact]
    public void TheTemplateNeverDerivesTheClusterNameFromTheItem() {
        // ⚠ The one thing the template must NOT do: guess the Cluster's name. It is projected in as
        // platform.clusterName, which the reconciler fills from the view.
        var template = Template();

        template.ShouldContain("recovery-vault.clusterName");
        template.ShouldNotContain(".Values.platform.protectedItem }}", customMessage: "the item's name is a label, never the cluster's name");
    }

    static string Template() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("recovery-vault.scheduledbackup.yaml")
            ?? throw new InvalidOperationException("the chart template is not embedded — see the .csproj");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
