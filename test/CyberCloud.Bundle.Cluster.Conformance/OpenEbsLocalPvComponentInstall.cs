using CyberCloud.Cluster.Conformance.Infrastructure;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Shouldly;
using System.Net;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     What <c>charts/bundle/install.sh</c> would run for the openebs-localpv component, read without
///     a cluster.
/// </summary>
/// <remarks>
///     ⚠ <b>This class exists so that a run of this assembly on a machine with no Docker daemon still
///     runs a test</b>, for the reason <see cref="CertManagerComponentInstaller" /> states at length:
///     Microsoft.Testing.Platform reports "Zero tests ran" for a run whose every test skipped, and
///     <c>--minimum-expected-tests 1</c> turns that into a red build. <c>Build.Architecture.cs</c>
///     § <c>LabelsGate</c> has the long version, and nothing here is wired to a gate.
///     ⚠ It is <b>not</b> a weaker restatement of <see cref="OpenEbsLocalPvOnAnEmptyCluster" />. It
///     asserts what the cluster test cannot see: that the <c>--set</c> was <i>derived from the
///     component.yaml</i>. An installer that hard-coded the flag would pass every assertion over
///     there and fail here the moment somebody edited the manifest and expected the installer to
///     follow.
/// </remarks>
public sealed class OpenEbsLocalPvComponentInstaller {
    /// <summary>
    ///     The values entry without which this component installs a storage class that eleven charts
    ///     are configured never to name.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named here as a literal on purpose, and it is the only literal in this class. Every
    ///     other value the assertions use is read out of the component.yaml so that the test and the
    ///     script reach it by different routes. This one is different in kind: it is not a pin that
    ///     moves, it is the claim. <c>hostpathClass.isDefaultClass</c> flipping to <c>false</c> is a
    ///     regression rather than a bump, and a test that read the expected value out of the file it
    ///     is checking would follow the regression down and stay green.
    /// </remarks>
    const string DefaultClassFlag = "hostpathClass.isDefaultClass";

