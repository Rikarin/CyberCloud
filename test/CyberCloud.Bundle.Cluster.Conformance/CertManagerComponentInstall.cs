using CyberCloud.Cluster.Conformance.Infrastructure;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Shouldly;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testcontainers.K3s;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     An empty k3s and a kubeconfig file pointing at it, shared by the tests that install into it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It neither throws nor skips when Docker does not answer, which is the contract every
///         cluster-backed assembly in this repository keeps.</b> <c>ClusterInfrastructure</c>'s
///         remarks give the reason in full: a start that threw would fail the class, which reads as
///         "the bundle is broken"; a start that skipped would take the class out of the runner's
///         output under one message. The absence of a daemon is a reportable outcome, and the report
///         is <see cref="Skip" /> — attached to the one test that needs it, naming the component, the
///         image, and the sentence that was not checked.
///     </para>
///     <para>
///         ⚠ <b>It is an <see cref="IClassFixture{TFixture}" /> and NOT a collection fixture, so each
///         installing class gets its own k3s and its own <c>InitializeAsync</c>.</b> That is a cost —
///         a second container start per class, serialised by <c>ClusterSlot</c> and by this
///         assembly's <c>CollectionBehavior</c> — and it is paid on purpose. A shared cluster would
///         make every class's subject depend on which classes ran before it: the cert-manager class
///         asserts <c>cert-manager.io/v1</c> is NOT served before it installs, and the openebs class
///         asserts <c>openebs-hostpath</c> does not exist, and both of those assertions are about a
///         cluster nothing else has touched. That failure — a test whose input set differs between a
///         full-suite run and a filtered one — is one this repository has actually shipped, and it
///         hid a real defect underneath a green full suite.
///     </para>
///     <para>
///         ⚠ <b>Postgres and Redis are not started, unlike the provider suites' fixture.</b> Nothing
///         here has a grain, a reminder or a durable shard: the subject is a shell script and an API
///         server. Starting the other two would add a minute to a lane already measured in minutes to
///         make the fixture look like its neighbours.
///     </para>
/// </remarks>
public sealed class EmptyClusterFixture : IAsyncLifetime {
    K3sContainer? container;
    Exception? failure;

    /// <summary>The kubeconfig file <c>install.sh</c> is pointed at, or <see langword="null" />.</summary>
    public string? KubeconfigPath { get; private set; }

    /// <summary>The raw client, or <see langword="null" /> when the cluster did not come up.</summary>
    public IKubernetes? Client { get; private set; }

    /// <summary>Why a test did not run, in the form every cluster-backed suite here uses.</summary>
    /// <param name="component">The <c>charts/bundle/</c> component the calling test installs.</param>
    /// <param name="owedRow">The <c>bundle.yaml</c> § owed id the calling test narrows.</param>
    /// <param name="wouldProve">What the calling test would have proved.</param>
    /// <remarks>
    ///     ⚠ <b>The component and the owed row are parameters rather than the literals they used to
    ///     be, and that is not tidying.</b> This message named cert-manager and
    ///     <c>one-of-eighteen-has-been-installed</c> in its own text while the fixture is shared by
    ///     every class in the assembly. The moment a second class took the fixture, a machine with no
    ///     Docker daemon would have printed a skip about cert-manager for a run that was about the
    ///     storage class — a report that is worse than none, because it reads as if the row it names
    ///     were the one left unchecked.
    /// </remarks>
    public string Skip(string component, string owedRow, string wouldProve) =>
        $"SKIPPED — charts/bundle/ {component}: no empty cluster to install onto, so nothing was "
        + "checked. "
        + $"NEEDS: a Docker daemon able to run {ClusterInfrastructure.K3sImage}, and `bash` and `helm` "
        + "on PATH. "
        + $"WOULD PROVE: {wouldProve} "
        + "This suite is present by name and skipped rather than absent, because "
        + $"charts/bundle/bundle.yaml § owed, `{owedRow}`, must not be readable "
        + "as closed on a machine that never ran the install. "
        + "What went wrong: "
        + (failure is null ? "no exception was recorded." : failure.GetType().Name + ": " + failure.Message);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        if (!BundleInstaller.OnPath("bash") || !BundleInstaller.OnPath("helm")) {
            failure = new InvalidOperationException(
                "install.sh is a bash script whose phase-15 row is one `helm upgrade --install`; "
                + "one of `bash` or `helm` is not on PATH."
            );

            return;
        }

