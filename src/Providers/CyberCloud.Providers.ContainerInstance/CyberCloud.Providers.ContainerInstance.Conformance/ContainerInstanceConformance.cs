using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.ContainerInstance.Contracts;
using Shouldly;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerInstance.Conformance;

/// <summary>
///     <c>CyberCloud.ContainerInstance/containerGroups</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The action is <c>restart</c>, not <c>logs</c>, and the harness decides it.</b> The shared
///         POST reaches a <see cref="FakeKubeCluster" /> with no kubelet, where a log read answers
///         "waiting to start" by design, and on the k3s lane the suite's POST can land before the
///         container has written anything. <c>restart</c> reads the pod the reconciler applied, deletes
///         it, waits for it to be gone and applies the same render again — a write the suite can see
///         succeed on either harness. The log path is proven against a real kubelet by
///         <c>ContainerGroupLifecycleConformance</c> in the cluster-backed project, where a container runs
///         and its output comes back through the <c>logs</c> action.
///     </para>
///     <para>
///         ⚠ <b>The body runs busybox with a command that writes a line and sleeps</b>, because on the
///         k3s lane the pod is real — a kubelet has run it since the WSL2 kernel moved to cgroup v2 — and
///         a busybox with no command exits at once, which under <c>Always</c> is a crash loop. The
///         changed body moves the CPU, which is the pod-level budget: an immutable field, so the
///         update test is the replace-the-pod path and not a no-op apply.
///     </para>
/// </remarks>
public sealed class ContainerGroupCase : IProviderCaseSource {
    /// <summary>The line the conformance body's container writes first.</summary>
    public const string Marker = "container-group-conformance-started";

    /// <summary>The conformance body's command.</summary>
    /// <remarks>
    ///     ⚠ <b>The shell traps <c>SIGTERM</c> and waits on its <c>sleep</c></b> rather than <c>exec</c>ing
    ///     it: a process that is PID 1 ignores a signal it has no handler for, so an <c>exec sleep</c>
    ///     holds every delete — the harness's <c>kubectl delete</c>, a replace, a restart, a teardown —
    ///     for the whole grace period. Measured at ten seconds a delete on the k3s lane (2026-09-23).
    /// </remarks>
    public static string[] Command { get; } =
        ["sh", "-c", "trap 'exit 0' TERM; echo " + Marker + "; sleep 3600 & wait"];

    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ContainerInstance/containerGroups",
            CreateProvider = static () => new ContainerInstanceProvider(),
            ReconcilerType = typeof(ContainerGroupReconciler),
            CreateReconciler = static clock => new ContainerGroupReconciler(clock),
            Type = ContainerGroups.Type,
            ApiVersion = ContainerGroups.V2026,
            Body = static cluster => ContainerGroups.Body(cluster, command: Command),
            ChangedBody = static cluster => ContainerGroups.Body(cluster, command: Command, cpu: "750m"),
            // Drops the required `/properties/containers`.
            InvalidBody = static cluster => Without(ContainerGroups.Body(cluster, command: Command), "containers"),
            InvalidBodyTarget = "/properties/containers",
            ActionName = ContainerGroups.RestartAction,
            // ⚠ ONE OBJECT: the body names no secure environment, no registry credential and no public
            // address, so neither Secret nor the floating IP is rendered. ContainerGroupReconcilerTests
            // asserts all three.
            Objects = static (id, ns) => [ContainerGroups.PodRef(ns, id.Name)],
            // A cluster data plane, which the harness breaks and reads itself — see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return ContainerGroups.Matches(match.ObjectJson, match.Namespace, desired.RootElement);
            }
        };

    /// <summary>A valid body with one property removed.</summary>
    /// <param name="body">A valid body.</param>
    /// <param name="property">The property under <c>/properties</c> to drop.</param>
    public static string Without(string body, string property) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove(property);
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the container-group type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ContainerGroupConformance(ProviderTestCluster<ContainerGroupCase> cluster)
    : ProviderConformanceTests<ContainerGroupCase>(cluster), IClassFixture<ProviderTestCluster<ContainerGroupCase>>;

/// <summary>The container-backed half, skipped loudly, against the container-group type.</summary>
public sealed class ContainerGroupClusterBackedConformance() : ClusterBackedConformanceTests(ContainerGroupCase.ProviderCase);

/// <summary>What this provider's registration into the shared suite is <b>shaped</b> like.</summary>
public sealed class ContainerInstanceSuiteShapeTests {
    [Fact]
    public void TheCaseOwnsOnePodAndDeclaresTheActionThatWritesTheCluster() {
        var id = new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "prod", ContainerGroups.Type, "web", Guid.NewGuid());

        ContainerGroupCase.ProviderCase.Objects(id, "ns").Single().Kind.Kind.ShouldBe("Pod");
        ContainerGroupCase.ProviderCase.ActionName.ShouldBe(ContainerGroups.RestartAction);
        ContainerGroups.Type.Depth.ShouldBe(1);
    }
}