    /// <summary>
    ///     The dry run names the chart, the repository and the version that <c>component.yaml</c>
    ///     pins, and passes the <c>hostpathClass.isDefaultClass</c> override without which the class
    ///     this component installs is not the cluster's default.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the same shape as cert-manager's <c>crds.enabled</c> and it is the more
    ///     dangerous of the two, because the failure it prevents is silent.</b> Deleting
    ///     <c>crds.enabled</c> makes cert-manager's install fail loudly on a not-ready Job. Deleting
    ///     this one makes <c>helm upgrade --install --wait</c> succeed, the Deployment go Ready, the
    ///     <c>openebs-hostpath</c> class exist — and every managed chart that leaves
    ///     <c>storageClassName</c> at its default <c>""</c> still bind through whatever else the
    ///     cluster calls default, or stay Pending forever where there is nothing else. Nothing about
    ///     the install reports a problem. <c>openebs-localpv/component.yaml</c> § the one value
    ///     without which this component does nothing carries the reading that established it, and
    ///     this is the assertion that keeps it true.
    ///     ⚠ Rendered firsthand at the pinned version on 2026-09-02 to confirm the flag is still the
    ///     only route to the annotation: <c>helm template</c> at 4.6.0 emits five objects with or
    ///     without it, and <c>storageclass.kubernetes.io/is-default-class</c> appears zero times
    ///     without and once with.
    /// </remarks>
    [Fact]
    public async Task TheDryRunNamesTheChartVersionTheComponentPinsAndPassesTheDefaultClassOverride() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH, so what "
            + "the installer would run could not be read. WOULD PROVE: that install.sh derives the "
            + "openebs-localpv helm invocation, and the default-storage-class override in "
            + "particular, from charts/bundle/openebs-localpv/component.yaml rather than "
            + "hard-coding it."
        );

        var run = await BundleInstaller.RunAsync(
            "--dry-run --phase 25",
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh --dry-run --phase 25 executes nothing and must therefore "
            + "succeed on any machine with bash. Its output was:\n" + run.Output
        );

        var component = BundleInstaller.OpenEbsLocalPvComponent;
        var chart = BundleInstaller.Pin(component, "chart");
        var repo = BundleInstaller.Pin(component, "repo");
        var version = BundleInstaller.Pin(component, "version");
        var isDefault = BundleInstaller.Value(component, DefaultClassFlag);

        chart.ShouldNotBeNullOrWhiteSpace();
        repo.ShouldNotBeNullOrWhiteSpace();
        version.ShouldNotBeNullOrWhiteSpace();

        // ⚠ The claim, asserted before the installer's output is read at all. Without these two lines
        // a deleted `values:` block would make `isDefault` null, the loop below would look for
        // "--set" and "hostpathClass.isDefaultClass=" in output that contains neither — and it WOULD
        // fail, but on a message about the installer rather than about the manifest. The manifest is
        // where the defect would be.
        isDefault.ShouldNotBeNull(
            $"charts/bundle/{component}/component.yaml has no `{DefaultClassFlag}` entry in its "
            + "`values:` block. localpv-provisioner ships that value as false, so without the entry "
            + "the bundle installs the openebs-hostpath class and marks nothing default — and all "
            + "eleven charts under charts/managed/ that name a storage class default theirs to \"\", "
            + "which means the cluster's default rather than any class. The claims stay Pending "
            + "exactly as they did before this component existed, and no install reports it."
        );

        isDefault.ShouldBe(
            "true",
            $"charts/bundle/{component}/component.yaml sets `{DefaultClassFlag}` to "
            + $"\"{isDefault}\" rather than \"true\". See the message above for what that costs; the "
            + "component.yaml's own § the one value without which this component does nothing has "
            + "the reading behind it."
        );

        // ⚠ Read out of the file above and asserted against the script's output below, so the two
        // arrive at each value by different routes — bundle.yaml's header forbids a version written
        // twice, and a test is a second place to write one.
        var expected = new[] {
            chart!, "--repo", repo!, "--version", version!,
            "--set", DefaultClassFlag + "=" + isDefault, "--wait"
        };

        foreach (var argument in expected) {
            run.Output.ShouldContain(
                argument,
                Case.Sensitive,
                $"charts/bundle/install.sh --dry-run --phase 25 did not mention \"{argument}\". Every "
                + $"value above is read out of charts/bundle/{component}/component.yaml by this test "
                + "and is supposed to be read out of the same file by the script — README.md § What a "
                + "component owes. Its output was:\n" + run.Output
            );
        }
    }
}