        try {
            // ⚠ Taken BEFORE the container, and held for the life of the process — the same permit
            // every other k3s-backed suite takes, so however many of them a run contains, at most one
            // holds a cluster at a time. ClusterSlot's remarks have the reasoning.
            ClusterSlot.Acquire();

            var k3s = new K3sBuilder(ClusterInfrastructure.K3sImage).Build();
            container = k3s;

            await k3s.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

            var kubeconfig = await k3s.GetKubeconfigAsync().ConfigureAwait(false);

            // ⚠ A FILE, because install.sh is a separate process and helm reads $KUBECONFIG rather
            // than anything this process could hand it in memory. Named with a GUID so two suites
            // running at once cannot read each other's cluster, and deleted in DisposeAsync — it
            // holds a working client certificate for the container.
            var path = Path.Combine(Path.GetTempPath(), "cybercloud-bundle-" + Guid.NewGuid().ToString("N") + ".kubeconfig");
            await File.WriteAllTextAsync(path, kubeconfig, TestContext.Current.CancellationToken).ConfigureAwait(false);
            KubeconfigPath = path;

            using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(kubeconfig));
            Client = new k8s.Kubernetes(await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(yaml).ConfigureAwait(false));
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            failure = ex;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        Client?.Dispose();

        if (KubeconfigPath is not null) {
            File.Delete(KubeconfigPath);
        }

