using CyberCloud.Bundle.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Compute.Contracts;
using Shouldly;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace CyberCloud.Bundle.Cluster.Nightly;

/// <summary>
///     What <c>charts/bundle/install.sh</c> would run for the two phase-30 components and the storage
///     class they need, read without a cluster.
/// </summary>
/// <remarks>
///     ⚠ <b>The daemon-free companion the other installing classes each have</b>, for the reason
///     <see cref="CertManagerComponentInstaller" /> states: a run whose every test skipped reports
///     "Zero tests ran" and fails under <c>--minimum-expected-tests 1</c>. It moved to this assembly
///     with the class it accompanies, because the trap is per assembly: the sibling keeps its own
///     three companions. What it asserts that the cluster class cannot see is the ORDER: three
///     components from two phases, named on the command line the wrong way round, and the roster
///     puts storage first and CDI before KubeVirt — the dependency
///     <c>charts/bundle/kubevirt/component.yaml § requires</c> records.
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
///     <c>charts/managed/image</c>, a disk provisioned through <c>charts/managed/disk</c>, and a
///     <c>charts/managed/virtual-machine</c> render that attaches the disk, is admitted by KubeVirt's
///     own webhooks and boots — the first guest this platform has ever run, with the family's three
///     charts all in front of the operators they were written for.
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
///         webhooks are what admit — or refuse — the three charts.
///     </para>
///     <para>
///         ⚠ <b>NIGHTLY, NOT PER-PR, AND THE NUMBER THAT DECIDED IT WAS READ OFF THE RUNNER.</b> This
///         class landed in <c>test/CyberCloud.Bundle.Cluster.Conformance</c> and the review of #28
///         asked what it costs there. gate.yml's <c>test</c> job runs every cluster-backed suite one
///         at a time, and on 2026-09-15 that chain took master 26 m 16 s on the runner — past the
///         25-minute budget pr.yml enforces, four minutes short of the job's 30-minute timeout — with
///         this assembly's sibling at 3 m 49 s of it. Eight minutes more would have timed the job
///         out on every PR. So the class moved to the <c>.Nightly</c> suffix, which
///         <c>build/Build.Test.cs § SuiteOwning</c> routes to <c>TestNightly</c> and nightly.yml's
///         <c>slow-suites</c> job runs; the sibling's three classes stay per-PR at under five minutes.
///     </para>
///     <para>
///         ⚠ <b>THE DISK CHART IS MEASURED HERE TOO, AND IT WAS NOT UNTIL THE REVIEW ASKED.</b> The
///         first version of this class applied the image and the machine and said the family's
///         webhook half was measured; <c>charts/managed/disk</c> had never met a real CDI, no data
///         disk had ever been attached, and the disk manifest's <c>access-mode-is-read-write-once</c>
///         row was written from the image's failure alone. Now a blank <c>DataVolume</c> is applied
///         from the disk chart — no immediate-bind annotation, on the bundle's WaitForFirstConsumer
///         class — and two things are read off the real operator: the phase CDI leaves a disk nobody
///         has attached in, which is what <c>Cdi.IsProvisioned</c> calls provisioned and the disk
///         reconciler calls converged, and then, once the machine that names it in <c>dataDisks</c>
///         is Running, that the same <c>DataVolume</c> reached <c>Succeeded</c>, that KubeVirt lists
///         the volume on the instance, and that the launcher pod mounts the claim. Read twice on
///         2026-09-18: the blank disk sits at <c>WaitForFirstConsumer</c> — not
///         <c>PendingPopulation</c>, so CDI 1.66 takes the classic path and not the populator's for
///         a blank source on this class — and once the machine is applied it goes <c>PVCBound</c>,
///         <c>ImportScheduled</c>, <c>Succeeded</c> in about ten seconds, before the root clone
///         finishes. What is still not proven is inside the guest — see below.
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
///         It does not prove the guest finished booting, that cloud-init ran, or that the attached
///         disk showed up inside it as a block device — nothing here reaches a console or a guest
///         agent, and cirros ships neither cloud-init nor qemu-guest-agent. Those are
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
///         With the disk added (2026-09-18, on a host thirteen agents were sharing): the disk is
///         provisioned within seconds of its apply, and the machine goes from applied to
///         <c>Running</c> in about 40 seconds with the disk populating inside that; the class stays
///         inside eight minutes of its own work, and <c>./build.sh TestNightly</c> reported 9 m 40 s
///         for the target with the wait for <c>ClusterSlot</c> behind another suite included.
///     </para>
/// </remarks>
/// <param name="cluster">The empty k3s.</param>
public sealed class KubeVirtOnAnEmptyCluster(EmptyClusterFixture cluster) : IClassFixture<EmptyClusterFixture> {
    const string KubeVirtGroup = "kubevirt.io";
    const string CdiGroup = "cdi.kubevirt.io";
    const string Probe = "bundle-kubevirt-probe";
    const string ImageName = "cirros";
    const string DiskName = "data";
    const string MachineName = "probe";

