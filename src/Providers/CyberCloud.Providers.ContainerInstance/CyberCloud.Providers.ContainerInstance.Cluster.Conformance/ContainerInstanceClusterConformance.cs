using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.ContainerInstance.Conformance;
using CyberCloud.Providers.ContainerInstance.Contracts;
using CyberCloud.ResourceManager.Contracts;
using Shouldly;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using k8s;

namespace CyberCloud.Providers.ContainerInstance.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the container-group type, and a group that runs on the
///     node and whose output comes back through its <c>logs</c> action.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE POD IS REAL.</b> The k3s this starts has a kubelet, so the shared lifecycle's pod is
///         pulled, started and reported <c>Running</c> before the operation succeeds — the first
///         cluster-backed case in the tree whose <c>Converged</c> is a node's verdict rather than an API
///         server's echo.
///     </para>
///     <para>
///         ⚠ <b><see cref="AGroupRunsOnTheNodeAndItsOutputComesBackThroughTheLogsAction" /> is #28's
///         assertion, and every hop in it is the platform's own.</b> The PUT goes through the resource
///         manager to a real operation, the reconciler resolves a secure-environment handle from the
///         harness's vault into a Secret, the kubelet starts the container with it, and the <c>logs</c>
///         POST goes through the manager's action dispatcher to <c>ContainerGroupActionHandler</c> and
///         <c>IKubeClusterConnection.ReadLogsAsync</c> to the pod's <c>log</c> subresource. The line the
///         container wrote — including the vault's value, which no body and no rendered object carries —
///         is what comes back. Then <c>restart</c> replaces the pod and the line comes back from the new
///         one.
///     </para>
///     <para>
///         ⚠ <b>What this does not reach: the connection grain.</b> The harness connects reconcilers
///         and handlers through <see cref="RealClusterConnection" /> over the fabric's client, not
///         through <c>ClusterConnectionGrain</c>; the grain's <c>ReadLogsAsync</c> forwards to the same
///         <c>KubeApiClient.ReadLogsAsync</c> <c>PreconditionAndLogTests</c> runs against k3s, behind the
///         tenancy check <c>ClusterConnectionTenancyTests</c> probes.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ContainerGroupLifecycleConformance(ClusterConformanceFixture<ContainerGroupCase> fixture)
    : ClusterConformanceTests<ContainerGroupCase>(fixture), IClassFixture<ClusterConformanceFixture<ContainerGroupCase>> {
    /// <summary>What the probe's container writes, so its log has a line only it could have written.</summary>
    const string Marker = "container-group-log-probe-5d1e";

    /// <summary>The value the vault holds and the container prints — reachable by no other route.</summary>
    const string VaultValue = "resolved-from-the-vault-9b27";

    /// <summary>How long a log line is waited for: a busybox pull on a cold node, and its first write.</summary>
    static readonly TimeSpan LogBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     A group whose container writes a line and a vault-resolved value runs on the node, the
    ///     <c>logs</c> action returns both, and a <c>restart</c> runs a new pod that writes them again.
    /// </summary>
    [Fact]
    public async Task AGroupRunsOnTheNodeAndItsOutputComesBackThroughTheLogsAction() {
        var harness = Fixture.Require(
            "that a container group's pod is started by a real kubelet, that a secure-environment handle "
            + "reaches the container as a value, and that the logs action returns what the container wrote."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "log-probe";
        var path = string.Create(CultureInfo.InvariantCulture, $"tenants/{ConformanceIds.Tenant:D}/container-groups/log-probe");

        await ClusterConformanceState<ContainerGroupCase>.Vault.MintAsync(
            path,
            new Dictionary<string, string> { ["token"] = VaultValue },
            token
        );

        var body = ContainerGroups.Body(
            ClusterConformanceHarness<ContainerGroupCase>.ClusterId,
            ["probe=" + ContainerGroups.DefaultImage],
            // ⚠ The trap is ContainerGroupCase.Command's: an exec'd sleep is PID 1 and holds a delete for
            // the whole grace period, and restart waits on exactly that delete.
            ["sh", "-c", "trap 'exit 0' TERM; echo " + Marker + "; echo token=$TOKEN; sleep 3600 & wait"],
            cpu: "250m",
            memory: "64Mi",
            environment: ["GREETING=hello"],
            secureEnvironment: ["TOKEN=" + path + "#token"]
        );

        var accepted = (await WriteAsync(harness, name, body)).GetValueOrThrow();
        var status = await ConvergeAsync(harness, accepted.OperationId);

        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the group did not converge on a real kubelet: {status.Error?.Message}"
        );

        // ── Read around our own code: the node ran it, with the budget on the pod ──────────────
        var ns = ClusterConformanceHarness<ContainerGroupCase>.Namespace;
        var pod = await harness.Raw.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: token);

        pod.Status.Phase.ShouldBe("Running", "Converged is the kubelet's verdict, and the kubelet says otherwise");
        pod.Spec.Resources.ShouldNotBeNull(
            "the API server dropped the pod-level resources, so the group's budget is not enforced — the "
            + "PodLevelResources gate is off on this cluster"
        );
        pod.Spec.Resources.Limits["cpu"].ToString().ShouldBe("250m");
        pod.Spec.AutomountServiceAccountToken.ShouldBe(false);

        var uid = pod.Metadata.Uid;

        // ── The logs action, through the manager ───────────────────────────────────────────────
        var log = await LogsAsync(harness, name, token);

        log.ShouldContain(Marker);
        log.ShouldContain(
            "token=" + VaultValue,
            Shouldly.Case.Sensitive,
            "the secure-environment value did not reach the container — it is in the vault, and it is in no body and no object the platform applied except the Secret"
        );

        // ── restart: a new pod, and it writes the line again ───────────────────────────────────
        var restarted = await harness.Manager.ActionAsync(
            new() {
                Path = ClusterConformanceHarness<ContainerGroupCase>.Address(name).Path,
                ApiVersion = ContainerGroups.V2026,
                Verb = WriteVerb.Post,
                Action = ContainerGroups.RestartAction,
                Caller = ClusterConformanceHarness<ContainerGroupCase>.Caller()
            },
            token
        );

        restarted.IsSuccess.ShouldBeTrue(restarted.Error?.Message);

        using (var answer = JsonDocument.Parse(restarted.GetValueOrThrow().ActionResponse)) {
            answer.RootElement.GetProperty("podUidBefore").GetString().ShouldBe(uid);
            answer.RootElement.GetProperty("podUid").GetString().ShouldNotBe(uid, "restart applied onto the pod it was replacing");
        }

        (await LogsAsync(harness, name, token)).ShouldContain(Marker);

        var replaced = await harness.Raw.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: token);
        replaced.Metadata.Uid.ShouldNotBe(uid);

        await TearDownAsync(harness, name);
    }

    /// <summary>POSTs <c>logs</c> until the marker is in the answer or the budget is spent, and returns the last log.</summary>
    static async Task<string> LogsAsync(ClusterConformanceHarness<ContainerGroupCase> harness, string name, CancellationToken token) {
        var waited = Stopwatch.StartNew();
        var last = string.Empty;

        while (waited.Elapsed < LogBudget) {
            var answered = await harness.Manager.ActionAsync(
                new() {
                    Path = ClusterConformanceHarness<ContainerGroupCase>.Address(name).Path,
                    ApiVersion = ContainerGroups.V2026,
                    Verb = WriteVerb.Post,
                    Action = ContainerGroups.LogsAction,
                    Body = """{"tailLines": 50}""",
                    Caller = ClusterConformanceHarness<ContainerGroupCase>.Caller()
                },
                token
            );

            if (answered.IsSuccess) {
                using var answer = JsonDocument.Parse(answered.GetValueOrThrow().ActionResponse);
                ContainerGroups.LogsResponse.Validate(answer.RootElement).IsSuccess.ShouldBeTrue();
                last = answer.RootElement.GetProperty("log").GetString() ?? string.Empty;

                if (last.Contains(Marker, StringComparison.Ordinal)) {
                    return last;
                }
            } else {
                // A container being pulled or started is the retryable code, and nothing else is.
                answered.Error!.Code.ShouldBe(CyberCloud.Core.ErrorCode.OperationInProgress, answered.Error.Message);
                last = answered.Error.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        return last;
    }
}

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the container-group type.</summary>
public sealed class ContainerGroupSiloKillConformance : SiloKillConformanceTests<ContainerGroupCase>;