        if (container is not null) {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
///     What <c>charts/bundle/install.sh</c> would run for the cert-manager component, read without a
///     cluster.
/// </summary>
/// <remarks>
///     ⚠ <b>This class exists so that a run of this assembly on a machine with no Docker daemon still
///     runs a test.</b> Microsoft.Testing.Platform reports "Zero tests ran" — and fails under
///     <c>--minimum-expected-tests 1</c> — for a run whose every test skipped, so an assembly that
///     skipped wholesale would turn a missing daemon into a red build rather than into a visible
///     skip. <c>ClusterInfrastructure</c>'s remarks name the same trap, and
///     <c>Build.Architecture.cs</c> § <c>LabelsGate</c> has the long version of why it matters.
///     ⚠ It is <b>not</b> a weaker restatement of the cluster test. It asserts something the cluster
///     test cannot see: that the arguments were <i>derived from the component.yaml</i> rather than
///     written into the script. An installer that hard-coded <c>v1.21.1</c> would pass every assertion
///     in <see cref="CertManagerOnAnEmptyCluster" /> and fail here the moment the pin moved.
/// </remarks>
public sealed class CertManagerComponentInstaller {
    /// <summary>
    ///     The dry run names the chart, the repository and the version that <c>component.yaml</c>
    ///     pins, and passes the <c>crds.enabled</c> override without which the chart installs no
    ///     definitions.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>--set crds.enabled=true</c> is the assertion with a measured failure behind it.</b>
    ///     cert-manager's chart ships <c>crds.enabled: false</c>. Deleting the <c>values:</c> block
    ///     from <c>charts/bundle/cert-manager/component.yaml</c> and running this assembly turns both
    ///     tests red — this one in a quarter of a second, and
    ///     <see cref="CertManagerOnAnEmptyCluster" /> six minutes later on
    ///     <c>failed post-install: resource Job/cert-manager-system/cert-manager-startupapicheck not
    ///     ready</c>. That flag lives in a part of the component.yaml format nothing else in the tree
    ///     reads, so this is the only place a reviewer would see it go.
    ///     ⚠ The <c>values:</c> block is one line of a component manifest and the assertion above
    ///     covers exactly it. Every OTHER component's <c>values:</c> block — clickhouse-operator's
    ///     <c>watchNamespaces</c>, strimzi's <c>watchAnyNamespace</c>, redis-operator's pinned image
    ///     tag — is as unexercised as it was, and two of those three are the "operator watches only
    ///     its own namespace" trap <c>charts/bundle/README.md</c> already names.
    /// </remarks>
    [Fact]
    public async Task TheDryRunNamesTheChartVersionTheComponentPinsAndPassesTheCrdsOverride() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH, so what "
            + "the installer would run could not be read. WOULD PROVE: that install.sh derives the "
            + "cert-manager helm invocation from charts/bundle/cert-manager/component.yaml rather "
            + "than hard-coding it."
        );

        var run = await BundleInstaller.RunAsync(
            "--dry-run --phase 15",
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh --dry-run --phase 15 executes nothing and must therefore "
            + "succeed on any machine with bash. Its output was:\n" + run.Output
        );

        var chart = BundleInstaller.Pin(BundleInstaller.CertManagerComponent, "chart");
        var repo = BundleInstaller.Pin(BundleInstaller.CertManagerComponent, "repo");
        var version = BundleInstaller.Pin(BundleInstaller.CertManagerComponent, "version");

        chart.ShouldNotBeNullOrWhiteSpace();
        repo.ShouldNotBeNullOrWhiteSpace();
        version.ShouldNotBeNullOrWhiteSpace();

        // ⚠ Read out of the file above and asserted against the script's output below, so the two
        // arrive at the value by different routes. Asserting the literal "v1.21.1" here would make
        // this test a second place the pin is written, which is the exact thing bundle.yaml's header
        // forbids: "a version written twice is a version that disagrees with itself".
        foreach (var expected in new[] { chart!, "--repo", repo!, "--version", version!, "--set", "crds.enabled=true", "--wait" }) {
            run.Output.ShouldContain(
                expected,
                Case.Sensitive,
                $"charts/bundle/install.sh --dry-run --phase 15 did not mention \"{expected}\". Every "
                + "value above is read out of charts/bundle/cert-manager/component.yaml by this test "
                + "and is supposed to be read out of the same file by the script — README.md § What a "
                + "component owes. Its output was:\n" + run.Output
            );
        }
    }
}

/// <summary>
///     Installing the cert-manager component of <c>charts/bundle/</c> onto an empty cluster.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE COMPONENT OF EIGHTEEN. This class proves the install MECHANISM, not the
///         roster.</b> What a green run here supports, exactly: <c>charts/bundle/install.sh</c> can
///         be driven unattended against a fresh API server; it reads a pin out of a
///         <c>component.yaml</c> and installs it; <c>--wait</c> means what
///         <c>cert-manager/component.yaml</c> says it means, so "installed" implies "serving"; and
///         the one <c>serves:</c> line that component declares is true of the cluster afterwards.
///         What a green run here does NOT support: that the other seventeen pins install, that the
///         phase barriers order them correctly, that seventeen operators fit on one node, or that a
///         managed chart's custom resource reconciles. <c>charts/bundle/bundle.yaml</c> § owed keeps
///         that list; this class narrows the first row of it and closes nothing.
///     </para>
///     <para>
///         ⚠ <b>The empty-cluster half of the name is an assertion, not scene-setting.</b> The first
///         thing the test does is require that <c>cert-manager.io/v1</c> is <i>not</i> served. Without
///         it the whole method would pass unchanged against a cluster that already had cert-manager
///         on it — which is the shape of the defect this batch keeps finding, a check whose subject
///         depends on what ran before it.
///     </para>
/// </remarks>
/// <param name="cluster">The empty k3s.</param>
public sealed class CertManagerOnAnEmptyCluster(EmptyClusterFixture cluster) : IClassFixture<EmptyClusterFixture> {
    const string Group = "cert-manager.io";
    const string Version = "v1";
    const string Probe = "bundle-cert-manager-probe";