    /// <summary>The size of the image's claim, the disk, and the machine's root clone, all 1Gi: cirros is a few tens of megabytes.</summary>
    const string OneGibibyte = "1Gi";

    /// <summary>The bundle's own class, named explicitly for the reason <c>OpenEbsLocalPvOnAnEmptyCluster</c> gives: k3s ships a default of its own.</summary>
    const string StorageClass = "openebs-hostpath";

    /// <summary>The size the machine is rendered at: one core, which is what a one-node k3s has to spare.</summary>
    const string MachineSize = "s1.small";

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
    ///     blank disk from <c>charts/managed/disk</c> is provisioned and waits for a consumer, a
    ///     machine rendered from <c>charts/managed/virtual-machine</c> naming that disk is admitted
    ///     by KubeVirt's webhooks, its root disk is cloned from the image, the guest runs under KVM,
    ///     and the disk is populated and mounted by the machine that consumed it.
    /// </summary>
    [Fact]
    public async Task InstallingCdiAndKubeVirtAdmitsAMachineThatBootsUnderKvmWithADiskAttached() {
        Assert.SkipWhen(
            cluster.Client is null || cluster.KubeconfigPath is null,
            cluster.Skip(
                BundleInstaller.KubeVirtComponent,
                "virtual-machines-need-a-node-with-kvm",
                "that one charts/bundle/install.sh run installs openebs-localpv, containerized-data-importer "
                + "and kubevirt onto one API server, that a charts/managed/image DataVolume imports on the "
                + "bundle's class, that a charts/managed/disk DataVolume is provisioned on it and populated "
                + "once a machine consumes it, and that a charts/managed/virtual-machine render attaching the "
                + "disk is admitted by KubeVirt's webhooks and reaches Running under KVM."
            )
        );

        Assert.SkipUnless(
            BundleInstaller.OnPath("kubectl") && BundleInstaller.OnPath("helm"),
            "SKIPPED — this class renders three charts with `helm template` and applies them with "
            + "`kubectl`, and one of the two is not on PATH. WOULD PROVE: that KubeVirt's and CDI's real "
            + "webhooks admit what CyberCloud.Compute renders, and that the guest boots with its disk."
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

        // ⚠ READ AND REPORTED, NOT ASSERTED. charts/managed/virtual-machine/conformance.yaml § owed,
        // `scale-sets-are-not-landed`, names KubeVirt's VirtualMachinePool as the shape a scale set
        // would render, and kubevirt/component.yaml claims `serves: kubevirt.io/v1` alone — the
        // operator installs the other definitions at runtime, so whether this pin serves the pool
        // group is a fact only a cluster can answer. The diagnostic line at the bottom carries the
        // answer for the row to quote; nothing here depends on it.
        var poolsServed = await IsServedAsync(client, "pool.kubevirt.io", "v1alpha1", "virtualmachinepools", token);

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
            ["--set", "source.kind=url", "--set", "source.url=" + CirrosDisk, "--set", "size=" + OneGibibyte, "--set", "storageClass=" + StorageClass],
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

        // ── A disk nobody has attached: provisioned, and waiting for its first consumer ─────────
        //
        // ⚠ THE DISK CHART'S FIRST MEETING WITH A REAL CDI. No immediate-bind annotation, on a
        // WaitForFirstConsumer class, so what CDI does with a claim nobody consumes is the thing under
        // test: the disk reconciler converges on WaitForFirstConsumer or PendingPopulation
        // (Cdi.IsProvisioned) and calls that inventory, and until this ran that was a reading of
        // CDI's source rather than of CDI. Which of the two phases 1.66 reports for a blank source on
        // this class is recorded in the diagnostic line at the bottom, not asserted: KubeVirt's own
        // controller treats the two as one, and so does the platform.
        var disk = await RenderAsync(
            "disk",
            DiskName,
            ["--set", "size=" + OneGibibyte, "--set", "storageClass=" + StorageClass],
            token
        );

        disk.ShouldContain("blank: {}", Case.Sensitive, "a managed disk is a blank DataVolume. Rendered:\n" + disk);
        disk.ShouldNotContain("cdi.kubevirt.io/storage.bind.immediate.requested", Case.Sensitive, "a disk must bind to its first consumer's node, not to CDI's helper pod's — the chart's own template says why");
        disk.ShouldContain("ReadWriteOnce", Case.Sensitive, "CDI has no StorageProfile for openebs.io/local; the disk chart writes the access mode for the same reason the image chart does");

        await ApplyAsync(disk, token);

        var provisioned = await Poll(
            ImportBudget,
            async () => {
                var current = await client.CustomObjects.GetNamespacedCustomObjectAsync(CdiGroup, "v1beta1", Probe, "datavolumes", DiskName, cancellationToken: token);
                var phase = PhaseOf(current);
                return Cdi.IsProvisioned(phase) ? phase : null;
            },
            token
        );

        provisioned.ShouldNotBeNull(
            $"the disk DataVolume did not reach a provisioned phase (Succeeded, WaitForFirstConsumer or PendingPopulation) within {ImportBudget.TotalMinutes:F0} minutes. "
            + "The image imported on the same class moments ago, so this is what CDI does with a BLANK source and no "
            + "immediate-bind annotation — the branch the disk reconciler was written from CDI's source for. Last status:\n"
            + await DescribeAsync(client, CdiGroup, "v1beta1", Probe, "datavolumes", DiskName, token)
        );

        provisioned.ShouldNotBe(
            Cdi.Succeeded,
            "a blank disk on a WaitForFirstConsumer class was populated before anything consumed it, so either the class binds "
            + "immediately — which OpenEbsLocalPvOnAnEmptyCluster asserts it does not — or the chart grew the annotation it must not carry"
        );

        // ── The machine: admitted by KubeVirt's own webhook, cloned by CDI, booted under KVM ───
        var machine = await RenderAsync(
            "virtual-machine",
            MachineName,
            ["--set", "image=" + ImageName, "--set", "osDiskSize=" + OneGibibyte, "--set", "size=" + MachineSize, "--set", "dataDisks={" + DiskName + "}"],
            token
        );

        machine.ShouldContain("claimName: \"" + DiskName + "\"", Case.Sensitive, "the disk was not rendered as a claim by its resource name. Rendered:\n" + machine);

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

        // ── The disk: consumed by the machine, populated by CDI, mounted by the launcher ────────
        //
        // ⚠ THE SECOND HALF OF THE DISK MEASUREMENT. A claim that waited for its first consumer has
        // one now: KubeVirt scheduled the launcher, the claim bound to its node, CDI's blank
        // population ran, and the pod mounts the volume. Three objects say so, none of them written
        // by this test.
        PhaseOf(await client.CustomObjects.GetNamespacedCustomObjectAsync(CdiGroup, "v1beta1", Probe, "datavolumes", DiskName, cancellationToken: token))
            .ShouldBe(
                Cdi.Succeeded,
                $"the disk sat at {provisioned} before the machine consumed it and was not populated once it did. The machine is "
                + "Running, so KubeVirt started the guest without waiting for the disk — read the DataVolume's conditions:\n"
                + await DescribeAsync(client, CdiGroup, "v1beta1", Probe, "datavolumes", DiskName, token)
            );

        instance.GetProperty("status").GetProperty("volumeStatus").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ShouldContain(DiskName, "KubeVirt does not list the disk among the instance's volumes");

        launcher.Spec.Volumes
            .Where(x => x.PersistentVolumeClaim is not null)
            .Select(x => x.PersistentVolumeClaim.ClaimName)
            .ShouldContain(Disks.ObjectNameOf(DiskName), "the launcher pod does not mount the disk's claim by the name Disks.ObjectNameOf renders");

        // ── What a tenant would read through the provider ──────────────────────────────────────
        //
        // ⚠ BOTH READINGS THE RECONCILER TAKES, AGAINST AN OBJECT THE MUTATING WEBHOOK HAS BEEN
        // THROUGH. Matches decides whether a read-back carries the desired body and ReadinessOf
        // decides whether it converged; the two conformance suites see only what a derived stub
        // stored. A body built with VirtualMachines.Body from the same values the chart was rendered
        // with is what holds the chart's shape and the C# reading together — ComputeChartDriftTests
        // compares two dictionaries and nothing else does.
        var admitted = JsonSerializer.Serialize(
            await client.CustomObjects.GetNamespacedCustomObjectAsync(KubeVirtGroup, "v1", Probe, "virtualmachines", MachineName, cancellationToken: token)
        );

        using var desired = JsonDocument.Parse(
            VirtualMachines.Body(Guid.Empty, image: ImageName, size: MachineSize, osDiskSize: OneGibibyte, dataDisks: [DiskName])
        );

        VirtualMachines.Matches(admitted, Probe, desired.RootElement).ShouldBeTrue(
            "the reconciler would report the admitted VirtualMachine as not yet carrying the desired spec, forever: KubeVirt's "
            + "mutating webhook changed a field Matches reads, or the chart and VirtualMachines.VirtualMachineJson disagree. "
            + "The admitted object:\n"
            + admitted
        );

        var readiness = VirtualMachines.ReadinessOf(admitted);

        readiness.Kind.ShouldBe(
            VirtualMachines.ReadinessKind.Ready,
            "the reconciler would not call this machine converged, and KubeVirt calls it Running: " + readiness.Detail
        );

        TestContext.Current.SendDiagnosticMessage(
            $"install.sh (openebs-localpv + CDI + KubeVirt): {installed.TotalSeconds:F0} s; cirros import: {importTook.TotalSeconds:F0} s; "
            + $"blank disk provisioned as {provisioned}; machine applied to Running with the disk attached: {bootTook.TotalSeconds:F0} s; "
            + $"pool.kubevirt.io/v1alpha1 served by this pin: {poolsServed}"
        );
    }

    /// <summary>The root DataVolume KubeVirt derives from the chart's template, spelled by the contracts so the chart's helper is held to it.</summary>
    static readonly string VirtualMachineRoot = VirtualMachines.RootDataVolumeName(MachineName);
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
