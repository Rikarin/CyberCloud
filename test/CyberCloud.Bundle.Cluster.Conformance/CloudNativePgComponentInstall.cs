using CyberCloud.Cluster.Conformance.Infrastructure;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Shouldly;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     What <c>charts/bundle/install.sh</c> would run for the cloudnative-pg component, read without
///     a cluster.
/// </summary>
/// <remarks>
///     ⚠ <b>It is the daemon-free companion the other two installing components each have</b>, for
///     the reason <see cref="CertManagerComponentInstaller" /> states at length: a run whose every
///     test skipped reports "Zero tests ran" and fails under <c>--minimum-expected-tests 1</c>.
///     ⚠ <b>It is also the only place <c>--component</c> is asserted to select by name.</b>
///     <c>--phase 50</c> selects EIGHT components — clickhouse, cloudnative-pg, mariadb, opensearch,
///     rabbitmq, redis, seaweedfs and strimzi — so the phase selector cannot address this row at all,
///     which is what <c>--component</c> exists for and what the cluster class below depends on.
/// </remarks>
public sealed class CloudNativePgComponentInstaller {
    /// <summary>
    ///     The dry run selects cloudnative-pg by name and names the chart, the repository and the
    ///     version that <c>component.yaml</c> pins.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The negative half is the assertion with a measured defect behind it.</b>
    ///     <c>--phase 50</c> is eight components, so a test that selected the phase and looked for
    ///     the cloudnative-pg row would be green over a run that also installed seven other
    ///     operators — which on a cluster is seven image pulls and roughly four more minutes. This
    ///     asserts that the seven are NOT attempted, which is the whole reason the cluster class
    ///     below fits in a test lane.
    /// </remarks>
    [Fact]
    public async Task TheDryRunSelectsOneComponentByNameAndNamesTheVersionItPins() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH, so what "
            + "the installer would run could not be read. WOULD PROVE: that install.sh --component "
            + "selects one row of charts/bundle/bundle.yaml by name and derives its helm invocation "
            + "from charts/bundle/cloudnative-pg/component.yaml rather than hard-coding it."
        );

        var component = BundleInstaller.CloudNativePgComponent;

        var run = await BundleInstaller.RunAsync(
            "--dry-run --component " + component,
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            0,
            $"charts/bundle/install.sh --dry-run --component {component} executes nothing and must "
            + "therefore succeed on any machine with bash. Its output was:\n" + run.Output
        );

        var chart = BundleInstaller.Pin(component, "chart");
        var repo = BundleInstaller.Pin(component, "repo");
        var version = BundleInstaller.Pin(component, "version");

        chart.ShouldNotBeNullOrWhiteSpace();
        repo.ShouldNotBeNullOrWhiteSpace();
        version.ShouldNotBeNullOrWhiteSpace();

        foreach (var expected in new[] { chart!, "--repo", repo!, "--version", version!, "--wait" }) {
            run.Output.ShouldContain(
                expected,
                Case.Sensitive,
                $"charts/bundle/install.sh --dry-run --component {component} did not mention "
                + $"\"{expected}\". Every value above is read out of charts/bundle/{component}/"
                + "component.yaml by this test and is supposed to be read out of the same file by "
                + "the script — README.md § What a component owes. Its output was:\n" + run.Output
            );
        }

        // ⚠ The seven other rows of phase 50, named rather than counted, so a roster change that
        // moved one of them shows up here as a name instead of as an off-by-one.
        var alsoInPhase50 = new[] {
            "clickhouse-operator", "mariadb-operator", "opensearch-operator",
            "rabbitmq-cluster-operator", "redis-operator", "seaweedfs-operator",
            "strimzi-kafka-operator"
        };

        foreach (var other in alsoInPhase50) {
            run.Output.ShouldNotContain(
                "\n  " + other + "\n",
                Case.Sensitive,
                $"charts/bundle/install.sh --dry-run --component {component} also attempted "
                + $"`{other}`, which shares phase 50 with it. --component selects one row; if it "
                + "selected a phase instead, the cluster class in this file would install eight "
                + "operators and would not fit in any per-PR lane. Its output was:\n" + run.Output
            );
        }
    }
}

