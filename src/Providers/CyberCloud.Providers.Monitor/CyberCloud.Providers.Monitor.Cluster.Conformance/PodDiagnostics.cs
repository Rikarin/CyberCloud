using k8s;
using k8s.Models;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     What the kubelet says about the pods behind one Deployment — the diagnosis when a test that
///     waits for a pod does not get one, and the observation when it waits for a pod to be held.
/// </summary>
/// <remarks>
///     Shared by the two tests in this assembly that wait for a pod, because a message that named the
///     Deployment and not the container's waiting reason sent the first reader of a red run to
///     <c>kubectl describe</c> for what the test already had.
/// </remarks>
static class PodDiagnostics {
    /// <summary>The pods selected by one instance label, each with its containers' states in a line.</summary>
    /// <param name="raw">The harness's client.</param>
    /// <param name="ns">The namespace.</param>
    /// <param name="instanceLabel">The label key the Deployment selects its pods by.</param>
    /// <param name="objectName">The label's value — the Deployment's name.</param>
    /// <param name="token">The test's token.</param>
    public static async Task<string> DescribeAsync(IKubernetes raw, string ns, string instanceLabel, string objectName, CancellationToken token) {
        var pods = await ListAsync(raw, ns, instanceLabel, objectName, token);

        if (pods.Count == 0) {
            return "The Deployment has no pods at all, which is a ReplicaSet the controller never made — read the Deployment's conditions.";
        }

        var lines = pods.Select(pod => {
            var states = pod.Status?.ContainerStatuses?.Select(Describe) ?? ["no container statuses yet"];
            return $"{pod.Metadata.Name} phase={pod.Status?.Phase}: {string.Join("; ", states)}";
        });

        return "Pods: " + string.Join(" | ", lines);
    }

    /// <summary>
    ///     The first container waiting reason the kubelet reports that is not "still creating" or
    ///     "still pulling", with its message — or <see langword="null" /> while every container is
    ///     one of those, running, or absent.
    /// </summary>
    /// <param name="raw">The harness's client.</param>
    /// <param name="ns">The namespace.</param>
    /// <param name="instanceLabel">The label key the Deployment selects its pods by.</param>
    /// <param name="objectName">The label's value — the Deployment's name.</param>
    /// <param name="token">The test's token.</param>
    /// <remarks>
    ///     ⚠ <c>ContainerCreating</c> and the <c>ImagePull</c> reasons are filtered out because they
    ///     are the kubelet still working, not the kubelet having decided. What is left is a reason it
    ///     arrived at — <c>CreateContainerConfigError</c> for a reference that does not resolve,
    ///     <c>CrashLoopBackOff</c> for a container that exited — which is what a test waiting for the
    ///     kubelet's answer wants.
    /// </remarks>
    public static async Task<(string Reason, string Message)?> DecidedWaitingReasonAsync(
        IKubernetes raw,
        string ns,
        string instanceLabel,
        string objectName,
        CancellationToken token
    ) {
        var pods = await ListAsync(raw, ns, instanceLabel, objectName, token);

        foreach (var pod in pods) {
            foreach (var container in pod.Status?.ContainerStatuses ?? []) {
                if (container.State?.Waiting is { Reason: { Length: > 0 } reason } waiting
                    && reason is not ("ContainerCreating" or "ImagePullBackOff" or "ErrImagePull" or "PodInitializing")) {
                    return (reason, waiting.Message ?? string.Empty);
                }
            }
        }

        return null;
    }

    static async Task<IList<V1Pod>> ListAsync(IKubernetes raw, string ns, string instanceLabel, string objectName, CancellationToken token) {
        var pods = await raw.CoreV1.ListNamespacedPodAsync(ns, labelSelector: $"{instanceLabel}={objectName}", cancellationToken: token);
        return pods.Items;
    }

    static string Describe(V1ContainerStatus status) =>
        status.State?.Waiting is { } w ? $"{status.Name}: {w.Reason} — {w.Message}"
        : status.State?.Terminated is { } t ? $"{status.Name}: terminated {t.Reason} ({t.ExitCode})"
        : $"{status.Name}: running, ready={status.Ready}";
}