/// <summary>
///     Provisioning a volume through <c>charts/bundle/openebs-localpv</c>'s storage class on an
///     empty cluster.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE OBVIOUS VERSION OF THIS TEST WOULD PASS WITH THE COMPONENT UNINSTALLED, AND
///         AVOIDING THAT IS THE WHOLE OF THIS CLASS.</b> The k3s this fixture starts already ships
///         Rancher's <c>local-path</c> provisioner and marks its class default —
///         <c>charts/managed/cloud-shell/SOURCE</c> already reasons about it. So a test that
///         installed this component, created a bare <c>PersistentVolumeClaim</c> and waited for
///         <c>Bound</c> would bind through whichever default the admission plugin picked and would
///         be green over a cluster where <c>install.sh</c> had never run. Four things make the
///         difference, and <c>bundle.yaml</c> § owed, <c>one-volume-has-been-provisioned</c>, names
///         all four: the claim names <c>openebs-hostpath</c> explicitly; a pod exists, because
///         <c>WaitForFirstConsumer</c> means a claim with no consumer stays Pending and proves
///         nothing; the bound volume's class is read back off the API server; and the default-class
///         annotation is asserted to be on OUR class, on a cluster that has two.
///     </para>
///     <para>
///         ⚠ <b>The empty-cluster half is an assertion, and here it has a second clause the
///         cert-manager class does not need.</b> Before anything installs, this requires both that
///         <c>openebs-hostpath</c> is absent — so every claim below is about a class this test's own
///         run created — and that <c>local-path</c> is present AND annotated default. The second is
///         not scene-setting either: it is the premise that gives the explicit
///         <c>storageClassName</c> its meaning. On a cluster with no other default, a bare claim
///         would bind through <c>openebs-hostpath</c> anyway and "named it explicitly" would be a
///         distinction with nothing behind it. If k3s ever stops shipping <c>local-path</c> as
///         default, this test is still correct and is no longer interesting, and it should say so
///         out loud rather than quietly weaken.
///     </para>
///     <para>
///         ⚠ <b>What a green run does NOT support.</b> That the volume survives anything: it is one
///         copy of a directory on one node, with no quota, and <c>component.yaml</c> § which stage is
///         on is where that is written down. That a <c>charts/managed/</c> custom resource
///         reconciles — nothing has applied one against a bundle-installed definition yet, which is
///         still the sentence <c>charts/bundle/</c> exists for. And that the phase barrier works:
///         <c>--phase 25</c> narrows the run to one phase, that phase holds exactly one component,
///         and a run that crosses no boundary exercises none.
///     </para>
///     <para>
///         ⚠ <b>That last clause quoted <c>install.sh</c>'s usage text — <i>"skips that
///         guarantee"</i> — until the #74 review, and the quotation had been stale since
///         2026-09-03.</b> The sibling quotation in <see cref="BundleInstaller" /> was removed for
///         exactly this on 2026-09-05, in the commit that rewrote the usage text a second time; this
///         was the third copy, in the same assembly, and it survived that commit because nobody
///         grepped for the phrase. What this paragraph needs is a property of the script's
///         BEHAVIOUR — a selector narrows a run to what it selects — and quoting prose to establish
///         behaviour is how a citation goes stale without anything going red.
///         <c>charts/bundle/bundle.yaml</c> § owed,
///         <c>a-selector-that-matched-nothing-reported-success</c>, keeps the usage text's history,
///         and <c>--help</c> now counts the phases out of <c>bundle.yaml</c> rather than asserting
///         them so the number cannot go stale a third time.
///     </para>
/// </remarks>
/// <param name="cluster">The empty k3s.</param>
public sealed class OpenEbsLocalPvOnAnEmptyCluster(EmptyClusterFixture cluster) : IClassFixture<EmptyClusterFixture> {
    /// <summary>The class the component installs. Not read from component.yaml — it is the chart's.</summary>
    /// <remarks>
    ///     ⚠ The name is not a pin and is deliberately NOT derived from anything. It is the string
    ///     eleven <c>charts/managed/</c> charts would have to be set to, and the string a platform
    ///     operator types. A test that computed it from the chart would agree with a rename that
    ///     broke every one of them.
    /// </remarks>
    const string StorageClass = "openebs-hostpath";

    /// <summary>k3s's own bundled class, which arrives before this test does anything.</summary>
    const string K3sStorageClass = "local-path";

    /// <summary>The annotation the DefaultStorageClass admission plugin reads.</summary>
    const string DefaultClassAnnotation = "storageclass.kubernetes.io/is-default-class";

    const string Provisioner = "openebs.io/local";
    const string BindingMode = "WaitForFirstConsumer";

    /// <summary>The provisioner's configured base path, and the discriminator against k3s's class.</summary>
    /// <remarks>
    ///     ⚠ Rancher's <c>local-path</c> writes under <c>/var/lib/rancher/k3s/storage</c>. So a
    ///     PersistentVolume whose node-local path begins here cannot have come from the class that
    ///     was already on this cluster, whatever any name says — which makes this the one assertion
    ///     in the method that would survive a mix-up in every other one.
    /// </remarks>
    const string BasePath = "/var/openebs/local";

    const string Probe = "bundle-openebs-probe";

    /// <summary>
    ///     The pod that makes the claim bind. Pinned, tiny, and multi-arch.
    /// </summary>
    /// <remarks>
    ///     ⚠ A pinned tag rather than <c>latest</c>, because "a version pin verified against a tag
    ///     that does not exist" has four sightings in this bundle alone and a test image is not
    ///     exempt. Checked on Docker Hub on 2026-09-02: <c>busybox:1.37</c> serves amd64 and arm64/v8
    ///     among others at 2.2 MB, so the pull is small on either kind of build machine.
    ///     ⚠ Not <c>openebs/linux-utils</c>, which the provisioner's own helper pod pulls anyway and
    ///     which would therefore have cost nothing. That image's shell is an implementation detail of
    ///     somebody else's provisioner, and a test that broke when they changed their helper's base
    ///     image would be reporting on the wrong thing.
    /// </remarks>
    const string ProbeImage = "busybox:1.37";