    /// <summary>How long the issued certificate gets. A self-signed issue is immediate when it works.</summary>
    static readonly TimeSpan ReadyBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     After <c>install.sh --phase 15</c>, <c>cert-manager.io/v1</c> is served and a self-signed
    ///     Certificate reaches <c>Ready</c> with a parseable certificate in the Secret it names.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The Certificate is the assertion; the served group is only the half that a CRD apply
    ///     could have faked.</b> Installing definitions makes <c>cert-manager.io/v1</c> servable with
    ///     no controller behind it whatever — which is precisely what phase 20 of this bundle does on
    ///     purpose for <c>monitoring.coreos.com</c>. A <c>Certificate</c> that reaches <c>Ready</c>
    ///     with a certificate in its Secret cannot be produced by definitions alone: it needs the
    ///     controller running, the webhook serving and admitting, and the issuer reconciling. That is
    ///     why <c>charts/bundle/README.md</c> § Installing calls cert-manager's readiness observable,
    ///     and why this row was the honest one to do first.
    /// </remarks>
    [Fact]
    public async Task InstallingTheComponentMakesCertManagerIoV1ServableAndASelfSignedCertificateReachReady() {
        Assert.SkipWhen(
            cluster.Client is null || cluster.KubeconfigPath is null,
            cluster.Skip(
                BundleInstaller.CertManagerComponent,
                "one-of-eighteen-has-been-installed",
                "that charts/bundle/install.sh --phase 15 installs the cert-manager component onto a "
                + "fresh API server unattended, that cert-manager.io/v1 — the component's only "
                + "`serves:` line — is served afterwards, and that a self-signed Certificate reaches "
                + "Ready, which needs the controller and the webhook and not merely the definitions."
            )
        );

        var client = cluster.Client!;
        var token = TestContext.Current.CancellationToken;

        // ── The cluster is empty, or the rest of this method proves nothing ────────────────────
        (await IsServedAsync(client, "certificates", token)).ShouldBeFalse(
            "cert-manager.io/v1 was already served before install.sh ran. Every assertion below "
            + "would then hold over a cluster this test did not install, so the run would report on "
            + "the fixture rather than on charts/bundle/. The fixture starts a fresh k3s per process; "
            + "a cluster that arrives with cert-manager on it is a fixture defect, not a bundle one."
        );

        // ── The installer, unattended ──────────────────────────────────────────────────────────
        var run = await BundleInstaller.RunAsync("--phase 15", cluster.KubeconfigPath, token);

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh --phase 15 failed against a fresh k3s. This is the first thing "
            + "in the repository to run it against an API server at all — charts/bundle/bundle.yaml "
            + "§ owed, `one-of-eighteen-has-been-installed` — so a failure here is a defect in the "
            + "installer or in the pin, not in this test's expectations. Its output was:\n"
            + run.Output
        );

        // ── The `serves:` line the component declares ──────────────────────────────────────────
        foreach (var plural in new[] { "certificates", "issuers", "clusterissuers" }) {
            (await IsServedAsync(client, plural, token)).ShouldBeTrue(
                $"{Group}/{Version} {plural} is not served after install.sh --phase 15 reported "
                + "success. charts/bundle/cert-manager/component.yaml declares `serves: "
                + $"{Group}/{Version}`, and the Bundle gate's coverage check treats that line as a "
                + "claim about what the pin installs. Installer output:\n" + run.Output
            );
        }

        // ── The controller, the webhook and the issuer, which definitions alone cannot fake ────
        await CreateProbeObjectsAsync(client, token);

        var ready = await WaitForReadyAsync(client, token);

