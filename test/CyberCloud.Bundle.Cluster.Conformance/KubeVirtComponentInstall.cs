using CyberCloud.Cluster.Conformance.Infrastructure;
using Shouldly;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     What <c>charts/bundle/install.sh</c> would run for the two phase-30 components and the storage
///     class they need, read without a cluster.
/// </summary>
/// <remarks>
///     ⚠ <b>The daemon-free companion the other installing classes each have</b>, for the reason
///     <see cref="CertManagerComponentInstaller" /> states: a run whose every test skipped reports
///     "Zero tests ran" and fails under <c>--minimum-expected-tests 1</c>. What it asserts that the
///     cluster class cannot see is the ORDER: three components from two phases, named on the command
///     line the wrong way round, and the roster puts storage first and CDI before KubeVirt — the
///     dependency <c>charts/bundle/kubevirt/component.yaml § requires</c> records.
/// </remarks>
public sealed class KubeVirtComponentInstaller {
    /// <summary>
    ///     The dry run selects the three by name, orders them by the roster, and runs each
    ///     <c>manifest:</c> row's <c>waitFor:</c> before the next component.
    /// </summary>
    [Fact]
    public async Task TheDryRunOrdersStorageBeforeCdiBeforeKubeVirtWhateverTheCommandLineSays() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH, so what "
            + "the installer would run could not be read. WOULD PROVE: that install.sh --component "
            + "selects the phase-25 storage row and both phase-30 rows by name, orders them by the "
            + "roster rather than by the command line, and waits for each manifest row's own "
            + "`waitFor:` entries before starting the next."
        );

        var run = await BundleInstaller.RunAsync(
            "--dry-run --component " + BundleInstaller.KubeVirtComponent
            + " --component " + BundleInstaller.CdiComponent
            + " --component " + BundleInstaller.OpenEbsLocalPvComponent,
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(0, "a dry run executes nothing and must succeed on any machine with bash. Its output was:\n" + run.Output);

        var storage = run.Output.IndexOf("\n  " + BundleInstaller.OpenEbsLocalPvComponent + "\n", StringComparison.Ordinal);
        var cdi = run.Output.IndexOf("\n  " + BundleInstaller.CdiComponent + "\n", StringComparison.Ordinal);
        var kubevirt = run.Output.IndexOf("\n  " + BundleInstaller.KubeVirtComponent + "\n", StringComparison.Ordinal);

        storage.ShouldBeGreaterThan(-1, run.Output);
        cdi.ShouldBeGreaterThan(storage, "CDI's DataVolumes need a storage class, and the roster puts phase 25 before phase 30. Output:\n" + run.Output);
        kubevirt.ShouldBeGreaterThan(cdi, "KubeVirt v1.9.0's template controller needs CDI's DataVolume kind at start — kubevirt/component.yaml § requires. Output:\n" + run.Output);

        foreach (var component in new[] { BundleInstaller.CdiComponent, BundleInstaller.KubeVirtComponent }) {
            foreach (var entry in BundleInstaller.WaitFor(component)) {
                run.Output.ShouldContain(
                    "would run: kubectl wait --timeout=10m " + entry.Replace("{", "\\{").Replace("}", "\\}"),
                    Case.Sensitive,
                    $"{component}'s `waitFor:` entry did not become a `kubectl wait` in the dry run. Output:\n" + run.Output
                );
            }
        }

        // The other two phase-40 consumers of these groups are NOT attempted: --component selects rows.
        foreach (var other in new[] { "cluster-api", "cluster-api-provider-kubevirt", "kamaji", "cert-manager" }) {
            run.Output.ShouldNotContain("\n  " + other + "\n", Case.Sensitive, $"`{other}` was attempted and was not asked for. Output:\n" + run.Output);
        }
    }
}