/// <summary>
///     Two <c>charts/bundle/</c> components on one cluster, and a <c>charts/managed/</c> custom
///     resource applied against a definition the bundle installed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE SENTENCE <c>charts/bundle/</c> EXISTS FOR, AND IT WAS UNEXERCISED UNTIL
///         THIS CLASS.</b> <c>charts/bundle/bundle.yaml</c> § owed,
///         <c>one-volume-has-been-provisioned</c>, states the target in its own words: <i>"the path
///         that actually matters — a reconciler applies a resource, an operator creates the claim,
///         the claim binds — is still unexercised end to end"</i>. What binds here is not this test's
///         hand-written claim. It is a PersistentVolumeClaim that <b>CloudNativePG</b> created, as a
///         controller-owned child of a <c>Cluster</c> rendered from <c>charts/managed/postgres</c>,
///         through the StorageClass <c>charts/bundle/openebs-localpv</c> installed a moment earlier.
///     </para>
///     <para>
///         ⚠ <b>ONE invocation of the installer, TWO components, TWO phases — the first time any of
///         the three has been true.</b> Both older installing classes pass <c>--phase</c> and install
///         one component onto a cluster of their own, which <c>bundle.yaml</c> § owed says
///         explicitly does not add up to co-tenancy: <i>"NOTHING HAS INSTALLED TWO COMPONENTS ONTO
///         ONE CLUSTER, AND THE TWO ROWS ABOVE DO NOT ADD UP TO THAT"</i>. This installs phase 25 and
///         phase 50 in one run, in the roster's order, onto one API server.
///     </para>
///     <para>
///         ⚠ <b>What it still does NOT prove, said here because the gap is easy to overstate away.</b>
///         Not the phase <i>barrier</i> in full: this run walks two phases and helm's <c>--wait</c>
///         holds each, but the six <c>manifest:</c> components wait for nothing at all and none of
///         them is installed here — <c>bundle.yaml</c> § owed,
///         <c>the-manifest-path-waits-for-nothing</c>. Not <c>install.sh</c>'s <c>kubectl</c> branch,
///         which is still unexecuted by any test; the <c>kubectl</c> this class runs is its own, to
///         apply a rendered chart. Not the other fifteen pins. And not the <i>default</i> body of
///         <c>charts/managed/postgres</c>: <see cref="ChartValues" /> overrides seven values and says
///         why for each.
///     </para>
///     <para>
///         ⚠ <b>Costs, measured on a ten-CPU host on 2026-09-03 rather than estimated.</b> The
///         installer's two components take <b>26 s</b> together — cheaper than cert-manager's single
///         row, which pays six minutes' worth of <c>startupapicheck</c> when it goes wrong. From the
///         <c>Cluster</c> apply: the claim exists in <b>8 s</b>, it is <c>Bound</c> at <b>18 s</b>,
///         and the cluster reports <c>Ready</c> at <b>68 s</b>, the bulk of which is the
///         <c>ghcr.io/cloudnative-pg/postgresql</c> pull. So the cluster work is about 95 s and the
///         k3s start is the other half of the bill.
///     </para>
/// </remarks>
/// <param name="cluster">The empty k3s.</param>
public sealed class CloudNativePgOnAnEmptyCluster(EmptyClusterFixture cluster) : IClassFixture<EmptyClusterFixture> {
    const string Group = "postgresql.cnpg.io";
    const string Version = "v1";
    const string Plural = "clusters";

    /// <summary>The class <c>charts/bundle/openebs-localpv</c> installs.</summary>
    const string StorageClass = "openebs-hostpath";

    /// <summary>
    ///     The provisioner's base path, and the one assertion that survives a mix-up in every other.
    /// </summary>
    /// <remarks>
    ///     ⚠ Rancher's <c>local-path</c>, which this k3s ships and annotates default, writes under
    ///     <c>/var/lib/rancher/k3s/storage</c>. <see cref="OpenEbsLocalPvOnAnEmptyCluster" /> has the
    ///     measurement behind that; it is repeated here because the claim under test is created by an
    ///     operator that would have been perfectly happy to take the other default.
    /// </remarks>
    const string BasePath = "/var/openebs/local";

    const string Probe = "bundle-cnpg-probe";

