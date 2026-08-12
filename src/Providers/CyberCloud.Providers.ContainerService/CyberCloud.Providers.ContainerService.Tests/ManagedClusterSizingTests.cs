using System.Reflection;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     The facts that live in both charts' <c>_helpers.tpl</c> and in C#, and have to agree.
/// </summary>
/// <remarks>
///     ⚠ <b>THE HALF ADR-012's GENERATION DOES NOT REACH.</b> Each chart's <c>@param</c> block is
///     generated from a <c>ResourceSchema</c> and byte-diffed by <c>./build.sh Charts</c>; a Helm
///     <i>template</i> is not a schema and nothing generates it — <c>ChartSurfaces</c> filters
///     <c>templates/</c> out of the chart tree on purpose. So each fact below is two hand-maintained
///     copies of one thing, and this file is what stops them drifting.
/// </remarks>
public sealed class ManagedClusterSizingTests {
    [Fact]
    public void TheChartAndTheReconcilerAgreeOnWhatAControlPlaneContainerCosts() {
        // ⚠ THE FACT THE QUOTA METERS DEPEND ON. ContainerServiceProvider reserves against the C# copy;
        // if the chart's figure drifted upward the management cluster would run pods a tenant is not
        // charged for, and the control-plane population is one of this type's only two meters.
        var helpers = Embedded("kubernetes.helpers.tpl");

        var block = Regex.Match(
            helpers,
            "define \"kubernetes\\.controlPlaneResources\" -}}(.*?){{- end",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue("the chart declares no `kubernetes.controlPlaneResources` helper");

        // The helper reads the values, so the numbers live in values.yaml — which is where the
        // @internal rows put them. Both are checked below.
        block.Groups[1].Value.ShouldContain("controlPlaneCpu");
        block.Groups[1].Value.ShouldContain("controlPlaneMemory");

        var values = File.ReadAllText(ChartFile("kubernetes", "values.yaml"));

        values.ShouldContain("controlPlaneCpu: " + ManagedClusters.ControlPlaneCpu);
        values.ShouldContain("controlPlaneMemory: " + ManagedClusters.ControlPlaneMemory);
    }

    [Fact]
    public void BothChartsAndTheReconcilerPinTheSamePatchForEveryOfferedMinor() {
        // ⚠ THREE COPIES OF ONE TABLE, AND A DRIFT BETWEEN ANY TWO IS A VERSION SKEW NOBODY DECLARED.
        // The control plane's patch and the nodes' patch come from the same map in C#; each chart has
        // its own `dict` because a Helm template cannot read another chart's.
        foreach (var chart in new[] { "kubernetes.helpers.tpl", "kubernetes-agentpool.helpers.tpl" }) {
            var helpers = Embedded(chart);

            foreach (var (minor, pinned) in ManagedClusters.PinnedPatch) {
                helpers.ShouldContain(
                    "\"" + minor + "\" \"" + pinned + "\"",
                    Case.Sensitive,
                    chart + " does not pin " + minor + " to " + pinned
                );
            }

            // ⚠ And the other direction: a minor the chart pins and the registry does not offer is a
            // value a tenant can write into values.yaml and never into a resource body.
            foreach (Match row in Regex.Matches(
                         helpers,
                         "\"(\\d+\\.\\d+)\" \"v\\d+\\.\\d+\\.\\d+\"",
                         RegexOptions.None,
                         TimeSpan.FromSeconds(5)
                     )) {
                ManagedClusters.PinnedPatch.ShouldContainKey(row.Groups[1].Value);
            }
        }
    }

    [Fact]
    public void TheChartAndTheReconcilerDeriveTheSameControlPlaneName() {
        // ⚠ ASSERTED AGAINST A LITERAL on both sides. Reading the suffix out of ControlPlaneName and
        // then looking for it in the chart would find whatever it was changed to. Nothing either of
        // them applies creates the joined name — Cluster API resolves the ref by it — so a prefix that
        // drifted would produce two renderings of one resource that disagree about where its control
        // plane lives, and both would apply, read back and converge.
        Embedded("kubernetes.helpers.tpl").ShouldContain(
            "printf \"%s-control-plane\"",
            Case.Sensitive,
            "the chart derives the control-plane name some other way than the `-control-plane` suffix"
        );

        ManagedClusters.ControlPlaneName("prod").ShouldBe("prod-control-plane");
        ManagedClusters.KubeconfigSecretName("prod").ShouldBe("prod-kubeconfig");

        Embedded("kubernetes.helpers.tpl").ShouldContain("printf \"%s-kubeconfig\"", Case.Sensitive);
    }

    [Fact]
    public void TheChartAndTheReconcilerAgreeOnTheDataStoreAndTheServiceDomain() {
        var values = File.ReadAllText(ChartFile("kubernetes", "values.yaml"));

        values.ShouldContain("dataStoreName: " + ManagedClusters.DataStoreName);
        values.ShouldContain("serviceDomain: " + ManagedClusters.ServiceDomain);
    }

    [Fact]
    public void TheChartAndTheReconcilerAgreeOnTheNodeImageAndTheSelectorLabel() {
        Embedded("kubernetes-agentpool.helpers.tpl")
            .ShouldContain(AgentPools.PoolLabel + ":", Case.Sensitive);

        File.ReadAllText(ChartFile("kubernetes-agentpool", "values.yaml"))
            .ShouldContain("nodeImageRepository: " + AgentPools.NodeImageRepository);
    }

    [Fact]
    public void BothChartsFoldEverySlashInTheResourceTypeLabel() {
        // ⚠ Helm's `replace` is already a replace-all; asserting it is the point, because the child's
        // type path has TWO slashes and a single-replacement spelling would be refused at admission
        // per object rather than at lint time.
        foreach (var chart in new[] { "kubernetes.helpers.tpl", "kubernetes-agentpool.helpers.tpl" }) {
            Embedded(chart).ShouldContain(
                "replace \"/\" \"_\" | lower",
                Case.Sensitive,
                chart + " does not fold the resource-type label value"
            );
        }
    }

    static string ChartFile(string chart, string name) {
        // ⚠ Walks up from the test assembly rather than embedding values.yaml, because that file is
        // GENERATED by ./build.sh Charts and an embedded copy would be a stale second one.
        //
        // ⚠ THE MARKER IS `CyberCloud.slnx` AND NOT `charts/`, WHICH IS THE MISTAKE THIS COMMENT
        // EXISTS TO STOP SOMEBODY REPEATING. `./build.sh Charts` writes packaged charts into
        // `artifacts/charts/`, so a walk that stopped at the first directory containing a `charts`
        // child stops inside `artifacts/` — with a real directory, a plausible path and a
        // DirectoryNotFoundException naming a file that does exist one level up.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("no `CyberCloud.slnx` above " + AppContext.BaseDirectory);

        return Path.Combine(directory.FullName, "charts", "managed", chart, name);
    }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. It is declared in "
                + "CyberCloud.Providers.ContainerService.Tests.csproj with a LogicalName."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