/// <summary>
///     CDI and KubeVirt installed by <c>install.sh</c> onto one k3s, an image imported through
///     <c>charts/managed/image</c>, and a <c>charts/managed/virtual-machine</c> render admitted by
///     KubeVirt's own webhooks and booted — the first guest this platform has ever run.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST TEST IN THIS REPOSITORY TO INSTALL A <c>manifest:</c> COMPONENT, AND THE
///         FIRST TO PUT A PROVIDER'S RENDER IN FRONT OF THE OPERATOR IT WAS WRITTEN FOR.</b>
///         <c>charts/bundle/bundle.yaml § owed</c>, <c>the-manifest-path-waits-for-nothing</c>, ended
///         "no test has yet run a manifest: row, and the lane that could now exists on this
///         machine"; every <c>.Cluster.Conformance</c> suite records that its k3s has no operator and
///         a derived CRD stub admits anything. This class closes both for two rows and one family:
///         <c>install.sh --component</c> installs openebs-localpv, containerized-data-importer and
///         kubevirt in the roster's order onto a fresh k3s, waits for <c>CDI</c> and <c>KubeVirt</c>
///         to report <c>Deployed</c>, and then the real <c>cdi.kubevirt.io</c> and <c>kubevirt.io</c>
///         webhooks are what admit — or refuse — the two charts.
///     </para>
///     <para>
///         ⚠ <b>THE ASSERTION IS <c>Running</c>, AND THIS CLASS WAS WRITTEN TO ASSERT THE OPPOSITE.</b>
///         Issue #95 and <c>charts/bundle/bundle.yaml § owed</c>,
///         <c>virtual-machines-need-a-node-with-kvm</c>, both said Docker Desktop's VM lends no
///         <c>/dev/kvm</c> to a container, so the first version of this test asserted the state a node
///         without KVM allows: <c>ErrorUnschedulable</c> on <i>Insufficient devices.kubevirt.io/kvm</i>.
///         The first run that got past CDI (2026-09-17) turned that red the other way — the machine
///         reported <c>Running</c> 46 seconds after the apply. Nobody had measured the premise: WSL2
///         runs with nested virtualization on, the WSL kernel carries KVM, and the k3s container is
///         privileged, so <c>/dev/kvm</c> is there (<c>docker run --privileged alpine ls -l /dev/kvm</c>
///         answers <c>crw-rw---- 10, 232</c>; an unprivileged container answers "No such file").
///         virt-handler's device plugin advertised the device, the launcher pod got it, and cirros
///         booted under KVM — not emulation, which this bundle never switches on. So the assertion is
///         the measured state, and it is the one the row said the lane could not reach.
///     </para>
///     <para>
///         ⚠ <b>What <c>Running</c> does and does not prove.</b> It proves KubeVirt admitted the render,
///         CDI cloned the image's claim into the root disk on the bundle's class, the scheduler placed
///         the launcher pod with the KVM device it asked for, and libvirt reported the guest running.
///         It does not prove the guest finished booting, that cloud-init ran, or that a disk showed
///         up inside it — nothing here reaches a console or a guest agent, and cirros ships neither
///         cloud-init nor qemu-guest-agent. Those are
///         <c>charts/managed/virtual-machine/conformance.yaml § owed</c>, <c>the-guest-is-not-reached</c>,
///         and the VM lane (#95) keeps kube-ovn, LINSTOR and the node-pool Machines — but no longer
///         KVM as such.
///     </para>
///     <para>
///         ⚠ <b>The image is cirros, not a catalogue row, and the reason is the clock.</b> The
///         platform catalogue is four Ubuntu and Debian container disks of several hundred megabytes
///         each; <c>quay.io/kubevirt/cirros-container-disk-demo</c> is a few tens, and what this
///         class needs from the import is that CDI's registry importer ran against a real claim on
///         the bundle's own class and reported <c>Succeeded</c> — which the same code path does for a
///         catalogue row. It is applied through <c>charts/managed/image</c>'s <c>url</c> source, so
///         the chart's scheme-picks-the-importer branch is what gets exercised. ⚠ And the first run
///         found what no fake could: CDI 1.66 has no <c>StorageProfile</c> entry for
///         <c>openebs.io/local</c> and refused the claim with <i>no accessMode specified</i>, which is
///         why every claim <c>CyberCloud.Compute</c> renders now writes <c>ReadWriteOnce</c> —
///         <c>charts/managed/disk/conformance.yaml § owed</c>, <c>access-mode-is-read-write-once</c>.
///     </para>
///     <para>
///         ⚠ <b>Costs, measured on 2026-09-17 on a fresh k3s</b>: about 5 minutes 30 seconds for
///         <c>install.sh</c> to put the three components on (CDI <c>Deployed</c> at ~1 m 40 s, KubeVirt
///         at ~5 m, most of it image pulls), under a minute for the cirros import, and 46 seconds from
///         the machine's apply to <c>Running</c> — the clone took about 20 seconds of that. The budgets
///         below are generous multiples; the commit that added this class has the exact readings.
///     </para>
/// </remarks>
/// <param name="cluster">The empty k3s.</param>
public sealed class KubeVirtOnAnEmptyCluster(EmptyClusterFixture cluster) : IClassFixture<EmptyClusterFixture> {
    const string KubeVirtGroup = "kubevirt.io";
    const string CdiGroup = "cdi.kubevirt.io";
    const string Probe = "bundle-kubevirt-probe";
    const string ImageName = "cirros";
    const string MachineName = "probe";