        ready.ShouldBeTrue(
            $"a self-signed Certificate did not reach Ready within {ReadyBudget.TotalMinutes:0} "
            + "minute(s). cert-manager/component.yaml puts this component in phase 15 rather than 40 "
            + "so that its webhook is Ready before anything creates a Certificate, and says `helm "
            + "install --wait` is \"the barrier that makes 'installed' mean 'serving'\". A Certificate "
            + "that stays un-Ready after that barrier returned is that sentence being wrong. "
            + "Installer output:\n" + run.Output
        );

        // ⚠ The Secret is opened rather than counted. A Ready condition is cert-manager's own claim
        // about itself; a certificate that parses is the claim being true.
        var secret = await client.CoreV1.ReadNamespacedSecretAsync(Probe + "-tls", Probe, cancellationToken: token);

        secret.Data.ShouldContainKey(
            "tls.crt",
            "the Certificate reported Ready and the Secret it names carries no tls.crt."
        );

        using var issued = X509CertificateLoader.LoadCertificate(secret.Data["tls.crt"].AsSpan());

        issued.Subject.ShouldContain(
            Probe + ".bundle.invalid",
            Case.Sensitive,
            "the issued certificate is not the one the Certificate asked for."
        );
    }

    /// <summary>Whether a kind in cert-manager's group answers a list.</summary>
    /// <remarks>
    ///     ⚠ A list rather than a discovery call, because a 404 from a list is unambiguous: the API
    ///     server serves the group/version and the plural, or it does not. Discovery answers a
    ///     slightly different question — what the server ADVERTISES — and is cached by intermediaries.
    /// </remarks>
    static async Task<bool> IsServedAsync(IKubernetes client, string plural, CancellationToken token) {
        try {
            await client.CustomObjects.ListClusterCustomObjectAsync(Group, Version, plural, cancellationToken: token);
            return true;
        } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
            return false;
        }
    }

    static async Task CreateProbeObjectsAsync(IKubernetes client, CancellationToken token) {
        await client.CoreV1.CreateNamespaceAsync(
            new V1Namespace { Metadata = new V1ObjectMeta { Name = Probe } },
            cancellationToken: token
        );

        await client.CustomObjects.CreateNamespacedCustomObjectAsync(
            new JsonObject {
                ["apiVersion"] = Group + "/" + Version,
                ["kind"] = "Issuer",
                ["metadata"] = new JsonObject { ["name"] = Probe, ["namespace"] = Probe },
                // The one issuer type that needs nothing outside the cluster. An ACME issuer would
                // make this test depend on Let's Encrypt being reachable, which is a different suite.
                ["spec"] = new JsonObject { ["selfSigned"] = new JsonObject() }
            },
            Group,
            Version,
            Probe,
            "issuers",
            cancellationToken: token
        );

        await client.CustomObjects.CreateNamespacedCustomObjectAsync(
            new JsonObject {
                ["apiVersion"] = Group + "/" + Version,
                ["kind"] = "Certificate",
                ["metadata"] = new JsonObject { ["name"] = Probe, ["namespace"] = Probe },
                ["spec"] = new JsonObject {
                    ["secretName"] = Probe + "-tls",
                    ["commonName"] = Probe + ".bundle.invalid",
                    ["dnsNames"] = new JsonArray(Probe + ".bundle.invalid"),
                    ["issuerRef"] = new JsonObject { ["name"] = Probe, ["kind"] = "Issuer" }
                }
            },
            Group,
            Version,
            Probe,
            "certificates",
            cancellationToken: token
        );
    }

    static async Task<bool> WaitForReadyAsync(IKubernetes client, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + ReadyBudget;

        while (DateTimeOffset.UtcNow < deadline) {
            var certificate = await client.CustomObjects.GetNamespacedCustomObjectAsync(
                Group,
                Version,
                Probe,
                "certificates",
                Probe,
                cancellationToken: token
            );

            if (IsReady(JsonSerializer.SerializeToElement(certificate))) {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }

        return false;
    }

    static bool IsReady(JsonElement certificate) =>
        certificate.TryGetProperty("status", out var status)
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