    /// <summary>
    ///     How long the claim is watched while it must NOT bind.
    /// </summary>
    /// <remarks>
    ///     ⚠ This window is not a race and cannot become one. <c>WaitForFirstConsumer</c> means the
    ///     claim stays Pending until a pod that mounts it is scheduled, with no timeout of its own —
    ///     so a longer wait cannot turn this assertion red and a shorter one cannot turn it green by
    ///     accident. It is long enough that a provisioner ignoring the binding mode would have
    ///     finished; provisioning below takes minutes because of an image pull, not seconds.
    /// </remarks>
    static readonly TimeSpan PendingDwell = TimeSpan.FromSeconds(20);

    /// <summary>How long the pod gets to be scheduled, pull, mount the volume and exit.</summary>
    /// <remarks>
    ///     ⚠ It covers three pulls in the worst case: the probe image, the provisioner's
    ///     <c>linux-utils</c> helper, and whatever the node has not cached. The helper pod has its
    ///     own 120-second budget — <c>OPENEBS_IO_HELPER_POD_TIMEOUT_SECS</c> in the rendered
    ///     Deployment — so a budget shorter than that would report this harness giving up rather than
    ///     the provisioner's own message.
    /// </remarks>
    static readonly TimeSpan BoundBudget = TimeSpan.FromMinutes(6);

    /// <summary>
    ///     After <c>install.sh --phase 25</c>, <c>openebs-hostpath</c> is the cluster's default class
    ///     as well as k3s's own, and a claim that names it explicitly binds — once a pod exists — to a
    ///     PersistentVolume the API server agrees is on that class and on that provisioner's path.
    /// </summary>
    [Fact]
    public async Task InstallingTheComponentMakesOpenEbsHostpathDefaultAndBindsAClaimThatNamesItThroughAPod() {
        Assert.SkipWhen(
            cluster.Client is null || cluster.KubeconfigPath is null,
            cluster.Skip(
                BundleInstaller.OpenEbsLocalPvComponent,
                "one-volume-has-been-provisioned",
                "that charts/bundle/install.sh --phase 25 installs the storage component onto a fresh "
                + "API server unattended, that the class it installs is annotated default on a "
                + "cluster that already had a different default, and that a claim naming "
                + $"`storageClassName: {StorageClass}` binds through a pod to a PersistentVolume "
                + $"whose class and node-local path are that component's and not k3s's."
            )
        );

        var client = cluster.Client!;
        var token = TestContext.Current.CancellationToken;

        // ── The cluster is empty of OUR class, and is NOT empty of a default ───────────────────
        //
        // Both clauses, and the second is the one that makes the rest mean anything. See the class
        // remarks.
        var before = await client.StorageV1.ListStorageClassAsync(cancellationToken: token);

        before.Items.ShouldNotContain(
            storageClass => storageClass.Metadata.Name == StorageClass,
            $"{StorageClass} already existed before install.sh ran. Every assertion below would then "
            + "hold over a cluster this test did not install, so the run would report on the fixture "
            + "rather than on charts/bundle/. The fixture starts a fresh k3s per class; a cluster "
            + "that arrives with this class on it is a fixture defect, not a bundle one."
        );

        var k3sClass = before.Items.SingleOrDefault(storageClass => storageClass.Metadata.Name == K3sStorageClass);

        k3sClass.ShouldNotBeNull(
            $"{ClusterInfrastructure.K3sImage} does not ship a `{K3sStorageClass}` StorageClass, which is "
            + "the premise this test's central assertion rests on: it is the OTHER default, the one "
            + "that makes naming a class explicitly different from taking whatever the cluster hands "
            + "out. Without it a bare claim would bind through openebs-hostpath anyway and this "
            + "method would be green for a reason it does not state. Nothing below is WRONG if k3s "
            + "has stopped bundling Rancher's provisioner — it is merely no longer interesting, and "
            + "that is a thing to decide about rather than to inherit silently. See "
            + "charts/managed/cloud-shell/SOURCE, which reasons about the same class."
        );

        IsDefault(k3sClass).ShouldBeTrue(
            $"{K3sStorageClass} exists but is not annotated {DefaultClassAnnotation}=true, so this "
            + "cluster has no pre-existing default and the two-default situation the `values:` block "
            + "exists for does not arise here. Same reasoning as the assertion above."
        );

        // ── The installer, unattended ──────────────────────────────────────────────────────────
        var run = await BundleInstaller.RunAsync("--phase 25", cluster.KubeconfigPath, token);

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh --phase 25 failed against a fresh k3s. Its output was:\n"
            + run.Output
        );