    /// <summary>A container disk small enough to import inside a test budget — see the class remarks.</summary>
    const string CirrosDisk = "docker://quay.io/kubevirt/cirros-container-disk-demo:v1.9.0";

    /// <summary>The device the launcher pod asks for and this node, against the record, has.</summary>
    const string KvmDevice = "devices.kubevirt.io/kvm";

    /// <summary>How long the import gets: one <c>cdi-importer</c> image pull and a few tens of megabytes.</summary>
    static readonly TimeSpan ImportBudget = TimeSpan.FromMinutes(6);

    /// <summary>How long the machine gets to boot once it is applied: the clone, the launcher pull, libvirt.</summary>
    static readonly TimeSpan RunningBudget = TimeSpan.FromMinutes(6);

    /// <summary>
    ///     After one <c>install.sh</c> run installs the storage class, CDI and KubeVirt onto one
    ///     cluster, an image imported through <c>charts/managed/image</c> reaches <c>Succeeded</c>, a
    ///     machine rendered from <c>charts/managed/virtual-machine</c> is admitted by KubeVirt's
    ///     webhooks, its root disk is cloned from the image, and the guest runs under KVM.
    /// </summary>
    [Fact]
    public async Task InstallingCdiAndKubeVirtAdmitsAMachineThatBootsUnderKvm() {
        Assert.SkipWhen(
            cluster.Client is null || cluster.KubeconfigPath is null,
            cluster.Skip(
                BundleInstaller.KubeVirtComponent,
                "virtual-machines-need-a-node-with-kvm",
                "that one charts/bundle/install.sh run installs openebs-localpv, containerized-data-importer "
                + "and kubevirt onto one API server, that a charts/managed/image DataVolume imports on the "
                + "bundle's class, and that a charts/managed/virtual-machine render is admitted by "
                + "KubeVirt's webhooks and reaches Running under KVM."
            )
        );

        Assert.SkipUnless(
            BundleInstaller.OnPath("kubectl") && BundleInstaller.OnPath("helm"),
            "SKIPPED — this class renders two charts with `helm template` and applies them with "
            + "`kubectl`, and one of the two is not on PATH. WOULD PROVE: that KubeVirt's and CDI's real "
            + "webhooks admit what CyberCloud.Compute renders, and that the guest boots."
        );

        var client = cluster.Client!;
        var token = TestContext.Current.CancellationToken;

        // ── The cluster is empty of both groups, and each clause is load-bearing ───────────────
        (await IsServedAsync(client, KubeVirtGroup, "v1", "virtualmachines", token)).ShouldBeFalse(
            "kubevirt.io/v1 was already served before install.sh ran; a fixture that arrives with KubeVirt on it is a fixture defect."
        );
        (await IsServedAsync(client, CdiGroup, "v1beta1", "datavolumes", token)).ShouldBeFalse(
            "cdi.kubevirt.io/v1beta1 was already served before install.sh ran."
        );

        // ── ONE installer run, THREE components, TWO phases, named the wrong way round ─────────
        var started = Stopwatch.StartNew();

        var run = await BundleInstaller.RunAsync(
            "--component " + BundleInstaller.KubeVirtComponent
            + " --component " + BundleInstaller.CdiComponent
            + " --component " + BundleInstaller.OpenEbsLocalPvComponent,
            cluster.KubeconfigPath,
            token,
            BundleInstaller.ManifestBudget
        );

        var installed = started.Elapsed;

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh installing openebs-localpv, CDI and KubeVirt onto one fresh k3s "
            + "failed. This is the first run in this repository that installs a `manifest:` row under "
            + "test, so a failure is as likely to be the kubectl branch, the waitFor, or the node "
            + "(/var/run must be a shared mount for virt-handler) as either pin. Its output was:\n"
            + run.Output
        );

