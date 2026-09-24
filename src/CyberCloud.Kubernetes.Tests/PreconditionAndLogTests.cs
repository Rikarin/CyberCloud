using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Shouldly;
using System.Diagnostics;
using k8s;
using k8s.Models;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The two fabric members #28's scale sets and container groups added, against a real k3s: an
///     ordinary apply made conditional on the version it was computed from, and a pod's log read back
///     through the kubelet.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Real, for the reason <see cref="CoOwnedApplyTests" /> is.</b> That a
///         <c>metadata.resourceVersion</c> in an apply body is an optimistic lock is the API server's
///         behaviour, and so is the <c>409</c> body <c>KubeApiClient</c> reads as
///         <see cref="ApplyResult.Stale" /> rather than as drift; the co-owned mode had measured both
///         for its own commands, and <c>IKubeCommandBuilder.IfResourceVersion</c> is the first ordinary
///         command to rely on them.
///     </para>
///     <para>
///         ⚠ <b>The log half needs a kubelet</b>, which a k3s-in-Docker on this host has since the
///         WSL2 kernel moved to cgroup v2 (2026-09-15). The pod is busybox, pinned the way
///         <c>OpenEbsLocalPvOnAnEmptyCluster</c> pins it, writing one line and sleeping.
///     </para>
/// </remarks>
/// <param name="k3s">The shared k3s.</param>
[Collection(K3sSuite.Name)]
public sealed class PreconditionAndLogTests(K3sFixture k3s) {
    static readonly GroupVersionKind ConfigMaps =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    static readonly GroupVersionKind Pods = new() { Group = "", Version = "v1", Kind = "Pod", Plural = "pods" };

    /// <summary>What the probe pod writes, so its log has something only it could have said.</summary>
    const string Marker = "cybercloud-log-probe-7f3a";