        // ── The class, read off the API server rather than off the chart ───────────────────────
        var installed = await client.StorageV1.ReadStorageClassAsync(StorageClass, cancellationToken: token);

        installed.Provisioner.ShouldBe(
            Provisioner,
            $"the {StorageClass} class is not on {Provisioner}. component.yaml § THE STORAGE CLASS "
            + "is explicit that this is an external dynamic provisioner in the pre-CSI shape rather "
            + "than a CSI driver, and this line is where that is true of a cluster rather than of a "
            + "rendered template."
        );

        installed.VolumeBindingMode.ShouldBe(
            BindingMode,
            $"the {StorageClass} class does not bind {BindingMode}. The claim below is asserted to "
            + "stay Pending until a pod exists, which is only correct behaviour under this mode — so "
            + "a class that bound immediately would make that assertion red and this message is the "
            + "one that explains why."
        );

        // ⚠ ASSERTION FOUR, AND THE ONE WITH THE SILENT FAILURE BEHIND IT. Nothing else in this
        // method would notice the `--set` going missing: the class would still exist, the explicit
        // claim would still bind, and eleven managed charts that name no class at all would still be
        // pointed at k3s's provisioner on a cluster this bundle built.
        IsDefault(installed).ShouldBeTrue(
            $"the {StorageClass} class carries no {DefaultClassAnnotation}=true annotation after "
            + "install.sh --phase 25 succeeded. localpv-provisioner 4.6.0 ships "
            + "`hostpathClass.isDefaultClass: false` and writes that annotation only under the "
            + "conditional, so this is the `values:` block in "
            + $"charts/bundle/{BundleInstaller.OpenEbsLocalPvComponent}/component.yaml not reaching "
            + "helm. It is the one defect here that reports nothing at install time: eleven charts "
            + "under charts/managed/ default `storageClassName` to \"\", which means the cluster's "
            + "default, so they would silently keep using k3s's provisioner — or stay Pending forever "
            + "on a cluster that has none. Installer output:\n" + run.Output
        );

        // ⚠ TWO defaults, asserted rather than assumed. component.yaml § which stage is on lists a
        // "default-class handover" as a cost of the replicated stage precisely because two annotated
        // classes is not "the newer one wins". This line records that the situation is real here, so
        // the explicit storageClassName below is load-bearing and not decoration.
        var after = await client.StorageV1.ListStorageClassAsync(cancellationToken: token);
        var defaults = after.Items.Where(IsDefault).Select(storageClass => storageClass.Metadata.Name).Order().ToList();

        defaults.ShouldBe(
            new[] { K3sStorageClass, StorageClass }.Order().ToList(),
            $"this cluster's default-annotated classes are [{string.Join(", ", defaults)}]. Both are "
            + "expected: k3s's own, and the one install.sh just added. A claim that named neither "
            + "would get one of them by an admission-plugin rule this test does not model, which is "
            + "why the claim below names one."
        );

        // ── A claim that names the class, and a pod, because the mode requires one ─────────────
        await client.CoreV1.CreateNamespaceAsync(
            new V1Namespace { Metadata = new V1ObjectMeta { Name = Probe } },
            cancellationToken: token
        );