        var storageAt = run.Output.IndexOf("\n  " + BundleInstaller.OpenEbsLocalPvComponent + "\n", StringComparison.Ordinal);
        var cdiAt = run.Output.IndexOf("\n  " + BundleInstaller.CdiComponent + "\n", StringComparison.Ordinal);
        var kubevirtAt = run.Output.IndexOf("\n  " + BundleInstaller.KubeVirtComponent + "\n", StringComparison.Ordinal);

        storageAt.ShouldBeLessThan(cdiAt, "the roster carries the order; the command line named KubeVirt first. Output:\n" + run.Output);
        cdiAt.ShouldBeLessThan(kubevirtAt, "CDI must be Deployed before KubeVirt starts — kubevirt/component.yaml § requires. Output:\n" + run.Output);

        // ── Both operators report Deployed, which is what install.sh waited for ────────────────
        (await IsServedAsync(client, KubeVirtGroup, "v1", "virtualmachines", token)).ShouldBeTrue("kubevirt.io/v1 is not served after install.sh succeeded. Output:\n" + run.Output);
        (await IsServedAsync(client, CdiGroup, "v1beta1", "datavolumes", token)).ShouldBeTrue("cdi.kubevirt.io/v1beta1 is not served after install.sh succeeded. Output:\n" + run.Output);

        PhaseOf(await client.CustomObjects.GetNamespacedCustomObjectAsync(KubeVirtGroup, "v1", "kubevirt", "kubevirts", "kubevirt", cancellationToken: token))
            .ShouldBe("Deployed", "install.sh returned before the KubeVirt resource reported Deployed, so its waitFor: is not the barrier it claims");
        PhaseOf(await client.CustomObjects.GetClusterCustomObjectAsync(CdiGroup, "v1beta1", "cdis", "cdi", cancellationToken: token))
            .ShouldBe("Deployed");

        // ── The node advertises KVM, which is the premise the whole record had backwards ───────
        var nodes = await client.CoreV1.ListNodeAsync(cancellationToken: token);
        var node = nodes.Items.ShouldHaveSingleItem("a Testcontainers k3s is one node");

        node.Status.Allocatable.ShouldContainKey(
            KvmDevice,
            "virt-handler's device plugin advertised no KVM device on this node. Until 2026-09-17 the "
            + "record said Docker Desktop's VM lends no /dev/kvm to a container; it does, to a "
            + "privileged one, because WSL2 runs with nested virtualization. A node without the "
            + "device would leave the machine below at ErrorUnschedulable, and that is a different "
            + "lane from the one this class measured — read charts/bundle/bundle.yaml § owed, "
            + "`virtual-machines-need-a-node-with-kvm`, before moving any assertion here."
        );

        node.Status.Allocatable[KvmDevice].ToInt64().ShouldBeGreaterThan(0);