    /// <summary>
    ///     An ordinary apply carrying a version the object has moved past is refused as stale, writes
    ///     nothing, and succeeds once it carries the current one.
    /// </summary>
    [Fact]
    public async Task AnApplyCarryingAVersionTheObjectMovedPastIsStaleAndWritesNothing() {
        var token = TestContext.Current.CancellationToken;
        const string name = "precondition-probe";
        var target = new ObjectRef { Kind = ConfigMaps, Namespace = K3sFixture.Namespace, Name = name };

        (await k3s.Api.ApplyAsync(Command(name, "first", string.Empty), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);

        var read = (await k3s.Api.GetAsync(target, token)).GetValueOrThrow();
        read.ResourceVersion.ShouldNotBeNullOrEmpty();

        // Somebody else moves the object — an action, in the Compute family — after the read.
        (await k3s.Api.ApplyAsync(Command(name, "moved", string.Empty), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Updated);

        var stale = (await k3s.Api.ApplyAsync(Command(name, "from-the-old-read", read.ResourceVersion), token))
            .GetValueOrThrow();

        stale.Result.ShouldBe(
            ApplyResult.Stale,
            "the API server refused the apply on the moved version, and KubeApiClient must read that 409 as "
            + "Stale — a Conflict would be a drift event with no fields in it"
        );
        (await k3s.ReadFieldAsync("configmaps", name, "", "v1", "data", "value"))
            .ShouldBe("moved", "a stale apply wrote something");

        var current = (await k3s.Api.GetAsync(target, token)).GetValueOrThrow();

        (await k3s.Api.ApplyAsync(Command(name, "from-the-new-read", current.ResourceVersion), token))
            .GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Updated);
        (await k3s.ReadFieldAsync("configmaps", name, "", "v1", "data", "value")).ShouldBe("from-the-new-read");
    }

    /// <summary>
    ///     A running pod's log is read back through the kubelet, a tail of it is a tail, and a pod that
    ///     is not there is <see cref="ErrorCode.ResourceNotFound" />.
    /// </summary>
    [Fact]
    public async Task ARunningPodsLogIsReadBackThroughTheKubelet() {
        var token = TestContext.Current.CancellationToken;
        const string name = "log-probe";
        var pod = new ObjectRef { Kind = Pods, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Raw.CoreV1.CreateNamespacedPodAsync(
            new V1Pod {
                Metadata = new() { Name = name },
                Spec = new() {
                    RestartPolicy = "Never",
                    Containers = [
                        new() {
                            Name = "probe",
                            Image = "busybox:1.37",
                            Command = ["sh", "-c", $"echo first; echo {Marker}; sleep 3600"]
                        }
                    ]
                }
            },
            K3sFixture.Namespace,
            cancellationToken: token
        );

        var started = Stopwatch.StartNew();
        Result<string> logs;

        do {
            logs = await k3s.Api.ReadLogsAsync(pod, "probe", 10, token);

            if (logs.IsSuccess && logs.GetValueOrThrow().Contains(Marker, StringComparison.Ordinal)) {
                break;
            }

            // A pod not yet scheduled, without a container status, or being pulled answers the
            // retryable code (KubeApiClient.NotYet).
            if (logs.TryGetError(out var error)) {
                error.Code.ShouldBe(ErrorCode.OperationInProgress, error.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        } while (started.Elapsed < TimeSpan.FromMinutes(3));

        logs.IsSuccess.ShouldBeTrue(logs.Error?.Message);
        logs.GetValueOrThrow().ShouldContain(Marker);

        var tail = (await k3s.Api.ReadLogsAsync(pod, "probe", 1, token)).GetValueOrThrow();
        tail.Trim().ShouldBe(Marker, "tailLines 1 is the last line and nothing before it");

        var absent = await k3s.Api.ReadLogsAsync(pod with { Name = "no-such-pod" }, string.Empty, 10, token);
        absent.IsFailure.ShouldBeTrue();
        absent.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        var wrongContainer = await k3s.Api.ReadLogsAsync(pod, "not-a-container", 10, token);
        wrongContainer.IsFailure.ShouldBeTrue();
        wrongContainer.Error!.Code.ShouldBe(
            ErrorCode.InvalidRequestBody,
            "a container the pod does not have is the caller's mistake, not a container still starting: "
            + wrongContainer.Error.Message
        );
    }

    /// <summary>
    ///     A pod no node will take answers "not yet", not an empty log: the API server streams nothing
    ///     for an unscheduled pod with a <c>200</c>, and the client has to tell that apart itself.
    /// </summary>
    [Fact]
    public async Task AnUnscheduledPodsLogIsNotYetRatherThanEmpty() {
        var token = TestContext.Current.CancellationToken;
        const string name = "log-probe-unscheduled";
        var pod = new ObjectRef { Kind = Pods, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Raw.CoreV1.CreateNamespacedPodAsync(
            new V1Pod {
                Metadata = new() { Name = name },
                Spec = new() {
                    RestartPolicy = "Never",
                    // No node carries this label, so the pod stays Pending with no nodeName.
                    NodeSelector = new Dictionary<string, string> { ["cybercloud.test/no-such-node"] = "true" },
                    Containers = [new() { Name = "probe", Image = "busybox:1.37", Command = ["sh", "-c", "echo never"] }]
                }
            },
            K3sFixture.Namespace,
            cancellationToken: token
        );

        var logs = await k3s.Api.ReadLogsAsync(pod, "probe", 10, token);

        logs.IsFailure.ShouldBeTrue("an unscheduled pod read as an empty log says its container ran and wrote nothing");
        logs.Error!.Code.ShouldBe(ErrorCode.OperationInProgress, logs.Error.Message);
    }

    /// <summary>
    ///     A pod the API server has, whose log read comes back <c>404</c>, is "not yet" too: the 404 is
    ///     about the node, not the pod.
    /// </summary>
    /// <remarks>
    ///     ⚠ The race this stands for is a kubelet that has not synced a pod the scheduler just bound —
    ///     measured once, 378 ms after a create, and not reproducible on demand. A pod bound by hand to
    ///     a node that does not exist reaches the same branch every time.
    /// </remarks>
    [Fact]
    public async Task APodWhoseNodeAnswersNotFoundIsNotYetRatherThanGone() {
        var token = TestContext.Current.CancellationToken;
        const string name = "log-probe-no-kubelet";
        var pod = new ObjectRef { Kind = Pods, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Raw.CoreV1.CreateNamespacedPodAsync(
            new V1Pod {
                Metadata = new() { Name = name },
                Spec = new() {
                    RestartPolicy = "Never",
                    NodeName = "cybercloud-no-such-node",
                    Containers = [new() { Name = "probe", Image = "busybox:1.37", Command = ["sh", "-c", "echo never"] }]
                }
            },
            K3sFixture.Namespace,
            cancellationToken: token
        );

        var logs = await k3s.Api.ReadLogsAsync(pod, "probe", 10, token);

        logs.IsFailure.ShouldBeTrue();
        logs.Error!.Code.ShouldBe(
            ErrorCode.OperationInProgress,
            "the pod is there; only its node could not answer, and a ResourceNotFound would tell the action's caller the group is gone: "
            + logs.Error.Message
        );
    }

    static KubeCommand Command(string name, string value, string readVersion) =>
        KubeCommand.For(new UnusedConnection())
            .WithTenantId(Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"))
            .WithResourceId(
                new ResourceId(
                    Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
                    Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
                    "prod",
                    new("CyberCloud.Compute", "virtualMachines"),
                    name,
                    Guid.Parse("5b1e0d44-2c3a-4e5f-8a9b-1c2d3e4f5a6b")
                )
            )
            .WithKind(ConfigMaps)
            .InNamespace(K3sFixture.Namespace)
            .IfResourceVersion(readVersion)
            .ObjectJson(
                new System.Text.Json.Nodes.JsonObject {
                    ["metadata"] = new System.Text.Json.Nodes.JsonObject { ["name"] = name },
                    ["data"] = new System.Text.Json.Nodes.JsonObject { ["value"] = value }
                }.ToJsonString()
            )
            .Build();
}