    /// <summary>
    ///     The seven values this test overrides on <c>charts/managed/postgres</c>, and why each.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every one of them narrows what a green run says, so each is named rather than
    ///     bundled into a "test values" file nobody reads.</b>
    ///     <c>replicas=1</c> — the chart defaults to 2, and the second instance is a second image
    ///     pull that proves nothing this one does not.
    ///     <c>pooling.enabled=false</c> — a <c>Pooler</c> is a second custom resource in the same
    ///     group; it would add a PgBouncer pull and no new claim.
    ///     <c>backup.enabled=false</c> — barman-cloud needs an object store this cluster has none of,
    ///     and the chart's own <c>destinationPath</c> default is empty.
    ///     <c>monitoring.enabled=false</c> — <c>enablePodMonitor</c> makes the operator create a
    ///     <c>monitoring.coreos.com</c> object, whose definition is phase 20's and is not installed
    ///     here; leaving it on would make this class depend on a third component.
    ///     <c>storage.class</c> — THE POINT OF THE TEST. The chart's default is <c>""</c>, which
    ///     means "the cluster's default", and this cluster has two. See
    ///     <see cref="OpenEbsLocalPvOnAnEmptyCluster" />: a claim that names neither would bind
    ///     through k3s's provisioner and the run would be green over a bundle nothing installed.
    ///     <c>storage.size=128Mi</c> — the chart's 20Gi is a request nothing enforces on a hostpath
    ///     volume, and a smaller number is not a weaker assertion here.
    ///     <c>sizing.preset=s1.nano</c> — 100m/512Mi, so the instance is schedulable on a
    ///     single-node k3s inside a container.
    /// </remarks>
    static readonly string[] ChartValues = [
        "--set", "replicas=1",
        "--set", "pooling.enabled=false",
        "--set", "backup.enabled=false",
        "--set", "monitoring.enabled=false",
        "--set", "storage.class=" + StorageClass,
        "--set", "storage.size=128Mi",
        "--set", "sizing.preset=s1.nano"
    ];

    /// <summary>How long the operator gets to create the claim after the resource is applied.</summary>
    /// <remarks>
    ///     ⚠ Measured at 8 s, budgeted at 60. It is the operator's first reconcile of an object it
    ///     has never seen; nothing is pulled and nothing is scheduled, so a run that needs more than
    ///     this is a controller that is not watching rather than a slow machine.
    /// </remarks>
    static readonly TimeSpan ClaimBudget = TimeSpan.FromSeconds(60);

    /// <summary>How long the claim gets to bind once its consumer is scheduled.</summary>
    /// <remarks>
    ///     ⚠ It covers the localpv provisioner's own helper pod, whose image pull this budget is
    ///     mostly about — <c>OPENEBS_IO_HELPER_POD_TIMEOUT_SECS</c> in the rendered Deployment is 120
    ///     seconds, so a shorter budget would report this harness giving up rather than the
    ///     provisioner's message.
    /// </remarks>
    static readonly TimeSpan BoundBudget = TimeSpan.FromMinutes(4);

    /// <summary>How long the cluster gets to reach <c>Ready</c>.</summary>
    /// <remarks>
    ///     ⚠ Measured at 68 s from the apply, budgeted at 8 minutes, and the difference is one image:
    ///     <c>ghcr.io/cloudnative-pg/postgresql</c> is several hundred megabytes and a build machine
    ///     with a cold cache and a slow link is the case this budget is for.
    /// </remarks>
    static readonly TimeSpan ReadyBudget = TimeSpan.FromMinutes(8);

