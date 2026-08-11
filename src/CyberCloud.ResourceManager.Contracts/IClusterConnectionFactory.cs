namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Hands the reconcile driver a live connection to a cluster.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A seam rather than a direct dependency, and the reason is
///         docs/plan/03 § Assembly graph rules, rule 3.</b> The production implementation is a thin
///         handle over <c>IClusterConnectionGrain</c> and lives in <c>CyberCloud.Kubernetes</c> — the
///         one assembly allowed to reference <c>KubernetesClient</c>. Taking a direct dependency on it
///         here would put <c>k8s.Models</c> in the compile-time closure of the resource manager, and
///         from there of everything that references the resource manager. So the manager names this
///         interface and the host wires the implementation, which is the same shape
///         <c>IShardMapCache</c> takes in <c>CyberCloud.ServiceDefaults</c>.
///     </para>
///     <para>
///         ⚠ <b>Returning <see langword="null" /> is a legitimate answer and not a failure.</b>
///         docs/plan/08 § What the resource manager deliberately does not do: the manager <i>"must
///         work for a provider with no cluster at all (a DNS zone, a mail domain, a role
///         assignment)"</i>. A resource with no <c>clusterId</c> gets <see langword="null" /> and its
///         reconciler never looks. A resource whose type declared <c>RequiresCluster</c> and got
///         <see langword="null" /> is a wiring failure, and the driver says so by name rather than
///         handing the reconciler a null it will dereference.
///     </para>
/// </remarks>
public interface IClusterConnectionFactory {
    /// <summary>A handle to one cluster.</summary>
    /// <param name="clusterId">
    ///     The cluster. <see cref="Guid.Empty" /> means the resource is not placed in one, and the
    ///     answer is <see langword="null" />.
    /// </param>
    /// <returns>
    ///     The connection, or <see langword="null" /> when there is none. ⚠ Cheap — this is called on
    ///     every reconcile pass, so an implementation that opened a socket here would open one per
    ///     pass. The handle is a reference to a grain; the socket is the grain's.
    /// </returns>
    IKubeClusterConnection? Connect(Guid clusterId);
}