        // ── An image, imported through the chart onto the bundle's own class ───────────────────
        await client.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = Probe } }, cancellationToken: token);

        var image = await RenderAsync(
            "image",
            ImageName,
            ["--set", "source.kind=url", "--set", "source.url=" + CirrosDisk, "--set", "size=1Gi", "--set", "storageClass=openebs-hostpath"],
            token
        );

        image.ShouldContain("registry:", Case.Sensitive, "a docker:// url did not pick CDI's registry importer. Rendered:\n" + image);
        image.ShouldContain("cdi.kubevirt.io/storage.bind.immediate.requested", Case.Sensitive, "an image without the immediate-bind annotation never imports on a WaitForFirstConsumer class");
        image.ShouldContain("ReadWriteOnce", Case.Sensitive, "CDI has no StorageProfile for openebs.io/local and refuses a claim with no access mode — the first real run's finding");

        await ApplyAsync(image, token);

        var importStarted = Stopwatch.StartNew();

        var imported = await Poll(
            ImportBudget,
            async () => {
                var current = await client.CustomObjects.GetNamespacedCustomObjectAsync(CdiGroup, "v1beta1", Probe, "datavolumes", ImageName, cancellationToken: token);
                return PhaseOf(current) == "Succeeded" ? "imported" : null;
            },
            token
        );

        imported.ShouldNotBeNull(
            $"the image DataVolume did not reach Succeeded within {ImportBudget.TotalMinutes:F0} minutes. "
            + "CDI's definitions are served and the operator is Deployed, so this is the importer — read "
            + $"the DataVolume's conditions and the importer pod in `{Probe}`. Last status:\n"
            + await DescribeAsync(client, CdiGroup, "v1beta1", Probe, "datavolumes", ImageName, token)
        );

        var importTook = importStarted.Elapsed;

        // ── The machine: admitted by KubeVirt's own webhook, cloned by CDI, booted under KVM ───
        var machine = await RenderAsync(
            "virtual-machine",
            MachineName,
            ["--set", "image=" + ImageName, "--set", "osDiskSize=1Gi", "--set", "size=s1.small"],
            token
        );

        // ⚠ THE APPLY IS THE WEBHOOK ASSERTION. A derived CRD stub admits anything; kubevirt.io/v1's
        // validating webhook checks the run strategy, every volume against its disk, the data volume
        // template against its volume, and the memory and cpu shapes — and answers with its own
        // words when one is wrong. Exit 0 here is the schema half every earlier family still owes.
        await ApplyAsync(machine, token);

        var bootStarted = Stopwatch.StartNew();

        var running = await Poll(
            RunningBudget,
            async () => {
                var current = await client.CustomObjects.GetNamespacedCustomObjectAsync(KubeVirtGroup, "v1", Probe, "virtualmachines", MachineName, cancellationToken: token);
                return PrintableStatusOf(current) == "Running" ? "running" : null;
            },
            token
        );

        running.ShouldNotBeNull(
            $"the VirtualMachine did not report Running within {RunningBudget.TotalMinutes:F0} minutes. "
            + "The node advertises KVM (asserted above), so this is the clone, the launcher image pull, "
            + $"or libvirt — read the DataVolume `{VirtualMachineRoot}` and the virt-launcher pod in `{Probe}`. "
            + "Last status:\n"
            + await DescribeAsync(client, KubeVirtGroup, "v1", Probe, "virtualmachines", MachineName, token)
        );

        var bootTook = bootStarted.Elapsed;

        // ── The evidence, off the objects KubeVirt and CDI created rather than off the machine ──
        PhaseOf(await client.CustomObjects.GetNamespacedCustomObjectAsync(CdiGroup, "v1beta1", Probe, "datavolumes", VirtualMachineRoot, cancellationToken: token))
            .ShouldBe("Succeeded", "the root disk KubeVirt created from dataVolumeTemplates was not filled by CDI's clone of the image");

        var instance = JsonSerializer.SerializeToElement(
            await client.CustomObjects.GetNamespacedCustomObjectAsync(KubeVirtGroup, "v1", Probe, "virtualmachineinstances", MachineName, cancellationToken: token)
        );

        instance.GetProperty("status").GetProperty("phase").GetString().ShouldBe("Running");
        instance.GetProperty("status").GetProperty("nodeName").GetString().ShouldBe(node.Metadata.Name);

        var launchers = await client.CoreV1.ListNamespacedPodAsync(Probe, labelSelector: "kubevirt.io=virt-launcher", cancellationToken: token);
        var launcher = launchers.Items.ShouldHaveSingleItem("exactly one launcher pod runs a running instance");

        launcher.Status.Phase.ShouldBe("Running");

        // ⚠ KVM, NOT EMULATION. The compute container asked for the device and got it; a KubeVirt with
        // useEmulation on requests none, and a node without the device would have refused the pod.
        launcher.Spec.Containers.Single(x => x.Name == "compute").Resources.Requests
            .ShouldContainKey(KvmDevice, "the launcher pod did not request the KVM device, so what ran is not what this lane's row is about");

        // ── What a tenant would read through the provider ──────────────────────────────────────
        var readiness = CyberCloud.Providers.Compute.Contracts.VirtualMachines.ReadinessOf(
            JsonSerializer.Serialize(await client.CustomObjects.GetNamespacedCustomObjectAsync(KubeVirtGroup, "v1", Probe, "virtualmachines", MachineName, cancellationToken: token))
        );

        readiness.Kind.ShouldBe(
            CyberCloud.Providers.Compute.Contracts.VirtualMachines.ReadinessKind.Ready,
            "the reconciler would not call this machine converged, and KubeVirt calls it Running: " + readiness.Detail
        );

        TestContext.Current.SendDiagnosticMessage(
            $"install.sh (openebs-localpv + CDI + KubeVirt): {installed.TotalSeconds:F0} s; cirros import: {importTook.TotalSeconds:F0} s; "
            + $"machine applied to Running: {bootTook.TotalSeconds:F0} s"
        );
    }

    /// <summary>The root DataVolume KubeVirt derives from the chart's template — <c>VirtualMachines.RootDataVolumeName</c>'s spelling.</summary>
    const string VirtualMachineRoot = MachineName + "-root";
    /// <summary>Whether a group answers a list — a 404 is unambiguous where discovery is not.</summary>
    static async Task<bool> IsServedAsync(IKubernetes client, string group, string version, string plural, CancellationToken token) {
        try {
            await client.CustomObjects.ListClusterCustomObjectAsync(group, version, plural, cancellationToken: token);
            return true;
        } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
            return false;
        }
    }

    static string PhaseOf(object resource) =>
        JsonSerializer.SerializeToElement(resource) is var element
        && element.TryGetProperty("status", out var status)
        && status.TryGetProperty("phase", out var phase)
            ? phase.GetString() ?? string.Empty
            : string.Empty;

    static string PrintableStatusOf(object resource) =>
        JsonSerializer.SerializeToElement(resource) is var element
        && element.TryGetProperty("status", out var status)
        && status.TryGetProperty("printableStatus", out var printable)
            ? printable.GetString() ?? string.Empty
            : string.Empty;

    static async Task<string> DescribeAsync(IKubernetes client, string group, string version, string ns, string plural, string name, CancellationToken token) {
        try {
            var current = await client.CustomObjects.GetNamespacedCustomObjectAsync(group, version, ns, plural, name, cancellationToken: token);
            return JsonSerializer.SerializeToElement(current).TryGetProperty("status", out var status) ? status.GetRawText() : "(no status)";
        } catch (HttpOperationException ex) {
            return "(unreadable: " + ex.Message + ")";
        }
    }

    /// <summary><c>helm template</c> over a <c>charts/managed/</c> chart into the probe namespace.</summary>
    /// <remarks>
    ///     ⚠ The chart in the tree, rendered by helm, rather than an object written here — the same
    ///     rule <see cref="CloudNativePgOnAnEmptyCluster" /> gives: what makes this the sentence the
    ///     bundle exists for is that the object comes out of <c>charts/managed/</c>, so a chart that
    ///     stopped rendering a field the operator needs is red here.
    /// </remarks>
    static async Task<string> RenderAsync(string chart, string release, string[] values, CancellationToken token) {
        var arguments = new List<string> { "template", release, Path.Combine(BundleInstaller.RepositoryRoot, "charts", "managed", chart), "--namespace", Probe };
        arguments.AddRange(values);

        var (exitCode, output) = await CaptureAsync("helm", arguments, input: null, kubeconfig: null, token);

        exitCode.ShouldBe(0, $"`helm template charts/managed/{chart}` failed:\n" + output);

        return output;
    }

    async Task ApplyAsync(string rendered, CancellationToken token) {
        var (exitCode, output) = await CaptureAsync("kubectl", ["apply", "--namespace", Probe, "-f", "-"], rendered, cluster.KubeconfigPath, token);

        exitCode.ShouldBe(
            0,
            "applying the rendered chart failed. The definition is installed and served and the operator is "
            + "Deployed, so a rejection here is the operator's own webhook's opinion of the chart's body — "
            + "the half no derived CRD stub can reach. kubectl said:\n"
            + output
            + "\nThe document was:\n"
            + rendered
        );
    }

    static async Task<(int ExitCode, string Output)> CaptureAsync(string command, IReadOnlyList<string> arguments, string? input, string? kubeconfig, CancellationToken token) {
        var start = new ProcessStartInfo(command) {
            WorkingDirectory = BundleInstaller.RepositoryRoot,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        if (kubeconfig is not null) {
            start.Environment["KUBECONFIG"] = kubeconfig;
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (input is not null) {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);

        return (process.ExitCode, output.ToString());
    }

    static void Append(StringBuilder output, string? line) {
        if (line is null) {
            return;
        }

        lock (output) {
            output.AppendLine(line);
        }
    }

    static async Task<T?> Poll<T>(TimeSpan budget, Func<Task<T?>> read, CancellationToken token)
        where T : class {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline) {
            T? value;

            try {
                value = await read().ConfigureAwait(false);
            } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
                // The controller has not created the object yet — the instance, most often.
                value = null;
            }

            if (value is not null) {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }

        return null;
    }
}