    /// <summary>
    ///     After one <c>install.sh</c> run that installs the storage component and the PostgreSQL
    ///     operator onto one cluster, a <c>Cluster</c> rendered from <c>charts/managed/postgres</c>
    ///     makes the operator create a claim on the bundle's own storage class, that claim binds to a
    ///     volume on the bundle's own provisioner path, and the cluster reaches <c>Ready</c>.
    /// </summary>
    [Fact]
    public async Task InstallingTwoComponentsLetsTheOperatorCreateAndBindTheClaimForAChartRenderedCluster() {
        Assert.SkipWhen(
            cluster.Client is null || cluster.KubeconfigPath is null,
            cluster.Skip(
                BundleInstaller.CloudNativePgComponent,
                "two-of-nineteen-have-been-installed",
                "that one charts/bundle/install.sh run installs TWO components across TWO phases onto "
                + "one API server, and that a charts/managed/postgres Cluster applied afterwards makes "
                + "CloudNativePG create a PersistentVolumeClaim on the openebs-hostpath class the "
                + "same run installed, bind it to a volume under " + BasePath + ", and report Ready."
            )
        );

        Assert.SkipUnless(
            BundleInstaller.OnPath("kubectl") && BundleInstaller.OnPath("helm"),
            "SKIPPED — this class renders charts/managed/postgres with `helm template` and applies it "
            + "with `kubectl`, and one of the two is not on PATH. ⚠ That kubectl is THIS TEST'S, not "
            + "install.sh's: the installer's own kubectl branch belongs to the six `manifest:` "
            + "components and is still unexecuted by anything. WOULD PROVE: that an operator this "
            + "bundle installed creates and binds the claim for a managed chart's custom resource."
        );

        var client = cluster.Client!;
        var token = TestContext.Current.CancellationToken;

        // ── The cluster is empty of BOTH components, and each clause is load-bearing ───────────
        //
        // Without them every assertion below would hold over a cluster this test did not install,
        // which is the shape of defect this repository keeps finding.
        (await IsServedAsync(client, token)).ShouldBeFalse(
            $"{Group}/{Version} was already served before install.sh ran, so the Cluster applied "
            + "below would be admitted by a definition this test did not install. The fixture starts "
            + "a fresh k3s per class; a cluster that arrives with CloudNativePG on it is a fixture "
            + "defect rather than a bundle one."
        );

        var before = await client.StorageV1.ListStorageClassAsync(cancellationToken: token);

        before.Items.ShouldNotContain(
            storageClass => storageClass.Metadata.Name == StorageClass,
            $"{StorageClass} already existed before install.sh ran, so the claim the operator creates "
            + "below would bind through a class this test did not install."
        );

        // ── ONE installer run, TWO components, TWO phases ──────────────────────────────────────
        //
        // ⚠ The order here is deliberately the WRONG way round on the command line: --component is
        // documented to filter the roster rather than to order it, so storage must still be installed
        // before the operator that provisions through it. bundle.yaml puts openebs-localpv in phase
        // 25 and cloudnative-pg in phase 50, and the run below is asserted to obey that and not this.
        var run = await BundleInstaller.RunAsync(
            "--component " + BundleInstaller.CloudNativePgComponent
            + " --component " + BundleInstaller.OpenEbsLocalPvComponent,
            cluster.KubeconfigPath,
            token
        );

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh installing two components onto one fresh k3s failed. This is "
            + "the first run in this repository that puts two of them on one API server, so a "
            + "failure here is as likely to be co-tenancy as it is to be either pin. Its output "
            + "was:\n" + run.Output
        );

        run.Output.IndexOf("\n  " + BundleInstaller.OpenEbsLocalPvComponent + "\n", StringComparison.Ordinal)
            .ShouldBeLessThan(
                run.Output.IndexOf("\n  " + BundleInstaller.CloudNativePgComponent + "\n", StringComparison.Ordinal),
                "install.sh installed cloudnative-pg before openebs-localpv, which is the order the "
                + "command line asked for and not the order charts/bundle/bundle.yaml gives. The "
                + "roster carries the order; a selector that reordered it would be a second place "
                + "the order is written, and on a real bundle it is what installs a webhook onto a "
                + "cluster with no CNI. Its output was:\n" + run.Output
            );

        // ── Both components are present afterwards, which is the co-tenancy claim ──────────────
        (await IsServedAsync(client, token)).ShouldBeTrue(
            $"{Group}/{Version} is not served after install.sh succeeded, so the component's "
            + "`serves:` line is not true of the cluster it installed. Installer output:\n"
            + run.Output
        );

        var installed = await client.StorageV1.ReadStorageClassAsync(StorageClass, cancellationToken: token);

        installed.Provisioner.ShouldBe(
            "openebs.io/local",
            $"the {StorageClass} class is not on this component's provisioner after a run that also "
            + "installed an operator. Installer output:\n" + run.Output
        );

        // ── A charts/managed/ custom resource, rendered by helm and applied ────────────────────
        await client.CoreV1.CreateNamespaceAsync(
            new V1Namespace { Metadata = new V1ObjectMeta { Name = Probe } },
            cancellationToken: token
        );

        var rendered = await RenderAsync(token);

        rendered.ShouldContain(
            "apiVersion: " + Group + "/" + Version,
            Case.Sensitive,
            "charts/managed/postgres did not render an object in the group this component serves, so "
            + "the apply below would prove nothing about the bundle. What it rendered was:\n"
            + rendered
        );