        await client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(
            new V1PersistentVolumeClaim {
                Metadata = new V1ObjectMeta { Name = Probe },
                Spec = new V1PersistentVolumeClaimSpec {
                    // ⚠ THE POINT OF THE WHOLE METHOD. Omitting this line is the test bundle.yaml
                    // § owed says would pass with the component uninstalled.
                    StorageClassName = StorageClass,
                    AccessModes = ["ReadWriteOnce"],
                    Resources = new V1VolumeResourceRequirements {
                        Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("64Mi") }
                    }
                }
            },
            Probe,
            cancellationToken: token
        );

        // ⚠ Pending BEFORE the pod, held for a window rather than sampled once. A single read right
        // after the create would also pass against a class that bound immediately, because binding
        // is not instantaneous either — so the assertion would be about latency instead of about
        // the binding mode.
        var deadline = DateTimeOffset.UtcNow + PendingDwell;

        while (DateTimeOffset.UtcNow < deadline) {
            var pending = await client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
                Probe,
                Probe,
                cancellationToken: token
            );

            // ⚠ PARENTHESISED, and it is not style. `pending.Status?.Phase.ShouldBe(…)` conditions
            // the WHOLE chain — including the call — on Status being non-null, so a claim whose
            // status had not been written yet would assert nothing at all and this loop would spin
            // out its window green. The parentheses make a null status a null phase and a failure.
            (pending.Status?.Phase).ShouldBe(
                "Pending",
                $"the claim reached \"{pending.Status?.Phase}\" with no pod mounting it. The "
                + $"{StorageClass} class declares {BindingMode}, which exists so that a node-local "
                + "volume is created on the node its consumer was scheduled to — a claim that binds "
                + "without a consumer has been provisioned somewhere nothing asked for. "
                + $"It bound to \"{pending.Spec?.VolumeName}\"."
            );

            // ⚠ No ConfigureAwait(false) — xUnit1030 is error-severity here and would bypass the
            // runner's parallelization limits, which is not a thing to do in a lane that holds a
            // cross-process cluster permit. The private helpers below are not test methods and keep
            // theirs.
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        await client.CoreV1.CreateNamespacedPodAsync(
            new V1Pod {
                Metadata = new V1ObjectMeta { Name = Probe },
                Spec = new V1PodSpec {
                    // ⚠ Never, so the pod's terminal phase is the assertion. A default restart policy
                    // would put a container that exits 0 into CrashLoopBackOff and the pod would
                    // never reach Succeeded, which is a confusing way to discover a working volume.
                    RestartPolicy = "Never",
                    Containers = [
                        new V1Container {
                            Name = "writer",
                            Image = ProbeImage,
                            // ⚠ A WRITE and a read-back, not a sleep. A pod that merely mounts the
                            // volume proves the kubelet attached a directory; a pod that writes into
                            // it and reads the bytes back proves the directory is usable, which is
                            // the thing a stateful managed service actually needs. `set -e` so a
                            // failed write is a failed pod rather than an exit code nobody reads.
                            Command = ["/bin/sh", "-c", "set -e; printf bound > /data/probe; test \"$(cat /data/probe)\" = bound"],
                            VolumeMounts = [new V1VolumeMount { Name = "data", MountPath = "/data" }]
                        }
                    ],
                    Volumes = [
                        new V1Volume {
                            Name = "data",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = Probe }
                        }
                    ]
                }
            },
            Probe,
            cancellationToken: token
        );

        var phase = await WaitForPodAsync(client, token);

        phase.ShouldBe(
            "Succeeded",
            $"the probe pod ended in \"{phase}\" rather than Succeeded within "
            + $"{BoundBudget.TotalMinutes:0} minutes. It mounts a {StorageClass} claim, writes one "
            + "file into it and reads the bytes back, so anything but Succeeded is the volume not "
            + "being provisioned, not being mounted, or not being writable. The provisioner runs a "
            + "helper pod on the target node to create the directory — `kubectl -n "
            + $"{Probe} describe pod {Probe}` and the openebs-localpv-system Deployment's log are "
            + "where the reason is. Installer output:\n" + run.Output
        );

        // ── The bound volume, read back off the API server ─────────────────────────────────────
        var claim = await client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(Probe, Probe, cancellationToken: token);

        // ⚠ Parenthesised for the reason the Pending loop above states.
        (claim.Status?.Phase).ShouldBe("Bound", "the pod succeeded and the claim it mounted is not Bound.");

        claim.Spec.StorageClassName.ShouldBe(
            StorageClass,
            "the claim was created naming " + StorageClass + " and the API server records a "
            + "different class on it."
        );

        claim.Spec.VolumeName.ShouldNotBeNullOrWhiteSpace("a Bound claim names no volume.");

        // ⚠ THE VOLUME, not the claim. A claim's storageClassName is what this test asked for; the
        // PersistentVolume's is what the cluster did about it, and they are separate fields written
        // by separate actors. This is bundle.yaml § owed's third item and it is the reason the read
        // is here rather than stopping at Bound.
        var volume = await client.CoreV1.ReadPersistentVolumeAsync(claim.Spec.VolumeName, cancellationToken: token);

        volume.Spec.StorageClassName.ShouldBe(
            StorageClass,
            $"the claim bound to PersistentVolume \"{claim.Spec.VolumeName}\", whose class is "
            + $"\"{volume.Spec.StorageClassName}\" rather than {StorageClass}."
        );

        volume.Metadata.Annotations.ShouldContainKeyAndValue(
            "pv.kubernetes.io/provisioned-by",
            Provisioner,
            $"the volume is on the {StorageClass} class but was not provisioned by {Provisioner}."
        );

        // ⚠ The one assertion that would survive a mix-up in every other one: k3s's own provisioner
        // writes under /var/lib/rancher/k3s/storage, so a path under this prefix cannot have come
        // from the class that was already here whatever any name says.
        var path = volume.Spec.Local?.Path ?? volume.Spec.HostPath?.Path;

        path.ShouldNotBeNull(
            "the bound PersistentVolume declares neither `spec.local` nor `spec.hostPath`, so there "
            + "is no node-local path to check. localpv-provisioner's hostpath cas-type writes "
            + "`spec.local` with a node affinity; a volume of some other shape on this class is the "
            + "provisioner doing something this test does not model."
        );

        path.ShouldStartWith(
            BasePath,
            Case.Sensitive,
            $"the volume's node-local path is \"{path}\", which is not under the "
            + $"OPENEBS_IO_BASE_PATH the component installs ({BasePath}). Rancher's local-path "
            + "provisioner — already on this cluster, and marked default — writes under "
            + "/var/lib/rancher/k3s/storage, so a path outside this prefix is the strongest "
            + "available sign that the claim bound through the class that was already here rather "
            + "than through the one install.sh added."
        );
    }

    /// <summary>Whether a class is annotated default.</summary>
    /// <remarks>
    ///     ⚠ The value is the string "true" and the API server does not normalise it: a class
    ///     annotated "True" is NOT default to the DefaultStorageClass admission plugin, which parses
    ///     with Go's <c>strconv.ParseBool</c> — that accepts "True", so it is. This compares
    ///     ordinal-case-insensitively for that reason and not out of tolerance.
    /// </remarks>
    static bool IsDefault(V1StorageClass storageClass) =>
        storageClass.Metadata?.Annotations is { } annotations
        && annotations.TryGetValue(DefaultClassAnnotation, out var value)
        && bool.TryParse(value, out var isDefault)
        && isDefault;

    /// <summary>Polls the probe pod until it reaches a terminal phase or the budget runs out.</summary>
    static async Task<string?> WaitForPodAsync(IKubernetes client, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + BoundBudget;
        string? phase = null;

        while (DateTimeOffset.UtcNow < deadline) {
            try {
                var pod = await client.CoreV1.ReadNamespacedPodAsync(Probe, Probe, cancellationToken: token);
                phase = pod.Status?.Phase;

                if (phase is "Succeeded" or "Failed") {
                    return phase;
                }
            } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
                // The create returned, so this is the read racing the watch cache. Poll again.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }

        return phase;
    }
}