        await ApplyAsync(rendered, token);

        // ── The operator creates the claim. Not this test ──────────────────────────────────────
        var claim = await Poll(
            ClaimBudget,
            async () => {
                var claims = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
                    Probe,
                    cancellationToken: token
                );

                return claims.Items.FirstOrDefault();
            },
            token
        );

        claim.ShouldNotBeNull(
            $"no PersistentVolumeClaim appeared in `{Probe}` within {ClaimBudget.TotalSeconds:F0} "
            + "seconds of the Cluster being applied. The API server accepted the object — the "
            + "definition is installed — so this is the operator not acting on it, which is the one "
            + "failure a CRD-only install cannot be told apart from and is why this class exists."
        );

        // ⚠ THE ASSERTION THE WHOLE CLASS IS FOR. bundle.yaml § owed calls the older storage test's
        // subject "a 64Mi directory by a test's own hand-written claim". This claim has a controller
        // owner reference to the custom resource the chart rendered, so it cannot have been written
        // by this test: the API server records who owns it, and it is the Cluster.
        var owner = claim.Metadata.OwnerReferences?.SingleOrDefault(reference => reference.Controller == true);

        owner.ShouldNotBeNull(
            $"the claim `{claim.Metadata.Name}` has no controlling owner, so nothing on the API "
            + "server says an operator created it. A claim this test could have written is the "
            + "assertion bundle.yaml § owed, `one-volume-has-been-provisioned`, already has."
        );

        owner.Kind.ShouldBe(
            "Cluster",
            $"the claim is controlled by a `{owner.Kind}` rather than by the custom resource "
            + "charts/managed/postgres rendered."
        );

        owner.ApiVersion.ShouldBe(
            Group + "/" + Version,
            "the claim's controller is in another api-group, so it is not the object this bundle "
            + "component serves."
        );

        claim.Spec.StorageClassName.ShouldBe(
            StorageClass,
            $"the operator created its claim on `{claim.Spec.StorageClassName}` rather than on the "
            + "class this run installed. The chart was rendered with `storage.class` set, so this is "
            + "either the chart dropping the value or the operator overriding it — and on a cluster "
            + "with two default classes, neither is visible from the fact that the claim binds."
        );

        // ── The claim binds, on this component's provisioner and on its path ───────────────────
        var bound = await Poll(
            BoundBudget,
            async () => {
                var current = await client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
                    claim.Metadata.Name,
                    Probe,
                    cancellationToken: token
                );

                return current.Status?.Phase == "Bound" ? current : null;
            },
            token
        );

        bound.ShouldNotBeNull(
            $"the operator's claim did not reach Bound within {BoundBudget.TotalMinutes:F0} minutes. "
            + $"The class is {StorageClass} and its binding mode is WaitForFirstConsumer, so a claim "
            + "that stays Pending here means the instance pod was never scheduled — read the pods in "
            + $"`{Probe}` rather than the provisioner."
        );

        var volume = await client.CoreV1.ReadPersistentVolumeAsync(bound.Spec.VolumeName, cancellationToken: token);

        volume.Spec.StorageClassName.ShouldBe(
            StorageClass,
            "the bound volume's class, read off the API server rather than off the claim, is not this "
            + "component's."
        );

        // ⚠ The path, and it is the assertion that survives a mix-up in every other one: openebs
        // writes under /var/openebs/local and Rancher's local-path under
        // /var/lib/rancher/k3s/storage, on a cluster that has both classes and both annotated
        // default.
        volume.Spec.Local?.Path.ShouldNotBeNull(
            "the bound volume is not a node-local volume at all, so it did not come from this "
            + "component's provisioner."
        );

        volume.Spec.Local!.Path.ShouldStartWith(
            BasePath,
            Case.Sensitive,
            $"the bound volume's node-local path is `{volume.Spec.Local.Path}`, which is not under "
            + $"`{BasePath}`. k3s's own provisioner writes under /var/lib/rancher/k3s/storage, so a "
            + "path outside this one is the operator's claim having bound through the wrong default "
            + "class on a cluster that has two."
        );

        // ── And the database actually comes up on it ───────────────────────────────────────────
        //
        // ⚠ Bound is where bundle.yaml § owed's sentence ends and this is one step past it, for 45
        // measured seconds: a claim that binds says the volume exists, and Ready says PostgreSQL
        // ran initdb on it and answers. The difference is the difference between an object and a
        // service.
        var ready = await Poll(
            ReadyBudget,
            async () => {
                var current = await client.CustomObjects.GetNamespacedCustomObjectAsync(
                    Group,
                    Version,
                    Probe,
                    Plural,
                    Probe,
                    cancellationToken: token
                );

                return IsReady(JsonSerializer.SerializeToElement(current)) ? "ready" : null;
            },
            token
        );

        ready.ShouldNotBeNull(
            $"the Cluster did not report Ready within {ReadyBudget.TotalMinutes:F0} minutes. Its "
            + "claim is bound and its definition is installed, so this is the operator's own "
            + "bootstrap — read the instance pod's logs in `" + Probe + "`."
        );
    }

    /// <summary>Whether the group this component serves answers a list.</summary>
    /// <remarks>
    ///     ⚠ A list rather than discovery, for the reason <see cref="CertManagerOnAnEmptyCluster" />
    ///     gives: a 404 from a list is unambiguous, and discovery answers what the server
    ///     ADVERTISES.
    /// </remarks>
    static async Task<bool> IsServedAsync(IKubernetes client, CancellationToken token) {
        try {
            await client.CustomObjects.ListClusterCustomObjectAsync(Group, Version, Plural, cancellationToken: token);
            return true;
        } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
            return false;
        }
    }

    /// <summary>
    ///     <c>helm template</c> over <c>charts/managed/postgres</c>, with the overrides
    ///     <see cref="ChartValues" /> explains.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The chart in the tree, rendered by helm, rather than a Cluster written here.</b> A
    ///     hand-written custom resource would exercise the definition and nothing else; what makes
    ///     this the sentence <c>charts/bundle/</c> exists for is that the object comes out of
    ///     <c>charts/managed/</c> — the same templates the reconciler renders — so a chart that
    ///     stopped rendering a field the operator needs is red here.
    /// </remarks>
    static async Task<string> RenderAsync(CancellationToken token) {
        var chart = Path.Combine(BundleInstaller.RepositoryRoot, "charts", "managed", "postgres");
        var arguments = new List<string> { "template", Probe, chart };

        arguments.AddRange(ChartValues);

        var (exitCode, output) = await CaptureAsync("helm", arguments, input: null, kubeconfig: null, token);

        exitCode.ShouldBe(0, "`helm template charts/managed/postgres` failed:\n" + output);

        return output;
    }

    /// <summary>Applies a rendered document to the probe namespace.</summary>
    async Task ApplyAsync(string rendered, CancellationToken token) {
        var (exitCode, output) = await CaptureAsync(
            "kubectl",
            ["apply", "--namespace", Probe, "-f", "-"],
            rendered,
            cluster.KubeconfigPath,
            token
        );

        exitCode.ShouldBe(
            0,
            "applying the rendered charts/managed/postgres document failed. The definition is "
            + "installed and served, so a rejection here is the API server's opinion of the chart's "
            + "own body — which is the half no `helm lint` can reach. kubectl said:\n" + output
        );
    }

    /// <summary>Runs a command, feeds it <paramref name="input" />, and returns what it said.</summary>
    /// <remarks>
    ///     ⚠ Standard output and standard error are interleaved into one string, exactly as
    ///     <see cref="BundleInstaller.RunAsync" /> does it and for the same reason: a failure report
    ///     that separates a tool's diagnosis from the line it was diagnosing is a report nobody can
    ///     read.
    /// </remarks>
    static async Task<(int ExitCode, string Output)> CaptureAsync(
        string command,
        IReadOnlyList<string> arguments,
        string? input,
        string? kubeconfig,
        CancellationToken token
    ) {
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

    /// <summary>Polls until <paramref name="read" /> returns non-null, or the budget runs out.</summary>
    static async Task<T?> Poll<T>(TimeSpan budget, Func<Task<T?>> read, CancellationToken token)
        where T : class {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline) {
            var value = await read().ConfigureAwait(false);

            if (value is not null) {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }

        return null;
    }

    static bool IsReady(JsonElement resource) =>
        resource.TryGetProperty("status", out var status)
        && status.TryGetProperty("conditions", out var conditions)
        && conditions.ValueKind == JsonValueKind.Array
        && conditions.EnumerateArray().Any(condition =>
            condition.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "Ready"
            && condition.TryGetProperty("status", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() == "True");
}
